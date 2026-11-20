using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.Engine.Check;

/// <summary>문장이 든 표. 범위 밖 값이 어느 규칙인지(P-1, T-9, S-8, M-2) 가른다.</summary>
public enum Area { Top, Profile, Tag, Seq, Act, Map, Manual }

/// <summary>
/// 인자 칸 하나의 쓰임. Loc = 결과를 붙일 인자(식의 한쪽이면 그 식의 인자), Node = 본 풀이 나무,
/// Spec = 문법 표의 칸, Parent = 바로 위 묶음·문장 이름, SigOut = ON·OFF 안 신호가 출력(수동 표 출력, WAIT_OUT),
/// InCond = 조건 안.
/// </summary>
public sealed record Use(SourceFile File, Statement St, Arg Loc, Node Node, ArgSpec Spec, Area Area, string Parent, bool SigOut, bool InCond);

/// <summary>칸 하나의 잘못. Kind: unknown(없는 이름), kind(종류 다름), high(핀 1 활성), range(범위 밖), count(인자 수), form(꼴), ionone(미연결), tagonly, seqonly.</summary>
public sealed record Problem(Use Use, string Kind, string Text, IReadOnlyList<string> Candidates);

/// <summary>
/// 검사 규칙이 같이 쓰는 사실: 모든 표 문장의 칸을 문법 표대로 한 번 훑은 쓰임 목록과 칸 잘못 목록.
/// 규칙 함수는 이것을 걸러 결과를 낸다(규칙 하나 = 함수 하나).
/// </summary>
public sealed class Facts
{
    public Site Site { get; }
    public NameDict Names => Site.Names;
    public IReadOnlyList<Use> Uses { get; }
    public IReadOnlyList<Problem> Problems { get; }

    /// <summary>문장마다 고른 문법 줄.</summary>
    public IReadOnlyDictionary<Statement, GrammarEntry> Rows { get; }

    /// <summary>안쪽 이름(조건·동작·상태 낱말 …) 풀이 나무마다 고른 문법 줄.</summary>
    public IReadOnlyDictionary<Node, GrammarEntry> Inners => inners;

    readonly Dictionary<Node, GrammarEntry> inners = new(ReferenceEqualityComparer.Instance);

    readonly Dictionary<Statement, Area> areas = new(ReferenceEqualityComparer.Instance);

    public Facts(Site site)
    {
        Site = site;
        var uses = new List<Use>();
        var rows = new Dictionary<Statement, GrammarEntry>(ReferenceEqualityComparer.Instance);
        foreach (var f in site.Files)
            foreach (var st in f.Statements.Where(s => s.Shape == StatementShape.Macro))
            {
                var area = AreaOf(st);
                areas[st] = area;
                var row = FitRow(StatementRows(st.Name, area), st.Args);
                if (row == null) continue;
                rows[st] = row;
                var specs = Expand(row, st.Args.Count)!;
                for (int i = 0; i < st.Args.Count; i++)
                    Walk(uses, f, st, st.Args[i], st.Args[i].Node, specs[i], area, st.Name, false, false);
            }
        Uses = uses;
        Rows = rows;
        Problems = uses.SelectMany(Classify).ToList();
    }

    public Area AreaOf(Statement st)
    {
        if (areas.TryGetValue(st, out var a)) return a;
        return st.Zone switch
        {
            "PROFILE_TABLE_BEGIN" => Area.Profile,
            "TAG_TABLE_BEGIN" => Area.Tag,
            "SEQ_BEGIN" => Area.Seq,
            "ACT_BEGIN" => Area.Act,
            "SEQ_MAP_BEGIN" or "ACT_MAP_BEGIN" => Area.Map,
            "MANUAL_BEGIN" => Area.Manual,
            _ => st.Name == "LOAD_IF" ? Area.Profile : Area.Top,
        };
    }

    // ---- 결과 만들기 ----

    public Finding Error(string rule, Piece p, string text, Arg? cell = null, IReadOnlyList<string>? cand = null, IReadOnlyList<int>? related = null) =>
        new(rule, Severity.Error, Site.FileOf(p).Name, p, cell, text, cand ?? [], related ?? []);

    public Finding Warn(string rule, Piece p, string text, Arg? cell = null, IReadOnlyList<int>? related = null) =>
        new(rule, Severity.Warning, Site.FileOf(p).Name, p, cell, text, [], related ?? []);

    /// <summary>죽은 줄 글: <c>무시됨 · 중복 (n줄)</c>, n = 가린 줄의 화면 줄 번호(구역 안 문장 순번).</summary>
    public static string Dead(IEnumerable<int> ordinals) => $"무시됨 · 중복 ({string.Join(", ", ordinals)}줄)";

    // ---- 문법 줄 고르기 ----

    static Place PlaceOf(Area a) => a switch
    {
        Area.Tag => Place.TagRule,
        Area.Seq or Area.Act => Place.Step,
        Area.Manual => Place.Manual,
        _ => Place.Frame,
    };

    static IEnumerable<GrammarEntry> StatementRows(string name, Area area) =>
        GrammarTable.Find(name).Where(r => r.Role == Role.Statement && r.Place == PlaceOf(area));

    /// <summary>인자 수가 맞고 고정 낱말 칸이 맞는 첫 줄. 낱말이 맞는 줄이 없으면 인자 수만 맞는 첫 줄.</summary>
    public GrammarEntry? FitRow(IEnumerable<GrammarEntry> rows, IReadOnlyList<Arg> args) => Fit(rows, args);

    /// <summary><see cref="FitRow"/> 의 정적 꼴(사이트 없이 쓴다: 화면 블록).</summary>
    public static GrammarEntry? Fit(IEnumerable<GrammarEntry> rows, IReadOnlyList<Arg> args)
    {
        GrammarEntry? first = null;
        foreach (var r in rows)
        {
            var specs = Expand(r, args.Count);
            if (specs == null) continue;
            first ??= r;
            bool words = true;
            for (int i = 0; i < args.Count && words; i++)
                if (specs[i].Kind == ArgKind.Word && !(args[i].Node is NameNode n && specs[i].Words!.Contains(n.Name))) words = false;
            for (int i = 0; i < args.Count && words; i++)
            {
                bool pt = args[i].Node is CallNode { Name: "PARAM_TIME" };
                if (specs[i].Kind == ArgKind.ParamTime ? !pt : specs[i].Kind is ArgKind.Timeout or ArgKind.Time && pt) words = false;
            }
            if (args.Count == 2 && specs[1].Kind == ArgKind.AcsStation && args[1].Node is not CallNode { Name: "STATION" }) words = false; // 칸 형식은 STATION( k ) 만
            if (r.Name == "AGV_STOP" && args.Count == 1 && specs[0].Kind == ArgKind.Number && args[0].Node is NameNode { Name: "TAPE" or "TAPE_GD" }) words = false;
            if (r.Name == "SEQ_START" && args.Count == 2 && specs[0].Kind == ArgKind.Number && args[0].Node is NameNode { Name: "TAPE" or "TAPE_GD" }) words = false;
            if (words) return r;
        }
        return first;
    }

    /// <summary>인자 n 개에 칸을 하나씩 준다(생략·여러 개는 앞에서부터 채운다). 수가 안 맞으면 null.</summary>
    public static IReadOnlyList<ArgSpec>? Expand(GrammarEntry row, int n)
    {
        if (n < row.MinArgs || n > row.MaxArgs) return null;
        int extra = n - row.MinArgs;
        var list = new List<ArgSpec>(n);
        foreach (var a in row.Args)
        {
            int k = a.Least + Math.Min(a.Most - a.Least, extra);
            extra -= k - a.Least;
            for (int i = 0; i < k; i++) list.Add(a);
        }
        return list;
    }

    // ---- 안쪽 묶음 ----

    static HashSet<string> NamesAt(Place p) => GrammarTable.Rows.Where(r => r.Place == p).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

    static readonly HashSet<string> TagActionNames = NamesAt(Place.TagAction).Except(["FROM_SENSOR", "AGV_ID_MINUS", "TAG_NUM"]).ToHashSet();
    static readonly HashSet<string> StepOnly = NamesAt(Place.Step).Except(NamesAt(Place.TagAction)).ToHashSet();
    public static readonly HashSet<string> TagOnly = NamesAt(Place.TagAction).Union(NamesAt(Place.TagCond)).Union(["X_TAG_NUM", "TAG_IS"])
        .Except(NamesAt(Place.Step)).ToHashSet();

    /// <summary>AND·NOT 안에 오는 상태 낱말(v0.58.0).</summary>
    public static readonly IReadOnlySet<string> StateWords = NamesAt(Place.StateCond);

    static readonly HashSet<string> TagConds = [.. NamesAt(Place.TagCond), "AND"];

    static readonly HashSet<string> AndItems = ["ON", "OFF", "ANY", "ANY_ON", "NO_IN", "IN", .. NamesAt(Place.StateCond)];

    /// <summary>칸 종류마다 올 수 있는 안쪽 이름.</summary>
    public static IReadOnlySet<string> Allowed(ArgKind kind, Area area) => kind switch
    {
        ArgKind.InCond or ArgKind.OutCond => (HashSet<string>)["IN", "ON", "OFF", "ANY_ON", "NO_IN"],
        ArgKind.AndCond => (HashSet<string>)["IN", "ON", "OFF", "ANY_ON", "NO_IN", "AND"],
        ArgKind.AndItem => AndItems,
        ArgKind.StateCond => StateWords.ToHashSet(),
        ArgKind.ParamTime => (HashSet<string>)["PARAM_TIME"],
        ArgKind.OutMask => (HashSet<string>)["OUTS"],
        ArgKind.InGroup => (HashSet<string>)["ON", "OFF", "ANY_ON"],
        ArgKind.Until => (HashSet<string>)["UNTIL"],
        ArgKind.Any2 => (HashSet<string>)["ANY2_ON"],
        ArgKind.LoadSensors => (HashSet<string>)["LOAD_SENSORS"],
        ArgKind.XCond => NamesAt(Place.ExtCond),
        ArgKind.TagCond => TagConds,
        ArgKind.Action => TagActionNames,
        ArgKind.ManCond => (HashSet<string>)["IN", "KEY", "NO_IN", "AND"],
        ArgKind.ManOut => (HashSet<string>)["ON", "OFF", "BELT", "NO_OUT"],
        ArgKind.StationExpr => (HashSet<string>)["STATION", "MOD"],
        ArgKind.AcsSource or ArgKind.AcsStation => (HashSet<string>)["STATION"],
        ArgKind.AcsNumber => area == Area.Tag ? (HashSet<string>)["STATION", "AGV_ID_MINUS", "TAG_NUM"] : (HashSet<string>)["STATION"],
        ArgKind.FromSensor => (HashSet<string>)["FROM_SENSOR"],
        ArgKind.Timeout => (HashSet<string>)["UNMATCHED"],
        _ => (HashSet<string>)[],
    };

    static bool IsCallKind(ArgKind k) => k is ArgKind.InCond or ArgKind.OutCond or ArgKind.InGroup or ArgKind.Until or ArgKind.Any2
        or ArgKind.LoadSensors or ArgKind.XCond or ArgKind.TagCond or ArgKind.Action or ArgKind.ManCond or ArgKind.ManOut
        or ArgKind.FromSensor or ArgKind.AndCond or ArgKind.AndItem or ArgKind.StateCond or ArgKind.ParamTime or ArgKind.OutMask;

    /// <summary>안쪽 이름의 문법 줄(칸 종류·자리에 맞고 인자 수가 맞는 것). 없으면 null.</summary>
    GrammarEntry? Inner(string name, IReadOnlyList<Arg> args, ArgKind kind, Area area)
    {
        if (!Allowed(kind, area).Contains(name)) return null;
        var rows = GrammarTable.Find(name).Where(r => r.Role != Role.Statement && r.Place != Place.Packed && PlaceOk(kind, r.Place));
        return FitRow(rows, args);
    }

    /// <summary>같은 이름이 여러 자리에 있는 것(<c>STATION( k )</c> 스텝 식과 <c>STATION( k, lo, hi )</c> 상태 낱말)을 칸 종류로 가른다.</summary>
    static bool PlaceOk(ArgKind kind, Place p) => kind switch
    {
        ArgKind.Action => p == Place.TagAction,
        ArgKind.AndItem => p is Place.InputCond or Place.StateCond,
        ArgKind.StateCond => p == Place.StateCond,
        ArgKind.StationExpr or ArgKind.AcsSource => p == Place.Step,
        ArgKind.AcsNumber or ArgKind.AcsStation => p is Place.Step or Place.TagAction,
        _ => true,
    };

    static bool Conditional(ArgKind k) => k is ArgKind.InCond or ArgKind.OutCond or ArgKind.Until or ArgKind.Any2 or ArgKind.LoadSensors or ArgKind.ManCond
        or ArgKind.AndCond or ArgKind.AndItem;

    void Walk(List<Use> uses, SourceFile f, Statement st, Arg loc, Node node, ArgSpec spec, Area area, string parent, bool sigOut, bool inCond)
    {
        sigOut |= spec.Kind is ArgKind.OutCond or ArgKind.ManOut;
        inCond |= Conditional(spec.Kind);
        uses.Add(new Use(f, st, loc, node, spec, area, parent, sigOut, inCond));

        if (node is BinaryNode b && spec.Kind is ArgKind.StationExpr or ArgKind.AcsNumber or ArgKind.AcsSource)
        {
            // STATION(k) ± n, n + MOD(…), TAG_NUM ± n: 식 쪽은 같은 칸, 숫자 쪽은 n 범위
            var (expr, n) = b.Left is NumberNode ? (b.Right, b.Left) : (b.Left, b.Right);
            Walk(uses, f, st, loc, expr, spec, area, parent, sigOut, inCond);
            var nSpec = spec.Kind == ArgKind.StationExpr ? new ArgSpec(ArgKind.Number, -2048, 2047) : new ArgSpec(ArgKind.Number, -128, 127);
            Walk(uses, f, st, loc, n, nSpec, area, parent + " ±", sigOut, inCond);
            return;
        }

        string? name = node is CallNode c0 ? c0.Name : node is NameNode n0 ? n0.Name : null;
        var args = node is CallNode c ? c.Args : [];
        if (name == null) return;
        bool callish = IsCallKind(spec.Kind) || spec.Kind is ArgKind.StationExpr or ArgKind.AcsSource or ArgKind.AcsNumber or ArgKind.AcsStation or ArgKind.Timeout;
        if (!callish) return;
        var entry = Inner(name, args, spec.Kind, area);
        if (entry == null) return;
        inners[node] = entry;
        var specs = Expand(entry, args.Count)!;
        for (int i = 0; i < args.Count; i++)
            Walk(uses, f, st, args[i], args[i].Node, specs[i], area, entry.Name, sigOut, inCond);
    }

    // ---- 칸 잘못 가르기 ----

    static readonly HashSet<string> Groups = ["ON", "OFF", "ANY_ON", "ANY2_ON", "IN", "LOAD_SENSORS", "OUTS"];

    IEnumerable<string> Pool(params NameKind[] kinds) => Names.All.Where(d => kinds.Contains(d.Kind)).Select(d => d.Name);

    /// <summary>접두만 다른 짝: <c>X_FORWARD</c>↔<c>IF_FORWARD</c>. 짝이 문법 표에 없으면 null. B절 코어 형식이 들어오면 이 쌍 표를 맞춘다.</summary>
    static string? Swapped(string name)
    {
        var other = name.StartsWith("X_", StringComparison.Ordinal) ? "IF_" + name[2..]
            : name.StartsWith("IF_", StringComparison.Ordinal) ? "X_" + name[3..] : null;
        return other != null && GrammarTable.Find(other).Any() ? other : null;
    }

    /// <summary><c>IN( … )</c> 묶음 개수 한계: v0.58.0 부터 7·8 인자도 받는다.</summary>
    int InLimit => Names.NewDsl ? 8 : 6;

    /// <summary><c>STATION(0) + 1</c>(또는 <c>1 + STATION(0)</c>): 시퀀스 <c>ACS</c> 번호 칸이 받는 합.</summary>
    static bool Station0Plus1(Node n) => n is BinaryNode { Op: '+' } b
        && (b.Left is CallNode { Name: "STATION", Args: [{ Node: NumberNode { Value: 0 } }] } && b.Right is NumberNode { Value: 1 }
            || b.Right is CallNode { Name: "STATION", Args: [{ Node: NumberNode { Value: 0 } }] } && b.Left is NumberNode { Value: 1 });

    IEnumerable<Problem> Classify(Use u)
    {
        var spec = u.Spec;
        Problem P(string kind, string text, IReadOnlyList<string>? cand = null) => new(u, kind, text, cand ?? []);
        var node = u.Node;
        var nameNode = node as NameNode;
        var def = nameNode != null ? Names.Find(nameNode.Name, u.File) : null;

        switch (spec.Kind)
        {
            case ArgKind.Ident:
            case ArgKind.Label:
            case ArgKind.SeqTable:
            case ArgKind.ActTable:
                yield break; // N-5, S-1, S-12 가 본다

            case ArgKind.Profile:
                if (nameNode != null)
                {
                    if (def == null) { yield return P("unknown", $"없는 이름 {nameNode.Name}", NameDict.Near(nameNode.Name, Pool(NameKind.Profile))); yield break; }
                    if (def.Kind != NameKind.Profile) { yield return P("kind", $"종류 다름 {nameNode.Name} · 프로파일 자리"); yield break; }
                }
                else if (node is not NumberNode) { yield return P("form", $"형식 {u.Loc.Text}"); yield break; }
                var pv = Names.Value(node, u.File);
                if (pv is long v && (v < spec.Min || v > spec.Max)) yield return P("range", $"프로파일 {v} · {spec.Min}~{spec.Max}");
                yield break;

            case ArgKind.Input or ArgKind.CoreInput or ArgKind.Signal or ArgKind.Output:
            {
                bool wantOut = spec.Kind == ArgKind.Output || spec.Kind == ArgKind.Signal && u.SigOut;
                string slot = wantOut ? "출력 자리" : "입력 자리";
                long? pin;
                if (nameNode != null)
                {
                    if (def == null)
                    {
                        var pool = wantOut ? Pool(NameKind.Output) : Pool(NameKind.Input, NameKind.InputHigh, NameKind.IoNone);
                        yield return P("unknown", $"없는 이름 {nameNode.Name}", NameDict.Near(nameNode.Name, pool));
                        yield break;
                    }
                    if (spec.Kind == ArgKind.CoreInput && !(def.Core && def.Kind == NameKind.Input)) { yield return P("kind", $"종류 다름 {nameNode.Name} · 코어 IN_*"); yield break; }
                    switch (def.Kind)
                    {
                        case NameKind.IoNone:
                            if (u.Parent == "FROM_SENSOR") yield return P("range", $"신호 ID 밖 IO_NONE · 0~63");
                            else if (!wantOut && u.InCond) yield return P("ionone", $"미연결 신호 {nameNode.Name}");
                            yield break;
                        case NameKind.InputHigh:
                            if (wantOut || spec.Kind != ArgKind.Signal) { yield return P("high", $"반대 극성 입력 {nameNode.Name} · ON·OFF 밖"); yield break; }
                            break;
                        case NameKind.Input:
                            if (wantOut) { yield return P("kind", $"종류 다름 {nameNode.Name} · {slot}"); yield break; }
                            break;
                        case NameKind.Output:
                            if (!wantOut) { yield return P("kind", $"종류 다름 {nameNode.Name} · {slot}"); yield break; }
                            break;
                        default:
                            yield return P("kind", $"종류 다름 {nameNode.Name} · {slot}");
                            yield break;
                    }
                    pin = def.Value;
                }
                else if (node is NumberNode num) pin = num.Value;
                else { yield return P("form", $"형식 {u.Loc.Text}"); yield break; }
                long lo = spec.Max == 0 ? 0 : spec.Min, hi = spec.Max == 0 ? 31 : spec.Max;
                if (pin is long p && (p < lo || p > hi)) yield return P("range", $"신호 ID 밖 {p} · {lo}~{hi}");
                yield break;
            }

            case ArgKind.Belt:
            {
                var key = node is NumberNode bn ? bn.Literal : nameNode?.Name;
                if (key == null) { yield return P("form", $"형식 {u.Loc.Text}"); yield break; }
                if (!Names.Belts.ContainsKey(key)) yield return P("unknown", $"없는 벨트 {key}", NameDict.Near(key, Names.Belts.Keys));
                yield break;
            }

            case ArgKind.SeqShort:
                if (nameNode == null) { yield return P("form", $"형식 {u.Loc.Text}"); yield break; }
                if (Names.Find("SEQ_" + nameNode.Name) is not { Kind: NameKind.SeqId })
                    yield return P("unknown", $"없는 시퀀스 {nameNode.Name}",
                        NameDict.Near(nameNode.Name, Pool(NameKind.SeqId).Where(x => x.StartsWith("SEQ_", StringComparison.Ordinal)).Select(x => x[4..])));
                yield break;

            case ArgKind.LoadIf or ArgKind.SeqId or ArgKind.ActId or ArgKind.State or ArgKind.CallNo or ArgKind.CoreParam or ArgKind.FndParam:
            {
                var want = spec.Kind switch
                {
                    ArgKind.LoadIf => NameKind.LoadIf,
                    ArgKind.SeqId => NameKind.SeqId,
                    ArgKind.ActId => NameKind.ActId,
                    ArgKind.State => NameKind.State,
                    ArgKind.CallNo => NameKind.CallNo,
                    ArgKind.CoreParam => NameKind.CoreParam,
                    _ => NameKind.FndParam,
                };
                bool numberOk = spec.Kind is ArgKind.ActId or ArgKind.State or ArgKind.CallNo;
                if (nameNode != null)
                {
                    if (def == null) { yield return P("unknown", $"없는 이름 {nameNode.Name}", NameDict.Near(nameNode.Name, Pool(want))); yield break; }
                    if (def.Kind != want && !(numberOk && def.Kind is NameKind.Alias or NameKind.Constant)) { yield return P("kind", $"종류 다름 {nameNode.Name}"); yield break; }
                }
                else if (!(numberOk && node is NumberNode)) { yield return P("form", $"형식 {u.Loc.Text}"); yield break; }
                var val = Names.Value(node, u.File);
                long max = spec.Kind == ArgKind.State ? 15 : spec.Max;
                if (numberOk && max > 0 && val is long sv && (sv < 0 || sv > max)) yield return P("range", $"범위 밖 {sv} · 0~{max}");
                yield break;
            }

            case ArgKind.Number or ArgKind.Time or ArgKind.Timeout or ArgKind.StopDist:
            {
                if (spec.Kind == ArgKind.StopDist && nameNode?.Name == "FL") yield break;
                if (spec.Kind == ArgKind.Timeout && node is CallNode tc)
                {
                    if (tc.Name != "UNMATCHED" || Inner(tc.Name, tc.Args, spec.Kind, u.Area) == null) yield return P("form", $"형식 {u.Loc.Text}");
                    yield break;
                }
                if (nameNode != null && def == null)
                {
                    var pool = Names.All.Where(d => d.Value != null && d.Kind is NameKind.Alias or NameKind.Constant).Select(d => d.Name);
                    yield return P("unknown", $"없는 이름 {nameNode.Name}", NameDict.Near(nameNode.Name, pool));
                    yield break;
                }
                if (node is CallNode) { yield return P("form", $"형식 {u.Loc.Text}"); yield break; }
                var val = Names.Value(node, u.File);
                if (val == null) { if (nameNode != null) yield return P("kind", $"종류 다름 {nameNode.Name} · 숫자 자리"); yield break; }
                long x = val.Value;
                if (x < spec.Min || x > spec.Max) yield return P("range", $"범위 밖 {x} · {spec.Min}~{spec.Max}");
                else if (spec.Unit == "도" && u.Area == Area.Tag && x % 5 != 0) yield return P("range", $"범위 밖 {x} · 5의 배수");
                yield break;
            }

            case ArgKind.Word:
                if (nameNode != null && spec.Words!.Contains(nameNode.Name)) yield break;
                if (u.Parent == "AGV_TURN" && nameNode?.Name == "NOWAIT" && u.Area == Area.Tag) { yield return P("seqonly", "태그 표 금지 NOWAIT"); yield break; }
                if (u.Parent == "AGV_TURN" && nameNode?.Name == "TAPE" && u.Area != Area.Tag) { yield return P("tagonly", "시퀀스 표 금지 TAPE"); yield break; }
                yield return P("unknown", $"없는 낱말 {u.Loc.Text}", NameDict.Near(nameNode?.Name ?? u.Loc.Text, spec.Words!));
                yield break;

            case ArgKind.AcsSource when node is NumberNode or NameNode:
            {
                // 번호 칸(v0.58.0): 숫자·낱말 STATION0·STATION0_PLUS1·AGV_ID. CONST 는 새 코어에서 오류(D-5)
                if (nameNode?.Name is "CONST" or "STATION0" or "STATION0_PLUS1" or "AGV_ID") yield break;
                var sv = Names.Value(node, u.File);
                if (sv == null)
                {
                    string[] words = ["STATION0", "STATION0_PLUS1", "AGV_ID"];
                    yield return def == null ? P("unknown", $"없는 낱말 {u.Loc.Text}", NameDict.Near(nameNode?.Name ?? u.Loc.Text, words)) : P("kind", $"종류 다름 {u.Loc.Text} · 번호 자리");
                }
                else if (sv < spec.Min || sv > spec.Max) yield return P("range", $"범위 밖 {sv} · {spec.Min}~{spec.Max}");
                yield break;
            }

            case ArgKind.ParamAny:
                if (nameNode == null) { yield return P("form", $"형식 {u.Loc.Text}"); yield break; }
                if (def == null) yield return P("unknown", $"없는 이름 {nameNode.Name}", NameDict.Near(nameNode.Name, Pool(NameKind.FndParam, NameKind.CoreParam)));
                else if (def.Kind is not (NameKind.FndParam or NameKind.CoreParam)) yield return P("kind", $"종류 다름 {nameNode.Name} · 파라미터");
                yield break;

            case ArgKind.OutMask when nameNode != null:
                if (nameNode.Name != "NO_OUT" && def is null or { Kind: not (NameKind.Constant or NameKind.Alias) })
                    yield return def == null ? P("unknown", $"없는 이름 {nameNode.Name}", ["NO_OUT"]) : P("kind", $"종류 다름 {nameNode.Name} · 출력 묶음");
                yield break;

            case ArgKind.AndItem or ArgKind.StateCond when node is NumberNode:
                yield return P("form", $"형식 {u.Loc.Text} · 항목");
                yield break;

            case ArgKind.AcsNumber when nameNode?.Name == "AGV_ID" && u.Parent == "ACS" && u.Area == Area.Tag:
                yield break; // 태그 ACS( 종류, AGV_ID ) = AGV_ID_MINUS( 0 )

            case ArgKind.AcsNumber or ArgKind.StationExpr when node is NumberNode or NameNode { Name: not "TAG_NUM" }:
                if (spec.Kind == ArgKind.StationExpr) { yield return P("form", $"형식 {u.Loc.Text} · STATION 식"); yield break; }
                var av = Names.Value(node, u.File);
                if (av == null) { yield return P(nameNode != null && def == null ? "unknown" : "kind", $"없는 이름 {u.Loc.Text}"); yield break; }
                if (av < spec.Min || av > spec.Max) yield return P("range", $"범위 밖 {av} · {spec.Min}~{spec.Max}");
                yield break;

            case ArgKind.AcsNumber or ArgKind.AcsSource when node is BinaryNode && u.Area != Area.Tag:
                if (!(Names.NewDsl && Station0Plus1(node))) yield return P("tagonly", Names.NewDsl ? "시퀀스 표 금지 STATION ± n · STATION(0) + 1 만" : "시퀀스 표 금지 STATION ± n");
                yield break;
        }

        // 안쪽 묶음 칸
        if (node is BinaryNode || node is NumberNode && spec.Kind == ArgKind.Action && Names.Value(node) == 0) yield break;
        string? nm = node is CallNode cn ? cn.Name : nameNode?.Name;
        if (nm == null) yield break;
        var args = node is CallNode cc ? cc.Args : [];
        if (spec.Kind == ArgKind.Action && nameNode != null && def is { Kind: NameKind.Alias, Value: 0 }) yield break; // NO_ACTION
        if (spec.Kind == ArgKind.Action && def is { Kind: NameKind.Macro }) yield break; // shortcut: 함수 꼴 별칭(CALL_T 등)은 뜻을 모른다. 펼쳐 보려면 원문을 풀어야 한다
        var allowed = Allowed(spec.Kind, u.Area);
        if (allowed.Count == 0) yield break;
        if (nm == "IN" && allowed.Contains("IN") && args.Count > InLimit) { yield return P("groupcount", $"묶음 {args.Count}개 · {InLimit} 이하"); yield break; }
        if (Swapped(nm) is { } sw && allowed.Contains(sw) && !allowed.Contains(nm)) { yield return P("swap", $"자리 다름 {nm}", [sw]); yield break; }
        if (u.Area is Area.Seq or Area.Act && TagOnly.Contains(nm)) { yield return P("tagonly", $"시퀀스 표 금지 {nm}"); yield break; }
        if (Inner(nm, args, spec.Kind, u.Area) != null)
        {
            if (spec.Kind == ArgKind.InCond && u.St.Name == "LOAD_IF" && nm == "NO_IN") yield return P("form", "LOAD_IF 빈 조건 NO_IN");
            yield break;
        }
        if (allowed.Contains(nm))
        {
            yield return Groups.Contains(nm) && args.Count > (nm == "IN" ? InLimit : 16)
                ? P("groupcount", nm == "IN" ? $"묶음 {args.Count}개 · {InLimit} 이하" : $"신호 {args.Count}개 · 16 이하")
                : P("count", $"인자 수 {nm} {args.Count}");
            yield break;
        }
        if (spec.Kind == ArgKind.Action && StepOnly.Contains(nm)) { yield return P("seqonly", $"태그 표 금지 {nm}"); yield break; }
        if (spec.Kind == ArgKind.ManCond && nm is "ON" or "OFF" or "ANY_ON") { yield return P("mancond", $"조건 형식 {nm} · IN( … )"); yield break; }
        if (GrammarTable.Find(nm).Any() || Names.Find(nm, u.File) != null)
        {
            yield return P("kind", $"종류 다름 {nm}");
            yield break;
        }
        yield return P("unknown", $"없는 이름 {nm}", NameDict.Near(nm, allowed));
    }
}
