using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using TableEditor.Engine.Ops;

/// <summary>
/// 프로그램 업데이트: 판 비교, 매니페스트, 리다이렉트 태그, SHA 불일치 거부, 스테이징, 사용자 데이터 위치,
/// 그리고 교체 도우미(cmd + robocopy)를 %TEMP% 의 가짜 설치 폴더(한글·공백·&amp; 경로)에 실제로 돌린다: 성공, 실패 시 복구, 되돌리기, 실행 중.
/// </summary>
static class UpdaterTests
{
    public static void Run(Action<bool, string> check)
    {
        Versions(check);
        Manifest(check);
        Redirect(check);
        Sha(check);
        Helper(check);
    }

    static Version V(string s) => Updater.TryParseVersion(s, out var v) ? v : throw new FormatException(s);

    static void Versions(Action<bool, string> check)
    {
        check(V("v0.10.0") > V("0.9.0"), "판 0.10 > 0.9");
        check(V("v0.2.0") == V("0.2.0") && V("0.2") == new Version(0, 2, 0), "판 v 접두·두 자리");
        check(V("v0.2.0-rc1") == new Version(0, 2, 0) && V("0.2.0+abc1234") == new Version(0, 2, 0), "판 꼬리표 뗌");
        check(V("v1.0.0") > V("0.99.99") && V("0.1.10") > V("0.1.9"), "판 자리별 숫자 비교");
        check(!Updater.TryParseVersion("latest", out _) && !Updater.TryParseVersion("", out _), "판 아닌 글");
    }

    static void Manifest(Action<bool, string> check)
    {
        var sha = new string('A', 64);
        var r = Updater.ParseManifest($$"""
            { "version": "v0.2.0", "file": "TableEditor-v0.2.0-win-x64.zip", "sha256": "{{sha}}", "size": 102760448,
              "published": "2026-11-15T09:00:00Z", "page": "https://example.invalid/p", "notes": "변경" }
            """);
        check(r is { File: "TableEditor-v0.2.0-win-x64.zip", Size: 102760448, Page: "https://example.invalid/p", Notes: "변경", CanDownload: true }
            && r.Version == new Version(0, 2, 0) && r.Sha256 == new string('a', 64) && r.Published?.Year == 2026, "매니페스트 필드(sha 소문자로)");
        check(r!.AssetUrl == $"{Updater.Releases}/download/v0.2.0/TableEditor-v0.2.0-win-x64.zip", "매니페스트 → 자산 주소");
        var bad = Updater.ParseManifest("""{ "version": "0.3.0", "file": "..\\x.zip", "sha256": "123" }""");
        check(bad is { File: null, Sha256: null, CanDownload: false } && bad.Page.EndsWith("/tag/v0.3.0"), "매니페스트 경로 글자·짧은 sha 버림");
        check(Updater.ParseManifest("""{ "version": "x" }""") == null && Updater.ParseManifest("[]") == null, "매니페스트 판 틀림");
    }

    static void Redirect(Action<bool, string> check)
    {
        check(Updater.TagFromLocation("https://github.com/Leepy0/AGV_DCU_TableEditor/releases/tag/v0.1.0") == new Version(0, 1, 0), "리다이렉트 태그 v0.1.0");
        check(Updater.TagFromLocation("/Leepy0/AGV_DCU_TableEditor/releases/tag/v0.10.2/") == new Version(0, 10, 2), "리다이렉트 상대 경로");
        check(Updater.TagFromLocation("https://github.com/Leepy0/AGV_DCU_TableEditor/releases") == null && Updater.TagFromLocation(null) == null, "리다이렉트 태그 없음");
    }

    sealed class Bytes(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    static void Sha(Action<bool, string> check)
    {
        var dir = Temp("sha");
        try
        {
            var body = Encoding.UTF8.GetBytes("zip 대신");
            var good = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant();
            var wrong = new string('0', 64);
            using var http = new HttpClient(new Bytes(body));
            ReleaseInfo Rel(string sha) => new(new Version(0, 2, 0), "a.zip", sha, body.Length, "p", "", null);

            var e = Catch(() => Updater.DownloadAsync(Rel(wrong), dir, null, default, http).GetAwaiter().GetResult());
            check(e is UpdateException { Message: "SHA-256 불일치" }, "SHA 불일치 → 거부");
            check(!File.Exists(Path.Combine(dir, "a.zip")) && !File.Exists(Path.Combine(dir, "a.zip.partial")), "SHA 불일치 → 파일 남기지 않음");

            var got = Updater.DownloadAsync(Rel(good), dir, null, default, http).GetAwaiter().GetResult();
            check(File.ReadAllBytes(got).AsSpan().SequenceEqual(body) && !File.Exists(got + ".partial"), "SHA 같음 → .partial 에서 이름 바꿈");

            var part = Path.Combine(dir, "b.partial");
            File.WriteAllBytes(part, body);
            check(Catch(() => Updater.Accept(part, Path.Combine(dir, "b.zip"), wrong)) is UpdateException && !File.Exists(part), "Accept 불일치 → .partial 지움");
            check(Catch(() => Updater.DownloadAsync(Rel(good) with { Sha256 = null }, dir, null, default, http).GetAwaiter().GetResult()) is UpdateException,
                "sha 없는 릴리스(v0.1.0 리다이렉트) → 받지 않음");
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- 도우미 실제 실행 ----

    static void Helper(Action<bool, string> check)
    {
        var root = Temp("helper");
        var procs = new List<Process>();
        try
        {
            var install = Path.Combine(root, "가짜 설치 & 판");
            var upd = Path.Combine(root, "updates");
            Pack(install, "v0.1.0", "old", "gone.dll");

            // 사용자 데이터 위치
            check(Updater.DataInside(install, [Path.Combine(root, "data"), Path.Combine(root, "가짜 설치 & 판2", "x")]) == null, "데이터 밖(이름이 앞만 같은 폴더 포함)");
            check(Updater.DataInside(install, [Path.Combine(install, "site")]) != null && Updater.DataInside(install, [Updater.OldDir(install) + "\\s"]) != null,
                "데이터가 설치·.old 안 → 막음");
            check(Updater.DataInside(Updater.InstallDir, [Updater.DataDir]) == null, "이 PC: 설정 폴더는 설치 폴더 밖");
            check(Updater.CanReplace(install), "쓰기 권한(가짜 설치)");

            // 로컬 zip + 로컬 매니페스트 → SHA 검증 → 스테이징
            var src = Path.Combine(root, "pkg", "TableEditor-v0.2.0-win-x64");
            Pack(src, "v0.2.0", "new", Path.Combine("gcc", "bin", "gcc.exe"));
            var zip = Path.Combine(root, "TableEditor-v0.2.0-win-x64.zip");
            ZipFile.CreateFromDirectory(src, zip, CompressionLevel.Fastest, includeBaseDirectory: true);
            var manifest = Path.Combine(root, Updater.ManifestName);
            File.WriteAllText(manifest, $$"""{ "version": "0.2.0", "file": "{{Path.GetFileName(zip)}}", "sha256": "{{Updater.HashFile(zip)}}", "size": {{new FileInfo(zip).Length}} }""");
            var rel = Updater.ParseManifest(File.ReadAllText(manifest))!;
            Directory.CreateDirectory(upd);
            File.Copy(zip, Path.Combine(upd, rel.File! + ".partial"));
            Updater.Accept(Path.Combine(upd, rel.File! + ".partial"), Path.Combine(upd, rel.File!), rel.Sha256!);
            var stage = Updater.Stage(Path.Combine(upd, rel.File!), rel.Version, upd);
            check(Path.GetFileName(stage) == "TableEditor-v0.2.0-win-x64" && Updater.IsPackage(stage, rel.Version) && !File.Exists(Path.Combine(upd, rel.File!)),
                "스테이징: 맨 위 폴더, 검증, zip 지움");
            check(Updater.PendingOk(new PendingUpdate("0.2.0", stage, rel.Sha256!), new Version(0, 1, 0))
                && !Updater.PendingOk(new PendingUpdate("0.2.0", stage, rel.Sha256!), new Version(0, 2, 0)), "받은 판: 지금보다 새 판일 때만");
            File.WriteAllText(Path.Combine(src, "VERSION"), "v0.9.9\r\n");
            var badZip = Path.Combine(upd, "bad.zip");
            ZipFile.CreateFromDirectory(src, badZip, CompressionLevel.Fastest, includeBaseDirectory: true);
            check(Catch(() => Updater.Stage(badZip, new Version(0, 3, 0), upd)) is UpdateException && !Directory.Exists(Updater.StageDir(upd, new Version(0, 3, 0))),
                "스테이징: VERSION 다름 → 거부, 지움");

            // (1) 성공 교체: 앱(가짜 PID)이 끝나길 기다렸다가
            var app = Sleeper(2);
            procs.Add(app);
            var sw = Stopwatch.StartNew();
            var r1 = RunHelper(Updater.HelperStart(install, stage, app.Id, start: false, upd, wait: 30, tries: 3), upd);
            check(r1 == "ok", $"교체 결과 ok ({r1})");
            check(sw.Elapsed.TotalSeconds >= 1, "교체: PID 끝날 때까지 기다림");
            check(Read(install, "TableEditor.exe") == "new" && Read(install, "VERSION").Trim() == "v0.2.0" && File.Exists(Path.Combine(install, "gcc", "bin", "gcc.exe"))
                && !File.Exists(Path.Combine(install, "gone.dll")), "교체: 새 판만 남음");
            check(Read(Updater.OldDir(install), "TableEditor.exe") == "old" && File.Exists(Path.Combine(Updater.OldDir(install), "gone.dll")), "교체: 직전 판 .old");
            check(Updater.OldVersion(install) != null, ".old 판 읽음");

            // (2) 실패 시 복구: 새 판 파일 하나를 잠가 robocopy 가 실패
            var src3 = Path.Combine(root, "stage3");
            Pack(src3, "v0.3.0", "newer", "locked.dll");
            using (new FileStream(Path.Combine(src3, "locked.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var r2 = RunHelper(Updater.HelperStart(install, src3, 999_999, start: false, upd, wait: 30, tries: 3), upd);
                check(r2 == "fail-copy", $"복사 실패 결과 fail-copy ({r2})");
            }
            check(Read(install, "TableEditor.exe") == "new" && !File.Exists(Path.Combine(install, "locked.dll")), "복구: 설치 폴더 원래 판");
            check(Read(Updater.OldDir(install), "TableEditor.exe") == "old" && !Directory.Exists(install + ".old~"), "복구: .old 도 원래대로");

            // (3) 실행 중: 설치 폴더에서 연 콘솔의 tedit.exe(일하는 폴더 = 설치)가 돌면 앱이 찾아 알리고,
            //     도우미는 폴더 이름을 못 바꿔 아무것도 바꾸지 않는다. (실행 파일만으로는 이름 바꾸기가 막히지 않는다: 그 경우 tedit 은 .old 에서 계속 돈다)
            var tedit = Path.Combine(install, "tedit.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), tedit, overwrite: true);
            var busy = Process.Start(new ProcessStartInfo(tedit, "-n 60 127.0.0.1")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, WorkingDirectory = install })!;
            procs.Add(busy);
            check(Updater.RunningIn(install, Environment.ProcessId).Contains("tedit.exe"), "실행 중 tedit.exe 찾음");
            var r3 = RunHelper(Updater.HelperStart(install, src3, 999_999, start: false, upd, wait: 30, tries: 2), upd);
            check(r3 == "fail-busy", $"실행 중 결과 fail-busy ({r3})");
            check(Read(install, "TableEditor.exe") == "new" && Read(Updater.OldDir(install), "TableEditor.exe") == "old", "실행 중: 그대로");
            busy.Kill();
            busy.WaitForExit();
            check(Updater.RunningIn(install, Environment.ProcessId).Count == 0, "끝난 뒤 실행 중 없음");

            // (4) 되돌리기: .old → 설치
            var r4 = RunHelper(Updater.HelperStart(install, null, 999_999, start: false, upd, wait: 30, tries: 3), upd);
            check(r4 == "ok-rollback", $"되돌리기 결과 ok-rollback ({r4})");
            check(Read(install, "TableEditor.exe") == "old" && File.Exists(Path.Combine(install, "gone.dll")), "되돌리기: 직전 판");
            check(!Directory.Exists(Updater.OldDir(install)) && !Directory.Exists(install + ".rollback"), "되돌리기: .old·.rollback 남지 않음");
            check(RunHelper(Updater.HelperStart(install, null, 999_999, start: false, upd, wait: 30, tries: 3), upd) == "fail-noold", "되돌릴 .old 없음");
        }
        finally
        {
            foreach (var p in procs) { try { if (!p.HasExited) p.Kill(); } catch (InvalidOperationException) { } p.Dispose(); }
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    /// <summary>가짜 묶음: 가짜 exe·DLL 글 + VERSION. extra = 그 판에만 있는 파일.</summary>
    static void Pack(string dir, string version, string mark, string extra)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, Updater.ExeName), mark);
        File.WriteAllText(Path.Combine(dir, Updater.CliName), mark);
        File.WriteAllText(Path.Combine(dir, "hostfxr.dll"), mark);
        File.WriteAllText(Path.Combine(dir, "VERSION"), version + "\r\n");
        var e = Path.Combine(dir, extra);
        Directory.CreateDirectory(Path.GetDirectoryName(e)!);
        File.WriteAllText(e, mark);
    }

    static string Read(string dir, string name) => File.ReadAllText(Path.Combine(dir, name));

    static Process Sleeper(int seconds) =>
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"), $"-n {seconds + 1} 127.0.0.1")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;

    static string? RunHelper(ProcessStartInfo si, string dir)
    {
        using var p = Process.Start(si)!;
        if (!p.WaitForExit(120_000)) { p.Kill(true); return "timeout"; }
        return Updater.TakeResult(dir);
    }

    static string Temp(string name)
    {
        var d = Path.Combine(Path.GetTempPath(), $"te_upd_{name}_{Guid.NewGuid():N}"[..24]);
        Directory.CreateDirectory(d);
        return d;
    }

    static Exception? Catch(Action a)
    {
        try { a(); return null; }
        catch (Exception e) { return e; }
    }
}
