using System.Security.Cryptography;
using System.Text;
using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

/// <summary>
/// 검사 규칙 시험: T900_DEMO 를 메모리에 읽어 한 군데를 고치고 그 규칙이 걸리는지 본다(디스크의 fixture 는 건드리지 않는다).
/// 코어 규칙(V-*)은 <c>$env:TEMP</c> 아래 지어낸 core 폴더로 본다.
/// </summary>
static class CheckTests
{
    static readonly UTF8Encoding Utf8 = new(false);

    sealed record Edit(string File, string Find, string Replace);

    static string Fixture(string root) => Path.Combine(root, "tests", "fixtures", "T900_DEMO");

    static IReadOnlyList<Finding> Run(string root, string? core, params Edit[] edits)
    {
        var folder = Fixture(root);
        var files = new List<SourceFile>();
        foreach (var z in FileZones.Files)
        {
            var path = Path.Combine(folder, z.FileName);
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path, Utf8);
            foreach (var e in edits.Where(e => e.File == z.FileName))
            {
                if (e.Find.Length == 0) { text = e.Replace; continue; }
                int i = text.IndexOf(e.Find, StringComparison.Ordinal);
                if (i < 0) throw new InvalidOperationException($"{e.File} 에 없음: {e.Find}");
                text = text[..i] + e.Replace + text[(i + e.Find.Length)..];
            }
            files.Add(SourceFile.Parse(z.FileName, Utf8.GetBytes(text)));
        }
        return Checker.Run(Site.FromFiles(files, folder, core));
    }

    static Edit Tag(string line) => new("tag_table.c", "\nTAG_TABLE_END", "\n    " + line + "\nTAG_TABLE_END");

    static Edit Profile(string line) => new("profiles.c", "PROFILE_TABLE_END", "    " + line + "\r\nPROFILE_TABLE_END");

    static Edit Manual(string beforeLine, string line) => new("manual_table.c", beforeLine, "    " + line + "\r\n" + beforeLine);

    static Edit MapRow(string line) => new("seq_table.c", "SEQ_MAP_END", "    " + line + "\r\nSEQ_MAP_END");

    /// <summary>새 시퀀스 표 t_x(스텝 body)와 그 대응표 줄.</summary>
    static Edit[] Seq(string body, bool map = true)
    {
        var table = new Edit("seq_table.c", "SEQ_MAP_BEGIN( site_seqs )", "SEQ_BEGIN( t_x )\n" + body + "SEQ_END\n\nSEQ_MAP_BEGIN( site_seqs )");
        return map ? [table, MapRow("SEQ_MAP( 0,         SEQ_UNLOAD_LAST,    t_x )")] : [table];
    }

    static string Lines(string step, int n) => string.Concat(Enumerable.Repeat("    " + step + "\n", n));

    /// <summary>규칙마다 걸리는 예. (규칙, 등급, 고침, 글에 들어갈 낱말)</summary>
    static IEnumerable<(string Rule, Severity Sev, Edit[] Edits, string Text)> Cases()
    {
        const Severity E = Severity.Error, W = Severity.Warning;
        yield return ("R-1", W, [Tag("{ 41, PF_BELT, 0, 0, 0, { TA_STOP } },")], "원문 블록");
        yield return ("R-2", E, [new("tag_table.c", "\nTAG_TABLE_END", "\n")], "표지 짝");
        yield return ("N-1", E, [Tag("TAG( 200, ANY_PROFILE, AGV_TURN( CW, 90, FB, 1, 0, ALIGN ) )")], "후보 T_FB");
        yield return ("N-1", E, [Tag("TAG_IF( 200, ANY_PROFILE, IF_X( X_NOPE(1) ), AGV_STOP )")], "X_NOPE");
        yield return ("N-1", E, [Tag("TAG( 200, PF_BELTX, AGV_STOP )")], "후보 PF_BELT");
        yield return ("N-2", E, [Tag("TAG_IF( 201, ANY_PROFILE, IF_IN( ON(DEMO_CV1_PWR) ), AGV_STOP )")], "입력 자리");
        yield return ("N-2", E, [Tag("TAG_IF( 201, ANY_PROFILE, IF_X( X_ID(5) ), AGV_STOP )")], "인자 수");
        yield return ("N-2", E, [Manual("MANUAL_END", "MAN( PF_BOX, IDLE, IN( ON(DEMO_SEN_1) ), ON(DEMO_SEN_2) )"),
            Manual("MANUAL_END", "MAN_ELSE( PF_BOX, IDLE, NO_OUT )")], "출력 자리");
        yield return ("N-3", E, [Tag("TAG_IF( 202, ANY_PROFILE, IF_IN( ANY_ON(DEMO_SENH_1, DEMO_SEN_2) ), AGV_STOP )")], "핀 1 활성");
        yield return ("N-4", E, [new("io_map.h", "#define DEMO_SEN_4      IN_EXT_4", "#define DEMO_SEN_4      IN_EXT_4\n#define DEMO_SEN_1      IN_EXT_9")], "두 번 정의 DEMO_SEN_1");
        yield return ("N-4", E, [new("seq_table.c", "SEQ_LABELS( DONE, REQ, RUN, ERR, BELT_STOP )", "SEQ_LABELS( DONE, REQ, RUN, ERR, BELT_STOP, REQ )")], "두 번 정의 REQ");
        yield return ("N-5", E, [new("seq_table.c", "SEQ_LABELS( DONE, REQ, RUN, ERR, BELT_STOP )", "SEQ_LABELS( DONE, REQ, RUN, ERR, BELT_STOP, ON )")], "코어 이름과 겹침 ON");
        yield return ("N-5", E, [new("tag_table.c", "TAG_TABLE_BEGIN( site_tags )", "TAG_TABLE_BEGIN( my_tags )")], "site_tags");
        yield return ("N-7", W, [new("tag_table.c", "AGV_STOP( 30 ), AGV_AUTOSTART( 5 )", "AGV_STOP( 030 ), AGV_AUTOSTART( 5 )")], "앞자리 0 · 030 = 8진 24");
        yield return ("N-7", E, [new("tag_table.c", "AGV_STOP( 30 ), AGV_AUTOSTART( 5 )", "AGV_STOP( 08 ), AGV_AUTOSTART( 5 )")], "앞자리 0 · 08 8진 아님");
        yield return ("N-5", E, [new("io_map.h", "#define DEMO_LAMP       OUT_LAMP_Y", "#define DEMO_LAMP       OUT_LAMP_Y\n#define CW 1")], "코어 이름과 겹침 CW");
        yield return ("P-1", E, [new("profiles.c", "PROFILE( PF_BOX,            0, 30, 39  )", "PROFILE( PF_BOX,            0, 39, 30  )")], "처음 > 끝");
        yield return ("P-1", E, [Profile("PROFILE( PF_BOX, 1000, 40, 120 )")], "범위 밖");
        yield return ("P-2", E, [Profile("PROFILE( PF_BOX, 0, 31, 32 )")], "무시됨 · 중복 (4줄)");
        yield return ("P-2", E, [Profile("PROFILE( PF_BOX, 0, 5, 12 )")], "무시됨 · 중복 (1, 2줄)");
        yield return ("P-3", W, [Profile("PROFILE( PF_BOX, 0, 35, 45 )")], "호기 겹침 (4줄)");
        yield return ("P-4", E, [new("site.h", "PF_BELT = 1,", "PF_BELT = 64,")], "번호 범위 밖");
        yield return ("P-4", E, [new("site.h", "    PF_BOX\n", "    PF_BOX,\n    PF_TWIN = 2\n"), Profile("PROFILE( PF_TWIN, 0, 50, 50 )")], "번호 겹침 2");
        yield return ("P-5", W, [new("site.h", "    PF_BOX\n", "    PF_BOX,\n    PF_EXTRA\n")], "프로파일 줄 없음 PF_EXTRA");
        yield return ("P-6", W, [Profile("PROFILE( PF_LIFT, 0, 40, 41 )")], "적재 설정 다름");
        yield return ("I-1", E, [Tag("TAG_IF( 203, ANY_PROFILE, IF_IN( ON(IN_AMP_L_OVERLOAD) ), AGV_STOP )")], "신호 ID 밖 32");
        yield return ("I-2", E, [Tag("TAG_IF( 204, ANY_PROFILE, IF_IN( IN( ON(DEMO_SEN_1), OFF(DEMO_SEN_1) ) ), AGV_STOP )")], "같은 핀 반대");
        yield return ("I-2", E, [new("io_map.h", "#define DEMO_LIFT_TOP   IN_EXT_6", "#define DEMO_LIFT_TOP   IN_EXT_6\n#define DEMO_SEN_5      IN_EXT_5"),
            Tag("TAG_IF( 204, ANY_PROFILE, IF_IN( ON(DEMO_SENH_1, DEMO_SEN_5) ), AGV_STOP )")], "ID 4");
        yield return ("I-2", E, [Tag("TAG_IF( 204, ANY_PROFILE, IF_IN( IN( ANY_ON(DEMO_SEN_1, DEMO_SEN_2), OFF(DEMO_SEN_2) ) ), AGV_STOP )")], "ID 1");
        yield return ("I-3", E, [Tag("TAG_IF( 205, ANY_PROFILE, IF_IN( IN( ANY_ON(DEMO_SEN_1), ANY_ON(DEMO_SEN_2) ) ), AGV_STOP )")], "ANY_ON 둘");
        yield return ("I-4", E, [Tag("TAG_IF( 206, ANY_PROFILE, IF_IN( ON(" + string.Join(", ", Enumerable.Repeat("DEMO_SEN_1", 17)) + ") ), AGV_STOP )")], "신호 17개");
        yield return ("I-4", E, [Tag("TAG_IF( 206, ANY_PROFILE, IF_IN( IN( " + string.Join(", ", Enumerable.Repeat("ON(DEMO_SEN_1)", 7)) + " ) ), AGV_STOP )")], "묶음 7개");
        yield return ("I-5", E, [new("act_table.c", "BELT_ON( 2, CW )", "BELT_ON( 2, CCW )")], "없는 방향 2 CCW");
        yield return ("I-5", E, [new("io_map.h", "#define BELTLIFT_CCW    OUTS( DEMO_LIFT_DOWN )", "#define BELTLIFT_CCW    OUTS( DEMO_LIFT_DOWN )\n#define BELT7_CW        OUTS( DEMO_LAMP )")], "벨트 한쪽만 7");
        yield return ("I-6", W, [Tag("TAG_IF( 206, ANY_PROFILE, IF_IN( ON(DEMO_SPARE_IN) ), AGV_STOP )")], "미연결 신호 DEMO_SPARE_IN");
        yield return ("T-1", E, [Tag("TAG( 207, ANY_PROFILE, AGV_TURN( CW, 90, 0, 1, 0 ), AGV_STOP( TAPE ) )")], "회전 + 정지");
        yield return ("T-2", E, [Tag("TAG( 208, ANY_PROFILE, AGV_TURN( CW, 90, 0, 1, 0 ), AGV_TURN( CCW, 90, 0, 1, 0 ) )")], "회전 둘");
        yield return ("T-3", E, [Tag("TAG( 209, ANY_PROFILE, AGV_STOP( TAPE ), AGV_STOP( TAPE_GD ) )")], "교차선 정지 둘");
        yield return ("T-4", E, [Tag("TAG( 210, PF_BELT, SEQ_START( TAPE, LOAD ), SEQ_START( UNLOAD ) )")], "시퀀스 둘");
        yield return ("T-5", E, [Tag("TAG( 211, ANY_PROFILE, AGV_STOP( TAPE ), AGV_SPEED( LOW ) )")], "속도");
        yield return ("T-6", E, [Tag("TAG( 212, ANY_PROFILE, AGV_STOP( FL, FROM_SENSOR( DEMO_SEN_4, 100 ) ), AGV_STOP( TAPE ) )")], "센서 정지 + 교차선");
        yield return ("T-6", E, [Tag("TAG( 212, ANY_PROFILE, AGV_STOP( FL, FROM_SENSOR( DEMO_SEN_4, 100 ) ), AGV_TURN( CW, 90, 0, 1, 0, TAPE ) )")], "센서 정지 + 교차선");
        yield return ("T-7", E, [Tag("TAG( 213, ANY_PROFILE, OBS_ON, OBS_OFF, AGV_SPEED( LOW ), AGV_STOP( FL, FROM_SENSOR( DEMO_SEN_4, 100 ) ) )")], "넷째 칸");
        yield return ("T-8", E, [Tag("TAG( 214, ANY_PROFILE, AGV_TURN( CW, 90, 0, 1, 0 ), ACS_SEND( ARRIVE, 1 ), OBS_ON )")], "동작 칸 5");
        yield return ("T-9", E, [Tag("TAG( 215, ANY_PROFILE, AGV_TURN( CW, 92, 0, 1, 0 ) )")], "5의 배수");
        yield return ("T-9", E, [Tag("TAG( 215, ANY_PROFILE, AGV_TURN( CW, 0, 0, 1, 0 ) )")], "범위 밖 0");
        yield return ("T-9", E, [Tag("TAG_RANGE( 140, 130, PF_BELT, OBS_ON )")], "처음 > 끝");
        yield return ("T-9", E, [Tag("TAG( 216, ANY_PROFILE, ACS_SEND( ARRIVE, 256 ) )")], "범위 밖 256");
        yield return ("T-9", E, [Tag("TAG( 216, ANY_PROFILE, ACS_SEND( ARRIVE, STATION(24) ) )")], "범위 밖 24");
        yield return ("T-9", E, [Tag("TAG( 216, ANY_PROFILE, ACS_SEND( ARRIVE, STATION(1) + 200 ) )")], "범위 밖 200");
        yield return ("T-9", E, [Tag("TAG_IF( 216, PF_LIFT, IF_ID( 15, 12 ), OBS_ON )")], "처음 > 끝 IF_ID");
        yield return ("T-10", E, [Tag("TAG( 1, PF_BELT, OBS_ON )")], "무시됨 · 중복 (1줄)");
        yield return ("T-10", E, [Tag("TAG_IF( 50, ANY_PROFILE, IF_FORWARD, OBS_ON )")], "무시됨 · 중복");
        yield return ("T-10", E, [Tag("TAG( 110, PF_BELT, OBS_ON )")], "무시됨 · 중복");
        yield return ("T-11", E, [Tag("TAG_IF( 53, PF_LIFT, IF_ID( 11, 11 ), OBS_ON )")], "무시됨 · 중복");
        yield return ("T-11", E, [Tag("TAG_IF( 54, ANY_PROFILE, IF_X2( X_STATION(1, 71, 72), X_FORWARD ), OBS_ON )")], "무시됨 · 중복");
        yield return ("T-12", E, [Tag("TAG_IF( 217, PF_LIFT, IF_ID( 50, 51 ), OBS_ON )")], "호기 안 맞음 50~51");
        yield return ("T-13", E, [Tag("TAG( 218, PF_BELT, SEQ_START( TROLLEY_LOAD ) )")], "대응표 없음 TROLLEY_LOAD");
        yield return ("T-13", W, [Tag("TAG( 218, PF_BOX, SEQ_START( LOAD ) )")], "대응표 없음 LOAD · PF_BOX");
        yield return ("T-14", E, [Tag("TAG( 219, ANY_PROFILE, AGV_MOVE( 10 ) )")], "태그 표 금지 AGV_MOVE");
        yield return ("T-14", E, [Tag("TAG( 219, ANY_PROFILE, AGV_TURN( CW, 90, 0, 1, 0, NOWAIT ) )")], "NOWAIT");
        yield return ("T-15", E, [Tag(string.Join("\n    ", Enumerable.Range(0, 1025).Select(i => $"TAG_IF( 900, ANY_PROFILE, IF_ID( {i % 100}, {i % 100} ), OBS_ON )")))], "규칙");
        yield return ("T-15", E, [Tag(string.Join("\n    ", Enumerable.Range(0, 256).Select(i => $"TAG( 901, ANY_PROFILE, ACS_SEND( ARRIVE, {i % 256} ), STATION_RESET )")))], "동작 묶음");
        yield return ("T-16", W, [Tag("TAG( 220, ANY_PROFILE, AGV_TURN( CW, 90, 0, 1, 2 ) )")], "회전 속도 2");
        yield return ("T-17", E, [Tag("TAG( 221, ANY_PROFILE, ACT_START( 7 ) )")], "ACT 대응표 없음 7");
        yield return ("T-17", E, [Tag("TAG( 221, ANY_PROFILE, CALL( 9 ) )")], "함수 없음 9");
        yield return ("S-1", E, [new("seq_table.c", "GOTO( BELT_STOP )", "GOTO( NOPE )")], "선언 없는 라벨 NOPE");
        yield return ("S-2", E, Seq("    GOTO( RUN )\n"), "라벨 위치 없음 RUN");
        yield return ("S-3", W, Seq("    L( RUN )\n    END_STOP\n"), "안 쓰는 라벨 RUN");
        yield return ("S-3", W, [new("seq_table.c", "SEQ_LABELS( DONE, REQ, RUN, ERR, BELT_STOP )", "SEQ_LABELS( DONE, REQ, RUN, ERR, BELT_STOP, IDLE_LABEL )")], "안 쓰는 라벨 IDLE_LABEL");
        yield return ("S-4", E, Seq("    L( RUN )\n    L( RUN )\n    GOTO( RUN )\n"), "라벨 두 번 RUN (1줄)");
        yield return ("S-5", W, Seq("    END_STOP\n    OBS_ON\n    END_STOP\n"), "END_STOP 뒤");
        yield return ("S-5", W, Seq("    WHEN( NO_IN, DONE )\n    OTHERWISE( DONE )\n    WHEN( NO_IN, DONE )\n    L( DONE )\n    END_STOP\n"), "OTHERWISE 뒤");
        yield return ("S-5", W, Seq("    SELECT_BEGIN\n    WHEN( NO_IN, DONE )\n    SELECT_END\n    OBS_ON\n    L( DONE )\n    END_STOP\n"), "WHEN 뒤");
        yield return ("S-6", W, Seq("    OBS_ON\n"), "끝 스텝 없음 t_x");
        yield return ("S-7", E, [new("act_table.c", "OUT_PULSE( DEMO_LAMP, 30, 30, 3 )", "AGV_FORBACK( TOGGLE )")], "ACT 금지 AGV_FORBACK");
        yield return ("S-7", E, [new("act_table.c", "BELT_ON( 2, CW )", "BELT_ON( 2, CW, UNTIL( ON(DEMO_SEN_1) ) )")], "ACT 금지 BELT_ON");
        yield return ("S-8", E, Seq("    DELAY( 70000 )\n    END_STOP\n"), "범위 밖 70000");
        yield return ("S-8", E, Seq("    WAIT_STATE( 16, 1 )\n    END_STOP\n"), "범위 밖 16");
        yield return ("S-8", E, Seq("    WAIT_STABLE_TO( IN( ON(DEMO_SEN_1) ), 50, UNMATCHED( 40000 ), DONE )\n    L( DONE )\n    END_STOP\n"), "범위 밖 40000");
        yield return ("S-8", E, Seq("    WHEN_ID( 20, 10, DONE )\n    L( DONE )\n    END_STOP\n"), "처음 > 끝");
        yield return ("S-9", W, [new("io_map.h", "#define BELTLIFT_CW", "#define BELT3_CW        OUTS( DEMO_LAMP )\n#define BELT3_CCW       NO_OUT\n#define BELTLIFT_CW"),
            .. Seq("    BELT_ON( 1, CW, UNTIL( ON(DEMO_SEN_1) ) )\n    BELT_ON( 2, CW, UNTIL( ON(DEMO_SEN_2) ) )\n    BELT_ON( 3, CW, UNTIL( ON(DEMO_SEN_3) ) )\n    BELT_ON( LIFT, CW, UNTIL( ON(DEMO_SEN_4) ) )\n    WAIT_BELTS( 1, 2, 3 )\n    END_STOP\n")],
            "UNTIL 벨트 4");
        yield return ("S-10", W, Seq("    SELECT_END\n    END_STOP\n"), "SELECT_END 짝 없음");
        yield return ("S-10", W, Seq("    OBS_ON\n    SELECT_END_TO( 0, DONE )\n    L( DONE )\n    END_STOP\n"), "SELECT_END_TO 0");
        yield return ("S-11", W, Seq("    END_STOP\n", map: false), "대응표 없음 t_x");
        yield return ("S-12", E, [MapRow("SEQ_MAP( PF_BELT,   SEQ_LOAD,           belt_unload )")], "무시됨 · 중복 (1줄)");
        yield return ("S-12", E, [MapRow("SEQ_MAP( PF_BOX,    SEQ_UNLOAD,         nope )")], "없는 표 nope");
        yield return ("S-12", E, [MapRow("SEQ_MAP( PF_BOX,    SEQ_DEMO_SHUTTLE,   shuttle )")], "무시됨 · 중복 (7줄)");
        yield return ("S-13", E, [new("seq_custom.h", "    SEQ_DEMO_SHUTTLE\n", "    SEQ_DEMO_SHUTTLE = 70\n")], "범위 밖 SEQ_DEMO_SHUTTLE 70");
        yield return ("S-13", E, [new("seq_custom.h", "    SEQ_DEMO_SHUTTLE\n", "    SEQ_DEMO_SHUTTLE = 32\n")], "ID 겹침 32");
        yield return ("S-14", E, Seq("    AGV_TURN( CW, 90, 0, 1, 0, TAPE )\n    END_STOP\n"), "시퀀스 표 금지 TAPE");
        yield return ("S-14", E, Seq("    AGV_STOP\n    END_STOP\n"), "시퀀스 표 금지 AGV_STOP");
        yield return ("S-14", E, Seq("    ACS_SEND( ARRIVE, STATION(0) + 1 )\n    END_STOP\n"), "STATION ± n");
        yield return ("S-14", E, Seq("    WAIT_IF( X_TAG_NUM(STATION(0)) )\n    END_STOP\n"), "X_TAG_NUM");
        yield return ("S-15", E, Seq("    CALL( 5 )\n    END_STOP\n"), "함수 없음 5");
        yield return ("S-15", E, Seq("    WAIT_ACT( 9 )\n    END_STOP\n"), "ACT 대응표 없음 9");
        yield return ("S-16", E, Seq("    AGV_TURN( CW, 0, 0, 1, 0 )\n    END_STOP\n"), "범위 밖 0");
        yield return ("S-16", E, Seq("    AGV_TURN( CW, 360, 0, 1, 0 )\n    END_STOP\n"), "범위 밖 360");
        yield return ("S-16b", W, Seq("    AGV_TURN( CW, 90, 0, 1, 2 )\n    END_STOP\n"), "회전 속도 2");
        yield return ("S-17", W, Seq("    BELT_OFF( 2 )\n    END_STOP\n"), "안 켠 벨트 2");
        yield return ("S-18", E, Seq(Lines("OBS_ON", 64) + "    END_STOP\n"), "스텝 65");
        yield return ("M-1", E, [new("manual_table.c", "    MAN_ELSE( PF_BOX,   JOG,                              NO_OUT )\r\n", "")], "사슬 안 닫힘 PF_BOX JOG");
        yield return ("M-2", E, [Manual("MANUAL_END", "MAN( PF_CART, IDLE, ON(DEMO_SEN_1), NO_OUT )"), Manual("MANUAL_END", "MAN_ELSE( PF_CART, IDLE, NO_OUT )")], "조건 꼴");
        yield return ("M-2", E, [Manual("MANUAL_END", "MAN( ANY_PROFILE, IDLE, NO_IN, NO_OUT )"), Manual("MANUAL_END", "MAN_ELSE( ANY_PROFILE, IDLE, NO_OUT )")], "프로파일 0");
        yield return ("M-2", E, [Manual("MANUAL_END", "MAN( PF_CART, IDLE, NO_IN, ON(DEMO_MAN_LAMP), OFF(DEMO_MAN_LAMP) )"), Manual("MANUAL_END", "MAN_ELSE( PF_CART, IDLE, NO_OUT )")], "켜고 끔");
        yield return ("M-2", E, [Manual("MANUAL_END", "MAN( PF_CART, IDLE, NO_IN, BELT( 1, CW ), ON(DEMO_CV1_DIR) )"), Manual("MANUAL_END", "MAN_ELSE( PF_CART, IDLE, NO_OUT )")], "켜고 끔");
        yield return ("M-3", E, [Manual("MAN_ELSE( PF_BELT,  IDLE,", "MAN( PF_BELT,       IDLE, IN( ON(DEMO_MAN_UP) ),      BELT( 1, CCW ) )")], "무시됨 · 중복 (1줄)");
        yield return ("M-3", E, [Manual("MAN( PF_BOX,        JOG,", "MAN( PF_BOX,        JOG,  NO_IN,                      NO_OUT )")], "무시됨 · 중복");
        yield return ("M-4", W, [Manual("MAN_FB_SW( PF_BELT", "MAN_ELSE( PF_CART, IDLE, NO_OUT )")], "MAN 없는 사슬 PF_CART");
        yield return ("M-5", W, [Manual("MANUAL_END", "MAN_FB_SW( PF_BELT, IN_EXT_11 )")], "MAN_FB_SW 둘 PF_BELT");
        yield return ("M-6", W, [new("site.h", "    PF_BOX\n", "    PF_BOX,\n    PF_EXTRA\n"), Manual("MANUAL_END", "MAN_FB_SW( PF_EXTRA, IN_EXT_11 )")], "프로파일 줄 없음 PF_EXTRA");
        yield return ("M-7", W, [Manual("MAN_ELSE( PF_BELT,  IDLE,", "MAN( PF_BELT,       IDLE, IN( ON(DEMO_SEN_2) ),       ON(DEMO_LAMP) )")], "ACT 와 같은 출력");
        yield return ("C-2", E, [new("act_table.c", "", "#include \"agv/seq_dsl.h\"\r\nconst seq_def_t site_acts[1] = { { 0, 0, 0, 0 } };\r\nconst uint16_t site_nacts = 0;\r\n")], "ACT 처음");
        yield return ("C-3", E, [new("seq_custom.h", "    CALL_SEQ_COUNT\n", "    CALL_SEQ_COUNT = 0\n"), new("tag_custom.h", "TAGC_COUNT = 1", "TAGC_COUNT = 0")], "CALL 처음");
    }

    /// <summary>지어낸 core 폴더: VERSION, 헤더(문법 이름 전부 또는 하나 뺌), MANIFEST.sha256(맞게 또는 하나 틀리게).</summary>
    static string MakeCore(string name, string version, bool dropName, bool badHash)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tedit_check_tests", name);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        var inc = Directory.CreateDirectory(Path.Combine(dir, "include", "agv")).FullName;
        File.WriteAllText(Path.Combine(dir, "VERSION"), version + " 0000000 cl2000 v0\n", Utf8);
        var names = GrammarTable.Names.Where(n => !dropName || n != "TAG_NUM");
        File.WriteAllText(Path.Combine(inc, "seq_dsl.h"), "#ifndef AGV_SEQ_DSL_H\r\n#define AGV_SEQ_DSL_H\r\n" + string.Concat(names.Select(n => $"#define {n} 1\r\n")) + "#endif\r\n", Utf8);
        File.WriteAllText(Path.Combine(inc, "tag_dsl.h"), "#ifndef AGV_TAG_DSL_H\n#define AGV_TAG_DSL_H\n#endif\n", Utf8);
        string Hash(string rel) => Convert.ToHexStringLower(SHA256.HashData(Encoding.Latin1.GetBytes(File.ReadAllText(Path.Combine(dir, rel), Encoding.Latin1).Replace("\r\n", "\n"))));
        var rels = new[] { "VERSION", "include/agv/seq_dsl.h", "include/agv/tag_dsl.h" };
        var lines = rels.Select(r => (badHash && r == "VERSION" ? new string('0', 64) : Hash(r)) + "  " + r);
        File.WriteAllText(Path.Combine(dir, "MANIFEST.sha256"), string.Join("\n", lines) + "\n", Utf8);
        return dir;
    }

    public static void Run(Action<bool, string> check, string root)
    {
        var baseline = Run(root, null);
        check(baseline.Count == 0, "T900_DEMO 결과 0: " + string.Join(" | ", baseline.Take(5)));

        var good = MakeCore("good", "v0.56.1", false, false);
        var withCore = Run(root, good);
        check(withCore.Count == 0, "좋은 core 결과 0: " + string.Join(" | ", withCore.Take(5)));
        var bad = Run(root, MakeCore("bad", "v0.53.0", true, true));
        check(bad.Any(f => f.Rule == "V-1" && f.Severity == Severity.Error && f.Text.Contains("v0.53.0")), "V-1 코어 버전 낮음");
        check(bad.Any(f => f.Rule == "V-2" && f.Severity == Severity.Warning && f.Text.Contains("TAG_NUM")), "V-2 헤더 다름");
        check(bad.Any(f => f.Rule == "V-3" && f.Severity == Severity.Error && f.Text.Contains("수정됨 VERSION")), "V-3 MANIFEST 다름");
        File.WriteAllText(Path.Combine(good, "include", "agv", "extra.h"), "", Utf8);
        check(Run(root, good).Any(f => f.Rule == "V-3" && f.Text.Contains("추가됨 include/agv/extra.h")), "V-3 추가됨");

        var seen = new HashSet<string>();
        foreach (var (rule, sev, edits, text) in Cases())
        {
            seen.Add(rule);
            IReadOnlyList<Finding> got;
            try { got = Run(root, null, edits); }
            catch (Exception e) { check(false, $"{rule} 예외 {e.Message}"); continue; }
            bool hit = got.Any(f => f.Rule == rule && f.Severity == sev && f.Text.Contains(text, StringComparison.Ordinal));
            check(hit, $"{rule} '{text}' 안 걸림: " + string.Join(" | ", got.Take(6)));
        }

        // 규칙 목록의 모든 규칙(V-* 는 위)이 걸리는 예를 하나 이상 가진다
        var missing = Checker.Rules.Select(r => r.Rule).Where(r => !r.StartsWith("V-", StringComparison.Ordinal) && !seen.Contains(r)).ToList();
        check(missing.Count == 0, "예 없는 규칙: " + string.Join(", ", missing));

        // 걸리면 안 되는 것
        var split = Run(root, null, Profile("PROFILE( PF_CART, 0, 45, 45 )"), Profile("PROFILE( PF_BOX, 0, 40, 49 )"));
        check(!split.Any(f => f.Rule is "P-3" or "P-2"), "따로 빼기는 P-3 아님: " + string.Join(" | ", split));
        var guarded = Run(root, null, Tag("TAG_IF( 230, PF_BOX, IF_IN( ON(DEMO_SEN_1) ), AGV_STOP )"), Tag("TAG( 230, PF_BOX, NO_ACTION )"), Tag("TAG( 230, ANY_PROFILE, OBS_ON )"));
        check(!guarded.Any(f => f.Rule is "T-10" or "T-11"), "조건 줄 뒤 NO_ACTION·ANY_PROFILE 은 죽은 줄 아님: " + string.Join(" | ", guarded));
        var raw = Run(root, null, Tag("{ 41, PF_BELT, 0, 0, 0, { TA_STOP } },"), Tag("TAG( 1, PF_BELT, OBS_ON )"));
        check(raw.Any(f => f.Rule == "R-1") && !raw.Any(f => f.Rule == "T-10"), "원문 블록이 있으면 가림 검사를 건너뛴다");

        // 줄 번호: 여러 줄 문장의 안쪽 인자는 그 인자의 줄
        var multi = Run(root, null, new Edit("tag_table.c", "IF_X( X_ERR_EQ(281) ),", "IF_X( X_ERR_EQ(70000) ),"));
        var t9 = multi.FirstOrDefault(f => f.Rule == "T-9");
        check(t9 != null && t9.Line == t9.Piece!.StartLine + 1 && t9.ToString().StartsWith("오류 T-9 tag_table.c:", StringComparison.Ordinal), "여러 줄 문장 인자 줄: " + t9);

        // 실제 사이트(DCU_APP_ROOT 가 있을 때만): 죽지 않는다. 기대값은 명령으로 본다(저장소에 두지 않는다)
        var app = Environment.GetEnvironmentVariable("DCU_APP_ROOT");
        if (!string.IsNullOrEmpty(app))
            foreach (var dir in Directory.GetDirectories(Path.Combine(app, "site")))
                Checker.Run(Site.Open(dir, Path.Combine(app, "core")));
    }
}
