namespace TableEditor.Engine.Ops;

/// <summary>도구 하나의 상태. 없으면 Found=false 와 Detail(이유). 제한 시간을 넘으면 Found=true, Slow=true(경고). 예외를 내지 않는다.</summary>
public sealed record ToolStatus(string Name, bool Found, string? Path, string? Version, string? Detail, bool Slow = false);

/// <summary>사양 7.1절 도구 확인.</summary>
public static class ToolProbe
{
    public const string Cl2000Default = @"C:\ti\ti-cgt-c2000_18.12.2.LTS\ti-cgt-c2000_18.12.2.LTS\bin\cl2000.exe";

    /// <summary>휴대용 gcc 기본 경로: 실행 파일 폴더\gcc\bin\gcc.exe</summary>
    public static string DefaultGcc() => System.IO.Path.Combine(AppContext.BaseDirectory, "gcc", "bin", "gcc.exe");

    public static async Task<IReadOnlyList<ToolStatus>> ProbeAllAsync(string? gccPath = null, string cl2000Path = Cl2000Default, CancellationToken ct = default)
    {
        var all = await Task.WhenAll(Cl2000Async(cl2000Path, ct), GitAsync(ct), PowerShellAsync(ct), GccAsync(gccPath ?? DefaultGcc(), ct));
        return all;
    }

    /// <summary>하위 도구를 안 띄우는 <c>--compiler_revision</c>(출력 예: 18.12.2). 제한 시간 20 s 는 D1(회사 PC 실측) 뒤에 정한다.</summary>
    public static async Task<ToolStatus> Cl2000Async(string path = Cl2000Default, CancellationToken ct = default, TimeSpan? limit = null)
    {
        var r = await FirstLine("cl2000", path, ["--compiler_revision"], ct, limit);
        return r with { Version = r.Version is { } v && System.Text.RegularExpressions.Regex.Match(v, @"\d+(\.\d+)+\S*") is { Success: true } m ? "v" + m.Value : r.Version };
    }

    public static Task<ToolStatus> GitAsync(CancellationToken ct = default) => FirstLine("git", Which("git"), ["--version"], ct);

    public static Task<ToolStatus> PowerShellAsync(CancellationToken ct = default) =>
        FirstLine("powershell", Which("powershell"), ["-NoProfile", "-Command", "$PSVersionTable.PSVersion.ToString()"], ct);

    public static Task<ToolStatus> GccAsync(string path, CancellationToken ct = default) => FirstLine("gcc", path, ["--version"], ct);

    /// <summary>PATH 에서 실행 파일을 찾는다(.exe). 없으면 null.</summary>
    public static string? Which(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var f = System.IO.Path.Combine(dir.Trim('"'), name + ".exe");
                if (File.Exists(f)) return f;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    static async Task<ToolStatus> FirstLine(string name, string? path, string[] args, CancellationToken ct, TimeSpan? limit = null)
    {
        if (path == null || !File.Exists(path)) return new ToolStatus(name, false, path, null, path == null ? "PATH 에 없음" : "파일 없음");
        var r = await ProcessRunner.RunAsync(new ProcessSpec(path, args, Timeout: limit ?? TimeSpan.FromSeconds(20), OutputEncoding: ProcessRunner.Oem()), null, ct);
        if (r.StartError != null) return new ToolStatus(name, false, path, null, r.StartError);
        if (r.TimedOut && name == "cl2000") return new ToolStatus(name, true, path, null, "느림", Slow: true); // 있다: 실패 아니라 경고, 빌드는 계속
        var line = r.Texts.Select(t => t.Trim()).FirstOrDefault(t => t.Length > 0);
        return r.Ok && line != null
            ? new ToolStatus(name, true, path, line, null)
            : new ToolStatus(name, false, path, line, r.TimedOut ? "시간 초과" : $"종료 코드 {r.ExitCode}");
    }
}
