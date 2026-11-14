using System.Text;
using TableEditor.Engine.Ops;

/// <summary>작업 3 시험: 오류 글 해석, 프로세스 실행, 도구 없음. 사이트 이름·경로는 모두 지어낸 것.</summary>
static class OpsTests
{
    public static void Run(Action<bool, string> check)
    {
        Errors(check);
        Field(check);
        Runner(check);
        Missing(check);
        Stamp(check);
    }

    static void Errors(Action<bool, string> check)
    {
        var site = @"C:\work\app\site\T900_FAKE";
        var lines = new[]
        {
            "core v0.0.0 abc1234 cl2000 v18.12.2.LTS",
            @"""C:\work\app\site\T900_FAKE\manual_table.c"", line 24: error #20: identifier ""PF_DEMO_BELTX"" is undefined",
            @"""C:\work\app\site\T900_FAKE\tag_table.c"", line 7: warning #225-D: function declared implicitly",
            @"""C:\work\app\site\T900_FAKE\tag_table.c"", line 9: error #95: the size of an array must be greater than zero",
            @"2 errors detected in the compilation of ""C:\work\app\site\T900_FAKE\manual_table.c"".",
            "c28: COMPILE FAILED: 1",
            @"""cmd/F2808_app_direct.cmd"", line 27: error: program will not fit into available",
            "memory.  placement with alignment/blocking fails for section \".econst\" size 0x2000",
            "error: errors encountered during linking;",
            "error: unresolved symbols remain",
            @"""C:\work\app\build\c28\x.out"" not built",
            "LINK FAILED",
        };
        var e = BuildErrors.Parse(lines, site);
        var a = e.Single(x => x.Number == 20);
        check(a.Source == "cl2000" && a.File == "manual_table.c" && a.Line == 24 && !a.IsWarning && a.Text == "identifier \"PF_DEMO_BELTX\" is undefined", "BuildErrors cl2000 #20");
        check(e.Count(x => x.Source == "cl2000") == 3 && e.Single(x => x.Number == 225).IsWarning, "BuildErrors cl2000 경고 구분, 요약 줄은 제외");
        check(e.Single(x => x.Number == 95) is { File: "tag_table.c", Line: 9 }, "BuildErrors #95 줄만");
        check(e.Any(x => x.Source == "build" && x.Text == "COMPILE FAILED: 1") && e.Any(x => x.Source == "build" && x.Text == "LINK FAILED"), "BuildErrors 빌드 스크립트");
        check(e.Any(x => x.Source == "link" && x.File == "cmd/F2808_app_direct.cmd" && x.Line == 27) && e.Any(x => x.Source == "link" && x.Text == "unresolved symbols remain")
            && e.Any(x => x.Source == "link" && x.Text.EndsWith("not built")), "BuildErrors 링커");

        var gcc = new[]
        {
            @"C:\work\app\site\T900_FAKE\tag_table.c:103:50: error: 'FB' undeclared here (not in a function)",
            @"C:\work\app\core\include/agv/tag_dsl.h:55:3: error: size of unnamed array is negative",
            @"C:\work\app\site\T900_FAKE\tag_table.c:120:9: note: in expansion of macro 'TAG_X'",
            @"C:\work\app\site\T900_FAKE\tag_table.c:130:5: warning: excess elements in array initializer",
            "    TAG_IF( 1 )",
            "규칙 3(태그 921): 조건이 겹침",
            "규칙 4: 표 번호가 255 를 넘음",
            "규칙 1100 개: 1024 넘음",
            "규칙 222(범위 0), 조건 25, 입력 3, 동작 89 -> build/tag_pack/tag_packed.c",
            "seq_pack: 시퀀스 스텝이 64 를 넘는다: DEMO_SEQ 스텝 70",
            "seq_pack: 시퀀스 ID 는 64 미만: DEMO_SEQ ID 70",
            "seq_pack: 왕복이 다르다 (3)",
            "seq_pack: ACT 스텝이 64 를 넘는다: DEMO_ACT 스텝 66",
        };
        var g = BuildErrors.Parse(gcc, site);
        check(g.Any(x => x.Source == "gcc" && x.File == "tag_table.c" && x.Line == 103 && x.Text.StartsWith("'FB' undeclared")), "BuildErrors gcc 오류");
        check(g.Any(x => x.Source == "gcc" && x.File == "tag_table.c" && x.Line == 120 && x.Text == "size of unnamed array is negative"), "BuildErrors gcc 매크로 검사: note 의 사이트 줄");
        check(g.Any(x => x.IsWarning && x.File == "tag_table.c" && x.Line == 130), "BuildErrors gcc 경고");
        check(g.Any(x => x.Source == "tag_pack_gen" && x.Number == 3 && x.Text == "태그 921: 조건이 겹침") && g.Any(x => x.Number == 4) && g.Any(x => x.Number == 1100), "BuildErrors tag_pack_gen");
        check(g.Count(x => x.Source == "tag_pack_gen") == 3, "BuildErrors 성공 줄은 오류 아님");
        check(g.Any(x => x.Source == "seq_pack_gen" && x.Text.StartsWith("시퀀스 스텝이")), "BuildErrors seq_pack_gen");
        check(g.Count(x => x.Source == "seq_pack_gen") == 4 && g.Any(x => x.Text == "왕복이 다르다 (3)") && g.Any(x => x.Text.StartsWith("ACT 스텝이")), "BuildErrors seq_pack_gen 꼴 4가지");

        var chk = new[]
        {
            "core 수정됨 2개. 코어는 코어 저장소에서 고치고 update_core.ps1 로 받는다",
            "  수정됨 lib/core.lib",
            "  없어짐 include/agv/x.h",
            "tag_packed.c: 바뀜 tag_table.c, 없어짐 site.h",
            "압축 표가 낡았다: python tools/tag_pack.py site/T900_FAKE",
            "core/ 가 없다. tools/update_core.ps1 을 먼저 실행한다",
        };
        var c = BuildErrors.Parse(chk, site);
        check(c.Count == 6 && c.All(x => x.Source == "check"), "BuildErrors check.ps1 6건");
        check(c.Any(x => x.File == "lib/core.lib" && x.Text == "수정됨") && c.Any(x => x.File == "tag_packed.c" && x.Text.StartsWith("바뀜")), "BuildErrors check 목록 줄");
        check(BuildErrors.Parse(["아무 줄", "", "core v1 2 cl2000 v1"], site).Count == 0, "BuildErrors 모르는 줄은 건너뜀");
    }

    static void Field(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tedit_ops_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "site.h"), "// x\n#ifndef SITE_H\n#define SITE_H\nint a;\nint b;\n");
            var copy = @"C:\work\app\build\site_field\T900_FAKE\";
            var e = BuildErrors.Parse(
            [
                $@"""{copy}site.h"", line 10: error #20: nope",
                $@"""{copy}site.h"", line 4: error #21: inserted line",
                $@"""{copy}site.h"", line 3: error #22: before",
                $@"""{copy}manual_table.c"", line 24: error #23: other file",
            ], dir);
            check(e.Single(x => x.Number == 20) is { File: "site.h", Line: 9 }, "-Field site.h 한 줄 밀림 되돌림");
            check(e.Single(x => x.Number == 21).Line == 3 && e.Single(x => x.Number == 22).Line == 3, "-Field 끼운 줄·앞 줄");
            check(e.Single(x => x.Number == 23) is { File: "manual_table.c", Line: 24 }, "-Field 다른 파일은 줄 그대로, 원본 이름");
        }
        finally { Directory.Delete(dir, true); }
    }

    static void Runner(Action<bool, string> check)
    {
        static ProcessResult Cmd(string script, TimeSpan? timeout = null, CancellationToken ct = default, Action<OutputLine>? on = null, Encoding? enc = null) =>
            ProcessRunner.RunAsync(new ProcessSpec("cmd", ["/c", script], null, timeout, enc), on, ct).GetAwaiter().GetResult();

        var got = new List<OutputLine>();
        var r = Cmd("echo one& echo two& echo err 1>&2& exit /b 3", on: got.Add);
        check(r.ExitCode == 3 && !r.Ok && r.StartError == null, "ProcessRunner 종료 코드 3");
        check(got.Where(l => !l.IsError).Select(l => l.Text).SequenceEqual(["one", "two"]) && got.Any(l => l.IsError && l.Text.TrimEnd() == "err"), "ProcessRunner 줄 단위 콜백, stdout·stderr 구분");
        check(Cmd("echo hi").Ok, "ProcessRunner 종료 코드 0");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t = Cmd("ping -n 30 127.0.0.1 >nul", TimeSpan.FromMilliseconds(600));
        check(t.TimedOut && !t.Ok && sw.Elapsed < TimeSpan.FromSeconds(10), "ProcessRunner 시간 제한");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        sw.Restart();
        var c = Cmd("ping -n 30 127.0.0.1 >nul", ct: cts.Token);
        check(c.Cancelled && !c.TimedOut && sw.Elapsed < TimeSpan.FromSeconds(10), "ProcessRunner 취소(자식 프로세스까지)");

        var s = ProcessRunner.RunAsync(new ProcessSpec(@"C:\no\such\tool.exe", [])).GetAwaiter().GetResult();
        check(s.StartError != null && !s.Ok && s.ExitCode == -1, "ProcessRunner 없는 실행 파일은 예외 대신 StartError");

        var oem = ProcessRunner.Oem();
        if (oem.GetBytes("한글").Length == 4 && oem.GetString(oem.GetBytes("한글")) == "한글")
        {
            var k = Cmd("echo 한글", enc: oem);
            check(k.Texts.Any(x => x.Trim() == "한글"), "ProcessRunner 콘솔 코드 페이지 한글");
        }
    }

    static void Missing(Action<bool, string> check)
    {
        var none = Path.Combine(Path.GetTempPath(), "tedit_no_such_dir", "gcc.exe");
        var g = ToolProbe.GccAsync(none).GetAwaiter().GetResult();
        check(!g.Found && g.Detail != null, "ToolProbe gcc 없음은 상태 값");
        var c = ToolProbe.Cl2000Async(Path.Combine(Path.GetTempPath(), "tedit_no_such_dir", "cl2000.exe")).GetAwaiter().GetResult();
        check(!c.Found && c.Version == null, "ToolProbe cl2000 없음은 상태 값");
        var all = ToolProbe.ProbeAllAsync(none, Path.Combine(Path.GetTempPath(), "tedit_no_such_dir", "cl2000.exe")).GetAwaiter().GetResult();
        check(all.Count == 4 && all.Select(x => x.Name).SequenceEqual(["cl2000", "git", "powershell", "gcc"]), "ToolProbe 4개 도구");

        var dir = Path.Combine(Path.GetTempPath(), "tedit_ops_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var p = SitePack.RunAsync(dir, "T900_FAKE", none).GetAwaiter().GetResult();
            check(!p.Ok && p.Reason!.Contains("gcc 없음"), "TagPack gcc 없음은 실패 값");
            Directory.CreateDirectory(Path.Combine(dir, "site", "T900_FAKE"));
            File.WriteAllText(Path.Combine(dir, "site", "T900_FAKE", "foo_packed.c"), "/* x */\n");
            var p3 = SitePack.RunAsync(dir, "T900_FAKE", Environment.ProcessPath).GetAwaiter().GetResult();
            check(!p3.Ok && p3.Reason!.Contains("모르는 생성물"), "SitePack 모르는 *_packed.c 는 실패 값");
            var p2 = SitePack.RunAsync(dir, @"..\x", none).GetAwaiter().GetResult();
            check(!p2.Ok && p2.Step == "입력", "TagPack 잘못된 사이트 이름");
            var b = SiteBuild.RunAsync(dir, "T900_FAKE").GetAwaiter().GetResult();
            check(!b.Ok && b.Reason!.Contains("check.ps1"), "SiteBuild check.ps1 없음은 실패 값");
        }
        finally { Directory.Delete(dir, true); }
        check(SiteBuild.DefaultName("T143_ANY") == "agv_143" && SiteBuild.DefaultName("T083_ANY") == "agv_083", "SiteBuild 기본 이미지 이름");
    }

    static void Stamp(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tedit_ops_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.h"), "x\r\ny\r\n"u8.ToArray());
            // CRLF 를 LF 로 맞춘 해시: LF 파일과 같아야 한다
            check(SitePack.NormHash("x\r\ny\r\n"u8.ToArray()) == SitePack.NormHash("x\ny\n"u8.ToArray()), "NormHash 줄 끝 무관");
            check(SitePack.NormHash("a"u8.ToArray()) == "ca978112ca1bbdcafac231b39a23dc4da786eff8147c4e72b9807785afee48bb", "NormHash SHA-256");
            var data = "/* 생성물 */\r\nint x;\r\n"u8.ToArray();
            var s = Encoding.UTF8.GetString(SitePack.Stamp(data, ["a.h"], dir));
            var lines = s.Split("\r\n");
            check(lines[0] == "/* 생성물 */" && lines[1].StartsWith("/* 원본 해시(SHA-256, 줄 끝 LF).") && lines[2] == " * src a.h " + SitePack.NormHash("x\ny\n"u8.ToArray())
                && lines[3] == " */" && lines[4] == "int x;", "Stamp 첫 주석 다음 줄에 원본 해시, 줄 끝 CRLF 유지");
        }
        finally { Directory.Delete(dir, true); }
    }
}
