using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Input;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Ops;
using TableEditor.Engine.Text;

namespace TableEditor.App.ViewModels;

/// <summary>압축 한 번(사양 7.2절 3·3b). 자체 시험은 가짜로 갈아 끼운다.</summary>
public delegate Task<PackResult> PackRunner(string appRoot, string site, Action<OutputLine>? onLine, CancellationToken ct);

/// <summary>개발(field = false)·현장 빌드 한 번(사양 7.2절 4·5).</summary>
public delegate Task<BuildResult> ImageRunner(string appRoot, string site, bool field, Action<OutputLine>? onLine, CancellationToken ct);

/// <summary>
/// 깃·빌드·회차·코어 올리기·이력 보기(사양 7·8절). 저장·깃·빌드 동작은 한 줄로 선다(겹치지 않는다).
/// 도는 동안 그 버튼만 흐리고 위 줄에 낱말 + 진행. 편집은 막지 않는다:
/// 받아서 표가 바뀌었을 때 저장 안 한 편집이 있으면 다시 읽지 않고 "바깥 변경" 칩을 띄운다(저장하면 충돌 팝업).
/// </summary>
public sealed partial class MainVm
{
    public const string BuildTabName = "빌드", HistoryTabName = "이력";

    public BuildVm BuildPane { get; private set; } = null!;
    public HistoryVm History { get; private set; } = null!;

    public PackRunner Packer { get; set; } = null!;
    public ImageRunner Imager { get; set; } = (app, site, field, on, ct) => SiteBuild.RunAsync(app, site, null, field, on, ct);
    public Func<CancellationToken, Task<IReadOnlyList<ToolStatus>>> Probe { get; set; } = null!;

    /// <summary>폴더·README 를 기본 프로그램으로 연다(자체 시험은 기록만).</summary>
    public Action<string> OpenPath { get; set; } = OpenShell;

    static void OpenShell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Win32Exception) { Process.Start(new ProcessStartInfo("notepad.exe", [path]) { UseShellExecute = false }); } // .md 에 연결된 프로그램이 없으면
    }

    void InitOps()
    {
        BuildPane = new BuildVm(this);
        History = new HistoryVm(this);
        Imager = (app, site, field, on, ct) => SiteBuild.RunAsync(app, site, null, field, on, ct, Settings.Cl2000Path);
        Packer = (app, site, on, ct) => SitePack.RunAsync(app, site, Settings.GccPath, on, ct);
        Probe = ct => ToolProbe.ProbeAllAsync(Settings.GccPath, Settings.Cl2000Path ?? ToolProbe.Cl2000Default, ct);
        Pull = new Command(() => _ = PullAsync(), () => CanGit && op != "pull");
        Push = new Command(() => _ = PushAsync(), () => CanGit && op != "push");
        Build = new Command(() => _ = BuildAsync(), () => CanGit && !building && op is not ("build" or "round" or "core"));
        Discard = new Command(() => _ = DiscardAsync(), () => CanEdit && HasRepo);
        ReloadExternal = new Command(() => _ = ReloadExternalAsync(), () => CanEdit);
        BackFromView = new Command(LeaveView, () => IsReadOnly && Dialog == null);
        UpdateCore = new Command(() => _ = CoreUpdateAsync(), () => CanGit && op == null);
    }

    public ICommand Pull { get; private set; } = Command.None;
    public ICommand Push { get; private set; } = Command.None;
    public ICommand Build { get; private set; } = Command.None;
    public ICommand Discard { get; private set; } = Command.None;
    public ICommand ReloadExternal { get; private set; } = Command.None;
    public ICommand BackFromView { get; private set; } = Command.None;
    public ICommand UpdateCore { get; private set; } = Command.None;

    /// <summary>깃·빌드 버튼이 눌리는가: 저장소로 열었고, 읽기 전용 보기가 아니고, 팝업이 없다.</summary>
    public bool CanGit => HasRepo && Site != null && !IsReadOnly && Dialog == null && SiteName != null;

    Git? GitOf() => Repo == null ? null : new Git(Repo, Settings.AppUrl);

    // ---- 한 줄로 서기 ----

    readonly SemaphoreSlim gate = new(1, 1);
    string? op;

    /// <summary>저장·깃·빌드 동작 하나를 돌린다. 앞 동작이 끝날 때까지 기다린다. 도는 동안 위 줄에 낱말.</summary>
    async Task<T> Locked<T>(string key, string word, Func<Task<T>> body)
    {
        await gate.WaitAsync();
        op = key;
        BusyText = word;
        CommandManager.InvalidateRequerySuggested();
        try { return await body(); }
        finally
        {
            op = null;
            BusyText = null;
            gate.Release();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>도는 동작의 낱말을 바꾼다(압축 → 개발 빌드 → 현장 빌드).</summary>
    public void Word(string word) { if (op != null) BusyText = word; }

    // ---- 깃 상태 칩 ----

    int? pending;
    bool offline;

    /// <summary>위 줄 깃 칩: 최신 / ↑ 대기 n / 오프라인(대기가 있으면 같이). 원격 가지를 모르면 —.</summary>
    public string GitText => offline ? (pending > 0 ? $"오프라인 · 대기 {pending}" : "오프라인")
        : pending switch { null => "—", 0 => "최신", var n => $"↑ 대기 {n}" };
    public int? Pending => pending;
    public bool IsOffline => offline;

    void SetGit(int? p = null, bool? off = null)
    {
        if (p != null) pending = p;
        if (off is bool o) offline = o;
        Raise(nameof(GitText));
        Raise(nameof(Pending));
        Raise(nameof(IsOffline));
    }

    public async Task RefreshGitAsync()
    {
        if (GitOf() is not { } g) return;
        pending = await g.PendingAsync();
        SetGit();
    }

    /// <summary>
    /// 깃 결과 → 칩·팝업. Popup 이 false 면 팝업이 없다(성공·오프라인). Login → 로그인 팝업, 그 밖 → 까닭과 파일. 성공인가.
    /// </summary>
    async Task<bool> ReportAsync(GitResult r, string what)
    {
        if (r.Pending is int p) SetGit(p);
        if (r.Ok) return true;
        if (!r.Popup) return false;
        var title = r.Outcome switch
        {
            GitOutcome.Login => "로그인 필요",
            GitOutcome.Conflict => "충돌",
            GitOutcome.Refused => what + " 막힘",
            GitOutcome.Dirty => "저장 안 한 변경",
            _ => what + " 실패",
        };
        await Close1(title, [.. r.Reason is { } why ? [why] : Array.Empty<string>(), .. r.Files]);
        return false;
    }

    /// <summary>[닫기] 하나짜리 팝업.</summary>
    Task<string?> Close1(string title, IReadOnlyList<string> lines) =>
        Show(new AskVm(title, lines, [new("ok", "닫기", Primary: true, Default: true)]));

    static AskButton CancelButton => new("cancel", "취소", Default: true);

    // ---- 켤 때 ----

    /// <summary>켤 때(사양 8.1·8.3절): 작성자 없으면 묻기 → 연결 확인 → 칩. 대기가 있으면 한 번 올린다.</summary>
    public async Task StartupAsync()
    {
        if (GitOf() is not { } g) return;
        var (name, mail) = await g.AuthorAsync();
        if (name == null || mail == null) await AskAuthorAsync(name ?? (Settings.UserName.Length > 0 ? Settings.UserName : null), mail);
        var on = await g.CheckOnlineAsync();
        offline = !on.Ok;
        await RefreshGitAsync();
        if (on.Ok && pending > 0) await PushAsync();
    }

    /// <summary>작성자 팝업 → 저장소 로컬 설정. 이름은 설정의 이름에도 쓴다.</summary>
    public async Task AskAuthorAsync(string? name, string? mail)
    {
        if (GitOf() is not { } g) return;
        var vm = new AuthorVm(name, mail);
        if (await Show(vm) != "ok") return;
        var r = await g.SetAuthorAsync(vm.Name.Trim(), vm.Mail.Trim());
        if (!await ReportAsync(r, "작성자")) return;
        Settings.UserName = vm.Name.Trim();
        Settings.Save();
        Config.Reload();
    }

    // ---- 받기 ----

    /// <summary>[받기](사양 8.2·8.4절): 저장 안 한 편집 → [저장][버리기][취소], 충돌 팝업, 표가 바뀌면 다시 읽기 → 검사 → 압축 → 압축 커밋.</summary>
    public async Task PullAsync()
    {
        if (!CanGit) return;
        if (!await ConfirmLeaveAsync(reload: true)) return;
        var site = SiteName!;
        for (int i = 0; i < 2; i++)
        {
            var r = await Locked("pull", "받는 중", () => GitOf()!.PullAsync(site));
            if (r.Outcome == GitOutcome.Dirty && i == 0)
            {
                if (await ResolveDirtyAsync(r.Files)) continue;
                return;
            }
            if (r.Outcome == GitOutcome.Offline) SetGit(off: true);
            else if (r.Ok) SetGit(off: false);
            if (r.Outcome == GitOutcome.Conflict) { await ConflictAsync(r.Files); break; }
            if (!await ReportAsync(r, "받기")) break;
            if (r.HeadMoved) await Locked("pull", "받는 중", AfterHeadMovedAsync);
            if (r.Notes.Count > 0) Notify(string.Join(" · ", r.Notes), error: false);
            break;
        }
        await RefreshGitAsync();
        History.Stale();
    }

    /// <summary>디스크에 커밋 안 한 표 변경(사양 8.4절 1): [저장](사유 → 커밋) / [버리기] / [취소]. 다시 해도 되는가.</summary>
    async Task<bool> ResolveDirtyAsync(IReadOnlyList<string> files)
    {
        var key = await Show(new AskVm("저장 안 한 변경", files, [CancelButton, new("drop", "버리기"), new("save", "저장", Primary: true)]));
        if (key == "save")
        {
            var rv = new ReasonVm();
            if (await Show(rv) != "ok") return false;
            var r = await Locked("save", "저장", () => GitOf()!.SaveAsync(SiteName!, rv.Text.Trim()));
            return await ReportAsync(r, "저장");
        }
        if (key == "drop")
        {
            var r = await Locked("discard", "버리는 중", () => GitOf()!.DiscardAsync(SiteName!));
            if (!await ReportAsync(r, "버리기")) return false;
            await ReloadAsync();
            return true;
        }
        return false;
    }

    /// <summary>
    /// 충돌(사양 8.4절 5): [내 것 보관하고 원격 받기][그대로 두기]. 앞의 것은 한 번 더 확인 뒤
    /// 보관 가지 → 원격으로 맞춤 → 다시 읽기·검사·압축. 끝나면 보관 가지 이름을 알린다.
    /// </summary>
    async Task ConflictAsync(IReadOnlyList<string> files)
    {
        if (await Show(new ConflictVm(files)) != "keep") return;
        var sure = await Show(new FactsVm("내 것 보관하고 원격 받기",
            [new("내 커밋", "keep/<날짜시각>", Mono: true), new("작업 폴더", $"origin/{Git.Branch}", Mono: true)],
            [CancelButton, new("ok", "보관하고 받기", Primary: true)]));
        if (sure != "ok") return;
        var r = await Locked("pull", "받는 중", () => GitOf()!.KeepMineAndResetAsync());
        if (!await ReportAsync(r, "받기")) return;
        await Locked("pull", "받는 중", AfterHeadMovedAsync);
        if (r.Value != null) Notify("보관 · " + r.Value, error: false);
    }

    /// <summary>
    /// 받기·되돌리기로 표가 바뀐 뒤(GitResult.HeadMoved): 다시 읽기 → 검사 → 압축 → 바뀌었으면 압축 커밋.
    /// 저장 안 한 편집이 있으면 화면은 그대로 두고(바깥 변경 칩) 디스크 표로 검사한다. 끝까지 갔는가.
    /// </summary>
    async Task<bool> AfterHeadMovedAsync()
    {
        if (Repo is not { } root || SiteName is not { } name) return false;
        var folder = Path.Combine(root, "site", name);
        var core = Path.Combine(root, "core");
        Site disk;
        if (Dirty)
        {
            disk = await Task.Run(() => Site.Open(folder, Directory.Exists(core) ? core : null));
            CheckExternal();
        }
        else
        {
            await TryOpen(() => OpenAsync(folder, core));
            if (Site is not { } s) return false;
            disk = s;
        }
        int errors = Checker.Run(disk).Count(f => f.Severity == Severity.Error);
        if (errors > 0)
        {
            Notify($"검사 오류 {errors} · 압축 안 함");
            return false;
        }
        Word("압축");
        var pk = await RunPackAsync(root, name, null, CancellationToken.None);
        if (pk is not { Ok: true }) return false;
        if (pk.Changed) return await ReportAsync(await GitOf()!.CommitPackedAsync(name), "압축 커밋");
        return true;
    }

    // ---- 올리기 ----

    /// <summary>
    /// [올리기](사양 8.2·8.3절). 오프라인·막힌 망은 팝업 없이 칩만. 받기에서 표가 바뀌면(HeadMoved) 올라간 것이 아니다:
    /// 다시 읽기 → 검사 → 압축 → 압축 커밋 → 한 번 더 올린다.
    /// </summary>
    public async Task PushAsync()
    {
        if (!CanGit) return;
        var site = SiteName!;
        for (int i = 0; i < 3; i++)
        {
            var r = await Locked("push", "올리는 중", () => GitOf()!.PushAsync(site));
            if (r.Outcome == GitOutcome.Dirty && i == 0)
            {
                if (await ResolveDirtyAsync(r.Files)) continue;
                break;
            }
            if (r.Outcome == GitOutcome.Conflict && r.Files.Count > 0) { await ConflictAsync(r.Files); break; }
            if (!await ReportAsync(r, "올리기")) break;
            SetGit(off: false);
            if (r.Notes.Count > 0) Notify(string.Join(" · ", r.Notes), error: false);
            if (!r.HeadMoved) break;
            if (!await Locked("push", "올리는 중", AfterHeadMovedAsync)) break;
        }
        await RefreshGitAsync();
        History.Stale();
    }

    // ---- 저장 뒤 커밋 ----

    /// <summary>저장(파일) 뒤: 압축 → 사유로 커밋(사양 8.2절 저장). 압축이 실패하면 커밋하지 않는다(설계서 4.7절 5).</summary>
    async Task AfterSaveAsync(string? reason)
    {
        if (Repo is not { } root || SiteName is not { } name) return;
        Word("압축");
        var pk = await RunPackAsync(root, name, null, CancellationToken.None);
        if (reason == null) return;
        if (pk is not { Ok: true })
        {
            Notify("압축 실패 · 커밋 안 함");
            return;
        }
        Word("저장");
        await ReportAsync(await GitOf()!.SaveAsync(name, reason), "저장");
        await RefreshGitAsync();
        History.Stale();
    }

    /// <summary>압축(실패하면 검사 목록에 한 줄). 결과, 예외면 null.</summary>
    async Task<PackResult?> RunPackAsync(string root, string site, Action<OutputLine>? onLine, CancellationToken ct)
    {
        PackResult? pr = null;
        try
        {
            pr = await Packer(root, site, onLine, ct);
            packFindings = pr.Ok ? [] : [new Finding("압축", Severity.Error, "tag_packed.c", null, null, $"{pr.Step} · {pr.Reason}", [], [])];
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            packFindings = [new Finding("압축", Severity.Error, "tag_packed.c", null, null, e.Message, [], [])];
        }
        if (Site is { } s) SetFindings(s, Checker.Run(s).Concat(packFindings).ToList());
        return pr;
    }

    // ---- 빌드 ----

    TabVm TabNamed(string name) => Tabs.First(t => t.Name == name);

    /// <summary>
    /// [빌드](사양 7.2절): 저장 안 한 변경이 있으면 저장 흐름 먼저 → 도구 확인(없으면 팝업 → 설정) →
    /// 검사 → 압축 → 개발 → 현장. 빌드 오류는 검사 목록에(줄을 누르면 그 블록).
    /// </summary>
    public async Task BuildAsync()
    {
        if (!CanGit || building || op is "build" or "round" or "core") return;
        building = true; // 누르는 즉시 잠금(저장·도구 확인 동안 두 번째 눌림 막기)
        CommandManager.InvalidateRequerySuggested();
        try
        {
            Tab = TabNamed(BuildTabName);
            if (Dirty && !await SaveAsync()) return;
            if (Dirty) return;
            var b = BuildPane;
            var root = Repo!;
            var site = SiteName!;
            b.Start("빌드", root);
            b.ToolsChecked(false);
            BusyText = "도구 확인";
            bool ready;
            try { ready = await ToolsReadyAsync(); }
            finally { if (op == null) BusyText = null; }
            b.ToolsChecked(true);
            if (!ready) { b.Finish(); return; }
            ClearOpFindings();
            await Locked("build", "검사", async () =>
            {
                try
                {
                    if (!CheckStage(b)) return 0;
                    var img = await StagedBuilder(b)(root, site, b.OnLine, b.Token);
                    if (!img.Ok) ShowBuildErrors(img.Errors);
                }
                finally { b.Finish(); }
                return 0;
            });
        }
        finally
        {
            building = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    bool building;

    /// <summary>앞 빌드·압축 오류를 검사 목록에서 뺀다(새로 돈다).</summary>
    void ClearOpFindings()
    {
        packFindings = [];
        if (Site is { } s) SetFindings(s, Checker.Run(s));
    }

    /// <summary>검사 단계: 오류가 있으면 첫 오류로 가고 멈춘다.</summary>
    bool CheckStage(BuildVm b)
    {
        b.Begin(b.Check, "검사");
        bool ok = Site is { } s && !Checker.Run(s).Any(f => f.Severity == Severity.Error);
        b.End(b.Check, ok);
        if (!ok && Findings.FirstOrDefault(f => f.IsError) is { } first) Go(first);
        return ok;
    }

    /// <summary>도구 확인(사양 7.1절). 없으면 팝업에 없는 도구 이름과 [설정].</summary>
    async Task<bool> ToolsReadyAsync()
    {
        var tools = await Probe(CancellationToken.None);
        var missing = tools.Where(t => !t.Found && t.Name is "gcc" or "powershell" or "cl2000").Select(t => t.Name).ToList();
        if (missing.Count == 0) return true;
        var key = await Show(new AskVm("도구 없음", missing, [new("close", "닫기", Default: true), new("settings", "설정", Primary: true)]));
        if (key == "settings")
        {
            Tab = SettingsTab;
            Config.ShowTools(tools.Select(t => new ToolRow(t.Name, t.Found, t.Found ? (t.Slow ? $"느림 · {t.Path}" : $"{t.Version} · {t.Path}") : t.Detail ?? t.Path ?? "")));
        }
        return false;
    }

    /// <summary>압축 → 개발 → 현장(단계 표시를 움직인다). 회차 내기·코어 올리기도 이것을 빌드로 쓴다.</summary>
    ImageBuilder StagedBuilder(BuildVm b) => async (app, site, onLine, ct) =>
    {
        if (b.Check.State == StageState.Run || b.Check.State == StageState.Wait) b.End(b.Check, true); // 코어 올리기: 엔진 검사를 지났다
        b.Begin(b.Pack, "압축");
        var pk = await RunPackAsync(app, site, onLine, ct);
        b.End(b.Pack, pk is { Ok: true });
        if (pk is not { Ok: true }) return new(false, null, null, $"압축 · {pk?.Step} · {pk?.Reason}", []);

        b.Begin(b.Dev, "개발 빌드");
        var dev = await Imager(app, site, false, onLine, ct);
        b.End(b.Dev, dev.Ok);
        b.DevOut = dev.OutPath;
        if (!dev.Ok) return new(false, null, null, "개발 빌드 · " + dev.Reason, dev.Errors);

        b.Begin(b.Field, "현장 빌드");
        var field = await Imager(app, site, true, onLine, ct);
        b.End(b.Field, field.Ok);
        b.FieldOut = field.OutPath;
        if (!field.Ok) return new(false, dev.OutPath, null, "현장 빌드 · " + field.Reason, field.Errors);
        return new(true, dev.OutPath, field.OutPath, null, []);
    };

    /// <summary>빌드 오류(사양 7.3절) → 검사 목록. (파일, 줄) 은 LineMap 으로, tag_pack_gen 규칙 n 은 n 번째 태그 줄로.</summary>
    void ShowBuildErrors(IReadOnlyList<BuildError> errors)
    {
        if (Site is not { } s) return;
        var map = LineMap.Of(s);
        var list = new List<Finding>();
        foreach (var e in errors.Where(e => !e.IsWarning))
        {
            Piece? p = e.File != null && e.Line is int l ? map.At(e.File, l) : null;
            if (e.Source == "tag_pack_gen" && e.Number is int n && n < s.TagRules.Count) p = s.TagRules[n].St;
            list.Add(new Finding("빌드", Severity.Error, e.File ?? e.Source, p, null, e.Text, [], []));
        }
        if (list.Count == 0) return;
        packFindings = [.. packFindings, .. list];
        SetFindings(s, Checker.Run(s).Concat(packFindings).ToList());
        CheckOpen = true;
    }

    // ---- 회차 내기 ----

    /// <summary>다음 회차 번호(그 폴더 가장 큰 번호 + 1, 두 자리).</summary>
    static string NextRound(string root, string site)
    {
        var dir = Path.Combine(root, "release", site);
        var re = new Regex("^" + Regex.Escape(SiteBuild.DefaultName(site)) + @"_(\d+)(_field)?\.(out|map)$");
        int max = Directory.Exists(dir)
            ? Directory.GetFiles(dir).Select(f => re.Match(Path.GetFileName(f))).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Max()
            : 0;
        return (max + 1).ToString("D2");
    }

    /// <summary>
    /// 회차 내기(사양 7.5절): 확인 팝업(번호, 코어, 표 커밋, 미공유) → Round.RunAsync(빌드 화면에 진행) →
    /// README 회차 줄 머리를 보이고 [README 열기].
    /// </summary>
    public async Task RoundAsync()
    {
        if (!CanGit || op != null) return;
        if (Dirty && !await SaveAsync()) return;
        if (Dirty) return;
        var root = Repo!;
        var site = SiteName!;
        var g = GitOf()!;
        var nn = NextRound(root, site);
        var head = await GitRead.HeadShortAsync(root) ?? "?";
        var unshared = await g.PendingAsync() is not 0;
        var key = await Show(new FactsVm("회차 내기",
            [new("회차", nn + "차"), new("코어", Round.CoreVersion(root) ?? "?"), new("표", head, Mono: true), new("공유", unshared ? "미공유" : "공유됨")],
            [CancelButton, new("ok", nn + "차 내기", Primary: true)]));
        if (key != "ok") return;
        if (!await ToolsReadyAsync()) return;

        Tab = TabNamed(BuildTabName);
        var b = BuildPane;
        b.Start(nn + "차 회차", root);
        ClearOpFindings();
        var r = await Locked("round", "검사", async () =>
        {
            try { return CheckStage(b) ? await Round.RunAsync(g, site, StagedBuilder(b), b.OnLine, b.Token) : null; }
            finally { b.Finish(); }
        });
        await RefreshGitAsync();
        History.Stale();
        if (r == null) return;
        if (r.Ok)
        {
            b.ReadmeLine = r.Line;
            b.ReadmePath = Path.Combine(root, "release", site, "README.md");
            Notify($"{r.Number}차 · 커밋", error: false);
            return;
        }
        if (r.Build is { Ok: false } img) ShowBuildErrors(img.Errors);
        await Close1("회차 내기 실패", [r.Reason ?? ""]);
    }

    // ---- 코어 올리기 ----

    /// <summary>코어 올리기(사양 8.6절): 버전 목록 → 확인 → CoreUpdate.RunAsync(빌드 화면에 진행) → 결과 줄(성공 core = …, 실패 까닭).</summary>
    public async Task CoreUpdateAsync()
    {
        if (!CanGit || op != null) return;
        if (!await ConfirmLeaveAsync(reload: true)) return;
        var root = Repo!;
        var site = SiteName!;
        var g = GitOf()!;
        var now = Round.CoreVersion(root) ?? "—";
        var pick = new VersionsVm(now);
        _ = pick.LoadAsync(() => CoreUpdate.ListVersionsAsync(g, Settings.CoreUrl));
        if (await Show(pick) != "ok" || pick.Selected is not { } v) return;
        if (await Show(new FactsVm("코어 올리기", [new("지금", now), new("새", v)], [CancelButton, new("ok", v + " 받기", Primary: true)])) != "ok") return;

        Tab = TabNamed(BuildTabName);
        var b = BuildPane;
        b.Start("코어 " + v, root);
        ClearOpFindings();
        b.ResultLabel = "코어";
        b.Begin(b.Check, "코어 받는 중");
        var r = await Locked("core", "코어 받는 중", async () =>
        {
            try { return await CoreUpdate.RunAsync(g, site, v, null, StagedBuilder(b), b.OnLine, b.Token); }
            finally { b.Finish(); }
        });
        if (r.Ok)
        {
            b.ResultLine = r.CoreLine ?? v;
            await ReloadAsync();
        }
        else
        {
            b.ResultLine = r.Reason;
            if (r.Findings.Count > 0 && Site is { } s) { packFindings = [.. r.Findings.Where(f => f.Severity == Severity.Error)]; SetFindings(s, Checker.Run(s).Concat(packFindings).ToList()); }
            if (r.Build is { Ok: false } img) ShowBuildErrors(img.Errors);
            await Close1("코어 올리기 실패", [r.Reason ?? "", "core/ 원상"]);
        }
        await RefreshGitAsync();
        History.Stale();
    }

    // ---- 버리기·다시 읽기 ----

    /// <summary>지금 사이트를 디스크에서 다시 연다(초안은 지운다).</summary>
    async Task ReloadAsync()
    {
        if (Site is not { } s) return;
        draftTimer.Stop();
        TryDraft(() => Drafts.Clear(s));
        await TryOpen(() => OpenAsync(s.Folder, s.CoreFolder));
        external = [];
        Raise(nameof(HasExternal));
    }

    /// <summary>사이트 폴더의 깃 변경 줄(경로).</summary>
    async Task<IReadOnlyList<string>> SiteChangesAsync()
    {
        if (GitOf() is not { } g || SiteName is not { } name) return [];
        var st = await g.StatusAsync();
        var prefix = $"site/{name}/";
        return st?.Changed.Select(l => l.Length > 3 ? l[3..].Trim('"') : l).Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).Select(p => p[prefix.Length..]).ToList() ?? [];
    }

    /// <summary>[버리기](변경 칩 메뉴): 확인 팝업 뒤 편집 내용과 저장 안 한 파일 변경을 버린다.</summary>
    public async Task DiscardAsync()
    {
        if (Site == null || IsReadOnly) return;
        var files = await SiteChangesAsync();
        if (!Dirty && files.Count == 0) { Notify("변경 없음", error: false); return; }
        var facts = new List<Fact>();
        if (Dirty) facts.Add(new("편집", $"변경 {ChangeCount}"));
        if (files.Count > 0) facts.Add(new("파일", string.Join(", ", files)));
        if (await Show(new FactsVm("버리기", facts, [CancelButton, new("ok", "버리기", Primary: true)])) != "ok") return;
        if (files.Count > 0)
        {
            var r = await Locked("discard", "버리는 중", () => GitOf()!.DiscardAsync(SiteName!));
            if (!await ReportAsync(r, "버리기")) return;
        }
        await ReloadAsync();
    }

    IReadOnlyList<string> external = [];
    public bool HasExternal => external.Count > 0;
    public IReadOnlyList<string> ExternalFiles => external;

    /// <summary>창이 다시 앞에 왔다: 읽은 뒤 바깥에서 바뀐 파일(SaveFiles.Conflicts)이 있으면 "바깥 변경" 칩.</summary>
    public void CheckExternal()
    {
        if (Site is not { } s || IsReadOnly || IsBusy || s.Folder.Length == 0) return;
        try { external = SaveFiles.Conflicts(s); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        Raise(nameof(HasExternal));
        Raise(nameof(ExternalFiles));
    }

    /// <summary>[다시 읽기](바깥 변경 칩): 편집이 있으면 버린다는 확인 뒤.</summary>
    public async Task ReloadExternalAsync()
    {
        if (Dirty && await Show(new FactsVm("다시 읽기", [new("편집", $"변경 {ChangeCount}"), new("바뀐 파일", string.Join(", ", external))],
                [CancelButton, new("ok", "다시 읽기", Primary: true)])) != "ok") return;
        await ReloadAsync();
    }

    // ---- 이력: 보기·되돌리기 ----

    sealed record Stash(EditHistory History, IReadOnlyDictionary<string, int> Baseline, IReadOnlyList<Finding> Found, TabVm Tab);
    Stash? stash;
    string? viewHash, viewFolder;

    public bool IsReadOnly => stash != null;
    public string ReadOnlyText => $"읽기 전용 · {viewHash}";

    /// <summary>[보기]: 그 커밋의 사이트 파일을 임시 폴더에 풀어 읽기 전용으로 연다. 모든 표 탭을 볼 수 있다.</summary>
    public async Task ViewCommitAsync(HistoryEntry e)
    {
        if (GitOf() is not { } g || SiteName is not { } name || history == null || IsBusy) return;
        var dir = Path.Combine(Path.GetTempPath(), "tedit_view", e.Hash, name);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            int n = 0;
            foreach (var f in FileZones.Files)
                if (await g.ShowAsync(e.Hash, name, f.FileName) is { } bytes) { await File.WriteAllBytesAsync(Path.Combine(dir, f.FileName), bytes); n++; }
            if (n == 0) { Notify("표 없음 · " + e.Hash); return; }
            var core = Site?.CoreFolder;
            var s = await Task.Run(() => Site.Open(dir, core));
            stash ??= new Stash(history, baseline, packFindings, Tab);
            viewHash = e.Hash.Length > 7 ? e.Hash[..7] : e.Hash;
            viewFolder = dir;
            history = new EditHistory(s);
            baseline = Baseline(s);
            packFindings = [];
            RaiseView();
            Refresh(null, draft: false);
            if (Tab.Group != 0) Tab = TabNamed(TagTab);
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { Notify("보기 실패 · " + x.Message); }
    }

    /// <summary>[돌아가기]: 보기 전의 편집 상태 그대로.</summary>
    public void LeaveView()
    {
        if (stash is not { } st) return;
        history = st.History;
        baseline = st.Baseline;
        packFindings = st.Found;
        stash = null;
        try { if (viewFolder != null && Directory.Exists(viewFolder)) Directory.Delete(viewFolder, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* 임시 폴더: 다음 보기가 지운다 */ }
        viewFolder = viewHash = null;
        RaiseView();
        Refresh(null, draft: false);
        Tab = st.Tab;
    }

    void RaiseView()
    {
        Raise(nameof(IsReadOnly));
        Raise(nameof(ReadOnlyText));
        Raise(nameof(CanEdit));
        Raise(nameof(CanGit));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>[되돌리기]: 확인 팝업(사유) → git revert → 다시 읽기 → 검사 → 압축 확인.</summary>
    public async Task RevertAsync(HistoryEntry e)
    {
        if (!CanGit || op != null) return;
        if (!await ConfirmLeaveAsync(reload: true)) return;
        var subject = e.Subject;
        if (await Show(new FactsVm("되돌리기", [new("사유", subject), new("커밋", e.Hash, Mono: true), new("날짜", e.Date.Length >= 16 ? e.Date[..16] : e.Date)],
                [CancelButton, new("ok", "되돌리기", Primary: true)])) != "ok") return;
        var r = await Locked("revert", "되돌리는 중", () => GitOf()!.RevertAsync(e.Hash));
        if (await ReportAsync(r, "되돌리기") && r.HeadMoved) await Locked("revert", "압축", AfterHeadMovedAsync);
        await RefreshGitAsync();
        History.Stale();
    }
}
