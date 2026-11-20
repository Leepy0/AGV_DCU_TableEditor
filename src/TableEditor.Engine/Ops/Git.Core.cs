using System.Text.RegularExpressions;

namespace TableEditor.Engine.Ops;

/// <summary>코어 버전 변경(사양 8.6절)의 깃 단계. 코어 저장소에는 읽기(ls-remote)만 한다.</summary>
public sealed partial class Git
{
    static readonly Regex DistTag = new(@"^[0-9a-f]{40}\trefs/tags/dist-(v\d+\.\d+\.\d+)$");

    /// <summary>git ls-remote --tags &lt;코어 주소&gt; 의 dist-v* (^{} 줄은 뺀다). 연결 안 되면 null.</summary>
    internal async Task<IReadOnlyList<string>?> CoreTagsAsync(string coreUrl, CancellationToken ct)
    {
        if (!OneLine(coreUrl)) return null;
        Note("core-versions");
        var r = await GitAsync(true, ct, "ls-remote", "--tags", "--", coreUrl.Trim());
        if (!r.Ok) return null;
        return r.Out.Select(l => DistTag.Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct()
            .OrderByDescending(v => Version.Parse(v[1..])).ToList();
    }

    /// <summary>저장소 없이 <c>git ls-remote --tags &lt;주소&gt;</c> 출력 줄. 못 읽으면 null.</summary>
    internal static async Task<IReadOnlyList<string>?> RemoteTagLinesAsync(string url, TimeSpan limit, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(new ProcessSpec("git", ["--no-pager", "ls-remote", "--tags", "--", url], null, limit, ProcessRunner.Utf8, Env), null, ct);
        return r.Ok ? r.Lines.Where(l => !l.IsError).Select(l => l.Text).ToList() : null;
    }

    static string[] CorePaths(string site) => ["core", .. PackedPaths(site)];

    /// <summary>8.6절 6: git add -- core 와 생성물 → 커밋 "코어 vX 받음". 바뀐 것이 없으면 NoChange.</summary>
    internal async Task<GitResult> CommitCoreAsync(string site, string version, CancellationToken ct)
    {
        Note($"core-update commit {version}");
        return await CommitPathsAsync($"코어 {version} 받음", ct, await TrackedOrExisting(CorePaths(site), ct));
    }

    /// <summary>8.6절 7: git restore --staged --worktree -- core 와 생성물, git clean -fd -- core.</summary>
    internal async Task<GitResult> RestoreCoreAsync(string site, CancellationToken ct)
    {
        Note("core-update restore");
        var tracked = (await GitAsync(false, ct, ["ls-files", "--", .. PackedPaths(site)])).Out;
        var r = await GitAsync(false, ct, ["restore", "--staged", "--worktree", "--", "core", .. tracked]);
        var c = await GitAsync(false, ct, "clean", "-fd", "--quiet", "--", "core");
        return !r.Ok ? Fail(r, "git restore") : !c.Ok ? Fail(c, "git clean") : new(GitOutcome.Ok);
    }

    /// <summary>update_core.ps1 같은 바깥 스크립트 실행을 git.log 에 남긴다(코어 변경이 코어 버전 변경 때만인지 A-11 로 본다).</summary>
    internal void NoteScript(string code, IEnumerable<string> args) => AppendLog(LogPath, $"# core-update script\t{code}\t{string.Join("\t", args)}");
}
