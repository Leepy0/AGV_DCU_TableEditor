using System.Windows.Input;
using TableEditor.App.Blocks;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.App.ViewModels;

/// <summary>호기 지도 칸 하나(0~99). Color = 프로파일 색 순번(-1 = 어느 줄에도 안 걸림), IsHot = 고른 줄의 호기.</summary>
public sealed class MapCellVm(int id, string? profile, int color, int ordinal) : Observable
{
    public int Id { get; } = id;
    public string? Profile { get; } = profile;
    public int Color { get; } = color;
    public bool IsEmpty => Profile == null;
    public string Tip => Profile == null ? $"호기 {Id}" : $"호기 {Id} · {Profile} · {ordinal}줄";
    public override string ToString() => Tip;

    bool hot;
    public bool IsHot { get => hot; set => Set(ref hot, value); }
}

/// <summary>프로파일 표 한 줄. 머리 줄(IsCurrent)에서 칸을 바로 고친다(타입·처음·끝 숫자, 프로파일·적재 드롭다운).</summary>
public sealed class ProfileRowVm : Observable
{
    public const string NoLoad = "없음", Sensors = "센서";

    public ProfileVm Owner { get; }
    public Piece Piece { get; }
    public ProfileRow? Row { get; }
    public int Ordinal { get; }
    public string OrdinalText => Ordinal > 0 ? Ordinal.ToString() : "";
    public string Profile { get; } = "";
    public string TypeText { get; } = "";
    public string FirstText { get; } = "";
    public string LastText { get; } = "";
    public string LoadText { get; } = NoLoad;
    public IReadOnlyList<Chip> Chips { get; } = [];
    public BlockVm? LoadBlock { get; }
    public bool IsLoadIf => Row?.St.Name == "PROFILE_LOAD_IF";

    /// <summary>화면 읽기 프로그램의 줄 이름.</summary>
    public override string ToString() => $"{OrdinalText}줄 {Profile} {FirstText}–{LastText}";
    public int Color { get; }
    public string Comment { get; } = "";
    public string? Status { get; }
    public Severity? StatusSeverity { get; }
    public bool IsError => StatusSeverity == Severity.Error;
    public bool IsRaw => Row == null;
    public bool IsChanged { get; }

    public ProfileRowVm(ProfileVm owner, Site site, BlockBuilder b, IReadOnlyList<Finding> findings, Piece piece, int ordinal, bool changed, int color)
    {
        Owner = owner;
        Piece = piece;
        Ordinal = ordinal;
        IsChanged = changed;
        Color = color;
        var worst = findings.Where(f => ReferenceEquals(f.Piece, piece)).OrderBy(f => f.Severity).FirstOrDefault();
        Status = worst?.Text;
        StatusSeverity = worst?.Severity;
        if (piece is not Statement st || site.ProfileRows.FirstOrDefault(r => ReferenceEquals(r.St, st)) is not { } row || st.Args.Count < 4)
        {
            LoadBlock = BlockBuilder.RawBlock(piece.Text);
            return;
        }
        Row = row;
        Profile = row.Profile;
        TypeText = BlockBuilder.Collapse(st.Args[1].Text);
        FirstText = BlockBuilder.Collapse(st.Args[2].Text);
        LastText = BlockBuilder.Collapse(st.Args[3].Text);
        var load = st.Args.Count > 4 ? st.Args[4] : null;
        Chips = BlockBuilder.LoadChips(load);
        LoadText = st.Name switch { "PROFILE_LOAD" => Sensors, "PROFILE_LOAD_IF" when load != null => BlockBuilder.Collapse(load.Text), _ => NoLoad };
        if (st.Name == "PROFILE_LOAD") LoadBlock = b.Statement(st, Place.Frame);
        Comment = BlockHost.Strip(st.TailComment);
    }

    bool current;
    public bool IsCurrent { get => current; set { if (Set(ref current, value) && value) ResetDrafts(); } }

    // ---- 줄 안 편집 ----

    string typeDraft = "", firstDraft = "", lastDraft = "", profileDraft = "", loadDraft = NoLoad;

    public string TypeDraft { get => typeDraft; set { if (Set(ref typeDraft, value)) Raise(nameof(TypeBad)); } }
    public string FirstDraft { get => firstDraft; set { if (Set(ref firstDraft, value)) { Raise(nameof(FirstBad)); Raise(nameof(LastBad)); } } }
    public string LastDraft { get => lastDraft; set { if (Set(ref lastDraft, value)) Raise(nameof(LastBad)); } }

    static long? Num(string s) => long.TryParse(s.Trim(), out var v) ? v : null;

    /// <summary>빨간 테: 숫자가 아니거나 범위 밖(타입 0~999, 호기 0~99, 처음 ≤ 끝).</summary>
    public bool TypeBad => Num(typeDraft) is not (>= 0 and <= 999);
    public bool FirstBad => Num(firstDraft) is not (>= 0 and <= 99);
    public bool LastBad => Num(lastDraft) is not (>= 0 and <= 99) || Num(firstDraft) is long a && Num(lastDraft) is long b && a > b;

    /// <summary>프로파일 드롭다운. 끝 항목 = 새 프로파일.</summary>
    public string ProfileDraft
    {
        get => profileDraft;
        set
        {
            if (value == null || !Set(ref profileDraft, value)) return;
            if (value != Profile) Owner.SetProfile(this, value);
        }
    }

    /// <summary>적재 드롭다운: 없음 / 센서 / 적재 조건 이름.</summary>
    public string LoadDraft
    {
        get => loadDraft;
        set
        {
            if (value == null || !Set(ref loadDraft, value)) return;
            if (value != LoadText) Owner.SetLoad(this, value);
        }
    }

    public void ResetDrafts()
    {
        typeDraft = TypeText;
        firstDraft = FirstText;
        lastDraft = LastText;
        profileDraft = Profile;
        loadDraft = LoadText;
        foreach (var n in new[] { nameof(TypeDraft), nameof(FirstDraft), nameof(LastDraft), nameof(ProfileDraft), nameof(LoadDraft), nameof(TypeBad), nameof(FirstBad), nameof(LastBad) }) Raise(n);
    }
}

/// <summary>
/// 프로파일 화면(사양 9.2절): 줄 표 + 호기 지도(0~99). 줄 순서 = 첫 일치(사양 5.6절)라 끌면 지도가 바로 바뀐다.
/// 이름 바꾸기·삭제는 바뀌는 파일·줄 수를 팝업으로 묻는다.
/// </summary>
public sealed class ProfileVm : BlockHost
{
    public const string NewProfile = "새 프로파일";

    public ProfileVm(MainVm main) : base(main)
    {
        Add = new Command(() => _ = AddRowAsync(), () => Main.CanEdit && Zone != null);
        Delete = new Command(DeleteRow, () => Main.CanEdit && Selected != null);
        Rename = new Command(() => _ = RenameAsync(), () => Main.CanEdit && Selected?.Row != null);
        DeleteProfile = new Command(() => _ = DeleteProfileAsync(), () => Main.CanEdit && Selected?.Row != null);
        EditLoad = new Command(() => _ = EditLoadAsync(), () => Main.CanEdit && Selected?.IsLoadIf == true);
        Commit = new Command(p => CommitNumbers(p as ProfileRowVm ?? Selected));
        Cancel = new Command(p => (p as ProfileRowVm ?? Selected)?.ResetDrafts());
    }

    ZoneBlock? Zone => Site?.ZonesOf("PROFILE_TABLE_BEGIN").FirstOrDefault();

    protected override Piece? CodePiece => Selected?.Piece;

    protected override SignalScope? ScopeFor() => SignalScope.Of(Site, Selected?.Row?.Profile);

    public IReadOnlyList<ProfileRowVm> Rows { get; private set; } = [];
    public IReadOnlyList<MapCellVm> Cells { get; private set; } = [];
    public bool IsEmpty => Rows.Count == 0;
    public string CountText => $"{Rows.Count(r => r.Row != null)}줄";

    /// <summary>프로파일 드롭다운: 정의된 프로파일 + 새 프로파일.</summary>
    public IReadOnlyList<string> ProfileChoices { get; private set; } = [];

    /// <summary>적재 드롭다운: 없음, 센서, 적재 조건 이름들.</summary>
    public IReadOnlyList<string> LoadChoices { get; private set; } = [];

    public void Reload(Site s, IReadOnlyList<Finding> findings, Func<Piece, bool> changed, IReadOnlyList<Piece>? touched)
    {
        int prev = Selected?.Ordinal ?? 0;
        int prevField = HotFieldIndex;
        Site = s;
        var names = s.Profiles.Select(p => p.Name).ToList();
        int ColorOf(string p) => names.IndexOf(p) is var i and >= 0 ? i : names.Count + p.Length;
        var b = new BlockBuilder(s.Names, findings);
        var rows = new List<ProfileRowVm>();
        if (Zone is { } z)
            foreach (var p in z.Body.Where(p => p.Kind is PieceKind.Statement or PieceKind.Raw))
            {
                var ord = p is Statement st ? z.Ordinal(st) : 0;
                var prof = p is Statement { Args.Count: > 0 } x ? x.Args[0].Text : "";
                rows.Add(new ProfileRowVm(this, s, b, findings, p, ord, changed(p), ColorOf(prof)));
            }
        Rows = rows;
        ProfileChoices = [.. names, NewProfile];
        LoadChoices = [ProfileRowVm.NoLoad, ProfileRowVm.Sensors, .. s.Names.Of(NameKind.LoadIf).Select(d => d.Name)];
        Cells = BuildMap(rows);
        foreach (var n in new[] { nameof(Rows), nameof(Cells), nameof(ProfileChoices), nameof(LoadChoices), nameof(IsEmpty), nameof(CountText) }) Raise(n);

        var pick = touched is { Count: > 0 } ? rows.FirstOrDefault(r => touched.Any(t => ReferenceEquals(t, r.Piece))) : null;
        bool same = pick == null;
        pick ??= rows.FirstOrDefault(r => r.Ordinal == prev && prev > 0);
        Select(pick, scroll: touched is { Count: > 0 });
        if (same && pick != null) Restore(Picked == null ? [] : [Picked], -1, prevField);
    }

    /// <summary>호기마다 위에서 첫 줄(타입 0 줄 먼저, 없으면 아무 타입). 원문 줄은 건너뛴다.</summary>
    static List<MapCellVm> BuildMap(IReadOnlyList<ProfileRowVm> rows)
    {
        var live = rows.Where(r => r.Row is { First: not null, Last: not null }).ToList();
        var cells = new List<MapCellVm>(100);
        for (int id = 0; id < 100; id++)
        {
            var hit = live.FirstOrDefault(r => r.Row!.Type == 0 && r.Row.First <= id && id <= r.Row.Last)
                ?? live.FirstOrDefault(r => r.Row!.First <= id && id <= r.Row.Last);
            cells.Add(hit == null ? new MapCellVm(id, null, -1, 0) : new MapCellVm(id, hit.Profile, hit.Color, hit.Ordinal));
        }
        return cells;
    }

    // ---- 고르기 ----

    ProfileRowVm? selected;
    public ProfileRowVm? Selected
    {
        get => selected;
        set => Select(value, scroll: false);
    }

    public bool HasSelection => Selected != null;

    /// <summary>VM 이 줄을 골랐다(명령 뒤): 화면이 그 줄로 스크롤한다.</summary>
    public event Action<object>? ScrollTo;

    public void Select(ProfileRowVm? r, bool scroll)
    {
        if (!ReferenceEquals(selected, r))
        {
            if (selected != null) selected.IsCurrent = false;
            selected = r;
            if (selected != null) selected.IsCurrent = true;
            Raise(nameof(Selected));
            Raise(nameof(HasSelection));
            Raise(nameof(DetailTitle));
        }
        Picked = r?.LoadBlock;
        foreach (var c in Cells)
            c.IsHot = r?.Row is { First: long a, Last: long b } && a <= c.Id && c.Id <= b;
        RaiseCode();
        CommandManager.InvalidateRequerySuggested();
        if (scroll && r != null) ScrollTo?.Invoke(r);
    }

    public void SelectPiece(Piece p) => Select(Rows.FirstOrDefault(r => ReferenceEquals(r.Piece, p)), scroll: true);

    /// <summary>정의 줄(site.h)·적재 조건(LOAD_IF)의 결과: 그 프로파일·조건을 쓰는 첫 줄.</summary>
    public void SelectName(string name) =>
        Select(Rows.FirstOrDefault(r => r.Profile == name || r.LoadText == name), scroll: true);

    public string DetailTitle => Selected == null ? "" : Selected.Row == null ? "원문" : $"{Selected.OrdinalText}줄 · {Selected.Profile}";

    // ---- 명령 ----

    public ICommand Add { get; }
    public ICommand Delete { get; }
    public ICommand Rename { get; }
    public ICommand DeleteProfile { get; }
    public ICommand EditLoad { get; }
    public ICommand Commit { get; }
    public ICommand Cancel { get; }

    /// <summary>타입·처음·끝: 바뀐 칸만, 틀리면 확정하지 않고 되돌린다.</summary>
    public void CommitNumbers(ProfileRowVm? r)
    {
        if (r?.Row is not { } row) return;
        if (r.TypeBad || r.FirstBad || r.LastBad) return;
        var edits = new[] { (2, r.FirstDraft), (3, r.LastDraft), (1, r.TypeDraft) }
            .Where(e => e.Item2.Trim() != BlockBuilder.Collapse(row.St.Args[e.Item1].Text)).ToList();
        if (edits.Count == 0) return;
        var key = Locate(Site, row.St);
        EditResult res = new(Site, null, []);
        foreach (var (i, text) in edits)
        {
            var st = (Statement)res.Site!.File(key.File)!.Pieces[key.Index];
            res = SiteEdit.SetArg(res.Site, st, st.Args[i], text);
            if (!res.Ok) break;
        }
        if (!Main.Apply(res with { Base = Site })) r.ResetDrafts();
    }

    public void SetProfile(ProfileRowVm r, string name)
    {
        if (r.Row is not { } row) return;
        if (name == NewProfile) { _ = NewProfileAsync(r); return; }
        if (!Main.Apply(SiteEdit.SetArg(Site, row.St, row.St.Args[0], name))) r.ResetDrafts();
    }

    /// <summary>새 프로파일: 이름 팝업 → site.h enum 끝 + 이 줄의 프로파일(<see cref="BundleEdit.AddProfile"/> 가 붙인 줄은 이 줄로 대신한다).</summary>
    async Task NewProfileAsync(ProfileRowVm r)
    {
        var name = await AskName("새 프로파일", "PF_", "추가");
        if (name == null || r.Row is not { } row) { r.ResetDrafts(); return; }
        var key = Locate(Site, row.St);
        var res = BundleEdit.AddProfile(Site, name, row.Type ?? 0, row.First ?? 0, row.Last ?? 0);
        if (res.Ok && res.Site!.ZonesOf("PROFILE_TABLE_BEGIN").First().Statements[^1] is { } added)
        {
            res = SiteEdit.Delete(res.Site, [added]);
            if (res.Ok && res.Site!.File(key.File)!.Pieces[key.Index] is Statement st) res = SiteEdit.SetArg(res.Site, st, st.Args[0], name);
        }
        if (!Main.Apply(res with { Base = Site })) r.ResetDrafts();
    }

    async Task<string?> AskName(string title, string text, string ok)
    {
        var vm = new NameVm(title, text, ok);
        return await Main.Show(vm) == "ok" ? vm.Text.Trim() : null;
    }

    /// <summary>적재 바꾸기: 매크로 이름(PROFILE·PROFILE_LOAD·PROFILE_LOAD_IF)과 다섯째 칸.</summary>
    public void SetLoad(ProfileRowVm r, string choice)
    {
        if (r.Row is not { } row) return;
        var st = row.St;
        string name = choice == ProfileRowVm.NoLoad ? "PROFILE" : choice == ProfileRowVm.Sensors ? "PROFILE_LOAD" : "PROFILE_LOAD_IF";
        string? arg = choice == ProfileRowVm.NoLoad ? null
            : choice == ProfileRowVm.Sensors ? (st.Name == "PROFILE_LOAD" ? st.Args[4].Text : $"LOAD_SENSORS({FirstInput()})")
            : choice;
        if (!Main.Apply(ProfileEdit.SetLoad(Site, st, name, arg))) r.ResetDrafts();
    }

    string FirstInput() => Choices.For(new ArgSpec(ArgKind.Input), Site.Names).FirstOrDefault(c => c.Group == "사이트")?.Name ?? "IN_EXT_1";

    /// <summary>[추가]: 고른 줄 아래 한 줄. 프로파일은 고른 줄 것, 호기는 지도의 첫 빈 칸.</summary>
    async Task AddRowAsync()
    {
        if (!Main.CommitPending()) return;
        if (Zone is not { } z) return;
        var free = Cells.FirstOrDefault(c => c.IsEmpty)?.Id ?? 0;
        if (Site.Profiles.Count == 0)
        {
            var name = await AskName("새 프로파일", "PF_", "추가");
            if (name != null) Main.Apply(BundleEdit.AddProfile(Site, name, 0, free, free));
            return;
        }
        var profile = Selected?.Row?.Profile ?? Site.Profiles[0].Name;
        int at = Selected?.Ordinal > 0 ? Selected.Ordinal : z.Statements.Count;
        Main.Apply(SiteEdit.Insert(Site, z, at, [new NewStatement("PROFILE", [profile, "0", free.ToString(), free.ToString()])]));
    }

    void DeleteRow()
    {
        if (Selected is not { } r) return;
        int next = r.Ordinal;
        if (Main.Apply(SiteEdit.Delete(Site, [r.Piece])) && next > 0)
            Select(Rows.FirstOrDefault(x => x.Ordinal == next) ?? Rows.LastOrDefault(x => x.Ordinal > 0), scroll: true);
    }

    /// <summary>줄 끌기: target 줄 앞으로(null 이면 끝).</summary>
    public void MoveRow(ProfileRowVm moving, ProfileRowVm? target)
    {
        if (Zone is not { } z || moving.Piece is not Statement || ReferenceEquals(moving, target)) return;
        int index = target?.Ordinal > 0 ? target.Ordinal - 1 : z.Statements.Count;
        Main.Apply(SiteEdit.Move(Site, [moving.Piece], z, index));
    }

    /// <summary>바뀌는 파일·줄 수 글.</summary>
    static string Impact(Site before, EditResult r) =>
        $"파일 {r.Site!.Files.Count(f => !ReferenceEquals(f, before.File(f.Name)))} · 줄 {r.Touched.Count}";

    async Task RenameAsync()
    {
        if (!Main.CommitPending()) return;
        if (Selected?.Row is not { } row) return;
        var old = row.Profile;
        var name = await AskName("이름 바꾸기", old, "다음");
        if (name == null || name == old) return;
        var s = Site;
        var r = BundleEdit.RenameProfile(s, old, name);
        if (!r.Ok) { await Tell("이름 바꾸기", r.Error!); return; }
        if (await Main.Show(new AskVm("이름 바꾸기", [$"{old} → {name}", Impact(s, r)],
                [new("cancel", "취소", Default: true), new("ok", "바꾸기", Primary: true)])) == "ok")
            Main.Apply(r);
    }

    async Task DeleteProfileAsync()
    {
        if (!Main.CommitPending()) return;
        if (Selected?.Row is not { } row) return;
        var s = Site;
        var r = BundleEdit.DeleteProfile(s, row.Profile);
        if (!r.Ok) { await Tell("프로파일 삭제 막힘", r.Error!); return; }
        if (await Main.Show(new AskVm("프로파일 삭제", [row.Profile, Impact(s, r)],
                [new("cancel", "취소", Default: true), new("ok", "삭제", Primary: true)])) == "ok")
            Main.Apply(r);
    }

    Task Tell(string title, string why) => Main.Show(new AskVm(title, [why], [new("ok", "닫기", Primary: true, Default: true)]));

    /// <summary>적재 조건(LOAD_IF) 팝업: 조건 둘.</summary>
    async Task EditLoadAsync()
    {
        if (!Main.CommitPending()) return;
        if (Selected is not { IsLoadIf: true } r) return;
        var def = Site.Names.Find(r.LoadText);
        if (def?.Def is not Statement st || st.Name != "LOAD_IF") { Main.Notify("적재 조건 없음 · " + r.LoadText); return; }
        var vm = new LoadVm(st, Site.Names, SignalScope.Of(Site, Selected?.Row?.Profile));
        if (await Main.Show(vm) != "ok") return;
        Main.Apply(ProfileEdit.SetLoadIf(Site, st, vm.First.Text(), vm.Second.Text()));
    }
}

/// <summary>
/// 프로파일 화면의 편집 명령 중 엔진에 없는 것(엔진으로 옮길 것): 매크로 이름 바꾸기 + 칸 넣기·지우기를 한 명령으로.
/// </summary>
public static class ProfileEdit
{
    /// <summary>적재: 매크로 이름(name)과 다섯째 칸(arg, null = 없음).</summary>
    public static EditResult SetLoad(Site site, Statement st, string name, string? arg) => SetLoadFrom(site, st, name, arg) with { Base = site };

    static EditResult SetLoadFrom(Site site, Statement st, string name, string? arg)
    {
        var key = BlockHost.Locate(site, st);
        Statement At(Site s) => (Statement)s.File(key.File)!.Pieces[key.Index];
        var r = st.Name == name ? new EditResult(site, null, [st])
            : SiteEdit.ReplaceText(site, st, st.Text[..st.Indent.Length] + name + st.Text[(st.Indent.Length + st.Name.Length)..]);
        if (!r.Ok) return r;
        var cur = At(r.Site!);
        if (cur.Name != name || cur.Args.Count < 4) return new EditResult(null, "문장으로 읽히지 않음", []);
        if (arg == null) return cur.Args.Count > 4 ? SiteEdit.DeleteArg(r.Site!, cur, 4) : r;
        return cur.Args.Count > 4 ? SiteEdit.SetArg(r.Site!, cur, cur.Args[4], arg) : SiteEdit.InsertArg(r.Site!, cur, 4, arg);
    }

    /// <summary>LOAD_IF 조건 둘: 둘째가 null 이면 지운다.</summary>
    public static EditResult SetLoadIf(Site site, Statement st, string? first, string? second) => SetLoadIfFrom(site, st, first, second) with { Base = site };

    static EditResult SetLoadIfFrom(Site site, Statement st, string? first, string? second)
    {
        if (first == null) return new EditResult(null, "조건 1 없음", []);
        var key = BlockHost.Locate(site, st);
        var r = SiteEdit.SetArg(site, st, st.Args[1], first);
        if (!r.Ok) return r;
        var cur = (Statement)r.Site!.File(key.File)!.Pieces[key.Index];
        if (second == null) return cur.Args.Count > 2 ? SiteEdit.DeleteArg(r.Site, cur, 2) : r;
        return cur.Args.Count > 2 ? SiteEdit.SetArg(r.Site, cur, cur.Args[2], second) : SiteEdit.InsertArg(r.Site, cur, 2, second);
    }
}
