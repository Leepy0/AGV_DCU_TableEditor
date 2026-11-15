using System.Windows.Input;
using TableEditor.App.Blocks;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.App.ViewModels;

/// <summary>
/// 나무 한 줄. Level 0 = 프로파일(또는 연결 없음), 1 = 시퀀스 ID, 2 = 표. Table = 표 줄이면 그 이름.
/// Group = 든 프로파일 묶음(전체, 프로파일 이름, 연결 없음이면 null), Id = 든 시퀀스 ID. IsGroupStart = 둘째 묶음부터의 프로파일 머리(구분선).
/// </summary>
public sealed class TreeItemVm(int level, string text, string? table, string? note, bool err) : Observable
{
    public int Level { get; } = level;
    public string Text { get; } = text;
    public string? Table { get; } = table;
    public string? Note { get; } = note;
    public bool IsTable => Table != null;
    public override string ToString() => Text;
    public bool HasError { get; } = err;
    public string? Group { get; init; }
    public string? Id { get; init; }

    bool groupStart;
    public bool IsGroupStart { get => groupStart; set => Set(ref groupStart, value); }
}

/// <summary>스텝 한 줄: 줄 번호, 들여쓰기(라벨 아래 한 단), 라벨 이름표, 분기 묶음 선, 블록.</summary>
public sealed class StepRowVm : Observable
{
    public Piece Piece { get; init; } = null!;
    public int Ordinal { get; init; }
    public string OrdinalText => Ordinal > 0 ? Ordinal.ToString() : "";
    public int Level { get; init; }
    public string? Label { get; init; }
    public bool IsLabel => Label != null;
    public bool InSelect { get; init; }
    public bool SelectTop { get; init; }
    public bool SelectBottom { get; init; }
    public BlockVm Block { get; init; } = null!;
    public bool IsChanged { get; init; }
    public string Comment { get; init; } = "";

    /// <summary>명령을 바꾼 줄: 옛 꼬리 주석이 맞는지 볼 것(주석은 그대로 둔다).</summary>
    public bool CommentCheck { get; init; }
    public string? Status { get; init; }
    public Severity? StatusSeverity { get; init; }
    public bool IsRaw => Block.Raw;
    public override string ToString() => $"{OrdinalText}줄 {Label ?? Block.ToString()}";
}

/// <summary>
/// 시퀀스 화면(사양 9.2절): 나무(프로파일 → 시퀀스 ID → 표) + 스텝 블록 세로 목록. 선택 줄 칸으로 인자를 고치고(새 이름 칸만 읽기 전용),
/// 스텝 추가·명령 바꾸기·지우기·끌어 옮기기, 표 추가·지우기. 명령은 모두 <see cref="MainVm.Apply"/>(검사·되돌리기·저장 차례 그대로).
/// 파라미터는 FND 이름, 시간은 틱 옆에 초.
/// </summary>
public sealed class SeqVm : BlockHost
{
    public SeqVm(MainVm main) : base(main)
    {
        Filter.Changed += Refilter;
        AddStep = new Command(() => _ = AddStepAsync(), () => Main.CanEdit && Zone != null);
        ChangeStep = new Command(() => _ = ChangeStepAsync(), () => Main.CanEdit && SelectedStep?.Piece is Statement);
        DeleteStep = new Command(() => _ = DeleteStepAsync(), () => Main.CanEdit && SelectedStep != null);
        AddTable = new Command(() => _ = AddTableAsync(), () => Main.CanEdit);
        DeleteTable = new Command(() => _ = DeleteTableAsync(), () => Main.CanEdit && Zone != null);
    }

    /// <summary>거르기 막대: 호기 + 프로파일(태그 화면과 같은 것).</summary>
    public FilterVm Filter { get; } = new();
    IReadOnlyList<TreeItemVm> allTree = [];

    protected override Piece? CodePiece => SelectedStep?.Piece;

    /// <summary>새로 정하는 이름 칸만 읽기 전용(이름 바꾸기는 묶음 명령의 몫).</summary>
    protected override bool Locks(FieldInfo f) => f.Spec.Kind == ArgKind.Ident;

    public IReadOnlyList<TreeItemVm> Tree { get; private set; } = [];
    public IReadOnlyList<StepRowVm> Steps { get; private set; } = [];
    public bool IsEmpty => Tree.Count == 0;
    IReadOnlyList<Finding> findings = [];
    IReadOnlyDictionary<string, int> baseline = new Dictionary<string, int>();

    static readonly HashSet<string> Branches = ["WHEN", "WHEN_STATE", "WHEN_IF", "WHEN_IF_IN", "WHEN_ANY2", "WHEN_ID", "OTHERWISE", "SELECT_END_TO"];

    public void Reload(Site s, IReadOnlyList<Finding> list, IReadOnlyDictionary<string, int> saved, IReadOnlyList<Piece>? touched)
    {
        var prevTable = SelectedTable;
        int prevStep = SelectedStep?.Ordinal ?? 0;
        int prevField = HotFieldIndex;
        Site = s;
        findings = list;
        baseline = saved;

        var bad = list.Where(f => f.Severity == Severity.Error && f.Piece is Statement st && s.ZoneOf(st) is { Open: "SEQ_BEGIN" or "ACT_BEGIN" })
            .Select(f => s.ZoneOf((Statement)f.Piece!)!.Name).ToHashSet();
        var tree = new List<TreeItemVm>();
        var maps = s.SeqMap.Concat(s.ActMap).ToList();
        foreach (var pg in maps.GroupBy(m => m.AnyProfile ? TagVm.AllProfiles : m.Profile))
        {
            tree.Add(new TreeItemVm(0, pg.Key, null, null, false) { Group = pg.Key });
            foreach (var ig in pg.GroupBy(m => BlockBuilder.Collapse(m.Id)))
            {
                tree.Add(new TreeItemVm(1, ig.Key, null, null, false) { Group = pg.Key, Id = ig.Key });
                foreach (var m in ig) tree.Add(new TreeItemVm(2, m.Table, m.Table, Count(s, m.Table), bad.Contains(m.Table)) { Group = pg.Key, Id = ig.Key });
            }
        }
        var linked = maps.Select(m => m.Table).ToHashSet();
        var loose = s.Seqs.Where(t => !linked.Contains(t.Name)).ToList();
        if (loose.Count > 0)
        {
            tree.Add(new TreeItemVm(0, "연결 없음", null, null, false));
            foreach (var t in loose) tree.Add(new TreeItemVm(1, t.Name, t.Name, t.Steps.Count.ToString(), bad.Contains(t.Name)));
        }
        allTree = tree;
        Filter.Profiles = [TagVm.AllProfiles, .. s.Profiles.Select(p => p.Name).Union(maps.Where(m => !m.AnyProfile).Select(m => m.Profile)).Distinct()];
        ShowTree();

        string? table = touched is { Count: > 0 } && touched[0] is Statement ts && s.ZoneOf(ts) is { Open: "SEQ_BEGIN" or "ACT_BEGIN" } tz ? tz.Name : prevTable;
        if (table != null && !s.Seqs.Any(t => t.Name == table)) table = null; // 지운 표·다른 표의 편집
        if (table != null && !Tree.Any(t => t.Table == table))
        {
            // 거르기에 가려진 표: 방금 고친 표면 거르기를 풀고, 아니면 보이는 첫 표로
            if (touched is { Count: > 0 }) { Filter.Clear(); ShowTree(); }
            else table = null;
        }
        table ??= Tree.FirstOrDefault(t => t.IsTable)?.Table;
        selectedTable = null;
        ShowTable(table);
        var pick = touched is { Count: > 0 } ? Steps.FirstOrDefault(r => touched.Any(t => ReferenceEquals(t, r.Piece))) : null;
        bool same = pick == null;
        pick ??= Steps.FirstOrDefault(r => r.Ordinal == prevStep && prevStep > 0);
        SelectStep(pick, scroll: touched is { Count: > 0 });
        if (same && pick != null) Restore([pick.Block], 0, prevField);
    }

    // ---- 거르기 ----

    /// <summary>프로파일 묶음째 거른다. 전체 묶음은 모든 호기·프로파일에 맞고, 연결 없음은 거를 때 빠진다.</summary>
    bool Keep(TreeItemVm head) =>
        Filter.Match(head.Group, head.Group == TagVm.AllProfiles, n => head.Group == TagVm.AllProfiles || Site.ProfilesAt(n).Contains(head.Group!));

    /// <summary>보이는 나무를 다시 만든다(표 고르기는 그대로).</summary>
    void ShowTree()
    {
        var list = new List<TreeItemVm>();
        for (int i = 0; i < allTree.Count;)
        {
            int j = i + 1;
            while (j < allTree.Count && allTree[j].Level > 0) j++;
            if (Keep(allTree[i])) list.AddRange(allTree.Skip(i).Take(j - i));
            i = j;
        }
        foreach (var t in list) t.IsGroupStart = t.Level == 0 && !ReferenceEquals(t, list[0]);
        Tree = list;
        Raise(nameof(Tree));
        Raise(nameof(IsEmpty));
    }

    /// <summary>거르기가 바뀜: 고른 표가 가려지면 보이는 첫 표로.</summary>
    void Refilter()
    {
        ShowTree();
        if (Tree.FirstOrDefault(t => ReferenceEquals(t, node)) is { } keep) { node = keep; Raise(nameof(Node)); return; }
        var first = Tree.FirstOrDefault(t => t.Table == selectedTable) ?? Tree.FirstOrDefault(t => t.IsTable);
        if (first?.Table != selectedTable) { ShowTable(first?.Table); SelectStep(Steps.FirstOrDefault(), true); }
        node = first;
        Raise(nameof(Node));
    }

    static string Count(Site s, string table) => s.Seqs.FirstOrDefault(t => t.Name == table)?.Steps.Count.ToString() ?? "—";

    // ---- 표 ----

    string? selectedTable;
    public string? SelectedTable => selectedTable;
    public string TableTitle => selectedTable ?? "";

    TreeItemVm? node;
    /// <summary>나무에서 고른 줄. 머리(프로파일·ID)를 누르면 그 아래 첫 표.</summary>
    public TreeItemVm? Node
    {
        get => node;
        set
        {
            if (value == null || ReferenceEquals(value, node)) return;
            var t = value.IsTable ? value : Tree.SkipWhile(x => !ReferenceEquals(x, value)).FirstOrDefault(x => x.IsTable);
            node = t;
            Raise();
            if (t?.Table != null && t.Table != selectedTable) { ShowTable(t.Table); SelectStep(Steps.FirstOrDefault(), true); }
        }
    }

    void ShowTable(string? name)
    {
        selectedTable = name;
        node = Tree.FirstOrDefault(t => t.Table == name);
        Raise(nameof(Node));
        Raise(nameof(SelectedTable));
        Raise(nameof(TableTitle));
        var zone = name == null ? null : Site.Zones.FirstOrDefault(z => z.Open is "SEQ_BEGIN" or "ACT_BEGIN" && z.Name == name);
        Steps = zone == null ? [] : BuildSteps(zone);
        Raise(nameof(Steps));
        Raise(nameof(StepCount));
    }

    public string StepCount => $"스텝 {Steps.Count(r => r.Ordinal > 0)}";

    List<StepRowVm> BuildSteps(ZoneBlock zone)
    {
        var b = new BlockBuilder(Site.Names, findings);
        // 바뀐 줄 점: 표를 열 때마다 기준선을 새로 센다(표는 나중에 골라 열린다)
        var changed = ChangedBy(baseline);
        var body = zone.Body.Where(p => p.Kind is PieceKind.Statement or PieceKind.Raw).ToList();
        string Name(int i) => i < body.Count && body[i] is Statement s ? s.Name : "";

        // 분기 묶음: SELECT_BEGIN … SELECT_END, 또는 이어진 WHEN 줄들(사양 4.4절)
        var spans = new List<(int From, int To)>();
        for (int i = 0; i < body.Count; i++)
        {
            int j = i;
            if (Name(i) == "SELECT_BEGIN")
            {
                while (j + 1 < body.Count && Branches.Contains(Name(j + 1))) j++;
                if (Name(j + 1) == "SELECT_END") j++;
            }
            else if (Branches.Contains(Name(i)))
                while (j + 1 < body.Count && Branches.Contains(Name(j + 1))) j++;
            else continue;
            spans.Add((i, j));
            i = j;
        }

        var list = new List<StepRowVm>();
        bool underLabel = false;
        for (int i = 0; i < body.Count; i++)
        {
            var p = body[i];
            var st = p as Statement;
            bool label = st?.Name == "L";
            underLabel |= label;
            var span = spans.FirstOrDefault(x => x.From <= i && i <= x.To);
            bool inSpan = spans.Any(x => x.From <= i && i <= x.To);
            var worst = findings.Where(f => ReferenceEquals(f.Piece, p)).OrderBy(f => f.Severity).FirstOrDefault();
            list.Add(new StepRowVm
            {
                Piece = p, Ordinal = st != null ? zone.Ordinal(st) : 0, Level = label || !underLabel ? 0 : 1,
                Label = label && st!.Args.Count > 0 ? BlockBuilder.Collapse(st.Args[0].Text) : null,
                InSelect = inSpan, SelectTop = inSpan && span.From == i, SelectBottom = inSpan && span.To == i,
                Block = st != null ? b.Statement(st, Place.Step) : BlockBuilder.RawBlock(p.Text),
                IsChanged = changed(p), Comment = st == null ? "" : Strip(st.TailComment), Status = worst?.Text, StatusSeverity = worst?.Severity,
                CommentCheck = st != null && st.TailComment.Length > 0 && commentCheck.Contains(p.Text),
            });
        }
        foreach (var r in list)
        {
            var row = r;
            r.Block.Row = r;
            r.Block.Pick = new Command(() => SelectStep(row, false));
        }
        return list;
    }

    // ---- 스텝 고르기 ----

    StepRowVm? step;
    public StepRowVm? SelectedStep { get => step; set => SelectStep(value, false); }
    public bool HasStep => SelectedStep != null;
    public event Action<object>? ScrollTo;

    public void SelectStep(StepRowVm? r, bool scroll)
    {
        if (!ReferenceEquals(step, r))
        {
            step = r;
            Raise(nameof(SelectedStep));
            Raise(nameof(HasStep));
        }
        Picked = r?.Block;
        Raise(nameof(DetailTitle));
        Raise(nameof(DetailHead));
        RaiseCode();
        CommandManager.InvalidateRequerySuggested();
        if (scroll && r != null) ScrollTo?.Invoke(r);
    }

    public string DetailTitle => SelectedStep == null ? "" : $"{SelectedStep.OrdinalText}줄 · {selectedTable} · ";
    public string DetailHead => Picked == null || Picked.Raw ? "원문" : Picked.Head;

    /// <summary>표 이름으로(대응표에서): 그 표의 첫 스텝.</summary>
    public void SelectTable(string name)
    {
        ShowTable(name);
        SelectStep(Steps.FirstOrDefault(), true);
    }

    public bool HasSeq(string id) => allTree.Any(t => t.IsTable && t.Id == id);

    /// <summary>
    /// 시퀀스 ID 의 표를 고르고 보이게 한다: 그 프로파일 묶음 → 전체 묶음 → 다른 묶음 차례. 거르기에 가려 있으면 거르기를 푼다.
    /// </summary>
    public bool SelectSeq(string? profile, string id)
    {
        TreeItemVm? Find() => Tree.Where(t => t.IsTable && t.Id == id)
            .OrderBy(t => t.Group == profile ? 0 : t.Group == TagVm.AllProfiles ? 1 : 2).FirstOrDefault();
        var hit = Find();
        if (hit == null && Filter.IsSet) { Filter.Clear(); ShowTree(); hit = Find(); }
        if (hit == null) return false;
        ShowTable(hit.Table);
        node = hit;
        Raise(nameof(Node));
        SelectStep(Steps.FirstOrDefault(), true);
        return true;
    }

    // ---- 구조 편집 ----

    public ICommand AddStep { get; }
    public ICommand ChangeStep { get; }
    public ICommand DeleteStep { get; }
    public ICommand AddTable { get; }
    public ICommand DeleteTable { get; }

    /// <summary>고른 표의 구역.</summary>
    ZoneBlock? Zone => selectedTable == null ? null : Site?.Zones.FirstOrDefault(z => z.Open is "SEQ_BEGIN" or "ACT_BEGIN" && z.Name == selectedTable);

    string Fill(ArgSpec a) => Choices.DefaultArg(a, Site.Names);

    static readonly IReadOnlyList<AskButton> OkOnly = [new AskButton("ok", "확인", Primary: true, Default: true)];

    /// <summary>[추가]: 고른 스텝 뒤(없으면 끝)에 새 스텝 하나(꼭 있는 칸만 기본값).</summary>
    public async Task AddStepAsync()
    {
        if (Zone is not { } z) return;
        var d = new StepVm("스텝 추가", "추가", z.Open == "ACT_BEGIN", null, e => Choices.DefaultText(e, Site.Names));
        if (await Main.Show(d) != "ok" || Zone is not { } zone) return;
        var e = d.Command.Entry;
        int index = SelectedStep is { } s ? (s.Ordinal > 0 ? s.Ordinal : SlotOf(s, zone)) : zone.Statements.Count;
        Main.Apply(SiteEdit.Insert(Site, zone, index, [new NewStatement(e.Name, e.Args.Where(a => a.Least > 0).Select(Fill).ToList())]));
    }

    /// <summary>명령을 바꾼 줄의 글자(꼬리 주석 확인 표시). 글자가 같은 줄이면 다시 그린 뒤에도 표시가 남는다.</summary>
    readonly HashSet<string> commentCheck = [];

    /// <summary>
    /// [명령 바꾸기]: 같은 종류 칸의 인자는 옮겨 그 줄을 다시 쓴다(<see cref="SeqEdit.Change"/>). 라벨 줄을 다른 줄이 쓰면 팝업으로 막는다.
    /// 팝업에 버릴 인자와 "주석 확인"(옛 꼬리 주석)을 보인다. 주석은 지우지 않고 그 줄 주석 칸에 표시만 한다.
    /// </summary>
    public async Task ChangeStepAsync()
    {
        if (SelectedStep?.Piece is not Statement st || Zone is not { } z) return;
        if (SeqEdit.LabelUses(z, st) is { Count: > 0 } uses)
        {
            await Main.Show(new AskVm("바꿀 수 없음", [$"라벨 {SelectedStep.Label} · 쓰는 줄 {uses.Count}", .. uses.Select(At)], OkOnly));
            return;
        }
        var from = SeqEdit.EntryOf(st);
        string Preview(GrammarEntry to) => from == null ? Choices.DefaultText(to, Site.Names) : ActionForms.Convert(to, from, st.Args, Fill);
        IReadOnlyList<string> Drops(GrammarEntry to) => from == null ? [] : ActionForms.Dropped(to, from, st.Args);
        var d = new StepVm("명령 바꾸기", "바꾸기", z.Open == "ACT_BEGIN", from, Preview, Drops, Strip(st.TailComment));
        if (await Main.Show(d) != "ok" || d.Command.Entry == from) return;
        var r = SeqEdit.Change(Site, st, d.Command.Entry, Fill);
        if (r.Ok && st.TailComment.Length > 0 && r.Touched.FirstOrDefault() is { } changed) commentCheck.Add(changed.Text);
        Main.Apply(r);
    }

    /// <summary>[삭제]·Del: 고른 스텝. 라벨 줄을 다른 줄이 쓰면 팝업으로 막는다.</summary>
    public async Task DeleteStepAsync()
    {
        if (SelectedStep is not { } row || Zone is not { } z) return;
        if (row.Piece is Statement st && SeqEdit.LabelUses(z, st) is { Count: > 0 } uses)
        {
            await Main.Show(new AskVm("지울 수 없음", [$"라벨 {row.Label} · 쓰는 줄 {uses.Count}", .. uses.Select(At)], OkOnly));
            return;
        }
        // 짝 표지(분기 시작·끝)는 짝째, 끝 줄·원문 줄은 확인 팝업(기본 취소) 뒤에만
        List<Piece> pieces = [row.Piece];
        List<string>? ask = null;
        if (row.Piece is Statement { Name: "SELECT_BEGIN" or "SELECT_END" } sel && Partner(z, sel) is { } other)
        {
            pieces.Add(other);
            ask = [$"분기 시작·끝 짝 · {Math.Min(z.Ordinal(sel), z.Ordinal(other))}줄 · {Math.Max(z.Ordinal(sel), z.Ordinal(other))}줄"];
        }
        else if (row.Piece is Statement end && end.Name.StartsWith("END_", StringComparison.Ordinal) && ReferenceEquals(z.Statements[^1], end))
            ask = [$"끝 줄 · {row.Ordinal}줄 {end.Name}"];
        else if (row.Piece.Kind == PieceKind.Raw)
            ask = [$"원문 · {row.Piece.EndLine - row.Piece.StartLine + 1}줄", BlockBuilder.FirstLine(row.Piece.Text)];
        if (ask != null && await Main.Show(new AskVm("스텝 삭제", ask, [new AskButton("cancel", "취소", Default: true), new AskButton("delete", "삭제", Primary: true)])) != "delete") return;
        var next = Steps.SkipWhile(r => !ReferenceEquals(r, row)).Skip(1).FirstOrDefault(r => r.Ordinal > 0 && !pieces.Contains(r.Piece));
        int nextOrd = next?.Ordinal ?? 0;
        int gone = pieces.Count(p => p is Statement s2 && z.Ordinal(s2) < nextOrd);
        if (Main.Apply(SiteEdit.Delete(Site, pieces)) && nextOrd > 0)
            SelectStep(Steps.FirstOrDefault(r => r.Ordinal == nextOrd - gone), true);
    }

    /// <summary>분기 시작의 짝 끝(뒤로 첫 SELECT_END), 분기 끝의 짝 시작(앞으로 첫 SELECT_BEGIN).</summary>
    static Statement? Partner(ZoneBlock z, Statement st)
    {
        var list = z.Statements.ToList();
        int i = list.IndexOf(st);
        return st.Name == "SELECT_BEGIN"
            ? list.Skip(i + 1).FirstOrDefault(x => x.Name == "SELECT_END")
            : list.Take(i).LastOrDefault(x => x.Name == "SELECT_BEGIN");
    }

    /// <summary>줄의 구역 문장 자리: 문장 줄이면 그 앞(순번 - 1), 원문 줄이면 다음 문장 줄 앞(없으면 끝).</summary>
    int SlotOf(StepRowVm row, ZoneBlock z) =>
        row.Ordinal > 0 ? row.Ordinal - 1 : Steps.SkipWhile(r => !ReferenceEquals(r, row)).FirstOrDefault(r => r.Ordinal > 0) is { } n ? n.Ordinal - 1 : z.Statements.Count;

    /// <summary>스텝 끌어 옮기기: target 앞으로(null 이면 끝으로).</summary>
    public void MoveStep(StepRowVm from, StepRowVm? target)
    {
        if (Zone is not { } z || ReferenceEquals(from, target) || from.Piece is not Statement) return;
        int index = target == null ? z.Statements.Count : SlotOf(target, z);
        if (index == from.Ordinal - 1 || index == from.Ordinal) return; // 같은 자리: 되돌리기 단계를 쌓지 않는다
        Main.Apply(SiteEdit.Move(Site, [from.Piece], z, index));
    }

    /// <summary>[표 추가]: 새 표(<c>END_STOP</c> 한 줄) + 대응표 줄(<see cref="BundleEdit.AddSeqTable"/>).</summary>
    public async Task AddTableAsync()
    {
        var d = new SeqTableVm(Site);
        if (await Main.Show(d) != "ok") return;
        var name = d.Name.Trim();
        if (!Main.Apply(BundleEdit.AddSeqTable(Site, name, d.ProfileArg, d.Id))) return;
        Filter.Clear();
        ShowTree();
        SelectTable(name);
    }

    /// <summary>
    /// [표 삭제]: 그 표를 시작하는 태그 동작·스텝이 있으면 팝업으로 막는다. 없으면 확인 팝업(기본 취소) 뒤 표와 그 대응표 줄을 지운다.
    /// 글자로만 찾은 곳(원문 줄·함수 꼴 별칭 등)은 확인 팝업에 "확인 필요" 줄 수와 자리를 보인다.
    /// </summary>
    public async Task DeleteTableAsync()
    {
        if (Zone is not { } z) return;
        var refs = SeqEdit.TableUses(Site, z.Name);
        if (refs.Blocking.Count > 0)
        {
            await Main.Show(new AskVm("지울 수 없음", [$"표 {z.Name} · 시작하는 줄 {refs.Blocking.Count}", .. refs.Blocking.Select(At)], OkOnly));
            return;
        }
        List<string> lines = [$"표 {z.Name} · 스텝 {z.Statements.Count}", $"대응표 {refs.Maps.Count}줄"];
        if (refs.Maybe.Count > 0) lines = [.. lines, $"확인 필요 {refs.Maybe.Count}줄", .. refs.Maybe.Select(At)];
        var ask = new AskVm("표 삭제", lines, [new AskButton("cancel", "취소", Default: true), new AskButton("delete", "삭제", Primary: true)]);
        if (await Main.Show(ask) != "delete") return;
        if (Main.Apply(SeqEdit.DeleteTable(Site, z.Name)) && Tree.FirstOrDefault(t => t.IsTable)?.Table is { } first) SelectTable(first);
    }

    /// <summary>팝업 한 줄: 파일 n줄 · 첫 줄 글.</summary>
    string At(Piece p) => $"{Site.FileOf(p).Name} {p.StartLine}줄 · {BlockBuilder.FirstLine(p.Text)}";

    /// <summary>검사 줄에서: 그 조각이 든 표·스텝.</summary>
    public void SelectPiece(Piece p)
    {
        var zone = p is Statement st ? Site.ZoneOf(st) ?? Site.Zones.FirstOrDefault(z => ReferenceEquals(z.Begin, st) || ReferenceEquals(z.End, st)) : null;
        zone ??= Site.Zones.FirstOrDefault(z => z.Body.Contains(p));
        if (zone == null || zone.Open is not ("SEQ_BEGIN" or "ACT_BEGIN")) return;
        if (zone.Name != selectedTable) ShowTable(zone.Name);
        SelectStep(Steps.FirstOrDefault(r => ReferenceEquals(r.Piece, p)) ?? Steps.FirstOrDefault(), true);
    }
}
