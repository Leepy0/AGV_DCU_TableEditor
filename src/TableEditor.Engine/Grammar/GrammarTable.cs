namespace TableEditor.Engine.Grammar;

/// <summary>문법 표 한 줄이 쓰이는 자리(사양 4.1~4.8절).</summary>
public enum Place { Frame, InputCond, ExtCond, Step, Manual, TagRule, TagCond, TagAction, Packed, StateCond }

/// <summary>문장으로 한 줄을 차지하는가(Statement), 다른 문장의 인자 안에만 오는가(Part), 값 이름인가(Value).</summary>
public enum Role { Statement, Part, Value }

/// <summary>쓸 때의 괄호 안쪽 공백(사양 4.0·5.4절). None = 인자 없음, 이름만.</summary>
public enum Spacing { None, Spaced, Tight }

/// <summary>인자 칸의 종류. 이름 사전 종류 / 고정 낱말 / 숫자 / 조건 / 안쪽 묶음.</summary>
public enum ArgKind
{
    Ident,        // 새로 정하는 이름(표 이름, 배열 이름, 라벨 선언, 적재 조건 이름)
    Profile,      // 프로파일. Min 0 이면 ANY_PROFILE(0)도 된다
    Input,        // 입력 별칭·코어 IN_*
    CoreInput,    // 코어 IN_* 만(IN_ACTIVE_HIGH 안)
    Signal,       // ON·OFF 안: 조건에서는 입력, 수동 표 출력 칸에서는 출력
    Output,
    Belt,
    LoadIf,
    SeqId,        // SEQ_ 붙인 전체 이름
    SeqShort,     // SEQ_START 안: SEQ_ 뺀 이름
    SeqTable,
    Label,
    ActId,
    ActTable,
    State,
    CallNo,
    CoreParam,    // P_… 코어 이름(화면은 FND 이름)
    FndParam,     // IF_PARAM·X_PARAM 표 이름
    Number,
    Time,         // 틱
    Timeout,      // 틱 또는 UNMATCHED( … )
    Word,         // 고정 낱말(Words)
    InCond,       // IN( … ), 묶음 하나, NO_IN
    InGroup,      // IN( … ) 안의 묶음
    XCond,        // X_…
    OutCond,      // WAIT_OUT 의 출력 조건
    Any2,         // ANY2_ON( … )
    Until,        // UNTIL( … )
    LoadSensors,  // LOAD_SENSORS( … )
    TagCond,      // IF_…
    Action,       // 태그 동작
    ManCond,      // IN( … ), KEY( … ), NO_IN
    ManOut,       // ON( … ), OFF( … ), BELT( … ), NO_OUT
    StationExpr,  // STATION(k) 식
    AcsSource,    // 시퀀스 ACS 번호 출처
    AcsNumber,    // ACS_SEND 번호
    AcsStation,   // ACS_SEND·태그 ACS 의 STATION( 칸 )
    StopDist,     // FL 또는 cm
    FromSensor,   // FROM_SENSOR( … )
    AndCond,      // IN( … ), 묶음 하나, NO_IN, AND( … )(v0.58.0)
    AndItem,      // AND( … ) 안 항목: ON·OFF·ANY·IN·상태 낱말
    StateCond,    // NOT( … ) 안: 상태 낱말
    ParamTime,    // PARAM_TIME( 파라미터, 단위 )
    ParamAny,     // PARAM_TIME 의 파라미터: FND 이름 또는 P_… 코어 이름
    OutMask,      // OUT_SET 의 출력 묶음: OUTS( … ), NO_OUT, 벨트 이름
}

/// <summary>인자 칸 하나. Least·Most = 이 칸이 몇 번 오는가(1·1 하나, 0·1 생략 가능, 1·16 여러 개).</summary>
public sealed record ArgSpec(ArgKind Kind, long Min = 0, long Max = 0, string Unit = "", IReadOnlyList<string>? Words = null, int Least = 1, int Most = 1)
{
    /// <summary>칸 라벨(화면 낱말). 문법 줄의 꼴 글자에서 나온다. 없으면 빈 글자.</summary>
    public string Label { get; init; } = "";

    /// <summary>켬/끔 칸: 숫자 1·0 을 화면에서 ON·OFF 로 보이고 고른다(원문은 1·0 그대로). 다른 값은 숫자로 둔다.</summary>
    public bool Switch { get; init; }

    public ArgSpec Opt() => this with { Least = 0 };
    public ArgSpec Sw() => this with { Switch = true };

    public const string On = "ON", Off = "OFF";

    /// <summary>켬/끔 칸 인자의 화면 글: 1 → ON, 0 → OFF. 켬/끔 칸이 아니거나 다른 값(식·이름·2 이상)이면 null.</summary>
    public string? SwitchText(Text.Node n) => Switch && n is Text.NumberNode { Value: 0 or 1 } x ? (x.Value == 1 ? On : Off) : null;

    /// <summary>화면 ON·OFF → 원문 1·0.</summary>
    public static string SwitchRaw(string shown) => shown == On ? "1" : "0";
    public ArgSpec Many(int least, int most) => this with { Least = least, Most = most };
}

/// <summary>문법 표 한 줄. 한 이름이 꼴마다(AGV_STOP 다섯 꼴 등)·자리마다(두 뜻 이름 12개) 따로 줄을 갖는다.</summary>
public sealed record GrammarEntry(
    string Name, Place Place, Role Role, string Form, IReadOnlyList<ArgSpec> Args,
    Spacing Spacing, int TagCells, bool ActForbidden, string Head, string Category,
    string CoreFrom, string? CoreTo)
{
    /// <summary>팔레트 분류(화면 띠 색·추가 드롭다운). 태그 동작은 <see cref="GrammarTable.TagPalette"/>, 시퀀스 스텝은 <see cref="GrammarTable.StepPalette"/> 가운데 하나. 그 밖은 빈 글자.</summary>
    public string Palette { get; init; } = "";

    /// <summary>같은 명령의 형식들이 나눠 갖는 열쇠(<c>WAIT</c>/<c>WAIT_TO</c> 등). 목록에는 무리마다 하나, 형식은 칸에서 고른다.</summary>
    public string Family { get; init; } = "";

    /// <summary>형식 칸의 낱말(없음·시간·파라미터 등). 빈 글자면 <see cref="TableEditor.Engine.Model.ActionForms.Word"/> 가 인자에서 구한다.</summary>
    public string FormName { get; init; } = "";

    /// <summary>인자별 짧은 설명(단위·범위·고를 낱말). 인자가 없으면 빈 글자. <see cref="ArgSpec"/> 에서 만든다.</summary>
    public string Note { get; init; } = "";

    /// <summary>이 줄이 생긴 코어 버전(<see cref="GrammarTable.NewVersion"/>). 빈 글자 = v0.54.0 부터 있던 줄.</summary>
    public string Since { get; init; } = "";

    /// <summary>새 형식(v0.58.0 이상)에서 새 줄에 쓰지 않는 옛 이름(별칭). 읽기·검사는 그대로 받는다.</summary>
    public bool Legacy { get; init; }

    public int MinArgs => Args.Sum(a => a.Least);
    public int MaxArgs => Args.Sum(a => a.Most);
}

/// <summary>사양 4절 문법 전수(공개 이름 163개). 읽기·쓰기·검사·화면이 이 표에서 나온다.</summary>
public static class GrammarTable
{
    /// <summary>이 표가 맞는 코어 문법 버전(사양 6절 머리).</summary>
    public const string Version = "v0.54.0";

    /// <summary>새 DSL 형식(<c>AND( … )</c>·<c>AGV_SPEED( m/min )</c> 등)이 들어온 코어 버전. 사이트 <c>core/VERSION</c> 이 이 이상일 때만 새 줄을 새 형식으로 쓴다.</summary>
    public const string NewVersion = "v0.58.0";

    /// <summary>보고 종류 <c>IDLE</c>(<c>DSL_INFO_IDLE</c> = 'X')이 들어온 코어 버전. 이 미만 사이트는 IDLE 을 고를 수 없고 쓴 줄은 V-4 오류다.</summary>
    public const string IdleVersion = "v0.59.0";

    /// <summary>코어 버전 글(<c>v0.58.0</c>)이 <see cref="NewVersion"/> 이상인가. 모르면(null) 아니다.</summary>
    public static bool IsNew(string? coreVersion) => AtLeast(coreVersion, NewVersion);

    /// <summary>코어 버전에서 보고 종류 IDLE 을 쓸 수 있다(<see cref="IdleVersion"/> 이상). 모르면(null) 아니다.</summary>
    public static bool IdleOk(string? coreVersion) => AtLeast(coreVersion, IdleVersion);

    static bool AtLeast(string? coreVersion, string min)
    {
        static int[] Parts(string v) => v.TrimStart('v').Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        if (coreVersion == null) return false;
        var (a, b) = (Parts(coreVersion), Parts(min));
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int c = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
            if (c != 0) return c > 0;
        }
        return true;
    }

    /// <summary>코어 형식에 맞는 줄인가: 새 코어는 옛 별칭을 뺀 줄, 옛 코어는 새 줄을 뺀 줄(팔레트·형식 칸·새 줄 쓰기).</summary>
    public static bool Fits(GrammarEntry r, bool newDsl) => newDsl ? !r.Legacy : r.Since.Length == 0;

    const int Lots = 255; // shortcut: "…" 의 개수 한계가 사양에 없는 칸(SEQ_LABELS)은 255 로 둔다. 한계가 정해지면 바꾼다

    static readonly string[] Rot = ["CW", "CCW"];
    static readonly string[] SpeedSteps = ["HIGH", "LOW", "LLOW", "MIN"];
    static readonly string[] ForBack = ["FWD", "BACK", "TOGGLE"];
    static readonly string[] Branch = ["LEFT", "RIGHT"];
    static readonly string[] TurnOptSeq = ["NOWAIT", "ALIGN", "OBS"];
    static readonly string[] TurnOptTag = ["ALIGN", "TAPE", "OBS"];
    static readonly string[] MoveOpt = ["TAPE", "TAPE_GD"];
    static readonly string[] TimeUnits = ["TICK", "DSEC", "SEC", "MIN"];
    static readonly string[] Reports = ["LOADING", "UNLOADING", "NEXT", "ARRIVE", "FINISH", "PROCESSING", "STANDBY", "CALL", "IDLE"];
    static readonly string[] ReportsTag = [.. Reports, "STANDBY_RESET"];

    /// <summary>보고 종류 낱말 전부(<c>ACS( 종류, … )</c> 의 종류 칸).</summary>
    public static IReadOnlySet<string> ReportKinds { get; } = new HashSet<string>(ReportsTag, StringComparer.Ordinal);
    static readonly string[] Modes = ["IDLE", "JOG"];
    static readonly string[] Keys = ["MODE", "SHIFT", "UP", "RESET", "SET", "DOWN"];
    static readonly string[] BeltMan = ["CW", "CCW", "OFF"];

    static readonly HashSet<string> TightNames =
        ["ON", "OFF", "ANY_ON", "ANY2_ON", "STATION", "AGV_ID_MINUS", "LOAD_SENSORS", "MOD", "ANY", "ID", "STATION_MOD", "STATION_DIV", "TAG_IS",
         "ERR_IS", "KEY", "NOT", "PARAM", "PARAM_MIN", "PARAM_MAX"];

    static ArgSpec A(ArgKind k) => new(k);
    static ArgSpec N(long min, long max, string unit = "") => new(ArgKind.Number, min, max, unit);
    static ArgSpec Pf(long min) => new(ArgKind.Profile, min, 63);
    static ArgSpec W(params string[] words) => new(ArgKind.Word, Words: words);
    static readonly ArgSpec T = new(ArgKind.Time, 0, 65535, "틱");
    static readonly ArgSpec TO = new(ArgKind.Timeout, 0, 65535, "틱");
    static readonly ArgSpec Lb = new(ArgKind.Label);
    static readonly ArgSpec Cond = new(ArgKind.InCond);
    static readonly ArgSpec AndC = new(ArgKind.AndCond);
    static readonly ArgSpec X = new(ArgKind.XCond);
    static readonly ArgSpec Id = new(ArgKind.Number, 0, 99, "호기");
    static readonly ArgSpec Slot = new(ArgKind.Number, 0, 23, "칸");
    static readonly ArgSpec U16 = new(ArgKind.Number, 0, 65535);

    static GrammarEntry E(string name, Place place, Role role, string form, string head, string cat, params ArgSpec[] args)
    {
        var spacing = args.Length == 0 ? Spacing.None
            : TightNames.Contains(name) || name.StartsWith("X_", StringComparison.Ordinal) ? Spacing.Tight
            : Spacing.Spaced;
        var labels = FormLabels(form);
        if (labels.Length == args.Length) args = args.Select((a, i) => a with { Label = labels[i] }).ToArray();
        return new GrammarEntry(name, place, role, form, args, spacing, 0, false, head, cat, Version, null) { Palette = PaletteOf(name, place, cat), Family = FamilyOf(name), FormName = FormOf(name), Note = NoteOf(args) };
    }

    /// <summary>같은 뜻의 이름 묶음(대기 제한·지연 단위·속도 단위·보고 출처·WHEN 계열·대기 계열). 그 밖은 이름 그대로.</summary>
    static string FamilyOf(string name) => name switch
    {
        "WAIT_TO_PARAM" or "WAIT_PARAM_TO" or "WAIT_IF" or "WAIT_IF_TO" or "WAIT_IF_IN" or "WAIT_IF_IN_TO" or "WAIT_ANY2" or "WAIT_ANY2_TO" => "WAIT",
        "WHEN_IF" or "WHEN_IF_IN" or "WHEN_ANY2" or "WHEN_ID" => "WHEN",
        "DELAY_PARAM" => "DELAY",
        "AGV_SPEED_MMIN" => "AGV_SPEED",
        "ACS_SEND" => "ACS",
        _ => name.EndsWith("_TO", StringComparison.Ordinal) ? name[..^3] : name,
    };

    /// <summary>묶음 안 형식 낱말. 같은 이름의 형식 변형(TAPE·cm 등)은 빈 글자로 둔다.</summary>
    static string FormOf(string name) => name switch
    {
        "WAIT_TO_PARAM" or "WAIT_PARAM_TO" or "DELAY_PARAM" => "파라미터",
        "WHEN" => "조건",
        "WHEN_IF" => "상태",
        "WHEN_IF_IN" => "상태·입력",
        "WHEN_ANY2" => "하나라도 2",
        "WHEN_ID" => "호기",
        "WAIT_IF" => "상태",
        "WAIT_IF_TO" => "상태·시간",
        "WAIT_IF_IN" => "상태·입력",
        "WAIT_IF_IN_TO" => "상태·입력·시간",
        "WAIT_ANY2" => "하나라도 2",
        "WAIT_ANY2_TO" => "하나라도 2·시간",
        "DELAY" => "틱",
        "AGV_SPEED" => "단계",
        "AGV_SPEED_MMIN" => "m/min",
        "ACS" => "스테이션",
        "ACS_SEND" => "숫자",
        "WAIT" or "WAIT_STABLE" or "OUT_FLICKER_UNTIL_IN" or "SELECT_END" => "없음",
        "WAIT_TO" or "WAIT_STABLE_TO" or "OUT_FLICKER_UNTIL_IN_TO" or "SELECT_END_TO" => "시간",
        _ => "",
    };

    static string ArgNote(ArgSpec a)
    {
        string range = a.Max > a.Min ? $"{a.Min}~{a.Max}" : "";
        string desc = a.Kind switch
        {
            _ when a.Switch => "ON·OFF",
            ArgKind.Word => string.Join("·", a.Words ?? []),
            ArgKind.Number or ArgKind.Time or ArgKind.Timeout or ArgKind.StopDist or ArgKind.AcsNumber or ArgKind.AcsStation => string.Join(" ", new[] { a.Unit, range }.Where(s => s.Length > 0)),
            ArgKind.Profile => "프로파일",
            ArgKind.Input or ArgKind.CoreInput or ArgKind.FromSensor => "입력",
            ArgKind.Signal => "신호",
            ArgKind.Output => "출력",
            ArgKind.Belt => "벨트",
            ArgKind.SeqId or ArgKind.SeqShort => "시퀀스",
            ArgKind.SeqTable or ArgKind.ActTable => "표",
            ArgKind.Label => "라벨",
            ArgKind.ActId => "ACT",
            ArgKind.State => "상태",
            ArgKind.CallNo => "번호",
            ArgKind.CoreParam or ArgKind.FndParam => "파라미터",
            ArgKind.InCond or ArgKind.InGroup => "입력 조건",
            ArgKind.AndCond => "조건",
            ArgKind.AndItem => "항목",
            ArgKind.StateCond => "상태",
            ArgKind.ParamTime => "제한",
            ArgKind.ParamAny => "파라미터",
            ArgKind.OutMask => "출력 묶음",
            ArgKind.XCond or ArgKind.TagCond => "차량 상태",
            _ => "",
        };
        var label = a.Label;
        var text = desc.Length == 0 ? label : label.Length == 0 || desc.StartsWith(label, StringComparison.Ordinal) ? desc : $"{label} {desc}";
        return a.Most > 1 ? text + "…" : text;
    }

    static string NoteOf(ArgSpec[] args) => string.Join(" · ", args.Select(ArgNote).Where(s => s.Length > 0));

    /// <summary>태그 동작 팔레트(사양 9.2절 태그).</summary>
    public static readonly IReadOnlyList<string> TagPalette = ["정지", "시퀀스", "속도·방향", "회전", "분기", "보고", "장애물 감지", "기타"];

    /// <summary>시퀀스 스텝 팔레트(사양 9.2절 시퀀스).</summary>
    public static readonly IReadOnlyList<string> StepPalette = ["출력", "벨트", "대기", "분기", "보고", "주행", "끝"];

    static string PaletteOf(string name, Place place, string cat) => place switch
    {
        Place.TagAction => name switch
        {
            "AGV_STOP" or "AGV_AUTOSTART" or "AGV_AUTOSTART_PARAM" => "정지",
            "SEQ_START" => "시퀀스",
            "AGV_SPEED" or "AGV_SPEED_MMIN" or "AGV_FORBACK" => "속도·방향",
            "AGV_TURN" => "회전",
            "AGV_BRANCH" => "분기",
            "ACS_SEND" or "ACS" or "STATION_RESET" => "보고",
            "OBS_ON" or "OBS_OFF" or "OBS_OFF_AUTO" => "장애물 감지",
            _ => "기타",
        },
        Place.Step => name switch
        {
            "END_STOP" or "END_ERROR" or "END_RESTART" or "ERROR_HOLD" => "끝",
            "OBS_ON" or "OBS_OFF" or "OBS_OFF_AUTO" => "주행",
            "SET_STATE" or "ACT_START" or "CALL" => "출력",
            "WAIT_STATE" or "WAIT_ACT" => "대기",
            _ => StepPalette.Contains(cat) ? cat : "",
        },
        _ => "",
    };

    /// <summary>꼴 글자의 괄호 안을 쉼표로 나눈 칸 라벨. <c>[ ]</c>·<c>…</c> 는 뗀다. 안쪽 묶음 칸은 그 묶음의 낱말.</summary>
    static string[] FormLabels(string form)
    {
        int open = form.IndexOf('('), close = form.LastIndexOf(')');
        if (open < 0 || close <= open) return [];
        var body = form[(open + 1)..close].Replace("[", "").Replace("]", "");
        var list = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i <= body.Length; i++)
        {
            if (i < body.Length && body[i] == '(') depth++;
            else if (i < body.Length && body[i] == ')') depth--;
            else if (i == body.Length || body[i] == ',' && depth == 0)
            {
                var t = body[start..i].Trim().TrimEnd('…').Trim();
                if (t.Contains('(')) t = t[..t.IndexOf('(')].Trim() switch
                {
                    "FROM_SENSOR" => "센서",
                    "ANY2_ON" => "둘째",
                    "STATION" => "스테이션",
                    "UNTIL" => "정지 조건",
                    "LOAD_SENSORS" => "적재 센서",
                    "PARAM_TIME" => "제한",
                    var other => other,
                };
                list.Add(t);
                start = i + 1;
            }
        }
        return list.Count == 1 && list[0].Length == 0 ? [] : list.ToArray();
    }

    static GrammarEntry Cells(this GrammarEntry e, int n) => e with { TagCells = n };
    static GrammarEntry Added(this GrammarEntry e) => e with { Since = NewVersion };
    static GrammarEntry Alias(this GrammarEntry e) => e with { Legacy = true };
    static GrammarEntry Form(this GrammarEntry e, string word) => e with { FormName = word };

    /// <summary>코어 v0.58.0 동작 변경을 낱말로(사양 B4). 인자 설명 뒤에 붙는다.</summary>
    static GrammarEntry Word(this GrammarEntry e, string text) => e with { Note = e.Note.Length == 0 ? text : e.Note + " · " + text };
    static GrammarEntry NoAct(this GrammarEntry e) => e with { ActForbidden = true };

    const Place F = Place.Frame, I = Place.InputCond, XC = Place.ExtCond, S = Place.Step,
        M = Place.Manual, TR = Place.TagRule, TC = Place.TagCond, TA = Place.TagAction, PK = Place.Packed, SC = Place.StateCond;
    const Role St = Role.Statement, Pt = Role.Part, V = Role.Value;

    public static readonly IReadOnlyList<GrammarEntry> Rows =
    [
        // ---- 4.1 표 틀 (23) ----
        E("PROFILE_TABLE_BEGIN", F, St, "PROFILE_TABLE_BEGIN( 이름 )", "프로파일 표", "틀", A(ArgKind.Ident)),
        E("PROFILE", F, St, "PROFILE( 프로파일, 타입, 처음, 끝 )", "프로파일", "틀", Pf(1), N(0, 999, "타입"), Id, Id),
        E("PROFILE_LOAD", F, St, "PROFILE_LOAD( 프로파일, 타입, 처음, 끝, 묶음 )", "프로파일 적재", "틀", Pf(1), N(0, 999, "타입"), Id, Id, A(ArgKind.LoadSensors)),
        E("LOAD_SENSORS", F, Pt, "LOAD_SENSORS( 입력… )", "적재 센서", "틀", A(ArgKind.Input).Many(1, 16)),
        E("LOAD_IF", F, St, "LOAD_IF( 이름, 조건[, 조건] )", "적재 조건", "틀", A(ArgKind.Ident), Cond, Cond.Opt()),
        E("PROFILE_LOAD_IF", F, St, "PROFILE_LOAD_IF( 프로파일, 타입, 처음, 끝, 이름 )", "프로파일 조건", "틀", Pf(1), N(0, 999, "타입"), Id, Id, A(ArgKind.LoadIf)),
        E("PROFILE_TABLE_END", F, St, "PROFILE_TABLE_END", "프로파일 표 끝", "틀"),
        E("SEQ_LABELS", F, St, "SEQ_LABELS( 라벨… )", "라벨 목록", "틀", A(ArgKind.Ident).Many(1, Lots)),
        E("SEQ_BEGIN", F, St, "SEQ_BEGIN( 이름 )", "시퀀스 표", "틀", A(ArgKind.Ident)),
        E("SEQ_END", F, St, "SEQ_END", "시퀀스 끝", "틀"),
        E("ACT_BEGIN", F, St, "ACT_BEGIN( 이름 )", "ACT", "틀", A(ArgKind.Ident)),
        E("ACT_END", F, St, "ACT_END", "ACT 끝", "틀"),
        E("SEQ_MAP_BEGIN", F, St, "SEQ_MAP_BEGIN( 이름 )", "대응표", "틀", A(ArgKind.Ident)),
        E("SEQ_MAP", F, St, "SEQ_MAP( 프로파일, 시퀀스 ID, 표 이름 )", "시퀀스 연결", "틀", Pf(0), A(ArgKind.SeqId), A(ArgKind.SeqTable)),
        E("SEQ_MAP_END", F, St, "SEQ_MAP_END", "대응표 끝", "틀"),
        E("ACT_MAP_BEGIN", F, St, "ACT_MAP_BEGIN( 이름 )", "ACT 대응표", "틀", A(ArgKind.Ident)),
        E("ACT_MAP", F, St, "ACT_MAP( 프로파일, ACT ID, 표 이름 )", "ACT 대응", "틀", Pf(0), A(ArgKind.ActId), A(ArgKind.ActTable)),
        E("ACT_MAP_END", F, St, "ACT_MAP_END", "ACT 대응표 끝", "틀"),
        E("MANUAL_BEGIN", F, St, "MANUAL_BEGIN( 이름 )", "수동 표", "틀", A(ArgKind.Ident)),
        E("MANUAL_END", F, St, "MANUAL_END", "수동 표 끝", "틀"),
        E("TAG_TABLE_BEGIN", F, St, "TAG_TABLE_BEGIN( 이름 )", "태그 표", "틀", A(ArgKind.Ident)),
        E("TAG_TABLE_END", F, St, "TAG_TABLE_END", "태그 표 끝", "틀"),
        E("ANY_PROFILE", F, V, "ANY_PROFILE", "모든 프로파일", "틀"),

        // ---- 4.2 입력 조건 (8) + v0.58.0 ----
        E("ON", I, Pt, "ON( 신호… )", "켜짐", "입력", A(ArgKind.Signal).Many(1, 16)),
        E("OFF", I, Pt, "OFF( 신호… )", "꺼짐", "입력", A(ArgKind.Signal).Many(1, 16)),
        E("ANY_ON", I, Pt, "ANY_ON( 신호… )", "하나라도", "입력", A(ArgKind.Input).Many(1, 16)),
        E("IN", I, Pt, "IN( 묶음… )", "모두", "입력", A(ArgKind.InGroup).Many(1, 8)),
        E("NO_IN", I, V, "NO_IN", "조건 없음", "입력"),
        E("ANY2_ON", I, Pt, "ANY2_ON( 신호… )", "하나라도", "입력", A(ArgKind.Input).Many(1, 16)),
        E("IN_ACTIVE_HIGH", I, Pt, "IN_ACTIVE_HIGH( 입력 )", "반대 극성 입력", "입력", A(ArgKind.CoreInput)),
        E("UNMATCHED", I, Pt, "UNMATCHED( 시간 )", "불일치 시간만", "입력", new ArgSpec(ArgKind.Time, 0, 32767, "틱")),
        E("AND", I, Pt, "AND( 항목… )", "모두", "입력", A(ArgKind.AndItem).Many(1, 16)).Added(),
        E("ANY", I, Pt, "ANY( 신호… )", "하나라도", "입력", A(ArgKind.Input).Many(1, 16)).Added(),
        E("IN_INVERTED", I, Pt, "IN_INVERTED( 입력 )", "반대 극성 입력", "입력", A(ArgKind.CoreInput)).Added(),
        E("PARAM_TIME", I, Pt, "PARAM_TIME( 파라미터, 단위 )", "파라미터 제한", "입력", A(ArgKind.ParamAny), W(TimeUnits)).Added(),

        // ---- 4.3 확장 조건 (22) ----
        E("X_ALWAYS", XC, Pt, "X_ALWAYS", "조건 없음", "차량 상태"),
        E("X_FORWARD", XC, Pt, "X_FORWARD", "전진 중", "차량 상태"),
        E("X_BACKWARD", XC, Pt, "X_BACKWARD", "후진 중", "차량 상태"),
        E("X_FORBACK_MODE", XC, Pt, "X_FORBACK_MODE", "전후진 모드", "차량 상태"),
        E("X_LOW_BATTERY", XC, Pt, "X_LOW_BATTERY", "저전압", "차량 상태"),
        E("X_CROSS_IN", XC, Pt, "X_CROSS_IN", "교통제어 진입 요청 중", "차량 상태"),
        E("X_CROSS_OUT", XC, Pt, "X_CROSS_OUT", "교통제어 진출 요청 중", "차량 상태"),
        E("X_CROSS_IN_ON", XC, Pt, "X_CROSS_IN_ON", "교통제어 진입 대기 중", "차량 상태"),
        E("X_CROSS_OUT_ON", XC, Pt, "X_CROSS_OUT_ON", "교통제어 진출 대기 중", "차량 상태"),
        E("X_ERR_EQ", XC, Pt, "X_ERR_EQ( 코드 )", "오류 번호 =", "차량 상태", U16),
        E("X_PARAM_EQ", XC, Pt, "X_PARAM_EQ( 파라미터, 값 )", "파라미터 =", "차량 상태", A(ArgKind.CoreParam), N(-32768, 65535)),
        E("X_PARAM_GE", XC, Pt, "X_PARAM_GE( 파라미터, 값 )", "파라미터 ≥", "차량 상태", A(ArgKind.CoreParam), N(-32768, 65535)),
        E("X_PARAM_LE", XC, Pt, "X_PARAM_LE( 파라미터, 값 )", "파라미터 ≤", "차량 상태", A(ArgKind.CoreParam), N(-32768, 65535)),
        E("X_ID", XC, Pt, "X_ID( 처음, 끝 )", "호기 범위", "차량 상태", Id, Id),
        E("X_KEY", XC, Pt, "X_KEY( 키 )", "키 눌림", "차량 상태", W(Keys)),
        E("X_STATION", XC, Pt, "X_STATION( 칸, 하한, 상한 )", "스테이션 범위", "차량 상태", Slot, U16, U16),
        E("X_STATION_RUNNING", XC, Pt, "X_STATION_RUNNING( 칸, 하한, 상한 )", "스테이션 범위·주행 중", "차량 상태", Slot, U16, U16),
        E("X_STATION_MOD", XC, Pt, "X_STATION_MOD( 칸, n, m )", "스테이션 나머지", "차량 상태", Slot, N(1, 65535), U16),
        E("X_STATION_DIV", XC, Pt, "X_STATION_DIV( 칸, n, m )", "스테이션 몫", "차량 상태", Slot, N(1, 65535), U16),
        E("X_TAG_NUM", XC, Pt, "X_TAG_NUM( 식 )", "태그 번호 = 식", "차량 상태", A(ArgKind.StationExpr)),
        E("X_PARAM", XC, Pt, "X_PARAM( FND 이름, 값 )", "파라미터 =", "차량 상태", A(ArgKind.FndParam), N(-32768, 65535)),
        E("X_NOT", XC, Pt, "X_NOT( 차량 상태 )", "반전", "차량 상태", X),

        // ---- v0.58.0 상태 낱말(AND 안, 옛 X_ 이름 대응. 헤더 dsl_and.h) ----
        E("ALWAYS", SC, Pt, "ALWAYS", "조건 없음", "차량 상태").Added(),
        E("FORWARD", SC, Pt, "FORWARD", "전진 중", "차량 상태").Added(),
        E("BACKWARD", SC, Pt, "BACKWARD", "후진 중", "차량 상태").Added(),
        E("FORBACK_MODE", SC, Pt, "FORBACK_MODE", "전후진 모드", "차량 상태").Added(),
        E("LOW_BATTERY", SC, Pt, "LOW_BATTERY", "저전압", "차량 상태").Added(),
        E("CROSS_IN", SC, Pt, "CROSS_IN", "교통제어 진입 요청 중", "차량 상태").Added(),
        E("CROSS_OUT", SC, Pt, "CROSS_OUT", "교통제어 진출 요청 중", "차량 상태").Added(),
        E("CROSS_IN_WAIT", SC, Pt, "CROSS_IN_WAIT", "교통제어 진입 대기 중", "차량 상태").Added(),
        E("CROSS_OUT_WAIT", SC, Pt, "CROSS_OUT_WAIT", "교통제어 진출 대기 중", "차량 상태").Added(),
        E("ERR_IS", SC, Pt, "ERR_IS( 코드 )", "오류 번호 =", "차량 상태", U16).Added(),
        E("PARAM", SC, Pt, "PARAM( FND 이름, 값 )", "파라미터 =", "차량 상태", A(ArgKind.FndParam), N(-32768, 65535)).Added(),
        E("PARAM_MIN", SC, Pt, "PARAM_MIN( FND 이름, 값 )", "파라미터 ≥", "차량 상태", A(ArgKind.FndParam), N(-32768, 65535)).Added(),
        E("PARAM_MAX", SC, Pt, "PARAM_MAX( FND 이름, 값 )", "파라미터 ≤", "차량 상태", A(ArgKind.FndParam), N(-32768, 65535)).Added(),
        E("ID", SC, Pt, "ID( 처음, 끝 )", "호기 범위", "차량 상태", Id, Id).Added(),
        E("KEY", SC, Pt, "KEY( 키 )", "키 눌림", "차량 상태", W(Keys)).Added(),
        E("STATION", SC, Pt, "STATION( 칸, 하한, 상한 )", "스테이션 범위", "차량 상태", Slot, U16, U16).Added(),
        E("STATION_MOD", SC, Pt, "STATION_MOD( 칸, n, m )", "스테이션 나머지", "차량 상태", Slot, N(1, 65535), U16).Added(),
        E("STATION_DIV", SC, Pt, "STATION_DIV( 칸, n, m )", "스테이션 몫", "차량 상태", Slot, N(1, 65535), U16).Added(),
        E("TAG_IS", SC, Pt, "TAG_IS( 식 )", "태그 번호 = 식", "차량 상태", A(ArgKind.StationExpr)).Added(),
        E("NOT", SC, Pt, "NOT( 상태 )", "반전", "차량 상태", A(ArgKind.StateCond)).Added(),

        // ---- 4.4 시퀀스·ACT 스텝 (62) ----
        // 라벨·분기(12)
        E("L", S, St, "L( 라벨 )", "라벨", "분기", Lb),
        E("GOTO", S, St, "GOTO( 라벨 )", "GOTO", "분기", Lb),
        E("SELECT_BEGIN", S, St, "SELECT_BEGIN", "고르기 시작(대기)", "분기"),
        E("WHEN", S, St, "WHEN( 조건, 라벨 )", "WHEN", "분기", AndC, Lb),
        E("WHEN_STATE", S, St, "WHEN_STATE( 상태, 값, 라벨 )", "WHEN 상태값", "분기", A(ArgKind.State), U16, Lb),
        E("WHEN_IF", S, St, "WHEN_IF( 차량 상태, 라벨 )", "WHEN", "분기", X, Lb).Alias(),
        E("WHEN_IF_IN", S, St, "WHEN_IF_IN( 차량 상태, 조건, 라벨 )", "WHEN", "분기", X, Cond, Lb).Alias(),
        E("WHEN_ANY2", S, St, "WHEN_ANY2( 조건, ANY2_ON( … ), 라벨 )", "WHEN", "분기", Cond, A(ArgKind.Any2), Lb).Alias(),
        E("WHEN_ID", S, St, "WHEN_ID( 처음, 끝, 라벨 )", "WHEN", "분기", Id, Id, Lb),
        E("OTHERWISE", S, St, "OTHERWISE( 라벨 )", "그 밖", "분기", Lb),
        E("SELECT_END", S, St, "SELECT_END", "고르기 끝", "분기"),
        E("SELECT_END_TO", S, St, "SELECT_END_TO( 시간, 라벨 )", "고르기 제한", "분기", T, Lb),
        // 출력(6)
        E("OUT_ON", S, St, "OUT_ON( 출력… )", "출력 켬", "출력", A(ArgKind.Output).Many(1, 16)),
        E("OUT_OFF", S, St, "OUT_OFF( 출력… )", "출력 끔", "출력", A(ArgKind.Output).Many(1, 16)),
        E("OUT_SET", S, St, "OUT_SET( 켬, 끔 )", "출력 켬·끔", "출력", A(ArgKind.OutMask), A(ArgKind.OutMask)).Added(),
        E("OUT_PULSE", S, St, "OUT_PULSE( 출력, 켬, 끔, 횟수 )", "펄스", "출력", A(ArgKind.Output), T, T, U16),
        E("OUT_FLICKER_UNTIL_IN", S, St, "OUT_FLICKER_UNTIL_IN( 출력, 간격, 조건 )", "점멸", "출력", A(ArgKind.Output), T, AndC),
        E("OUT_FLICKER_UNTIL_IN_TO", S, St, "OUT_FLICKER_UNTIL_IN_TO( 출력, 간격, 조건, 제한, 라벨 )", "점멸·제한", "출력", A(ArgKind.Output), T, AndC, TO, Lb),
        E("HANDSHAKE", S, St, "HANDSHAKE( 출력, 응답 입력, 제한, 라벨 )", "출력 켬 → 응답 대기", "출력", A(ArgKind.Output), A(ArgKind.Input), TO, Lb).Word("출력 안 끔"),
        // 벨트(6)
        E("OUTS", S, Pt, "OUTS( 출력… )", "출력 묶음", "벨트", A(ArgKind.Output).Many(1, 16)),
        E("NO_OUT", S, V, "NO_OUT", "출력 없음", "벨트"),
        E("BELT_ON", S, St, "BELT_ON( 벨트, 방향 )", "벨트 켬", "벨트", A(ArgKind.Belt), W(Rot)),
        E("BELT_ON", S, St, "BELT_ON( 벨트, 방향, UNTIL( 조건 ) )", "벨트 켬", "벨트", A(ArgKind.Belt), W(Rot), A(ArgKind.Until)).NoAct(),
        E("UNTIL", S, Pt, "UNTIL( 조건 )", "정지 조건", "벨트", Cond),
        E("BELT_OFF", S, St, "BELT_OFF( 벨트 )", "벨트 끔", "벨트", A(ArgKind.Belt)),
        E("WAIT_BELTS", S, St, "WAIT_BELTS( 벨트… )", "벨트 대기", "벨트", A(ArgKind.Belt).Many(1, 3)),
        // 대기(14)
        E("WAIT", S, St, "WAIT( 조건 )", "대기", "대기", AndC),
        E("WAIT_TO", S, St, "WAIT_TO( 조건, 제한, 라벨 )", "대기·제한", "대기", AndC, TO, Lb),
        E("WAIT_TO", S, St, "WAIT_TO( 조건, PARAM_TIME( 파라미터, 단위 ), 라벨 )", "대기·제한(파라미터)", "대기", AndC, A(ArgKind.ParamTime), Lb).Added().Form("파라미터"),
        E("WAIT_STABLE", S, St, "WAIT_STABLE( 조건, 안정 )", "유지 대기", "대기", AndC, T),
        E("WAIT_STABLE_TO", S, St, "WAIT_STABLE_TO( 조건, 안정, 제한, 라벨 )", "유지 대기·제한", "대기", AndC, T, TO, Lb),
        E("WAIT_OUT", S, St, "WAIT_OUT( 조건 )", "출력 대기", "대기", A(ArgKind.OutCond)),
        E("DELAY", S, St, "DELAY( 시간 )", "지연", "대기", T),
        E("WAIT_IF", S, St, "WAIT_IF( 차량 상태 )", "대기", "대기", X).Alias(),
        E("WAIT_IF_TO", S, St, "WAIT_IF_TO( 차량 상태, 제한, 라벨 )", "대기·제한", "대기", X, TO, Lb).Alias(),
        E("WAIT_IF_IN", S, St, "WAIT_IF_IN( 차량 상태, 조건 )", "대기", "대기", X, Cond).Alias(),
        E("WAIT_IF_IN_TO", S, St, "WAIT_IF_IN_TO( 차량 상태, 조건, 제한, 라벨 )", "대기·제한", "대기", X, Cond, TO, Lb).Alias(),
        E("WAIT_ANY2", S, St, "WAIT_ANY2( 조건, ANY2_ON( … ) )", "대기", "대기", Cond, A(ArgKind.Any2)).Alias(),
        E("WAIT_ANY2_TO", S, St, "WAIT_ANY2_TO( 조건, ANY2_ON( … ), 제한, 라벨 )", "대기·제한", "대기", Cond, A(ArgKind.Any2), TO, Lb).Alias(),
        E("DELAY_PARAM", S, St, "DELAY_PARAM( 파라미터, 단위 )", "파라미터 지연", "대기", A(ArgKind.CoreParam), W(TimeUnits)),
        E("WAIT_TO_PARAM", S, St, "WAIT_TO_PARAM( 조건, 파라미터, 단위, 라벨 )", "대기·제한(파라미터)", "대기", Cond, A(ArgKind.CoreParam), W(TimeUnits), Lb).Alias(),
        E("WAIT_PARAM_TO", S, St, "WAIT_PARAM_TO( 조건, 파라미터, 단위, 라벨 )", "대기·제한(파라미터)", "대기", Cond, A(ArgKind.CoreParam), W(TimeUnits), Lb).Added().Alias(),
        // ACS·스테이션(7)
        E("STATION", S, Pt, "STATION( k )", "스테이션[k]", "보고", Slot),
        E("MOD", S, Pt, "MOD( STATION(k), m )", "나머지", "보고", A(ArgKind.StationExpr), N(1, 255)),
        E("ACS", S, St, "ACS( 종류, 번호 )", "보고", "보고", W(Reports), new ArgSpec(ArgKind.AcsSource, 0, 65535)).Word("STANDBY 정지 없음"),
        E("ACS_SEND", S, St, "ACS_SEND( 종류, 칸 )", "보고", "보고", W(Reports), new ArgSpec(ArgKind.AcsStation, 0, 23)).Alias().Form("스테이션"),
        E("ACS_SEND", S, St, "ACS_SEND( 종류, 번호 )", "보고", "보고", W(Reports), new ArgSpec(ArgKind.AcsNumber, 0, 65535)).Alias(),
        E("ACS_WAIT_ACK", S, St, "ACS_WAIT_ACK( 제한, 라벨 )", "응답 대기", "보고", TO, Lb),
        E("ACS_STATION", S, St, "ACS_STATION( 칸, 값, 길이 )", "스테이션 넣기", "보고", Slot, U16, U16),
        E("STATION_RESET", S, St, "STATION_RESET", "스테이션 초기화", "보고"),
        // 주행(7, ACT 금지)
        E("AGV_SPEED", S, St, "AGV_SPEED( 단계 )", "속도(단계·m/min)", "주행", W(SpeedSteps)).NoAct(),
        E("AGV_SPEED", S, St, "AGV_SPEED( 속도 )", "속도(단계·m/min)", "주행", N(4, 255, "m/min")).NoAct().Added().Form("m/min"),
        E("AGV_SPEED_MMIN", S, St, "AGV_SPEED_MMIN( 속도 )", "속도(단계·m/min)", "주행", N(4, 255, "m/min")).NoAct().Alias(),
        E("AGV_FORBACK", S, St, "AGV_FORBACK( 방향 )", "전후진", "주행", W(ForBack)).NoAct(),
        E("AGV_TURN", S, St, "AGV_TURN( 방향, 각도, 회전 뒤, 자동 출발[, 속도[, 옵션[, 옵션]]] )", "회전", "주행",
            W(Rot), N(1, 359, "도"), N(0, 15), N(0, 15).Sw(), N(0, 31, "m/min").Opt(), W(TurnOptSeq).Opt(), W(TurnOptSeq).Opt()).NoAct(),
        E("AGV_MOVE", S, St, "AGV_MOVE( cm )", "이동", "주행", N(1, 3276, "cm")).NoAct(),
        E("AGV_MOVE", S, St, "AGV_MOVE( 한계 cm, 옵션 )", "이동", "주행", N(1, 3276, "cm"), W(MoveOpt)).NoAct(),
        E("ERROR_HOLD", S, St, "ERROR_HOLD( 코드, 조건, 라벨 )", "오류 표시·조건 대기", "주행", U16, AndC, Lb).NoAct(),
        E("END_RESTART", S, St, "END_RESTART", "끝·재출발", "주행").NoAct(),
        // 공통(10)
        E("OBS_ON", S, St, "OBS_ON", "장애물 감지 켬", "공통"),
        E("OBS_OFF", S, St, "OBS_OFF", "장애물 감지 끔", "공통"),
        E("OBS_OFF_AUTO", S, St, "OBS_OFF_AUTO", "장애물 감지 일시 중지", "공통"),
        E("SET_STATE", S, St, "SET_STATE( 상태, 값 )", "상태 설정", "공통", A(ArgKind.State), U16),
        E("WAIT_STATE", S, St, "WAIT_STATE( 상태, 값 )", "상태 대기", "공통", A(ArgKind.State), U16),
        E("ACT_START", S, St, "ACT_START( ACT ID )", "ACT 시작", "공통", A(ArgKind.ActId)),
        E("WAIT_ACT", S, St, "WAIT_ACT( ACT ID )", "ACT 대기", "공통", A(ArgKind.ActId)),
        E("CALL", S, St, "CALL( 번호 )", "사용자 함수", "공통", A(ArgKind.CallNo)),
        E("END_STOP", S, St, "END_STOP", "끝(재출발 없음)", "공통"),
        E("END_ERROR", S, St, "END_ERROR( 코드 )", "오류 끝", "공통", U16),

        // ---- 4.5 수동 표 (5) ----
        E("MAN", M, St, "MAN( 프로파일, 모드, 조건, 출력… )", "가지", "수동", Pf(1), W(Modes), A(ArgKind.ManCond), A(ArgKind.ManOut).Many(1, 16)),
        E("MAN_ELSE", M, St, "MAN_ELSE( 프로파일, 모드, 출력… )", "사슬 끝", "수동", Pf(1), W(Modes), A(ArgKind.ManOut).Many(1, 16)),
        E("BELT", M, Pt, "BELT( 벨트, 방향 )", "벨트", "수동", A(ArgKind.Belt), W(BeltMan)),
        E("MAN_FB_SW", M, St, "MAN_FB_SW( 프로파일, 입력 )", "방향 스위치", "수동", Pf(1), A(ArgKind.Input)),
        E("MAN_OBS_B", M, St, "MAN_OBS_B( 프로파일, 입력 )", "후방 장애물", "수동", Pf(1), A(ArgKind.Input)),

        // ---- 4.6 태그 규칙(4) ----
        E("TAG", TR, St, "TAG( 태그, 프로파일, 동작… )", "태그", "태그", N(0, 1023, "태그"), Pf(0), A(ArgKind.Action).Many(1, 16)),
        E("TAG_IF", TR, St, "TAG_IF( 태그, 프로파일, 조건, 동작… )", "태그 조건", "태그", N(0, 1023, "태그"), Pf(0), A(ArgKind.TagCond), A(ArgKind.Action).Many(1, 16)),
        E("TAG_RANGE", TR, St, "TAG_RANGE( 처음, 끝, 프로파일, 동작… )", "태그 범위", "태그", N(0, 1023, "태그"), N(0, 1023, "태그"), Pf(0), A(ArgKind.Action).Many(1, 16)),
        E("TAG_RANGE_IF", TR, St, "TAG_RANGE_IF( 처음, 끝, 프로파일, 조건, 동작… )", "태그 범위 조건", "태그", N(0, 1023, "태그"), N(0, 1023, "태그"), Pf(0), A(ArgKind.TagCond), A(ArgKind.Action).Many(1, 16)),

        // ---- 4.6 조건(16) ----
        E("IF_X", TC, Pt, "IF_X( 차량 상태 )", "모두", "조건", X),
        E("IF_X2", TC, Pt, "IF_X2( 차량 상태, 차량 상태 )", "모두", "조건", X, X),
        E("IF_IN", TC, Pt, "IF_IN( 조건 )", "모두", "조건", Cond),
        E("IF_X_IN", TC, Pt, "IF_X_IN( 차량 상태, 조건 )", "모두", "조건", X, Cond),
        E("IF_X2_IN", TC, Pt, "IF_X2_IN( 차량 상태, 차량 상태, 조건 )", "모두", "조건", X, X, Cond),
        E("IF_FORBACK_MODE", TC, Pt, "IF_FORBACK_MODE", "전후진 모드", "조건"),
        E("IF_LOW_BATTERY", TC, Pt, "IF_LOW_BATTERY", "저전압", "조건"),
        E("IF_FORWARD", TC, Pt, "IF_FORWARD", "전진 중", "조건"),
        E("IF_BACKWARD", TC, Pt, "IF_BACKWARD", "후진 중", "조건"),
        E("IF_STATION", TC, Pt, "IF_STATION( 칸, 하한, 상한 )", "스테이션 범위", "조건", Slot, U16, U16),
        E("IF_STATION_RUNNING", TC, Pt, "IF_STATION_RUNNING( 칸, 하한, 상한 )", "스테이션 범위·주행 중", "조건", Slot, U16, U16),
        E("IF_ID", TC, Pt, "IF_ID( 처음, 끝 )", "호기 범위", "조건", Id, Id),
        E("IF_STATION_MOD", TC, Pt, "IF_STATION_MOD( 칸, n, m )", "스테이션 나머지", "조건", Slot, N(1, 65535), U16),
        E("IF_STATION_DIV", TC, Pt, "IF_STATION_DIV( 칸, n, m )", "스테이션 몫", "조건", Slot, N(1, 65535), U16),
        E("IF_TAG_NUM", TC, Pt, "IF_TAG_NUM( 식 )", "태그 번호 = 식", "조건", A(ArgKind.StationExpr)),
        E("IF_PARAM", TC, Pt, "IF_PARAM( FND 이름, 값 )", "파라미터 =", "조건", A(ArgKind.FndParam), N(-32768, 65535)),

        // ---- 4.7 태그 전용 이름(10) ----
        E("AGV_STOP", TA, Pt, "AGV_STOP", "정지", "동작").Cells(1),
        E("AGV_STOP", TA, Pt, "AGV_STOP( cm )", "정지", "동작", N(0, 255, "cm")).Cells(1).Word("정지 중 무시"),
        E("AGV_STOP", TA, Pt, "AGV_STOP( TAPE )", "정지", "동작", W("TAPE")).Cells(1),
        E("AGV_STOP", TA, Pt, "AGV_STOP( TAPE_GD )", "정지", "동작", W("TAPE_GD")).Cells(1),
        E("AGV_STOP", TA, Pt, "AGV_STOP( 거리, FROM_SENSOR( 입력, 유지틱 ) )", "정지", "동작", new ArgSpec(ArgKind.StopDist, 0, 255, "cm"), A(ArgKind.FromSensor)).Cells(2),
        E("FROM_SENSOR", TA, Pt, "FROM_SENSOR( 입력, 유지틱 )", "센서", "동작", new ArgSpec(ArgKind.Input, 0, 63), N(1, 511, "틱")),
        E("AGV_AUTOSTART", TA, Pt, "AGV_AUTOSTART( 초 )", "자동 출발", "동작", N(0, 255, "초")).Cells(1).Word("정지 중 무시"),
        E("AGV_AUTOSTART_PARAM", TA, Pt, "AGV_AUTOSTART_PARAM", "자동 출발 AS", "동작").Cells(1).Word("정지 중 무시"),
        E("AGV_BRANCH", TA, Pt, "AGV_BRANCH( 방향 )", "분기", "동작", W(Branch)).Cells(1),
        E("SEQ_START", TA, Pt, "SEQ_START( 시퀀스 )", "정지·시퀀스", "동작", A(ArgKind.SeqShort)).Cells(1).Word("도는 중 출발은 끝난 뒤"),
        E("SEQ_START", TA, Pt, "SEQ_START( cm, 시퀀스 )", "정지·시퀀스", "동작", N(0, 255, "cm"), A(ArgKind.SeqShort)).Cells(2),
        E("SEQ_START", TA, Pt, "SEQ_START( TAPE, 시퀀스 )", "정지·시퀀스", "동작", W("TAPE"), A(ArgKind.SeqShort)).Cells(1),
        E("SEQ_START", TA, Pt, "SEQ_START( TAPE_GD, 시퀀스 )", "정지·시퀀스", "동작", W("TAPE_GD"), A(ArgKind.SeqShort)).Cells(1),
        E("AGV_ID_MINUS", TA, Pt, "AGV_ID_MINUS( n )", "호기 − n", "동작", N(0, 255)),
        E("TAG_NUM", TA, V, "TAG_NUM", "태그 번호", "동작"),
        E("AGV_WAIT_START", TA, Pt, "AGV_WAIT_START", "출발 대기 걸기", "동작").Cells(1),
        E("AGV_WAIT_CANCEL", TA, Pt, "AGV_WAIT_CANCEL", "출발 대기 풀기", "동작").Cells(1),
        E("NO_ACTION", TA, V, "NO_ACTION", "동작 없음", "동작").Cells(1).Added(),

        // ---- 4.7 시퀀스와 같은 이름(12, 태그 표에서의 뜻) ----
        E("AGV_SPEED", TA, Pt, "AGV_SPEED( 단계 )", "속도(단계·m/min)", "동작", W(SpeedSteps)).Cells(1),
        E("AGV_SPEED", TA, Pt, "AGV_SPEED( 속도 )", "속도(단계·m/min)", "동작", N(4, 255, "m/min")).Cells(1).Added().Form("m/min"),
        E("AGV_SPEED_MMIN", TA, Pt, "AGV_SPEED_MMIN( 속도 )", "속도(단계·m/min)", "동작", N(4, 255, "m/min")).Cells(1).Alias(),
        E("AGV_FORBACK", TA, Pt, "AGV_FORBACK( 방향 )", "전후진", "동작", W(ForBack)).Cells(1),
        E("AGV_TURN", TA, Pt, "AGV_TURN( 방향, 각도, 회전 뒤, 자동 출발[, 속도[, 옵션[, 옵션]]] )", "회전", "동작",
            W(Rot), N(5, 315, "도"), N(0, 15), N(0, 15).Sw(), N(0, 31, "m/min").Opt(), W(TurnOptTag).Opt(), W(TurnOptTag).Opt()).Cells(2),
        E("ACS_SEND", TA, Pt, "ACS_SEND( 종류, 칸 )", "보고", "동작", W(ReportsTag), new ArgSpec(ArgKind.AcsStation, 0, 23)).Cells(2).Alias().Form("스테이션"),
        E("ACS_SEND", TA, Pt, "ACS_SEND( 종류, 번호 )", "보고", "동작", W(ReportsTag), new ArgSpec(ArgKind.AcsNumber, 0, 255)).Cells(2).Alias(),
        E("ACS", TA, Pt, "ACS( 종류, STATION0 )", "보고", "동작", W(Reports), W("STATION0")).Cells(1).Word("STANDBY 정지 중엔 정지 없음"),
        E("ACS", TA, Pt, "ACS( 종류, 칸 )", "보고", "동작", W(ReportsTag), new ArgSpec(ArgKind.AcsStation, 0, 23)).Cells(2).Added().Word("STANDBY 정지 중엔 정지 없음"),
        E("ACS", TA, Pt, "ACS( 종류, 번호 )", "보고", "동작", W(ReportsTag), new ArgSpec(ArgKind.AcsNumber, 0, 255)).Cells(2).Added().Form("숫자").Word("STANDBY 정지 중엔 정지 없음"),
        E("STATION_RESET", TA, Pt, "STATION_RESET", "스테이션 초기화", "동작").Cells(1),
        E("OBS_ON", TA, Pt, "OBS_ON", "장애물 감지 켬", "동작").Cells(1),
        E("OBS_OFF", TA, Pt, "OBS_OFF", "장애물 감지 끔", "동작").Cells(1),
        E("OBS_OFF_AUTO", TA, Pt, "OBS_OFF_AUTO", "장애물 감지 일시 중지", "동작").Cells(1),
        E("ACT_START", TA, Pt, "ACT_START( ACT ID )", "ACT 시작", "동작", new ArgSpec(ArgKind.ActId, 0, 255)).Cells(1),
        E("CALL", TA, Pt, "CALL( 번호 )", "사용자 함수", "동작", new ArgSpec(ArgKind.CallNo, 0, 255)).Cells(1),

        // ---- 4.8 압축 태그 표 (13, 생성물 전용) ----
        E("TAG_X_BEGIN", PK, St, "TAG_X_BEGIN( 이름 )", "확장 칸 표", "압축", A(ArgKind.Ident)),
        E("TAG_X", PK, St, "TAG_X( 차량 상태 )", "확장 칸", "압축", X),
        E("TAG_IN_BEGIN", PK, St, "TAG_IN_BEGIN( 이름 )", "입력 칸 표", "압축", A(ArgKind.Ident)),
        E("TAG_IN", PK, St, "TAG_IN( 조건 )", "입력 칸", "압축", Cond),
        E("TAG_ACT_BEGIN", PK, St, "TAG_ACT_BEGIN( 이름 )", "동작 칸 표", "압축", A(ArgKind.Ident)),
        E("TAG_ACTS", PK, St, "TAG_ACTS( 칸 넷 )", "동작 칸", "압축", U16, U16, U16, U16),
        E("TAGP_BEGIN", PK, St, "TAGP_BEGIN( 이름 )", "규칙 표", "압축", A(ArgKind.Ident)),
        E("TAGP", PK, St, "TAGP( 태그, 프로파일, 조건 1 번호, 조건 2 번호, 입력 번호, 동작 번호 )", "규칙", "압축",
            N(0, 1023), N(0, 63), N(0, 255), N(0, 255), N(0, 255), N(0, 255)),
        E("TAG_POOL_END", PK, St, "TAG_POOL_END", "칸 표 끝", "압축"),
        E("TAG_PACKED", PK, St, "TAG_PACKED( 이름, 규칙, 조건, 입력, 동작 )", "묶음", "압축",
            A(ArgKind.Ident), A(ArgKind.Ident), A(ArgKind.Ident), A(ArgKind.Ident), A(ArgKind.Ident)),
        E("TAG_RANGES_BEGIN", PK, St, "TAG_RANGES_BEGIN( 이름 )", "범위 칸 표", "압축", A(ArgKind.Ident)),
        E("TAG_RANGE_P", PK, St, "TAG_RANGE_P( 규칙 번호, 끝 태그 )", "범위 칸", "압축", N(0, 1023), N(0, 1023)),
        E("TAG_PACKED_R", PK, St, "TAG_PACKED_R( 이름, 규칙, 조건, 입력, 동작, 범위 )", "범위 묶음", "압축",
            A(ArgKind.Ident), A(ArgKind.Ident), A(ArgKind.Ident), A(ArgKind.Ident), A(ArgKind.Ident), A(ArgKind.Ident)),
    ];

    static readonly ILookup<string, GrammarEntry> byName = Rows.ToLookup(r => r.Name, StringComparer.Ordinal);

    /// <summary>공개 이름(중복 없이). 사양 4.9절 163개(v0.54.0) + v0.58.0 에서 늘어난 이름.</summary>
    public static readonly IReadOnlyList<string> Names = Rows.Select(r => r.Name).Distinct().ToList();

    /// <summary>v0.54.0 부터 있던 이름. 코어 이름 겹침 검사(N-5)는 이 이름만 본다: v0.58.0 이름(<c>FORWARD</c>·<c>ID</c>·<c>NO_ACTION</c> 등)은 사이트가 같은 이름을 정의해도 쓰기 전에는 안 깨진다.</summary>
    public static readonly IReadOnlySet<string> OldNames = Rows.Where(r => r.Since.Length == 0).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

    public static IEnumerable<GrammarEntry> Find(string name) => byName[name];

    public static IEnumerable<GrammarEntry> Find(string name, Place place) => byName[name].Where(r => r.Place == place);
}
