using TableEditor.Engine.Grammar;
using TableEditor.Engine.Text;

namespace TableEditor.Engine.Model;

/// <summary>
/// 옛 확장 조건 <c>X_…</c> 와 새 상태 낱말(<c>AND( … )</c> 안, 코어 v0.58.0)의 대응(코어 docs/03 3.3.3). 새 코어 사이트에서 조건을 고쳐 쓸 때
/// 옛 이름을 새 이름으로 옮긴다. 대응이 없는 것(<c>X_STATION_RUNNING</c>)은 null: 그 조건은 옛 형식(<c>IF_X</c> …)으로 쓴다.
/// </summary>
public static class StateWords
{
    static readonly Dictionary<string, string> Plain = new(StringComparer.Ordinal)
    {
        ["X_ALWAYS"] = "ALWAYS", ["X_FORWARD"] = "FORWARD", ["X_BACKWARD"] = "BACKWARD", ["X_FORBACK_MODE"] = "FORBACK_MODE",
        ["X_LOW_BATTERY"] = "LOW_BATTERY", ["X_CROSS_IN"] = "CROSS_IN", ["X_CROSS_OUT"] = "CROSS_OUT",
        ["X_CROSS_IN_ON"] = "CROSS_IN_WAIT", ["X_CROSS_OUT_ON"] = "CROSS_OUT_WAIT", ["X_ERR_EQ"] = "ERR_IS", ["X_ID"] = "ID",
        ["X_KEY"] = "KEY", ["X_STATION"] = "STATION", ["X_STATION_MOD"] = "STATION_MOD", ["X_STATION_DIV"] = "STATION_DIV",
        ["X_TAG_NUM"] = "TAG_IS", ["X_PARAM"] = "PARAM",
    };

    static readonly Dictionary<string, string> CoreParam = new(StringComparer.Ordinal)
    {
        ["X_PARAM_EQ"] = "PARAM", ["X_PARAM_GE"] = "PARAM_MIN", ["X_PARAM_LE"] = "PARAM_MAX",
    };

    /// <summary>이 이름이 새 상태 낱말인가.</summary>
    public static bool IsWord(string name) => GrammarTable.Find(name).Any(r => r.Place == Place.StateCond);

    /// <summary>새 상태 낱말이면 그대로, 옛 <c>X_…</c> 면 대응하는 새 낱말(인자도 옮김). 대응이 없으면 null.</summary>
    public static XCond? ToState(XCond x)
    {
        if (IsWord(x.Name)) return x;
        if (Plain.TryGetValue(x.Name, out var word)) return new XCond(word, x.Args);
        if (CoreParam.TryGetValue(x.Name, out word) && x.Args.Count == 2
            && Grammar.CoreConstants.Params.FirstOrDefault(p => p.Core == x.Args[0].Trim() && p.TableNames.Count > 0) is { } p)
            return new XCond(word, [p.TableNames[0], x.Args[1]]);
        if (x.Name == "X_NOT" && x.Args.Count == 1 && Reader.ParseArg(x.Args[0]) is { } inner && XCond.FromNode(inner) is { } ix && ToState(ix) is { } s)
            return new XCond("NOT", [s.Text]);
        return null;
    }
}
