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
    /// <summary>같은 이름·자리의 꼴(태그 칸을 차지하는 것). 한 개면 바꿀 꼴이 없다.</summary>
    public static IReadOnlyList<GrammarEntry> Of(GrammarEntry e) =>
        GrammarTable.Find(e.Name, e.Place).Where(r => r.TagCells > 0 || e.Place != Place.TagAction).ToList();

    /// <summary>꼴을 가르는 낱말: 고정 낱말 하나(TAPE), 단위(cm), 센서. 없으면 빈 글자(바로).</summary>
    public static string Word(GrammarEntry e) => string.Join(" ", e.Args.Select(a => a.Kind switch
    {
        ArgKind.Word when a.Words is { Count: 1 } w => w[0],
        ArgKind.FromSensor => "센서",
        ArgKind.Number or ArgKind.StopDist when a.Unit.Length > 0 => a.Unit,
        _ => null,
    }).OfType<string>().Distinct());

    /// <summary>
    /// from 꼴의 인자들을 to 꼴 글자로: 같은 종류 칸(숫자·거리는 단위가 같으면 같은 칸)의 인자는 그대로 옮기고, 없는 칸은 fill 로 채운다.
    /// 생략 칸은 옮길 인자가 있을 때만 쓴다.
    /// </summary>
    public static string Convert(GrammarEntry to, GrammarEntry from, IReadOnlyList<Arg> args, Func<ArgSpec, string> fill)
    {
        var specs = Facts.Expand(from, args.Count) ?? [];
        var left = args.Select((a, i) => (Arg: a, Spec: i < specs.Count ? specs[i] : null)).ToList();
        var outArgs = new List<string>();
        foreach (var spec in to.Args)
            for (int k = 0; k < spec.Most; k++)
            {
                int i = left.FindIndex(x => x.Spec != null && Same(x.Spec, spec) && Fits(x.Arg.Node, spec));
                if (i >= 0) { outArgs.Add(left[i].Arg.Text.Trim()); left.RemoveAt(i); }
                else if (k < spec.Least) outArgs.Add(fill(spec));
                else break;
            }
        return Form.Call(to.Name, outArgs);
    }

    static bool Numeric(ArgSpec s) => s.Kind is ArgKind.Number or ArgKind.StopDist;

    static bool Same(ArgSpec a, ArgSpec b) => (a.Kind == b.Kind || Numeric(a) && Numeric(b)) && (!Numeric(a) || a.Unit == b.Unit);

    /// <summary>옮길 인자가 새 칸에 맞는가: 낱말 칸은 그 낱말, 숫자 칸에 거리 FL 은 안 맞다.</summary>
    static bool Fits(Node n, ArgSpec spec) => spec.Kind switch
    {
        ArgKind.Word => n is NameNode w && spec.Words!.Contains(w.Name),
        ArgKind.Number => n is not NameNode { Name: "FL" },
        _ => true,
    };
}
