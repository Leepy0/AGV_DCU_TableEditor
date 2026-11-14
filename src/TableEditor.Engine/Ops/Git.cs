using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace TableEditor.Engine.Ops;

/// <summary>깃 동작 결과의 갈래. Offline 은 팝업 없이 "대기 n" 만 보인다(사양 8.3절).</summary>
public enum GitOutcome { Ok, NoChange, Dirty, Offline, Login, Conflict, Refused, Failed }

/// <summary>
/// 깃 동작 하나의 결과. Files = 충돌·바뀐 파일, HeadMoved = 받기·되돌리기로 표가 바뀜(호출자가 다시 읽기 → 검사 → 압축 → <see cref="Git.CommitPackedAsync"/>),
/// Notes = 회차 번호 올림 같은 알림, Pending = 올리지 못한 커밋 수, Value = 보관 가지 이름 같은 값.
/// </summary>
public sealed record GitResult(GitOutcome Outcome, string? Reason = null)
{
    public IReadOnlyList<string> Files { get; init; } = [];
    public bool HeadMoved { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    public int? Pending { get; init; }
    public string? Value { get; init; }
    public bool Ok => Outcome is GitOutcome.Ok or GitOutcome.NoChange;
    /// <summary>팝업을 띄울 결과. 성공과 오프라인(대기)은 팝업이 없다.</summary>
    public bool Popup => !Ok && Outcome != GitOutcome.Offline;
}

/// <summary>git status --porcelain=v1 -b. Changed = "XY 경로" 줄 그대로.</summary>
public sealed record GitStatus(string? Branch, bool HasUpstream, int Ahead, int Behind, IReadOnlyList<string> Changed)
{
    public bool Clean => Changed.Count == 0;
}

/// <summary>이력 한 줄(사양 8.2절 이력).</summary>
public sealed record HistoryEntry(string Hash, string Date, string Author, string Subject);

/// <summary>
/// 사양 8절 깃 동작. 동작마다 공개 함수 하나이고, git 인자는 이 클래스 안의 상수와 검사한 값(사이트·해시·파일 이름·사유)으로만 만든다.
/// 자유 인자를 받는 공개 함수가 없다. 강제 올리기 인자를 만드는 코드가 없다(8.5절).
/// 모든 명령과 종료 코드를 &lt;루트&gt;/build/table_editor/git.log 에 남긴다.
/// </summary>
public sealed partial class Git
{
    public const string Branch = "master";
    public const string LogRelative = "build/table_editor/git.log";
    static readonly TimeSpan NetTimeout = TimeSpan.FromSeconds(60);
    static readonly TimeSpan LocalTimeout = TimeSpan.FromMinutes(5);
    static readonly object LogGate = new();

    // \z·[0-9]: "$" 는 끝 줄바꿈 앞에서도 맞고 "\d" 는 전각 숫자도 받는다
    static readonly Regex SiteName = new(@"^T[0-9]{3}_[A-Z0-9_]+\z");
    static readonly Regex HashText = new(@"^[0-9a-f]{4,40}\z");
    static readonly Regex FileName = new(@"^[A-Za-z0-9_]+\.(c|h)\z");

    // 영어 메시지로 8.3절 판정을 한다. 터미널 로그인 창은 띄우지 않는다(Credential Manager 만)
    static readonly IReadOnlyDictionary<string, string?> Env = new Dictionary<string, string?>
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_MERGE_AUTOEDIT"] = "no",
        ["LC_ALL"] = "C",
        ["LANGUAGE"] = "C",
    };

    public string Root { get; }
    /// <summary>설정의 앱 주소. push 전 주소 확인에 쓴다(8.5절).</summary>
    public string AppUrl { get; }
    public string LogPath { get; }

    public Git(string root, string appUrl)
    {
        Root = Path.GetFullPath(root);
        AppUrl = appUrl;
        LogPath = Path.Combine(Root, "build", "table_editor", "git.log");
    }

    // ---- 실행·기록 ----

    internal sealed record Run(int Code, IReadOnlyList<string> Out, IReadOnlyList<string> Err, string? Why)
    {
        public bool Ok => Code == 0 && Why == null;
        public string All => string.Join("\n", Err.Concat(Out));
    }

    /// <summary>git -C &lt;루트&gt; --no-pager &lt;args&gt;. args 는 이 클래스 안에서만 만든다.</summary>
    async Task<Run> GitAsync(bool net, CancellationToken ct, params string[] args)
    {
        string[] full = ["-C", Root, "--no-pager", .. args];
        var r = await ProcessRunner.RunAsync(new ProcessSpec("git", full, Root, net ? NetTimeout : LocalTimeout, ProcessRunner.Utf8, Env), null, ct);
        var why = r.StartError ?? (r.TimedOut ? "시간 초과" : r.Cancelled ? "취소됨" : null);
        WriteLog(LogPath, r.TimedOut ? "timeout" : r.ExitCode.ToString(), full);
        return new Run(r.ExitCode, r.Lines.Where(l => !l.IsError).Select(l => l.Text).ToList(),
            r.Lines.Where(l => l.IsError).Select(l => l.Text).ToList(), why);
    }

    /// <summary>기록 줄: 시각 TAB 종료 코드 TAB git TAB 인자…(탭으로 갈라 정확히 되읽는다). 표시 줄은 "#" 로 시작.</summary>
    static void WriteLog(string logPath, string code, IEnumerable<string> args) =>
        AppendLog(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{code}\tgit\t{string.Join("\t", args)}");

    internal void Note(string text) => AppendLog(LogPath, $"# {text}");

    static void AppendLog(string logPath, string line)
    {
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath, line.Replace('\n', ' ').Replace('\r', ' ') + "\n", ProcessRunner.Utf8);
            }
        }
        catch (IOException) { } // 기록 실패가 동작을 막지 않는다
        catch (UnauthorizedAccessException) { }
    }

    static GitResult Fail(Run r, string what) => new(GitOutcome.Failed, $"{what}: {r.Why ?? Last(r)}");
    static string Last(Run r) => r.Err.Concat(r.Out).LastOrDefault(t => t.Trim().Length > 0)?.Trim() ?? $"종료 코드 {r.Code}";

    /// <summary>
    /// 8.3절 판정: 원격 앞섬 / 로그인 필요 / 원격이 거절(실패, 팝업) / 그 밖(망 막힘·오프라인, 팝업 없음).
    /// 사양 표보다 넓다: 로그인 창을 못 띄운 것·열쇠 거절도 로그인으로, 원격 훅 거절·없는 저장소는 실패로 본다
    /// (오프라인으로 두면 팝업 없이 대기만 쌓인다). 403 은 사양대로 오프라인이다(사내망 push 막힘).
    /// </summary>
    internal static GitOutcome Classify(Run r)
    {
        var t = r.All;
        if (t.Contains("[rejected]") || t.Contains("fetch first") || t.Contains("non-fast-forward")) return GitOutcome.Conflict;
        if (t.Contains("Authentication failed") || t.Contains("could not read Username") || t.Contains("could not read Password")
            || t.Contains("terminal prompts disabled") || t.Contains("Permission denied (")) return GitOutcome.Login; // "(publickey)" 꼴만: 로컬 파일 오류의 Permission denied 는 로그인이 아니다
        if (t.Contains("remote rejected") || t.Contains("Repository not found")) return GitOutcome.Failed;
        return GitOutcome.Offline;
    }

    /// <summary>망 명령 실패의 결과 갈래: 로그인·실패는 그대로(팝업), 나머지는 오프라인.</summary>
    static GitOutcome NetOutcome(Run r)
    {
        var k = Classify(r);
        return k is GitOutcome.Login or GitOutcome.Failed ? k : GitOutcome.Offline;
    }

    /// <summary>
    /// 상태 줄("XY 경로")의 경로. 공백·특수 글자가 든 경로는 git 이 큰따옴표로 감싸 준다: 벗긴다. 이름 바꾸기 줄은 새 이름.
    /// </summary>
    internal static string StatusPath(string line)
    {
        var p = line.Length > 3 ? line[3..] : "";
        if (line.Length > 0 && line[0] is 'R' or 'C')
        {
            int arrow = p.LastIndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) p = p[(arrow + 4)..];
        }
        if (p.Length < 2 || p[0] != '"' || p[^1] != '"') return p;
        var sb = new StringBuilder();
        for (int i = 1; i < p.Length - 1; i++)
        {
            if (p[i] != '\\' || i + 1 >= p.Length - 1) { sb.Append(p[i]); continue; }
            char c = p[++i];
            sb.Append(c switch { 't' => '\t', 'n' => '\n', _ => c });
        }
        return sb.ToString();
    }

    /// <summary>
    /// 8.5절: 쓰기 전에 본다. Root 가 저장소 맨 위가 아니거나 origin 이 코어 저장소면 Refused(코어를 받은 폴더에서는 어떤 쓰기도 하지 않는다).
    /// branch 이면 지금 가지가 master 가 아닐 때도 Refused(8.1절 가지는 master 하나). 통과면 null.
    /// </summary>
    internal async Task<GitResult?> WriteGuardAsync(CancellationToken ct, bool branch = true)
    {
        var top = await GitAsync(false, ct, "rev-parse", "--show-prefix");
        if (!top.Ok) return new(GitOutcome.Failed, "git 없음 또는 저장소 아님: " + Root);
        if (top.Out.Any(l => l.Trim().Length > 0)) return new(GitOutcome.Refused, "저장소 맨 위 폴더가 아님: " + Root);
        var url = await GitAsync(false, ct, "remote", "get-url", "--all", "origin");
        if (url.Ok && url.Out.Any(IsCoreUrl)) return new(GitOutcome.Refused, "코어 저장소를 받은 폴더에서는 쓰지 않는다");
        if (branch)
        {
            var b = await GitAsync(false, ct, "symbolic-ref", "--short", "-q", "HEAD");
            var name = b.Out.FirstOrDefault()?.Trim() ?? "";
            if (name != Branch) return new(GitOutcome.Refused, $"가지가 {Branch} 가 아님: {(name.Length > 0 ? name : "가지 없음")}");
        }
        return null;
    }

    static bool IsCoreUrl(string url) =>
        url.Contains("AGV_DCU_Core", StringComparison.OrdinalIgnoreCase) || string.Equals(NormUrl(url), NormUrl(CoreUpdate.CoreUrl), StringComparison.OrdinalIgnoreCase);

    static string? CheckSite(string site) => SiteName.IsMatch(site) ? null : "사이트 이름이 잘못됨: " + site;
    static string SitePath(string site) => $"site/{site}";
    static string ReleasePath(string site) => $"release/{site}";

    /// <summary>한 줄 값: 비어 있지 않고 제어 문자가 없다. 앞이 '-' 이면 안 된다(옵션으로 읽히지 않게).</summary>
    static bool OneLine(string s) => s.Trim().Length > 0 && !s.Any(char.IsControl) && !s.TrimStart().StartsWith('-');

    // ---- 8.2 동작 ----

    /// <summary>처음 받기: git clone -- &lt;앱 주소&gt; &lt;폴더&gt;. 폴더는 없거나 비어 있어야 한다. 실패 = 주소·로그인·망 팝업.</summary>
    public static async Task<GitResult> CloneAsync(string appUrl, string folder, CancellationToken ct = default)
    {
        if (!OneLine(appUrl)) return new(GitOutcome.Failed, "앱 주소가 잘못됨");
        folder = Path.GetFullPath(folder);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()) return new(GitOutcome.Failed, "폴더가 비어 있지 않음: " + folder);
        string[] args = ["--no-pager", "clone", "--", appUrl, folder];
        var r = await ProcessRunner.RunAsync(new ProcessSpec("git", args, null, NetTimeout, ProcessRunner.Utf8, Env), null, ct);
        if (Directory.Exists(folder)) WriteLog(Path.Combine(folder, "build", "table_editor", "git.log"), r.TimedOut ? "timeout" : r.ExitCode.ToString(), args);
        if (r.Ok) return new(GitOutcome.Ok);
        var run = new Run(r.ExitCode, [], r.Lines.Select(l => l.Text).ToList(), r.StartError);
        var kind = r.StartError != null ? GitOutcome.Failed : Classify(run);
        return new(kind == GitOutcome.Conflict ? GitOutcome.Failed : kind, Last(run));
    }

    /// <summary>상태: git status --porcelain=v1 -b. git 이 없거나 저장소가 아니면 null.</summary>
    public async Task<GitStatus?> StatusAsync(CancellationToken ct = default)
    {
        var r = await GitAsync(false, ct, "-c", "core.quotepath=off", "status", "--porcelain=v1", "-b");
        if (!r.Ok) return null;
        string? branch = null;
        bool up = false;
        int ahead = 0, behind = 0;
        var changed = new List<string>();
        foreach (var line in r.Out)
        {
            if (!line.StartsWith("## ")) { if (line.Length > 0) changed.Add(line); continue; }
            var m = Regex.Match(line, @"^## (?:No commits yet on )?([^ .]+(?:\.[^ .]+)*?)(?:\.\.\.(\S+))?(?: \[(.*)\])?$");
            if (!m.Success) continue;
            branch = m.Groups[1].Value;
            up = m.Groups[2].Success;
            var a = Regex.Match(m.Groups[3].Value, @"ahead (\d+)");
            var b = Regex.Match(m.Groups[3].Value, @"behind (\d+)");
            if (a.Success) ahead = int.Parse(a.Groups[1].Value);
            if (b.Success) behind = int.Parse(b.Groups[1].Value);
        }
        return new GitStatus(branch, up, ahead, behind, changed);
    }

    /// <summary>연결 확인: git ls-remote --heads origin. Ok / Offline / Login / Failed(없는 저장소 등).</summary>
    public async Task<GitResult> CheckOnlineAsync(CancellationToken ct = default)
    {
        var r = await GitAsync(true, ct, "ls-remote", "--heads", "origin");
        return r.Ok ? new(GitOutcome.Ok) : new(NetOutcome(r), Last(r));
    }

    /// <summary>대기 수: git rev-list --count origin/master..HEAD. 원격 가지를 모르면 null.</summary>
    public async Task<int?> PendingAsync(CancellationToken ct = default)
    {
        var r = await GitAsync(false, ct, "rev-list", "--count", $"origin/{Branch}..HEAD");
        return r.Ok && int.TryParse(r.Out.FirstOrDefault(), out var n) ? n : null;
    }

    /// <summary>작업 폴더가 깨끗한가(8.4절 1, 7.5절). 깃이 아는 파일의 변경, 사이트·회차 폴더의 새 파일이 있으면 Dirty.</summary>
    internal async Task<GitResult> RequireCleanAsync(string site, CancellationToken ct, params string[] alsoUntracked)
    {
        var st = await StatusAsync(ct);
        if (st == null) return new(GitOutcome.Failed, "git status 실패(git 없음 또는 저장소 아님)");
        string[] watch = [SitePath(site) + "/", ReleasePath(site) + "/", .. alsoUntracked];
        var dirty = st.Changed.Where(l => !l.StartsWith("??") || watch.Any(w => StatusPath(l).StartsWith(w, StringComparison.Ordinal))).ToList();
        return dirty.Count == 0 ? new(GitOutcome.Ok) : new(GitOutcome.Dirty, "저장 안 한 변경") { Files = dirty.Select(StatusPath).ToList() };
    }

    /// <summary>저장(8.2절): git add -- site/&lt;사이트&gt; → git commit "&lt;사이트&gt;: &lt;사유&gt;". 사유 한 줄 필수. 바뀐 것이 없으면 NoChange.</summary>
    public async Task<GitResult> SaveAsync(string site, string reason, CancellationToken ct = default)
    {
        if (CheckSite(site) is { } bad) return new(GitOutcome.Failed, bad);
        if (!OneLine(reason)) return new(GitOutcome.Failed, "사유 한 줄이 필요");
        if (await WriteGuardAsync(ct) is { } no) return no;
        Note($"save {site}");
        return await CommitPathsAsync($"{site}: {reason.Trim()}", ct, SitePath(site));
    }

    /// <summary>받은 뒤·되돌린 뒤 압축 표를 다시 만들었으면: 생성물만 커밋("압축 표 다시 만듦"). 바뀐 것이 없으면 NoChange.</summary>
    public async Task<GitResult> CommitPackedAsync(string site, CancellationToken ct = default)
    {
        if (CheckSite(site) is { } bad) return new(GitOutcome.Failed, bad);
        if (await WriteGuardAsync(ct) is { } no) return no;
        Note($"repack {site}");
        return await CommitPathsAsync("압축 표 다시 만듦", ct, await TrackedOrExisting(PackedPaths(site), ct));
    }

    static string[] PackedPaths(string site) => [$"{SitePath(site)}/tag_packed.c", $"{SitePath(site)}/seq_packed.c"];

    /// <summary>있는 파일이나 깃이 아는 파일만(없는 경로를 add 하면 git 이 실패한다).</summary>
    async Task<string[]> TrackedOrExisting(string[] paths, CancellationToken ct)
    {
        var tracked = (await GitAsync(false, ct, ["ls-files", "--", .. paths])).Out.ToHashSet();
        return paths.Where(p => tracked.Contains(p) || File.Exists(Path.Combine(Root, p))).ToArray();
    }

    /// <summary>add -- 경로 → 바뀐 것이 있으면 commit --message=… -- 경로(다른 곳에 올려 둔 변경은 넣지 않는다).</summary>
    async Task<GitResult> CommitPathsAsync(string message, CancellationToken ct, params string[] paths)
    {
        if (paths.Length == 0) return new(GitOutcome.NoChange);
        var add = await GitAsync(false, ct, ["add", "--", .. paths]);
        if (!add.Ok) return Fail(add, "git add");
        var diff = await GitAsync(false, ct, ["diff", "--cached", "--quiet", "--", .. paths]);
        if (diff.Code == 0 && diff.Why == null) return new(GitOutcome.NoChange);
        if (diff.Code != 1) return Fail(diff, "git diff");
        var c = await GitAsync(false, ct, ["commit", "--quiet", "--message=" + message, "--", .. paths]);
        if (!c.Ok) return Fail(c, "git commit");
        return new(GitOutcome.Ok) { Value = await HeadShortAsync(ct) };
    }

    internal async Task<string?> HeadShortAsync(CancellationToken ct)
    {
        var r = await GitAsync(false, ct, "rev-parse", "--short", "HEAD");
        return r.Ok ? r.Out.FirstOrDefault()?.Trim() : null;
    }

    async Task<string?> HeadAsync(CancellationToken ct)
    {
        var r = await GitAsync(false, ct, "rev-parse", "HEAD");
        return r.Ok ? r.Out.FirstOrDefault()?.Trim() : null;
    }

    /// <summary>
    /// 받기(8.2절): 깨끗한지 → git fetch origin → 회차 번호 겹침이면 내 회차 번호를 올림(7.5절 1·2) →
    /// merge --ff-only → 안 되면 merge --no-edit. 충돌은 8.4절: 생성물·README 회차 줄뿐이면 풀고 커밋, 아니면 merge --abort 뒤 Conflict(Files).
    /// 합친 뒤 회차가 둘 이상이면 큰 번호만 남긴다(7.5절 4). HeadMoved 이면 호출자가 다시 읽기 → 검사 → 압축 → CommitPackedAsync.
    /// </summary>
    public async Task<GitResult> PullAsync(string site, CancellationToken ct = default)
    {
        if (CheckSite(site) is { } bad) return new(GitOutcome.Failed, bad);
        if (await WriteGuardAsync(ct) is { } no) return no;
        return await PullCheckedAsync(site, ct);
    }

    /// <summary>받기의 몸통(값·쓰기 확인을 마친 뒤).</summary>
    async Task<GitResult> PullCheckedAsync(string site, CancellationToken ct)
    {
        Note($"pull {site}");
        var clean = await RequireCleanAsync(site, ct);
        if (!clean.Ok) return clean;
        var f = await GitAsync(true, ct, "fetch", "origin");
        if (!f.Ok) return new(NetOutcome(f), Last(f)) { Pending = await PendingAsync(ct) };
        var notes = new List<string>();
        var renum = await RenumberRoundsAsync(site, notes, ct);
        if (renum != null) return renum;

        var before = await HeadAsync(ct);
        var ff = await GitAsync(false, ct, "merge", "--ff-only", "--quiet", $"origin/{Branch}");
        if (ff.Ok) return Done(await HeadAsync(ct) != before);

        var m = await GitAsync(false, ct, "-c", "merge.conflictStyle=merge", "merge", "--no-edit", "--quiet", $"origin/{Branch}");
        if (!m.Ok)
        {
            var u = await GitAsync(false, ct, "-c", "core.quotepath=off", "diff", "--name-only", "--diff-filter=U");
            var conflicts = u.Out.Where(s => s.Length > 0).ToList();
            if (conflicts.Count == 0)
            {
                await AbortMergeAsync(ct);
                return Fail(m, "git merge");
            }
            var unsolved = new List<string>();
            foreach (var file in conflicts)
            {
                if (IsPacked(file))
                {
                    var t = await GitAsync(false, ct, "checkout", "--theirs", "--", file);
                    if (!t.Ok) unsolved.Add(file);
                }
                else if (file == $"{ReleasePath(site)}/README.md" && TryCombineReadme(Path.Combine(Root, file))) { }
                else unsolved.Add(file);
            }
            if (unsolved.Count > 0)
            {
                await AbortMergeAsync(ct);
                return new(GitOutcome.Conflict, "충돌") { Files = conflicts };
            }
            var add = await GitAsync(false, ct, ["add", "--", .. conflicts]);
            var c = add.Ok ? await GitAsync(false, ct, "commit", "--no-edit", "--quiet") : add;
            if (!c.Ok)
            {
                await AbortMergeAsync(ct);
                return Fail(c, "충돌 푼 뒤 커밋");
            }
            notes.Add("충돌 풂: " + string.Join(", ", conflicts));
        }
        var tidy = await PruneRoundsAsync(site, notes, ct);
        if (tidy != null) return tidy;
        return Done(true);

        GitResult Done(bool moved) => new(moved || notes.Count > 0 ? GitOutcome.Ok : GitOutcome.NoChange) { HeadMoved = moved, Notes = notes };
    }

    static bool IsPacked(string path) => Regex.IsMatch(path, @"^site/[^/]+/(tag|seq)_packed\.c$");

    async Task AbortMergeAsync(CancellationToken ct)
    {
        if (await InProgressAsync("MERGE_HEAD", ct)) await GitAsync(false, ct, "merge", "--abort");
    }

    /// <summary>
    /// 진행 중 표지 파일(MERGE_HEAD, REVERT_HEAD)이 있는가. git 에게 자리를 묻는다:
    /// git worktree 에서는 <c>.git</c> 이 파일이고 표지는 <c>.git/worktrees/&lt;이름&gt;/</c> 에 있다.
    /// </summary>
    async Task<bool> InProgressAsync(string head, CancellationToken ct)
    {
        var r = await GitAsync(false, ct, "rev-parse", "--git-path", head);
        var p = r.Ok ? r.Out.FirstOrDefault()?.Trim() : null;
        return !string.IsNullOrEmpty(p) && File.Exists(Path.GetFullPath(p, Root));
    }

    /// <summary>
    /// 8.4절 5(가) 내 것 보관하고 원격 받기: git branch keep/&lt;날짜시각&gt; → git reset --hard origin/master.
    /// 화면이 팝업 확인을 받은 뒤에만 부른다. 작업 폴더가 깨끗하지 않으면 하지 않는다. Value = 보관 가지 이름.
    /// 깃이 모르는 내 파일(무시되는 파일 포함) 가운데 원격에 같은 경로가 있으면 reset 이 그것을 덮는다. 보관 가지에도 없다: Dirty(Files)로 막는다.
    /// </summary>
    public async Task<GitResult> KeepMineAndResetAsync(CancellationToken ct = default)
    {
        if (await WriteGuardAsync(ct) is { } no) return no;
        Note("keep-and-reset");
        var st = await StatusAsync(ct);
        if (st == null) return new(GitOutcome.Failed, "git status 실패");
        var tracked = st.Changed.Where(l => !l.StartsWith("??")).ToList();
        if (tracked.Count > 0) return new(GitOutcome.Dirty, "저장 안 한 변경") { Files = tracked.Select(StatusPath).ToList() };
        var mine = await GitAsync(false, ct, "-c", "core.quotepath=off", "ls-files", "--others");
        if (!mine.Ok) return Fail(mine, "git ls-files");
        var theirs = await GitAsync(false, ct, "-c", "core.quotepath=off", "ls-tree", "-r", "--name-only", $"origin/{Branch}");
        if (!theirs.Ok) return Fail(theirs, "원격 가지");
        var remote = theirs.Out.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clash = mine.Out.Where(remote.Contains).ToList();
        if (clash.Count > 0) return new(GitOutcome.Dirty, "깃이 모르는 파일이 원격에도 있음") { Files = clash };
        var name = $"keep/{DateTime.Now:yyyyMMdd-HHmmss}";
        var b = await GitAsync(false, ct, "branch", name);
        if (!b.Ok) return Fail(b, "보관 가지");
        var r = await GitAsync(false, ct, "reset", "--hard", "--quiet", $"origin/{Branch}");
        if (!r.Ok) return Fail(r, "git reset");
        return new(GitOutcome.Ok) { Value = name, HeadMoved = true };
    }

    /// <summary>
    /// 올리기(8.2·8.3·8.5절): push 주소 확인 → 받기 → git push origin master. 원격이 앞서면 받기 뒤 한 번 다시.
    /// 망 막힘·오프라인은 Offline(팝업 없음, Pending). 주소가 설정과 다르거나 코어 저장소면 Refused(push 안 함).
    /// 받기에서 표가 바뀌면(HeadMoved) push 하지 않고 돌려준다(Ok, HeadMoved, Pending = 올릴 커밋 수):
    /// 호출자가 다시 읽기 → 검사 → 압축 → <see cref="CommitPackedAsync"/> 를 한 뒤 다시 부른다(8.4절 3. 낡은 생성물을 올리지 않는다).
    /// </summary>
    public async Task<GitResult> PushAsync(string site, CancellationToken ct = default)
    {
        if (CheckSite(site) is { } bad) return new(GitOutcome.Failed, bad);
        if (await WriteGuardAsync(ct) is { } no) return no;
        Note($"push {site}");
        var urls = await PushUrlsAsync(ct);
        Note($"push-url {string.Join(" ", urls)}");
        // push 주소가 여럿이면 git 은 모두에 올린다: 하나라도 다르면 올리지 않는다
        string? why = urls.Count == 0 ? "push 주소 없음" : null;
        foreach (var u in urls)
            if (why == null) PushAllowed(u, AppUrl, out why);
        if (why != null) return new(GitOutcome.Refused, why) { Pending = await PendingAsync(ct) };

        var pull = await PullCheckedAsync(site, ct);
        if (!pull.Ok) return pull;
        if (pull.HeadMoved) return pull with { Outcome = GitOutcome.Ok, Pending = await PendingAsync(ct) };
        var notes = pull.Notes.ToList();
        for (int attempt = 0; ; attempt++)
        {
            var pending = await PendingAsync(ct);
            if (pending == 0) return new(GitOutcome.NoChange) { Notes = notes, Pending = 0 };
            var p = await GitAsync(true, ct, "push", "origin", Branch);
            if (p.Ok) return new(GitOutcome.Ok) { Notes = notes, Pending = await PendingAsync(ct) };
            var k = Classify(p);
            if (k == GitOutcome.Conflict && attempt == 0)
            {
                var again = await PullCheckedAsync(site, ct);
                if (!again.Ok) return again;
                notes.AddRange(again.Notes);
                if (again.HeadMoved) return new(GitOutcome.Ok) { HeadMoved = true, Notes = notes, Pending = await PendingAsync(ct) };
                continue;
            }
            var outcome = k == GitOutcome.Conflict ? GitOutcome.Failed : k;
            return new(outcome, Last(p)) { Notes = notes, Pending = await PendingAsync(ct) };
        }
    }

    /// <summary>origin 의 push 주소 모두(<c>remote.origin.pushurl</c> 이 여럿일 수 있다).</summary>
    async Task<IReadOnlyList<string>> PushUrlsAsync(CancellationToken ct)
    {
        var r = await GitAsync(false, ct, "remote", "get-url", "--push", "--all", "origin");
        return r.Ok ? r.Out.Select(l => l.Trim()).Where(l => l.Length > 0).ToList() : [];
    }

    /// <summary>8.5절: push 주소가 설정의 앱 주소와 같고 코어 저장소가 아닐 때만.</summary>
    internal static bool PushAllowed(string? pushUrl, string appUrl, out string? why)
    {
        why = null;
        if (string.IsNullOrWhiteSpace(pushUrl)) why = "push 주소 없음";
        else if (pushUrl.Contains("AGV_DCU_Core", StringComparison.OrdinalIgnoreCase) || appUrl.Contains("AGV_DCU_Core", StringComparison.OrdinalIgnoreCase)) why = "코어 저장소로는 올리지 않는다";
        else if (!string.Equals(NormUrl(pushUrl), NormUrl(appUrl), StringComparison.OrdinalIgnoreCase)) why = "push 주소가 설정의 앱 주소와 다르다";
        return why == null;
    }

    static string NormUrl(string u) => u.Trim().Replace('\\', '/').TrimEnd('/');

    /// <summary>이력: git log -n 100 -- site/&lt;사이트&gt; release/&lt;사이트&gt;.</summary>
    public async Task<IReadOnlyList<HistoryEntry>> HistoryAsync(string site, CancellationToken ct = default)
    {
        if (CheckSite(site) != null) return [];
        var r = await GitAsync(false, ct, "log", "--date=iso", "--format=%h%x09%ad%x09%an%x09%s", "-n", "100", "--", SitePath(site), ReleasePath(site));
        if (!r.Ok) return [];
        return r.Out.Select(l => l.Split('\t', 4)).Where(p => p.Length == 4).Select(p => new HistoryEntry(p[0], p[1], p[2], p[3])).ToList();
    }

    /// <summary>옛 표 보기: git show &lt;해시&gt;:site/&lt;사이트&gt;/&lt;파일&gt; 의 바이트(줄 끝 그대로). 없으면 null. 읽기 전용 화면용.</summary>
    public async Task<byte[]?> ShowAsync(string hash, string site, string file, CancellationToken ct = default)
    {
        if (!HashText.IsMatch(hash) || CheckSite(site) != null || !FileName.IsMatch(file)) return null;
        string[] full = ["-C", Root, "--no-pager", "show", $"{hash}:{SitePath(site)}/{file}"];
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in full) psi.ArgumentList.Add(a);
        foreach (var (k, v) in Env) psi.Environment[k] = v;
        try
        {
            using var p = Process.Start(psi)!;
            using var ms = new MemoryStream();
            var err = p.StandardError.ReadToEndAsync(ct);
            await p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
            await err;
            await p.WaitForExitAsync(ct);
            WriteLog(LogPath, p.ExitCode.ToString(), full);
            return p.ExitCode == 0 ? ms.ToArray() : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            WriteLog(LogPath, "start-error", full);
            return null;
        }
    }

    /// <summary>되돌리기(커밋 하나): git revert --no-edit &lt;해시&gt;. 충돌이면 revert --abort 뒤 Conflict. 화면은 팝업 확인 뒤에 부른다.</summary>
    public async Task<GitResult> RevertAsync(string hash, CancellationToken ct = default)
    {
        if (!HashText.IsMatch(hash)) return new(GitOutcome.Failed, "해시가 잘못됨");
        if (await WriteGuardAsync(ct) is { } no) return no;
        Note($"revert {hash}");
        var st = await StatusAsync(ct);
        if (st == null) return new(GitOutcome.Failed, "git status 실패");
        if (st.Changed.Any(l => !l.StartsWith("??"))) return new(GitOutcome.Dirty, "저장 안 한 변경");
        var r = await GitAsync(false, ct, "revert", "--no-edit", hash);
        if (r.Ok) return new(GitOutcome.Ok) { HeadMoved = true, Value = await HeadShortAsync(ct) };
        var u = await GitAsync(false, ct, "-c", "core.quotepath=off", "diff", "--name-only", "--diff-filter=U");
        if (await InProgressAsync("REVERT_HEAD", ct)) await GitAsync(false, ct, "revert", "--abort");
        var files = u.Out.Where(s => s.Length > 0).ToList();
        return files.Count > 0 ? new(GitOutcome.Conflict, "충돌") { Files = files } : Fail(r, "git revert");
    }

    /// <summary>버리기(저장 안 한 변경): git restore --staged --worktree -- site/&lt;사이트&gt;. 화면은 팝업 확인 뒤에 부른다.</summary>
    public async Task<GitResult> DiscardAsync(string site, CancellationToken ct = default)
    {
        if (CheckSite(site) is { } bad) return new(GitOutcome.Failed, bad);
        if (await WriteGuardAsync(ct) is { } no) return no;
        Note($"discard {site}");
        var r = await GitAsync(false, ct, "restore", "--staged", "--worktree", "--", SitePath(site));
        return r.Ok ? new(GitOutcome.Ok) : Fail(r, "git restore");
    }

    /// <summary>작성자: git config user.name·user.email. 없는 것은 null.</summary>
    public async Task<(string? Name, string? Email)> AuthorAsync(CancellationToken ct = default)
    {
        var n = await GitAsync(false, ct, "config", "user.name");
        var e = await GitAsync(false, ct, "config", "user.email");
        static string? V(Run r) => r.Ok && r.Out.FirstOrDefault()?.Trim() is { Length: > 0 } s ? s : null;
        return (V(n), V(e));
    }

    /// <summary>작성자를 저장소 로컬 설정에 쓴다(git config --local). 이름·메일 한 줄씩.</summary>
    public async Task<GitResult> SetAuthorAsync(string name, string email, CancellationToken ct = default)
    {
        if (!OneLine(name) || !OneLine(email)) return new(GitOutcome.Failed, "이름·메일이 잘못됨");
        if (await WriteGuardAsync(ct, branch: false) is { } no) return no;
        var a = await GitAsync(false, ct, "config", "--local", "user.name", name.Trim());
        var b = a.Ok ? await GitAsync(false, ct, "config", "--local", "user.email", email.Trim()) : a;
        return b.Ok ? new(GitOutcome.Ok) : Fail(b, "git config");
    }
}
