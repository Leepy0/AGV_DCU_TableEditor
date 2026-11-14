namespace TableEditor.Engine.Ops;

/// <summary>개발·현장 이미지 빌드 결과. DevOut·FieldOut = 만든 .out(.map 은 같은 이름).</summary>
public sealed record ImageBuild(bool Ok, string? DevOut, string? FieldOut, string? Reason, IReadOnlyList<BuildError> Errors);

/// <summary>회차 내기·코어 올리기의 빌드 단계. 시험은 가짜 .out 을 만드는 것으로 갈아 끼운다.</summary>
public delegate Task<ImageBuild> ImageBuilder(string appRoot, string site, Action<OutputLine>? onLine, CancellationToken ct);

/// <summary>회차 결과. Number = 회차 번호 글자(예 "74"), Unshared = README 줄에 미공유, Line = 쓴 README 회차 줄.</summary>
public sealed record RoundResult(GitOutcome Outcome, string? Reason, string? Number, bool Unshared, string? TableCommit, string? Line, ImageBuild? Build)
{
    public bool Ok => Outcome == GitOutcome.Ok;
}

/// <summary>
/// 회차 내기(사양 7.5절): 작업 폴더 깨끗 → .gitignore·작성자 확인 → 개발·현장 빌드 → release/&lt;사이트&gt;/ 복사 → 이전 회차 git rm → README 회차 줄 머리 → 커밋.
/// 번호 = 그 폴더 가장 큰 번호 + 1. 받기·연결을 기다리지 않는다. 화면은 팝업 확인 뒤에 부른다.
/// 복사 뒤에 실패하면 release/&lt;사이트&gt; 를 전 상태로 되돌린다(이전 회차·README 복구, 새 파일 삭제).
/// </summary>
public static class Round
{
    /// <summary>기본 빌드: SitePack(7.2절 3·3b) → SiteBuild 개발 → SiteBuild 현장.</summary>
    public static ImageBuilder DefaultBuilder(string? gccPath = null) => async (app, site, onLine, ct) =>
    {
        var pk = await SitePack.RunAsync(app, site, gccPath, onLine, ct);
        if (!pk.Ok) return new(false, null, null, $"압축 · {pk.Step} · {pk.Reason}", []);
        var dev = await SiteBuild.RunAsync(app, site, null, false, onLine, ct);
        if (!dev.Ok) return new(false, null, null, "개발 빌드 · " + dev.Reason, dev.Errors);
        var field = await SiteBuild.RunAsync(app, site, null, true, onLine, ct);
        if (!field.Ok) return new(false, dev.OutPath, null, "현장 빌드 · " + field.Reason, field.Errors);
        return new(true, dev.OutPath, field.OutPath, null, []);
    };

    public static async Task<RoundResult> RunAsync(Git git, string site, ImageBuilder? build = null, Action<OutputLine>? onLine = null, CancellationToken ct = default)
    {
        static RoundResult Fail(GitOutcome o, string? why, ImageBuild? b = null) => new(o, why, null, false, null, null, b);
        if (await git.WriteGuardAsync(ct) is { } no) return Fail(no.Outcome, no.Reason);
        var clean = await git.RequireCleanAsync(site, ct);
        if (!clean.Ok) return Fail(clean.Outcome, clean.Reason + (clean.Files.Count > 0 ? ": " + string.Join(", ", clean.Files) : ""));

        var (head, unshared) = await git.TableCommitAsync(ct);
        if (head == null) return Fail(GitOutcome.Failed, "HEAD 없음");

        var image = Git.Image(site);
        var dir = Path.Combine(git.Root, "release", site);
        var existed = Directory.Exists(dir) ? Directory.GetFiles(dir).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        int max = existed.Select(f => Git.RoundOf(f, image)).OfType<(string, int Number)>().Select(x => x.Number).DefaultIfEmpty(0).Max();
        var nn = (max + 1).ToString("D2");

        // 빌드·복사 전에 커밋이 될 조건을 본다: 여기서 걸리면 아무것도 안 만든다
        if (await git.RoundReadyAsync(site, nn, ct) is { } notReady) return Fail(notReady.Outcome, notReady.Reason);

        var b = await (build ?? DefaultBuilder())(git.Root, site, onLine, ct);
        if (!b.Ok || b.DevOut == null || b.FieldOut == null) return Fail(GitOutcome.Failed, b.Reason ?? "빌드 결과 없음", b);
        // 빌드가 생성물을 바꿨으면 이미지가 커밋과 맞지 않는다
        var after = await git.RequireCleanAsync(site, ct);
        if (!after.Ok) return Fail(GitOutcome.Dirty, "빌드 뒤 표가 커밋과 다르다(먼저 저장): " + string.Join(", ", after.Files), b);

        // 여기부터 실패하면 release/<사이트> 를 전 상태로: 이전 회차·README 복구, 새로 생긴 파일 삭제
        async Task<RoundResult> Undo(GitOutcome o, string? why)
        {
            var back = await git.RestoreRoundAsync(site, ct);
            if (!back.Ok) why = $"{why} · 원상 실패: {back.Reason}";
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir))
                        if (!existed.Contains(f)) File.Delete(f);
                    if (existed.Count == 0 && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { why = $"{why} · 새 파일 못 지움: {e.Message}"; }
            return Fail(o, why, b);
        }

        string line;
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var (src, tag) in new[] { (b.DevOut, ""), (b.FieldOut, "_field") })
            {
                File.Copy(src, Path.Combine(dir, $"{image}_{nn}{tag}.out"), overwrite: true);
                var map = Path.ChangeExtension(src, ".map");
                if (File.Exists(map)) File.Copy(map, Path.Combine(dir, $"{image}_{nn}{tag}.map"), overwrite: true);
            }
            var rm = await git.RemoveRoundsExceptAsync(site, max + 1, ct);
            if (!rm.Ok) return await Undo(rm.Outcome, rm.Reason);
            line = $"{nn}차 이미지(core {CoreVersion(git.Root) ?? "?"}, 표 {head}{(unshared ? ", 미공유" : "")}, `{image}_{nn}.out`, 현장 배포 `{image}_{nn}_field.out`):";
            Git.InsertRoundLine(Path.Combine(dir, "README.md"), line);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return await Undo(GitOutcome.Failed, "복사 실패: " + e.Message); }

        var c = await git.CommitRoundAsync(site, nn, $"{nn}차 이미지: core {CoreVersion(git.Root) ?? "?"} 묶음, {image}_{nn}(개발·현장)", ct);
        if (!c.Ok) return await Undo(c.Outcome, c.Reason);

        // 깃이 모르는 이전 회차 파일도 지운다(폴더에는 가장 큰 번호만). 커밋한 뒤라 실패해도 회차는 난 것이다
        try
        {
            foreach (var f in Directory.GetFiles(dir))
                if (Git.RoundOf(f, image) is { } r && r.Number != max + 1) File.Delete(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* 남은 옛 파일: 다음 회차가 지운다 */ }
        return new(GitOutcome.Ok, null, nn, unshared, head, line, b);
    }

    /// <summary>core/VERSION 첫 칸. 없으면 null.</summary>
    public static string? CoreVersion(string appRoot)
    {
        var f = Path.Combine(appRoot, "core", "VERSION");
        if (!File.Exists(f)) return null;
        var first = File.ReadAllText(f).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first;
    }
}
