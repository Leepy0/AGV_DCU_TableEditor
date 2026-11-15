using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TableEditor.Engine.Ops;

/// <summary>GitHub 정식 릴리스 하나. File = zip 이름, Sha256 = 소문자 64 hex. 둘 중 하나라도 없으면 판만 알리고 받지 않는다.</summary>
public sealed record ReleaseInfo(Version Version, string? File, string? Sha256, long Size, string Page, string Notes, DateTimeOffset? Published)
{
    public string Tag => "v" + Version.ToString(3);
    public string? AssetUrl => File == null ? null : $"{Updater.Releases}/download/{Tag}/{File}";
    public bool CanDownload => File != null && Sha256 != null;
}

/// <summary>받아서 풀고 검증한 새 판(설정에 남는다). Stage = TableEditor.exe 가 든 폴더.</summary>
public sealed record PendingUpdate(string Version, string Stage, string Sha256);

/// <summary>업데이트 막힘·실패. Message = 짧은 낱말(팝업 줄).</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 프로그램 업데이트(참고: 공개 레포 AmrMapEditor 의 Updater). 사내망은 api.github.com 이 막히고 github.com 만 열려
/// 확인은 매니페스트 → releases/latest 리다이렉트 → API 차례. 받기는 HttpClient 로 .partial 에 받고 SHA-256 이 맞을 때만 이름을 바꾼다.
/// 배포는 폴더 zip 이라 스테이징 폴더에 풀어 검증하고, 교체는 앱이 끝난 뒤 바깥 도우미(<see cref="HelperScript"/>)가
/// 설치 폴더를 <c>&lt;설치&gt;.old</c> 로 옮기고 새 판을 복사한다. 사용자 데이터(설정·초안·사이트 저장소)는 설치 폴더 밖이다.
/// </summary>
public static class Updater
{
    public const string Repo = "Leepy0/AGV_DCU_TableEditor";
    public const string ManifestName = "TableEditor.json";
    public const string ExeName = "TableEditor.exe";
    public const string CliName = "tedit.exe";
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

    static readonly HttpClient Http = Client(follow: true);
    static readonly HttpClient NoRedirect = Client(follow: false);

    static HttpClient Client(bool follow)
    {
        // 시스템 프록시 + 사내 프록시의 Windows 로그인 인증
        var h = new HttpClientHandler
        {
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            AllowAutoRedirect = follow,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var c = new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan }; // 시간 제한은 호출마다 토큰으로
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"AGV_DCU_TableEditor/{Current.ToString(3)}");
        return c;
    }

    // ---- 확인 ----

    /// <summary>최신 정식 릴리스. 없으면 null. 세 방법이 모두 실패하면 첫 예외.</summary>
    public static async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        Exception? first = null;
        foreach (var source in new Func<CancellationToken, Task<ReleaseInfo?>>[] { FromManifestAsync, FromRedirectAsync, FromApiAsync })
        {
            try
            {
                if (await source(ct) is { } r) return r;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { first ??= e; } // 다음 방법으로
        }
        return first == null ? null : throw first;
    }

    static async Task<ReleaseInfo?> FromManifestAsync(CancellationToken ct)
    {
        using var res = await Http.GetAsync($"{Releases}/latest/download/{ManifestName}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return ParseManifest(await res.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// 매니페스트 <c>{ version, file, sha256, size, published, page, notes }</c>. 판이 틀리면 null.
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
        long size = m.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
        var page = Str(m, "page");
        return new ReleaseInfo(v, plain ? file : null, hex ? sha : null, size, page.Length > 0 ? page : $"{Releases}/tag/v{v.ToString(3)}",
            Str(m, "notes"), DateTimeOffset.TryParse(Str(m, "published"), out var p) ? p : null);
    }

    /// <summary>매니페스트가 없는 릴리스(v0.1.0): releases/latest 의 Location(…/releases/tag/vX.Y.Z)에서 판만 읽는다.</summary>
    static async Task<ReleaseInfo?> FromRedirectAsync(CancellationToken ct)
    {
        using var res = await NoRedirect.GetAsync($"{Releases}/latest", HttpCompletionOption.ResponseHeadersRead, ct);
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

    /// <summary>GitHub API(사외망). zip 자산과 GitHub 가 계산한 digest 를 쓴다.</summary>
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
                size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                var digest = Str(a, "digest");
                sha = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && digest.Length == 71 ? digest[7..].ToLowerInvariant() : null;
            }
        var page = Str(r, "html_url");
        return new ReleaseInfo(v, file, sha, size, page.Length > 0 ? page : $"{Releases}/tag/v{v.ToString(3)}", Str(r, "body"),
            DateTimeOffset.TryParse(Str(r, "published_at"), out var p) ? p : null);
    }

    // ---- 받기 ----

    /// <summary>zip 을 dir 에 받는다(.partial → SHA-256 이 맞으면 이름 바꿈). 이미 받아 둔 같은 파일은 그대로. 받은 zip 경로.</summary>
    public static async Task<string> DownloadAsync(ReleaseInfo r, string dir, IProgress<double>? progress, CancellationToken ct, HttpClient? http = null)
    {
        if (!r.CanDownload) throw new UpdateException("매니페스트 없음");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, r.File!);
        if (File.Exists(target) && await Task.Run(() => HashFile(target), ct) == r.Sha256) return target;
        var part = target + ".partial";
        try
        {
            using var res = await (http ?? Http).GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            long total = res.Content.Headers.ContentLength ?? r.Size;
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buf = new byte[81920];
            long done = 0;
            double shown = -1;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                double pct = total > 0 ? Math.Min(100, done * 100.0 / total) : 0;
                if (pct - shown >= 1) { progress?.Report(pct); shown = pct; }
            }
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
    /// 받은 zip 을 <c>dir\stage-X.Y.Z</c> 에 풀고 검증한다: TableEditor.exe·tedit.exe 가 있고 VERSION 이 그 판. TableEditor.exe 가 든 폴더.
    /// zip 은 맨 위 폴더 하나(TableEditor-vX.Y.Z-win-x64)를 품는다. 다 풀면 zip 은 지운다.
    /// </summary>
    public static string Stage(string zip, Version v, string dir)
    {
        var stage = StageDir(dir, v);
        if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        try
        {
            ZipFile.ExtractToDirectory(zip, stage); // 경로가 폴더 밖을 가리키는 항목은 예외(.NET 이 막는다)
        }
        catch (InvalidDataException e)
        {
            TryDeleteDir(stage);
            throw new UpdateException("zip 손상", e);
        }
        var root = StageRoot(stage, v);
        if (root == null)
        {
            TryDeleteDir(stage);
            throw new UpdateException("묶음 틀림");
        }
        TryDelete(zip);
        return root;
    }

    public static string StageDir(string dir, Version v) => Path.Combine(dir, "stage-" + v.ToString(3));

    /// <summary>스테이징 폴더에서 TableEditor.exe 가 든 폴더(바로 아래 또는 폴더 하나 아래). 묶음이 틀리면 null.</summary>
    public static string? StageRoot(string stage, Version v)
    {
        if (!Directory.Exists(stage)) return null;
        var root = File.Exists(Path.Combine(stage, ExeName)) ? stage
            : Directory.GetDirectories(stage) is [var one] && File.Exists(Path.Combine(one, ExeName)) ? one : null;
        return root != null && IsPackage(root, v) ? root : null;
    }

    /// <summary>묶음 폴더인가: TableEditor.exe, tedit.exe, VERSION = 그 판.</summary>
    public static bool IsPackage(string root, Version v)
    {
        var ver = Path.Combine(root, "VERSION");
        return File.Exists(Path.Combine(root, ExeName)) && File.Exists(Path.Combine(root, CliName)) && File.Exists(ver)
            && TryParseVersion(File.ReadAllText(ver), out var got) && got == v;
    }

    /// <summary>받아 둔 판이 아직 쓸 만한가: 지금보다 새 판이고 스테이징이 그대로.</summary>
    public static bool PendingOk(PendingUpdate? p, Version current) =>
        p != null && TryParseVersion(p.Version, out var v) && v > current && IsPackage(p.Stage, v);

    /// <summary>시작할 때: 중단된 받기, 쓰지 않는 스테이징 정리(keep 은 남긴다).</summary>
    public static void Cleanup(string dir, string? keep)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.partial")) TryDelete(f);
            foreach (var d in Directory.GetDirectories(dir, "stage-*"))
                if (keep == null || !(Path.GetFullPath(keep) + "\\").StartsWith(Path.GetFullPath(d) + "\\", StringComparison.OrdinalIgnoreCase))
                    TryDeleteDir(d);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // 정리 실패는 다음에
    }

    // ---- 교체 전 검사 ----

    /// <summary>설치 폴더와 그 부모에 쓸 수 있는가(설치 폴더 이름 바꾸기·새로 만들기). 관리자 권한 없이.</summary>
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

    /// <summary>
    /// 사용자 데이터가 도우미가 옮기거나 지우는 폴더(설치, .old, .old~, .rollback) 안에 있으면 그 경로. 없으면 null.
    /// 설정·초안·받은 판(<see cref="DataDir"/>)과 사이트 저장소는 설치 폴더 밖이어야 한다.
    /// </summary>
    public static string? DataInside(string install, IEnumerable<string?> data)
    {
        var roots = new[] { install, OldDir(install), install + ".old~", install + ".rollback" }.Select(r => Path.GetFullPath(r).TrimEnd('\\') + "\\").ToList();
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
    /// 바깥 도우미(cmd + robocopy). 글은 ASCII 만, 경로는 환경 변수로 받는다(cmd 가 유니코드 그대로 넘긴다: 한글·공백·&amp; 경로).
    /// apply: PID 가 끝나길 기다림 → 이전 .old → .old~ → 설치 → 설치.old → 새 판 복사 → 됐으면 .old~ 지움.
    /// 복사가 틀리면 설치를 지우고 .old → 설치, .old~ → .old(원래대로).
    /// rollback: 설치 → 설치.rollback → 설치.old → 설치 → .rollback 지움. 이름 바꾸기가 막히면(tedit 실행 중 등) 바꾸지 않고 끝.
    /// 결과 낱말 하나를 TE_RESULT 에 쓴다: ok, ok-rollback, fail-wait, fail-src, fail-old, fail-busy, fail-copy, fail-noold, fail-lost.
    /// </summary>
    public const string HelperScript = """"
        @echo off
        setlocal EnableExtensions DisableDelayedExpansion
        rem TableEditor update helper. ASCII only: paths come in environment variables.
        rem TE_MODE apply or rollback, TE_DST install folder, TE_SRC new package folder, TE_PID app process,
        rem TE_START 1 = start the app at the end, TE_RESULT result word file, TE_LOG log, TE_WAIT wait loops, TE_TRIES rename tries
        set "OLD=%TE_DST%.old"
        set "BAD=%TE_DST%.rollback"
        set "PREV=%TE_DST%.old~"
        call :log begin %TE_MODE% pid %TE_PID%
        set /a W=0
        :wait
        tasklist /FI "PID eq %TE_PID%" /NH /FO CSV 2>nul | find """%TE_PID%""" >nul
        if errorlevel 1 goto gone
        set /a W+=1
        if %W% geq %TE_WAIT% goto f_wait
        ping -n 2 127.0.0.1 >nul
        goto wait
        :gone
        if /i "%TE_MODE%"=="rollback" goto rollback

        rem apply: keep the previous .old as .old~ until the new copy is good
        if not exist "%TE_SRC%\TableEditor.exe" goto f_src
        if exist "%PREV%" rmdir /s /q "%PREV%"
        if exist "%PREV%" goto f_old
        if not exist "%OLD%" goto a_move
        set "A=%OLD%"
        set "B=%PREV%"
        call :ren
        if errorlevel 1 goto f_old
        :a_move
        set "A=%TE_DST%"
        set "B=%OLD%"
        call :ren
        if errorlevel 1 goto a_busy
        call :log copy
        robocopy "%TE_SRC%" "%TE_DST%" /E /R:1 /W:1 /NP /NFL /NDL /NJH /NJS >>"%TE_LOG%" 2>&1
        if errorlevel 8 goto back
        if not exist "%TE_DST%\TableEditor.exe" goto back
        rmdir /s /q "%PREV%" 2>nul
        call :done ok
        goto end
        :back
        call :log restore
        if exist "%TE_DST%" rmdir /s /q "%TE_DST%"
        set "A=%OLD%"
        set "B=%TE_DST%"
        call :ren
        if errorlevel 1 goto f_lost
        call :unprev
        call :done fail-copy
        goto end
        :a_busy
        call :unprev
        goto f_busy

        :rollback
        if not exist "%OLD%\TableEditor.exe" goto f_noold
        if exist "%BAD%" rmdir /s /q "%BAD%"
        if exist "%BAD%" goto f_old
        set "A=%TE_DST%"
        set "B=%BAD%"
        call :ren
        if errorlevel 1 goto f_busy
        set "A=%OLD%"
        set "B=%TE_DST%"
        call :ren
        if not errorlevel 1 goto rb_ok
        set "A=%BAD%"
        set "B=%TE_DST%"
        call :ren
        if errorlevel 1 goto f_lost
        call :done fail-busy
        goto end
        :rb_ok
        rmdir /s /q "%BAD%" 2>nul
        call :done ok-rollback
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
        :f_busy
        call :done fail-busy
        goto end
        :f_noold
        call :done fail-noold
        goto end
        :f_lost
        call :done fail-lost
        exit /b 4

        :end
        if "%TE_START%"=="1" if exist "%TE_DST%\TableEditor.exe" start "" /D "%TE_DST%" "%TE_DST%\TableEditor.exe"
        exit /b 0

        :ren
        set /a T=0
        :ren1
        if exist "%B%" exit /b 1
        move "%A%" "%B%" >nul 2>&1
        if exist "%B%" if not exist "%A%" exit /b 0
        set /a T+=1
        if %T% geq %TE_TRIES% exit /b 1
        ping -n 2 127.0.0.1 >nul
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

    /// <summary>
    /// 도우미 시작 정보(cmd.exe 창 없이). 도우미 글은 dir\apply.cmd 에 쓰고, 일하는 폴더도 dir(설치 폴더를 잡지 않게).
    /// source = 새 판 폴더(apply), null = rollback. wait·tries = 기다림·이름 바꾸기 횟수(약 1초씩).
    /// </summary>
    public static ProcessStartInfo HelperStart(string install, string? source, int pid, bool start, string dir, int wait = 180, int tries = 20)
    {
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "apply.cmd");
        File.WriteAllText(script, HelperScript.ReplaceLineEndings("\r\n") + "\r\n", Encoding.ASCII);
        TryDelete(ResultPath(dir));
        var si = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /c \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = dir,
        };
        var env = si.Environment;
        env["TE_MODE"] = source == null ? "rollback" : "apply";
        env["TE_DST"] = Path.GetFullPath(install).TrimEnd('\\');
        env["TE_SRC"] = source == null ? "" : Path.GetFullPath(source).TrimEnd('\\');
        env["TE_PID"] = pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        env["TE_START"] = start ? "1" : "0";
        env["TE_RESULT"] = ResultPath(dir);
        env["TE_LOG"] = Path.Combine(dir, "apply.log");
        env["TE_WAIT"] = wait.ToString(System.Globalization.CultureInfo.InvariantCulture);
        env["TE_TRIES"] = tries.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return si;
    }

    /// <summary>도우미가 남긴 결과 낱말(읽고 지운다). 없으면 null.</summary>
    public static string? TakeResult(string dir)
    {
        var path = ResultPath(dir);
        try
        {
            if (!File.Exists(path)) return null;
            var word = File.ReadAllText(path).Trim();
            File.Delete(path);
            return word;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>.old 판(되돌릴 판). 없으면 null.</summary>
    public static string? OldVersion(string install)
    {
        var exe = Path.Combine(OldDir(install), ExeName);
        if (!File.Exists(exe)) return null;
        var pv = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
        return pv != null && TryParseVersion(pv.Split('+')[0], out var v) ? v.ToString(3) : "?";
    }

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
