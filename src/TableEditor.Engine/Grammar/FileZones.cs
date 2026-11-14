namespace TableEditor.Engine.Grammar;

/// <summary>
/// 표 구역 하나. Open·Close 는 구역 표지 매크로 이름(enum 구역은 "enum"·"};").
/// 구역 안 문장 = 문법 표에서 자리가 Places 에 들고 문장 역할인 이름(Names 가 있으면 그 이름만).
/// </summary>
public sealed record ZoneDef(string Open, string Close, IReadOnlyList<Place> Places, IReadOnlyList<string>? Names = null)
{
    public bool IsEnum => Open == "enum";
}

/// <summary>
/// 파일 하나의 구역 표(사양 5.2절). Top = 최상위에서 문장으로 읽는 이름(구역 여는 이름 밖),
/// Defines = 최상위 <c>#define 이름 값</c> 을 문장으로 읽는가, Writable = 사양 2.3절 쓰기 칸.
/// </summary>
public sealed record FileZoneDef(string FileName, IReadOnlyList<string> Top, bool Defines, IReadOnlyList<ZoneDef> Zones, bool Writable);

public static class FileZones
{
    public static readonly ZoneDef Enum = new("enum", "};", []);

    static readonly ZoneDef Profiles = new("PROFILE_TABLE_BEGIN", "PROFILE_TABLE_END", [Place.Frame], ["PROFILE", "PROFILE_LOAD", "PROFILE_LOAD_IF"]);
    static readonly ZoneDef Tags = new("TAG_TABLE_BEGIN", "TAG_TABLE_END", [Place.TagRule]);
    static readonly ZoneDef Seq = new("SEQ_BEGIN", "SEQ_END", [Place.Step]);
    static readonly ZoneDef Act = new("ACT_BEGIN", "ACT_END", [Place.Step]);
    static readonly ZoneDef SeqMap = new("SEQ_MAP_BEGIN", "SEQ_MAP_END", [Place.Frame], ["SEQ_MAP"]);
    static readonly ZoneDef ActMap = new("ACT_MAP_BEGIN", "ACT_MAP_END", [Place.Frame], ["ACT_MAP"]);
    static readonly ZoneDef Manual = new("MANUAL_BEGIN", "MANUAL_END", [Place.Manual]);

    /// <summary>읽기 대상 파일 11개(사양 2.3절 읽기 O). 이 밖의 파일은 열지 않는다.</summary>
    public static readonly IReadOnlyList<FileZoneDef> Files =
    [
        new("site.h", [], false, [Enum], true),
        new("profiles.c", ["LOAD_IF"], false, [Profiles], true),
        new("io_map.h", [], true, [], true),
        new("custom_io.h", [], true, [], true),
        new("tag_table.c", [], true, [Tags], true),
        new("seq_table.c", ["SEQ_LABELS"], false, [Seq, Act, SeqMap, ActMap], true),
        new("seq_custom.h", [], false, [Enum], true),
        new("act_ids.h", [], true, [Enum], true),
        new("act_table.c", ["SEQ_LABELS"], false, [Seq, Act, SeqMap, ActMap], true),
        new("manual_table.c", [], false, [Manual], true),
        new("tag_custom.h", [], false, [Enum], false), // CALL 번호 enum 만 읽는다. 나머지는 원문
    ];

    /// <summary>표에 없는 파일 이름: 구역 없음. 주석·빈 줄 밖은 모두 원문.</summary>
    public static FileZoneDef For(string fileName) =>
        Files.FirstOrDefault(f => string.Equals(f.FileName, fileName, StringComparison.OrdinalIgnoreCase))
        ?? new FileZoneDef(fileName, [], false, [], false);

    public static bool IsReadTarget(string fileName) =>
        Files.Any(f => string.Equals(f.FileName, fileName, StringComparison.OrdinalIgnoreCase));
}
