using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using TableEditor.Engine.Ops;

/// <summary>
/// 프로그램 업데이트: 버전 비교, 매니페스트, 리다이렉트 태그, SHA 불일치 거부, 실패 호스트 이름, files.txt 대조·막힘, 스테이징·.new 준비, 차례 잠금, 정리,
/// 그리고 교체 도우미(cmd)를 %TEMP% 의 가짜 설치 폴더(한글·공백·&amp; 경로, 도우미 폴더는 &amp;·()·^)에 실제로 돌린다.
/// 도우미는 아무것도 지우지 않는다(버린 폴더는 .trash-*, 앱이 검사 뒤 지움): 사용자 파일이 사라지는 경로(검토 R1·R2·R3)가 없는지 본다.
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
        Links(check);
        Fallbacks(check);
    }

    /// <summary>
    /// 확인 길(시스템 프록시 / 직접 연결): 프록시 길이 시간 초과하면 매니페스트를 직접 길로(나머지 방법을 프록시로 24초 기다리지 않는다), 된 길을 기억해 다음 확인·받기가 그 길부터,
    /// 실패 글은 방법마다 한 줄(방법 · 길 · 호스트 · 까닭 · 초), 끝으로 git ls-remote 최신 정식 태그. 가짜 방법으로 돌린다(망 없음).
    /// </summary>
    static void Fallbacks(Action<bool, string> check)
    {
        var proxy = new HttpClient();
        var direct = new HttpClient();
        HttpClient Pick(Updater.NetRoute r) => r == Updater.NetRoute.Proxy ? proxy : direct;
        var rel = new ReleaseInfo(new Version(1, 2, 3), null, null, 0, "p", "", null);
        var calls = new List<string>();
        Updater.CheckMethod Hang(string name, string host) => new(name, async (h, t, ct) => { calls.Add(name + (ReferenceEquals(h, proxy) ? "/프록시" : "/직접")); t.Host = host; await Task.Delay(Timeout.Infinite, ct); return null; });
        Updater.CheckMethod Fail(string name, string host, string what) => new(name, (h, t, _) => { calls.Add(name + (ReferenceEquals(h, proxy) ? "/프록시" : "/직접")); t.Host = host; throw new UpdateException(what); });
        Updater.CheckMethod OnlyDirect(string name, ReleaseInfo? r) => new(name, async (h, t, _) =>
        {
            calls.Add(name + (ReferenceEquals(h, proxy) ? "/프록시" : "/직접"));
            t.Host = "github.com";
            if (ReferenceEquals(h, proxy)) throw new UpdateException("연결 실패 · github.com");
            await Task.Yield();
            return r;
        });
        Task<ReleaseInfo?> NoTags(CancellationToken _) => throw new UpdateException("깃 실패");
        var ms150 = TimeSpan.FromMilliseconds(150);
        var tick = default(CancellationToken);

        // 프록시 길 시간 초과 · 직접 길 성공: 매니페스트 둘만 시도하고 나머지 방법(프록시로 24초)은 건드리지 않는다
        Updater.CheckMethod[] sys = [new("매니페스트", async (h, t, ct) =>
            {
                calls.Add("매니페스트" + (ReferenceEquals(h, proxy) ? "/프록시" : "/직접"));
                t.Host = "release-assets.githubusercontent.com";
                if (ReferenceEquals(h, proxy)) await Task.Delay(Timeout.Infinite, ct);
                return rel;
            }), Fail("리다이렉트", "github.com", "연결 실패 · github.com"), Fail("API", "api.github.com", "연결 실패 · api.github.com")];
        var (got, route) = Updater.CheckAsync(sys, Pick, Updater.NetRoute.Proxy, NoTags, ms150, tick).GetAwaiter().GetResult();
        check(got?.Version == new Version(1, 2, 3) && route == Updater.NetRoute.Direct && string.Join(",", calls) == "매니페스트/프록시,매니페스트/직접", $"프록시 시간 초과 → 직접 매니페스트 ({string.Join(",", calls)})");

        // 기억한 길부터: 직접이 된 뒤에는 직접으로 시작(프록시 시도 없음)
        calls.Clear();
        (got, route) = Updater.CheckAsync(sys, Pick, Updater.NetRoute.Direct, NoTags, ms150, tick).GetAwaiter().GetResult();
        check(got != null && route == Updater.NetRoute.Direct && string.Join(",", calls) == "매니페스트/직접", "기억한 길(직접)부터 시작 · 프록시 안 건드림");

        // 받기도 같은 길: 기억이 받기 클라이언트를 고른다
        var saved = Updater.Route;
        Updater.Route = Updater.NetRoute.Direct;
        bool dl = ReferenceEquals(Updater.DownloadClient, Updater.ClientFor(Updater.NetRoute.Direct));
        Updater.Route = Updater.NetRoute.Proxy;
        dl &= ReferenceEquals(Updater.DownloadClient, Updater.ClientFor(Updater.NetRoute.Proxy)) && !ReferenceEquals(Updater.ClientFor(Updater.NetRoute.Proxy), Updater.ClientFor(Updater.NetRoute.Direct));
        Updater.Route = saved;
        check(dl, "받기 클라이언트 = 기억한 길 (프록시·직접은 다른 클라이언트)");

        // 매니페스트가 없는 릴리스(404 = 답은 옴): 나머지 방법은 그 길로만
        calls.Clear();
        Updater.CheckMethod[] noManifest = [OnlyDirect("매니페스트", null), OnlyDirect("리다이렉트", rel), Fail("API", "api.github.com", "x")];
        (got, route) = Updater.CheckAsync(noManifest, Pick, Updater.NetRoute.Proxy, NoTags, ms150, tick).GetAwaiter().GetResult();
        check(got != null && route == Updater.NetRoute.Direct && string.Join(",", calls) == "매니페스트/프록시,매니페스트/직접,리다이렉트/직접", $"404 답을 준 길로 이어감 ({string.Join(",", calls)})");

        // 모두 실패 → 방법마다 한 줄 + git 태그 줄
        calls.Clear();
        Updater.CheckMethod[] bad = [Hang("매니페스트", "release-assets.githubusercontent.com"), Fail("리다이렉트", "github.com", "연결 실패 · github.com"), Fail("API", "api.github.com", "HTTP 403 · api.github.com")];
        var e1 = Catch(() => Updater.CheckAsync(bad, Pick, Updater.NetRoute.Proxy, NoTags, ms150, tick).GetAwaiter().GetResult());
        var lines = e1?.Message.Split('\n') ?? [];
        check(e1 is UpdateException && lines.Length == 7
            && System.Text.RegularExpressions.Regex.IsMatch(lines[0], @"^매니페스트 · 프록시 · release-assets\.githubusercontent\.com · 시간 초과 \d+ s$")
            && System.Text.RegularExpressions.Regex.IsMatch(lines[1], @"^매니페스트 · 직접 · release-assets\.githubusercontent\.com · 시간 초과 \d+ s$")
            && lines[2].StartsWith("리다이렉트 · 프록시 · github.com · 연결 실패 ") && lines[3].StartsWith("리다이렉트 · 직접 · ")
            && lines[4].StartsWith("API · 프록시 · api.github.com · HTTP 403 ") && lines[6].StartsWith("깃 태그 · github.com · 깃 실패 "),
            "실패 글 = 방법 · 길 · 호스트 · 까닭 · 초 (" + string.Join(" / ", lines) + ")");

        // 둘 다 실패 → ls-remote 로 버전만
        var tagged = Updater.CheckAsync(bad, Pick, Updater.NetRoute.Proxy, _ => Task.FromResult<ReleaseInfo?>(rel), ms150, tick).GetAwaiter().GetResult();
        check(tagged.Release?.Version == new Version(1, 2, 3) && tagged.Release.File == null && !tagged.Release.CanDownload && tagged.Route == null, "모든 길 실패 → git 태그로 버전만 (받기 정보 없음)");

        string H(int i) => new string((char)('a' + i % 6), 40);
        var lsLines = new[] { $"{H(1)}\trefs/tags/v0.9.0", $"{H(2)}\trefs/tags/v0.10.0", $"{H(3)}\trefs/tags/v0.10.0^{{}}", $"{H(4)}\trefs/tags/v1.0.0-rc1", $"{H(5)}\trefs/tags/v0.2.1",
            $"{H(0)}\trefs/tags/dist-v2.0.0", $"{H(1)}\trefs/tags/v0.10.1" };
        check(Updater.LatestTag(lsLines) == new Version(0, 10, 1), "ls-remote: 정식 태그만, 버전 정렬 (0.10.1 > 0.9.0)");
        check(Updater.LatestTag([$"{H(1)}\trefs/tags/v1.0.0-rc1", "garbage"]) == null, "정식 태그가 없으면 null");
    }


    /// <summary>정션(링크): .old·.new·trash 가 정션이거나 안에 정션이 있어도 대상을 따라가 옮기거나 지우지 않는다. 빈 trash 트리는 표지 없이도 지운다.</summary>
    static void Links(Action<bool, string> check)
    {
        var root = Temp("link");
        try
        {
            var upd = Path.Combine(root, "upd");
            var install = Path.Combine(root, "TE");
            var victim = Path.Combine(root, "victim");
            Pack(install, "v0.2.0", "cur", "a.dll");
            Pack(victim, "v0.1.0", "old", "b.dll");
            File.WriteAllText(Path.Combine(victim, Updater.TrashName), "x");
            int Files() => Directory.GetFiles(victim, "*", SearchOption.AllDirectories).Length;
            int before = Files();

            // .old 가 정션 → 앱이 막고, 도우미도 막음(대상 그대로)
            Junction(Updater.OldDir(install), victim);
            PackNew(install, "v0.3.0", "new");
            check(Updater.IsLink(Updater.OldDir(install)) && !Updater.Check(Updater.OldDir(install)).Ours
                && Updater.Blockers(install, "apply").Any(x => x.Contains("우리 것 아님")), "정션 .old → 앱이 막음");
            using (var p = Updater.StartHelper(Updater.HelperStart("apply", install, Updater.NewDir(install), 999_999, false, upd, 5, 2), upd)) p.WaitForExit();
            check(Updater.TakeResult(upd) == "fail-notours" && Files() == before && Read(install, Updater.ExeName) == "cur", "정션 .old → 도우미도 막음, 대상 그대로");
            Directory.Delete(Updater.OldDir(install)); // 정션만 지움(대상은 그대로)

            // trash 자체가 정션 → 남기고 이름만
            var t1 = install + ".trash-1-a";
            Junction(t1, victim);
            var left = Updater.EmptyTrash(install);
            check(left.Contains(Path.GetFileName(t1)) && Files() == before, "정션 trash → 지우지 않음(대상 파일 그대로)");
            Directory.Delete(t1);

            // trash 안에 정션 → 목록 밖, 남김
            var t2 = install + ".trash-2-a";
            Pack(t2, "v0.1.0", "old", "c.dll");
            File.WriteAllText(Path.Combine(t2, Updater.TrashName), "x");
            Junction(Path.Combine(t2, "link"), victim);
            left = Updater.EmptyTrash(install);
            check(left.Contains(Path.GetFileName(t2)) && Files() == before, "trash 안 정션 → 지우지 않음(대상 그대로)");
            Directory.Delete(Path.Combine(t2, "link"));
            Directory.Delete(t2, true);

            // 표지까지 지운 뒤 끊긴 빈 trash 트리 → 표지 없이도 지움, 팝업 없음
            var t3 = install + ".trash-3-a";
            Directory.CreateDirectory(Path.Combine(t3, "gcc", "bin"));
            left = Updater.EmptyTrash(install);
            check(!Directory.Exists(t3) && left.Count == 0, "빈 trash 트리 → 지움");

            // .new 가 정션 → 받은 버전 아님, 버리기도 안 함
            Directory.Delete(Updater.NewDir(install), true);
            Junction(Updater.NewDir(install), victim);
            check(Updater.Pending(install, new Version(0, 0, 1)) == null && !Updater.Discard(install) && Files() == before, "정션 .new → 받은 버전 아님, 버리기 안 함");
            Directory.Delete(Updater.NewDir(install));
        }
        finally
        {
            foreach (var d in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
                if (Updater.IsLink(d)) Directory.Delete(d);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    static void Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /s /c \"mklink /J \"{link}\" \"{target}\"\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        p.WaitForExit();
        if (!Updater.IsLink(link)) throw new InvalidOperationException("정션을 못 만듦: " + link);
    }

    static Version V(string s) => Updater.TryParseVersion(s, out var v) ? v : throw new FormatException(s);

    static void Versions(Action<bool, string> check)
    {
        check(V("v0.10.0") > V("0.9.0"), "버전 0.10 > 0.9");
        check(V("v0.2.0") == V("0.2.0") && V("0.2") == new Version(0, 2, 0), "버전 v 접두·두 자리");
        check(!Updater.TryParseVersion("v0.2.0-rc1", out _) && !Updater.TryParseVersion("0.2.0+abc1234", out _) && !Updater.TryParseVersion("0.2.0.1", out _),
            "버전 꼬리표·넷째 자리 → 아님");
        check(V("v1.0.0") > V("0.99.99") && V("0.1.10") > V("0.1.9"), "버전 자리별 숫자 비교");
        check(!Updater.TryParseVersion("latest", out _) && !Updater.TryParseVersion("", out _), "버전 아닌 글");
    }

    static void Manifest(Action<bool, string> check)
    {
        var sha = new string('A', 64);
        var r = Updater.ParseManifest($$"""
            { "version": "v0.2.0", "file": "TableEditor-v0.2.0-net10-x64.zip", "sha256": "{{sha}}", "size": 102760448, "unpacked": 316000000,
              "published": "2026-11-15T09:00:00Z", "page": "https://example.invalid/p", "notes": "변경" }
            """);
        check(r is { File: "TableEditor-v0.2.0-net10-x64.zip", Size: 102760448, Unpacked: 316000000, Page: "https://example.invalid/p", Notes: "변경", CanDownload: true }
            && r.Version == new Version(0, 2, 0) && r.Sha256 == new string('a', 64) && r.Published?.Year == 2026, "매니페스트 필드(sha 소문자로)");
        check(r!.AssetUrl == $"{Updater.Releases}/download/v0.2.0/TableEditor-v0.2.0-net10-x64.zip", "매니페스트 → 자산 주소");
        var bad = Updater.ParseManifest("""{ "version": "0.3.0", "file": "..\\x.zip", "sha256": "123", "size": 1.5e30, "unpacked": -4 }""");
        check(bad is { File: null, Sha256: null, CanDownload: false, Size: 0, Unpacked: 0 } && bad.Page.EndsWith("/tag/v0.3.0"), "매니페스트 경로 글자·짧은 sha·큰 수·음수 버림");
        check(Updater.ParseManifest("""{ "version": "x" }""") == null && Updater.ParseManifest("[]") == null, "매니페스트 버전 틀림");
    }

    /// <summary>
    /// H1: 옛 클라이언트(v0.2.2 이하)가 읽는 TableEditor.json 은 버전 알림만. v0.2.2 의 Updater.cs·UpdateVm.cs 는 이 버전과 같다(git diff 없음):
    /// ParseManifest → file·sha 없음 → CanDownload false → CheckAsync 는 Available 로 두고 팝업 없이 토스트 "새 버전 X", [받기]는 토스트 "받기 불가 · 매니페스트 없음", 예외·오류 팝업 없음.
    /// </summary>
    static void LegacyManifest(Action<bool, string> check)
    {
        var legacy = Updater.ParseManifest("""
            { "version": "0.2.3", "published": "2026-11-21T00:00:00Z", "page": "https://example.invalid/tag/v0.2.3",
              "notes": ".NET 10 Desktop Runtime 설치 후 zip 을 받아 교체 / 변경" }
            """);
        check(legacy is { CanDownload: false, File: null, Sha256: null, Size: 0, Unpacked: 0, AssetUrl: null, Runtime: 0 }
            && legacy.Version == new Version(0, 2, 3) && legacy.Page.EndsWith("/tag/v0.2.3") && legacy.Notes.StartsWith(".NET 10 Desktop Runtime"),
            "알림 전용 매니페스트(TableEditor.json) → 버전·쪽·안내만, 받기 불가(옛 클라이언트도 같은 ParseManifest)");
        check(Updater.ManifestName == "TableEditor.net10.json" && Updater.LegacyManifestName == "TableEditor.json", "새 클라이언트는 net10 매니페스트 이름을 읽음");
    }

    /// <summary>H1: 매니페스트 runtime 칸, 받기 전 런타임 검사(가짜 설치 폴더·가짜 프로세스 메이저).</summary>
    static void Runtime(Action<bool, string> check)
    {
        var full = Updater.ParseManifest("""{ "version": "0.2.3", "runtime": "10", "file": "a.zip", "sha256": "%s" }""".Replace("%s", new string('b', 64)));
        check(full is { Runtime: 10, CanDownload: true }, "net10 매니페스트: runtime 칸");
        check(Updater.ParseManifest("""{ "version": "0.2.3", "runtime": 10 }""")!.Runtime == 0 && Updater.ParseManifest("""{ "version": "0.2.3", "runtime": "x" }""")!.Runtime == 0
            && Updater.ParseManifest("""{ "version": "0.2.3" }""")!.Runtime == 0, "runtime 칸 없음·글자 아님 → 0(검사 안 함)");
        // N5: 프로세스 런타임 폴더(...\dotnet\shared\Microsoft.NETCore.App\10.0.1\)에서 dotnet 설치 폴더까지 세 단계
        check(Updater.DotnetRoots(@"C:\x\dotnet\shared\Microsoft.NETCore.App\10.0.1\").First() == @"C:\x\dotnet", "dotnet 설치 폴더 = 런타임 폴더 세 단계 위");
        // N3: 받기 자체도 막는다(받기·준비 모두 DownloadAsync 를 지난다)
        var need99 = new ReleaseInfo(new Version(0, 2, 3), "a.zip", new string('b', 64), 1, "p", "", null, 0, 99);
        var blocked = Catch(() => Updater.DownloadAsync(need99, Path.GetTempPath(), null, default).GetAwaiter().GetResult());
        check(blocked is UpdateException { Message: "런타임 필요 · .NET 99 Desktop" }, $"런타임 없으면 받기 거부 ({blocked?.Message})");
        var root = Path.Combine(Path.GetTempPath(), "te_rt_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string[] roots = [root];
            ReleaseInfo Rel(int rt) => new(new Version(0, 2, 3), "a.zip", new string('b', 64), 1, "p", "", null, 0, rt);
            check(Updater.RuntimeNeeded(Rel(10), 10, roots) == null && Updater.RuntimeNeeded(Rel(9), 10, roots) == null && Updater.RuntimeNeeded(Rel(0), 10, roots) == null,
                "runtime 이 지금 메이저 이하 → 검사 통과(진행)");
            check(Updater.RuntimeNeeded(Rel(11), 10, roots) == 11, "더 큰 메이저 · 설치 폴더 없음 → 런타임 필요 11");
            Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.NETCore.App", "11.0.0"));
            check(Updater.RuntimeNeeded(Rel(11), 10, roots) == 11, "NETCore.App 만 있고 WindowsDesktop.App 없음 → 런타임 필요");
            Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App", "10.0.5"));
            check(Updater.RuntimeNeeded(Rel(11), 10, roots) == 11, "다른 메이저(10)만 설치 → 런타임 필요 11");
            Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App", "11.0.0-preview.1"));
            check(Updater.RuntimeNeeded(Rel(11), 10, roots) == null, "WindowsDesktop.App 11.* 설치 → 진행");
            check(Updater.RuntimeNeeded(Rel(110), 10, roots) == 110, "11.* 은 110 으로 오인하지 않음");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
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

            // 리뷰 9: 받기가 그 길에서 연결 실패하면 다른 길로 처음부터 한 번 더(SHA-256 검증 그대로)
            var alt = new Fake(body);
            using var altHttp = new HttpClient(alt);
            var got2 = Updater.DownloadAsync(Rel(good), dir, null, default, blocked, altHttp).GetAwaiter().GetResult();
            check(File.ReadAllBytes(got2).AsSpan().SequenceEqual(body) && alt.Hits > 0 && !File.Exists(got2 + ".partial"), "받기: 첫 길 연결 실패 → 다른 길로 한 번 더");
            File.Delete(got2);
            var alt2 = new Fake(body);
            using var altHttp2 = new HttpClient(alt2);
            var e3 = Catch(() => Updater.DownloadAsync(Rel(wrong), dir, null, default, http, altHttp2).GetAwaiter().GetResult());
            check(e3 is UpdateException { Message: "SHA-256 불일치" } && alt2.Hits == 0, "받기: SHA 불일치는 다른 길로 다시 받지 않음");
            using var blocked2 = new HttpClient(new Fake(body, down: true));
            var e4 = Catch(() => Updater.DownloadAsync(Rel(good), dir, null, default, blocked, blocked2).GetAwaiter().GetResult());
            check(e4 is UpdateException { Message: "연결 실패 · assets.example.invalid" } && !File.Exists(Path.Combine(dir, "a.zip")), "받기: 두 길 모두 실패 → 예외 · 파일 없음");
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- 도우미 실제 실행 ----

    static void Helper(Action<bool, string> check)
    {
        var root = Temp("helper");
        var procs = new List<Process>();
        Process Track(Process p) { procs.Add(p); return p; }
        try
        {
            var upd = Path.Combine(root, "upd & (x) ^");
            check(Updater.UpdateDirFor(Path.Combine(root, "a")) != Updater.UpdateDirFor(Path.Combine(root, "b"))
                && Updater.UpdateDirFor(Path.Combine(root, "a")) == Updater.UpdateDirFor(Path.Combine(root, "A")), "설치본마다 updates 폴더 따로");

            // 차례 잠금(파일, FileShare.None): 둘째는 못 잡고, 놓으면 잡힌다
            using (var g1 = Updater.TryGate(upd))
                check(g1 != null && Updater.TryGate(upd) == null, "차례 잠금: 다른 창은 못 잡음");
            using (var g2 = Updater.TryGate(upd)) check(g2 != null, "차례 잠금: 놓으면 다시 잡힘");

            // ---- 설치 폴더 검사 ----
            var install = Path.Combine(root, "가짜 설치 & 판");
            Pack(install, "v0.1.0", "old", "gone.dll");
            check(Updater.DataInside(install, [Path.Combine(root, "data"), Path.Combine(root, "가짜 설치 & 판2", "x")]) == null, "데이터 밖(이름이 앞만 같은 폴더 포함)");
            check(Updater.DataInside(install, [Path.Combine(install, "site")]) != null && Updater.DataInside(install, [install + ".trash-1-a\\s"]) != null,
                "데이터가 설치·.trash 안 → 막음");
            check(Updater.DataInside(Updater.InstallDir, [Updater.DataDir]) == null, "이 PC: 설정 폴더는 설치 폴더 밖");
            check(Updater.CanReplace(install), "쓰기 권한(가짜 설치)");
            check(Updater.Check(install) is { Ours: true, Complete: true } && Updater.Blockers(install, "prepare").Count == 0, "files.txt 대조: 온전·우리 것");

            var memo = Path.Combine(install, "내 메모.txt");
            File.WriteAllText(memo, "사용자");
            var b1 = Updater.Blockers(install, "prepare");
            check(b1.Any(x => x.Contains("내 메모.txt")), $"목록 밖 파일 → 막힘·이름 ({string.Join(" / ", b1)})");
            File.Delete(memo);
            Directory.CreateDirectory(Path.Combine(install, "gcc", ".git"));
            check(Updater.Blockers(install, "prepare").Any(x => x.Contains(".git")), ".git → 막힘");
            Directory.Delete(Path.Combine(install, "gcc"), true);
            // 탐색기 파일은 깊이·대소문자 없이 무시
            File.WriteAllText(Path.Combine(install, "DESKTOP.INI"), "[.ShellClassInfo]");
            File.SetAttributes(Path.Combine(install, "DESKTOP.INI"), FileAttributes.Hidden | FileAttributes.System);
            Directory.CreateDirectory(Path.Combine(install, "sub"));
            File.WriteAllText(Path.Combine(install, "sub", "thumbs.db"), "x");
            check(Updater.Check(install) is { Ours: true, Complete: true }, "desktop.ini·Thumbs.db 무시");
            File.SetAttributes(Path.Combine(install, "DESKTOP.INI"), FileAttributes.Normal);
            File.Delete(Path.Combine(install, "DESKTOP.INI"));
            Directory.Delete(Path.Combine(install, "sub"), true);

            // ---- 로컬 zip + 로컬 매니페스트 → 가짜 서버 → .new 준비 ----
            var zip = Zip(root, "v0.2.0", "new", Path.Combine("gcc", "bin", "gcc.exe"));
            var manifest = Path.Combine(root, Updater.ManifestName);
            File.WriteAllText(manifest, $$"""{ "version": "0.2.0", "file": "{{Path.GetFileName(zip)}}", "sha256": "{{Updater.HashFile(zip)}}", "size": {{new FileInfo(zip).Length}} }""");
            var rel = Updater.ParseManifest(File.ReadAllText(manifest))!;
            var server = new Fake(File.ReadAllBytes(zip));
            using (var http = new HttpClient(server))
            {
                var nw = Updater.PrepareAsync(rel, install, upd, null, default, http).GetAwaiter().GetResult();
                check(nw == Updater.NewDir(install) && Updater.IsReady(nw, rel.Sha256) && Updater.Pending(install, new Version(0, 1, 0)) == new Version(0, 2, 0),
                    ".new 준비: 완성 표지·받은 버전");
                check(!Directory.EnumerateDirectories(upd, "stage-*").Any() && !File.Exists(Path.Combine(upd, rel.File!))
                    && !Directory.EnumerateDirectories(root, "*.tmp-*").Any(), "준비 뒤 zip·스테이징·임시 폴더 없음");
                int hits = server.Hits;
                Updater.PrepareAsync(rel, install, upd, null, default, http).GetAwaiter().GetResult();
                check(server.Hits == hits, "같은 sha 로 완성된 .new → 다시 받지 않음");
            }

            // 덜 풀린 묶음(files.txt 에 있는데 zip 에 없음) → 거부, 임시 폴더 없음
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

            // 정리: 살아 있는 pid 의 tmp·차례를 남이 잡았으면 건너뜀, 주인이 끝난 tmp 는 지움
            var liveTmp = Updater.NewDir(install3) + ".tmp-" + Environment.ProcessId;
            var deadTmp = Updater.NewDir(install3) + ".tmp-999999";
            var liveStage = Path.Combine(upd, $"stage-0.3.0.tmp-{Environment.ProcessId}");
            Pack(liveTmp, "v0.3.0", "x", "y.dll");
            Pack(deadTmp, "v0.3.0", "x", "y.dll");
            Directory.CreateDirectory(liveStage);
            using (Updater.TryGate(upd)) Updater.Cleanup(upd, install3, new Version(0, 1, 0));
            check(Directory.Exists(deadTmp), "정리: 다른 창이 차례를 잡으면 건너뜀");
            Updater.Cleanup(upd, install3, new Version(0, 1, 0));
            check(Directory.Exists(liveTmp) && Directory.Exists(liveStage) && !Directory.Exists(deadTmp), "정리: 살아 있는 pid 의 tmp 남김, 끝난 것 지움");
            Directory.Delete(liveTmp, true);
            Directory.Delete(liveStage, true);

            // (1) 성공 교체: 앱(가짜 PID)이 끝나길 기다렸다가 이름 바꾸기 둘. 잠금은 도우미 PID 로 판정
            var app = Track(Sleeper(2));
            var sw = Stopwatch.StartNew();
            var r1 = RunHelper(Updater.HelperStart("apply", install, Updater.NewDir(install), app.Id, start: false, upd, wait: 30, tries: 3), upd, check);
            check(r1 == "ok", $"교체 결과 ok ({r1})");
            check(sw.Elapsed.TotalSeconds >= 1, "교체: PID 끝날 때까지 기다림");
            check(Read(install, Updater.ExeName) == "new" && File.Exists(Path.Combine(install, "gcc", "bin", "gcc.exe")) && !File.Exists(Path.Combine(install, "gone.dll"))
                && Updater.Check(install).Complete, "교체: 새 버전, 온전");
            check(Read(Updater.OldDir(install), Updater.ExeName) == "old" && !Directory.Exists(Updater.NewDir(install)) && !File.Exists(Updater.LockPath(upd)),
                "교체: 직전 버전 .old, .new 없음, 잠금 없음");
            check(Updater.OldVersion(install) == "0.1.0" && Updater.Diagnose(install) == null, "직전 버전 읽음, 고칠 것 없음");

            // (2a) 설치 폴더를 일하는 폴더로 쓰는 tedit → 폴더 이름 바꾸기가 막힘: 아무것도 바꾸지 않음
            PackNew(install, "v0.3.0", "newer");
            var tedit = Path.Combine(install, Updater.CliName);
            var teditBytes = File.ReadAllBytes(tedit);
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), tedit, overwrite: true);
            var busy = Track(Process.Start(new ProcessStartInfo(tedit, "-n 60 127.0.0.1")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, WorkingDirectory = install })!);
            check(Updater.RunningIn(install, Environment.ProcessId).Contains(Updater.CliName), "(2a) 실행 중 tedit.exe 찾음");
            var r2 = RunHelper(Updater.HelperStart("apply", install, Updater.NewDir(install), 999_999, start: false, upd, wait: 30, tries: 2), upd, check);
            check(r2 == "fail-busy", $"(2a) 일하는 폴더 핸들 → fail-busy ({r2})");
            check(Read(install, Updater.ExeName) == "new" && Read(Updater.OldDir(install), Updater.ExeName) == "old" && Updater.IsReady(Updater.NewDir(install))
                && Trash(install).Length == 0, "(2a) 설치·.old·.new 그대로, trash 없음");
            busy.Kill();
            busy.WaitForExit();
            File.WriteAllBytes(tedit, teditBytes);

            // (2b) 검토 R2: .old 에서 옛 버전 exe 만 도는 중(일하는 폴더는 밖) → 앱이 찾아 막는다.
            //      도우미를 그대로 돌려도 지우지 않는다: .old 는 trash 로 통째 옮겨지고, 도는 동안은 trash 도 남긴다
            var old = Updater.OldDir(install);
            var oldExe = Path.Combine(old, Updater.ExeName);
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), oldExe, overwrite: true);
            Relist(old);
            var oldRun = Track(Process.Start(new ProcessStartInfo(oldExe, "-n 60 127.0.0.1")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, WorkingDirectory = Path.GetTempPath() })!);
            check(Updater.RunningIn(install, Environment.ProcessId).Contains(Updater.ExeName), "(2b) .old 에서 도는 옛 버전 찾음 → 앱이 막음");
            var r2b = RunHelper(Updater.HelperStart("apply", install, Updater.NewDir(install), 999_999, start: false, upd, wait: 30, tries: 3), upd, check);
            var t2b = Trash(install);
            check(r2b == "ok" && t2b.Length == 1 && File.Exists(Path.Combine(t2b[0], Updater.ExeName)) && File.Exists(Path.Combine(t2b[0], "hostfxr.dll"))
                && File.Exists(Path.Combine(t2b[0], Updater.TrashName)), $"(2b) 도우미는 .old 를 통째 trash 로(지우지 않음) ({r2b})");
            check(Updater.EmptyTrash(install).Count == 0 && Directory.Exists(t2b[0]), "(2b) 도는 동안 trash 남김");
            oldRun.Kill();
            oldRun.WaitForExit();
            for (int i = 0; i < 30 && Trash(install).Length > 0; i++) { Updater.EmptyTrash(install); if (Trash(install).Length > 0) Thread.Sleep(100); } // 이미지 해제가 조금 늦다: 앱은 다음 시작 때 다시
            check(Trash(install).Length == 0, "(2b) 끝난 뒤 검사 통과 trash 지움");
            check(Updater.Blockers(install, "prepare").Count == 0 && Read(install, Updater.ExeName) == "newer" && Read(old, Updater.ExeName) == "new",
                "(2b) 다음 업데이트가 막히지 않음");

            // (3) 검토 R3: .old 에 사용자 파일 → 앱이 막고, 도우미를 그대로 돌려도 지우지 않음(trash 에 보존·안 지우고 이름을 알림)
            File.WriteAllText(Path.Combine(old, "user.txt"), "u");
            PackNew(install, "v0.4.0", "four");
            check(Updater.Blockers(install, "apply").Any(x => x.Contains("우리 것 아님")), "(3) .old 에 사용자 파일 → 앱이 막음");
            var r3 = RunHelper(Updater.HelperStart("apply", install, Updater.NewDir(install), 999_999, start: false, upd, wait: 30, tries: 3), upd, check);
            var t3 = Trash(install);
            check(r3 == "ok" && t3.Length == 1 && File.ReadAllText(Path.Combine(t3[0], "user.txt")) == "u", $"(3) 도우미 직접 실행 → user.txt 는 trash 에 보존 ({r3})");
            var left3 = Updater.EmptyTrash(install);
            check(left3.Count == 1 && left3[0] == Path.GetFileName(t3[0]) && File.Exists(Path.Combine(t3[0], "user.txt")), "(3) 검사 실패 trash 는 남기고 이름만 알림");
            Directory.Move(t3[0], Path.Combine(root, "keep3"));

            // (4) 검토 R1: 설치 불완전 + 설치 안에 사용자 파일 + 온전한 .old → Diagnose 는 rollback 을 내도 막힘 검사가 막는다.
            //     도우미를 그대로 돌려도 내 메모는 trash 에 보존된다(지우지 않음)
            File.Delete(Path.Combine(install, "hostfxr.dll"));
            File.WriteAllText(memo, "사용자");
            var fix = Updater.Diagnose(install);
            check(fix is { Mode: "rollback" } && Updater.Blockers(install, "rollback").Any(x => x.Contains("내 메모.txt")), $"(4) rollback 제안이어도 사용자 파일이면 막힘 ({fix})");
            var r4 = RunHelper(Updater.HelperStart("rollback", install, null, 999_999, start: false, upd, wait: 30, tries: 3), upd, check);
            var t4 = Trash(install);
            check(r4 == "ok-rollback" && t4.Length == 1 && File.ReadAllText(Path.Combine(t4[0], "내 메모.txt")) == "사용자" && Read(install, Updater.ExeName) == "newer",
                $"(4) 도우미 직접 실행 → 내 메모는 trash 에 보존 ({r4})");
            check(Updater.EmptyTrash(install).Count == 1 && File.Exists(Path.Combine(t4[0], "내 메모.txt")), "(4) 사용자 파일 든 trash 는 지우지 않음");
            Directory.Move(t4[0], Path.Combine(root, "keep4"));

            // (5) 우리 것 아닌 .old(표지 없음) → 앱도 도우미도 막음, 파일 보존
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "남의 파일.txt"), "보존");
            PackNew(install, "v0.5.0", "five");
            check(Updater.Blockers(install, "apply").Any(x => x.Contains("우리 것 아님")), "(5) 표지 없는 .old → 앱이 막음");
            var r5 = RunHelper(Updater.HelperStart("apply", install, Updater.NewDir(install), 999_999, start: false, upd, wait: 30, tries: 3), upd, check);
            check(r5 == "fail-notours" && File.ReadAllText(Path.Combine(old, "남의 파일.txt")) == "보존" && Read(install, Updater.ExeName) == "newer",
                $"(5) 도우미도 막음, 파일 보존 ({r5})");
            Directory.Delete(old, true);
            check(Updater.Discard(install) && !Directory.Exists(Updater.NewDir(install)), "[버리기] .new 지움");

            // (6) 되돌리기: 설치 → trash, .old → 설치
            var r6a = RunHelper(Updater.HelperStart("apply", install, Updater.NewDir(install), 999_999, start: false, upd, wait: 30, tries: 3), upd, check);
            PackNew(install, "v0.6.0", "six");
            var r6b = RunHelper(Updater.HelperStart("apply", install, Updater.NewDir(install), 999_999, start: false, upd, wait: 30, tries: 3), upd, check);
            check(r6a == "fail-src" && r6b == "ok" && Read(install, Updater.ExeName) == "six" && Read(old, Updater.ExeName) == "newer", $"받은 버전 없음 → fail-src, 다시 적용 ok ({r6a}, {r6b})");
            check(Updater.Blockers(install, "rollback").Count == 0, "되돌리기 막힘 없음");
            var r6 = RunHelper(Updater.HelperStart("rollback", install, null, 999_999, start: false, upd, wait: 30, tries: 3), upd, check);
            check(r6 == "ok-rollback" && Read(install, Updater.ExeName) == "newer" && !Directory.Exists(old), $"되돌리기: 직전 버전 ({r6})");
            Updater.EmptyTrash(install);
            check(Trash(install).Length == 0, "되돌리기 뒤 trash 검사·지움");
            check(RunHelper(Updater.HelperStart("rollback", install, null, 999_999, start: false, upd, wait: 30, tries: 3), upd, check) == "fail-noold", "직전 버전 없음");

            // (7) 교체 중 끊김(설치 → .old 뒤 멈춤) → .old 에서 연 앱이 복구 제안 → 같은 막힘 검사 → recover
            PackNew(install, "v0.7.0", "seven");
            Directory.Move(install, old);
            var fix7 = Updater.Diagnose(old);
            check(fix7 is { Mode: "recover" } && fix7.Source == Updater.NewDir(install) && fix7.Target == install
                && Updater.RecoverBlockers(fix7.Source, fix7.Target).Count == 0, $"끊긴 교체 → 복구 제안(.new → 설치) ({fix7})");
            var r7 = RunHelper(Updater.HelperStart("recover", fix7!.Target, fix7.Source, 999_999, start: false, upd, wait: 30, tries: 3), upd, check);
            check(r7 == "ok-recover" && Read(install, Updater.ExeName) == "seven" && Updater.Check(install).Complete, $"복구: 새 버전으로 마무리 ({r7})");
            check(Updater.RecoverBlockers(old, install).Any(x => x.Contains("설치 폴더 있음")), "설치 폴더가 있으면 recover 막힘");

            // (8) 잠금: 도우미 PID·시작 시각. 기다리는 중엔 도는 중, 강제 종료하면 바로 아님(10분 기다리지 않음) → 결과 없음 알림
            var waiter = Track(Sleeper(30));
            using (var h = Updater.StartHelper(Updater.HelperStart("apply", install, Updater.NewDir(install), waiter.Id, start: false, upd, wait: 60, tries: 3), upd))
            {
                Thread.Sleep(300);
                check(Updater.LockFresh(upd) && Updater.TakeResult(upd) == null, "도우미가 기다리는 중 → 잠금 살아 있음, 결과 아직 모름");
                h.Kill(entireProcessTree: true);
                h.WaitForExit();
            }
            check(!Updater.LockFresh(upd), "도우미 강제 종료 → 잠금 바로 죽음");
            Updater.DropStaleLock(upd);
            check(!File.Exists(Updater.LockPath(upd)) && Updater.TakeResult(upd) == "none", "죽은 잠금 지움, 결과 없음(none)");
        }
        finally
        {
            foreach (var p in procs) { try { if (!p.HasExited) p.Kill(); p.WaitForExit(); } catch (InvalidOperationException) { } p.Dispose(); }
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    static string[] Trash(string install) => Directory.GetDirectories(Path.GetDirectoryName(install)!, Path.GetFileName(install) + ".trash-*");

    /// <summary>가짜 묶음: 가짜 exe·DLL 글 + VERSION + files.txt(크기 TAB 상대 경로). extra = 그 버전에만 있는 파일.</summary>
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
        Relist(dir);
    }

    /// <summary>files.txt 를 지금 파일로 다시 쓴다.</summary>
    static void Relist(string dir)
    {
        var full = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
        File.WriteAllLines(Path.Combine(dir, Updater.ListName),
            Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) is not (Updater.ListName or Updater.ReadyName))
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
        var src = Path.Combine(root, "pkg-" + version, $"TableEditor-{version}-net10-x64");
        Pack(src, version, mark, extra);
        if (drop != null) File.Delete(Path.Combine(src, drop));
        var zip = Path.Combine(root, $"TableEditor-{version}-net10-x64.zip");
        ZipFile.CreateFromDirectory(src, zip, CompressionLevel.Fastest, includeBaseDirectory: true);
        return zip;
    }

    static string Read(string dir, string name) => File.ReadAllText(Path.Combine(dir, name));

    static Process Sleeper(int seconds) =>
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"), $"-n {seconds + 1} 127.0.0.1")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;

    /// <summary>앱처럼 띄운다(잠금·launched 표지). 끝나면 잠금이 지워졌는지도 본다.</summary>
    static string? RunHelper(ProcessStartInfo si, string dir, Action<bool, string> check)
    {
        using var p = Updater.StartHelper(si, dir);
        if (!p.WaitForExit(120_000)) { p.Kill(true); return "timeout"; }
        check(!Updater.LockFresh(dir), "도우미가 끝나면 잠금 아님");
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
