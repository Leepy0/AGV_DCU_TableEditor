namespace TableEditor.App.Blocks;

/// <summary>
/// 태그 동작의 팔레트 분류(사양 9.2절 태그: 정지 / 시퀀스 / 속도·방향 / 회전 / 분기 / 보고 / 장애물 / 기타).
/// 분류 키는 테마의 띠 색 이름(<c>Cat.stop</c> 등)과 같다. 문법 표의 분류 칸은 "동작" 하나라 화면 쪽에 둔다.
/// </summary>
public static class Palette
{
    public static readonly IReadOnlyList<(string Key, string Name)> Categories =
    [
        ("stop", "정지"), ("seq", "시퀀스"), ("move", "속도·방향"), ("turn", "회전"),
        ("br", "분기"), ("acs", "보고"), ("obs", "장애물"), ("etc", "기타"),
    ];

    static readonly Dictionary<string, string> byName = new(StringComparer.Ordinal)
    {
        ["AGV_STOP"] = "stop", ["AGV_AUTOSTART"] = "stop", ["AGV_AUTOSTART_PARAM"] = "stop",
        ["SEQ_START"] = "seq",
        ["AGV_SPEED"] = "move", ["AGV_SPEED_MMIN"] = "move", ["AGV_FORBACK"] = "move",
        ["AGV_TURN"] = "turn",
        ["AGV_BRANCH"] = "br",
        ["ACS_SEND"] = "acs", ["ACS"] = "acs", ["STATION_RESET"] = "acs",
        ["OBS_ON"] = "obs", ["OBS_OFF"] = "obs", ["OBS_OFF_AUTO"] = "obs",
    };

    /// <summary>매크로 이름의 분류 키. 모르는 이름은 기타.</summary>
    public static string Of(string name) => byName.TryGetValue(name, out var k) ? k : "etc";
}
