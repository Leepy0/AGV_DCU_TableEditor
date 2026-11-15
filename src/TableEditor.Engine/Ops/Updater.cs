using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TableEditor.Engine.Ops;

/// <summary>
/// GitHub 정식 릴리스 하나. File = zip 이름, Sha256 = 소문자 64 hex. 둘 중 하나라도 없으면 판만 알리고 받지 않는다.
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
/// 교체: 앱이 끝난 뒤 바깥 도우미(<see cref="HelperScript"/>)가 이름 바꾸기 둘(설치 → .old, .new → 설치)로 한다.
/// 우리 표지(VERSION + TableEditor.exe + files.txt)가 없거나 목록 밖 파일이 있는 폴더는 옮기거나 지우지 않는다.
/// 사용자 데이터(설정·초안·사이트 저장소)는 설치 폴더 밖이다(<see cref="DataInside"/>).
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
    public static string Releases => $"https://github.com/{Repo}/releases";

    /// <summary>설정·초안·받은 판이 사는 곳(설치 폴더 밖).</summary>
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AGV_DCU_TableEditor");
    public static string UpdateDir => Path.Combine(DataDir, "updates");

    // 진입 어셈블리(TableEditor.exe)의 판·커밋·채널. 아래 Http 보다 먼저 읽어야 한다(필드 초기화는 글 차례)
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

    /// <summary>게시한 판만 스스로 바꾼다(bin 폴더는 바꾸지 않는다).</summary>
    public static bool CanSelfUpdate => Channel == "release";

    public static string InstallDir => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    public static string OldDir(string install) => install + ".old";
    public static string NewDir(string install) => install + ".new";

    // 리다이렉트는 손으로 따라간다: 실패한 호스트 이름을 알리려고(자산은 다른 호스트로 넘어간다)
    static readonly HttpClient Http = Client();

    static HttpClient Client()
    {
        var h = new HttpClientHandler
        {
            UseProxy = true,
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

    /// <summary>최신 정식 릴리스. 없으면 null. 방법마다 8초, 시간 초과·실패면 다음 방법. 모두 실패하면 첫 예외.</summary>
    public static async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        Exception? first = null;
        foreach (var source in new Func<CancellationToken, Task<ReleaseInfo?>>[] { FromManifestAsync, FromRedirectAsync, FromApiAsync })
        {
            using var one = CancellationTokenSource.CreateLinkedTokenSource(ct);
            one.CancelAfter(PerMethod);
            try
            {
                if (await source(one.Token) is { } r) return r;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException e) { first ??= new UpdateException("시간 초과", e); }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException or UpdateException or InvalidOperationException) { first ??= e; }
        }
        return first == null ? null : throw first;
    }

    static async Task<ReleaseInfo?> FromManifestAsync(CancellationToken ct)
    {
        using var res = await GetFollowAsync(Http, new Uri($"{Releases}/latest/download/{ManifestName}"), ct, notFoundOk: true);
        return res == null ? null : ParseManifest(await res.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// 매니페스트 <c>{ version, file, sha256, size, unpacked, published, page, notes }</c>. 판이 틀리면 null.
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

    /// <summary>매니페스트가 없는 릴리스(v0.1.0): releases/latest 의 Location(…/releases/tag/vX.Y.Z)에서 판만 읽는다.</summary>
    static async Task<ReleaseInfo?> FromRedirectAsync(CancellationToken ct)
    {
        using var res = await Http.GetAsync($"{Releases}/latest", HttpCompletionOption.ResponseHeadersRead, ct);
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
    static async Task<ReleaseInfo?> FromApiAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var res = await Http.SendAsync(req, ct);
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
    static async Task<HttpResponseMessage?> GetFollowAsync(HttpClient http, Uri url, CancellationToken ct, bool notFoundOk = false)
    {
        for (int hop = 0; hop < 6; hop++)
        {
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

    /// <summary>zip 을 dir 에 받는다(.partial → SHA-256 이 맞으면 이름 바꿈). 이미 받아 둔 같은 파일은 그대로. 30초 동안 바이트가 안 오면 실패.</summary>
    public static async Task<string> DownloadAsync(ReleaseInfo r, string dir, IProgress<double>? progress, CancellationToken ct, HttpClient? http = null)
    {
        if (!r.CanDownload) throw new UpdateException("매니페스트 없음");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, r.File!);
        if (File.Exists(target) && await Task.Run(() => HashFile(target), ct) == r.Sha256) return target;
        var part = target + ".partial";
        var url = new Uri(r.AssetUrl!);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            idle.CancelAfter(Idle);
            using var res = (await GetFollowAsync(http ?? Http, url, idle.Token))!;
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
        return target;
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
    /// 새 판을 설치 폴더 옆 &lt;설치&gt;.new 에 완성한다(앱이 도는 동안). 이미 같은 sha 로 완성돼 있으면 그대로.
    /// zip → dir\stage-X.tmp-&lt;pid&gt; 에 풀고 files.txt·항목 수·크기·판 대조 → &lt;설치&gt;.new.tmp-&lt;pid&gt; 로 옮김(다른 볼륨이면 복사) → 다시 대조 →
    /// .te-ready → 이름 바꿔 .new 로 확정. 어떤 예외든 임시 폴더는 치운다. .new 경로.
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
                if (string.Equals(Path.GetPathRoot(Path.GetFullPath(root)), Path.GetPathRoot(Path.GetFullPath(tmp)), StringComparison.OrdinalIgnoreCase))
                    Directory.Move(root, tmp);
                else
                    CopyDir(root, tmp, ct);
                ct.ThrowIfCancellationRequested();
                RequirePackage(tmp, r.Version);
                File.WriteAllText(Path.Combine(tmp, ReadyName), r.Sha256 + "\r\n");
                if (Directory.Exists(nw))
                {
                    if (!Check(nw).Ours) throw new UpdateException(Path.GetFileName(nw) + " 우리 것 아님");
                    Directory.Delete(nw, recursive: true);
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

    /// <summary>묶음 폴더가 온전한가(표지, files.txt 와 같음, 목록 밖·.git 없음, VERSION = 그 판). 아니면 UpdateException.</summary>
    public static void RequirePackage(string root, Version v)
    {
        var c = Check(root);
        if (!c.Marked) throw new UpdateException("묶음 표지 없음");
        if (c.Missing.Count > 0) throw new UpdateException("묶음 불완전 · " + c.Missing[0]);
        if (!c.Ours) throw new UpdateException("묶음 목록 밖 · " + (c.Extra.FirstOrDefault() ?? ".git"));
        if (ReadVersion(root) != v) throw new UpdateException("묶음 판 다름");
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

    /// <summary>받아 둔 새 판(&lt;설치&gt;.new, 지금보다 새 판). 없으면 null. 창마다 따로 기억하지 않고 이 폴더가 기준이다.</summary>
    public static Version? Pending(string install, Version current)
    {
        var nw = NewDir(install);
        return IsReady(nw) && ReadVersion(nw) is { } v && v > current ? v : null;
    }

    /// <summary>[버리기]: 받아 둔 .new 를 지운다(우리 것일 때만).</summary>
    public static bool Discard(string install)
    {
        var nw = NewDir(install);
        if (!Directory.Exists(nw)) return true;
        if (!Check(nw).Ours) return false;
        Directory.Delete(nw, recursive: true);
        return true;
    }

    /// <summary>
    /// 시작할 때(잠금이 없을 때만): 중단된 받기, 스테이징 임시 폴더, 설치 폴더 옆 .new.tmp-&lt;pid&gt; 정리,
    /// 그리고 지금 판 이하인 .new(이미 적용됨) 정리.
    /// </summary>
    public static void Cleanup(string dir, string install, Version current)
    {
        if (File.Exists(LockPath(dir))) return;
        try
        {
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.GetFiles(dir, "*.partial")) TryDelete(f);
                foreach (var d in Directory.GetDirectories(dir, "stage-*")) TryDeleteDir(d);
            }
            var parent = Path.GetDirectoryName(install);
            if (parent != null)
                foreach (var d in Directory.GetDirectories(parent, Path.GetFileName(NewDir(install)) + ".tmp-*"))
                    if (int.TryParse(d[(d.LastIndexOf('-') + 1)..], out _)) TryDeleteDir(d); // shortcut: 이름만 본다(.new.tmp-숫자), 남이 같은 이름을 쓰면 지워진다
            var nw = NewDir(install);
            if (Directory.Exists(nw) && Check(nw).Ours && (ReadVersion(nw) is not { } v || v <= current)) TryDeleteDir(nw);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // 정리 실패는 다음에
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

    /// <summary>폴더를 files.txt 와 대조한다(숨김·시스템 파일 포함). files.txt·.te-ready 는 목록 밖으로 치지 않는다.</summary>
    public static FolderCheck Check(string dir)
    {
        if (!Directory.Exists(dir)) return new(false, false, false, [], []);
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
        var opt = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
        foreach (var f in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opt))
        {
            var rel = f.FullName[root.Length..];
            if (f.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)) git = true;
            if (f.Attributes.HasFlag(FileAttributes.ReparsePoint)) { extra.Add(rel); continue; } // 링크·정션은 우리 묶음에 없다
            if (f is DirectoryInfo) continue;
            if (rel.Equals(ListName, StringComparison.OrdinalIgnoreCase) || rel.Equals(ReadyName, StringComparison.OrdinalIgnoreCase)) continue;
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

    /// <summary>폴더의 VERSION 판. 없거나 틀리면 null.</summary>
    public static Version? ReadVersion(string dir)
    {
        var f = Path.Combine(dir, "VERSION");
        try { return File.Exists(f) && TryParseVersion(File.ReadAllText(f), out var v) ? v : null; }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// 막는 까닭(팝업 줄). 비면 해도 된다. mode: prepare(받기) · apply(적용) · rollback(되돌리기).
    /// 설치 폴더에 목록 밖 파일·.git 이 있거나 표지가 없으면 막는다. 옆 폴더(.new, .old, .old~, .rollback)가 있는데 우리 것이 아니면 막는다.
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
        Foreign(install + ".old~");
        switch (mode)
        {
            case "prepare":
                Foreign(NewDir(install));
                Foreign(OldDir(install));
                break;
            case "apply":
                Foreign(OldDir(install));
                if (!IsReady(NewDir(install))) b.Add("받은 판 없음");
                break;
            case "rollback":
                var o = Check(OldDir(install));
                if (!o.Exists) b.Add(".old 없음");
                else if (!o.Marked) b.Add(".old 표지 없음");
                else if (!o.Ours || !o.Complete) b.Add(".old 온전하지 않음");
                Foreign(install + ".rollback");
                break;
        }
        return b;
    }

    /// <summary>
    /// 시작할 때 고칠 것. 설치 폴더 이름(.new·.old)으로 실행했고 원래 설치 폴더가 없으면(교체 중 끊김) recover: 완성된 .new, 없으면 .old 를 제자리로.
    /// 설치 폴더가 files.txt 와 다르고(불완전) .old 가 온전하면 rollback 제안. 고칠 것이 없으면 null.
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
    /// 사용자 데이터가 도우미가 옮기거나 지우는 폴더(설치, .new, .old, .old~, .rollback) 안에 있으면 그 경로. 없으면 null.
    /// 설정·초안·받은 판(<see cref="DataDir"/>)과 사이트 저장소는 설치 폴더 밖이어야 한다.
    /// </summary>
    public static string? DataInside(string install, IEnumerable<string?> data)
    {
        var roots = new[] { install, NewDir(install), OldDir(install), install + ".old~", install + ".rollback" }
            .Select(r => Path.GetFullPath(r).TrimEnd('\\') + "\\").ToList();
        return data.FirstOrDefault(p => !string.IsNullOrEmpty(p)
            && roots.Any(r => (Path.GetFullPath(p).TrimEnd('\\') + "\\").StartsWith(r, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>설치 폴더의 실행 파일로 도는 프로세스 이름(tedit.exe, 다른 TableEditor 창, gcc 등). except = 이 프로세스.</summary>
    public static IReadOnlyList<string> RunningIn(string install, int except)
    {
        var root = Path.GetFullPath(install).TrimEnd('\\') + "\\";
        var names = Directory.EnumerateFiles(install, "*.exe", SearchOption.AllDirectories)
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
                    if (path != null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) hit.Add(Path.GetFileName(path));
                }
        return hit;
    }

    // ---- 교체 도우미 ----

    /// <summary>
    /// 바깥 도우미(cmd). 글은 ASCII 만, 경로는 환경 변수로 받는다(한글·공백·&amp;·()·^ 경로). 외부 명령은 System32 전체 경로.
    /// 처음에 잠금(TE_LOCK)을 쓰고 끝에 지운다. PID 가 끝나길 기다린다(tasklist 가 실패하면 끝난 것으로 보지 않고 실패).
    /// apply: (.old → .old~) → 설치 → .old → .new → 설치 → .old~ 지움. 둘째 이름 바꾸기가 막히면 .old → 설치, .old~ → .old.
    /// rollback: 설치 → .rollback → .old → 설치 → .rollback 지움. recover: TE_SRC → 설치(설치 폴더가 없을 때만).
    /// 옮기거나 지우는 폴더는 표지(files.txt·VERSION·TableEditor.exe)가 있어야 한다. 성공했을 때만 앱을 다시 연다(TE_START).
    /// 결과 낱말: ok, ok-rollback, ok-recover, fail-tasklist, fail-wait, fail-src, fail-old, fail-notours, fail-busy, fail-noold, fail-exists, fail-lost.
    /// </summary>
    public const string HelperScript = """"
        @echo off
        setlocal EnableExtensions DisableDelayedExpansion
        rem TableEditor update helper. ASCII only: paths come in environment variables.
        rem TE_MODE apply, rollback or recover. TE_DST install folder. TE_SRC folder to move in. TE_PID app process.
        rem TE_START 1 = start the app after success. TE_RESULT, TE_LOG, TE_LOCK files. TE_WAIT wait loops. TE_TRIES rename tries.
        rem Only folders with files.txt, VERSION and TableEditor.exe are moved or deleted.
        set "SYS=%SystemRoot%\System32"
        set "OLD=%TE_DST%.old"
        set "PREV=%TE_DST%.old~"
        set "BAD=%TE_DST%.rollback"
        set "OKSTART=0"
        >"%TE_LOCK%" echo %TE_MODE%
        call :log begin %TE_MODE% pid %TE_PID%
        set /a W=0
        :wait
        "%SYS%\tasklist.exe" /FI "PID eq %TE_PID%" /NH /FO CSV >"%TE_LOCK%.tl" 2>nul
        if errorlevel 1 goto f_tasklist
        "%SYS%\find.exe" """%TE_PID%""" "%TE_LOCK%.tl" >nul 2>&1
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
        if not exist "%PREV%" goto a1
        set "X=%PREV%"
        call :ours
        if errorlevel 1 goto f_notours
        rmdir /s /q "%PREV%"
        if exist "%PREV%" goto f_old
        :a1
        if not exist "%OLD%" goto a2
        set "X=%OLD%"
        call :ours
        if errorlevel 1 goto f_notours
        set "A=%OLD%"
        set "B=%PREV%"
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
        call :unprev
        goto f_busy
        :a_ok
        if exist "%PREV%" rmdir /s /q "%PREV%"
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
        if not exist "%BAD%" goto r1
        set "X=%BAD%"
        call :ours
        if errorlevel 1 goto f_notours
        rmdir /s /q "%BAD%"
        if exist "%BAD%" goto f_old
        :r1
        set "A=%TE_DST%"
        set "B=%BAD%"
        call :ren
        if errorlevel 1 goto f_busy
        set "A=%OLD%"
        set "B=%TE_DST%"
        call :ren
        if not errorlevel 1 goto r_ok
        set "A=%BAD%"
        set "B=%TE_DST%"
        call :ren
        if errorlevel 1 goto f_lost
        goto f_busy
        :r_ok
        rmdir /s /q "%BAD%" 2>nul
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
        del "%TE_LOCK%.tl" 2>nul
        del "%TE_LOCK%" 2>nul
        if "%OKSTART%"=="1" if "%TE_START%"=="1" if exist "%TE_DST%\TableEditor.exe" start "" /D "%TE_DST%" "%TE_DST%\TableEditor.exe"
        exit /b 0

        :ours
        if not exist "%X%\files.txt" exit /b 1
        if not exist "%X%\VERSION" exit /b 1
        if not exist "%X%\TableEditor.exe" exit /b 1
        exit /b 0

        :ren
        set /a T=0
        :ren1
        if exist "%B%" exit /b 1
        move "%A%" "%B%" >nul 2>&1
        if exist "%B%" if not exist "%A%" exit /b 0
        set /a T+=1
        if %T% geq %TE_TRIES% exit /b 1
        "%SYS%\PING.EXE" -n 2 127.0.0.1 >nul
        goto ren1

        :unprev
        if not exist "%PREV%" exit /b 0
        set "A=%PREV%"
        set "B=%OLD%"
        call :ren
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

    /// <summary>도우미가 도는 중인가(잠금이 10분 안에 쓰였다). 더 오래된 잠금은 끊긴 도우미가 남긴 것이다.</summary>
    public static bool LockFresh(string dir)
    {
        var p = LockPath(dir);
        return File.Exists(p) && DateTime.UtcNow - File.GetLastWriteTimeUtc(p) < TimeSpan.FromMinutes(10);
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
        File.WriteAllText(LaunchedPath(dir), mode);
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
        env["TE_WAIT"] = wait.ToString(inv);
        env["TE_TRIES"] = tries.ToString(inv);
        return si;
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

    /// <summary>.old 판(되돌릴 판). 없으면 null.</summary>
    public static string? OldVersion(string install) => ReadVersion(OldDir(install))?.ToString(3);

    // ---- 공통 ----

    public static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>"v0.7.1" · "0.7.1" · "0.7" → 0.7.1 · 0.7.0(세 자리로).</summary>
    public static bool TryParseVersion(string? s, out Version v)
    {
        v = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (!Version.TryParse(t, out var parsed)) return false;
        v = Normalize(parsed);
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
