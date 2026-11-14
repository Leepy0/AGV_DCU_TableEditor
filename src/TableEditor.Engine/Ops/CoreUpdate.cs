using System.Text.RegularExpressions;
using TableEditor.Engine.Check;
using TableEditor.Engine.Model;

namespace TableEditor.Engine.Ops;

/// <summary>코어 올리기 결과. Changed = 커밋했다(같은 버전을 다시 받으면 false). Findings = 검사 오류, Errors = 빌드 오류.</summary>
public sealed record CoreResult(GitOutcome Outcome, string? Reason, bool Changed, string? CoreLine, IReadOnlyList<Finding> Findings, ImageBuild? Build)
{
    public bool Ok => Outcome is GitOutcome.Ok or GitOutcome.NoChange;
}

/// <summary>
/// 코어 올리기(사양 8.6절): 깨끗한지 → tools/update_core.ps1 -Ref vX.Y.Z [-From …] → core/VERSION 확인 → 다시 읽기·검사 →
/// 압축·개발·현장 빌드 → 성공이면 core 와 생성물 커밋, 실패면 core·생성물 원상(restore + clean). 화면은 팝업 확인 뒤에 부른다.
/// </summary>
public static class CoreUpdate
{
    public const string CoreUrl = "https://github.com/Leepy0/AGV_DCU_Core.git";
    static readonly Regex VersionText = new(@"^v\d+\.\d+\.\d+$");
    static readonly Regex ScriptError = new(@"(코어 묶음을 받지 못함|받은 묶음이 MANIFEST\.sha256 과 다르다|묶음 VERSION 이 ).*$");

    /// <summary>고를 버전: git ls-remote --tags &lt;코어 주소&gt; 의 dist-vX.Y.Z → "vX.Y.Z", 새것부터. 연결 안 되면 null(묶음 폴더 고르기로).</summary>
    public static Task<IReadOnlyList<string>?> ListVersionsAsync(Git git, string coreUrl = CoreUrl, CancellationToken ct = default) =>
        git.CoreTagsAsync(coreUrl, ct);

    public static async Task<CoreResult> RunAsync(Git git, string site, string version, string? from = null, ImageBuilder? build = null, Action<OutputLine>? onLine = null, CancellationToken ct = default)
    {
        static CoreResult Fail(GitOutcome o, string? why, IReadOnlyList<Finding>? f = null, ImageBuild? b = null) => new(o, why, false, null, f ?? [], b);
        if (!VersionText.IsMatch(version)) return Fail(GitOutcome.Failed, "버전이 잘못됨: " + version);
        if (from != null && (from.Trim().Length == 0 || from.TrimStart().StartsWith('-') || from.Any(char.IsControl))) return Fail(GitOutcome.Failed, "받을 곳이 잘못됨");
        var clean = await git.RequireCleanAsync(site, ct, "core/");
        if (!clean.Ok) return Fail(clean.Outcome, clean.Reason + (clean.Files.Count > 0 ? ": " + string.Join(", ", clean.Files) : ""));
        if (!File.Exists(Path.Combine(git.Root, "tools", "update_core.ps1"))) return Fail(GitOutcome.Failed, "tools/update_core.ps1 없음");

        // 3: 스크립트가 받아 확인한 뒤에만 core/ 를 바꾼다. 실패하면 core/ 그대로지만 아래 원상 단계도 돈다
        string[] args = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "tools/update_core.ps1", "-Ref", version, .. from != null ? new[] { "-From", from.Trim() } : []];
        var r = await ProcessRunner.RunAsync(new ProcessSpec("powershell", args, git.Root, TimeSpan.FromMinutes(5), ProcessRunner.Oem()), onLine, ct);
        git.NoteScript(r.TimedOut ? "timeout" : r.ExitCode.ToString(), args);
        var coreLine = r.Texts.Select(t => t.Trim()).LastOrDefault(t => t.StartsWith("core = "));
        if (!r.Ok || coreLine == null)
        {
            // PowerShell 오류 덩이에서 스크립트의 실패 글(8.6절 3)만 고른다
            var known = r.Texts.Select(t => ScriptError.Match(t)).FirstOrDefault(m => m.Success)?.Value.Trim();
            return await Restore(r.StartError ?? known ?? r.Texts.Select(t => t.Trim()).LastOrDefault(t => t.Length > 0) ?? $"종료 코드 {r.ExitCode}");
        }

        // 4: VERSION 첫 칸, 다시 읽기·검사(V-1·V-2 포함)
        if (Round.CoreVersion(git.Root) != version) return await Restore($"core/VERSION 이 {version} 가 아니다");
        var found = Checker.Run(Site.Open(Path.Combine(git.Root, "site", site), Path.Combine(git.Root, "core")));
        var errors = found.Where(f => f.Severity == Severity.Error).ToList();
        if (errors.Count > 0) return await Restore($"검사 오류 {errors.Count}", errors);

        // 5: 압축 표 다시 만들기 → 개발·현장 빌드
        var b = await (build ?? Round.DefaultBuilder())(git.Root, site, onLine, ct);
        if (!b.Ok) return await Restore(b.Reason ?? "빌드 실패", null, b);

        // 6: 커밋(같은 버전이면 바뀐 것 없음)
        var c = await git.CommitCoreAsync(site, version, ct);
        if (!c.Ok) return await Restore(c.Reason ?? "커밋 실패", null, b);
        return new(c.Outcome, null, c.Outcome == GitOutcome.Ok, coreLine, found, b);

        async Task<CoreResult> Restore(string why, IReadOnlyList<Finding>? f = null, ImageBuild? bb = null)
        {
            var back = await git.RestoreCoreAsync(site, ct);
            return Fail(GitOutcome.Failed, back.Ok ? why : $"{why} · 원상 실패: {back.Reason}", f, bb);
        }
    }
}
