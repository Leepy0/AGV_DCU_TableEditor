using System.Windows.Input;
using TableEditor.App.Blocks;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.App.ViewModels;

/// <summary>나무 한 줄. Level 0 = 프로파일(또는 연결 없음), 1 = 시퀀스 ID, 2 = 표. Table = 표 줄이면 그 이름.</summary>
public sealed class TreeItemVm(int level, string text, string? table, string? note, bool err) : Observable
{
    public int Level { get; } = level;
    public string Text { get; } = text;
    public string? Table { get; } = table;
    public string? Note { get; } = note;
    public bool IsTable => Table != null;
    public override string ToString() => Text;
    public bool HasError { get; } = err;
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
    public string? Status { get; init; }
    public Severity? StatusSeverity { get; init; }
    public bool IsRaw => Block.Raw;
    public override string ToString() => $"{OrdinalText}줄 {Label ?? Block.ToString()}";
}

/// <summary>
/// 시퀀스 화면(사양 9.2절, 1차): 나무(프로파일 → 시퀀스 ID → 표) + 스텝 블록 세로 목록. 숫자·시간 칸만 고친다(선택 줄 칸).
/// 이름·낱말·라벨 칸은 읽기 전용, 파라미터는 FND 이름, 시간은 틱 옆에 초.
/// </summary>
public sealed class SeqVm : BlockHost
{
    public SeqVm(MainVm main) : base(main) { }

    protected override Piece? CodePiece => SelectedStep?.Piece;

    /// <summary>숫자 칸 중 숫자 글자만 고친다. 이름·낱말·라벨·식은 읽기 전용.</summary>
    protected override bool Locks(FieldInfo f) => Choices.EditorOf(f.Spec) != FieldEditor.Number || f.Arg.Node is not NumberNode;

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
            tree.Add(new TreeItemVm(0, pg.Key, null, null, false));
            foreach (var ig in pg.GroupBy(m => BlockBuilder.Collapse(m.Id)))
            {
                tree.Add(new TreeItemVm(1, ig.Key, null, null, false));
                foreach (var m in ig) tree.Add(new TreeItemVm(2, m.Table, m.Table, Count(s, m.Table), bad.Contains(m.Table)));
            }
        }
        var linked = maps.Select(m => m.Table).ToHashSet();
        var loose = s.Seqs.Where(t => !linked.Contains(t.Name)).ToList();
        if (loose.Count > 0)
        {
            tree.Add(new TreeItemVm(0, "연결 없음", null, null, false));
            foreach (var t in loose) tree.Add(new TreeItemVm(1, t.Name, t.Name, t.Steps.Count.ToString(), bad.Contains(t.Name)));
        }
        Tree = tree;
        Raise(nameof(Tree));
        Raise(nameof(IsEmpty));

        string? table = touched is { Count: > 0 } && touched[0] is Statement ts && s.ZoneOf(ts) is { Open: "SEQ_BEGIN" or "ACT_BEGIN" } tz ? tz.Name : prevTable;
        if (table != null && !s.Seqs.Any(t => t.Name == table)) table = null; // 지운 표·다른 표의 편집
        table ??= tree.FirstOrDefault(t => t.IsTable)?.Table;
        selectedTable = null;
        ShowTable(table);
        var pick = touched is { Count: > 0 } ? Steps.FirstOrDefault(r => touched.Any(t => ReferenceEquals(t, r.Piece))) : null;
        bool same = pick == null;
        pick ??= Steps.FirstOrDefault(r => r.Ordinal == prevStep && prevStep > 0);
        SelectStep(pick, scroll: touched is { Count: > 0 });
        if (same && pick != null) Restore([pick.Block], 0, prevField);
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
