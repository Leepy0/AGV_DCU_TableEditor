using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;

/// <summary>
/// v0.2.4(코어 v0.60.0): 낱말·줄마다 최소 코어 버전(V-4 를 여러 버전으로), 수동 표 <c>MAN_START_SW</c>(M-8 뜻 없는 입력, M-9 중복),
/// 헤더 대조의 버전 판단. gcc 시험은 <c>DCU_CORE_ROOT</c> 가 있을 때만. 이름은 모두 지어낸 것.
/// </summary>
static class Core060Tests
{
    public static void Run(Action<bool, string> check)
    {
        int gcc = 0;
        var c60 = CoreKit.Core("c60", "v0.60.0");
        var c59 = CoreKit.Core("c59", "v0.59.0");
        var c57 = CoreKit.Core("c57", "v0.57.0");

        // 버전 판정
        var start = GrammarTable.Find("MAN_START_SW").Single();
        check(start.Since == "v0.60.0" && start.Place == Place.Manual && start.Role == Role.Statement && start.Args[0].Kind == ArgKind.ProfileSet, "MAN_START_SW 줄: v0.60.0 · 수동 표");
        check(GrammarTable.Fits(start, "v0.60.0") && GrammarTable.Fits(start, "v0.63.0") && !GrammarTable.Fits(start, "v0.59.0") && !GrammarTable.Fits(start, "v0.57.0") && !GrammarTable.Fits(start, (string?)null), "새 줄 쓰기: 사이트 core/VERSION 이 최소 버전 이상일 때만");
        check(GrammarTable.Fits(GrammarTable.Find("WAIT_STABLE", Place.Step).First(), "v0.58.0") && !GrammarTable.Fits(GrammarTable.Find("AND").Single(), "v0.57.0"), "v0.58.0 줄은 그대로");
        check(GrammarTable.SinceOf("MAN_START_SW") == "v0.60.0" && GrammarTable.SinceOf("WAIT") == "" && GrammarTable.SinceOf("AND") == "v0.58.0" && GrammarTable.SinceOf("STATE") == "v0.61.0", "이름의 최소 버전");
        check(GrammarTable.Compare("v0.9.0", "v0.10.0") < 0 && GrammarTable.Compare("v0.63.0", "v0.63.0") == 0, "버전 글 순서(숫자로)");

        // V-4: 코어 v0.60.0 미만의 MAN_START_SW
        List<Finding> V4(Site s) => Checker.Run(s).Where(f => f.Rule == "V-4").ToList();
        var line = "MAN_START_SW( PF_A, IN_EXT_9 )";
        check(V4(CoreKit.Man(c60, line)).Count == 0 && V4(CoreKit.Man(null, line)).Count == 0, "v0.60.0 · 코어 모름: MAN_START_SW 오류 아님");
        var low = V4(CoreKit.Man(c59, line));
        check(low.Count == 1 && low[0].Severity == Severity.Error && low[0].Text == "코어 v0.60.0 필요 · MAN_START_SW", "v0.59.0: 코어 v0.60.0 필요 · MAN_START_SW " + CoreKit.Dump(low));
        // 한 줄에 여러 버전: 가장 높은 버전을 알린다
        var multi = V4(CoreKit.Seq(c57, "WAIT( AND( ON(SEN_1) ) )", "WAIT_ACCUM( IN( ON(SEN_1) ), 5 )"));
        check(multi.Count == 2 && multi.Any(f => f.Text == "코어 v0.58.0 필요 · AND") && multi.Any(f => f.Text == "코어 v0.61.0 필요 · WAIT_ACCUM"), "줄마다 그 줄이 필요한 버전: " + CoreKit.Dump(multi));
        var both = V4(CoreKit.Seq(c57, "WAIT_ACCUM( AND( ON(SEN_1) ), 5 )"));
        check(both.Count == 1 && both[0].Text.StartsWith("코어 v0.61.0 필요 · ", StringComparison.Ordinal) && both[0].Text.Contains("AND") && both[0].Text.Contains("WAIT_ACCUM"), "한 줄 둘: 높은 버전 + 낱말 모음: " + CoreKit.Dump(both));
        check(V4(CoreKit.Man(c57, "MAN( PF_A, JOG, AND( KEY( UP ) ), NO_OUT )")).Count == 1, "수동 표 AND 는 v0.58.0 필요(KEY 단독은 옛 코어에도)");
        check(V4(CoreKit.Man(c57, "MAN( PF_A, JOG, KEY( UP ), NO_OUT )")).Count == 0, "수동 표 KEY( 키 ) 는 옛 코어에도 있다");

        // M-8: 뜻 없는 입력
        List<Finding> M(string rule, Site s) => Checker.Run(s).Where(f => f.Rule == rule).ToList();
        var ok = CoreKit.Man(c60, "MAN_START_SW( PF_A, IN_EXT_9 )", "MAN_START_SW( PF_B, SEN_1 )", "MAN_START_SW( PF_A, 31 )", "MAN_START_SW( PF_B, IN_IF_5 )");
        check(CoreKit.Errors(ok).Count(f => f.Rule is not ("V-2" or "V-3")) == 0 && M("M-8", ok).Count == 0, "MAN_START_SW 정상 입력(별칭·번호·IN_IF) 오류 0: " + CoreKit.Dump(CoreKit.Errors(ok)));
        var none = CoreKit.Man(c60, "MAN_START_SW( PF_A, IO_NONE )");
        check(M("M-8", none) is [{ Severity: Severity.Error }] m1 && m1[0].Text.Contains("IO_NONE"), "M-8: IO_NONE(미연결) 입력");
        var alias = CoreKit.Mk(c60, man: CoreKit.ManText("MAN_START_SW( PF_A, NC_IN )"), io: CoreKit.Io + "#define NC_IN IO_NONE\n");
        check(M("M-8", alias).Count == 1, "M-8: IO_NONE 별칭도");
        check(M("M-8", CoreKit.Man(c60, "MAN_START_SW( PF_A, IN_START_SW )")) is [{ Severity: Severity.Error }] m2 && m2[0].Text.Contains("IN_START_SW"), "M-8: IN_START_SW 자신");
        var self = CoreKit.Mk(c60, man: CoreKit.ManText("MAN_START_SW( PF_A, MY_START )"), io: CoreKit.Io + "#define MY_START IN_START_SW\n");
        check(M("M-8", self).Count == 1, "M-8: IN_START_SW 별칭도 자신");
        var outName = CoreKit.Man(c60, "MAN_START_SW( PF_A, OUT_A )");
        check(M("N-2", outName).Count == 1 && M("M-8", outName).Count == 0, "출력 이름은 N-2(종류 다름)");
        check(M("I-1", CoreKit.Man(c60, "MAN_START_SW( PF_A, 32 )")).Count == 1 && M("M-2", CoreKit.Man(c60, "MAN_START_SW( 0, IN_EXT_1 )")).Count == 1 && M("M-2", CoreKit.Man(c60, "MAN_START_SW( 64, IN_EXT_1 )")).Count == 1, "입력 ID 0~31 · 프로파일 1~63 밖");

        // M-9: 같은 (프로파일, 입력) 중복 = 경고
        var dup = CoreKit.Man(c60, "MAN_START_SW( PF_A, IN_EXT_9 )", "MAN_START_SW( PF_B, IN_EXT_9 )", "MAN_START_SW( PF_A, 9 )", "MAN_START_SW( PF_A, IN_EXT_9 )");
        var d = M("M-9", dup);
        check(d.Count == 1 && d[0].Severity == Severity.Warning && d[0].Text.Contains("PF_A"), "M-9: 같은 (프로파일, 입력) 중복 경고 " + CoreKit.Dump(d));
        var d2 = M("M-9", CoreKit.Man(c60, "MAN_START_SW( PF_A, IN_EXT_1 )", "MAN_START_SW( PF_A, 0 )"));
        check(d2.Count == 1, "M-9: 같은 입력을 이름·번호로 써도 중복");

        // 헤더 대조: 코어 버전을 알면 그 버전에 없던 이름은 안 바란다
        var names59 = GrammarTable.Names.Where(n => GrammarTable.SinceOf(n) is "" or "v0.58.0").ToList();
        string Hdr(IEnumerable<string> ns) => "#ifndef X_H\n#define X_H\n" + string.Join("\n", ns.Select(n => $"#define {n} 1")) + "\n#endif\n";
        check(HeaderCheck.Compare([Hdr(names59)], true, "v0.59.0").Same && !HeaderCheck.Compare([Hdr(names59)], true, "v0.60.0").Same && !HeaderCheck.Compare([Hdr(names59)], true).Same, "헤더 대조: v0.59.0 헤더는 v0.59.0 으로 같음, 버전을 모르면 새 이름이 모자람");
        check(HeaderCheck.Compare([Hdr(GrammarTable.Names)], true, "v0.63.0").Same && HeaderCheck.Compare([Hdr(GrammarTable.Names)], true, "v0.59.0").Same, "헤더에 새 이름이 더 있어도 문법 표에 있으면 같음");
        var verDir = CoreKit.Core("ver", "v0.60.0");
        check(HeaderCheck.VersionOf(verDir) == "v0.60.0", "VERSION 에서 코어 버전 읽기");
        var hv = Path.Combine(verDir, "include", "agv");
        Directory.CreateDirectory(hv);
        File.Delete(Path.Combine(verDir, "VERSION"));
        File.WriteAllText(Path.Combine(hv, "version.h"), "#define AGV_CORE_VER           v0_63_0      /* x */\n");
        check(HeaderCheck.VersionOf(verDir) == "v0.63.0", "VERSION 이 없으면 version.h 의 AGV_CORE_VER");

        // 파라미터 AD(60번)는 v0.61.0 부터(그 앞 코어 사이트에서는 없는 이름)
        check(CoreKit.Seq(CoreKit.Core("c61p", "v0.61.0"), "DELAY_PARAM( P_ACT_DELAY, DSEC )").Names.Find("AD") is { Kind: NameKind.FndParam, Value: 60 }
              && CoreKit.Seq(c60, "DELAY_PARAM( P_ACT_DELAY, DSEC )").Names.Find("AD") == null && CoreKit.Seq(null, "OBS_ON").Names.Find("AD") != null, "파라미터 60 AD: v0.61.0 부터, 코어 모름은 있음");
        var ad = Checker.Run(CoreKit.Seq(c60, "DELAY_PARAM( P_ACT_DELAY, DSEC )")).Where(f => f.Rule == "N-1").ToList();
        check(ad.Count == 1 && ad[0].Text.Contains("P_ACT_DELAY"), "v0.60.0 사이트의 P_ACT_DELAY = 없는 이름(N-1)");

        // 코어 컴파일
        CoreKit.Ok(check, "MAN_START_SW 줄", c60, "man", ["MAN_START_SW( PF_A, IN_EXT_9 )", "MAN_START_SW( PF_B, SEN_1 )", "MAN_START_SW( 3, 31 )"], ref gcc);
        CoreKit.Neg(check, "뜻 없음 MAN_START_SW IO_NONE(코어는 컴파일)", c60, "man", ["MAN_START_SW( PF_A, IO_NONE )"], "M-8", ref gcc, coreRejects: false);
        if (CoreKit.GccReady) Console.WriteLine($"  gcc 대조 {gcc}건(v0.60.0)");
    }
}
