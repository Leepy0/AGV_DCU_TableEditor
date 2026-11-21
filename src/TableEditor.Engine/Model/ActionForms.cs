using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Text;

namespace TableEditor.Engine.Model;

/// <summary>
/// 꼴이 여럿인 태그 동작(<c>AGV_STOP</c> 다섯, <c>SEQ_START</c> 넷). 문법 표는 꼴마다 줄이 따로라서
/// 칸 편집기는 고른 꼴의 칸만 안다. 그래서 TAPE·TAPE_GD·cm·바로 사이를 오가려면 꼴을 바꿔 인자를 통째로 다시 쓴다.
/// </summary>
public static class ActionForms
{
    /// <summary>
    /// 같은 무리(<see cref="GrammarEntry.Family"/>)·자리의 형식(태그 칸을 차지하는 것). 한 개면 바꿀 형식이 없다.
    /// newDsl 을 주면 그 코어 형식에 맞는 줄만(<see cref="GrammarTable.Fits"/>) + 고른 줄 자신: 새 코어는 옛 별칭 줄을 빼고, 옛 코어는 새 줄을 뺀다.
    /// </summary>
    public static IReadOnlyList<GrammarEntry> Of(GrammarEntry e, bool? newDsl = null) =>
        newDsl is bool nd ? OfCore(e, nd ? GrammarTable.NewVersion : null) : OfCore(e, null, filter: false);

    /// <summary><see cref="Of"/> 를 코어 버전으로: 그 버전에 맞는 줄(<see cref="GrammarTable.Fits(GrammarEntry, string?)"/>) + 고른 줄 자신.</summary>
    public static IReadOnlyList<GrammarEntry> OfCore(GrammarEntry e, string? core, bool filter = true) =>
        GrammarTable.Rows.Where(r => r.Place == e.Place && r.Role == e.Role && r.Family == e.Family && (r.TagCells > 0 || e.Place != Place.TagAction)
            && (!filter || ReferenceEquals(r, e) || GrammarTable.Fits(r, core))).ToList();

    /// <summary>형식 칸의 이름: 태그 동작이 한 이름 안의 변형(TAPE·cm)이면 위치, 그 밖은 형식.</summary>
    public static string FieldLabel(IReadOnlyList<GrammarEntry> forms) =>
        forms[0].Place == Place.TagAction && forms.All(f => f.Name == forms[0].Name) ? "위치" : "형식";

    /// <summary>
    /// 꼴을 가르는 낱말: 같은 이름의 모든 꼴에 있는 칸은 빼고 남은 칸마다 고정 낱말 하나(TAPE), 단위(cm), 센서, 아니면 칸 라벨.
    /// 없으면 빈 글자(바로). 예: SEQ_START = 바로·cm·TAPE·TAPE_GD, BELT_ON = 바로·정지 조건, AGV_MOVE = 바로·옵션.
    /// </summary>
    public static string Word(GrammarEntry e)
    {
        if (e.FormName.Length > 0) return e.FormName;
        var forms = Of(e);
        static string Key(ArgSpec a) => $"{a.Kind}|{a.Unit}|{string.Join(",", a.Words ?? [])}";
        var shared = forms.Count < 2 ? [] : forms.Select(f => f.Args.Select(Key).ToHashSet()).Aggregate((x, y) => { x.IntersectWith(y); return x; });
        return string.Join(" ", e.Args.Where(a => !shared.Contains(Key(a))).Select(a => a.Kind switch
        {
            ArgKind.Word when a.Words is { Count: 1 } w => w[0],
            ArgKind.FromSensor => "센서",
            ArgKind.Number or ArgKind.StopDist when a.Unit.Length > 0 => a.Unit,
            _ => a.Label.Length > 0 ? a.Label : null,
        }).OfType<string>().Distinct());
    }

    /// <summary>
    /// from 꼴의 인자들을 to 꼴 글자로: 같은 종류 칸(숫자·거리는 단위가 같으면 같은 칸)의 인자는 그대로 옮기고, 없는 칸은 fill 로 채운다.
    /// 생략 칸은 옮길 인자가 있을 때만 쓴다.
    /// </summary>
    public static string Convert(GrammarEntry to, GrammarEntry from, IReadOnlyList<Arg> args, Func<ArgSpec, string> fill) => Map(to, from, args, fill).Text;

    /// <summary>
    /// 꼴을 바꾸면 버려지는 인자 글(옮길 칸이 없는 것). 꼴을 가르는 낱말 하나 칸(TAPE 등)은 꼴 자체라 빼고 센다.
    /// 화면이 바꾸기 전에 확인 팝업으로 보인다.
    /// </summary>
    public static IReadOnlyList<string> Dropped(GrammarEntry to, GrammarEntry from, IReadOnlyList<Arg> args) => Map(to, from, args, _ => "").Dropped;

    static (string Text, IReadOnlyList<string> Dropped) Map(GrammarEntry to, GrammarEntry from, IReadOnlyList<Arg> args, Func<ArgSpec, string> fill)
    {
        var specs = Facts.Expand(from, args.Count) ?? [];
        var left = args.Select((a, i) => (Arg: a, Spec: i < specs.Count ? specs[i] : null)).ToList();
        var outArgs = new List<string>();
        var extra = new List<string>();
        bool inputOnly = to.Args.Any(a => a.Kind == ArgKind.ParamTime); // PARAM_TIME 형식의 조건 칸은 입력만 받는다(코어가 상태·둘째 ANY 를 막는다)
        foreach (var spec in to.Args)
            for (int k = 0; k < spec.Most; k++)
            {
                int i = left.FindIndex(x => x.Spec != null && Same(x.Spec, spec) && Fits(x.Arg.Node, spec));
                if (i >= 0)
                {
                    var arg = left[i].Arg;
                    if (inputOnly && CondLike(spec) && arg.Node is CallNode { Name: "AND" } and) { var (text, drop) = InputOnly(and); outArgs.Add(text); extra.AddRange(drop); }
                    else outArgs.Add(arg.Text.Trim());
                    left.RemoveAt(i);
                }
                else if (k < spec.Least) outArgs.Add(fill(spec));
                else break;
            }
        // 형식을 가르는 낱말 하나 칸(TAPE 등)은 꼴 자체라 빼지만 STATION0 은 값(스테이션 칸)이라 버리는 인자로 센다
        var dropped = left.Where(x => !Defining(x.Spec)).Select(x => x.Arg.Text.Trim()).Concat(extra).ToList();
        return (Form.Call(to.Name, outArgs), dropped);
    }

    static bool Defining(ArgSpec? s) => s is { Kind: ArgKind.Word, Words: [var w] } && w != "STATION0";

    /// <summary><c>AND( … )</c> 에서 입력 항목만 남긴다: 상태 낱말(<c>ALWAYS</c> 는 상태가 아니다)과 둘째 이후 <c>ANY</c> 는 버린다. 남은 것이 없으면 <c>NO_IN</c>.</summary>
    static (string Text, List<string> Dropped) InputOnly(CallNode and)
    {
        var keep = new List<string>();
        var drop = new List<string>();
        bool anyKept = false;
        foreach (var a in and.Args)
        {
            string name = a.Node switch { CallNode c => c.Name, NameNode m => m.Name, _ => "" };
            bool state = name != "ALWAYS" && Facts.StateWords.Contains(name);
            bool second = name == "ANY" && anyKept;
            if (name == "ANY") anyKept = true;
            (state || second ? drop : keep).Add(a.Text.Trim());
        }
        return (keep.Count == 0 ? "NO_IN" : Form.Call("AND", keep), drop);
    }

    static bool Numeric(ArgSpec s) => s.Kind is ArgKind.Number or ArgKind.StopDist;

    /// <summary>입력 조건 칸(<c>IN( … )</c> 만 / <c>AND( … )</c> 도)은 같은 조건 칸이다.</summary>
    static bool CondLike(ArgSpec s) => s.Kind is ArgKind.InCond or ArgKind.AndCond;

    static bool Same(ArgSpec a, ArgSpec b) => (a.Kind == b.Kind || Numeric(a) && Numeric(b) || CondLike(a) && CondLike(b)) && (!Numeric(a) || a.Unit == b.Unit)
        && (a.Kind != ArgKind.ParamTime || (a.Label == "제한") == (b.Label == "제한")); // PARAM_TIME: 제한 칸 대 조건 유지 틱(안정·누적·유지)으로만 가른다

    /// <summary>옮길 인자가 새 칸에 맞는가: 낱말 칸은 그 낱말, 숫자 칸에 거리 FL 은 안 맞다.</summary>
    static bool Fits(Node n, ArgSpec spec) => spec.Kind switch
    {
        ArgKind.Word => n is NameNode w && spec.Words!.Contains(w.Name),
        ArgKind.Number => n is not NameNode { Name: "FL" },
        ArgKind.InCond => n is not CallNode { Name: "AND" },
        _ => true,
    };
}
