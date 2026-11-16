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

    /// <summary>설정의 cl2000 경로(<c>…/bin/cl2000.exe</c>)에서 Cgt 폴더(bin 의 상위). 비었거나 bin 아래가 아니면 null.</summary>
    public static string? CgtOf(string? cl2000Path)
    {
        if (string.IsNullOrWhiteSpace(cl2000Path)) return null;
        var bin = Path.GetDirectoryName(cl2000Path.Trim());
        return bin != null && Path.GetFileName(bin).Equals("bin", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(bin) : null;
    }

    /// <summary>앱의 tools/check.ps1 가 <c>$Cgt</c> 인자를 아는가(모르는 인자를 넘기면 스크립트가 실패한다). <c>param( … )</c> 블록 안의 매개변수만 인정한다(지역 변수·주석의 글자는 아니다).</summary>
    public static bool KnowsCgt(string appRoot)
    {
        try { return Regex.IsMatch(ParamBlock(File.ReadAllText(Path.Combine(appRoot, "tools", "check.ps1"))), @"\$Cgt\b", RegexOptions.IgnoreCase); }
        catch (IOException) { return false; }
    }

    /// <summary>스크립트의 첫 <c>param( … )</c> 안쪽 글(괄호 짝·따옴표·# 주석을 읽는다). 없으면 빈 글.</summary>
    static string ParamBlock(string script)
    {
        var m = Regex.Match(script, @"^[ \t]*param\s*\(", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (!m.Success) return "";
        int depth = 1;
        for (int i = m.Index + m.Length; i < script.Length; i++)
        {
            char c = script[i];
            if (c is '\'' or '"') { int e = script.IndexOf(c, i + 1); if (e < 0) break; i = e; }
            else if (c == '#') { int e = script.IndexOf('\n', i); if (e < 0) break; i = e; }
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return script[(m.Index + m.Length)..i];
        }
        return "";
    }

    /// <summary>powershell 인자. cl2000 경로가 있고 스크립트가 <c>-Cgt</c> 를 알 때만 <c>-Cgt</c> 를 넘긴다.</summary>
    public static string[] ScriptArgs(string appRoot, string site, string name, bool field, string? cl2000Path = null)
    {
        var cgt = CgtOf(cl2000Path);
        return ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "tools/check.ps1", "-Site", site, "-Name", name, "-BuildOnly",
            .. field ? new[] { "-Field" } : [], .. cgt != null && KnowsCgt(appRoot) ? new[] { "-Cgt", cgt } : []];
    }

    public static async Task<BuildResult> RunAsync(string appRoot, string site, string? name = null, bool field = false, Action<OutputLine>? onLine = null, CancellationToken ct = default, string? cl2000Path = null)
    {
        if (site.Length == 0 || site.IndexOfAny(['/', '\\', ':']) >= 0 || site == "..") return Fail("사이트 이름이 잘못됨: " + site);
        if (!File.Exists(Path.Combine(appRoot, "tools", "check.ps1"))) return Fail("tools/check.ps1 없음: " + appRoot);
        if (ToolProbe.Which("powershell") == null) return Fail("powershell 없음(PATH)");
        name ??= DefaultName(site);
        if (field) name += "_field";
        var args = ScriptArgs(appRoot, site, name, field, cl2000Path);
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
