using System.Windows.Input;
using TableEditor.App.Blocks;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.App.ViewModels;

/// <summary>수동 표 한 줄: 가지(MAN), 끝(MAN_ELSE), 입력 줄(MAN_FB_SW·MAN_OBS_B·MAN_START_SW), 묶음 줄(MAN_PROFILES).</summary>
public sealed class ManRowVm : Observable
{
    public ManualVm Owner { get; init; } = null!;
    public CardVm? Card { get; init; }
    public Statement St { get; init; } = null!;
    public int Ordinal { get; init; }
    public string OrdinalText => Ordinal.ToString();
    public bool IsElse => St.Name == "MAN_ELSE";
    public bool IsInput => ManualVm.InputNames.Contains(St.Name);
    public string Head { get; init; } = "";
    public string Profile => St.Args[0].Text;
    public IReadOnlyList<Chip> Chips { get; init; } = [];
    public IReadOnlyList<BlockVm> Blocks { get; init; } = [];
    public string Comment { get; init; } = "";
    public string? Status { get; init; }
    public Severity? StatusSeverity { get; init; }
    public bool IsChanged { get; init; }

    /// <summary>출력 칸이 시작하는 인자 자리.</summary>
    public int OutStart => St.Name == "MAN" ? 3 : 2;
    public override string ToString() => $"{OrdinalText}줄 {(IsElse ? Head : string.Join(" ", Chips.Select(c => c.Full)))} → {string.Join(", ", Blocks)}";

    bool current;
    public bool IsCurrent { get => current; set => Set(ref current, value); }
}

/// <summary>사슬 카드: 프로파일·모드, 가지 줄들 + 끝 줄. 오류(M-1 등)는 카드에 빨간 띠와 글.</summary>
public sealed class CardVm : Observable
{
    public ManualVm Owner { get; init; } = null!;
    public ManChain Chain { get; init; } = null!;
    public string Profile => Chain.Profile;
    public string Mode => Chain.Mode;
    public IReadOnlyList<ManRowVm> Rows { get; set; } = [];
    public string? Status { get; set; }
    public Severity? StatusSeverity { get; set; }
    public bool IsError => StatusSeverity == Severity.Error;
    public string CountText => $"가지 {Rows.Count(r => !r.IsElse)}";
    public override string ToString() => $"{Profile} {Mode}";

    /// <summary>카드 머리의 모드 목록(이 줄의 모드는 목록 밖이어도 든다).</summary>
    public IReadOnlyList<string> ModeChoices => Owner.ModeChoices.Contains(Mode) ? Owner.ModeChoices : [Mode, .. Owner.ModeChoices];

    bool hot;
    /// <summary>고른 줄이 이 카드 안에 있다.</summary>
    public bool IsHot { get => hot; set => Set(ref hot, value); }

    /// <summary>카드 머리의 모드 드롭다운. 바꾸면 사슬 모든 줄의 모드.</summary>
    public string ModeDraft
    {
        get => Mode;
        set { if (value != null && value != Mode) Owner.SetMode(this, value); }
    }
}

/// <summary>추가 메뉴 항목(수동 출력).</summary>
public sealed record OutChoice(string Text, string Kind);

/// <summary>
/// 수동 화면(사양 9.2절): 프로파일별 사슬 카드, 따로 방향 전환 스위치·후방 장애물 입력 줄.
/// 카드 추가(빈 카드·다른 사슬 복사), 가지 추가·삭제·끌기(사슬 안), 사슬 단위 끌기. 조건은 팝업, 출력은 블록.
/// </summary>
public sealed class ManualVm : BlockHost
{
    public static readonly IReadOnlyList<string> Modes = ["IDLE", "JOG"];

    /// <summary>카드 아래 "입력" 칸에 보이는 줄: 방향 전환·후방 장애물·시작 스위치 입력 줄과 프로파일 묶음 줄.</summary>
    public static readonly IReadOnlySet<string> InputNames = new HashSet<string> { "MAN_FB_SW", "MAN_OBS_B", "MAN_START_SW", "MAN_PROFILES" };

    /// <summary>이 사이트가 받는 모드: <c>IDLE</c>·<c>JOG</c>, 코어 v0.63.0 이상이면 <c>ANY</c> 도.</summary>
    public IReadOnlyList<string> ModeChoices => Site != null && Site.Names.Has(GrammarTable.ModeAnyVersion) ? [.. Modes, "ANY"] : Modes;

    public ManualVm(MainVm main) : base(main)
    {
        AddCard = new Command(() => _ = AddCardAsync(), () => Main.CanEdit && Zone != null);
        AddInput = new Command(() => _ = AddInputAsync(), () => Main.CanEdit && Zone != null);
        AddBranch = new Command(AddBranchRow, () => Main.CanEdit && Selected?.Card != null);
        DeleteRow = new Command(DeleteSelected, () => Main.CanEdit && Selected != null && !Selected.IsElse);
        DeleteCard = new Command(DeleteSelectedCard, () => Main.CanEdit && Selected?.Card != null);
        DeleteBlock = new Command(() => DeleteOut(Picked), () => Main.CanEdit && Picked is { Index: >= 0 } && Selected is { IsInput: false, Blocks.Count: > 1 });
        EditCond = new Command(p =>
        {
            if (Main.HasDialog || p is not ManRowVm r) return;
            // 줄을 고르기 전에 지금 줄의 확정 안 한 칸부터 확정(고르면 칸이 버려진다). 확정이 줄을 다시 쓰면 새 줄로
            if (Main.CommitKeep(r.St) is not Statement kept) return;
            if (!ReferenceEquals(kept, r.St)) r = Cards.SelectMany(c => c.Rows).FirstOrDefault(x => ReferenceEquals(x.St, kept)) ?? r;
            Select(r, false);
            _ = EditCondAsync(r);
        });
        Pick = new Command(p => { if (p is ManRowVm r) Select(r, false); });
    }

    ZoneBlock? Zone => Site?.ZonesOf("MANUAL_BEGIN").FirstOrDefault();

    protected override Piece? CodePiece => Selected?.St;

    protected override SignalScope? ScopeFor() => ScopeOf(Site, Selected?.St);

    /// <summary>프로파일을 가진 줄(MAN·MAN_ELSE·입력 줄)은 그 프로파일(묶음이면 그 묶음의 프로파일 모두), 그 밖은 전부.</summary>
    public static SignalScope ScopeOf(Site s, Statement? st) =>
        SignalScope.Of(s, st is { Name: "MAN" or "MAN_ELSE" or "MAN_FB_SW" or "MAN_OBS_B" or "MAN_START_SW", Args.Count: > 0 } ? st.Args[0].Text : null);

    IReadOnlyList<CardVm> all = [];
    public IReadOnlyList<CardVm> Cards { get; private set; } = [];
    IReadOnlyList<ManRowVm> allInputs = [];
    public IReadOnlyList<ManRowVm> Inputs { get; private set; } = [];
    public bool HasInputs => Inputs.Count > 0;
    public bool IsEmpty => all.Count == 0 && allInputs.Count == 0;
    public string CountText => $"카드 {Cards.Count}";

    /// <summary>프로파일 거르기: 전체 + 사슬이 있는 프로파일.</summary>
    public IReadOnlyList<string> Filters { get; private set; } = [TagVm.AllProfiles];

    /// <summary>카드 추가 팝업의 프로파일(1~63 만, 전체 없음).</summary>
    public IReadOnlyList<string> Profiles { get; private set; } = [];

    string filter = TagVm.AllProfiles;
    public string Filter { get => filter; set { if (value != null && Set(ref filter, value)) Refilter(); } }

    /// <summary>열쇠 글(프로파일·묶음)이 이 프로파일을 가리키는가. 묶음은 그 묶음의 프로파일 합집합.</summary>
    bool KeyHas(string key, string profile) => key == profile || ProfileKey.Names(Site, key).Contains(profile);

    /// <summary>입력 줄이 이 프로파일에 보이는가: 열쇠가 가리키거나, 프로파일 묶음 줄이면 묶음의 프로파일에 든다.</summary>
    bool InputHas(ManRowVm r, string profile) =>
        r.St.Name == "MAN_PROFILES" ? r.St.Args.Skip(1).Any(a => KeyHas(a.Text.Trim(), profile)) : KeyHas(r.Profile, profile);

    void Refilter()
    {
        Cards = all.Where(c => filter == TagVm.AllProfiles || KeyHas(c.Profile, filter)).ToList();
        Inputs = allInputs.Where(r => filter == TagVm.AllProfiles || InputHas(r, filter)).ToList();
        Raise(nameof(Cards));
        Raise(nameof(Inputs));
        Raise(nameof(HasInputs));
        Raise(nameof(CountText));
    }

    public void Reload(Site s, IReadOnlyList<Finding> findings, Func<Piece, bool> changed, IReadOnlyList<Piece>? touched)
    {
        int? prev = Selected?.Ordinal;
        int prevBlock = Picked?.Index ?? -1, prevField = HotFieldIndex;
        Site = s;
        var b = new BlockBuilder(s.Names, findings);
        var z = Zone;
        (string?, Severity?) Worst(IEnumerable<Piece> ps)
        {
            var set = ps.ToHashSet();
            var w = findings.Where(f => f.Piece != null && set.Contains(f.Piece)).OrderBy(f => f.Severity).FirstOrDefault();
            return (w?.Text, w?.Severity);
        }
        ManRowVm Row(Statement st, CardVm? card)
        {
            var (text, sev) = Worst([st]);
            var row = new ManRowVm
            {
                Owner = this, Card = card, St = st, Ordinal = z!.Ordinal(st), Status = text, StatusSeverity = sev, IsChanged = changed(st),
                Head = st.Name switch { "MAN_ELSE" => "그 외", "MAN" => "", _ => st.Args.Count > 0 ? st.Args[0].Text : "" },
                Chips = st.Name == "MAN" && st.Args.Count > 2 ? b.ManChips(st.Args[2]) : [],
                Comment = Strip(st.TailComment),
                Blocks = st.Name is "MAN" or "MAN_ELSE"
                    ? st.Args.Skip(st.Name == "MAN" ? 3 : 2).Select((a, i) => b.ManOut(st, a, i)).ToList()
                    : [b.Statement(st, Place.Manual)],
            };
            foreach (var x in row.Blocks)
            {
                var block = x;
                x.Row = row;
                x.Pick = new Command(() => PickBlock(block));
            }
            return row;
        }
        var cards = new List<CardVm>();
        foreach (var c in z == null ? [] : s.ManualChains.Where(c => z.Statements.Contains(c.Rows[0])))
        {
            var card = new CardVm { Owner = this, Chain = c };
            card.Rows = c.Rows.Select(st => Row(st, card)).ToList();
            (card.Status, card.StatusSeverity) = Worst(c.Rows);
            cards.Add(card);
        }
        all = cards;
        allInputs = z == null ? [] : z.Statements.Where(st => InputNames.Contains(st.Name)).Select(st => Row(st, null)).ToList();
        Profiles = s.Profiles.Select(p => p.Name).ToList();
        // 거르기 목록: 카드·입력 줄의 열쇠를 프로파일 이름으로 푼 것(묶음은 그 묶음의 프로파일)
        IEnumerable<string> Keys() => cards.Select(c => c.Profile).Concat(allInputs.Where(r => r.St.Name != "MAN_PROFILES").Select(r => r.Profile))
            .Concat(allInputs.Where(r => r.St.Name == "MAN_PROFILES").SelectMany(r => r.St.Args.Skip(1).Select(a => a.Text.Trim())));
        Filters = [TagVm.AllProfiles, .. Keys().SelectMany(k => ProfileKey.Names(s, k) is { Count: > 0 } n ? n : [k]).Distinct()];
        if (!Filters.Contains(filter)) filter = TagVm.AllProfiles;
        foreach (var n in new[] { nameof(IsEmpty), nameof(Profiles), nameof(Filters), nameof(Filter), nameof(ModeChoices) }) Raise(n);
        Refilter();

        var rows = AllRows.ToList();
        var pick = touched is { Count: > 0 } ? rows.FirstOrDefault(r => touched.Any(t => ReferenceEquals(t, r.St))) : null;
        bool same = pick == null;
        if (pick != null && (pick.Card != null ? !Cards.Contains(pick.Card) : !Inputs.Contains(pick))) Filter = TagVm.AllProfiles;
        pick ??= prev is int p ? rows.FirstOrDefault(r => r.Ordinal == p) : null;
        selected = null;
        Select(pick, scroll: touched is { Count: > 0 });
        if (same && pick != null) Restore(pick.Blocks, prevBlock, prevField);
    }

    IEnumerable<ManRowVm> AllRows => all.SelectMany(c => c.Rows).Concat(allInputs);

    // ---- 고르기 ----

    ManRowVm? selected;
    public ManRowVm? Selected => selected;
    public bool HasSelection => selected != null;
    public event Action<object>? ScrollTo;

    public void Select(ManRowVm? r, bool scroll)
    {
        if (selected != null) selected.IsCurrent = false;
        selected = r;
        if (r != null) r.IsCurrent = true;
        foreach (var c in all) c.IsHot = r != null && ReferenceEquals(c, r.Card);
        Picked = r?.Blocks.FirstOrDefault();
        Raise(nameof(Selected));
        Raise(nameof(HasSelection));
        RaiseCode();
        CommandManager.InvalidateRequerySuggested();
        if (scroll && r != null) ScrollTo?.Invoke((object?)r.Card ?? r);
    }

    protected override void OnPicked()
    {
        Raise(nameof(DetailTitle));
        Raise(nameof(DetailHead));
    }

    public void PickBlock(BlockVm b)
    {
        if (b.Row is ManRowVm r && !ReferenceEquals(r, selected)) Select(r, false);
        Picked = b;
    }

    public void SelectPiece(Piece p)
    {
        var r = AllRows.FirstOrDefault(x => ReferenceEquals(x.St, p));
        if (r != null && (r.Card != null ? !Cards.Contains(r.Card) : !Inputs.Contains(r))) Filter = TagVm.AllProfiles;
        Select(r, true);
    }

    public string DetailTitle => Selected == null ? "" : Selected.IsInput ? $"{Selected.OrdinalText}줄 · " : $"{Selected.OrdinalText}줄 · {Selected.Profile} · {Selected.St.Args[1].Text} · ";
    public string DetailHead => Selected == null ? "" : Selected.IsInput ? Picked?.Head ?? "" : Picked == null || Picked.Raw ? (Selected.IsElse ? "그 외" : "가지") : Picked.Head;

    // ---- 명령 ----

    public ICommand AddCard { get; }
    public ICommand AddInput { get; }
    public ICommand AddBranch { get; }
    public ICommand DeleteRow { get; }
    public ICommand DeleteCard { get; }
    public ICommand DeleteBlock { get; }
    public ICommand EditCond { get; }
    public ICommand Pick { get; }

    // 기본 입력: 평범한 입력 별칭(IN_ACTIVE_HIGH·미연결·START 스위치 자신은 넣자마자 N-3·M-8 줄이 된다). 없으면 논리 입력 이름
    string FirstInput() => Site.Names.Of(NameKind.Input).FirstOrDefault(d => !d.Core && !d.Weak && d.Value != 16)?.Name ?? "IN_EXT_1";
    string FirstOutput() => Choices.For(new ArgSpec(ArgKind.Output), Site.Names).FirstOrDefault(c => c.Group == "사이트")?.Name ?? "OUT_EXT_1";

    async Task AddCardAsync()
    {
        if (!Main.CommitPending()) return;
        if (Zone is not { } z) return;
        var vm = new CardAddVm(Profiles, Site.ManualChains.Where(c => z.Statements.Contains(c.Rows[0])).ToList(), Selected?.Card?.Profile, ModeChoices.Contains("ANY"));
        if (await Main.Show(vm) != "ok" || vm.Profile is not { } pf) return;
        var items = vm.Template is { Chain: { } src }
            ? src.Rows.Select(st => new NewStatement(st.Name, st.Args.Select((a, i) => i == 0 ? pf : i == 1 ? vm.Mode : BlockBuilder.Collapse(a.Text)).ToList())).ToList()
            : [new NewStatement("MAN_ELSE", [pf, vm.Mode, "NO_OUT"])];
        // 넣는 자리: 마지막 사슬 뒤(입력 줄 앞)
        var last = Site.ManualChains.Where(c => z.Statements.Contains(c.Rows[0])).LastOrDefault();
        int at = last != null ? z.Ordinal(last.Rows[^1]) : 0;
        Main.Apply(SiteEdit.Insert(Site, z, at, items));
    }

    /// <summary>
    /// 입력 줄 추가: 종류 팝업(방향 전환·후방 장애물, 코어 v0.60.0 이상 시작 스위치, v0.63.0 이상 프로파일 묶음) → 표 끝에 한 줄.
    /// 프로파일 묶음 줄은 표 맨 앞(코어가 표를 위에서 훑으며 읽는다)에 가장 작은 빈 묶음 번호로 넣는다.
    /// </summary>
    async Task AddInputAsync()
    {
        if (!Main.CommitPending()) return;
        if (Zone is not { } z) return;
        var buttons = new List<AskButton> { new("cancel", "취소", Default: true), new("MAN_FB_SW", "방향 전환"), new("MAN_OBS_B", "후방 장애물") };
        if (Site.Names.Has(GrammarTable.StartSwVersion)) buttons.Add(new("MAN_START_SW", "시작 스위치"));
        if (Site.Names.Has(GrammarTable.CrossVersion)) buttons.Add(new("MAN_PROFILES", "묶음"));
        var kind = await Main.Show(new AskVm("입력 추가", [], buttons));
        if (kind is null or "cancel") return;
        var pf = Selected?.Card?.Profile is { } cp && !cp.StartsWith("PROFILE_SET", StringComparison.Ordinal) ? cp
            : filter != TagVm.AllProfiles ? filter : Profiles.FirstOrDefault();
        if (pf == null) { Main.Notify("프로파일 없음"); return; }
        if (kind == "MAN_PROFILES")
        {
            var used = Site.ManualRows.Where(r => r.Name == "MAN_PROFILES" && r.Args.Count > 0).Select(r => ProfileKey.SetNumber(Site, r.Args[0].Node)).OfType<long>().ToHashSet();
            var k = Enumerable.Range(0, 16).FirstOrDefault(i => !used.Contains(i), -1);
            if (k < 0) { Main.Notify("묶음 번호 16개 모두 씀"); return; }
            Main.Apply(SiteEdit.Insert(Site, z, 0, [new NewStatement("MAN_PROFILES", [$"PROFILE_SET( {k} )", pf])]));
            return;
        }
        Main.Apply(SiteEdit.Insert(Site, z, z.Statements.Count, [new NewStatement(kind, [pf, FirstInput()])]));
    }

    /// <summary>가지 추가: 고른 가지 아래(끝 줄을 골랐으면 끝 줄 앞).</summary>
    void AddBranchRow()
    {
        if (Selected?.Card is not { } card || Zone is not { } z) return;
        var after = Selected.IsElse ? card.Rows.LastOrDefault(r => !r.IsElse) : Selected;
        int at = after != null ? after.Ordinal : z.Ordinal(card.Chain.Rows[0]) - 1;
        Main.Apply(SiteEdit.Insert(Site, z, at, [new NewStatement("MAN", [card.Profile, card.Mode, $"IN( ON({FirstInput()}) )", "NO_OUT"])]));
    }

    void DeleteSelected()
    {
        if (Selected is not { } r) return;
        if (r.IsElse) { Main.Notify("끝 줄은 못 지움"); return; }
        int ord = r.Ordinal;
        if (Main.Apply(SiteEdit.Delete(Site, [r.St])))
            Select(AllRows.FirstOrDefault(x => x.Ordinal == ord) ?? AllRows.LastOrDefault(), true);
    }

    void DeleteSelectedCard()
    {
        if (Selected?.Card is not { } card) return;
        Main.Apply(SiteEdit.Delete(Site, card.Chain.Rows.Cast<Piece>().ToList()));
    }

    public void SetMode(CardVm card, string mode) =>
        Main.Apply(Chain(Site, card.Chain.Rows.Cast<Piece>().ToList(), (s, p) => SiteEdit.SetArg(s, (Statement)p, ((Statement)p).Args[1], mode)));

    async Task EditCondAsync(ManRowVm r)
    {
        if (r.St.Name != "MAN" || r.St.Args.Count < 3) return;
        if (Main.CommitKeep(r.St) is not Statement st || st.Args.Count < 3) return; // 칸 확정이 줄을 다시 쓰면 새 사이트의 같은 줄로
        var vm = new ManCondVm(st.Args[2], Site.Names, ScopeOf(Site, st));
        if (await Main.Show(vm) != "ok") return;
        Main.Apply(SiteEdit.SetArg(Site, st, st.Args[2], vm.Text));
    }

    /// <summary>[+] 메뉴: 켬 / 끔 / 벨트 / 출력 없음.</summary>
    public IReadOnlyList<OutChoice> OutChoices => [new("켬", "ON"), new("끔", "OFF"), new("벨트", "BELT"), new("출력 없음", "NO_OUT")];

    public void AddOut(ManRowVm r, string kind)
    {
        if (r.IsInput) return;
        var belt = Site.Names.Belts.Keys.FirstOrDefault() ?? "1";
        var text = kind switch { "ON" => $"ON({FirstOutput()})", "OFF" => $"OFF({FirstOutput()})", "BELT" => $"BELT( {belt}, CW )", _ => "NO_OUT" };
        int index = r.Blocks.Count;
        if (Main.Apply(SiteEdit.InsertArg(Site, r.St, r.St.Args.Count, text)) && Selected?.Blocks.Count > index) Picked = Selected.Blocks[index];
    }

    void DeleteOut(BlockVm? b)
    {
        if (b?.Row is not ManRowVm { IsInput: false } r || b.Index < 0 || r.Blocks.Count < 2) return;
        Main.Apply(SiteEdit.DeleteArg(Site, r.St, r.OutStart + b.Index));
    }

    /// <summary>가지 끌기: 같은 사슬 안, 끝 줄 위로만.</summary>
    public void MoveRow(ManRowVm moving, ManRowVm? target)
    {
        if (Zone is not { } z || moving.IsElse || moving.IsInput || target == null || ReferenceEquals(moving, target)) return;
        if (!ReferenceEquals(moving.Card, target.Card)) { Main.Notify("다른 사슬"); return; }
        Main.Apply(SiteEdit.Move(Site, [moving.St], z, target.Ordinal - 1));
    }

    /// <summary>사슬 끌기: target 카드 앞으로(null 이면 마지막 사슬 뒤).</summary>
    public void MoveCard(CardVm moving, CardVm? target)
    {
        if (Zone is not { } z || ReferenceEquals(moving, target)) return;
        int index = target != null ? z.Ordinal(target.Chain.Rows[0]) - 1 : z.Ordinal(all[^1].Chain.Rows[^1]);
        Main.Apply(SiteEdit.Move(Site, moving.Chain.Rows.Cast<Piece>().ToList(), z, index));
    }
}

/// <summary>본뜰 사슬 항목(null = 빈 카드).</summary>
public sealed record ChainChoice(string Text, ManChain? Chain)
{
    public override string ToString() => Text;
}

/// <summary>카드 추가 팝업: 프로파일(1~63), 모드, 본뜰 사슬(빈 카드 / 다른 프로파일의 사슬).</summary>
public sealed class CardAddVm : DialogVm
{
    public CardAddVm(IReadOnlyList<string> profiles, IReadOnlyList<ManChain> chains, string? current, bool anyOk = false)
    {
        AnyOk = anyOk;
        Profiles = profiles;
        profile = current ?? profiles.FirstOrDefault();
        Templates = [new ChainChoice("빈 카드", null), .. chains.Select(c => new ChainChoice($"{c.Profile} · {c.Mode} · 가지 {c.Rows.Count(r => r.Name == "MAN")}", c))];
        template = Templates[0];
        Ok = new Command(() => { if (Profile != null) Close("ok"); }, () => Profile != null);
    }

    public override string Title => "카드 추가";
    public IReadOnlyList<string> Profiles { get; }
    public IReadOnlyList<string> Modes => ManualVm.Modes;

    /// <summary>모드 ANY(코어 v0.63.0 이상).</summary>
    public bool AnyOk { get; }
    public IReadOnlyList<ChainChoice> Templates { get; }
    public ICommand Ok { get; }

    string? profile;
    public string? Profile { get => profile; set => Set(ref profile, value); }

    string mode = "IDLE";
    public bool IsIdle { get => mode == "IDLE"; set { if (value) { mode = "IDLE"; Raise(nameof(IsJog)); Raise(nameof(IsAny)); } } }
    public bool IsJog { get => mode == "JOG"; set { if (value) { mode = "JOG"; Raise(nameof(IsIdle)); Raise(nameof(IsAny)); } } }
    public bool IsAny { get => mode == "ANY"; set { if (value) { mode = "ANY"; Raise(nameof(IsIdle)); Raise(nameof(IsJog)); } } }
    public string Mode => mode;

    ChainChoice template;
    public ChainChoice Template
    {
        get => template;
        set
        {
            if (value == null || !Set(ref template, value)) return;
            if (value.Chain is { } c)
            {
                if (c.Mode == "JOG") IsJog = true; else if (c.Mode == "ANY" && AnyOk) IsAny = true; else IsIdle = true;
                Raise(nameof(IsIdle)); Raise(nameof(IsJog)); Raise(nameof(IsAny));
            }
        }
    }
}
