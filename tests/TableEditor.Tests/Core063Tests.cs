using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

/// <summary>
/// v0.2.4(코어 v0.63.0): 수동 표 프로파일 묶음(<c>MAN_PROFILES</c>·<c>PROFILE_SET</c>)·모드 <c>ANY</c>, 프로파일 표 <c>STOP_BACK_FL</c>, 태그 <c>SET_STATE</c>,
/// 보고 종류 <c>CROSS_IN</c>·<c>CROSS_OUT</c>. 코어 NEG 사례(<c>tests/dsl/man_set.c</c>·<c>hold_tag.c</c>)와 대조한다. 이름은 모두 지어낸 것.
/// </summary>
static class Core063Tests
{
    static readonly string[] ManOk =
    [
        "MAN_PROFILES( PROFILE_SET( 2 ), PF_A, PF_B )", "MAN_PROFILES( PROFILE_SET( 15 ), PF_C )", "MAN_PROFILES( PROFILE_SET( 0 ), 7, 31, 32, 63 )",
        "MAN( PROFILE_SET( 2 ), JOG, IN( ON(SEN_1) ), ON( OUT_EXT_3 ) )", "MAN_ELSE( PROFILE_SET( 2 ), JOG, OFF( OUT_EXT_3 ) )",
        "MAN( 7, IDLE, KEY( UP ), ON( OUT_EXT_4 ) )", "MAN_ELSE( 7, IDLE, NO_OUT )",
        "MAN( 9, ANY, IN( OFF(SEN_1) ), ON( OUT_EXT_5 ) )", "MAN_ELSE( 9, ANY, NO_OUT )",
        "MAN( PROFILE_SET( 0 ), ANY, NO_IN, ON( OUT_EXT_6 ) )", "MAN_ELSE( PROFILE_SET( 0 ), ANY, NO_OUT )",
        "MAN_FB_SW( PROFILE_SET( 15 ), SEN_2 )", "MAN_OBS_B( PROFILE_SET( 0 ), SEN_3 )", "MAN_START_SW( PROFILE_SET( 2 ), SEN_4 )", "MAN_START_SW( PF_A, SEN_1 )",
    ];

    static readonly string[] ProfOk =
    [
        "PROFILE( STOP_BACK_FL( PF_A ), 0, 1, 2 )", "PROFILE_LOAD( STOP_BACK_FL( 63 ), 100, 5, 9, LOAD_SENSORS( SEN_1 ) )", "PROFILE( PF_B, 0, 3, 7 )",
        "PROFILE( STOP_BACK_FL( PF_C ), 0, 10, 12 )",
    ];

    static readonly string[] TagOk =
    [
        "TAG( 12, 0, SET_STATE( 3, 7 ) )", "TAG( 13, 0, AGV_STOP, SET_STATE( 15, 65535 ) )", "TAG( 14, 0, SET_STATE( 0, 0 ) )",
        "TAG( 15, 0, ACS( CROSS_IN, 30 ), ACS( CROSS_OUT, STATION(1) ) )", "TAG( 16, 0, AGV_STOP, ACS( CROSS_OUT, STATION0 ), ACS( CROSS_IN, TAG_NUM + 2 ) )",
        "TAG( 17, 0, ACS( CROSS_IN, STATION0 ), ACS( CROSS_OUT, AGV_ID_MINUS( 3 ) ) )", "TAG( 18, 0, ACS( CROSS_OUT, 255 ), ACS( DISPLAY, STATION0 ) )",
    ];

    static readonly string[] SeqOk =
    [
        "ACS( CROSS_IN, 33 )", "ACS( CROSS_OUT, STATION(1) )", "ACS( CROSS_IN, STATION(0) + 1 )", "ACS( CROSS_OUT, AGV_ID )", "ACS( CROSS_IN, 65535 )",
    ];

    public static void Run(Action<bool, string> check, string root)
    {
        int gcc = 0;
        var c63 = CoreKit.Core("c63", "v0.63.0");
        var c61 = CoreKit.Core("c61", "v0.61.0");

        // 코어가 컴파일하는 새 줄
        CoreKit.Ok(check, "수동 표 묶음·ANY·입력 줄", c63, "man", ManOk, ref gcc);
        CoreKit.Ok(check, "프로파일 STOP_BACK_FL", c63, "prof", ProfOk, ref gcc);
        CoreKit.Ok(check, "태그 SET_STATE·CROSS", c63, "tag", TagOk, ref gcc);
        CoreKit.Ok(check, "시퀀스 ACS CROSS", c63, "seq", SeqOk, ref gcc);

        // 새 줄 쓰기·V-4: 사이트 코어 버전. 줄마다 하나
        List<Finding> V4(Site s) => Checker.Run(s).Where(f => f.Rule == "V-4").ToList();
        void Low(string label, string kind, string[] lines, string word)
        {
            Site Mk(string core) => kind switch { "man" => CoreKit.Man(core, lines), "prof" => CoreKit.Prof(core, lines), "tag" => CoreKit.Tag(core, lines), _ => CoreKit.Seq(core, lines) };
            var f = V4(Mk(c61));
            check(f.Count == 1 && f[0].Text == $"코어 v0.63.0 필요 · {word}", $"v0.61.0 에서 V-4: {label} → {CoreKit.Dump(f)}");
            check(V4(Mk(c63)).Count == 0, $"v0.63.0 에서 V-4 없음: {label}");
        }
        Low("MAN_PROFILES", "man", ["MAN_PROFILES( 5, PF_A )"], "MAN_PROFILES");
        Low("PROFILE_SET 열쇠", "man", ["MAN_FB_SW( PROFILE_SET( 0 ), SEN_1 )"], "PROFILE_SET");
        Low("모드 ANY", "man", ["MAN_ELSE( PF_A, ANY, NO_OUT )"], "ANY");
        check(V4(CoreKit.Man(c61, "MAN( PF_A, ANY, NO_IN, NO_OUT )")).Count == 1, "모드 ANY: MAN 줄도 V-4");
        Low("STOP_BACK_FL", "prof", ["PROFILE( STOP_BACK_FL( PF_A ), 0, 1, 2 )"], "STOP_BACK_FL");
        Low("태그 SET_STATE", "tag", ["TAG( 5, 0, SET_STATE( 1, 2 ) )"], "SET_STATE");
        Low("태그 CROSS_IN", "tag", ["TAG( 5, 0, ACS( CROSS_IN, 3 ) )"], "CROSS_IN");
        Low("태그 CROSS_OUT 1칸", "tag", ["TAG( 5, 0, ACS( CROSS_OUT, STATION0 ) )"], "CROSS_OUT");
        Low("시퀀스 CROSS_IN", "seq", ["ACS( CROSS_IN, 3 )"], "CROSS_IN");
        Low("시퀀스 ACS_SEND CROSS_OUT", "seq", ["ACS_SEND( CROSS_OUT, 3 )"], "CROSS_OUT");
        // 시퀀스 SET_STATE(스텝)는 예전부터 있다
        check(V4(CoreKit.Seq(c61, "SET_STATE( 1, 2 )")).Count == 0, "스텝 SET_STATE 는 옛 줄");
        // 새 줄 쓰기 목록: 모드 ANY·보고 종류는 낱말 단위
        var modes = GrammarTable.Find("MAN", Place.Manual).Single().Args[1].Words!;
        var reports = GrammarTable.Find("ACS", Place.Step).Single().Args[0].Words!;
        check(GrammarTable.WordOk("ANY", modes, "v0.63.0") && !GrammarTable.WordOk("ANY", modes, "v0.61.0") && GrammarTable.WordOk("IDLE", modes, "v0.54.0") && GrammarTable.WordOk("JOG", modes, null), "모드 목록: ANY 는 v0.63.0 부터, IDLE·JOG 는 늘");
        check(GrammarTable.WordOk("CROSS_IN", reports, "v0.63.0") && !GrammarTable.WordOk("CROSS_OUT", reports, "v0.61.0") && GrammarTable.WordOk("DISPLAY", reports, "v0.61.0") && !GrammarTable.WordOk("DISPLAY", reports, "v0.60.0")
              && GrammarTable.WordOk("IDLE", reports, "v0.59.0") && !GrammarTable.WordOk("IDLE", reports, "v0.58.0") && GrammarTable.WordOk("CALL", reports, null) && !GrammarTable.WordOk("CROSS_IN", reports, null), "보고 종류 목록: IDLE v0.59.0 · DISPLAY v0.61.0 · CROSS_IN·OUT v0.63.0, 코어 모르면 옛 낱말만");
        var setState = GrammarTable.Find("SET_STATE", Place.TagAction).Single();
        check(setState.TagCells == 2 && setState.Since == "v0.63.0" && GrammarTable.Fits(setState, "v0.63.0") && !GrammarTable.Fits(setState, "v0.61.0"), "태그 SET_STATE: 2칸 · v0.63.0");

        // 코어 NEG(man_set.c·hold_tag.c) → 편집기 오류
        (string Id, string Kind, string[] Lines, string Rule)[] neg =
        [
            ("SET_16", "man", ["MAN_PROFILES( PROFILE_SET( 16 ), 1 )"], "M-2"),
            ("SET_NEG", "man", ["MAN_ELSE( PROFILE_SET( -1 ), JOG, NO_OUT )"], "M-2"),
            ("SET_PLAIN", "man", ["MAN_PROFILES( 1, 2 )"], "M-8"),
            ("SET_PF0", "man", ["MAN_PROFILES( PROFILE_SET( 1 ), 1, 0 )"], "M-2"),
            ("SET_PF64", "man", ["MAN_PROFILES( PROFILE_SET( 1 ), 64 )"], "M-2"),
            ("SET_EMPTY", "man", ["MAN_PROFILES( PROFILE_SET( 1 ) )"], "M-8"),
            ("SET_17", "man", ["MAN_PROFILES( PROFILE_SET( 1 ), 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17 )"], "M-8"),
            ("SET_KEY", "man", ["MAN_START_SW( MAN_PSET + 16, SEN_1 )"], "N-2"),
            ("ANY_LOWER", "man", ["MAN( 1, any, NO_IN, NO_OUT )"], "N-1"),
            ("SET_64", "man", ["MAN( 64, JOG, NO_IN, NO_OUT )"], "M-2"),
            ("FL_0", "prof", ["PROFILE( STOP_BACK_FL( 0 ), 0, 1, 1 )"], "P-1"),
            ("FL_64", "prof", ["PROFILE( STOP_BACK_FL( 64 ), 0, 1, 1 )"], "P-1"),
            ("FL_TWICE", "prof", ["PROFILE( STOP_BACK_FL( STOP_BACK_FL( 1 ) ), 0, 1, 1 )"], "N-2"),
            ("FL_SET", "prof", ["PROFILE( STOP_BACK_FL( PROFILE_SET( 0 ) ), 0, 1, 1 )"], "N-2"),
            ("FL_MAN", "man", ["MAN_ELSE( STOP_BACK_FL( 1 ), JOG, NO_OUT )"], "N-2"),
            ("FL_TAG", "tag", ["TAG( 1, STOP_BACK_FL( 1 ), AGV_STOP )"], "N-2"),
            ("FL_TAGIF", "tag", ["TAG_IF( 1, STOP_BACK_FL( 1 ), IF_ID( 1, 2 ), AGV_STOP )"], "N-2"),
            ("FL_TAGRANGE", "tag", ["TAG_RANGE( 1, 2, STOP_BACK_FL( 1 ), AGV_STOP )"], "N-2"),
            ("FL_TAGRANGEIF", "tag", ["TAG_RANGE_IF( 1, 2, STOP_BACK_FL( 1 ), IF_ID( 1, 2 ), AGV_STOP )"], "N-2"),
            ("TAG_64", "tag", ["TAG( 1, 64, AGV_STOP )"], "T-9"),
            ("TAG_SETSTATE_SLOT", "tag", ["TAG( 10, 0, SET_STATE( 16, 1 ) )"], "T-9"),
            ("TAG_SETSTATE_SLOTNEG", "tag", ["TAG( 10, 0, SET_STATE( -1, 1 ) )"], "T-9"),
            ("TAG_SETSTATE_BIG", "tag", ["TAG( 10, 0, SET_STATE( 3, 65536 ) )"], "T-9"),
            ("TAG_SETSTATE_NEG", "tag", ["TAG( 10, 0, SET_STATE( 3, -1 ) )"], "T-9"),
            ("TAG_CROSS_BIG", "tag", ["TAG( 10, 0, ACS( CROSS_IN, 256 ) )"], "T-9"),
            ("TAG_CROSS_NEG", "tag", ["TAG( 10, 0, ACS( CROSS_OUT, -1 ) )"], "T-9"),
        ];
        foreach (var (id, kind, lines, rule) in neg) CoreKit.Neg(check, "NEG " + id, c63, kind, lines, rule, ref gcc);
        // 시퀀스 SET_STATE 와 달리 태그 표 SET_STATE 는 칸이 둘(동작 칸 합 4 넘으면 T-8)
        check(Checker.Run(CoreKit.Tag(c63, "TAG( 1, 0, SET_STATE( 1, 2 ), SET_STATE( 2, 3 ), AGV_STOP )")).Any(f => f.Rule == "T-8"), "T-8: SET_STATE 둘 + 정지 = 5칸");
        // 대응표·태그 표 프로파일 0~63(코어 v0.63.0 새 검사): 대응표 64
        var mapSite = CoreKit.Mk(c63, seq: "SEQ_BEGIN( s_x )\n    END_STOP\nSEQ_END\nSEQ_MAP_BEGIN( d_neg )\n    SEQ_MAP( 64, 1, s_x )\nSEQ_MAP_END\n");
        check(Checker.Run(mapSite).Any(f => f.Rule == "S-8" && f.Severity == Severity.Error), "NEG MAP_64: 대응표 프로파일 64 = S-8");
        var mapFl = CoreKit.Mk(c63, seq: "SEQ_BEGIN( s_x )\n    END_STOP\nSEQ_END\nSEQ_MAP_BEGIN( d_neg )\n    SEQ_MAP( STOP_BACK_FL( 1 ), 1, s_x )\nSEQ_MAP_END\n");
        check(Checker.Run(mapFl).Any(f => f.Rule == "N-2" && f.Severity == Severity.Error), "NEG FL_SEQMAP: 대응표의 STOP_BACK_FL = N-2");
        if (CoreKit.GccReady)
        {
            gcc += 2;
            check(!CoreKit.GccCompiles("agv/seq_dsl.h", "SEQ_BEGIN( s_x )\n    END_STOP\nSEQ_END\nSEQ_MAP_BEGIN( d_neg )\n    SEQ_MAP( 64, 1, s_x )\nSEQ_MAP_END\n"), "코어 NEG MAP_64");
            check(!CoreKit.GccCompiles("agv/seq_dsl.h", "SEQ_BEGIN( s_x )\n    END_STOP\nSEQ_END\nSEQ_MAP_BEGIN( d_neg )\n    SEQ_MAP( STOP_BACK_FL( 1 ), 1, s_x )\nSEQ_MAP_END\n"), "코어 NEG FL_SEQMAP");
        }

        // M-10: 묶음 줄은 쓰는 줄보다 앞
        List<Finding> M10(params string[] lines) => Checker.Run(CoreKit.Man(c63, lines)).Where(f => f.Rule == "M-10").ToList();
        check(M10("MAN_PROFILES( PROFILE_SET( 3 ), PF_A )", "MAN_FB_SW( PROFILE_SET( 3 ), SEN_1 )").Count == 0, "M-10: 묶음 줄이 앞이면 통과");
        var late = M10("MAN_FB_SW( PROFILE_SET( 3 ), SEN_1 )", "MAN_PROFILES( PROFILE_SET( 3 ), PF_A )");
        check(late is [{ Severity: Severity.Error }] && late[0].Text.Contains("뒤에 있음"), "M-10: 묶음 줄이 뒤에 있음 = 오류 " + CoreKit.Dump(late));
        var none = M10("MAN_FB_SW( PROFILE_SET( 4 ), SEN_1 )", "MAN_PROFILES( PROFILE_SET( 3 ), PF_A )");
        check(none is [{ Severity: Severity.Error }] && none[0].Text.Contains("줄 없음"), "M-10: 묶음 줄이 아예 없음 = 오류 " + CoreKit.Dump(none));
        check(M10("MAN_PROFILES( PROFILE_SET( 3 ), PF_A )", "MAN_PROFILES( PROFILE_SET( 3 ), PF_B )", "MAN_START_SW( PROFILE_SET( 3 ), SEN_1 )").Count == 0, "M-10: 같은 k 줄 여럿(OR)");

        // 열쇠 → 프로파일 이름(걸러 보기·신호 범위·검사가 쓴다)
        var key = CoreKit.Mk(c63, man: CoreKit.ManText("MAN_PROFILES( PROFILE_SET( 0 ), PF_A, 2 )", "MAN_PROFILES( PROFILE_SET( 0 ), PF_C )", "MAN_PROFILES( PROFILE_SET( 1 ), PF_B )"),
            prof: CoreKit.ProfText("PROFILE( STOP_BACK_FL( PF_A ), 0, 1, 2 )"));
        check(ProfileKey.Names(key, "PROFILE_SET( 0 )").SequenceEqual(["PF_A", "PF_B", "PF_C"]) && ProfileKey.Names(key, "PROFILE_SET( 1 )").SequenceEqual(["PF_B"]) && ProfileKey.Names(key, "PROFILE_SET(7)").Count == 0,
            "묶음 → 프로파일 이름(번호는 이름으로, 같은 k 줄 합집합): " + string.Join(",", ProfileKey.Names(key, "PROFILE_SET( 0 )")));
        check(ProfileKey.Names(key, "STOP_BACK_FL( PF_C )").SequenceEqual(["PF_C"]) && ProfileKey.Names(key, "PF_A").SequenceEqual(["PF_A"]) && ProfileKey.Names(key, "ANY_PROFILE").Count == 0 && ProfileKey.Names(key, "0").Count == 0,
            "열쇠: STOP_BACK_FL 풀기·이름·전체");

        // 프로파일 표: 줄의 프로파일은 STOP_BACK_FL 안쪽, 표시는 BackFl
        var pf = CoreKit.Prof(c63, ProfOk);
        check(pf.ProfileRows.Select(r => r.Profile).SequenceEqual(["PF_A", "63", "PF_B", "PF_C"]) && pf.ProfileRows.Select(r => r.BackFl).SequenceEqual([true, true, false, true]), "프로파일 줄: 안쪽 프로파일 · 후진 FL 표시");
        check(pf.RowsOf("PF_A").Count() == 1 && Checker.Run(pf).All(f => f.Rule != "P-5" || !f.Text.Contains("PF_A")), "RowsOf·P-5 가 STOP_BACK_FL 줄도 그 프로파일 줄로 본다");
        check(pf.ProfilesAt(2).SequenceEqual(["PF_A"]) && pf.ProfilesAt(11).SequenceEqual(["PF_C"]), "호기 → 프로파일(STOP_BACK_FL 줄도)");

        // 이름 바꾸기·삭제가 STOP_BACK_FL·MAN_PROFILES·MAN_START_SW 안의 이름도 따라간다
        var all = CoreKit.Mk(c63, man: CoreKit.ManText("MAN_PROFILES( PROFILE_SET( 0 ), PF_A, PF_B )", "MAN_START_SW( PF_A, SEN_1 )", "MAN_START_SW( PROFILE_SET( 0 ), SEN_2 )"),
            prof: CoreKit.ProfText("PROFILE( STOP_BACK_FL( PF_A ), 0, 1, 2 )", "PROFILE( PF_B, 0, 3, 7 )", "PROFILE( PF_C, 0, 10, 12 )"));
        var ren = BundleEdit.RenameProfile(all, "PF_A", "PF_Z");
        string Text(EditResult r, string file) => r.Site!.File(file)!.Pieces.OfType<Statement>().Aggregate("", (a, st) => a + st.Text);
        check(ren.Ok && Text(ren, "profiles.c").Contains("STOP_BACK_FL( PF_Z )") && Text(ren, "manual_table.c").Contains("PROFILE_SET( 0 ), PF_Z, PF_B )") && Text(ren, "manual_table.c").Contains("MAN_START_SW( PF_Z, SEN_1 )")
              && !Text(ren, "profiles.c").Contains("PF_A") && !Text(ren, "manual_table.c").Contains("PF_A"), "이름 바꾸기: STOP_BACK_FL·MAN_PROFILES·MAN_START_SW 안 이름도 " + ren.Error);
        var delUsed = BundleEdit.DeleteProfile(all, "PF_A");
        check(!delUsed.Ok && delUsed.Error!.Contains("쓰는 줄"), "삭제: 묶음 줄·수동 줄이 쓰는 프로파일은 막힘 " + delUsed.Error);
        var delC = BundleEdit.DeleteProfile(CoreKit.Mk(c63, man: CoreKit.ManText("MAN_START_SW( PF_A, SEN_1 )"), prof: CoreKit.ProfText("PROFILE( STOP_BACK_FL( PF_C ), 0, 1, 2 )", "PROFILE( PF_A, 0, 5, 6 )")), "PF_C");
        check(delC.Ok && !Text(delC, "profiles.c").Contains("PF_C") && Text(delC, "profiles.c").Contains("PF_A"), "삭제: STOP_BACK_FL 줄도 그 프로파일의 줄로 같이 지움 " + delC.Error);

        // 형식 바꾸기: PARAM_TIME 칸은 뜻(제한·안정·누적)이 같을 때만 옮긴다
        {
            var ws = CoreKit.Seq(c63, "WAIT_TO( IN( ON(SEN_1) ), PARAM_TIME( AS, SEC ), A )", "WAIT_STABLE_TO( IN( ON(SEN_1) ), PARAM_TIME( AS, SEC ), 500, A )", "WAIT_STABLE_TO( IN( ON(SEN_1) ), PARAM_TIME( AS, SEC ), 500, A )");
            var sts = ws.File("seq_table.c")!.Statements.Where(x => x.Name.StartsWith("WAIT")).ToList();
            GrammarEntry Pm(string n) => GrammarTable.Find(n, Place.Step).First(e => e.Args.Any(a => a.Kind == ArgKind.ParamTime));
            static string Fill(ArgSpec s) => s.Kind == ArgKind.ParamTime ? "PARAM_TIME( XX, SEC )" : s.Kind == ArgKind.Label ? "A" : "0";
            var waitTo = Pm("WAIT_TO"); var stable = Pm("WAIT_STABLE_TO"); var accumTo = Pm("WAIT_ACCUM_TO");
            var d1 = ActionForms.Dropped(stable, waitTo, sts[0].Args);
            check(ActionForms.Convert(stable, waitTo, sts[0].Args, Fill) == "WAIT_STABLE_TO( IN( ON(SEN_1) ), PARAM_TIME( XX, SEC ), 0, A )" && d1.SequenceEqual(["PARAM_TIME( AS, SEC )"]), "형식 바꾸기: 제한 파라미터 → 유지 형식은 값 버림 · " + string.Join(" / ", d1));
            var d2 = ActionForms.Dropped(waitTo, stable, sts[1].Args);
            check(d2.SequenceEqual(["PARAM_TIME( AS, SEC )", "500"]), "형식 바꾸기: 유지 파라미터 → 제한 형식은 파라미터·500 버림 · " + string.Join(" / ", d2));
            check(ActionForms.Dropped(accumTo, stable, sts[2].Args).SequenceEqual(["PARAM_TIME( AS, SEC )"]) && ActionForms.Convert(stable, stable, sts[2].Args, Fill).Contains("PARAM_TIME( AS, SEC ), 500"), "형식 바꾸기: 안정 → 누적 파라미터는 버림, 같은 뜻은 그대로");
        }

        // 표 밖 enum 라벨(읽기만): 한 줄·여러 줄·값 있음/없음·주석 섞임, S-1 안 걸림, 중복·미사용 검사가 안 깨짐
        {
            string Tbl(string head, params string[] body) => head + "\nSEQ_BEGIN( n )\n" + string.Concat(body.Select(l => "    " + l + "\n")) + "SEQ_END\n";
            const string en = "enum { E_ONE = 1012, E_TWO = 1013 };\nenum {\n    E_M1 = 1014, /* c, d */\n    E_M2         // e, f = 9\n};\n";
            var es = CoreKit.Mk(c63, seq: Tbl("SEQ_LABELS( A )\n" + en, "WAIT_TO( IN( ON(SEN_1) ), 5, E_ONE )", "GOTO( A )", "L( E_ONE )", "GOTO( E_TWO )", "L( E_TWO )", "GOTO( E_M1 )", "L( E_M1 )", "GOTO( E_M2 )", "L( E_M2 )", "END_STOP", "L( A )", "END_STOP"));
            var labels = es.Names.Of(NameKind.Label).ToDictionary(d => d.Name, d => d.Value);
            check(labels.Count == 5 && labels["A"] == 1 && labels["E_ONE"] == 1012 && labels["E_TWO"] == 1013 && labels["E_M1"] == 1014 && labels["E_M2"] == 1015, "enum 라벨: 한 줄·여러 줄·값 없음 이어 세기·주석 속 쉼표 무시 · " + string.Join(",", labels.Select(kv => kv.Key + "=" + kv.Value)));
            var ef = Checker.Run(es).Where(f => f.Rule is "S-1" or "S-2" or "S-3" or "S-4" or "N-4" or "N-5").ToList();
            check(ef.Count == 0, "enum 라벨을 쓰는 표: 라벨 검사 0 · " + CoreKit.Dump(ef));
            check(es.Names.Of(NameKind.Label).Where(d => d.Name.StartsWith("E_", StringComparison.Ordinal)).All(d => d.LabelCell == null && d.Def is { Kind: PieceKind.Raw }), "enum 라벨: 정의는 원문 조각, 칸 없음");
            var bad = Checker.Run(CoreKit.Mk(c63, seq: Tbl("SEQ_LABELS( A )\n" + en, "GOTO( E_NOPE )", "L( A )", "END_STOP"))).Where(f => f.Rule is "S-1" or "S-3").ToList();
            check(bad.Count(f => f.Rule == "S-1") == 1 && bad.Count(f => f.Rule == "S-3" && f.Text.Contains("E_", StringComparison.Ordinal)) == 4, "없는 이름은 S-1, 안 쓰는 enum 라벨은 S-3 경고(칸 없이) · " + CoreKit.Dump(bad));
            var dup = Checker.Run(CoreKit.Mk(c63, seq: Tbl("SEQ_LABELS( A, E_ONE )\n" + en, "GOTO( A )", "L( A )", "END_STOP"))).Where(f => f.Rule == "N-4").ToList();
            check(dup.Count == 1 && dup[0].Text.Contains("E_ONE", StringComparison.Ordinal), "SEQ_LABELS 와 enum 라벨 이름 겹침 = N-4 · " + CoreKit.Dump(dup));
            var add = BundleEdit.AddLabel(es, "seq_table.c", "E_TWO");
            check(!add.Ok && add.Error!.Contains("N-4", StringComparison.Ordinal) && BundleEdit.AddLabel(es, "seq_table.c", "E_NEW") is { Ok: true }, "라벨 추가: enum 라벨 이름은 이미 있음, 새 이름은 SEQ_LABELS 끝에");
            var other = CoreKit.Mk(c63, seq: Tbl("SEQ_LABELS( A )", "GOTO( A )", "L( A )", "END_STOP"));
            check(other.Names.Of(NameKind.Label).Count() == 1, "enum 없는 표는 그대로");
        }

        // fixture(지어낸 새 문법 한 벌): 가짜 코어 v0.63.0 에서 결과 0, 옛 코어에서는 V-4
        var fx = Path.Combine(root, "tests", "fixtures", "T900_DEMO", "dsl_v063");
        // 지어낸 core 폴더에는 헤더가 없어 fixture 가 정의한 NO_ACTION 의 겹침 경고가 하나 난다(다른 시험과 같다)
        var good = Checker.Run(Site.Open(fx, c63)).Where(f => f.Rule is not ("V-2" or "V-3") && !(f.Rule == "N-5" && f.Text.Contains("NO_ACTION", StringComparison.Ordinal))).ToList();
        check(good.Count == 0, "dsl_v063 + v0.63.0 결과 0: " + CoreKit.Dump(good));
        foreach (var (ver, since) in new[] { ("v0.59.0", "v0.63.0"), ("v0.60.0", "v0.63.0"), ("v0.61.0", "v0.63.0") })
        {
            var old = Checker.Run(Site.Open(fx, CoreKit.Core("fx" + ver, ver))).Where(f => f.Rule == "V-4").ToList();
            check(old.Count > 0 && old.All(f => f.Severity == Severity.Error) && old.Any(f => f.Text.Contains(since)), $"dsl_v063 + {ver}: 새 문법 줄마다 V-4({old.Count}줄)");
        }
        var c59 = Checker.Run(Site.Open(fx, CoreKit.Core("fx59", "v0.59.0"))).Where(f => f.Rule == "V-4").Select(f => f.Text).ToList();
        check(new[] { "MAN_START_SW", "WAIT_ACCUM", "STATE", "WHEN_OUT", "STOP_BACK_FL", "MAN_PROFILES", "SET_STATE", "CROSS_IN", "DISPLAY", "ANY" }.All(w => c59.Any(t => t.Contains(w, StringComparison.Ordinal))),
            "v0.59.0 에서 새 낱말이 모두 V-4 에 든다: " + string.Join(" / ", c59.Take(12)));
        check(Checker.Run(Site.Open(Path.Combine(root, "tests", "fixtures", "T900_DEMO", "dsl_v058"), c63)).Where(f => f.Rule is not ("V-2" or "V-3") && f.Severity == Severity.Error).Count() == 0, "v0.58.0 한 벌 + v0.63.0 코어: 오류 0");

        if (CoreKit.GccReady)
        {
            foreach (var f in new[] { "profiles.c", "manual_table.c", "seq_table.c", "act_table.c", "tag_table.c" })
            {
                gcc++;
                check(CoreKit.GccFile(Path.Combine(fx, f), fx), $"dsl_v063/{f} 코어 v0.63.0 헤더로 컴파일");
            }
            var hdr = HeaderCheck.Run(CoreKit.CoreRoot!);
            check(hdr.Same, "tedit headers: v0.63.0 헤더와 문법 표가 같다: " + string.Join(",", hdr.OnlyHeader.Concat(hdr.OnlyGrammar)));
            check(HeaderCheck.VersionOf(CoreKit.CoreRoot!) == "v0.63.0", "헤더 대조가 코어 버전을 version.h 에서 읽음");
            Console.WriteLine($"  gcc 대조 {gcc}건(v0.63.0)");
        }
    }
}
