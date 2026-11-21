using System.IO;
using System.Text;
using TableEditor.App.ViewModels;
using TableEditor.Engine.Ops;

namespace TableEditor.App;

/// <summary>
/// 자체 시험의 깃·빌드·회차·이력 단계. 작업 폴더 아래에 맨(bare) 원격 하나와 clone 둘(a = 이 창, b = 다른 사람)을 만든다.
/// 앱 주소 설정 = 그 맨 저장소 경로(실제 원격에 닿지 않는다). 압축·빌드는 가짜로 갈아 끼워 화면 흐름만 본다.
/// 캡처: save_reason.png, conflict.png, build.png, round.png, history.png.
/// </summary>
public sealed partial class SelfTest
{
    const string SiteName = "T900_DEMO";
    static readonly UTF8Encoding Utf8 = new(false);
    string remote = "", a = "", b = "";
    int dialogs;
    readonly List<string> opened = [];

    static readonly IReadOnlyDictionary<string, string?> GitEnv = new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C" };

    /// <summary>시험 준비용 git(엔진 밖). 줄 끝은 그대로 둔다.</summary>
    static async Task<string> G(string dir, params string[] args)
    {
        var r = await ProcessRunner.RunAsync(new ProcessSpec("git", ["-c", "core.autocrlf=false", "-C", dir, .. args], dir, TimeSpan.FromSeconds(60), ProcessRunner.Utf8, GitEnv));
        if (!r.Ok) throw new InvalidOperationException($"git {string.Join(" ", args)}: {string.Join(" ", r.Texts)}");
        return string.Join("\n", r.Lines.Where(l => !l.IsError).Select(l => l.Text)).Trim();
    }

    static string TagFile(string root) => Path.Combine(root, "site", SiteName, "tag_table.c");

    /// <summary>다른 사람(b): 받고, 한 줄 바꾸고, 커밋해 올린다.</summary>
    async Task OtherCommits(string from, string to, string message)
    {
        await G(b, "pull", "-q", "--no-rebase", "origin", "master");
        var text = File.ReadAllText(TagFile(b), Utf8);
        Check(text.Split(from).Length == 2, $"b: '{from}' 한 곳");
        File.WriteAllText(TagFile(b), text.Replace(from, to), Utf8);
        await G(b, "commit", "-q", "-am", $"{SiteName}: {message}");
        await G(b, "push", "-q", "origin", "master");
    }

    static void Clear(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, true);
    }

    /// <summary>가짜 check.ps1 출력(로컬 경로 없음).</summary>
    static IReadOnlyList<string> FakeLines(bool field)
    {
        var name = field ? "agv_900_field" : "agv_900";
        var list = new List<string>
        {
            $"core {TableEditor.Engine.Grammar.GrammarTable.Version} 0000000 cl2000 v18.12.2.LTS",
            "core 확인됨 (3f2a9c1d0b7e)",
            "시험 생략 (-BuildOnly): 코어 호스트 시험·레거시 대조·흐름 시험을 돌지 않았다",
        };
        foreach (var f in new[] { "act_table.c", "manual_table.c", "profiles.c", "seq_packed.c", "seq_table.c", "tag_packed.c", "tag_table.c" })
            list.Add($"cl2000 -v28 -ml -mt --float_support=softlib -O2 site{(field ? "_field" : "")}\\{SiteName}\\{f}");
        list.Add($"c28: linked build\\c28\\{name}.out");
        list.Add($"빌드 통과(시험 생략) -> build\\c28\\{name}.out");
        return list;
    }

    async Task GitScenario()
    {
        Step("깃: 시험 저장소(맨 원격 + clone 둘), 저장소로 열기");
        var bas = Path.Combine(Path.GetFullPath(work), "git");
        Clear(bas);
        Directory.CreateDirectory(bas);
        remote = Path.Combine(bas, "remote.git");
        var seed = Path.Combine(bas, "seed");
        a = Path.Combine(bas, "a");
        b = Path.Combine(bas, "b");
        await G(bas, "init", "-q", "--bare", "--initial-branch=master", remote);
        Directory.CreateDirectory(seed);
        await G(seed, "init", "-q", "--initial-branch=master");
        var siteDir = Path.Combine(seed, "site", SiteName);
        Directory.CreateDirectory(siteDir);
        foreach (var f in Directory.GetFiles(Path.Combine(RepoRoot(), "tests", "fixtures", SiteName)))
            File.Copy(f, Path.Combine(siteDir, Path.GetFileName(f)));
        File.WriteAllText(Path.Combine(seed, ".gitignore"), $"*.out\n*.map\nbuild/\n!release/{SiteName}/*\n", Utf8);
        Directory.CreateDirectory(Path.Combine(seed, "release", SiteName));
        File.WriteAllText(Path.Combine(seed, "release", SiteName, "README.md"), "# T900 데모 회차\n\n", Utf8);
        // 코어 버전 줄(V-3 이 보는 목록: 줄 끝 LF 로 맞춘 SHA-256). 코어 헤더는 없다
        var version = $"{TableEditor.Engine.Grammar.GrammarTable.Version} 0000000 cl2000 v18.12.2.LTS\n";
        Directory.CreateDirectory(Path.Combine(seed, "core"));
        File.WriteAllText(Path.Combine(seed, "core", "VERSION"), version, Utf8);
        File.WriteAllText(Path.Combine(seed, "core", "MANIFEST.sha256"), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Utf8.GetBytes(version))) + "  VERSION\n", Utf8);
        await G(seed, "add", "--", ".");
        await G(seed, "-c", "user.name=시험", "-c", "user.email=seed@example.invalid", "commit", "-q", "-m", "처음");
        await G(seed, "push", "-q", remote, "master");
        foreach (var (dir, who) in new[] { (a, "작업자A"), (b, "작업자B") })
        {
            await G(bas, "clone", "-q", remote, dir);
            await G(dir, "config", "core.autocrlf", "false");
            await G(dir, "config", "user.name", who);
            await G(dir, "config", "user.email", who == "작업자A" ? "a@example.invalid" : "b@example.invalid");
        }

        vm.Settings.AppUrl = remote;
        vm.Packer = (app, site, on, ct) =>
        {
            on?.Invoke(new OutputLine(false, "규칙 42(범위 0), 조건 1, 입력 1, 동작 51 -> build\\tag_pack\\tag_packed.c"));
            return Task.FromResult(new PackResult(true, false, null, null, []));
        };
        vm.Imager = (app, site, field, on, ct) =>
        {
            var dir = Path.Combine(app, "build", "c28");
            Directory.CreateDirectory(dir);
            var name = field ? "agv_900_field" : "agv_900";
            File.WriteAllText(Path.Combine(dir, name + ".out"), name);
            File.WriteAllText(Path.Combine(dir, name + ".map"), name);
            var lines = FakeLines(field);
            foreach (var l in lines) on?.Invoke(new OutputLine(false, l));
            return Task.FromResult(new BuildResult(true, 0, Path.Combine(dir, name + ".out"), null, [], lines));
        };
        IReadOnlyList<ToolStatus> allTools = [new("gcc", true, "gcc.exe", "gcc 16", null), new("powershell", true, "powershell.exe", "5.1", null), new("cl2000", true, "cl2000.exe", "v18.12.2.LTS", null)];
        vm.Probe = _ => Task.FromResult(allTools);
        vm.OpenPath = opened.Add;
        vm.DialogShown += _ => dialogs++;

        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        await vm.TryOpen(() => vm.OpenRepoAsync(a));
        await Idle();
        Check(vm.HasRepo && vm.SiteName == SiteName && vm.ErrorCount == 0, $"저장소·사이트·오류 0 ({vm.SiteName}, {vm.ErrorCount})");
        Check(vm.GitText == "최신", $"깃 칩 최신 ({vm.GitText})");

        Step("작성자 팝업 → 저장소 로컬 설정·설정의 이름");
        var (au, auDone) = await Open<AuthorVm>(() => vm.AskAuthorAsync(null, null));
        Check(!au.Ok.CanExecute(null), "빈 칸이면 저장 흐림");
        au.Name = "작업자A";
        au.Mail = "a@example.invalid";
        au.Ok.Execute(null);
        await auDone;
        Check(await G(a, "config", "--local", "user.name") == "작업자A" && vm.Settings.UserName == "작업자A", "로컬 user.name·설정 이름");

        Step("저장: 사유 팝업 → 파일 → 압축 → 커밋, 칩 ↑ 대기 1");
        Tag.SelectOnly(Row(22));
        await Idle();
        Tag.Fields.First(f => f.Label == "각도").Text = "45";
        Tag.Fields.First(f => f.Label == "각도").Commit();
        await Idle();
        var (rv, saveDone) = await Open<ReasonVm>(() => vm.SaveAsync());
        Check(!rv.Ok.CanExecute(null), "빈 사유 = 저장 흐림");
        rv.Text = "회전 각도 45";
        Check(rv.Ok.CanExecute(null), "사유 있으면 저장");
        await Shot("save_reason.png");
        rv.Ok.Execute(null);
        await saveDone;
        await Idle();
        Check(await G(a, "log", "-1", "--format=%s") == $"{SiteName}: 회전 각도 45", "커밋 글 = 사이트: 사유");
        Check(vm.ChangeCount == 0 && vm.Pending == 1 && vm.GitText == "↑ 대기 1", $"변경 0 · 대기 1 ({vm.ChangeCount}, {vm.GitText})");

        Step("바뀐 것이 없으면 저장은 조용히 끝");
        int before = dialogs;
        Check(await vm.SaveAsync() && dialogs == before, "팝업 없음");

        Step("올리기 → 대기 0, 원격에 있음");
        before = dialogs;
        await vm.PushAsync();
        await Idle();
        Check(dialogs == before && vm.Pending == 0 && vm.GitText == "최신", $"팝업 없음 · 최신 ({vm.GitText})");
        Check(await G(bas, "--git-dir=" + remote, "log", "-1", "--format=%s", "master") == $"{SiteName}: 회전 각도 45", "원격 master 에 커밋");

        Step("다른 clone 이 다른 줄을 올림 → 받기 → 표에 두 변경");
        await OtherCommits("CW,  180,", "CW,  135,", "회전 각도 135");
        await vm.PullAsync();
        await Idle();
        Check(Row(22).Rule!.St.Text.Contains("CCW, 45,") && Row(23).Rule!.St.Text.Contains("CW,  135,"), "45 · 135 모두");
        Check(vm.ChangeCount == 0 && vm.GitText == "최신", $"변경 0 · 최신 ({vm.GitText})");

        Step("같은 줄 충돌 → 충돌 팝업 → 보관하고 받기 → 보관 가지");
        await OtherCommits("CCW, 45,", "CCW, 60,", "회전 각도 60");
        Tag.SelectOnly(Row(22));
        await Idle();
        Tag.Fields.First(f => f.Label == "각도").Text = "30";
        Tag.Fields.First(f => f.Label == "각도").Commit();
        await Idle();
        var (rv2, save2) = await Open<ReasonVm>(() => vm.SaveAsync());
        rv2.Text = "회전 각도 30";
        rv2.Ok.Execute(null);
        await save2;
        var (cf, pullDone) = await Open<ConflictVm>(() => vm.PullAsync());
        Check(cf.Files.Contains($"site/{SiteName}/tag_table.c"), $"충돌 파일 ({string.Join(", ", cf.Files)})");
        await Shot("conflict.png");
        var (sure, _) = await Open<FactsVm>(() => { cf.Keep.Execute(null); return Task.CompletedTask; });
        Check(sure.Buttons.Any(x => x.Key == "cancel" && x.Default) && sure.Buttons.Any(x => x.Key == "ok" && x.Label == "보관하고 받기"), "확인 팝업 · 기본 취소 · 동사형");
        sure.Close("ok");
        await pullDone;
        await Idle();
        Check(vm.HasNotice && vm.Notice!.Contains("keep/") && !vm.NoticeIsError, $"보관 가지 알림 ({vm.Notice})"); // 알림은 4초 타이머로 사라진다: git 호출 전에 본다(PC 가 바쁘면 뒤에서는 이미 지워져 있다)
        var keep = await G(a, "branch", "--list", "keep/*");
        Check(keep.Contains("keep/"), $"보관 가지 ({keep})");
        Check(await G(a, "rev-parse", "HEAD") == await G(a, "rev-parse", "origin/master"), "HEAD = origin/master");
        Check(Row(22).Rule!.St.Text.Contains("CCW, 60,") && vm.Pending == 0, $"원격 값 60 · 대기 0 ({vm.Pending})");

        Step("빌드: 도구 없음 팝업 → 설정");
        vm.Probe = _ => Task.FromResult<IReadOnlyList<ToolStatus>>([new("gcc", false, "gcc.exe", null, "파일 없음"), allTools[1], allTools[2]]);
        var (tool, toolDone) = await Open<AskVm>(() => vm.BuildAsync());
        Check(tool.Title == "도구 없음" && tool.Lines.SequenceEqual(["gcc"]) && tool.Buttons.Any(x => x.Key == "settings"), $"없는 도구 이름 ({string.Join(",", tool.Lines)})");
        tool.Close("settings");
        await toolDone;
        Check(vm.IsSettingsTab, "설정 화면으로");
        vm.Probe = _ => Task.FromResult(allTools);

        var bp = vm.BuildPane;
        Step("빌드 오류 → 검사 목록 → 그 블록");
        var tagLine = Row(5).Rule!.St.StartLine;
        var ok = vm.Imager;
        vm.Imager = (app, site, field, on, ct) =>
        {
            on?.Invoke(new OutputLine(false, $"\"site\\{SiteName}\\tag_table.c\", line {tagLine}: error #20: identifier \"DEMO_X\" is undefined"));
            return Task.FromResult(new BuildResult(false, 1, null, "종료 코드 1", [new BuildError("cl2000", "tag_table.c", tagLine, 20, "identifier \"DEMO_X\" is undefined")], []));
        };
        await vm.BuildAsync();
        await Idle();
        var bf = vm.Findings.FirstOrDefault(f => f.Rule == "빌드");
        Check(bp.Dev.State == StageState.Fail && bp.Field.State == StageState.Wait && bf != null && bf.Tab?.Name == MainVm.TagTab, $"개발 실패 · 검사 목록 ({bf?.At})");
        if (bf != null) vm.Go(bf);
        Check(vm.IsTagTab && Tag.Selected?.First == 5, $"그 줄로 감 ({Tag.Selected?.First})");
        vm.Imager = ok;

        Step("빌드: 검사 → 압축 → 개발 → 현장, 출력·결과 경로");
        await vm.BuildAsync();
        await Idle();
        Check(vm.IsBuildTab && bp.Stages.All(s => s.State == StageState.Pass) && !bp.Running, $"단계 모두 통과 ({string.Join(",", bp.Stages.Select(s => s.Word))})");
        Check(bp.DevText == @"build\c28\agv_900.out" && bp.FieldText == @"build\c28\agv_900_field.out", $"결과 경로 ({bp.DevText}, {bp.FieldText})");
        Check(bp.Lines.Count >= 20 && bp.Lines[^1].StartsWith("빌드 통과", StringComparison.Ordinal), $"출력 줄 {bp.Lines.Count}");
        bp.OpenFolder.Execute(null);
        Check(opened.LastOrDefault()?.EndsWith(Path.Combine("build", "c28"), StringComparison.OrdinalIgnoreCase) == true, "폴더 열기");
        vm.ClearNotice();
        await Shot("build.png");

        Step("A7 빌드: 두 번 눌러도 한 번 · 바로 빌드 탭 · 도구 확인 낱말 · 단계 비중 진행");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        var slowTools = new TaskCompletionSource<IReadOnlyList<ToolStatus>>();
        int probes = 0, images = 0;
        var seen = new List<int>();
        vm.Probe = _ => { probes++; return slowTools.Task; };
        var okPack = vm.Packer;
        vm.Packer = (app, site, on, ct) => { seen.Add(bp.Progress); return okPack(app, site, on, ct); };
        vm.Imager = async (app, site, field, on, ct) =>
        {
            images++;
            seen.Add(bp.Progress);
            if (field) await Shot("build_run.png");
            return await ok(app, site, field, on, ct);
        };
        var run1 = vm.BuildAsync();
        await Idle();
        Check(vm.IsBuildTab && vm.BusyText == "도구 확인" && bp.Running && bp.Progress == 0, $"누르는 즉시 빌드 탭 · 도구 확인 ({vm.BusyText}, {bp.Progress})");
        Check(!vm.Build.CanExecute(null) && !bp.Run.CanExecute(null), "[빌드] 흐림");
        await vm.BuildAsync(); // 두 번째 눌림: 아무 일 없이 돌아온다
        slowTools.SetResult(allTools);
        await run1;
        await Idle();
        Check(probes == 1 && images == 2, $"한 번만 시작 (도구 확인 {probes}, 이미지 {images})");
        Check(seen.SequenceEqual([10, 25, 60]) && bp.Progress == 100 && vm.BusyText == null, $"압축·개발·현장 앞 진행 {string.Join("/", seen)} → {bp.Progress}");
        Check(vm.Build.CanExecute(null), "끝나면 [빌드] 눌림");
        vm.Packer = okPack;
        vm.Imager = ok;
        vm.Probe = _ => Task.FromResult(allTools);

        Step("A8·A9 cl2000: 출력 18.12.2 읽기 · 느리면 경고(실패 아님) · -Cgt 는 설정이 있고 스크립트가 알 때만");
        var fakeDir = Path.Combine(Path.GetFullPath(work), "fake_cl2000");
        Clear(fakeDir);
        Directory.CreateDirectory(fakeDir);
        File.WriteAllText(Path.Combine(fakeDir, "fast.cmd"), "@echo off\r\necho 18.12.2\r\n");
        File.WriteAllText(Path.Combine(fakeDir, "slow.cmd"), "@echo off\r\nping -n 10 127.0.0.1 >nul\r\necho 18.12.2\r\n");
        var fastTool = await ToolProbe.Cl2000Async(Path.Combine(fakeDir, "fast.cmd"));
        var slowTool = await ToolProbe.Cl2000Async(Path.Combine(fakeDir, "slow.cmd"), default, TimeSpan.FromSeconds(1));
        Check(fastTool.Found && fastTool.Version == "v18.12.2" && !fastTool.Slow, $"출력 18.12.2 읽기 ({fastTool.Version})");
        Check(slowTool.Found && slowTool.Slow, "제한 초과 = 느림 경고, 있음으로 계속");
        var cgtCfg = @"C:\ti\ti-cgt-c2000_18.12.2.LTS\bin\cl2000.exe";
        var scriptDir = Path.Combine(fakeDir, "app");
        Directory.CreateDirectory(Path.Combine(scriptDir, "tools"));
        File.WriteAllText(Path.Combine(scriptDir, "tools", "check.ps1"), "param([string]$Site)\n");
        Check(!SiteBuild.ScriptArgs(scriptDir, SiteName, "n", false, cgtCfg).Contains("-Cgt"), "스크립트가 -Cgt 를 모르면 안 넘김");
        File.WriteAllText(Path.Combine(scriptDir, "tools", "check.ps1"), "param([string]$Site, [string]$Cgt = 'x')\n");
        Check(SiteBuild.ScriptArgs(scriptDir, SiteName, "n", false, cgtCfg).SkipWhile(x => x != "-Cgt").Skip(1).FirstOrDefault() == @"C:\ti\ti-cgt-c2000_18.12.2.LTS"
            && !SiteBuild.ScriptArgs(scriptDir, SiteName, "n", false, null).Contains("-Cgt"), "알고 설정 있을 때만 -Cgt = bin 의 상위");
        Clear(fakeDir);

        Step("회차 내기: 확인 팝업(번호·코어·표·공유) → 진행 → README 줄");
        var head = await G(a, "rev-parse", "--short", "HEAD");
        var (rd, roundDone) = await Open<FactsVm>(() => vm.RoundAsync());
        string Fact(string l) => rd.Facts.FirstOrDefault(f => f.Label == l)?.Value ?? "";
        Check(Fact("회차") == "01차" && Fact("표") == head && Fact("공유") == "공유됨", $"값 ({Fact("회차")}, {Fact("표")}, {Fact("공유")})");
        Check(rd.Buttons.Any(x => x.Key == "cancel" && x.Default) && rd.Buttons.Any(x => x.Label == "01차 내기"), "[취소][01차 내기] · 기본 취소");
        Check(vm.ErrorCount == 0, $"앞 빌드 오류는 새 빌드에서 지워짐 ({vm.ErrorCount})");
        vm.ClearNotice();
        await Shot("round.png");
        rd.Close("ok");
        await roundDone;
        await Idle();
        var rel = Path.Combine(a, "release", SiteName);
        Check(File.Exists(Path.Combine(rel, "agv_900_01.out")) && File.Exists(Path.Combine(rel, "agv_900_01_field.out")), "회차 파일 넷");
        Check(bp.ReadmeLine?.StartsWith("01차 이미지(", StringComparison.Ordinal) == true && File.ReadAllText(Path.Combine(rel, "README.md")).Contains(bp.ReadmeLine), $"README 줄 머리 ({bp.ReadmeLine})");
        Check((await G(a, "log", "-1", "--format=%s")).StartsWith("01차 이미지", StringComparison.Ordinal) && vm.Pending == 1, $"회차 커밋 · 대기 1 ({vm.Pending})");
        Check(bp.OpenReadme.CanExecute(null) && bp.Round.CanExecute(null), "[README 열기][회차 내기] 눌림");
        bp.OpenReadme.Execute(null);
        Check(opened.LastOrDefault()?.EndsWith("README.md", StringComparison.Ordinal) == true, "README 열기");

        Step("이력: 목록(회차 표시·대기 표시)");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.HistoryTabName);
        await vm.History.LoadAsync();
        await Idle();
        var rows = vm.History.Rows;
        Check(rows.Count >= 5 && rows[0].Round == "01차" && rows[0].Waiting && rows.Skip(1).All(r => !r.Waiting), $"줄 {rows.Count} · 01차 · 대기 1줄");
        var r60 = rows.First(r => r.Reason == "회전 각도 60");
        Check(r60.Author == "작업자B" && r60.Date.Length == 16, $"사람·날짜 ({r60.Author}, {r60.Date})");
        vm.History.Selected = r60;
        await Shot("history.png");

        Step("보기(읽기 전용) → 모든 표 탭 → 돌아가기");
        var first = rows[^1];
        await vm.ViewCommitAsync(first.Entry);
        await Idle();
        Check(vm.IsReadOnly && vm.ReadOnlyText == "읽기 전용 · " + first.Hash[..Math.Min(7, first.Hash.Length)] && vm.IsTagTab, $"띠 ({vm.ReadOnlyText})");
        Check(Row(22).Rule!.St.Text.Contains("CCW, 90,"), "처음 표(90)");
        Check(!vm.Save.CanExecute(null) && !vm.Pull.CanExecute(null) && !vm.Push.CanExecute(null) && !vm.Build.CanExecute(null), "편집·저장·깃 버튼 흐림");
        Tag.SelectOnly(Row(22));
        await Idle();
        Tag.Fields.First(f => f.Label == "각도").Text = "10";
        Tag.Fields.First(f => f.Label == "각도").Commit();
        await Idle();
        Check(vm.ChangeCount == 0 && Row(22).Rule!.St.Text.Contains("CCW, 90,"), "편집 막힘");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.ProfileTab);
        Check(vm.Profile.Rows.Count == 4, "프로파일 탭도 보임");
        vm.BackFromView.Execute(null);
        await Idle();
        Check(!vm.IsReadOnly && vm.IsHistoryTab && Row(22).Rule!.St.Text.Contains("CCW, 60,"), "돌아옴(60)");

        Step("되돌리기: 확인 팝업(사유) → revert → 다시 읽기");
        var (rvt, revertDone) = await Open<FactsVm>(() => vm.RevertAsync(r60.Entry));
        Check(rvt.Facts.Any(f => f.Value == $"{SiteName}: 회전 각도 60") && rvt.Buttons.Any(x => x.Key == "cancel" && x.Default) && rvt.Buttons.Any(x => x.Label == "되돌리기"), "사유 · 기본 취소");
        rvt.Close("ok");
        await revertDone;
        await Idle();
        Check(Row(22).Rule!.St.Text.Contains("CCW, 45,") && vm.Pending == 2 && vm.ChangeCount == 0, $"45 로 돌아옴 · 대기 2 ({vm.Pending})");

        Step("바깥 변경 칩 → 다시 읽기");
        var tf = TagFile(a);
        var outside = File.ReadAllText(tf, Utf8).Replace("CW,  135,", "CW,  150,");
        File.WriteAllText(tf, outside, Utf8);
        vm.CheckExternal();
        Check(vm.HasExternal && vm.ExternalFiles.Contains("tag_table.c"), "바깥 변경 칩");
        await vm.ReloadExternalAsync();
        await Idle();
        Check(!vm.HasExternal && Row(23).Rule!.St.Text.Contains("CW,  150,"), "다시 읽음");

        Step("되돌리기: 편집 + 저장 안 한 파일 → 확인 → 처음대로");
        Tag.SelectOnly(Row(22));
        await Idle();
        Tag.Fields.First(f => f.Label == "각도").Text = "15";
        Tag.Fields.First(f => f.Label == "각도").Commit();
        await Idle();
        var (dc, discardDone) = await Open<FactsVm>(() => vm.DiscardAsync());
        Check(dc.Facts.Any(f => f.Label == "편집") && dc.Facts.Any(f => f.Label == "파일" && f.Value.Contains("tag_table.c")) && dc.Buttons.Any(x => x.Key == "cancel" && x.Default), "편집·파일 · 기본 취소");
        dc.Close("ok");
        await discardDone;
        await Idle();
        Check(vm.ChangeCount == 0 && Row(22).Rule!.St.Text.Contains("CCW, 45,") && Row(23).Rule!.St.Text.Contains("CW,  135,") && await G(a, "status", "--porcelain") == "", "편집·파일 모두 버림");

        Step("막힌 push 주소 → 올리기 = 팝업 없음, 대기 그대로");
        var blocked = Path.Combine(bas, "blocked.git");
        await G(a, "remote", "set-url", "--push", "origin", blocked);
        vm.Settings.AppUrl = blocked;
        before = dialogs;
        await vm.PushAsync();
        await Idle();
        Check(dialogs == before && vm.Pending == 2 && vm.GitText == "↑ 대기 2", $"팝업 없음 · {vm.GitText}");

        Step("push 주소가 설정과 다름 → 막힘 팝업(까닭)");
        vm.Settings.AppUrl = remote;
        var (refused, refusedDone) = await Open<AskVm>(() => vm.PushAsync());
        Check(refused.Title == "올리기 막힘" && refused.Lines.Any(l => l.Contains("앱 주소")), $"까닭 ({string.Join(" / ", refused.Lines)})");
        refused.Close("ok");
        await refusedDone;
    }
}
