using TableEditor.Engine.Ops;

namespace TableEditor.App;

/// <summary>
/// 화면에 필요한데 엔진 <see cref="Git"/> 에 공개 함수가 없는 읽기 둘(회차 확인 팝업의 표 커밋, 이력의 대기 표시).
/// 인자는 여기 상수뿐이고 쓰기 명령은 없다. shortcut: 엔진에 같은 공개 읽기 함수가 생기면 그것으로 바꾼다.
/// </summary>
public static class GitRead
{
    static readonly IReadOnlyDictionary<string, string?> Env = new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C" };

    static async Task<IReadOnlyList<string>> Lines(string root, params string[] args)
    {
        var r = await ProcessRunner.RunAsync(new ProcessSpec("git", ["-C", root, "--no-pager", .. args], root, TimeSpan.FromSeconds(30), ProcessRunner.Utf8, Env));
        return r.Ok ? r.Lines.Where(l => !l.IsError).Select(l => l.Text.Trim()).Where(t => t.Length > 0).ToList() : [];
    }

    /// <summary>git rev-parse --short HEAD. 없으면 null.</summary>
    public static async Task<string?> HeadShortAsync(string root) => (await Lines(root, "rev-parse", "--short", "HEAD")).FirstOrDefault();

    /// <summary>아직 안 올린 커밋(git rev-list origin/master..HEAD, 긴 해시). 원격 가지를 모르면 빈 목록.</summary>
    public static Task<IReadOnlyList<string>> UnpushedAsync(string root) => Lines(root, "rev-list", $"origin/{Git.Branch}..HEAD");
}
