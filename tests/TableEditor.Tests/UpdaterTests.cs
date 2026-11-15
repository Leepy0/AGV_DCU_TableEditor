using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using TableEditor.Engine.Ops;

/// <summary>
/// 프로그램 업데이트: 판 비교, 매니페스트, 리다이렉트 태그, SHA 불일치 거부, 실패 호스트 이름, files.txt 대조·막힘, 스테이징·.new 준비,
/// 그리고 교체 도우미(cmd)를 %TEMP% 의 가짜 설치 폴더(한글·공백·&amp; 경로, 도우미 폴더는 &amp;·()·^)에 실제로 돌린다:
/// 성공, 이전 .old 정리, 실행 중(막힘·그대로), 우리 것 아닌 .old(막힘·보존), 되돌리기, 끊긴 교체 복구, 잠금.
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
            { "version": "v0.2.0", "file": "TableEditor-v0.2.0-win-x64.zip", "sha256": "{{sha}}", "size": 102760448, "unpacked": 316000000,
              "published": "2026-11-15T09:00:00Z", "page": "https://example.invalid/p", "notes": "변경" }
            """);
        check(r is { File: "TableEditor-v0.2.0-win-x64.zip", Size: 102760448, Unpacked: 316000000, Page: "https://example.invalid/p", Notes: "변경", CanDownload: true }
            && r.Version == new Version(0, 2, 0) && r.Sha256 == new string('a', 64) && r.Published?.Year == 2026, "매니페스트 필드(sha 소문자로)");
        check(r!.AssetUrl == $"{Updater.Releases}/download/v0.2.0/TableEditor-v0.2.0-win-x64.zip", "매니페스트 → 자산 주소");
        var bad = Updater.ParseManifest("""{ "version": "0.3.0", "file": "..\\x.zip", "sha256": "123", "size": 1.5e30, "unpacked": -4 }""");
        check(bad is { File: null, Sha256: null, CanDownload: false, Size: 0, Unpacked: 0 } && bad.Page.EndsWith("/tag/v0.3.0"), "매니페스트 경로 글자·짧은 sha·큰 수·음수 버림");
        check(Updater.ParseManifest("""{ "version": "x" }""") == null && Updater.ParseManifest("[]") == null, "매니페스트 판 틀림");
    }

    static void Redirect(Action<bool, string> check)
    {
        check(Updater.TagFromLocation("https://github.com/Leepy0/AGV_DCU_TableEditor/releases/tag/v0.1.0") == new Version(0, 1, 0), "리다이렉트 태그 v0.1.0");
        check(Updater.TagFromLocation("/Leepy0/AGV_DCU_TableEditor/releases/tag/v0.10.2/") == new Version(0, 10, 2), "리다이렉트 상대 경로");
        check(Updater.TagFromLocation("https://github.com/Leepy0/AGV_DCU_TableEditor/releases") == null && Updater.TagFromLocation(null) == null, "리다이렉트 태그 없음");
    }

    /// <summary>가짜 서버: github.com 은 자산 호스트로 넘기고, 자산 호스트는 body 를 준다(down = 자산 호스트 연결 실패).</summary>
    sealed class Fake(byte[] body, bool down = false) : HttpMessageHandler
    {
        public int Hits;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Hits++;
            if (request.RequestUri!.Host == "github.com")
            {
                var res = new HttpResponseMessage(HttpStatusCode.Found);
                res.Headers.Location = new Uri("https://assets.example.invalid/blob");
                return Task.FromResult(res);
            }
            if (down) throw new HttpRequestException("막힘");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    static void Sha(Action<bool, string> check)
    {
        var dir = Temp("sha");
        try
        {
            var body = Encoding.UTF8.GetBytes("zip 대신");
            var good = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant();
            var wrong = new string('0', 64);
            using var http = new HttpClient(new Fake(body));
            ReleaseInfo Rel(string? sha) => new(new Version(0, 2, 0), "a.zip", sha, body.Length, "p", "", null);

            var e = Catch(() => Updater.DownloadAsync(Rel(wrong), dir, null, default, http).GetAwaiter().GetResult());
            check(e is UpdateException { Message: "SHA-256 불일치" }, "SHA 불일치 → 거부");
            check(!File.Exists(Path.Combine(dir, "a.zip")) && !File.Exists(Path.Combine(dir, "a.zip.partial")), "SHA 불일치 → 파일 남기지 않음");

            var got = Updater.DownloadAsync(Rel(good), dir, null, default, http).GetAwaiter().GetResult();
            check(File.ReadAllBytes(got).AsSpan().SequenceEqual(body) && !File.Exists(got + ".partial"), "SHA 같음 → .partial 에서 이름 바꿈(리다이렉트 따라감)");

            var part = Path.Combine(dir, "b.partial");
            File.WriteAllBytes(part, body);
            check(Catch(() => Updater.Accept(part, Path.Combine(dir, "b.zip"), wrong)) is UpdateException && !File.Exists(part), "Accept 불일치 → .partial 지움");
            check(Catch(() => Updater.DownloadAsync(Rel(null), dir, null, default, http).GetAwaiter().GetResult()) is UpdateException,
                "sha 없는 릴리스(v0.1.0 리다이렉트) → 받지 않음");

            File.Delete(got);
            using var blocked = new HttpClient(new Fake(body, down: true));
            var e2 = Catch(() => Updater.DownloadAsync(Rel(good), dir, null, default, blocked).GetAwaiter().GetResult());
            check(e2 is UpdateException { Message: "연결 실패 · assets.example.invalid" }, $"자산 호스트가 막힘 → 그 호스트 이름 ({e2?.Message})");
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
            var upd = Path.Combine(root, "upd & (x) ^");
            Pack(install, "v0.1.0", "old", "gone.dll");

            // 사용자 데이터 위치
            check(Updater.DataInside(install, [Path.Combine(root, "data"), Path.Combine(root, "가짜 설치 & 판2", "x")]) == null, "데이터 밖(이름이 앞만 같은 폴더 포함)");
            check(Updater.DataInside(install, [Path.Combine(install, "site")]) != null && Updater.DataInside(install, [Updater.NewDir(install) + "\\s"]) != null,
                "데이터가 설치·.new 안 → 막음");
            check(Updater.DataInside(Updater.InstallDir, [Updater.DataDir]) == null, "이 PC: 설정 폴더는 설치 폴더 밖");
            check(Updater.CanReplace(install), "쓰기 권한(가짜 설치)");
            check(Updater.Check(install) is { Ours: true, Complete: true } && Updater.Blockers(install, "prepare").Count == 0, "files.txt 대조: 온전·우리 것");

            // H1: 목록 밖 파일·.git·우리 것 아닌 옆 폴더 → 막힘
            var memo = Path.Combine(install, "내 메모.txt");
            File.WriteAllText(memo, "사용자");
            var b1 = Updater.Blockers(install, "prepare");
            check(b1.Any(x => x.Contains("내 메모.txt")), $"목록 밖 파일 → 막힘·이름 ({string.Join(" / ", b1)})");
            File.Delete(memo);
            Directory.CreateDirectory(Path.Combine(install, "gcc", ".git"));
            check(Updater.Blockers(install, "prepare").Any(x => x.Contains(".git")), ".git → 막힘");
            Directory.Delete(Path.Combine(install, "gcc"), true);

            // 로컬 zip + 로컬 매니페스트 → 가짜 서버 → .new 준비(H3)
            var zip = Zip(root, "v0.2.0", "new", Path.Combine("gcc", "bin", "gcc.exe"));
            var manifest = Path.Combine(root, Updater.ManifestName);
            File.WriteAllText(manifest, $$"""{ "version": "0.2.0", "file": "{{Path.GetFileName(zip)}}", "sha256": "{{Updater.HashFile(zip)}}", "size": {{new FileInfo(zip).Length}} }""");
            var rel = Updater.ParseManifest(File.ReadAllText(manifest))!;
            var server = new Fake(File.ReadAllBytes(zip));
            using (var http = new HttpClient(server))
            {
                var nw = Updater.PrepareAsync(rel, install, upd, null, default, http).GetAwaiter().GetResult();
                check(nw == Updater.NewDir(install) && Updater.IsReady(nw, rel.Sha256) && Updater.Pending(install, new Version(0, 1, 0)) == new Version(0, 2, 0),
                    ".new 준비: 완성 표지·받은 판");
                check(!Directory.EnumerateDirectories(upd, "stage-*").Any() && !File.Exists(Path.Combine(upd, rel.File!))
                    && !Directory.EnumerateDirectories(root, "*.tmp-*").Any(), "준비 뒤 zip·스테이징·임시 폴더 없음");
                int hits = server.Hits;
                Updater.PrepareAsync(rel, install, upd, null, default, http).GetAwaiter().GetResult();
                check(server.Hits == hits, "같은 sha 로 완성된 .new → 다시 받지 않음");
            }
            check(Updater.Blockers(install, "apply").Count == 0, "적용 막힘 없음");

            // H3: 덜 풀린 묶음(files.txt 에 있는데 zip 에 없음) → 거부, 임시 폴더 없음
            var broken = Zip(root, "v0.3.0", "x", "y.dll", drop: "y.dll");
            var relB = new ReleaseInfo(new Version(0, 3, 0), Path.GetFileName(broken), Updater.HashFile(broken), new FileInfo(broken).Length, "p", "", null);
            var install3 = Path.Combine(root, "다른 설치");
            Pack(install3, "v0.1.0", "old", "a.dll");
            using (var http = new HttpClient(new Fake(File.ReadAllBytes(broken))))
            {
                var e = Catch(() => Updater.PrepareAsync(relB, install3, upd, null, default, http).GetAwaiter().GetResult());
                check(e is UpdateException { Message: "묶음 불완전 · y.dll" }, $"덜 풀린 묶음 → 거부 ({e?.Message})");
            }
            check(!Directory.Exists(Updater.NewDir(install3)) && !Directory.EnumerateDirectories(upd, "stage-*").Any()
                && !Directory.EnumerateDirectories(root, "*.tmp-*").Any(), "거부 뒤 .new·스테이징·임시 폴더 없음");

            // (1) 성공 교체: 앱(가짜 PID)이 끝나길 기다렸다가 이름 바꾸기 둘
            var app = Sleeper(2);
            procs.Add(app);
            var sw = Stopwatch.StartNew();
            var r1 = RunHelper(Updater.HelperStart("apply", install, Updater.NewDir(install), app.Id, start: false, upd, wait: 30, tries: 3), upd);
            check(r1 == "ok", $"교체 결과 ok ({r1})");
            check(sw.Elapsed.TotalSeconds >= 1, "교체: PID 끝날 때까지 기다림");
            check(Read(install, Updater.ExeName) == "new" && File.Exists(Path.Combine(install, "gcc", "bin", "gcc.exe")) && !File.Exists(Path.Combine(install, "gone.dll"))
                && Updater.Check(install).Complete, "교체: 새 판, 온전");
            check(Read(Updater.OldDir(install), Updater.ExeName) == "old" && !Directory.Exists(Updater.NewDir(install)) && !File.Exists(Updater.LockPath(upd)),
                "교체: 직전 판 .old, .new 없음, 잠금 없음");
            check(Updater.OldVersion(install) == "0.1.0" && Updater.Diagnose(install) == null, ".old 판 읽음, 고칠 것 없음");

            // (2) 실행 중: 설치 폴더에서 연 tedit(일하는 폴더 = 설치) → 앱이 찾고, 도우미는 아무것도 바꾸지 않음(.new·.old 그대로)
            PackNew(install, "v0.3.0", "newer");
            var tedit = Path.Combine(install, Updater.CliName);
            var teditBytes = File.ReadAllBytes(tedit);
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), tedit, overwrite: true);
            var busy = Process.Start(new ProcessStartInfo(tedit, "-n 60 127.0.0.1")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, WorkingDirectory = install })!;
            procs.Add(busy);
            check(Updater.RunningIn(install, Environment.ProcessId).Contains(Updater.CliName), "실행 중 tedit.exe 찾음");
            var r2 = RunHelper(Updater.HelperStart("apply", install, Updater.NewDir(install), 999_999, start: false, upd, wait: 30, tries: 2), upd);
            check(r2 == "fail-busy", $"실행 중 결과 fail-busy ({r2})");
            check(Read(install, Updater.ExeName) == "new" && Read(Updater.OldDir(install), Updater.ExeName) == "old" && Updater.IsReady(Updater.NewDir(install))
                && !Directory.Exists(install + ".old~"), "실행 중: 설치·.old·.new 그대로");
            busy.Kill();
            busy.WaitForExit();
            File.WriteAllBytes(tedit, teditBytes);
            check(Updater.RunningIn(install, Environment.ProcessId).Count == 0, "끝난 뒤 실행 중 없음");

            // (3) 두 번째 적용: 이전 .old(0.1.0)는 우리 것이라 지우고, 0.2.0 이 .old
            var r3 = RunHelper(Updater.HelperStart("apply", install, Updater.NewDir(install), 999_999, start: false, upd, wait: 30, tries: 3), upd);
            check(r3 == "ok" && Read(install, Updater.ExeName) == "newer" && Read(Updater.OldDir(install), Updater.ExeName) == "new" && !Directory.Exists(install + ".old~"),
                $"두 번째 적용: .old = 직전 판 ({r3})");

            // (4) H1: 우리 것 아닌 .old(표지 없음) → 막힘, 지우지 않음
            var old = Updater.OldDir(install);
            Directory.Move(old, old + "-keep");
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "남의 파일.txt"), "보존");
            PackNew(install, "v0.4.0", "newest");
            check(Updater.Blockers(install, "apply").Any(x => x.Contains("우리 것 아님")), "우리 것 아닌 .old → 앱이 막음");
            var r4 = RunHelper(Updater.HelperStart("apply", install, Updater.NewDir(install), 999_999, start: false, upd, wait: 30, tries: 3), upd);
            check(r4 == "fail-notours" && File.ReadAllText(Path.Combine(old, "남의 파일.txt")) == "보존" && Read(install, Updater.ExeName) == "newer",
                $"우리 것 아닌 .old → 도우미도 막음, 파일 보존 ({r4})");
            Directory.Delete(old, true);
            Directory.Move(old + "-keep", old);
            check(Updater.Discard(install) && !Directory.Exists(Updater.NewDir(install)), "[버리기] .new 지움");

            // (5) 되돌리기: .old → 설치
            check(Updater.Blockers(install, "rollback").Count == 0, "되돌리기 막힘 없음");
            var r5 = RunHelper(Updater.HelperStart("rollback", install, null, 999_999, start: false, upd, wait: 30, tries: 3), upd);
            check(r5 == "ok-rollback" && Read(install, Updater.ExeName) == "new" && !Directory.Exists(old) && !Directory.Exists(install + ".rollback"),
                $"되돌리기: 직전 판, .old·.rollback 없음 ({r5})");
            check(RunHelper(Updater.HelperStart("rollback", install, null, 999_999, start: false, upd, wait: 30, tries: 3), upd) == "fail-noold", "되돌릴 .old 없음");

            // (6) H2: 교체 중 끊김(설치 → .old 뒤 멈춤) → .old 에서 연 앱이 복구 제안 → recover
            PackNew(install, "v0.5.0", "five");
            Directory.Move(install, old);
            var fix = Updater.Diagnose(old);
            check(fix is { Mode: "recover" } && fix.Source == Updater.NewDir(install) && fix.Target == install, $"끊긴 교체 → 복구 제안(.new → 설치) ({fix})");
            var r6 = RunHelper(Updater.HelperStart("recover", fix!.Target, fix.Source, 999_999, start: false, upd, wait: 30, tries: 3), upd);
            check(r6 == "ok-recover" && Read(install, Updater.ExeName) == "five" && Updater.Check(install).Complete, $"복구: 새 판으로 마무리 ({r6})");

            // (7) H2: 설치 불완전(목록의 파일이 없음) + 온전한 .old → 되돌리기 제안
            File.Delete(Path.Combine(install, "hostfxr.dll"));
            var fix2 = Updater.Diagnose(install);
            check(fix2 is { Mode: "rollback" } && fix2.Source == old, $"설치 불완전 → .old 되돌리기 제안 ({fix2})");

            // (8) 잠금: 도우미가 기다리는 중 끊김 → 잠금이 남음(10분 동안 도는 중으로 봄), 오래되면 지우고 결과 없음 알림
            var waiter = Sleeper(30);
            procs.Add(waiter);
            using (var h = Process.Start(Updater.HelperStart("apply", install, Updater.NewDir(install), waiter.Id, start: false, upd, wait: 60, tries: 3))!)
            {
                for (int i = 0; i < 50 && !File.Exists(Updater.LockPath(upd)); i++) Thread.Sleep(100);
                h.Kill(entireProcessTree: true);
                h.WaitForExit();
            }
            check(Updater.LockFresh(upd) && Updater.TakeResult(upd) == null, "끊긴 도우미 → 잠금 남음, 결과 아직 모름");
            File.SetLastWriteTimeUtc(Updater.LockPath(upd), DateTime.UtcNow.AddMinutes(-11));
            Updater.DropStaleLock(upd);
            check(!File.Exists(Updater.LockPath(upd)) && Updater.TakeResult(upd) == "none", "오래된 잠금 → 지움, 결과 없음(none)");
        }
        finally
        {
            foreach (var p in procs) { try { if (!p.HasExited) p.Kill(); } catch (InvalidOperationException) { } p.Dispose(); }
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    /// <summary>가짜 묶음: 가짜 exe·DLL 글 + VERSION + files.txt(크기 TAB 상대 경로). extra = 그 판에만 있는 파일.</summary>
    static void Pack(string dir, string version, string mark, string extra)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, Updater.ExeName), mark);
        File.WriteAllText(Path.Combine(dir, Updater.CliName), mark);
        File.WriteAllText(Path.Combine(dir, "hostfxr.dll"), mark + mark);
        File.WriteAllText(Path.Combine(dir, "VERSION"), version + "\r\n");
        var e = Path.Combine(dir, extra);
        Directory.CreateDirectory(Path.GetDirectoryName(e)!);
        File.WriteAllText(e, mark);
        var full = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
        File.WriteAllLines(Path.Combine(dir, Updater.ListName),
            Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != Updater.ListName)
                .Select(f => $"{new FileInfo(f).Length}\t{f[full.Length..]}"));
    }

    /// <summary>설치 폴더 옆에 완성된 .new 를 바로 둔다(받기는 위에서 시험).</summary>
    static void PackNew(string install, string version, string mark)
    {
        var nw = Updater.NewDir(install);
        Pack(nw, version, mark, "n.dll");
        File.WriteAllText(Path.Combine(nw, Updater.ReadyName), new string('0', 64));
    }

    /// <summary>묶음 zip(맨 위 폴더 하나, release.ps1 과 같음). drop = files.txt 에는 있고 zip 에서는 뺄 파일.</summary>
    static string Zip(string root, string version, string mark, string extra, string? drop = null)
    {
        var src = Path.Combine(root, "pkg-" + version, $"TableEditor-{version}-win-x64");
        Pack(src, version, mark, extra);
        if (drop != null) File.Delete(Path.Combine(src, drop));
        var zip = Path.Combine(root, $"TableEditor-{version}-win-x64.zip");
        ZipFile.CreateFromDirectory(src, zip, CompressionLevel.Fastest, includeBaseDirectory: true);
        return zip;
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
