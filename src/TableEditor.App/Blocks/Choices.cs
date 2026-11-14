using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;

namespace TableEditor.App.Blocks;

/// <summary>드롭다운 항목 하나. Note = 작은 글(코어 ID 등), Group = 분류(항목이 많을 때 묶음 머리).</summary>
public sealed record Choice(string Name, string? Note, string Group)
{
    public override string ToString() => Name;
}

/// <summary>칸 편집기 종류: 이름 드롭다운 / 고정 낱말 드롭다운 / 숫자 / 글(식·그 밖).</summary>
public enum FieldEditor { Names, Words, Number, Text }

/// <summary>칸 종류 → 편집기와 고를 것(이름 사전에서). 문법 표의 칸 종류만 본다.</summary>
public static class Choices
{
    public static FieldEditor EditorOf(ArgSpec s) => s.Kind switch
    {
        ArgKind.Word => FieldEditor.Words,
        ArgKind.Number or ArgKind.Time => FieldEditor.Number,
        ArgKind.Profile or ArgKind.Input or ArgKind.CoreInput or ArgKind.Signal or ArgKind.Output or ArgKind.Belt or ArgKind.LoadIf
            or ArgKind.SeqId or ArgKind.SeqShort or ArgKind.SeqTable or ArgKind.Label or ArgKind.ActId or ArgKind.ActTable
            or ArgKind.State or ArgKind.CallNo or ArgKind.CoreParam or ArgKind.FndParam => FieldEditor.Names,
        _ => FieldEditor.Text,
    };

    /// <summary>이름 칸의 항목. 사이트 이름 먼저, 코어 이름은 뒤에 따로 묶는다. 값이 있으면 작은 글로.</summary>
    public static IReadOnlyList<Choice> For(ArgSpec s, NameDict names)
    {
        IEnumerable<NameDef> Of(params NameKind[] kinds) => names.All.Where(d => kinds.Contains(d.Kind) && !d.Weak);
        Choice C(NameDef d, string? name = null) => new(name ?? d.Name, d.Value?.ToString(), d.Core ? "코어" : "사이트");
        IEnumerable<Choice> list = s.Kind switch
        {
            ArgKind.Profile => (s.Min == 0 ? [new Choice("ANY_PROFILE", "0", "전체")] : Array.Empty<Choice>())
                .Concat(Of(NameKind.Profile).Where(d => !d.Core).Select(d => C(d))),
            ArgKind.Input or ArgKind.Signal => Of(NameKind.Input, NameKind.InputHigh, NameKind.IoNone).Select(d => C(d)),
            ArgKind.CoreInput => Of(NameKind.Input).Where(d => d.Core).Select(d => C(d)),
            ArgKind.Output => Of(NameKind.Output).Select(d => C(d)),
            ArgKind.Belt => names.Belts.Keys.Select(k => new Choice(k, null, "사이트")),
            ArgKind.LoadIf => Of(NameKind.LoadIf).Select(d => C(d)),
            ArgKind.SeqId => Of(NameKind.SeqId).Select(d => C(d)),
            ArgKind.SeqShort => Of(NameKind.SeqId).Where(d => d.Name.StartsWith("SEQ_", StringComparison.Ordinal)).Select(d => C(d, d.Name[4..])),
            ArgKind.SeqTable => Of(NameKind.SeqTable).Select(d => C(d)),
            ArgKind.Label => Of(NameKind.Label).Select(d => C(d)),
            ArgKind.ActId => Of(NameKind.ActId).Select(d => C(d)),
            ArgKind.ActTable => Of(NameKind.ActTable).Select(d => C(d)),
            ArgKind.State => Of(NameKind.State).Select(d => C(d)),
            ArgKind.CallNo => Of(NameKind.CallNo).Select(d => C(d)),
            ArgKind.CoreParam => Of(NameKind.CoreParam).Select(d => C(d)),
            ArgKind.FndParam => Of(NameKind.FndParam).Select(d => C(d)),
            ArgKind.Word => (s.Words ?? []).Select(w => new Choice(w, null, "")),
            _ => [],
        };
        return list.DistinctBy(c => c.Name).OrderBy(c => c.Group == "코어" ? 1 : 0).ToList();
    }

    /// <summary>새 동작의 기본 글자: 꼭 있는 칸만, 첫 항목·범위 안 값으로.</summary>
    public static string DefaultText(GrammarEntry e, NameDict names)
    {
        var args = e.Args.Where(a => a.Least > 0).Select(a => DefaultArg(a, names)).ToList();
        if (args.Count == 0) return e.Name;
        return e.Spacing == Spacing.Tight ? $"{e.Name}({string.Join(", ", args)})" : $"{e.Name}( {string.Join(", ", args)} )";
    }

    static string DefaultArg(ArgSpec a, NameDict names)
    {
        switch (a.Kind)
        {
            case ArgKind.Number or ArgKind.Time or ArgKind.StopDist:
                long v = a.Unit == "도" ? 90 : a.Unit == "틱" ? 100 : 0;
                return Math.Clamp(v, a.Min, Math.Max(a.Min, a.Max)).ToString();
            case ArgKind.AcsNumber:
                return "0";
            case ArgKind.FromSensor:
                var input = For(new ArgSpec(ArgKind.Input), names).FirstOrDefault(c => c.Group == "사이트")?.Name ?? "IN_EXT_1";
                return $"FROM_SENSOR( {input}, 100 )";
            default:
                return For(a, names).FirstOrDefault()?.Name ?? "0";
        }
    }

    /// <summary>
    /// 추가 메뉴 낱말: 머리 이름. 같은 이름에 꼴이 여럿이면 꼴을 가르는 것을 붙인다(고정 낱말 하나, 단위, 센서).
    /// </summary>
    public static string MenuText(GrammarEntry e)
    {
        if (GrammarTable.Find(e.Name, e.Place).Count(r => r.TagCells > 0 || e.Place != Place.TagAction) < 2) return e.Head;
        var bits = e.Args.Select(a => a.Kind switch
        {
            ArgKind.Word when a.Words is { Count: 1 } w => w[0],
            ArgKind.FromSensor => "센서",
            ArgKind.Number or ArgKind.StopDist when a.Unit.Length > 0 => a.Unit,
            _ => null,
        }).OfType<string>().Distinct();
        return string.Join(" ", bits.Prepend(e.Head));
    }
}
