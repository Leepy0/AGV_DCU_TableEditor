using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;

/// <summary>
/// v0.2.4(코어 v0.61.0): 유지·누적 대기(<c>WAIT_ACCUM</c>·<c>WHEN_STABLE</c>·<c>WHEN_ACCUM</c>·<c>WHEN_OUT</c>, 틱 칸의 <c>PARAM_TIME</c>), 상태 칸 조건 <c>STATE</c>,
/// 보고 종류 <c>DISPLAY</c>, 압축 표 스텝 한계(S-18 999), 묶음 유지 가지 셋째(S-19). 코어 NEG 사례(<c>tests/dsl/hold_seq.c</c>·<c>hold_tag.c</c>)와 대조한다.
/// </summary>
static class Core061Tests
{
    public static void Run(Action<bool, string> check)
    {
        int gcc = 0;
        var c61 = CoreKit.Core("c61", "v0.61.0");
        var c60 = CoreKit.Core("c60", "v0.60.0");

        // 코어가 컴파일하는 새 줄: 편집기 오류 0·원문 블록 없음, gcc 도 컴파일
        string[] seqOk =
        [
            "WAIT_ACCUM( IN( ON(SEN_1) ), 200 )", "WAIT_ACCUM( AND( ON(SEN_1), OFF(SEN_2) ), 0 )",
            "WAIT_ACCUM_TO( IN( ON(SEN_1) ), 65534, 500, A )", "WAIT_ACCUM_TO( IN( OFF(SEN_2) ), 30, UNMATCHED(100), B )",
            "WAIT_STABLE( IN( ON(SEN_1) ), 300 )", "WAIT_STABLE( IN( ON(SEN_1) ), PARAM_TIME( ST, TICK ) )",
            "WAIT_STABLE_TO( AND( OFF(SEN_2) ), PARAM_TIME( AS, SEC ), UNMATCHED(100), A )",
            "WAIT_ACCUM( IN( ON(SEN_1) ), PARAM_TIME( AS, DSEC ) )", "WAIT_ACCUM_TO( IN( ON(SEN_1) ), PARAM_TIME( ACC, SEC ), 300, B )",
            "WAIT_ACCUM_TO( IN( ON(SEN_1) ), PARAM_TIME( AD, DSEC ), 6000, B )",
            "WAIT( AND( ON(SEN_1), STATE( 3, 1 ) ) )", "WAIT_TO( AND( STATE( 0, 1, 65535 ), OFF(SEN_2) ), 300, B )",
            "OUT_FLICKER_UNTIL_IN( OUT_A, 50, AND( ON(SEN_1), STATE( 2, 7 ) ) )", "ACS( DISPLAY, 13 )", "ACS( DISPLAY, STATION(1) )",
        ];
        CoreKit.Ok(check, "v0.61.0 시퀀스 새 줄", c61, "seq", seqOk, ref gcc);
        CoreKit.Ok(check, "WHEN 묶음 안 유지 가지·출력 가지", c61, "seq",
        [
            "SELECT_BEGIN", "WHEN_STABLE( IN( ON(SEN_1) ), 300, A )", "WHEN_STABLE( IN( ON(SEN_1), ANY_ON(SEN_2, SEN_3) ), 0, B )",
            "WHEN_OUT( IN( ON(OUT_A), OFF(OUT_B) ), A )", "WHEN_OUT( AND( ON(OUT_A) ), B )", "SELECT_END",
            "OBS_ON", "SELECT_BEGIN", "WHEN( AND( NOT( STATE( 15, 2, 9 ) ) ), A )", "WHEN_ACCUM( AND( ON(SEN_1), OFF(SEN_2) ), PARAM_TIME( ST, SEC ), B )",
            "WHEN_ACCUM( IN( OFF(SEN_H) ), 65534, A )", "SELECT_END",
        ], ref gcc);
        CoreKit.Ok(check, "v0.61.0 태그 STATE·DISPLAY", c61, "tag",
        [
            "TAG_IF( 10, 0, AND( STATE( 3, 1 ), ID( 46, 50 ) ), AGV_STOP )", "TAG_IF( 11, 0, AND( ON(SEN_1), NOT( STATE( 2, 1, 65535 ) ) ), OBS_OFF )",
            "TAG_RANGE_IF( 20, 30, 0, AND( STATE( 15, 4 ), STATE( 14, 0, 3 ) ), ACS( DISPLAY, 13 ) )",
        ], ref gcc);

        // 새 줄 쓰기·V-4: 사이트 코어 버전
        var newSteps = new[] { "WAIT_ACCUM", "WAIT_ACCUM_TO", "WHEN_STABLE", "WHEN_ACCUM", "WHEN_OUT" };
        check(newSteps.All(n => GrammarTable.Find(n, Place.Step).All(r => r.Since == "v0.61.0" && r.Role == Role.Statement && r.Place == Place.Step)), "새 스텝 다섯: 코어 v0.61.0");
        check(SeqEdit.StepRows(false, "v0.61.0").Select(r => r.Name).Intersect(newSteps).Count() == 5 && !SeqEdit.StepRows(false, "v0.60.0").Select(r => r.Name).Intersect(newSteps).Any()
              && SeqEdit.StepRows(true, "v0.61.0").Select(r => r.Name).Intersect(newSteps).Count() == 5, "스텝 추가 목록: v0.61.0 이상에서만(ACT 표도 된다)");
        var wait = GrammarTable.Find("WAIT_STABLE", Place.Step).First(r => r.Since.Length == 0);
        var accum = GrammarTable.Find("WAIT_ACCUM", Place.Step).First();
        check(ActionForms.OfCore(wait, "v0.61.0").Select(ActionForms.Word).SequenceEqual(["없음", "시간", "파라미터", "시간·파라미터"]) && ActionForms.OfCore(wait, "v0.60.0").Select(ActionForms.Word).SequenceEqual(["없음", "시간"]), "유지 대기 형식 칸: 파라미터 형식은 v0.61.0 부터");
        check(ActionForms.OfCore(accum, "v0.61.0").Count == 4, "누적 대기 형식 넷(없음·시간·파라미터·시간·파라미터)");
        List<Finding> V4(Site s) => Checker.Run(s).Where(f => f.Rule == "V-4").ToList();
        var lowLines = new[]
        {
            "WAIT_ACCUM( IN( ON(SEN_1) ), 5 )", "WAIT_ACCUM_TO( IN( ON(SEN_1) ), 5, 10, A )", "WHEN_STABLE( IN( ON(SEN_1) ), 5, A )", "WHEN_ACCUM( IN( ON(SEN_1) ), 5, A )",
            "WHEN_OUT( IN( ON(OUT_A) ), A )", "WAIT_STABLE( IN( ON(SEN_1) ), PARAM_TIME( AS, SEC ) )", "ACS( DISPLAY, 3 )", "WAIT( AND( STATE( 1, 1 ) ) )",
        };
        foreach (var l in lowLines)
        {
            var f = V4(CoreKit.Seq(c60, l));
            check(f.Count == 1 && f[0].Severity == Severity.Error && f[0].Text.StartsWith("코어 v0.61.0 필요 · ", StringComparison.Ordinal), $"v0.60.0 에서 V-4: {l} → {CoreKit.Dump(f)}");
            check(V4(CoreKit.Seq(c61, l)).Count == 0, $"v0.61.0 에서 V-4 없음: {l}");
        }
        check(V4(CoreKit.Seq(c60, "ACS( DISPLAY, 3 )"))[0].Text == "코어 v0.61.0 필요 · DISPLAY" && V4(CoreKit.Tag(c60, "TAG( 5, 0, ACS( DISPLAY, 3 ) )")).Count == 1 && V4(CoreKit.Seq(c60, "ACS_SEND( DISPLAY, 3 )")).Count == 1, "보고 종류 DISPLAY: 시퀀스·태그·ACS_SEND");
        check(V4(CoreKit.Tag(c60, "TAG_IF( 10, 0, AND( STATE( 3, 1 ) ), AGV_STOP )")) is [{ Text: "코어 v0.61.0 필요 · STATE" }], "태그 STATE 조건: v0.61.0 필요");
        // 태그 표에서는 새 스텝이 못 쓰는 이름
        foreach (var l in new[] { "WAIT_ACCUM( IN( ON(SEN_1) ), 10 )", "WAIT_ACCUM_TO( IN( ON(SEN_1) ), 10, 100, 1 )", "WHEN_STABLE( IN( ON(SEN_1) ), 10, 1 )", "WHEN_ACCUM( IN( ON(SEN_1) ), 10, 1 )", "WHEN_OUT( IN( ON(OUT_A) ), 1 )" })
            CoreKit.Neg(check, "NEG 태그 표 " + l.Split('(')[0], c61, "tag", [$"TAG( 1, 0, {l} )"], "T-14", ref gcc);

        // 코어 NEG(hold_seq.c) → 편집기 오류
        (string Id, string Line, string Rule)[] neg =
        [
            ("ACCUM_STATE", "WAIT_ACCUM( AND( ON(SEN_1), FORWARD ), 10 )", "D-1"),
            ("ACCUM_ANY2", "WAIT_ACCUM_TO( AND( ANY(SEN_1, SEN_2), ANY(SEN_3, SEN_4) ), 10, 100, A )", "D-1"),
            ("ACCUM_BIG", "WAIT_ACCUM( IN( ON(SEN_1) ), 65535 )", "S-8"),
            ("ACCUM_NEG1", "WAIT_ACCUM( IN( ON(SEN_1) ), -1 )", "S-8"),
            ("HOLD_MIN", "WAIT_STABLE( IN( ON(SEN_1) ), PARAM_TIME( AS, MIN ) )", "D-7"),
            ("HOLD_PLACE", "WAIT_STABLE( IN( ON(SEN_1) ), PARAM_TIME( TFB_1, SEC ) )", "D-4"),
            ("HOLD_PCOUNT", "WAIT_ACCUM( IN( ON(SEN_1) ), PARAM_TIME( P_COUNT, SEC ) )", "N-1"),
            ("HOLD_FND", "WAIT_STABLE_TO( IN( ON(SEN_1) ), PARAM_TIME( ZZZ, SEC ), 100, A )", "N-1"),
            ("HOLD_STATE_PARAM", "WAIT_STABLE( AND( ON(SEN_1), FORWARD ), PARAM_TIME( AS, SEC ) )", "D-1"),
            ("WHENHOLD_STATE", "WHEN_STABLE( AND( ON(SEN_1), BACKWARD ), 10, A )", "D-1"),
            ("WHENHOLD_ANY2", "WHEN_ACCUM( AND( ANY(SEN_1, SEN_2), ANY(SEN_3, SEN_4) ), 10, A )", "D-1"),
            ("WHENHOLD_BIG", "WHEN_STABLE( IN( ON(SEN_1) ), 65535, A )", "S-8"),
            ("WHENHOLD_MIN", "WHEN_ACCUM( IN( ON(SEN_1) ), PARAM_TIME( AS, MIN ), A )", "D-7"),
            ("WHENOUT_STATE", "WHEN_OUT( AND( ON(OUT_EXT_1), FORWARD ), A )", "D-1"),
            ("STATE_SLOT", "WAIT( AND( STATE( 16, 1 ) ) )", "S-8"),
            ("STATE_ORDER", "WAIT( AND( STATE( 1, 9, 2 ) ) )", "S-8"),
            ("STATE_BIG", "WAIT( AND( STATE( 1, 0, 65536 ) ) )", "S-8"),
            ("STATE_NEG1", "WAIT( AND( STATE( 1, -1 ) ) )", "S-8"),
            ("STATE_TWO", "WAIT( AND( STATE( 1, 1 ), FORWARD ) )", "D-1"),
            ("STATE_ARITY", "WAIT( AND( STATE( 1 ) ) )", "N-2"),
            ("STATE_STABLE", "WAIT_STABLE( AND( ON(SEN_1), STATE( 1, 1 ) ), 10 )", "D-1"),
        ];
        foreach (var (id, line, rule) in neg) CoreKit.Neg(check, "NEG " + id, c61, "seq", [line], rule, ref gcc);
        // 괄호로 시작하는 식 (0 - 1) 은 읽지 못해 원문 블록(R-1 경고)이 된다: 편집기가 못 고치고 코어 컴파일이 막는다
        foreach (var l in new[] { "WAIT_ACCUM( IN( ON(SEN_1) ), (0 - 1) )", "WAIT( AND( STATE( 1, (0 - 1) ) ) )" })
        {
            check(Checker.Run(CoreKit.Seq(c61, l)).Any(f => f.Rule == "R-1"), "괄호 식은 원문 블록: " + l);
            if (CoreKit.GccReady) { gcc++; check(!CoreKit.GccCompiles("agv/seq_dsl.h", CoreKit.SeqText(l)), "코어는 괄호 음수를 막는다: " + l); }
        }
        CoreKit.Neg(check, "NEG STATE_MAN", c61, "man", ["MAN( 1, IDLE, AND( ON(SEN_1), STATE( 1, 1 ) ), ON( OUT_EXT_1 ) )", "MAN_ELSE( 1, IDLE, NO_OUT )"], "D-1", ref gcc);
        // hold_tag.c NEG
        (string Id, string Line, string Rule)[] tagNeg =
        [
            ("TAG_STATE3", "TAG_IF( 10, 0, AND( STATE( 3, 1 ), STATE( 4, 1 ), FORWARD ), AGV_STOP )", "D-1"),
            ("TAG_STATE_SLOT", "TAG_IF( 10, 0, AND( STATE( 16, 1 ) ), AGV_STOP )", "T-9"),
            ("TAG_DISPLAY_BIG", "TAG( 10, 0, ACS( DISPLAY, 256 ) )", "T-9"),
        ];
        foreach (var (id, line, rule) in tagNeg) CoreKit.Neg(check, "NEG " + id, c61, "tag", [line], rule, ref gcc);

        // S-18: 시퀀스당 스텝 한계(코어 v0.61.0 부터 999, 그 앞 64, 코어 모름 64)
        string Steps(int n) => string.Concat(Enumerable.Repeat("OBS_ON\n    ", n)).TrimEnd();
        int S18(string? core, int n) => Checker.Run(CoreKit.Seq(core, Steps(n).Split("\n    "))).Count(f => f.Rule == "S-18");
        // 표 끝의 L 둘·END_STOP 도 스텝이라 줄 수 + 3
        check(S18(c60, 61) == 0 && S18(c60, 62) == 1 && S18(null, 62) == 1 && S18(c61, 62) == 0 && S18(c61, 996) == 0 && S18(c61, 997) == 1, "S-18: v0.60.0 64 · v0.61.0 999 · 코어 모름 64");
        var s18 = Checker.Run(CoreKit.Seq(c61, Steps(1000).Split("\n    "))).First(f => f.Rule == "S-18");
        check(s18.Text.Contains("999 이하") && Checker.Run(CoreKit.Seq(c60, Steps(70).Split("\n    "))).First(f => f.Rule == "S-18").Text.Contains("64 이하"), "S-18 글: 한계 숫자");

        // S-19: WHEN 묶음 안 유지 가지 셋째(앱 압축 표 생성기와 같은 묶음 뜻)
        int S19(params string[] lines) => Checker.Run(CoreKit.Seq(c61, lines)).Count(f => f.Rule == "S-19");
        const string H1 = "WHEN_STABLE( IN( ON(SEN_1) ), 10, A )", H2 = "WHEN_ACCUM( IN( ON(SEN_2) ), 10, A )", H3 = "WHEN_STABLE( IN( ON(SEN_3) ), 10, B )", O = "WHEN_OUT( IN( ON(OUT_A) ), B )", W = "WHEN( IN( ON(SEN_4) ), B )";
        check(S19(H1, H2) == 0 && S19(H1, H2, H3) == 1 && S19(H1, H2, H3, H1) == 2, "S-19: 둘까지, 셋째부터 오류(줄마다)");
        check(S19(H1, W, H2, O, H3) == 1 && S19(H1, W, H2, "OTHERWISE( B )", H3) == 1, "S-19: 다른 WHEN·WHEN_OUT·OTHERWISE 가 끼어도 같은 묶음");
        check(S19(H1, H2, "SELECT_END_TO( 100, B )", H3) == 0 && S19(H1, H2, "L( A )", H3) == 0 && S19(H1, H2, "OBS_ON", H3, H1) == 0, "S-19: SELECT_END_TO·라벨·다른 스텝을 만나면 묶음 끝");
        check(Checker.Run(CoreKit.Seq(c61, H1, H2, H3)).Where(f => f.Rule == "S-19").All(f => f.Severity == Severity.Error && f.Text.Contains("290")), "S-19 는 오류(저장 막음)이고 실행 때 오류 290 을 알린다");
        check(Checker.Run(CoreKit.Seq(c61, "SELECT_BEGIN", H1, H2, H3, "SELECT_END")).Count(f => f.Rule == "S-5") == 0, "묶음 뒤 라벨 앞에서 S-5(닿지 않음)가 안 걸림");
        // 표 안 원문 블록(#if)이 낀 묶음: 그 안 글자까지 세고, 합이 둘을 넘으면 오류 대신 경고
        List<Finding> S19f(params string[] lines) => Checker.Run(CoreKit.Seq(c61, lines)).Where(f => f.Rule == "S-19").ToList();
        var rawIn = S19f(H1, "#if 0", "WHEN_STABLE( IN( ON(SEN_3) ), 10, B )", "#endif", H2);
        check(rawIn.Count >= 1 && rawIn.All(f => f.Severity == Severity.Warning), "S-19: 원문 블록 안 WHEN_STABLE 까지 세어 셋째면 경고 · " + CoreKit.Dump(rawIn));
        check(S19f(H1, "#if 0", "WHEN_ACCUM( IN( ON(SEN_3) ), 10, B )", "#endif").Count == 0 && S19f(H1, H2, "// WHEN_STABLE 주석", "/* WHEN_ACCUM */").Count == 0 && S19f(H1, "#define X_WHEN_STABLE 1", H2).Count == 0, "S-19: 합이 둘이면 조용, 주석·이름 일부는 안 센다");
        var rawAfter = S19f(H1, "#if 0", "OBS_ON", "#endif", H2, H3);
        check(rawAfter is [{ Severity: Severity.Error }], "S-19: 낱말이 없는 원문 블록은 묶음을 끊지도 경고로 낮추지도 않는다 · " + CoreKit.Dump(rawAfter));
        var rawStmt = S19f("#if 0", "WHEN_STABLE( IN( ON(SEN_3) ), 10, B )", "#endif", H1, H2);
        check(rawStmt.Count == 1 && rawStmt[0].Severity == Severity.Warning, "S-19: 원문 안 가지 다음에 오는 문장 가지도 경고 · " + CoreKit.Dump(rawStmt));
        check(S19f(H1, H2, H3).All(f => f.Severity == Severity.Error), "S-19: 원문 블록이 없으면 그대로 오류");

        if (CoreKit.GccReady) Console.WriteLine($"  gcc 대조 {gcc}건(v0.61.0)");
    }
}
