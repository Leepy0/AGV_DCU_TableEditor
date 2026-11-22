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
        ArgKind.Number or ArgKind.Time or ArgKind.Timeout => FieldEditor.Number,
        ArgKind.Profile or ArgKind.ProfileSet or ArgKind.ProfileFl or ArgKind.Input or ArgKind.CoreInput or ArgKind.Signal or ArgKind.Output or ArgKind.Belt or ArgKind.LoadIf
            or ArgKind.SeqId or ArgKind.SeqShort or ArgKind.SeqTable or ArgKind.Label or ArgKind.ActId or ArgKind.ActTable
            or ArgKind.State or ArgKind.CallNo or ArgKind.CoreParam or ArgKind.FndParam or ArgKind.ParamAny => FieldEditor.Names,
        _ => FieldEditor.Text,
    };

    /// <summary>이름 칸의 항목. 사이트 이름 먼저, 코어 이름은 뒤에 따로 묶는다. 값이 있으면 작은 글로.</summary>
    public static IReadOnlyList<Choice> For(ArgSpec s, NameDict names, SignalScope? scope = null, Func<string, bool>? keep = null)
    {
        // 신호는 그 줄 프로파일에 맞는 것 + 지정 없는 것만(이미 쓴 것은 남긴다)
        IEnumerable<NameDef> Of(params NameKind[] kinds) => names.All.Where(d => kinds.Contains(d.Kind) && !d.Weak && (scope == null || !scope.Hides(d) || keep?.Invoke(d.Name) == true));
        if (scope is { Seq: true } && s.Kind is ArgKind.SeqId or ArgKind.SeqShort) return SeqChoices(Of(NameKind.SeqId).Where(d => s.Kind == ArgKind.SeqId || d.Name.StartsWith("SEQ_", StringComparison.Ordinal)), scope, s.Kind == ArgKind.SeqShort);
        Choice C(NameDef d, string? name = null) => new(name ?? d.Name, d.Value?.ToString(), d.Core ? "코어" : "사이트");
        Choice Labeled(NameDef d) => CoreConstants.ParamLabels.TryGetValue(d.Name, out var label) ? new(d.Name, label, "코어") : C(d);
        IEnumerable<Choice> list = s.Kind switch
        {
            ArgKind.Profile or ArgKind.ProfileSet or ArgKind.ProfileFl => (s.Min == 0 ? [new Choice("ANY_PROFILE", "0", "전체")] : Array.Empty<Choice>())
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
            ArgKind.FndParam => Of(NameKind.FndParam).Select(d => Labeled(d)),
            ArgKind.ParamAny => Of(NameKind.FndParam).Where(d => !CoreConstants.PlacedParams.Contains(d.Name)).Select(d => Labeled(d)).Concat(Of(NameKind.CoreParam).Select(d => C(d))),
            // 보고 종류 IDLE·DISPLAY·CROSS_IN·CROSS_OUT 과 수동 표 모드 ANY 는 그 낱말이 생긴 코어 버전 이상 사이트만(수동 표 모드 IDLE 은 다른 낱말이라 건드리지 않는다)
            ArgKind.Word => (s.Words ?? []).Where(w => GrammarTable.WordOk(w, s.Words!, names.CoreVersion)).Select(w => new Choice(w, null, "")),
            _ => [],
        };
        return list.DistinctBy(c => c.Name).OrderBy(c => c.Group == "코어" ? 1 : 0).ToList();
    }

    public const string Linked = "연결", Unlinked = "연결 없음";

    /// <summary>
    /// 태그 동작 시퀀스 ID: 그 줄 프로파일에 대응표가 있는 ID 를 위에 <c>ID → 표</c>(표 이름은 작은 글), 없는 ID 는 아래 "연결 없음"(흐리게).
    /// 프로파일이 없는(ANY_PROFILE) 줄은 ID 옆에 연결된 프로파일 수.
    /// </summary>
    /// <summary>대응표 줄의 프로파일 이름: 번호로 적었으면(<c>1</c>) 프로파일 번호로 이름을 푼다.</summary>
    static string Pf(Site s, MapRow m) =>
        long.TryParse(m.Profile.Trim(), out var n) && s.Profiles.FirstOrDefault(p => p.Number == n)?.Name is { } nm ? nm : m.Profile.Trim();

    static IReadOnlyList<Choice> SeqChoices(IEnumerable<NameDef> ids, SignalScope scope, bool shortName)
    {
        var site = scope.Site;
        return ids.Select(d =>
        {
            var rows = site.SeqMap.Where(m => m.Id == d.Name).ToList();
            var hit = scope.Profiles == null ? rows : rows.Where(m => m.AnyProfile || scope.Profiles.Contains(Pf(site, m))).ToList();
            var name = shortName ? d.Name[4..] : d.Name;
            if (hit.Count == 0) return (d, new Choice(name, d.Value?.ToString(), Unlinked));
            var note = scope.Profiles != null ? "→ " + string.Join(", ", hit.Select(m => m.Table).Distinct())
                : (hit.Any(m => m.AnyProfile) ? site.Profiles.Count : hit.Select(m => Pf(site, m)).Distinct().Count()) + "곳";
            return (d, new Choice(name, note, Linked));
        }).DistinctBy(x => x.Item2.Name).OrderBy(x => x.Item2.Group == Linked ? 0 : 1).ThenBy(x => x.d.Core ? 1 : 0).Select(x => x.Item2).ToList();
    }

    /// <summary>새 동작의 기본 글자: 꼭 있는 칸만, 첫 항목·범위 안 값으로.</summary>
    public static string DefaultText(GrammarEntry e, NameDict names)
    {
        var args = e.Args.Where(a => a.Least > 0).Select(a => DefaultArg(a, names)).ToList();
        // 태그 보고: 칸 0 기본은 1칸 형식 ACS( 종류, STATION0 )(1칸을 못 쓰는 종류는 2칸 그대로)
        if (e is { Name: "ACS", Place: Place.TagAction } && e.Args.Count == 2 && e.Args[1].Kind == ArgKind.AcsStation
            && GrammarTable.Find("ACS", Place.TagAction).FirstOrDefault(r => r.TagCells == 1)?.Args[0].Words is { } one && one.Contains(args[0]))
            args[1] = "STATION0";
        if (args.Count == 0) return e.Name;
        return e.Spacing == Spacing.Tight ? $"{e.Name}({string.Join(", ", args)})" : $"{e.Name}( {string.Join(", ", args)} )";
    }

    /// <summary>칸 하나의 기본 글자(새 동작, 꼴 바꾸기의 빈 칸).</summary>
    public static string DefaultArg(ArgSpec a, NameDict names)
    {
        switch (a.Kind)
        {
            case ArgKind.Number or ArgKind.Time or ArgKind.StopDist:
                long v = a.Unit == "도" ? 90 : a.Unit == "틱" ? 100 : 0;
                return Math.Clamp(v, a.Min, Math.Max(a.Min, a.Max)).ToString();
            case ArgKind.AcsNumber:
                return "0";
            case ArgKind.FromSensor:
                return $"FROM_SENSOR( {FirstInput(names)}, 100 )";
            case ArgKind.InCond or ArgKind.OutCond or ArgKind.OutAnd or ArgKind.AndCond:
                return "NO_IN";
            case ArgKind.ParamTime:
                return "PARAM_TIME( AS, SEC )";
            case ArgKind.ParamAny:
                return "AS";
            case ArgKind.OutMask:
                return "NO_OUT";
            case ArgKind.XCond:
                return "X_ALWAYS";
            case ArgKind.Until:
                return "UNTIL( NO_IN )";
            case ArgKind.Any2:
                return $"ANY2_ON({FirstInput(names)})";
            case ArgKind.AcsStation:
                return "STATION(0)";
            case ArgKind.AcsSource:
                return names.NewDsl ? "STATION(0)" : "STATION0";
            case ArgKind.StationExpr:
                return "STATION(0)";
            default:
                return For(a, names).FirstOrDefault()?.Name ?? "0";
        }
    }

    static string FirstInput(NameDict names) => For(new ArgSpec(ArgKind.Input), names).FirstOrDefault(c => c.Group == "사이트")?.Name ?? "IN_EXT_1";
}
