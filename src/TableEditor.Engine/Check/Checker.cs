using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.Engine.Check;

/// <summary>
/// 사이트 전체 검사(사양 6절). 규칙 하나 = 함수 하나, 번호는 사양 그대로.
/// 넣지 않은 규칙: R-3(저장 때), N-6(이름 바꾸기·지우기 때), C-1·C-1b·C-1c(빌드 단계에서 압축 표를 다시 만든다).
/// D-1~D-7 은 코어 v0.58.0 이상 사이트에만 건다(새 DSL 형식의 컴파일 오류를 저장 전에 잡는다). V-4 는 거꾸로 옛 코어 사이트가 새 문법을 쓴 줄을 잡는다.
/// </summary>
public static partial class Checker
{
    public static readonly IReadOnlyList<(string Rule, Func<Facts, IEnumerable<Finding>> Run)> Rules =
    [
        ("V-1", V1), ("V-2", V2), ("V-3", V3), ("V-4", V4), ("R-1", R1), ("R-2", R2),
        ("N-1", N1), ("N-2", N2), ("N-3", N3), ("N-4", N4), ("N-5", N5), ("N-7", N7),
        ("P-1", P1), ("P-2", P2), ("P-3", P3), ("P-4", P4), ("P-5", P5), ("P-6", P6),
        ("I-1", I1), ("I-2", I2), ("I-3", I3), ("I-4", I4), ("I-5", I5), ("I-6", I6), ("I-7", I7),
        ("T-1", T1), ("T-2", T2), ("T-3", T3), ("T-4", T4), ("T-5", T5), ("T-6", T6), ("T-7", T7), ("T-8", T8),
        ("T-9", T9), ("T-10", T10), ("T-11", T11), ("T-12", T12), ("T-13", T13), ("T-14", T14), ("T-15", T15),
        ("T-16", T16), ("T-17", T17),
        ("S-1", S1), ("S-2", S2), ("S-3", S3), ("S-4", S4), ("S-5", S5), ("S-6", S6), ("S-7", S7), ("S-8", S8),
        ("S-9", S9), ("S-10", S10), ("S-11", S11), ("S-12", S12), ("S-13", S13), ("S-14", S14), ("S-15", S15),
        ("S-16", S16), ("S-16b", S16b), ("S-17", S17), ("S-18", S18), ("S-19", S19),
        ("M-1", M1), ("M-2", M2), ("M-3", M3), ("M-4", M4), ("M-5", M5), ("M-6", M6), ("M-7", M7), ("M-8", M8), ("M-9", M9), ("M-10", M10),
        ("C-2", C2), ("C-3", C3),
        ("D-1", D1), ("D-2", D2), ("D-3", D3), ("D-4", D4), ("D-5", D5), ("D-6", D6), ("D-7", D7),
    ];

    public static IReadOnlyList<Finding> Run(Site site)
    {
        var facts = new Facts(site);
        var order = site.Files.Select(f => f.Name).ToList();
        return Rules.SelectMany(r => r.Run(facts))
            .OrderBy(f => order.IndexOf(f.File) is var i && i < 0 ? -1 : i)
            .ThenBy(f => f.Line)
            .ToList();
    }

    // ---- 같이 쓰는 것 ----

    static bool IsPin(ArgKind k) => k is ArgKind.Input or ArgKind.CoreInput or ArgKind.Signal or ArgKind.Output;

    static IEnumerable<Finding> FromProblems(Facts f, string rule, Func<Problem, bool> pick, bool warn = false) =>
        f.Problems.Where(pick).Select(p => warn ? f.Warn(rule, p.Use.St, p.Text, p.Use.Loc) : f.Error(rule, p.Use.St, p.Text, p.Use.Loc, p.Candidates));

    static IEnumerable<Finding> Ranges(Facts f, string rule, Func<Use, bool> pick) =>
        FromProblems(f, rule, p => p.Kind == "range" && !IsPin(p.Use.Spec.Kind) && pick(p.Use));

    /// <summary>문장 안의 모든 안쪽 묶음(자리 인자와 함께).</summary>
    static IEnumerable<(Arg Loc, CallNode Call)> Calls(Statement st)
    {
        IEnumerable<(Arg, CallNode)> Of(Arg loc, Node n) => n switch
        {
            CallNode c => c.Args.SelectMany(a => Of(a, a.Node)).Prepend((loc, c)),
            BinaryNode b => Of(loc, b.Left).Concat(Of(loc, b.Right)),
            _ => [],
        };
        return st.Args.SelectMany(a => Of(a, a.Node));
    }

    static string NameOf(Node n) => n switch { CallNode c => c.Name, NameNode m => m.Name, _ => "" };

    static IReadOnlyList<Arg> ArgsOf(Node n) => n is CallNode c ? c.Args : [];

    /// <summary>처음 ≤ 끝 짝: X_ID·IF_ID·WHEN_ID·TAG_RANGE·PROFILE·STATE( 칸, 처음, 끝 ).</summary>
    static IEnumerable<Finding> Pairs(Facts f, string rule, Func<Area, bool> area)
    {
        foreach (var file in f.Site.Files)
            foreach (var st in file.Statements.Where(s => s.Shape == StatementShape.Macro && area(f.AreaOf(s))))
            {
                if (st.Name is "WHEN_ID" or "TAG_RANGE" or "TAG_RANGE_IF" && st.Args.Count >= 2 && Bad(st.Args[0], st.Args[1]))
                    yield return f.Error(rule, st, $"처음 > 끝 {st.Args[0].Text} {st.Args[1].Text}", st.Args[0]);
                if (st.Name.StartsWith("PROFILE", StringComparison.Ordinal) && st.Args.Count >= 4 && Bad(st.Args[2], st.Args[3]))
                    yield return f.Error(rule, st, $"처음 > 끝 {st.Args[2].Text} {st.Args[3].Text}", st.Args[2]);
                foreach (var (loc, c) in Calls(st))
                    if (c.Name is "X_ID" or "IF_ID" or "ID" && c.Args.Count == 2 && Bad(c.Args[0], c.Args[1]))
                        yield return f.Error(rule, st, $"처음 > 끝 {c.Name}( {c.Args[0].Text}, {c.Args[1].Text} )", loc);
                    else if (c.Name == "STATE" && c.Args.Count == 3 && Bad(c.Args[1], c.Args[2]))
                        yield return f.Error(rule, st, $"처음 > 끝 STATE( {c.Args[0].Text}, {c.Args[1].Text}, {c.Args[2].Text} )", loc);
            }

        bool Bad(Arg a, Arg b) => f.Names.Value(a.Node) is long x && f.Names.Value(b.Node) is long y && x > y;
    }

    /// <summary>CALL 함수 수 = CALL_SEQ_COUNT + TAGC_COUNT. 모르면 null.</summary>
    static long? CallCount(Facts f) =>
        f.Names.Find("CALL_SEQ_COUNT")?.Value is long s ? s + (f.Names.Find("TAGC_COUNT")?.Value ?? 0) : null;

    static IEnumerable<(Statement St, Arg Loc, Node Node, Area Area)> UsesOf(Facts f, params string[] names) =>
        f.Uses.Where(u => names.Contains(NameOf(u.Node)) && u.Spec.Kind is ArgKind.Action)
            .Select(u => (u.St, u.Loc, u.Node, u.Area))
            .Concat(f.Site.Files.SelectMany(x => x.Statements)
                .Where(s => names.Contains(s.Name) && f.AreaOf(s) is Area.Seq or Area.Act)
                .Select(s => (s, s.Args.Count > 0 ? s.Args[0] : null!, (Node)new CallNode(s.Name, "", s.Args), f.AreaOf(s))));

    static int Compare(string a, string b) => GrammarTable.Compare(a, b);

    static Finding CoreFinding(string rule, Severity sev, string file, string text) => new(rule, sev, "core/" + file, null, null, text, [], []);

    static string Squash(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

    static long? Val(Facts f, Arg a) => f.Names.Value(a.Node);
}
