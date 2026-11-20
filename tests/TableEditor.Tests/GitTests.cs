using System.Text;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Ops;

/// <summary>
/// 작업 6 시험: 깃·회차·코어 버전 변경. $env:TEMP 아래에 맨(bare) 원격 하나와 작업 폴더 둘(a, b)을 만들어 쓴다.
/// 원격은 그 맨 저장소뿐이다(실제 원격에 닿지 않는다). 사이트 T900_DEMO, 이미지 agv_900 은 지어낸 것.
/// </summary>
static class GitTests
{
    const string Site = "T900_DEMO";
    static readonly UTF8Encoding Utf8 = new(false);

    sealed class Fx : IDisposable
    {
        public string Base = "", Remote = "", A = "", B = "";
        public Git Ga = null!, Gb = null!;
        public void Dispose()
        {
            foreach (var f in Directory.EnumerateFiles(Base, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            try { Directory.Delete(Base, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>시험 준비용 git(엔진 밖). 작성자·줄 끝 설정을 붙인다.</summary>
    static ProcessResult G(string dir, params string[] args) =>
        ProcessRunner.RunAsync(new ProcessSpec("git", ["-c", "user.name=t", "-c", "user.email=t@t.invalid", "-c", "core.autocrlf=false", "-C", dir, .. args],
            dir, TimeSpan.FromSeconds(60), ProcessRunner.Utf8, new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C" })).GetAwaiter().GetResult();

    static string Out(string dir, params string[] args) => string.Join("\n", G(dir, args).Lines.Where(l => !l.IsError).Select(l => l.Text)).Trim();

    static void Write(string root, string rel, string text)
    {
        var p = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, Utf8.GetBytes(text));
    }

    static string Read(string root, string rel) => File.ReadAllText(Path.Combine(root, rel), Utf8);

    static T Wait<T>(Task<T> t) => t.GetAwaiter().GetResult();

    const string Tags = "site/T900_DEMO/tag_table.c";
    const string Packed = "site/T900_DEMO/tag_packed.c";
    const string Readme = "release/T900_DEMO/README.md";

    static Fx Make()
    {
        var fx = new Fx { Base = Path.Combine(Path.GetTempPath(), "tedit_git_" + Guid.NewGuid().ToString("N")[..8]) };
        fx.Remote = Path.Combine(fx.Base, "remote.git");
        var seed = Path.Combine(fx.Base, "seed");
        Directory.CreateDirectory(seed);
        G(fx.Base, "init", "-q", "--bare", "--initial-branch=master", fx.Remote);
        G(seed, "init", "-q", "--initial-branch=master");
        Write(seed, ".gitignore", "*.out\n*.map\nbuild/\n!release/T900_DEMO/*\n");
        Write(seed, Tags, "TAG_TABLE_BEGIN( site_tags )\r\n    TAG( 1, ANY_PROFILE, AGV_STOP )\r\n    TAG( 2, ANY_PROFILE, AGV_STOP )\r\nTAG_TABLE_END\r\n");
        Write(seed, Packed, "/* 생성물 */\npacked 0\n");
        Write(seed, Readme, "# T900 데모\n\n03차 이미지(core v0.0.0, 표 0000000, `agv_900_03.out`, 현장 배포 `agv_900_03_field.out`): 처음\n");
        foreach (var s in new[] { ".out", ".map", "_field.out", "_field.map" }) Write(seed, $"release/T900_DEMO/agv_900_03{s}", "old" + s);
        var version = $"{GrammarTable.Version} 0000000 cl2000 v18.12.2.LTS\n";
        Write(seed, "core/VERSION", version);
        // V-3 이 보는 목록(줄 끝 LF 로 맞춘 SHA-256)
        Write(seed, "core/MANIFEST.sha256", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Utf8.GetBytes(version))) + "  VERSION\n");
        G(seed, "add", "--", ".");
        G(seed, "commit", "-q", "-m", "처음");
        G(seed, "push", "-q", fx.Remote, "master");
        fx.A = Path.Combine(fx.Base, "a");
        fx.B = Path.Combine(fx.Base, "b");
        foreach (var w in new[] { fx.A, fx.B })
        {
            G(fx.Base, "clone", "-q", fx.Remote, w);
            G(w, "config", "core.autocrlf", "false");
            G(w, "config", "user.name", "t");
            G(w, "config", "user.email", "t@t.invalid");
        }
        fx.Ga = new Git(fx.A, fx.Remote);
        fx.Gb = new Git(fx.B, fx.Remote);
        return fx;
    }

    /// <summary>"망을 끊는다": 맨 저장소 폴더 이름을 바꾼다.</summary>
    static void Offline(Fx fx, bool off)
    {
        var parked = fx.Remote + ".off";
        if (off) Directory.Move(fx.Remote, parked); else Directory.Move(parked, fx.Remote);
    }

    static int RemoteCount(Fx fx) => int.Parse(Out(fx.Base, "--git-dir=" + fx.Remote, "rev-list", "--count", "master"));

    /// <summary>가짜 빌드: build/c28 에 개발·현장 .out·.map 을 쓴다(내용 = 표시 글).</summary>
    static ImageBuilder Fake(string mark) => (app, site, on, ct) =>
    {
        var dir = Path.Combine(app, "build", "c28");
        Directory.CreateDirectory(dir);
        foreach (var n in new[] { "agv_900", "agv_900_field" })
        {
            File.WriteAllText(Path.Combine(dir, n + ".out"), $"{mark} {n} out");
            File.WriteAllText(Path.Combine(dir, n + ".map"), $"{mark} {n} map");
        }
        return Task.FromResult(new ImageBuild(true, Path.Combine(dir, "agv_900.out"), Path.Combine(dir, "agv_900_field.out"), null, []));
    };

    static void EditLine(string root, string from, string to) => Write(root, Tags, Read(root, Tags).Replace(from, to));

    public static void Run(Action<bool, string> check)
    {
        Basics(check);
        A8(check);
        A9(check);
        A10(check);
        Packed_(check);
        A17(check);
        A18(check);
        Core(check);
        LogRules(check);
        KeepUntracked(check);
        PushHeld(check);
        WorktreeAbort(check);
        Classify(check);
        PushUrls(check);
        Guards(check);
        Values(check);
        RoundRollback(check);
        ReadmeEol(check);
    }

    // ---- 작업 8(독립 검토)에서 찾은 결함 ----

    /// <summary>1: 보관하고 원격 받기 — 깃이 모르는 내 파일이 원격에도 있으면 덮지 않는다.</summary>
    static void KeepUntracked(Action<bool, string> check)
    {
        using var fx = Make();
        const string New = "site/T901_NEW/site.h";
        Write(fx.A, New, "남의 것\n");
        G(fx.A, "add", "--", "site/T901_NEW");
        G(fx.A, "commit", "-q", "-m", "남의 새 사이트");
        G(fx.A, "push", "-q", "origin", "master");
        Write(fx.B, New, "내 것(커밋 안 함)\n");
        G(fx.B, "fetch", "-q", "origin");
        var r = Wait(fx.Gb.KeepMineAndResetAsync());
        check(r.Outcome == GitOutcome.Dirty && r.Files.SequenceEqual([New]), $"1 깃이 모르는 파일이 원격에 있으면 Dirty ({r.Outcome} {string.Join(",", r.Files)})");
        check(Read(fx.B, New) == "내 것(커밋 안 함)\n" && Out(fx.B, "rev-parse", "HEAD") != Out(fx.B, "rev-parse", "origin/master"), "1 내 파일 그대로, reset 안 함");
        check(Out(fx.B, "branch", "--list", "keep/*") == "", "1 보관 가지도 안 만듦");
        // 원격에 없는 새 파일은 막지 않는다(그대로 남는다)
        File.Delete(Path.Combine(fx.B, New));
        Write(fx.B, "notes.txt", "x");
        var ok = Wait(fx.Gb.KeepMineAndResetAsync());
        check(ok.Ok && Read(fx.B, "notes.txt") == "x" && Read(fx.B, New) == "남의 것\n", $"1 원격에 없는 새 파일은 그대로 두고 받는다 ({ok.Outcome} {ok.Reason})");
        LogAll(check, fx);
    }

    /// <summary>6: 올리기 — 받아서 표가 바뀌면 push 하지 않고 돌려준다. 호출자가 다시 압축·커밋한 뒤 다시 올린다.</summary>
    static void PushHeld(Action<bool, string> check)
    {
        using var fx = Make();
        Write(fx.A, Packed, "/* 생성물 */\npacked A\n");
        EditLine(fx.A, "TAG( 1,", "TAG( 11,");
        Wait(fx.Ga.SaveAsync(Site, "a"));
        check(Wait(fx.Ga.PushAsync(Site)).Ok, "6 a 올리기");
        int remote = RemoteCount(fx);
        Write(fx.B, Packed, "/* 생성물 */\npacked B\n");
        Write(fx.B, "site/T900_DEMO/site.h", "/* b */\n");
        Wait(fx.Gb.SaveAsync(Site, "b"));
        var p = Wait(fx.Gb.PushAsync(Site));
        check(p.Ok && p.HeadMoved && p.Pending > 0, $"6 받아서 표가 바뀜: push 안 하고 돌려줌 ({p.Outcome} moved={p.HeadMoved} pending={p.Pending})");
        check(RemoteCount(fx) == remote, "6 원격은 그대로(낡은 생성물을 올리지 않는다)");
        check(Read(fx.B, Packed).Contains("packed A") && Read(fx.B, Tags).Contains("TAG( 11,") && File.Exists(Path.Combine(fx.B, "site/T900_DEMO/site.h")), "6 작업 폴더는 합쳐짐, 생성물은 원격 것");
        Write(fx.B, Packed, "/* 생성물 */\npacked AB\n");
        check(Wait(fx.Gb.CommitPackedAsync(Site)).Outcome == GitOutcome.Ok, "6 다시 만든 생성물 커밋");
        var q = Wait(fx.Gb.PushAsync(Site));
        check(q.Outcome == GitOutcome.Ok && q.Pending == 0 && !q.HeadMoved, $"6 다시 올리기 ({q.Outcome} {q.Reason})");
        check(Out(fx.Base, "--git-dir=" + fx.Remote, "show", "master:" + Packed).Contains("packed AB"), "6 원격의 생성물 = 다시 만든 것");
        LogAll(check, fx);
    }

    /// <summary>10: git worktree(.git 이 파일)에서도 충돌 뒤 되돌리기·병합을 취소한다.</summary>
    static void WorktreeAbort(Action<bool, string> check)
    {
        using var fx = Make();
        G(fx.A, "checkout", "-q", "-b", "park");
        var wt = Path.Combine(fx.Base, "wt");
        G(fx.A, "worktree", "add", "-q", wt, "master");
        check(File.Exists(Path.Combine(wt, ".git")), "10 준비: worktree 의 .git 은 파일");
        var g = new Git(wt, fx.Remote);
        EditLine(wt, "TAG( 1,", "TAG( 7,");
        var a = Wait(g.SaveAsync(Site, "a"));
        EditLine(wt, "TAG( 7,", "TAG( 8,");
        Wait(g.SaveAsync(Site, "b"));
        var rv = Wait(g.RevertAsync(a.Value ?? "0000"));
        check(rv.Outcome == GitOutcome.Conflict && rv.Files.SequenceEqual([Tags]), $"10 되돌리기 충돌 ({rv.Outcome} {rv.Reason})");
        check(Wait(g.StatusAsync())!.Clean && !Read(wt, Tags).Contains("<<<<<<<") && Read(wt, Tags).Contains("TAG( 8,"), "10 되돌리기 취소: 작업 폴더 그대로");

        EditLine(fx.B, "TAG( 2,", "TAG( 20,");
        Wait(fx.Gb.SaveAsync(Site, "남"));
        check(Wait(fx.Gb.PushAsync(Site)).Ok, "10 준비: 다른 PC 올리기");
        EditLine(wt, "TAG( 2,", "TAG( 21,");
        Wait(g.SaveAsync(Site, "나"));
        var pull = Wait(g.PullAsync(Site));
        check(pull.Outcome == GitOutcome.Conflict, $"10 받기 충돌 ({pull.Outcome} {pull.Reason})");
        check(Wait(g.StatusAsync())!.Clean && !Read(wt, Tags).Contains("<<<<<<<") && Read(wt, Tags).Contains("TAG( 21,"), "10 병합 취소: 작업 폴더 그대로");
        var bad = Git.CheckLog(File.ReadAllLines(g.LogPath));
        check(bad.Count == 0, "10 git.log 어김: " + string.Join(" | ", bad));
    }

    /// <summary>13: 거절·로그인 실패는 오프라인(팝업 없음)이 아니다.</summary>
    static void Classify(Action<bool, string> check)
    {
        static GitOutcome C(params string[] err) => Git.Classify(new Git.Run(1, [], err, null));
        check(C(" ! [remote rejected] master -> master (pre-receive hook declined)") == GitOutcome.Failed, "13 remote rejected = 실패");
        check(C("remote: Repository not found.", "fatal: repository 'https://example.invalid/a/b.git/' not found") == GitOutcome.Failed, "13 Repository not found = 실패");
        check(C("fatal: could not read Username for 'https://example.invalid': terminal prompts disabled") == GitOutcome.Login, "13 could not read Username = 로그인");
        check(C("fatal: terminal prompts disabled") == GitOutcome.Login, "13 terminal prompts disabled = 로그인");
        check(C("git@example.invalid: Permission denied (publickey).") == GitOutcome.Login, "13 Permission denied = 로그인");
        check(C("fatal: Authentication failed for 'https://example.invalid/a/b.git/'") == GitOutcome.Login, "13 Authentication failed = 로그인");
        check(C(" ! [rejected]        master -> master (fetch first)") == GitOutcome.Conflict, "13 원격 앞섬");
        check(C("fatal: unable to access 'https://example.invalid/': Could not resolve host: example.invalid") == GitOutcome.Offline, "13 망 막힘 = 오프라인");
        check(C("remote: Permission to a/b.git denied to c.", "fatal: unable to access 'https://example.invalid/a/b.git/': The requested URL returned error: 403") == GitOutcome.Offline,
            "13 403(사내망 push 막힘) = 오프라인");

        using var fx = Make();
        Write(fx.Remote, "hooks/pre-receive", "#!/bin/sh\necho 'policy: push denied' >&2\nexit 1\n");
        EditLine(fx.A, "TAG( 1,", "TAG( 13,");
        Wait(fx.Ga.SaveAsync(Site, "거절될 것"));
        var p = Wait(fx.Ga.PushAsync(Site));
        check(p.Outcome == GitOutcome.Failed && p.Popup && p.Pending == 1, $"13 원격 훅이 거절한 push = 실패(팝업), 대기 1 ({p.Outcome} {p.Reason})");
        LogAll(check, fx);
    }

    /// <summary>15: push 주소가 여럿이면 모두 설정의 앱 주소여야 한다.</summary>
    static void PushUrls(Action<bool, string> check)
    {
        using var fx = Make();
        var other = Path.Combine(fx.Base, "other.git");
        G(fx.Base, "init", "-q", "--bare", "--initial-branch=master", other);
        G(fx.A, "remote", "set-url", "--add", "--push", "origin", fx.Remote);
        G(fx.A, "remote", "set-url", "--add", "--push", "origin", other);
        int remote = RemoteCount(fx);
        EditLine(fx.A, "TAG( 1,", "TAG( 15,");
        Wait(fx.Ga.SaveAsync(Site, "a"));
        var p = Wait(fx.Ga.PushAsync(Site));
        check(p.Outcome == GitOutcome.Refused && p.Pending == 1, $"15 둘째 push 주소가 다르면 push 안 함 ({p.Outcome} {p.Reason})");
        check(RemoteCount(fx) == remote && Out(fx.Base, "--git-dir=" + other, "for-each-ref") == "", "15 어느 원격에도 올라가지 않음");
        check(!File.ReadAllLines(fx.Ga.LogPath).Any(l => l.Contains("\tpush\t")), "15 push 명령을 부르지 않음");
        LogAll(check, fx);
    }

    /// <summary>16: 쓰기 함수는 저장소 맨 위·master·코어 아님일 때만 돈다.</summary>
    static void Guards(Action<bool, string> check)
    {
        using var fx = Make();
        var ga = fx.Ga;
        // 가지가 master 가 아니다
        G(fx.A, "checkout", "-q", "-b", "side");
        EditLine(fx.A, "TAG( 1,", "TAG( 16,");
        var head = Out(fx.A, "rev-parse", "HEAD");
        bool built = false;
        ImageBuilder b = (app, site, on, ct) => { built = true; return Fake("G")(app, site, on, ct); };
        var results = new (string What, GitOutcome Outcome)[]
        {
            ("저장", Wait(ga.SaveAsync(Site, "x")).Outcome),
            ("생성물 커밋", Wait(ga.CommitPackedAsync(Site)).Outcome),
            ("받기", Wait(ga.PullAsync(Site)).Outcome),
            ("올리기", Wait(ga.PushAsync(Site)).Outcome),
            ("되돌리기", Wait(ga.RevertAsync(head[..8])).Outcome),
            ("버리기", Wait(ga.DiscardAsync(Site)).Outcome),
            ("보관하고 받기", Wait(ga.KeepMineAndResetAsync()).Outcome),
            ("회차", Wait(Round.RunAsync(ga, Site, b)).Outcome),
            ("코어 버전 변경", Wait(CoreUpdate.RunAsync(ga, Site, GrammarTable.Version, null, b)).Outcome),
        };
        foreach (var (what, o) in results) check(o == GitOutcome.Refused, $"16 master 아닌 가지: {what} = Refused ({o})");
        check(!built && Out(fx.A, "rev-parse", "HEAD") == head && Read(fx.A, Tags).Contains("TAG( 16,") && Out(fx.A, "branch", "--list", "keep/*") == "", "16 master 아닌 가지: 아무것도 안 바뀜");
        G(fx.A, "checkout", "-q", "master");
        check(Wait(ga.SaveAsync(Site, "master 에서")).Outcome == GitOutcome.Ok, "16 master 로 돌아오면 된다");

        // Root 가 저장소 맨 위가 아니다
        var sub = new Git(Path.Combine(fx.A, "site"), fx.Remote);
        EditLine(fx.A, "TAG( 2,", "TAG( 26,");
        var s = Wait(sub.DiscardAsync(Site));
        check(s.Outcome == GitOutcome.Refused && Read(fx.A, Tags).Contains("TAG( 26,"), $"16 저장소 맨 위가 아닌 폴더 = Refused ({s.Outcome} {s.Reason})");
        check(Wait(sub.KeepMineAndResetAsync()).Outcome == GitOutcome.Refused, "16 맨 위 아님: 보관하고 받기 Refused");
        Wait(ga.DiscardAsync(Site));

        // 저장소가 아니다
        var none = Path.Combine(fx.Base, "none");
        Directory.CreateDirectory(none);
        var n = Wait(new Git(none, fx.Remote).SaveAsync(Site, "x"));
        check(!n.Ok && n.Popup, $"16 저장소 아님 = 실패 ({n.Outcome})");

        // origin 이 코어 저장소다
        var core = Path.Combine(fx.Base, "AGV_DCU_Core.git");
        G(fx.Base, "init", "-q", "--bare", "--initial-branch=master", core);
        G(fx.B, "remote", "set-url", "origin", core);
        EditLine(fx.B, "TAG( 1,", "TAG( 36,");
        var headB = Out(fx.B, "rev-parse", "HEAD");
        check(Wait(fx.Gb.SaveAsync(Site, "x")).Outcome == GitOutcome.Refused && Wait(fx.Gb.KeepMineAndResetAsync()).Outcome == GitOutcome.Refused
            && Wait(fx.Gb.SetAuthorAsync("가", "a@t.invalid")).Outcome == GitOutcome.Refused && Wait(fx.Gb.DiscardAsync(Site)).Outcome == GitOutcome.Refused,
            "16 코어 저장소를 받은 폴더: 쓰기 Refused");
        check(Out(fx.B, "rev-parse", "HEAD") == headB && Read(fx.B, Tags).Contains("TAG( 36,") && Out(fx.B, "config", "user.name") == "t", "16 코어 폴더: 아무것도 안 바뀜");
        check(Wait(fx.Gb.StatusAsync()) != null && Wait(fx.Gb.HistoryAsync(Site)).Count > 0, "16 읽기는 된다");
        LogAll(check, fx);
    }

    /// <summary>17·21: 값 검사(끝 줄바꿈·전각 숫자), 상태 줄의 따옴표 경로.</summary>
    static void Values(Action<bool, string> check)
    {
        using var fx = Make();
        var ga = fx.Ga;
        EditLine(fx.A, "TAG( 1,", "TAG( 17,");
        foreach (var site in new[] { "T900_DEMO\n", "T９００_DEMO", "T900_DEMO\r" })
            check(Wait(ga.SaveAsync(site, "x")) is { Outcome: GitOutcome.Failed } r && r.Reason!.Contains("사이트 이름"), "17 사이트 이름 거절: " + site.Trim());
        var hash = Out(fx.A, "rev-parse", "--short", "HEAD");
        check(Wait(ga.RevertAsync(hash + "\n")) is { Outcome: GitOutcome.Failed } h && h.Reason!.Contains("해시"), "17 해시 끝 줄바꿈 거절");
        check(Wait(ga.ShowAsync(hash, Site, "tag_table.c\n")) == null && Wait(ga.ShowAsync(hash + "\n", Site, "tag_table.c")) == null && Wait(ga.ShowAsync(hash, Site, "tag_table.c")) != null, "17 옛 표 보기 값 검사");
        var cu = Wait(CoreUpdate.RunAsync(ga, Site, GrammarTable.Version + "\n", null, Fake("V")));
        check(!cu.Ok && cu.Reason!.Contains("버전이 잘못됨"), "17 버전 끝 줄바꿈 거절 · " + cu.Reason);
        Wait(ga.DiscardAsync(Site));

        const string Spaced = "site/T900_DEMO/새 파일.c";
        Write(fx.A, Spaced, "x\n");
        var pull = Wait(ga.PullAsync(Site));
        check(pull.Outcome == GitOutcome.Dirty && pull.Files.SequenceEqual([Spaced]), $"21 공백 든 새 파일도 사이트 폴더의 변경으로 본다 ({pull.Outcome} {string.Join(",", pull.Files)})");
        check(Git.StatusPath("?? \"site/a b/c \\\"d\\\".c\"") == "site/a b/c \"d\".c" && Git.StatusPath(" M site/x.c") == "site/x.c" && Git.StatusPath("R  a.c -> \"b c.c\"") == "b c.c", "21 상태 줄 경로 풀기");
        LogAll(check, fx);
    }

    /// <summary>5: 회차 내기 — 빌드 전에 .gitignore·작성자를 확인하고, 커밋이 실패하면 release/&lt;사이트&gt; 를 전 상태로 되돌린다.</summary>
    static void RoundRollback(Action<bool, string> check)
    {
        using var fx = Make();
        var rel = Path.Combine(fx.A, "release", Site);
        Dictionary<string, string> Snap() => Directory.GetFiles(rel).ToDictionary(f => Path.GetFileName(f)!, f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));
        var before = Snap();
        var head = Out(fx.A, "rev-parse", "HEAD");
        bool Same()
        {
            var now = Snap();
            return now.Count == before.Count && now.All(kv => before.TryGetValue(kv.Key, out var v) && v == kv.Value)
                && Out(fx.A, "status", "--porcelain=v1") == "" && Out(fx.A, "rev-parse", "HEAD") == head;
        }
        bool built = false;
        ImageBuilder b = (app, site, on, ct) => { built = true; return Fake("R")(app, site, on, ct); };

        // 가: 커밋 훅이 막는다(미리 확인으로 못 거르는 실패) → 이전 회차·README 가 돌아오고 새 파일은 없다
        var hook = Path.Combine(fx.A, ".git", "hooks", "pre-commit");
        Write(fx.A, ".git/hooks/pre-commit", "#!/bin/sh\necho 'hook: no' >&2\nexit 1\n");
        var r = Wait(Round.RunAsync(fx.Ga, Site, b));
        File.Delete(hook);
        check(!r.Ok && built, $"5 가: 커밋 실패 ({r.Outcome} {r.Reason})");
        check(Same(), "5 가: 회차 폴더·올린 것이 전 상태 · " + Out(fx.A, "status", "--porcelain=v1").Replace("\n", " | ") + " · " + string.Join(",", Snap().Keys));
        var again = Wait(Round.RunAsync(fx.Ga, Site, Fake("R2")));
        check(again.Ok && again.Number == "04", $"5 가: 그 뒤 회차 내기는 된다 ({again.Outcome} {again.Reason})");
        before = Snap();
        head = Out(fx.A, "rev-parse", "HEAD");

        // 나: 작성자가 없다 → 빌드 전에 막는다
        var empty = Path.Combine(fx.Base, "empty.gitconfig");
        File.WriteAllText(empty, "");
        var oldGlobal = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        var oldSystem = Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", empty);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
        try
        {
            G(fx.A, "config", "--unset", "user.name");
            G(fx.A, "config", "--unset", "user.email");
            G(fx.A, "config", "user.useConfigOnly", "true");
            built = false;
            var na = Wait(Round.RunAsync(fx.Ga, Site, b));
            check(!na.Ok && !built && na.Reason!.Contains("작성자"), $"5 나: 작성자 없음은 빌드 전에 막음 ({na.Outcome} {na.Reason})");
            G(fx.A, "config", "--unset", "user.useConfigOnly");
            G(fx.A, "config", "user.name", "t");
            G(fx.A, "config", "user.email", "t@t.invalid");
            check(Same(), "5 나: 아무것도 안 바뀜");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", oldGlobal);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", oldSystem);
        }

        // 다: .gitignore 에 예외 줄이 없는 사이트 → 빌드 전에 막는다. 폴더·README 를 만들지 않는다
        const string Other = "T902_NOEX";
        built = false;
        var ni = Wait(Round.RunAsync(fx.Ga, Other, b));
        check(!ni.Ok && !built && ni.Reason!.Contains(".gitignore"), $"5 다: .gitignore 예외 없음은 빌드 전에 막음 ({ni.Outcome} {ni.Reason})");
        var otherDir = Path.Combine(fx.A, "release", Other);
        check((!Directory.Exists(otherDir) || !Directory.EnumerateFileSystemEntries(otherDir).Any()) && Out(fx.A, "status", "--porcelain=v1") == "", "5 다: 남는 것 없음");
        LogAll(check, fx);
    }

    /// <summary>22: README 를 다시 쓸 때 줄마다 읽은 줄 끝을 그대로 둔다.</summary>
    static void ReadmeEol(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tedit_git_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var p = Path.Combine(dir, "README.md");
            const string R3 = "03차 이미지(core v0.0.0, 표 0000000, `agv_900_03.out`, 현장 배포 `agv_900_03_field.out`): 처음";
            File.WriteAllBytes(p, Utf8.GetBytes("# 제목\r\n\n설명 줄\r\n" + R3 + "\n끝 줄\r\n꼬리"));
            Git.InsertRoundLine(p, "04차 이미지(x):");
            check(File.ReadAllText(p, Utf8) == "# 제목\r\n\n설명 줄\r\n04차 이미지(x):\n" + R3 + "\n끝 줄\r\n꼬리", "22 회차 줄 넣기: 다른 줄의 줄 끝 그대로 · " + File.ReadAllText(p, Utf8).Replace("\r", "\\r").Replace("\n", "\\n"));
            Git.RenumberReadme(p, "agv_900", "03", "05");
            check(File.ReadAllText(p, Utf8) == "# 제목\r\n\n설명 줄\r\n04차 이미지(x):\n" + R3.Replace("03차", "05차").Replace("agv_900_03", "agv_900_05") + "\n끝 줄\r\n꼬리",
                "22 회차 번호 올림: 다른 줄의 줄 끝 그대로");
            File.WriteAllBytes(p, Utf8.GetBytes("# 제목\r\n\r\n설명"));
            Git.InsertRoundLine(p, "01차 이미지(x):");
            check(File.ReadAllText(p, Utf8) == "# 제목\r\n\r\n설명\r\n\r\n01차 이미지(x):\r\n", "22 회차 줄이 없던 README: 끝에, 그 파일의 줄 끝으로");
            check(Git.CombineRoundConflict("a\r\n<<<<<<< HEAD\n05차 이미지(x\r\n=======\n06차 이미지(y\n>>>>>>> o\nb\n끝") == "a\r\n06차 이미지(y\n05차 이미지(x\r\nb\n끝", "22 충돌 합치기: 줄마다 줄 끝 그대로");
        }
        finally { Directory.Delete(dir, true); }
    }

    static void LogAll(Action<bool, string> check, Fx fx)
    {
        foreach (var g in new[] { fx.Ga, fx.Gb })
        {
            if (!File.Exists(g.LogPath)) continue;
            var lines = File.ReadAllLines(g.LogPath);
            var bad = Git.CheckLog(lines);
            check(bad.Count == 0, "A-11 git.log 어김: " + string.Join(" | ", bad));
            check(!lines.Any(l => l.Contains("--force")), "A-11 --force 0건");
        }
    }

    static void Basics(Action<bool, string> check)
    {
        using var fx = Make();
        var ga = fx.Ga;
        check(Wait(ga.SetAuthorAsync("시험 사람", "a@t.invalid")).Ok, "작성자 저장");
        check(Wait(ga.AuthorAsync()) == ("시험 사람", "a@t.invalid"), "작성자 읽기");
        check(!Wait(ga.SetAuthorAsync("x\ny", "a@t.invalid")).Ok, "작성자 두 줄 거절");

        var st = Wait(ga.StatusAsync());
        check(st is { Branch: "master", HasUpstream: true, Ahead: 0, Clean: true }, "상태 깨끗");
        check(Wait(ga.CheckOnlineAsync()).Outcome == GitOutcome.Ok, "연결 확인");
        check(Wait(ga.PendingAsync()) == 0, "대기 0");

        check(Wait(ga.SaveAsync(Site, "바꾼 것 없음")).Outcome == GitOutcome.NoChange, "저장: 바뀐 것 없음");
        EditLine(fx.A, "TAG( 2,", "TAG( 3,");
        check(Wait(ga.SaveAsync(Site, "")).Outcome == GitOutcome.Failed, "저장: 사유 없음 거절");
        check(Wait(ga.SaveAsync(Site, "둘\n줄")).Outcome == GitOutcome.Failed, "저장: 사유 두 줄 거절");
        check(Wait(ga.SaveAsync("../x", "a")).Outcome == GitOutcome.Failed, "저장: 사이트 이름 거절");
        Write(fx.A, "other.txt", "x");
        G(fx.A, "add", "--", "other.txt");
        var saved = Wait(ga.SaveAsync(Site, "태그 3 으로"));
        check(saved.Outcome == GitOutcome.Ok && saved.Value != null, "저장 커밋");
        check(Out(fx.A, "log", "-1", "--format=%s") == "T900_DEMO: 태그 3 으로", "저장 커밋 글(한글)");
        check(Out(fx.A, "show", "--name-only", "--format=", "HEAD") == Tags, "저장은 site/<사이트> 만(올려 둔 다른 파일 제외)");
        G(fx.A, "reset", "-q", "--", "other.txt");
        File.Delete(Path.Combine(fx.A, "other.txt"));
        check(Wait(ga.PendingAsync()) == 1 && Wait(ga.StatusAsync())!.Ahead == 1, "대기 1");

        var hist = Wait(ga.HistoryAsync(Site));
        check(hist.Count == 2 && hist[0].Subject == "T900_DEMO: 태그 3 으로" && hist[0].Author == "시험 사람" && hist[1].Subject == "처음", "이력");
        var old = Wait(ga.ShowAsync(hist[1].Hash, Site, "tag_table.c"));
        check(old != null && Utf8.GetString(old).Contains("TAG( 2,") && Utf8.GetString(old).Contains("\r\n"), "옛 표 보기(줄 끝 그대로)");
        check(Wait(ga.ShowAsync("--all", Site, "tag_table.c")) == null && Wait(ga.ShowAsync(hist[1].Hash, Site, "../x.c")) == null, "옛 표 보기: 값 검사");

        var rv = Wait(ga.RevertAsync(saved.Value!));
        check(rv.Ok && rv.HeadMoved && Read(fx.A, Tags).Contains("TAG( 2,"), "되돌리기");

        EditLine(fx.A, "TAG( 1,", "TAG( 9,");
        check(Wait(ga.PullAsync(Site)).Outcome == GitOutcome.Dirty, "받기 전 깨끗해야");
        check(Wait(ga.DiscardAsync(Site)).Ok && Read(fx.A, Tags).Contains("TAG( 1,"), "버리기");

        var c = Path.Combine(fx.Base, "c");
        check(Wait(Git.CloneAsync(fx.Remote, c)).Ok && File.Exists(Path.Combine(c, Tags)), "처음 받기");
        check(Wait(Git.CloneAsync(fx.Remote, c)).Outcome == GitOutcome.Failed, "처음 받기: 빈 폴더 아님");
        check(Wait(Git.CloneAsync("--upload-pack=x", Path.Combine(fx.Base, "d"))).Outcome == GitOutcome.Failed, "처음 받기: 주소 검사");
        LogAll(check, fx);
    }

    /// <summary>A-8: 오프라인 저장 3 → 대기 3(팝업 없음) → 연결 → 올리기 → 원격에 커밋 3.</summary>
    static void A8(Action<bool, string> check)
    {
        using var fx = Make();
        int before = RemoteCount(fx);
        Offline(fx, true);
        check(Wait(fx.Ga.CheckOnlineAsync()).Outcome == GitOutcome.Offline, "A-8 연결 확인 = 오프라인");
        for (int i = 0; i < 3; i++)
        {
            EditLine(fx.A, $"TAG( {(i == 0 ? 1 : 10 + i - 1)},", $"TAG( {10 + i},");
            check(Wait(fx.Ga.SaveAsync(Site, $"오프라인 {i + 1}")).Outcome == GitOutcome.Ok, $"A-8 오프라인 저장 {i + 1}");
        }
        check(Wait(fx.Ga.PendingAsync()) == 3, "A-8 대기 3");
        var p = Wait(fx.Ga.PushAsync(Site));
        check(p.Outcome == GitOutcome.Offline && !p.Popup && p.Pending == 3, $"A-8 오프라인 올리기 = 대기 3, 팝업 없음 ({p.Outcome} {p.Pending})");
        Offline(fx, false);
        var q = Wait(fx.Ga.PushAsync(Site));
        check(q.Outcome == GitOutcome.Ok && q.Pending == 0, $"A-8 연결 뒤 올리기 ({q.Outcome} {q.Reason})");
        check(RemoteCount(fx) == before + 3, "A-8 원격에 커밋 3");
        check(Wait(fx.Ga.PushAsync(Site)).Outcome == GitOutcome.NoChange, "A-8 다시 올리기 = 바뀐 것 없음");
        LogAll(check, fx);
    }

    /// <summary>A-9: push 주소를 막힌 값으로(설정의 앱 주소도 그 값). 받기는 되고 올리기는 팝업 없이 대기. 주소가 다르거나 코어면 push 안 함.</summary>
    static void A9(Action<bool, string> check)
    {
        using var fx = Make();
        var blocked = Path.Combine(fx.Base, "blocked.git");
        G(fx.B, "remote", "set-url", "--push", "origin", blocked);
        var gb = new Git(fx.B, blocked);
        EditLine(fx.A, "TAG( 1,", "TAG( 5,");
        Wait(fx.Ga.SaveAsync(Site, "a"));
        check(Wait(fx.Ga.PushAsync(Site)).Ok, "A-9 다른 PC 올리기");
        var pull = Wait(gb.PullAsync(Site));
        check(pull.Ok && pull.HeadMoved && Read(fx.B, Tags).Contains("TAG( 5,"), "A-9 받기는 된다");
        EditLine(fx.B, "TAG( 2,", "TAG( 6,");
        Wait(gb.SaveAsync(Site, "b"));
        var p = Wait(gb.PushAsync(Site));
        check(p.Outcome == GitOutcome.Offline && !p.Popup && p.Pending == 1, $"A-9 올리기 = 대기 1, 팝업 없음 ({p.Outcome} {p.Reason})");

        var mismatch = Wait(new Git(fx.B, fx.Remote).PushAsync(Site));
        check(mismatch.Outcome == GitOutcome.Refused && mismatch.Pending == 1, "A-9 push 주소 ≠ 설정 → push 안 함");
        var core = Path.Combine(fx.Base, "AGV_DCU_Core.git");
        G(fx.B, "remote", "set-url", "--push", "origin", core);
        check(Wait(new Git(fx.B, core).PushAsync(Site)).Outcome == GitOutcome.Refused, "A-9 코어 주소 → push 안 함");
        check(!Directory.Exists(core) && !Directory.Exists(blocked), "A-9 막힌 곳에 아무것도 안 생김");
        var log = File.ReadAllLines(gb.LogPath);
        check(log.Count(l => l.Contains("\tpush\t")) == 1, "A-9 실제 push 명령은 막힌 주소로 한 번뿐");
        LogAll(check, fx);
    }

    /// <summary>A-10: 두 작업 폴더가 같은 태그 줄을 고친다 → 받기 → 충돌, 작업 폴더 그대로 → 보관 가지 뒤 원격 받기.</summary>
    static void A10(Action<bool, string> check)
    {
        using var fx = Make();
        EditLine(fx.A, "TAG( 1,", "TAG( 7,");
        Wait(fx.Ga.SaveAsync(Site, "a 가 7"));
        check(Wait(fx.Ga.PushAsync(Site)).Ok, "A-10 a 올리기");
        EditLine(fx.B, "TAG( 1,", "TAG( 8,");
        Wait(fx.Gb.SaveAsync(Site, "b 가 8"));
        var headBefore = Out(fx.B, "rev-parse", "HEAD");
        var fileBefore = File.ReadAllBytes(Path.Combine(fx.B, Tags));
        var pull = Wait(fx.Gb.PullAsync(Site));
        check(pull.Outcome == GitOutcome.Conflict && pull.Popup && pull.Files.SequenceEqual([Tags]), $"A-10 충돌 목록 ({pull.Outcome} {string.Join(",", pull.Files)})");
        check(Out(fx.B, "rev-parse", "HEAD") == headBefore && File.ReadAllBytes(Path.Combine(fx.B, Tags)).SequenceEqual(fileBefore)
            && !File.Exists(Path.Combine(fx.B, ".git", "MERGE_HEAD")) && Wait(fx.Gb.StatusAsync())!.Clean, "A-10 작업 폴더 받기 전 그대로");
        var push = Wait(fx.Gb.PushAsync(Site));
        check(push.Outcome == GitOutcome.Conflict && push.Popup, "A-10 올리기도 충돌로 멈춤");

        var keep = Wait(fx.Gb.KeepMineAndResetAsync());
        check(keep.Ok && keep.Value != null && keep.Value.StartsWith("keep/"), "A-10 보관 가지");
        check(Out(fx.B, "rev-parse", keep.Value!) == headBefore, "A-10 보관 가지 = 내 커밋");
        check(Out(fx.B, "rev-parse", "HEAD") == Out(fx.B, "rev-parse", "origin/master") && Read(fx.B, Tags).Contains("TAG( 7,"), "A-10 원격 받음");
        EditLine(fx.B, "TAG( 7,", "TAG( 70,");
        check(Wait(fx.Gb.KeepMineAndResetAsync()).Outcome == GitOutcome.Dirty, "A-10 저장 안 한 변경이 있으면 reset 안 함");
        LogAll(check, fx);
    }

    /// <summary>8.4절 3: 생성물만 충돌하면 원격 것으로 합치고, 호출자가 다시 만든 것을 커밋.</summary>
    static void Packed_(Action<bool, string> check)
    {
        using var fx = Make();
        Write(fx.A, Packed, "/* 생성물 */\npacked A\n");
        EditLine(fx.A, "TAG( 1,", "TAG( 11,");
        Wait(fx.Ga.SaveAsync(Site, "a"));
        check(Wait(fx.Ga.PushAsync(Site)).Ok, "생성물 충돌: a 올리기");
        Write(fx.B, Packed, "/* 생성물 */\npacked B\n");
        Write(fx.B, "site/T900_DEMO/site.h", "/* b */\n");
        Wait(fx.Gb.SaveAsync(Site, "b"));
        var pull = Wait(fx.Gb.PullAsync(Site));
        check(pull.Ok && pull.HeadMoved && Read(fx.B, Packed).Contains("packed A") && Read(fx.B, Tags).Contains("TAG( 11,") && File.Exists(Path.Combine(fx.B, "site/T900_DEMO/site.h")),
            $"생성물 충돌 → 원격 것으로 합침 ({pull.Outcome} {pull.Reason})");
        Write(fx.B, Packed, "/* 생성물 */\npacked AB\n");
        var re = Wait(fx.Gb.CommitPackedAsync(Site));
        check(re.Outcome == GitOutcome.Ok && Out(fx.B, "log", "-1", "--format=%s") == "압축 표 다시 만듦", "압축 표 다시 만듦 커밋");
        check(Wait(fx.Gb.CommitPackedAsync(Site)).Outcome == GitOutcome.NoChange, "다시 만든 것이 같으면 커밋 없음");
        check(Wait(fx.Gb.PushAsync(Site)).Ok, "합친 뒤 올리기");
        LogAll(check, fx);
    }

    /// <summary>A-17: 두 작업 폴더가 오프라인에서 같은 번호 회차 → a 올리기 → b 받기: b 회차 번호 올림, README 두 줄, 큰 번호만.</summary>
    static void A17(Action<bool, string> check)
    {
        using var fx = Make();
        Offline(fx, true);
        var ra = Wait(Round.RunAsync(fx.Ga, Site, Fake("A")));
        var rb = Wait(Round.RunAsync(fx.Gb, Site, Fake("B")));
        check(ra.Ok && rb.Ok && ra.Number == "04" && rb.Number == "04", $"A-17 두 쪽 모두 04 ({ra.Reason} {rb.Reason})");
        var bImage = File.ReadAllBytes(Path.Combine(fx.B, "release/T900_DEMO/agv_900_04.out"));
        var bLine = rb.Line!;
        Offline(fx, false);
        check(Wait(fx.Ga.PushAsync(Site)).Ok, "A-17 a 올리기");
        var pull = Wait(fx.Gb.PullAsync(Site));
        check(pull.Ok && pull.Notes.Any(n => n.StartsWith("회차 04 → 05(원격에 04 있음)")), $"A-17 회차 04 → 05 ({pull.Outcome} {pull.Reason} {string.Join(" / ", pull.Notes)})");
        var rel = Path.Combine(fx.B, "release", Site);
        var names = Directory.GetFiles(rel).Select(Path.GetFileName).Order().ToList();
        check(names.SequenceEqual(["README.md", "agv_900_05.map", "agv_900_05.out", "agv_900_05_field.map", "agv_900_05_field.out"]), "A-17 폴더에는 가장 큰 번호만: " + string.Join(",", names));
        check(Out(fx.B, "ls-files", "--", "release/T900_DEMO").Split('\n').Length == 5, "A-17 깃에도 가장 큰 번호만");
        check(File.ReadAllBytes(Path.Combine(rel, "agv_900_05.out")).SequenceEqual(bImage), "A-17 이미지 바이트 그대로");
        var lines = Read(fx.B, Readme).Split('\n');
        var rounds = lines.Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d+차 이미지\(")).ToList();
        check(rounds.Count == 3 && rounds[0].StartsWith("05차 이미지(") && rounds[1].StartsWith("04차 이미지(") && rounds[2].StartsWith("03차"), "A-17 README 두 줄(큰 번호 위): " + string.Join(" | ", rounds));
        check(rounds[0] == bLine.Replace("04차", "05차").Replace("agv_900_04", "agv_900_05") && rounds[1] == ra.Line, "A-17 README 줄은 번호·파일 이름만 바뀜");
        check(!Read(fx.B, Readme).Contains("<<<<<<<"), "A-17 충돌 표식 없음");
        check(Wait(fx.Gb.PushAsync(Site)).Ok, "A-17 b 올리기");
        var pa = Wait(fx.Ga.PullAsync(Site));
        check(pa.Ok && Directory.GetFiles(Path.Combine(fx.A, "release", Site), "agv_900_0*.out").Select(Path.GetFileName).Order().SequenceEqual(["agv_900_05.out", "agv_900_05_field.out"]), "A-17 a 받은 뒤도 05 만");
        LogAll(check, fx);
    }

    /// <summary>A-18: 오프라인에서 저장 뒤 회차 → README 회차 줄에 미공유. 공유된 표면 없음. 더러우면 회차 안 냄.</summary>
    static void A18(Action<bool, string> check)
    {
        using var fx = Make();
        var shared = Wait(Round.RunAsync(fx.Ga, Site, Fake("S")));
        check(shared.Ok && !shared.Unshared && !shared.Line!.Contains("미공유"), "A-18 공유된 표의 회차: 미공유 없음");
        check(shared.Line == $"04차 이미지(core {GrammarTable.Version}, 표 {shared.TableCommit}, `agv_900_04.out`, 현장 배포 `agv_900_04_field.out`):", "A-18 회차 줄 머리 꼴: " + shared.Line);
        check(Out(fx.A, "log", "-1", "--format=%s") == $"04차 이미지: core {GrammarTable.Version} 묶음, agv_900_04(개발·현장)", "A-18 회차 커밋 글");
        check(Directory.GetFiles(Path.Combine(fx.A, "release", Site), "*.out").Length == 2, "A-18 이전 회차 삭제");
        Offline(fx, true);
        EditLine(fx.A, "TAG( 1,", "TAG( 21,");
        Wait(fx.Ga.SaveAsync(Site, "오프라인"));
        var r = Wait(Round.RunAsync(fx.Ga, Site, Fake("U")));
        check(r.Ok && r.Unshared && r.Number == "05" && r.Line!.Contains($"표 {Out(fx.A, "rev-parse", "--short", "HEAD~1")}, 미공유,"), $"A-18 미공유 ({r.Reason} {r.Line})");
        check(Read(fx.A, Readme).Split('\n')[2] == r.Line, "A-18 회차 줄이 맨 위");
        EditLine(fx.A, "TAG( 21,", "TAG( 22,");
        check(Wait(Round.RunAsync(fx.Ga, Site, Fake("D"))).Outcome == GitOutcome.Dirty, "A-18 저장 안 한 변경이 있으면 회차 안 냄");
        Wait(fx.Ga.DiscardAsync(Site));
        ImageBuilder dirtying = async (app, site, on, ct) => { Write(app, Packed, "바뀜\n"); return await Fake("X")(app, site, on, ct); };
        check(Wait(Round.RunAsync(fx.Ga, Site, dirtying)).Outcome == GitOutcome.Dirty, "A-18 빌드가 생성물을 바꾸면 회차 안 냄");
        Offline(fx, false);
        LogAll(check, fx);
    }

    /// <summary>A-14(가짜 스크립트): 같은 버전 다시 받기 = 변경 없음, 없는 태그 = 실패 뒤 core/ 원상. 버전 목록은 dist-v* 만.</summary>
    static void Core(Action<bool, string> check)
    {
        using var fx = Make();
        var ver = GrammarTable.Version;
        // 사이트는 저장소 fixture 를 쓴다(검사 오류 0). 가짜 update_core.ps1: 없는 버전이면 core 를 더럽히고 실패
        var fixture = Path.Combine(RepoRoot(), "tests", "fixtures", Site);
        foreach (var f in Directory.GetFiles(fixture)) File.Copy(f, Path.Combine(fx.A, "site", Site, Path.GetFileName(f)), true);
        Write(fx.A, "tools/update_core.ps1",
            "param([string]$Ref, [string]$From = 'x')\r\n$core = Join-Path (Split-Path -Parent $PSScriptRoot) 'core'\r\n" +
            $"if ($Ref -ne '{ver}') {{ Set-Content (Join-Path $core 'VERSION') 'broken'; Set-Content (Join-Path $core 'junk.txt') 'j'; Write-Error \"no dist-$Ref\"; exit 1 }}\r\n" +
            $"Write-Output ('core = ' + (Get-Content (Join-Path $core 'VERSION')))\r\n");
        G(fx.A, "add", "--", ".");
        G(fx.A, "commit", "-q", "-m", "fixture");
        var head = Out(fx.A, "rev-parse", "HEAD");
        var same = Wait(CoreUpdate.RunAsync(fx.Ga, Site, ver, null, Fake("C")));
        check(same.Ok && !same.Changed && same.Outcome == GitOutcome.NoChange && same.CoreLine != null, $"A-14 같은 버전 = 변경 없음 ({same.Outcome} {same.Reason})");
        check(Out(fx.A, "rev-parse", "HEAD") == head, "A-14 커밋 없음");
        var bad = Wait(CoreUpdate.RunAsync(fx.Ga, Site, "v99.0.0", null, Fake("C")));
        check(!bad.Ok && Read(fx.A, "core/VERSION").StartsWith(ver) && !File.Exists(Path.Combine(fx.A, "core", "junk.txt")) && Wait(fx.Ga.StatusAsync())!.Clean,
            $"A-14 없는 태그 → 실패, core/ 원상 ({bad.Reason})");
        check(!Wait(CoreUpdate.RunAsync(fx.Ga, Site, "v1.0; rm", null, Fake("C"))).Ok, "코어 버전 변경: 버전 글 검사");
        Write(fx.A, "core/extra.h", "x");
        check(Wait(CoreUpdate.RunAsync(fx.Ga, Site, ver, null, Fake("C"))).Outcome == GitOutcome.Dirty, "코어 버전 변경: core 에 새 파일이 있으면 안 함");
        File.Delete(Path.Combine(fx.A, "core", "extra.h"));

        // 버전 목록: 맨 저장소의 태그(dist-v* 만, ^{} 줄·소스 태그 v* 제외)
        var coreRepo = Path.Combine(fx.Base, "core_src");
        Directory.CreateDirectory(coreRepo);
        G(coreRepo, "init", "-q", "--initial-branch=master");
        Write(coreRepo, "VERSION", "v0.1.0\n");
        G(coreRepo, "add", "--", ".");
        G(coreRepo, "commit", "-q", "-m", "c");
        G(coreRepo, "tag", "v0.1.0");
        G(coreRepo, "tag", "dist-v0.1.0");
        G(coreRepo, "tag", "-a", "-m", "a", "dist-v0.10.0");
        G(coreRepo, "tag", "dist-v0.9.1");
        var list = Wait(CoreUpdate.ListVersionsAsync(fx.Ga, coreRepo));
        check(list != null && list.SequenceEqual(["v0.10.0", "v0.9.1", "v0.1.0"]), "코어 버전 목록: " + string.Join(",", list ?? []));
        check(Wait(CoreUpdate.ListVersionsAsync(fx.Ga, Path.Combine(fx.Base, "none.git"))) == null, "코어 버전 목록: 연결 안 됨 = null");
        LogAll(check, fx);
        var log = File.ReadAllLines(fx.Ga.LogPath);
        check(log.Any(l => l.StartsWith("# core-update restore")) && log.Any(l => l.Contains("\tclean\t")), "A-14 원상 단계 기록");
    }

    /// <summary>A-11 검사기 자체: 어긴 줄을 찾는다.</summary>
    static void LogRules(Action<bool, string> check)
    {
        string L(params string[] a) => "2026-01-01 00:00:00\t0\tgit\t-C\tX\t--no-pager\t" + string.Join("\t", a);
        check(Git.CheckLog([L("push", "origin", "master")]).Count == 0, "검사기: 보통 push 통과");
        check(Git.CheckLog([L("push", "--force", "origin", "master")]).Count >= 1, "검사기: --force");
        check(Git.CheckLog([L("push", "--force-with-lease", "origin", "master")]).Count >= 1, "검사기: --force-with-lease");
        check(Git.CheckLog([L("push", "origin", "+master")]).Count == 1, "검사기: +가지");
        check(Git.CheckLog(["# push-url https://github.com/Leepy0/AGV_DCU_Core.git", L("push", "origin", "master")]).Count == 1, "검사기: 코어 주소 push");
        check(Git.CheckLog(["# save T900_DEMO", L("add", "--", "core")]).Count == 1, "검사기: 코어 버전 변경 밖 core 변경");
        check(Git.CheckLog(["# core-update commit v0.1.0", L("add", "--", "core", "site/T900_DEMO/tag_packed.c")]).Count == 0, "검사기: 코어 버전 변경 안 core 변경 통과");
        check(Git.CheckLog(["# pull T900_DEMO", L("clean", "-fd")]).Count == 1, "검사기: clean 자리");
        check(Git.CheckLog(["# pull T900_DEMO", L("reset", "--hard", "origin/master")]).Count == 1, "검사기: reset --hard 자리");
        check(Git.CombineRoundConflict("a\n<<<<<<< HEAD\n05차 이미지(x\n=======\n06차 이미지(y\n>>>>>>> o\nb\n") == "a\n06차 이미지(y\n05차 이미지(x\nb\n", "README 회차 줄 합치기");
        check(Git.CombineRoundConflict("<<<<<<< HEAD\n설명\n=======\n06차 이미지(y\n>>>>>>> o\n") == null, "README 회차 줄 아닌 충돌은 못 합침");
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TableEditor.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("TableEditor.sln 을 못 찾음");
    }
}
