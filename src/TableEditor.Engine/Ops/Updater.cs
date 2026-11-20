using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TableEditor.Engine.Ops;

/// <summary>
/// GitHub 정식 릴리스 하나. File = zip 이름, Sha256 = 소문자 64 hex. 둘 중 하나라도 없으면 버전만 알리고 받지 않는다.
/// Size = zip 바이트, Unpacked = 푼 묶음 바이트(매니페스트에 없으면 0).
/// </summary>
public sealed record ReleaseInfo(Version Version, string? File, string? Sha256, long Size, string Page, string Notes, DateTimeOffset? Published, long Unpacked = 0)
{
    public string Tag => "v" + Version.ToString(3);
    public string? AssetUrl => File == null ? null : $"{Updater.Releases}/download/{Tag}/{File}";
    public bool CanDownload => File != null && Sha256 != null;
}

/// <summary>업데이트 막힘·실패. Message = 짧은 낱말(팝업 줄).</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 폴더 검사(files.txt 대조). Marked = VERSION + TableEditor.exe + files.txt 가 있다(우리 묶음 표지).
/// Missing = 없거나 크기가 다른 파일, Extra = 목록 밖 파일(사용자 파일 등).
/// </summary>
public sealed record FolderCheck(bool Exists, bool Marked, bool HasGit, IReadOnlyList<string> Missing, IReadOnlyList<string> Extra)
{
    /// <summary>표지가 있고 목록 밖 파일·.git 이 없다: 옮기거나 지워도 사용자 파일을 잃지 않는다.</summary>
    public bool Ours => Exists && Marked && !HasGit && Extra.Count == 0;
    /// <summary>목록의 파일이 모두 같은 크기로 있다.</summary>
    public bool Complete => Exists && Marked && Missing.Count == 0;
}

/// <summary>시작할 때 고칠 것: recover = Source 폴더를 Target 으로 옮김(교체 중 끊김), rollback = .old 로 되돌림(설치 불완전).</summary>
public sealed record Repair(string Mode, string Source, string Target);

/// <summary>
/// 프로그램 업데이트(참고: 공개 레포 AmrMapEditor 의 Updater). api.github.com 이 막힌 망 대비로 확인은 매니페스트 → releases/latest 리다이렉트 → API 차례.
/// 받기: .partial → SHA-256 → stage-X.tmp-&lt;pid&gt; 에 풀고 files.txt 대조 → 설치 폴더 옆 &lt;설치&gt;.new 를 완성(.te-ready 표지).
/// 교체: 앱이 끝난 뒤 바깥 도우미(<see cref="HelperScript"/>)가 이름 바꾸기만 한다(설치 → .old, .new → 설치). 도우미는 아무것도 지우지 않는다:
/// 버릴 폴더는 &lt;설치&gt;.trash-&lt;시각&gt;-a 로 옮기고 .te-trash 표지를 쓴다. 실제 삭제는 앱이 다음 시작 때 <see cref="EmptyTrash"/> 에서
/// files.txt 대조(<see cref="Check"/>)를 통과한 폴더만 한다. 사용자 데이터(설정·초안·사이트 저장소)는 설치 폴더 밖이다(<see cref="DataInside"/>).
/// </summary>
public static class Updater
{
    public const string Repo = "Leepy0/AGV_DCU_TableEditor";
    public const string ManifestName = "TableEditor.json";
    public const string ExeName = "TableEditor.exe";
    public const string CliName = "tedit.exe";
    /// <summary>묶음 파일 목록(크기 TAB 상대 경로). tools/release.ps1 이 만든다.</summary>
    public const string ListName = "files.txt";
    /// <summary>.new 완성 표지(zip sha256 한 줄).</summary>
    public const string ReadyName = ".te-ready";
    /// <summary>도우미가 버린 폴더 표지.</summary>
    public const string TrashName = ".te-trash";
    public static string Releases => $"https://github.com/{Repo}/releases";

    /// <summary>탐색기가 아무 폴더에나 만드는 파일: 목록 대조에서 무시한다(폴더와 같이 옮겨지고 같이 지워진다).</summary>
    static readonly HashSet<string> ShellFiles = new(StringComparer.OrdinalIgnoreCase) { "desktop.ini", "Thumbs.db", "ehthumbs.db" };
    /// <summary>우리가 묶음 맨 위에 두는 파일(목록 밖으로 치지 않는다).</summary>
    static readonly HashSet<string> OwnFiles = new(StringComparer.OrdinalIgnoreCase) { ListName, ReadyName, TrashName };

    /// <summary>설정·초안·받은 버전이 사는 곳(설치 폴더 밖).</summary>
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AGV_DCU_TableEditor");

    /// <summary>설치본마다 따로(나란히 둔 두 설치본이 서로의 잠금·결과를 보지 않게): updates\&lt;설치 경로 해시 12자리&gt;.</summary>
    public static string UpdateDirFor(string install) => Path.Combine(DataDir, "updates",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(install).TrimEnd('\\').ToUpperInvariant())))[..12]);

    // 정적 필드는 글 차례로 초기화된다: VersionText → Build(버전 읽기) → Http(User-Agent) 차례를 지킨다
    static readonly Regex VersionText = new(@"^[vV]?(\d{1,9})\.(\d{1,9})(?:\.(\d{1,9}))?$", RegexOptions.CultureInvariant);

    // 진입 어셈블리(TableEditor.exe)의 버전·커밋·채널
    static readonly (Version Version, string? Commit, string Channel) Build = ReadBuild();

    static (Version, string?, string) ReadBuild()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(Updater).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var parts = info.Split('+', 2);
        var v = TryParseVersion(parts[0], out var pv) ? pv : Normalize(asm.GetName().Version ?? new Version(0, 0, 0));
        var commit = parts.Length > 1 && parts[1].Length >= 7 ? parts[1][..7] : null;
        var channel = asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildChannel")?.Value ?? "";
        return (v, commit, channel);
    }

    public static Version Current => Build.Version;
    public static string? Commit => Build.Commit;

    /// <summary>release = tools/release.ps1 게시, local = dotnet build(bin 폴더).</summary>
    public static string Channel => Build.Channel;

    /// <summary>게시한 버전만 스스로 바꾼다(bin 폴더는 바꾸지 않는다).</summary>
    public static bool CanSelfUpdate => Channel == "release";

    public static string InstallDir => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    public static string OldDir(string install) => install + ".old";
    public static string NewDir(string install) => install + ".new";
    static string TrashPrefix(string install) => install + ".trash-";

    // 리다이렉트는 손으로 따라간다: 실패한 호스트 이름을 알리려고(자산은 다른 호스트로 넘어간다)
    static readonly HttpClient Http = Client(true);
    static readonly HttpClient HttpDirect = Client(false);

    /// <summary>확인·받기 길. 시스템 프록시(자동 검색 포함)가 막히는 망은 직접 연결로만 된다.</summary>
    public enum NetRoute { Proxy, Direct }

    /// <summary>지난번에 된 길(설정에 남겨 다음 실행도 이 길부터). 확인이 되면 바뀐다.</summary>
    public static NetRoute Route { get; set; } = NetRoute.Proxy;

    internal static HttpClient ClientFor(NetRoute r) => r == NetRoute.Direct ? HttpDirect : Http;

    /// <summary>받기도 확인이 된 길로.</summary>
    internal static HttpClient DownloadClient => ClientFor(Route);

    static HttpClient Client(bool useProxy)
    {
        var h = new HttpClientHandler
        {
            UseProxy = useProxy,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials, // 시스템 프록시가 Windows 인증을 요구하는 망
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var c = new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan }; // 시간 제한은 호출마다 토큰으로
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"AGV_DCU_TableEditor/{Current.ToString(3)}");
        return c;
    }

    static readonly TimeSpan PerMethod = TimeSpan.FromSeconds(8);
    static readonly TimeSpan Idle = TimeSpan.FromSeconds(30);

    // ---- 확인 ----

    /// <summary>확인 방법 하나. Run 은 마지막으로 닿은 호스트를 trail 에 적는다(실패 줄에 쓴다).</summary>
    internal sealed record CheckMethod(string Name, Func<HttpClient, Trail, CancellationToken, Task<ReleaseInfo?>> Run);

    /// <summary>지금 닿고 있는 호스트(리다이렉트를 따라간 마지막).</summary>
    internal sealed class Trail { public string Host = "github.com"; }

    static readonly CheckMethod[] Methods =
    [
        new("매니페스트", FromManifestAsync),
        new("리다이렉트", FromRedirectAsync),
        new("API", FromApiAsync),
    ];

    /// <summary>
    /// 최신 정식 릴리스. 없으면 null. 길은 둘이다: 시스템 프록시(자동 검색 포함)와 직접 연결. 사내 PC 는 프록시 길이 시간 초과하고 직접은 된다.
    /// 순서: 매니페스트를 지난번에 된 길로(8초) → 실패하면 다른 길로(8초) → 그 뒤 나머지 방법을 되는 길로 → 끝으로 <c>git ls-remote --tags</c>(버전만).
    /// 된 길은 <see cref="Route"/> 에 남는다(다음 확인·받기가 그 길부터). 모두 실패하면 방법마다 한 줄(방법 · 길 · 호스트 · 까닭 · 초)을 줄바꿈으로 이은 <see cref="UpdateException"/>.
    /// </summary>
    public static async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        var (release, route) = await CheckAsync(Methods, ClientFor, Route, LsRemoteAsync, PerMethod, ct);
        if (route is { } r) Route = r;
        return release;
    }

    internal static async Task<(ReleaseInfo? Release, NetRoute? Route)> CheckAsync(IReadOnlyList<CheckMethod> methods, Func<NetRoute, HttpClient> clientFor,
        NetRoute preferred, Func<CancellationToken, Task<ReleaseInfo?>> lsRemote, TimeSpan perMethod, CancellationToken ct)
    {
        NetRoute[] routes = [preferred, preferred == NetRoute.Proxy ? NetRoute.Direct : NetRoute.Proxy];
        var lines = new List<string>();
        int none = 0; // 릴리스 없음(404)으로 답한 방법 수: 실패가 아니다

        // 방법 하나를 길 하나로: 성공 = 릴리스, 릴리스 없음 = (false, null), 실패 = 줄을 남기고 (false, null) + failed
        async Task<(bool Answered, ReleaseInfo? Release)> Try(CheckMethod m, NetRoute route)
        {
            var trail = new Trail();
            var clock = Stopwatch.StartNew();
            using var one = CancellationTokenSource.CreateLinkedTokenSource(ct);
            one.CancelAfter(perMethod);
            try { return (true, await m.Run(clientFor(route), trail, one.Token)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { lines.Add(Line(m.Name, route, trail.Host, "시간 초과", clock)); }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException or UpdateException or InvalidOperationException)
            { lines.Add(Line(m.Name, route, trail.Host, Reason(e), clock)); }
            return (false, null);
        }

        NetRoute? worked = null; // 답을 준 길
        foreach (var route in routes)
        {
            var (answered, r) = await Try(methods[0], route);
            if (r != null) return (r, route);
            if (answered) { none++; worked = route; break; }
        }
        foreach (var m in methods.Skip(1))
            foreach (var route in worked is { } w ? new[] { w } : routes)
            {
                var (answered, r) = await Try(m, route);
                if (r != null) return (r, route);
                if (answered) { none++; worked ??= route; break; }
            }
        if (lines.Count == 0 && none > 0) return (null, worked); // 모두 릴리스 없음

        var tags = Stopwatch.StartNew();
        try
        {
            if (await lsRemote(ct) is { } r) return (r, null);
            lines.Add($"깃 태그 · github.com · 태그 없음 {tags.Elapsed.TotalSeconds:0} s");
        }
        catch (UpdateException e) { lines.Add($"깃 태그 · github.com · {e.Message} {tags.Elapsed.TotalSeconds:0} s"); }
        throw new UpdateException(string.Join("\n", lines));
    }

    static string Line(string method, NetRoute route, string host, string reason, Stopwatch clock) =>
        $"{method} · {(route == NetRoute.Proxy ? "프록시" : "직접")} · {host} · {reason} {clock.Elapsed.TotalSeconds:0} s";
    /// <summary>실패 까닭 한 낱말. UpdateException 은 "낱말 · 호스트" 꼴이라 낱말만.</summary>
    static string Reason(Exception e) => e switch
    {
        UpdateException => e.Message.Split(" · ")[0],
        HttpRequestException h => h.HttpRequestError == HttpRequestError.Unknown ? "연결 실패" : "연결 실패 " + h.HttpRequestError,
        JsonException => "응답 해석 실패",
        _ => e.GetType().Name,
    };

    static readonly TimeSpan GitTagsLimit = TimeSpan.FromSeconds(15);

    /// <summary><c>git ls-remote --tags</c> 로 최신 정식 태그만 읽는다(파일 정보 없음: 받을 수 없고 버전만 알린다).</summary>
    static async Task<ReleaseInfo?> LsRemoteAsync(CancellationToken ct)
    {
        var lines = await Git.RemoteTagLinesAsync($"https://github.com/{Repo}", GitTagsLimit, ct) ?? throw new UpdateException("깃 실패");
        return LatestTag(lines) is { } v ? new ReleaseInfo(v, null, null, 0, $"{Releases}/tag/v{v.ToString(3)}", "", null) : null;
    }

    /// <summary>ls-remote 줄(<c>해시 TAB refs/tags/vX.Y.Z</c>)에서 가장 높은 정식 태그. <c>^{}</c> 줄·시험 태그(<c>v1.0.0-rc1</c>)는 뺀다.</summary>
    public static Version? LatestTag(IEnumerable<string> lines) => lines
        .Select(l => Regex.Match(l, @"^[0-9a-f]{40}\s+refs/tags/v(\d{1,9}\.\d{1,9}\.\d{1,9})$", RegexOptions.CultureInvariant))
        .Where(m => m.Success).Select(m => Version.Parse(m.Groups[1].Value)).OrderDescending().FirstOrDefault();

    static async Task<ReleaseInfo?> FromManifestAsync(HttpClient http, Trail trail, CancellationToken ct)
    {
        using var res = await GetFollowAsync(http, new Uri($"{Releases}/latest/download/{ManifestName}"), ct, notFoundOk: true, trail: trail);
        return res == null ? null : ParseManifest(await res.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// 매니페스트 <c>{ version, file, sha256, size, unpacked, published, page, notes }</c>. 버전이 틀리면 null.
    /// file 은 이름만(경로 글자가 있으면 버린다), sha256 은 64 hex 일 때만.
    /// </summary>
    public static ReleaseInfo? ParseManifest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var m = doc.RootElement;
        if (m.ValueKind != JsonValueKind.Object || !TryParseVersion(Str(m, "version"), out var v)) return null;
        var file = Str(m, "file");
        bool plain = file.Length > 0 && file.IndexOfAny([.. Path.GetInvalidFileNameChars(), '/', '\\']) < 0 && file is not ("." or "..");
        var sha = Str(m, "sha256").ToLowerInvariant();
        bool hex = sha.Length == 64 && sha.All(char.IsAsciiHexDigit);
        var page = Str(m, "page");
        return new ReleaseInfo(v, plain ? file : null, hex ? sha : null, Int(m, "size"), page.Length > 0 ? page : $"{Releases}/tag/v{v.ToString(3)}",
            Str(m, "notes"), DateTimeOffset.TryParse(Str(m, "published"), CultureInfo.InvariantCulture, out var p) ? p : null, Int(m, "unpacked"));
    }

    /// <summary>매니페스트가 없는 릴리스(v0.1.0): releases/latest 의 Location(…/releases/tag/vX.Y.Z)에서 버전만 읽는다.</summary>
    static async Task<ReleaseInfo?> FromRedirectAsync(HttpClient http, Trail trail, CancellationToken ct)
    {
        trail.Host = "github.com";
        using var res = await http.GetAsync($"{Releases}/latest", HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)res.StatusCode is < 300 or > 399) return res.StatusCode == HttpStatusCode.NotFound ? null : throw new HttpRequestException(res.StatusCode.ToString());
        return TagFromLocation(res.Headers.Location?.ToString()) is { } v
            ? new ReleaseInfo(v, null, null, 0, $"{Releases}/tag/v{v.ToString(3)}", "", null)
            : null;
    }

    /// <summary>".../releases/tag/v0.1.0" → 0.1.0. 아니면 null.</summary>
    public static Version? TagFromLocation(string? location)
    {
        if (string.IsNullOrEmpty(location)) return null;
        var path = Uri.TryCreate(location, UriKind.Absolute, out var u) ? u.AbsolutePath : location;
        int i = path.LastIndexOf("/tag/", StringComparison.Ordinal);
        return i >= 0 && TryParseVersion(Uri.UnescapeDataString(path[(i + 5)..]).TrimEnd('/'), out var v) ? v : null;
    }

    /// <summary>GitHub API. zip 자산과 GitHub 가 계산한 digest 를 쓴다.</summary>
    static async Task<ReleaseInfo?> FromApiAsync(HttpClient http, Trail trail, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        trail.Host = "api.github.com";
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var r = doc.RootElement;
        if (!TryParseVersion(Str(r, "tag_name"), out var v)) return null;
        string? file = null, sha = null;
        long size = 0;
        if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
            {
                var name = Str(a, "name");
                if (!name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) continue;
                file = name;
                size = Int(a, "size");
                var digest = Str(a, "digest");
                sha = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && digest.Length == 71 ? digest[7..].ToLowerInvariant() : null;
            }
        var page = Str(r, "html_url");
        return new ReleaseInfo(v, file, sha, size, page.Length > 0 ? page : $"{Releases}/tag/v{v.ToString(3)}", Str(r, "body"),
            DateTimeOffset.TryParse(Str(r, "published_at"), CultureInfo.InvariantCulture, out var p) ? p : null);
    }

    /// <summary>GET 하고 리다이렉트를 따라간다(최대 5). 실패는 그 호스트 이름을 붙인 UpdateException. notFoundOk 면 404 → null.</summary>
    static async Task<HttpResponseMessage?> GetFollowAsync(HttpClient http, Uri url, CancellationToken ct, bool notFoundOk = false, Trail? trail = null)
    {
        for (int hop = 0; hop < 6; hop++)
        {
            if (trail != null) trail.Host = url.Host;
            HttpResponseMessage res;
            try { res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct); }
            catch (HttpRequestException e) { throw new UpdateException("연결 실패 · " + url.Host, e); }
            if ((int)res.StatusCode is >= 300 and < 400 && res.Headers.Location is { } loc)
            {
                url = new Uri(url, loc);
                res.Dispose();
                continue;
            }
            if (res.StatusCode == HttpStatusCode.NotFound && notFoundOk) { res.Dispose(); return null; }
            if (!res.IsSuccessStatusCode)
            {
                int code = (int)res.StatusCode;
                res.Dispose();
                throw new UpdateException($"HTTP {code} · {url.Host}");
            }
            res.RequestMessage ??= new HttpRequestMessage(HttpMethod.Get, url); // 마지막 호스트(무응답 알림에 쓴다)
            return res;
        }
        throw new UpdateException("리다이렉트 많음 · " + url.Host);
    }

    // ---- 받기 ----

    /// <summary>
    /// zip 을 dir 에 받는다(.partial → SHA-256 이 맞으면 이름 바꿈). 이미 받아 둔 같은 파일은 그대로. 30초 동안 바이트가 안 오면 실패.
    /// 확인에 성공한 길(<see cref="Route"/>)로 받고, 그 길에서 연결·시간 초과로 실패하면 다른 길로 처음부터 한 번 더 받는다(이어받기 없음, SHA-256 검증 그대로).
    /// 다른 길에서 되면 <see cref="Route"/> 를 그 길로 바꾼다. http 를 주면(시험) 그 클라이언트로만 받고, 다시 시도할 클라이언트는 retry 로 준다.
    /// </summary>
    public static async Task<string> DownloadAsync(ReleaseInfo r, string dir, IProgress<double>? progress, CancellationToken ct, HttpClient? http = null, HttpClient? retry = null)
    {
        if (!r.CanDownload) throw new UpdateException("매니페스트 없음");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, r.File!);
        if (File.Exists(target) && await Task.Run(() => HashFile(target), ct) == r.Sha256) return target;
        var other = Route == NetRoute.Proxy ? NetRoute.Direct : NetRoute.Proxy;
        if (http == null) retry = ClientFor(other);
        try { await DownloadOnceAsync(r, target + ".partial", target, http ?? DownloadClient, progress, ct); }
        catch (Exception e) when (retry != null && !ct.IsCancellationRequested && e is HttpRequestException or IOException or TaskCanceledException
            or UpdateException { Message: not "SHA-256 불일치" })
        {
            await DownloadOnceAsync(r, target + ".partial", target, retry, progress, ct);
            if (http == null) Route = other;
        }
        return target;
    }

    static async Task DownloadOnceAsync(ReleaseInfo r, string part, string target, HttpClient client, IProgress<double>? progress, CancellationToken ct)
    {
        var url = new Uri(r.AssetUrl!);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            idle.CancelAfter(Idle);
            using var res = (await GetFollowAsync(client, url, idle.Token))!;
            url = res.RequestMessage?.RequestUri ?? url;
            long total = res.Content.Headers.ContentLength ?? r.Size;
            await using var src = await res.Content.ReadAsStreamAsync(idle.Token);
            await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buf = new byte[81920];
            long done = 0;
            double shown = -1;
            int n;
            while ((n = await src.ReadAsync(buf, idle.Token)) > 0)
            {
                idle.CancelAfter(Idle); // 무응답 시계를 다시 맞춘다
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                double pct = total > 0 ? Math.Min(100, done * 100.0 / total) : 0;
                if (pct - shown >= 1) { progress?.Report(pct); shown = pct; }
            }
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            TryDelete(part);
            throw new UpdateException("응답 없음 · " + url.Host, e);
        }
        catch
        {
            TryDelete(part); // 취소·끊김: 반쯤 받은 파일을 남기지 않는다
            throw;
        }
        await Task.Run(() => Accept(part, target, r.Sha256!), ct);
    }

    /// <summary>.partial 의 SHA-256 이 기대값과 같을 때만 target 으로 이름을 바꾼다. 다르면 지우고 예외.</summary>
    public static void Accept(string part, string target, string sha256)
    {
        if (!HashFile(part).Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(part);
            throw new UpdateException("SHA-256 불일치");
        }
        File.Move(part, target, overwrite: true);
    }

    /// <summary>
    /// 새 버전을 설치 폴더 옆 &lt;설치&gt;.new 에 완성한다(앱이 도는 동안, <see cref="TryGate"/> 를 잡고 부른다). 이미 같은 sha 로 완성돼 있으면 그대로.
    /// zip → dir\stage-X.tmp-&lt;pid&gt; 에 풀고 files.txt·항목 수·크기·버전 대조 → &lt;설치&gt;.new.tmp-&lt;pid&gt; 로 옮김(옮기기가 안 되면 복사) → 다시 대조 →
    /// .te-ready → 이름 바꿔 .new 로 확정. 어떤 예외든 임시 폴더는 치운다. 이미 있던 .new 는 우리 것일 때만 지운다. .new 경로.
    /// </summary>
    public static async Task<string> PrepareAsync(ReleaseInfo r, string install, string dir, IProgress<double>? progress, CancellationToken ct, HttpClient? http = null)
    {
        var nw = NewDir(install);
        if (IsReady(nw, r.Sha256)) return nw;
        var zip = await DownloadAsync(r, dir, progress, ct, http);
        var stage = Path.Combine(dir, $"stage-{r.Version.ToString(3)}.tmp-{Environment.ProcessId}");
        var tmp = $"{nw}.tmp-{Environment.ProcessId}";
        try
        {
            await Task.Run(() =>
            {
                TryDeleteDir(stage);
                ZipFile.ExtractToDirectory(zip, stage); // 경로가 폴더 밖을 가리키는 항목은 .NET 이 막는다
                ct.ThrowIfCancellationRequested();
                var root = Directory.GetDirectories(stage) is [var one] && !File.Exists(Path.Combine(stage, ExeName)) ? one : stage;
                RequirePackage(root, r.Version);
                TryDeleteDir(tmp);
                try { Directory.Move(root, tmp); } // 다른 볼륨(같은 문자 아래 마운트 포함)이면 예외 → 복사
                catch (IOException) { TryDeleteDir(tmp); CopyDir(root, tmp, ct); }
                ct.ThrowIfCancellationRequested();
                RequirePackage(tmp, r.Version);
                File.WriteAllText(Path.Combine(tmp, ReadyName), r.Sha256 + "\r\n");
                if (Directory.Exists(nw))
                {
                    if (!Check(nw).Ours) throw new UpdateException(Path.GetFileName(nw) + " 우리 것 아님");
                    DeleteOurs(nw);
                }
                Directory.Move(tmp, nw);
            }, ct);
        }
        catch (InvalidDataException e) { throw new UpdateException("zip 손상", e); }
        finally
        {
            TryDeleteDir(stage);
            TryDeleteDir(tmp);
        }
        TryDelete(zip);
        return nw;
    }

    /// <summary>묶음 폴더가 온전한가(표지, files.txt 와 같음, 목록 밖·.git 없음, VERSION = 그 버전). 아니면 UpdateException.</summary>
    public static void RequirePackage(string root, Version v)
    {
        var c = Check(root);
        if (!c.Marked) throw new UpdateException("묶음 표지 없음");
        if (c.Missing.Count > 0) throw new UpdateException("묶음 불완전 · " + c.Missing[0]);
        if (!c.Ours) throw new UpdateException("묶음 목록 밖 · " + (c.Extra.FirstOrDefault() ?? ".git"));
        if (ReadVersion(root) != v) throw new UpdateException("묶음 버전 다름");
    }

    /// <summary>.new 가 완성됐는가(.te-ready, 온전함). sha 를 주면 그 zip 에서 만든 것인지도.</summary>
    public static bool IsReady(string nw, string? sha = null)
    {
        var ready = Path.Combine(nw, ReadyName);
        if (!File.Exists(ready)) return false;
        if (sha != null && !File.ReadAllText(ready).Trim().Equals(sha, StringComparison.OrdinalIgnoreCase)) return false;
        var c = Check(nw);
        return c.Ours && c.Complete;
    }

    /// <summary>받아 둔 새 버전(&lt;설치&gt;.new, 현재 버전보다 새 버전). 없으면 null. 창마다 따로 기억하지 않고 이 폴더가 기준이다.</summary>
    public static Version? Pending(string install, Version current)
    {
        var nw = NewDir(install);
        return IsReady(nw) && ReadVersion(nw) is { } v && v > current ? v : null;
    }

    /// <summary>[버리기]: 받아 둔 .new 를 지운다(우리 것이고 그 안에서 도는 프로그램이 없을 때만).</summary>
    public static bool Discard(string install)
    {
        var nw = NewDir(install);
        if (!Directory.Exists(nw)) return true;
        if (!Check(nw).Ours || RunningIn([nw], -1).Count > 0) return false;
        DeleteOurs(nw);
        return true;
    }

    /// <summary>
    /// 창 사이 받기·정리 차례: dir\gate.lock 을 FileShare.None 으로 연다. 못 열면 null(다른 창이 쓰는 중).
    /// 프로세스가 죽으면 OS 가 핸들을 닫으므로 버려진 잠금이 남지 않는다. 다 쓰면 Dispose.
    /// </summary>
    public static FileStream? TryGate(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            return new FileStream(Path.Combine(dir, "gate.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// 시작할 때(도우미 잠금이 없고 차례를 잡았을 때만): 중단된 받기, 주인이 끝난 스테이징·.new.tmp-&lt;pid&gt;, 현재 버전 이하인 .new(이미 적용됨) 정리.
    /// tmp 이름의 pid 가 살아 있으면 남긴다(다른 창이 만드는 중).
    /// </summary>
    public static void Cleanup(string dir, string install, Version current)
    {
        if (LockFresh(dir)) return;
        using var gate = TryGate(dir);
        if (gate == null) return;
        try
        {
            foreach (var f in Directory.GetFiles(dir, "*.partial")) TryDelete(f);
            foreach (var d in Directory.GetDirectories(dir, "stage-*.tmp-*")) if (!OwnerAlive(d)) TryDeleteDir(d);
            var parent = Path.GetDirectoryName(install);
            if (parent != null)
                foreach (var d in Directory.GetDirectories(parent, Path.GetFileName(NewDir(install)) + ".tmp-*"))
                    if (!OwnerAlive(d) && Check(d) is { Extra.Count: 0, HasGit: false }) TryDeleteDir(d); // 우리가 푼 것만(목록 밖 파일이 없음)
            var nw = NewDir(install);
            if (Directory.Exists(nw) && Check(nw).Ours && (ReadVersion(nw) is not { } v || v <= current) && RunningIn([nw], -1).Count == 0) DeleteOurs(nw);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // 정리 실패는 다음에
    }

    /// <summary>"…tmp-1234" 의 1234 프로세스가 살아 있는가.</summary>
    static bool OwnerAlive(string tmpDir)
    {
        if (!int.TryParse(tmpDir[(tmpDir.LastIndexOf('-') + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) return false;
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>
    /// 도우미가 버린 폴더(&lt;설치&gt;.trash-*)를 지운다: .te-trash 표지가 있고, 그 안에서 도는 프로그램이 없고, files.txt 대조를 통과(우리 것)할 때만.
    /// 남긴 폴더 이름(표지 없음·목록 밖 파일)을 돌려준다. 도는 프로그램이 있으면 이번엔 건너뛴다.
    /// </summary>
    public static IReadOnlyList<string> EmptyTrash(string install)
    {
        var left = new List<string>();
        var parent = Path.GetDirectoryName(install);
        if (parent == null || !Directory.Exists(parent)) return left;
        foreach (var d in Directory.GetDirectories(parent, Path.GetFileName(TrashPrefix(install)) + "*"))
        {
            if (IsLink(d)) { left.Add(Path.GetFileName(d)); continue; } // 링크·정션: 따라가 지우지 않는다
            // 지우다 끊겨 표지까지 지운 빈 트리(파일·링크 없음)는 표지가 없어도 지운다
            if (!File.Exists(Path.Combine(d, TrashName)) && !Walk(d).Any(f => f is FileInfo || f.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            {
                try { Directory.Delete(d, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // 다음에
                continue;
            }
            // 지우다 끊긴 trash 는 표지가 일부 없을 수 있다: .te-trash 와 "목록 밖 없음"(files.txt 가 없으면 남은 파일이 모두 목록 밖)만 본다
            var c = Check(d);
            if (!File.Exists(Path.Combine(d, TrashName)) || c.HasGit || c.Extra.Count > 0) { left.Add(Path.GetFileName(d)); continue; }
            if (RunningIn([d], -1).Count > 0) continue;
            try { DeleteOurs(d); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // 다음에
        }
        return left;
    }

    /// <summary>
    /// 우리 묶음 폴더 지우기. TableEditor.exe 를 먼저(막 끝난 프로세스의 이미지가 남아 있으면 여기서 멈추고 아무것도 안 지움),
    /// 표지(VERSION, files.txt, .te-trash)는 맨 나중에: 중간에 끊겨도 다음에 다시 알아보고 이어 지운다.
    /// </summary>
    static void DeleteOurs(string dir)
    {
        // 폴더 자체나 그 안에 링크·정션이 있으면 지우지 않는다(대상을 따라가 지우지 않게). 부르는 쪽 검사(Ours)를 한 번 더
        if (IsLink(dir) || Walk(dir).Any(f => f.Attributes.HasFlag(FileAttributes.ReparsePoint))) throw new UpdateException(Path.GetFileName(dir) + " 링크");
        static void Del(string f) { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); }
        string[] last = [Path.Combine(dir, "VERSION"), Path.Combine(dir, ReadyName), Path.Combine(dir, ListName), Path.Combine(dir, TrashName)];
        var exe = Path.Combine(dir, ExeName);
        if (File.Exists(exe)) Del(exe);
        foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).ToList())
            if (!last.Contains(f, StringComparer.OrdinalIgnoreCase)) Del(f);
        foreach (var f in last.Where(File.Exists)) Del(f);
        Directory.Delete(dir, recursive: true);
    }

    static void CopyDir(string src, string dst, CancellationToken ct)
    {
        Directory.CreateDirectory(dst);
        foreach (var d in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, d)));
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(f, Path.Combine(dst, Path.GetRelativePath(src, f)));
        }
    }

    // ---- 폴더 검사 ----

    /// <summary>
    /// 폴더를 files.txt 와 대조한다(숨김·시스템 파일 포함). 맨 위의 files.txt·.te-ready·.te-trash 와
    /// 어느 깊이든 desktop.ini·Thumbs.db·ehthumbs.db 는 목록 밖으로 치지 않는다.
    /// </summary>
    public static FolderCheck Check(string dir)
    {
        if (!Directory.Exists(dir)) return new(false, false, false, [], []);
        if (IsLink(dir)) return new(true, false, false, [], [Path.GetFileName(dir) + " (링크)"]); // 링크·정션 폴더는 옮기기·지우기·적용 모두 막음
        var list = Path.Combine(dir, ListName);
        bool marked = File.Exists(list) && File.Exists(Path.Combine(dir, "VERSION")) && File.Exists(Path.Combine(dir, ExeName));
        var expect = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(list))
            foreach (var line in File.ReadAllLines(list))
                if (line.Split('\t', 2) is [var n, var rel] && long.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var size)) expect[rel] = size;
        var root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var extra = new List<string>();
        bool git = false;
        foreach (var f in Walk(dir))
        {
            var rel = f.FullName[root.Length..];
            if (f.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)) git = true;
            if (f.Attributes.HasFlag(FileAttributes.ReparsePoint)) { extra.Add(rel); continue; } // 링크·정션은 우리 묶음에 없다
            if (f is DirectoryInfo) continue;
            if (OwnFiles.Contains(rel) || ShellFiles.Contains(f.Name)) continue;
            if (expect.TryGetValue(rel, out var size))
            {
                seen.Add(rel);
                if (((FileInfo)f).Length != size) missing.Add(rel);
            }
            else extra.Add(rel);
        }
        missing.AddRange(expect.Keys.Where(k => !seen.Contains(k)));
        return new(true, marked, git, missing, extra);
    }

    /// <summary>폴더 자체가 링크·정션(ReparsePoint)인가.</summary>
    public static bool IsLink(string dir) => Directory.Exists(dir) && new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>폴더 안 모든 항목(숨김·시스템 포함). 링크·정션은 내놓되 그 안으로는 들어가지 않는다.</summary>
    static IEnumerable<FileSystemInfo> Walk(string dir)
    {
        var opt = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        var stack = new Stack<DirectoryInfo>([new DirectoryInfo(dir)]);
        while (stack.TryPop(out var cur))
            foreach (var f in cur.EnumerateFileSystemInfos("*", opt))
            {
                yield return f;
                if (f is DirectoryInfo d && !d.Attributes.HasFlag(FileAttributes.ReparsePoint)) stack.Push(d);
            }
    }

    /// <summary>폴더의 VERSION 버전. 없거나 틀리면 null.</summary>
    public static Version? ReadVersion(string dir)
    {
        var f = Path.Combine(dir, "VERSION");
        try { return File.Exists(f) && TryParseVersion(File.ReadAllText(f), out var v) ? v : null; }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// 막는 까닭(팝업 줄). 비면 해도 된다. mode: prepare(받기) · apply(적용) · rollback(되돌리기).
    /// 설치 폴더에 목록 밖 파일·.git 이 있거나 표지가 없으면 막는다. 옆 폴더(.new, .old)가 있는데 우리 것이 아니면 막는다.
    /// </summary>
    public static IReadOnlyList<string> Blockers(string install, string mode)
    {
        var b = new List<string>();
        var c = Check(install);
        if (!c.Marked) b.Add("설치 폴더 표지 없음(files.txt)");
        if (c.HasGit) b.Add("설치 폴더에 .git");
        if (c.Extra.Count > 0) b.Add($"목록 밖 {c.Extra.Count} · " + string.Join(", ", c.Extra.Take(3)) + (c.Extra.Count > 3 ? " …" : ""));
        void Foreign(string p)
        {
            if (Directory.Exists(p) && !Check(p).Ours) b.Add(Path.GetFileName(p) + " 우리 것 아님");
        }
        switch (mode)
        {
            case "prepare":
                Foreign(NewDir(install));
                Foreign(OldDir(install));
                break;
            case "apply":
                Foreign(OldDir(install));
                if (!IsReady(NewDir(install))) b.Add("받은 버전 없음");
                break;
            case "rollback":
                var o = Check(OldDir(install));
                if (!o.Exists) b.Add(".old 없음");
                else if (!o.Marked) b.Add(".old 표지 없음");
                else if (!o.Ours || !o.Complete) b.Add(".old 온전하지 않음");
                break;
        }
        return b;
    }

    /// <summary>recover(끊긴 교체 마무리)를 막는 까닭: 설치 폴더가 이미 있거나, 옮길 폴더가 우리 것이 아니거나 온전하지 않다.</summary>
    public static IReadOnlyList<string> RecoverBlockers(string source, string target)
    {
        var b = new List<string>();
        if (Directory.Exists(target)) b.Add("설치 폴더 있음");
        var c = Check(source);
        if (!c.Ours) b.Add(Path.GetFileName(source) + " 우리 것 아님");
        else if (!c.Complete) b.Add(Path.GetFileName(source) + " 온전하지 않음");
        return b;
    }

    /// <summary>
    /// 시작할 때 고칠 것. 설치 폴더 이름(.new·.old)으로 실행했고 원래 설치 폴더가 없으면(교체 중 끊김) recover: 완성된 .new, 없으면 .old 를 제자리로.
    /// 설치 폴더가 files.txt 와 다르고(불완전) .old 가 온전하면 rollback 제안. 고칠 것이 없으면 null. 실행 여부는 막힘 검사(<see cref="Blockers"/>)를 거친다.
    /// </summary>
    public static Repair? Diagnose(string running)
    {
        foreach (var suffix in new[] { ".new", ".old" })
        {
            if (!running.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var target = running[..^suffix.Length];
            if (Directory.Exists(target)) return null;
            var old = Check(OldDir(target));
            var src = IsReady(NewDir(target)) ? NewDir(target) : old.Ours && old.Complete ? OldDir(target) : null;
            return src == null ? null : new Repair("recover", src, target);
        }
        if (Check(running).Complete) return null;
        var o = Check(OldDir(running));
        return o.Ours && o.Complete ? new Repair("rollback", OldDir(running), running) : null;
    }

    // ---- 교체 전 검사 ----

    /// <summary>설치 폴더와 그 부모에 쓸 수 있는가(설치 폴더 이름 바꾸기·옆에 .new 만들기). 관리자 권한 없이.</summary>
    public static bool CanReplace(string install)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(install));
        if (parent == null || !Directory.Exists(install)) return false;
        var tag = $".te-write-{Environment.ProcessId}";
        try
        {
            var f = Path.Combine(install, tag);
            File.WriteAllBytes(f, []);
            File.Delete(f);
            var d = Path.Combine(parent, tag);
            Directory.CreateDirectory(d);
            Directory.Delete(d);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>받기 전 빈 공간: zip + 풀기 + .new. 모자라면 팝업 줄, 넉넉하거나 잴 수 없으면(UNC 등) null.</summary>
    public static string? SpaceShort(ReleaseInfo r, string install, string dir)
    {
        long zip = r.Size > 0 ? r.Size : 128L << 20;
        long un = r.Unpacked > 0 ? r.Unpacked : zip * 4;
        const long margin = 64L << 20;
        try
        {
            Directory.CreateDirectory(dir);
            var a = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!);
            var b = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(install))!);
            bool same = a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase);
            long needA = zip + un + (same ? un : 0) + margin, needB = un + margin;
            if (a.AvailableFreeSpace < needA) return $"공간 부족 · {a.Name} {needA / (double)(1L << 30):0.0} GB";
            if (!same && b.AvailableFreeSpace < needB) return $"공간 부족 · {b.Name} {needB / (double)(1L << 30):0.0} GB";
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>
    /// 사용자 데이터가 도우미가 옮기는 폴더(설치, .new, .old, .trash-*) 안에 있으면 그 경로. 없으면 null.
    /// 설정·초안·받은 버전(<see cref="DataDir"/>)과 사이트 저장소는 설치 폴더 밖이어야 한다.
    /// </summary>
    public static string? DataInside(string install, IEnumerable<string?> data)
    {
        var roots = new[] { install, NewDir(install), OldDir(install) }.Select(r => Path.GetFullPath(r).TrimEnd('\\') + "\\").ToList();
        var trash = Path.GetFullPath(TrashPrefix(install));
        return data.FirstOrDefault(p =>
        {
            if (string.IsNullOrEmpty(p)) return false;
            var full = Path.GetFullPath(p).TrimEnd('\\') + "\\";
            return roots.Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) || full.StartsWith(trash, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>도우미가 옮기는 폴더 모두: 설치, .old, .new, .trash-*(있는 것만).</summary>
    public static IReadOnlyList<string> Family(string install)
    {
        var list = new List<string> { install, OldDir(install), NewDir(install) };
        var parent = Path.GetDirectoryName(install);
        if (parent != null && Directory.Exists(parent)) list.AddRange(Directory.GetDirectories(parent, Path.GetFileName(TrashPrefix(install)) + "*"));
        return list.Where(Directory.Exists).ToList();
    }

    /// <summary>설치 폴더(와 .old·.new·.trash-*)의 실행 파일로 도는 프로세스 이름(tedit, 다른 창, 옛 버전, gcc 등). except = 이 프로세스.</summary>
    public static IReadOnlyList<string> RunningIn(string install, int except) => RunningIn(Family(install), except);

    /// <summary>주어진 폴더들의 실행 파일로 도는 프로세스 이름.</summary>
    public static IReadOnlyList<string> RunningIn(IReadOnlyList<string> dirs, int except)
    {
        var roots = dirs.Where(Directory.Exists).Select(d => Path.GetFullPath(d).TrimEnd('\\') + "\\").ToList();
        var names = roots.SelectMany(r => Directory.EnumerateFiles(r, "*.exe", SearchOption.AllDirectories))
            .Select(Path.GetFileNameWithoutExtension).Distinct(StringComparer.OrdinalIgnoreCase);
        var hit = new List<string>();
        foreach (var name in names)
            foreach (var p in Process.GetProcessesByName(name!))
                using (p)
                {
                    if (p.Id == except) continue;
                    string? path;
                    try { path = p.MainModule?.FileName; }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { continue; }
                    if (path != null && roots.Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase))) hit.Add(Path.GetFileName(path));
                }
        return hit;
    }

    // ---- 교체 도우미 ----

    /// <summary>
    /// 바깥 도우미(cmd). 글은 ASCII 만, 경로는 환경 변수로 받는다(한글·공백·&amp;·()·^ 경로). 외부 명령은 System32 전체 경로.
    /// 아무것도 지우지 않는다: 버릴 폴더는 TE_DST.trash-TE_STAMP-a 로 이름을 바꾸고 성공한 뒤 .te-trash 를 쓴다(지우기는 앱이 검사 뒤).
    /// PID 가 끝나길 기다린다(tasklist 가 실패하면 끝난 것으로 보지 않고 실패). 이름 바꾸기마다 if exist 로 결과를 확인해 기록한다.
    /// apply: .old → trash, 설치 → .old, .new → 설치. 둘째가 막히면 .old → 설치, trash → .old(원래대로).
    /// rollback: 설치 → trash, .old → 설치. recover: TE_SRC → 설치(설치 폴더가 없을 때만).
    /// 옮기는 폴더는 표지(files.txt·VERSION·TableEditor.exe)가 있어야 한다. 성공했을 때만 앱을 다시 연다(TE_START). 끝에 잠금(TE_LOCK)을 지운다.
    /// 결과 낱말: ok, ok-rollback, ok-recover, fail-tasklist, fail-wait, fail-src, fail-old, fail-notours, fail-busy, fail-noold, fail-exists, fail-lost.
    /// 대기·재시도 상한: PID 기다림 TE_WAIT 번(약 1.2초씩) + 이름 바꾸기 최대 4번 × TE_TRIES 번(약 1초씩).
    /// </summary>
    public const string HelperScript = """"
        @echo off
        setlocal EnableExtensions DisableDelayedExpansion
        rem TableEditor update helper. ASCII only: paths come in environment variables.
        rem TE_MODE apply, rollback or recover. TE_DST install folder. TE_SRC folder to move in. TE_PID app process.
        rem TE_START 1 = start the app after success. TE_RESULT, TE_LOG, TE_LOCK files. TE_STAMP trash name. TE_WAIT wait loops. TE_TRIES rename tries.
        rem Nothing is deleted here. Folders to drop are renamed to TE_DST.trash-TE_STAMP-a and marked; the app deletes them after checking.
        rem Only folders with files.txt, VERSION and TableEditor.exe are moved, never links or junctions (attribute l).
        set "SYS=%SystemRoot%\System32"
        set "OLD=%TE_DST%.old"
        set "TA=%TE_DST%.trash-%TE_STAMP%-a"
        set "OKSTART=0"
        call :log begin %TE_MODE% pid %TE_PID%
        set /a W=0
        :wait
        "%SYS%\tasklist.exe" /FI "PID eq %TE_PID%" /NH /FO CSV >"%TE_RESULT%.tl" 2>nul
        if errorlevel 1 goto f_tasklist
        "%SYS%\find.exe" """%TE_PID%""" "%TE_RESULT%.tl" >nul 2>&1
        if errorlevel 2 goto f_tasklist
        if errorlevel 1 goto gone
        set /a W+=1
        if %W% geq %TE_WAIT% goto f_wait
        "%SYS%\PING.EXE" -n 2 127.0.0.1 >nul
        goto wait
        :gone
        if /i "%TE_MODE%"=="rollback" goto rollback
        if /i "%TE_MODE%"=="recover" goto recover

        if not exist "%TE_SRC%\.te-ready" goto f_src
        set "X=%TE_SRC%"
        call :ours
        if errorlevel 1 goto f_src
        set "X=%TE_DST%"
        call :ours
        if errorlevel 1 goto f_notours
        if not exist "%OLD%" goto a2
        set "X=%OLD%"
        call :ours
        if errorlevel 1 goto f_notours
        set "A=%OLD%"
        set "B=%TA%"
        call :ren
        if errorlevel 1 goto f_old
        :a2
        set "A=%TE_DST%"
        set "B=%OLD%"
        call :ren
        if errorlevel 1 goto a_busy
        set "A=%TE_SRC%"
        set "B=%TE_DST%"
        call :ren
        if not errorlevel 1 goto a_ok
        call :log back
        set "A=%OLD%"
        set "B=%TE_DST%"
        call :ren
        if errorlevel 1 goto f_lost
        :a_busy
        call :untrash
        goto f_busy
        :a_ok
        call :mark
        set "OKSTART=1"
        call :done ok
        goto end

        :rollback
        if not exist "%OLD%" goto f_noold
        set "X=%OLD%"
        call :ours
        if errorlevel 1 goto f_notours
        set "X=%TE_DST%"
        call :ours
        if errorlevel 1 goto f_notours
        set "A=%TE_DST%"
        set "B=%TA%"
        call :ren
        if errorlevel 1 goto f_busy
        set "A=%OLD%"
        set "B=%TE_DST%"
        call :ren
        if not errorlevel 1 goto r_ok
        set "A=%TA%"
        set "B=%TE_DST%"
        call :ren
        if errorlevel 1 goto f_lost
        goto f_busy
        :r_ok
        call :mark
        set "OKSTART=1"
        call :done ok-rollback
        goto end

        :recover
        if exist "%TE_DST%" goto f_exists
        set "X=%TE_SRC%"
        call :ours
        if errorlevel 1 goto f_notours
        set "A=%TE_SRC%"
        set "B=%TE_DST%"
        call :ren
        if errorlevel 1 goto f_busy
        set "OKSTART=1"
        call :done ok-recover
        goto end

        :f_tasklist
        call :done fail-tasklist
        goto end
        :f_wait
        call :done fail-wait
        goto end
        :f_src
        call :done fail-src
        goto end
        :f_old
        call :done fail-old
        goto end
        :f_notours
        call :done fail-notours
        goto end
        :f_busy
        call :done fail-busy
        goto end
        :f_noold
        call :done fail-noold
        goto end
        :f_exists
        call :done fail-exists
        goto end
        :f_lost
        call :done fail-lost
        goto end

        :end
        del "%TE_RESULT%.tl" 2>nul
        del "%TE_LOCK%" 2>nul
        if "%OKSTART%"=="1" if "%TE_START%"=="1" if exist "%TE_DST%\TableEditor.exe" start "" /D "%TE_DST%" "%TE_DST%\TableEditor.exe"
        exit /b 0

        :ours
        set "XA="
        for %%I in ("%X%") do set "XA=%%~aI"
        if not defined XA exit /b 1
        if not "%XA:l=%"=="%XA%" exit /b 1
        if not exist "%X%\files.txt" exit /b 1
        if not exist "%X%\VERSION" exit /b 1
        if not exist "%X%\TableEditor.exe" exit /b 1
        exit /b 0

        :ren
        set /a T=0
        :ren1
        if exist "%B%" goto ren_no
        move "%A%" "%B%" >nul 2>&1
        if exist "%B%" if not exist "%A%" goto ren_ok
        set /a T+=1
        if %T% geq %TE_TRIES% goto ren_no
        "%SYS%\PING.EXE" -n 2 127.0.0.1 >nul
        goto ren1
        :ren_ok
        call :log ren ok
        exit /b 0
        :ren_no
        call :log ren fail
        exit /b 1

        :untrash
        if not exist "%TA%" exit /b 0
        set "A=%TA%"
        set "B=%OLD%"
        call :ren
        exit /b 0

        :mark
        if not exist "%TA%" exit /b 0
        >"%TA%\.te-trash" echo %TE_MODE%
        exit /b 0

        :done
        >"%TE_RESULT%" echo %1
        call :log %1
        exit /b 0

        :log
        >>"%TE_LOG%" echo %date% %time% %*
        exit /b 0
        """";

    public static string ResultPath(string dir) => Path.Combine(dir, "result.txt");
    public static string LockPath(string dir) => Path.Combine(dir, "apply.lock");
    static string LaunchedPath(string dir) => Path.Combine(dir, "launched.txt");

    /// <summary>
    /// 도우미가 도는 중인가: 잠금에 적힌 도우미 cmd 의 PID 가 살아 있고 시작 시각도 같다(PID 재사용을 거른다).
    /// 앱이 <see cref="StartHelper"/> 로 쓰고, 도우미가 끝에 지운다.
    /// </summary>
    public static bool LockFresh(string dir)
    {
        try
        {
            var p = LockPath(dir);
            if (!File.Exists(p) || File.ReadAllText(p).Split(' ') is not [var a, var b]
                || !int.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                || !long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return false;
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited && Math.Abs(proc.StartTime.ToUniversalTime().Ticks - ticks) < TimeSpan.TicksPerSecond;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>끊긴 도우미가 남긴 잠금을 지운다.</summary>
    public static void DropStaleLock(string dir)
    {
        if (!LockFresh(dir)) TryDelete(LockPath(dir));
    }

    /// <summary>
    /// 도우미 시작 정보(창 없이). 도우미 글은 dir\apply.cmd, 일하는 폴더도 dir(설치 폴더를 잡지 않게).
    /// mode: apply(source = .new) · rollback · recover(source = 옮길 폴더). wait·tries = 기다림·이름 바꾸기 횟수(약 1초씩).
    /// </summary>
    public static ProcessStartInfo HelperStart(string mode, string install, string? source, int pid, bool start, string dir, int wait = 180, int tries = 20)
    {
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "apply.cmd");
        File.WriteAllText(script, HelperScript.ReplaceLineEndings("\r\n") + "\r\n", Encoding.ASCII);
        TryDelete(ResultPath(dir));
        // /s: 바깥 따옴표 한 쌍만 벗긴다 → 경로에 &·()·^ 가 있어도 그대로
        var si = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /s /c \"\"{script}\"\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = dir,
        };
        var env = si.Environment;
        var inv = CultureInfo.InvariantCulture;
        env["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        env["TE_MODE"] = mode;
        env["TE_DST"] = Path.GetFullPath(install).TrimEnd('\\');
        env["TE_SRC"] = source == null ? "" : Path.GetFullPath(source).TrimEnd('\\');
        env["TE_PID"] = pid.ToString(inv);
        env["TE_START"] = start ? "1" : "0";
        env["TE_RESULT"] = ResultPath(dir);
        env["TE_LOG"] = Path.Combine(dir, "apply.log");
        env["TE_LOCK"] = LockPath(dir);
        env["TE_STAMP"] = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", inv);
        env["TE_WAIT"] = wait.ToString(inv);
        env["TE_TRIES"] = tries.ToString(inv);
        return si;
    }

    /// <summary>도우미를 띄우고, 띄운 뒤에야 잠금(도우미 PID·시작 시각)과 launched 표지를 쓴다.</summary>
    public static Process StartHelper(ProcessStartInfo si, string dir)
    {
        var p = Process.Start(si) ?? throw new UpdateException("도우미 시작 실패");
        try { File.WriteAllText(LockPath(dir), $"{p.Id} {p.StartTime.ToUniversalTime().Ticks}"); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } // 벌써 끝남: 잠금 없이
        File.WriteAllText(LaunchedPath(dir), si.Environment["TE_MODE"]);
        return p;
    }

    /// <summary>
    /// 도우미가 남긴 결과 낱말(읽고 지운다). 띄웠는데 결과가 없으면(도우미가 못 돌았거나 끊김) "none". 띄운 적이 없으면 null.
    /// 도우미가 도는 중이면 null(아직 모름).
    /// </summary>
    public static string? TakeResult(string dir)
    {
        if (LockFresh(dir)) return null;
        try
        {
            var path = ResultPath(dir);
            bool launched = File.Exists(LaunchedPath(dir));
            TryDelete(LaunchedPath(dir));
            if (!File.Exists(path)) return launched ? "none" : null;
            var word = File.ReadAllText(path).Trim();
            File.Delete(path);
            return word;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>.old 버전(직전 버전). 없으면 null.</summary>
    public static string? OldVersion(string install) => ReadVersion(OldDir(install))?.ToString(3);

    // ---- 공통 ----

    public static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>"v0.7.1" · "0.7.1" · "0.7" → 0.7.1 · 0.7.0(세 자리로). 꼬리표(-rc1, +x)나 넷째 자리가 있으면 false.</summary>
    public static bool TryParseVersion(string? s, out Version v)
    {
        v = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(s) || VersionText.Match(s.Trim()) is not { Success: true } m) return false;
        v = new Version(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0);
        return true;
    }

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static long Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n >= 0 ? n : 0;

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // 쓰는 중이면 다음에
    }

    static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
