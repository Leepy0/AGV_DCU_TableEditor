namespace TableEditor.Engine.Grammar;

public sealed record CoreName(string Name, int Value);

/// <summary>오류 코드와 뜻(낱말).</summary>
public sealed record ErrorCode(int Code, string Meaning);

/// <summary>
/// 파라미터 한 줄(사양 3.9절). Number = EEPROM 주소, TableNames = IF_PARAM·X_PARAM 에 쓰는 이름(없으면 빈 목록),
/// Min·Max = 코어 소스 한계(null = 자리별·미사용·미확인, Note 에 적음).
/// </summary>
public sealed record ParamDef(int Number, string Core, string Fnd, IReadOnlyList<string> TableNames, int? Min, int? Max, string Note = "");

/// <summary>코어 상수 표(사양 3.9절, 코어 공개 헤더 agv/io.h·seq.h·tagact.h·ui.h 와 같은 값).</summary>
public static class CoreConstants
{
    public const int IoNone = 0xFF;
    public const int SeqSiteBase = 32;
    public const int SeqMax = 64;

    static IEnumerable<CoreName> Run(string prefix, int count, int first) =>
        Enumerable.Range(1, count).Select(i => new CoreName(prefix + i, first + i - 1));

    /// <summary>논리 입력. 32·33 은 조건에 못 쓴다. IO_NONE = 미연결.</summary>
    public static readonly IReadOnlyList<CoreName> Inputs =
    [
        .. Run("IN_EXT_", 16, 0),
        new("IN_START_SW", 16), new("IN_STOP_SW", 17), new("IN_ESTOP", 18),
        new("IN_BUMPER_1", 19), new("IN_BUMPER_2", 20), new("IN_OBS_F", 21), new("IN_OBS_B", 22),
        new("IN_TAG_L", 23), new("IN_TAG_R", 24), new("IN_REMOTE_START", 25), new("IN_REMOTE_STOP", 26),
        .. Run("IN_IF_", 5, 27),
        new("IN_AMP_L_OVERLOAD", 32), new("IN_AMP_R_OVERLOAD", 33),
        new("IO_NONE", IoNone),
    ];

    /// <summary>논리 출력. 23·24 쓰지 않음, 32~39 표에 못 씀(이름은 agv/io.h).</summary>
    public static readonly IReadOnlyList<CoreName> Outputs =
    [
        .. Run("OUT_EXT_", 16, 0),
        new("OUT_LAMP_G", 16), new("OUT_LAMP_R", 17), new("OUT_LAMP_Y", 18), new("OUT_AGV_BACK", 19),
        new("OUT_IF_1", 20), new("OUT_IF_2", 21), new("OUT_BUZZER", 22), new("OUT_BRAKE_1", 23), new("OUT_BRAKE_2", 24),
        new("OUT_AMP_L_RESET", 32), new("OUT_AMP_L_DIR", 33), new("OUT_AMP_L_RUN", 34), new("OUT_AMP_L_CW", 35),
        new("OUT_AMP_R_RESET", 36), new("OUT_AMP_R_DIR", 37), new("OUT_AMP_R_RUN", 38), new("OUT_AMP_R_CW", 39),
    ];

    /// <summary>코어 시퀀스 ID 1~18. 태그 SEQ_START 에는 SEQ_ 를 뺀 이름. 사이트 ID 는 32~63.</summary>
    public static readonly IReadOnlyList<CoreName> SeqIds =
    [
        new("SEQ_LOAD", 1), new("SEQ_BOX_LOAD", 2), new("SEQ_LOAD_FINISH", 3), new("SEQ_LOAD_LAST", 4),
        new("SEQ_LOAD_EMPTY", 5), new("SEQ_LOAD_PART", 6), new("SEQ_CART_LOAD", 7), new("SEQ_TROLLEY_LOAD", 8),
        new("SEQ_LOAD_CHECK", 9), new("SEQ_UNLOAD", 10), new("SEQ_BOX_UNLOAD", 11), new("SEQ_UNLOAD_FINISH", 12),
        new("SEQ_UNLOAD_LAST", 13), new("SEQ_UNLOAD_EMPTY", 14), new("SEQ_UNLOAD_PART", 15), new("SEQ_CART_UNLOAD", 16),
        new("SEQ_TROLLEY_UNLOAD", 17), new("SEQ_MATERIAL_UNLOAD", 18),
    ];

    /// <summary>방향 값(agv/tagact.h).</summary>
    public static readonly IReadOnlyList<CoreName> Directions =
    [
        new("DIR_FWD", 1), new("DIR_BACK", 2), new("DIR_TOGGLE", 3),
        new("DIR_FWD_AFTER_TURN", 4), new("DIR_BACK_AFTER_TURN", 5),
        new("TURN_CW", 1), new("TURN_CCW", 2),
        new("BRANCH_LEFT", 1), new("BRANCH_RIGHT", 2),
    ];

    /// <summary>키(agv/ui.h).</summary>
    public static readonly IReadOnlyList<string> Keys = ["MODE", "SHIFT", "UP", "RESET", "SET", "DOWN"];

    public static readonly IReadOnlyList<ErrorCode> ErrorCodes =
    [
        new(200, "앰프 과부하 좌"), new(201, "앰프 과부하 우"), new(202, "엔코더 고착 좌"), new(203, "엔코더 고착 우"),
        new(204, "비상 정지"), new(205, "범퍼"), new(250, "파라미터 한계"), new(251, "파라미터 체크섬"),
        new(255, "가이드 통신"), new(257, "가이드 범위"), new(258, "가이드 센서"), new(260, "배터리 과전압"),
        new(265, "분기 시간 초과"), new(270, "센서 정지 감지 없음"), new(277, "접근 실패"), new(280, "배터리 저전압"),
        new(281, "장애물"), new(285, "교차로 대기"), new(286, "교차로 ACK 없음"), new(290, "일시 정지·표 오류·사용자 함수 실패"),
    ];

    /// <summary>파라미터(번호 = EEPROM 주소). 화면에는 FND 이름만 보인다.</summary>
    public static readonly IReadOnlyList<ParamDef> Params =
    [
        new(1, "P_SPEED_HIGH", "DRS", ["DRS"], 7, 60),
        new(2, "P_SPEED_LOW", "LOS", ["LOS"], 3, 60),
        new(3, "P_SPEED_TURN", "TNS", ["TNS"], 3, 25),
        new(4, "P_AUTO_DRIVE_MODE", "ATD", ["ATD"], 0, 3),
        new(5, "P_INTERFACE_MODE", "ITM", ["ITM"], 0, 99),
        new(6, "P_SOUND", "SND", ["SND"], 0, 1),
        new(7, "P_BUMPER_USE", "BPS", ["BPS"], null, null, "자리별 0~5, 0~5"),
        new(8, "P_OBS_USE", "OBS", ["OBS"], null, null, "자리별 0~2, 0~2"),
        new(9, "P_DISPLAY_MODE", "DSM", ["DSM"], 1, 10),
        new(10, "P_BAT_OFFSET_DV", "BO-", ["BO"], -9, 9),
        new(11, "P_SPEED_JOG", "JSP", ["JSP"], 7, 60),
        new(12, "P_ACC", "ACC", ["ACC"], 5, 60),
        new(13, "P_DCC", "DCC", ["DCC"], 20, 60),
        new(14, "P_AUTO_START_SEC", "AS", ["AS"], 0, 999),
        new(15, "P_STATION_NO", "ST", ["ST"], 0, 99),
        new(16, "P_BRANCH_MODE", "BRM", [], null, null, "미사용"),
        new(17, "P_TURN_FORBACK", "TFB", ["TFB", "TFB_1", "TFB_10"], null, null, "자리별 0~2, 0~2"),
        new(18, "P_TAG_DIGITS", "TAG", [], null, null, "미사용"),
        new(19, "P_STOP_LEN_MM", "SL", ["SL"], 0, 300),
        new(20, "P_STEER_KP", "K_P", ["K_P"], 0, 99),
        new(21, "P_STEER_KI", "K_I", ["K_I"], 0, 20),
        new(22, "P_WHEEL_DIA_MM", "WS", ["WS"], 10, 500),
        new(23, "P_GEAR_RATIO", "RG", ["RG"], 1, 250),
        new(24, "P_WHEEL_BASE_MM", "WW", ["WW"], 30, 300),
        new(25, "P_AGV_ID", "ID_", ["ID"], 0, 99),
        new(26, "P_AGV_TYPE", "AT", ["AT"], 0, 999),
        new(27, "P_LAMP_EXT_USE", "LO", ["LO"], 0, 1),
        new(28, "P_SPEED_MIN", "SPN", ["SPN"], 0, 999),
        new(29, "P_SWITCH_START_NUM", "SSN", [], null, null),
        new(30, "P_TAG_TYPE", "TTY", [], null, null),
        new(31, "P_COMM_LINK", "CML", ["CML"], 0, 1),
        new(32, "P_STOP_LEN_FWD_MM", "FL", ["FL"], 0, 900),
        new(33, "P_ACS_STANDBY", "ASS", ["ASS"], null, null, "자리별"),
        new(34, "P_BRANCH_OFFSET", "BGO", ["BGO"], 1, 99),
        new(35, "P_CROSS_IN_CM", "CD", ["CD"], 0, 999),
        new(36, "P_ACS_RETRY_LEGACY", "WRT", ["WRT"], 0, 99),
        new(37, "P_ACS_RETRY_SEC", "CT", ["CT"], 0, 989),
        new(38, "P_GUIDE_CURVE_DETECT", "GSR", [], null, null, "미사용"),
        new(39, "P_LEGACY_QUIRKS", "L", [], 0, 4095),
        new(40, "P_ERR_REPORT_MIN", "EST", ["EST"], 0, 99),
        new(41, "P_CROSS_RETRY_100MS", "RCI", ["RCI"], 0, 99),
        new(42, "P_CROSS_MODE", "LUS", [], null, null, "무시"),
        new(43, "P_STEER_RPM_OFFSET", "SR", ["SR"], -999, 999),
        new(44, "P_GUIDE_TIMEOUT", "GTO", ["GTO"], 0, 99),
        new(45, "P_ACS_VERSION", "AV-", ["AV"], 0, 99),
        new(46, "P_PARA_INIT", "INT", [], null, null, "런타임 값"),
        new(47, "P_VERSION", "V_", [], null, null, "런타임 값"),
        new(48, "P_BAT_DV", "VT", [], null, null, "런타임 값"),
        new(49, "P_BAT_PERCENT", "CR", [], null, null, "런타임 값"),
        new(50, "P_GUIDE_DIST_MM", "GD", ["GD"], 100, 999),
        new(51, "P_OPT_FLAGS", "OPT", [], null, null, "안 씀"),
        new(52, "P_SPEED_FF", "FF", [], null, null),
        new(53, "P_ENC_STALL_TICKS", "ES", ["ES"], null, null, "미확인"),
        new(54, "P_EMPTY_SPEED", "EDS", ["EDS"], null, null, "미확인"),
        new(55, "P_EMPTY_TURN", "ETN", ["ETN"], null, null, "미확인"),
        new(56, "P_EMPTY_ACC", "EAC", ["EAC"], null, null, "미확인"),
        new(57, "P_EMPTY_DCC", "EDC", ["EDC"], null, null, "미확인"),
        new(58, "P_LOAD_DETECT", "LDS", ["LDS"], 0, 1),
        new(59, "P_DEV_TEST", "TST", [], null, null, "개발 이미지만"),
    ];
}
