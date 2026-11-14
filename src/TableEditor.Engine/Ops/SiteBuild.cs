using System.Text.RegularExpressions;

namespace TableEditor.Engine.Ops;

/// <summary>빌드 결과. Ok = 종료 코드 0 이고 마지막 줄이 "빌드 통과(시험 생략) -&gt; 경로". OutPath 는 그 .out.</summary>
public sealed record BuildResult(bool Ok, int ExitCode, string? OutPath, string? Reason, IReadOnlyList<BuildError> Errors, IReadOnlyList<string> Lines);

/// <summary>사양 7.2절 4·5: tools/check.ps1 -BuildOnly [-Field]. 컴파일러는 직접 부르지 않는다.</summary>
public static class SiteBuild
{
    static readonly Regex Passed = new(@"^빌드 통과\(시험 생략\) -> (.+)$");

    /// <summary>이미지 이름 기본값: T143_xx → agv_143(check.ps1 의 -Name 규칙. 현장 이미지는 뒤에 _field).</summary>
    public static string DefaultName(string site)
    {
        var m = Regex.Match(site, @"^T(\d+)");
        return m.Success ? "agv_" + m.Groups[1].Value : "agv_" + site;
    }

    public static async Task<BuildResult> RunAsync(string appRoot, string site, string? name = null, bool field = false, Action<OutputLine>? onLine = null, CancellationToken ct = default)
    {
        if (site.Length == 0 || site.IndexOfAny(['/', '\\', ':']) >= 0 || site == "..") return Fail("사이트 이름이 잘못됨: " + site);
        if (!File.Exists(Path.Combine(appRoot, "tools", "check.ps1"))) return Fail("tools/check.ps1 없음: " + appRoot);
        if (ToolProbe.Which("powershell") == null) return Fail("powershell 없음(PATH)");
        name ??= DefaultName(site);
        if (field) name += "_field";
        string[] args = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "tools/check.ps1", "-Site", site, "-Name", name, "-BuildOnly", .. field ? new[] { "-Field" } : []];
        var r = await ProcessRunner.RunAsync(new ProcessSpec("powershell", args, appRoot, TimeSpan.FromMinutes(10), ProcessRunner.Oem()), onLine, ct);
        var texts = r.Texts.ToList();
        var errors = BuildErrors.Parse(texts, Path.Combine(appRoot, "site", site));
        if (r.StartError != null) return new BuildResult(false, r.ExitCode, null, r.StartError, errors, texts);
        if (r.Cancelled) return new BuildResult(false, r.ExitCode, null, "취소됨", errors, texts);
        if (r.TimedOut) return new BuildResult(false, r.ExitCode, null, "시간 초과", errors, texts);
        var last = texts.LastOrDefault(t => t.Trim().Length > 0)?.Trim();
        var m = last == null ? null : Passed.Match(last);
        if (r.ExitCode == 0 && m is { Success: true }) return new BuildResult(true, 0, m.Groups[1].Value, null, errors, texts);
        return new BuildResult(false, r.ExitCode, null, r.ExitCode == 0 ? "마지막 줄이 '빌드 통과' 가 아님" : $"종료 코드 {r.ExitCode}", errors, texts);

        static BuildResult Fail(string why) => new(false, -1, null, why, [], []);
    }
}
