using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Ops;

namespace TableEditor.App.ViewModels;

/// <summary>
/// 프로그램 업데이트(설정 화면의 업데이트 칸 + 위 줄 칩 + 토스트). 참고: AmrMapEditor MainWindow.AppUpdate.
/// 시작 3초 뒤 확인 → 새 버전이면 팝업, 그 뒤 1시간마다 확인 → 처음 보는 새 버전이면 토스트.
/// 받기 → &lt;설치&gt;.new 완성 → 닫을 때(또는 [닫고 적용]) 바깥 도우미가 이름 바꾸기로 교체. 도우미는 지우지 않고, 버린 폴더는 다음 시작 때 검사 뒤 지운다.
/// 받아 둔 버전은 창마다 기억하지 않고 .new 폴더가 기준이다(여러 창). 캡처·자체 시험(<see cref="Settings.Volatile"/>)은 네트워크를 쓰지 않는다.
/// 적용·되돌리기·복구는 모두 같은 검사(<see cref="LaunchCheckedAsync"/>)를 거친다. 업데이트 때문에 앱이 꺼지지 않게 예외는 실패 칩으로 둔다.
/// </summary>
public sealed class UpdateVm : Observable
{
    public enum State { None, Checking, Available, Downloading, Ready, Failed }

    readonly MainVm main;
    readonly Settings s;
    readonly DispatcherTimer hourly = new() { Interval = TimeSpan.FromHours(1) };
    readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly string install = Updater.InstallDir;
    readonly string dir = Updater.UpdateDirFor(Updater.InstallDir);
    Version current = Updater.Current;
    Version? pending;
    ReleaseInfo? latest;
    CancellationTokenSource? dl;
    string? shown; // 팝업·토스트로 이미 알린 버전
    string? failure;
    DateTime? checkedAt;

    public UpdateVm(MainVm main, Settings s)
    {
        if (Enum.TryParse<Updater.NetRoute>(s.UpdateRoute, out var route)) Updater.Route = route;
        this.main = main;
        this.s = s;
        hourly.Tick += (_, _) => Safe(() => CheckAsync(Origin.Tick));
        toastTimer.Tick += (_, _) => Toast = null;
        CheckNow = Cmd(() => CheckAsync(Origin.Manual), () => state is not (State.Checking or State.Downloading));
        Download = Cmd(DownloadAsync, () => latest is { } r && r.Version > current && state is State.Available or State.Failed);
        CancelDownload = new Command(() => dl?.Cancel(), () => state == State.Downloading);
        ApplyNow = Cmd(ApplyNowAsync, () => state == State.Ready);
        DiscardPending = Cmd(DiscardAsync, () => state == State.Ready);
        Rollback = Cmd(RollbackAsync, () => OldVersion != null && state != State.Downloading);
        OpenInfo = new Command(() => { if (main.HasDialog) return; Toast = null; main.Tab = main.SettingsTab; });
        if (Updater.CanSelfUpdate && !s.Volatile) Safe(() => { RefreshPending(); return Task.CompletedTask; });
    }

    /// <summary>창을 닫아 달라(도우미를 띄운 뒤). MainWindow 가 단다.</summary>
    public event Action? CloseForUpdate;

    ICommand Cmd(Func<Task> run, Func<bool> can) => new Command(() => Safe(run), can);

    /// <summary>명령·주기 확인 래퍼: 예외는 실패 칩으로(앱은 계속 돈다).</summary>
    async void Safe(Func<Task> run)
    {
        try { await run(); }
        catch (Exception e) { Fail(Word(e)); }
    }

    void Fail(string word)
    {
        failure = word;
        Now = State.Failed;
        Raise(nameof(ChipText));
    }

    static string Word(Exception e) => e switch
    {
        UpdateException => e.Message,
        System.Net.Http.HttpRequestException => "연결 실패",
        OperationCanceledException => "시간 초과",
        IOException or UnauthorizedAccessException => "파일 오류",
        _ => e.GetType().Name,
    };

    void RefreshPending()
    {
        pending = Updater.Pending(install, current);
        if (pending != null && state is State.None or State.Failed or State.Available) Now = State.Ready;
        else if (pending == null && state == State.Ready) Now = latest is { } r && r.Version > current ? State.Available : State.None;
    }

    // ---- 화면 값 ----

    public string VersionText => current.ToString(3);
    public string ChannelText => Updater.CanSelfUpdate ? "정식" : "로컬";
    public string BuildText => $"{VersionText} · {ChannelText} · {Updater.Commit ?? "—"}";

    State state;
    public State Now
    {
        get => state;
        private set
        {
            if (!Set(ref state, value)) return;
            foreach (var n in new[] { nameof(ChipText), nameof(FailureDetail), nameof(HasChip), nameof(IsDownloading), nameof(CheckPrimary), nameof(DownloadPrimary), nameof(ApplyPrimary), nameof(ChipAccent) })
                Raise(n);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    double progress;
    public double Progress { get => progress; private set { if (Set(ref progress, value)) Raise(nameof(ChipText)); } }

    /// <summary>상태 칩 하나(설정 화면·위 줄).</summary>
    public string ChipText => state switch
    {
        State.Checking => "확인 중",
        State.Available => $"새 버전 {latest?.Version.ToString(3)}",
        State.Downloading => $"받는 중 {Progress:0}%",
        State.Ready => $"준비됨 {pending?.ToString(3)}",
        State.Failed => $"실패 · {failure?.Split('\n')[0]}",
        _ => checkedAt is { } t ? $"최신 · {t:HH:mm}" : "확인 안 함",
    };

    /// <summary>확인 실패가 여러 줄이면(방법마다 한 줄) 첫 줄 뒤 나머지. 칩 아래에 보인다.</summary>
    public string FailureDetail => state == State.Failed && failure is { } f && f.Contains('\n') ? f[(f.IndexOf('\n') + 1)..] : "";

    /// <summary>위 줄 칩: 할 일이 있을 때만.</summary>
    public bool HasChip => state is State.Available or State.Downloading or State.Ready;
    public bool ChipAccent => state is State.Available or State.Ready;
    public bool IsDownloading => state == State.Downloading;

    // 강조 = 색만: 지금 누를 단추 하나
    public bool CheckPrimary => state is State.None or State.Failed;
    public bool DownloadPrimary => state == State.Available;
    public bool ApplyPrimary => state == State.Ready;

    /// <summary>직전 버전(.old, 되돌릴 곳). 시작할 때 한 번 읽는다(명령 재질의마다 파일을 읽지 않게).</summary>
    public string? OldVersion { get; } = Updater.CanSelfUpdate ? Updater.OldVersion(Updater.InstallDir) : null;

    public bool AutoCheck
    {
        get => s.UpdateAuto;
        set
        {
            s.UpdateAuto = value;
            s.Save();
            Raise();
            if (value && CanNetwork && Updater.CanSelfUpdate) hourly.Start(); else hourly.Stop();
        }
    }

    string? toast;
    /// <summary>창 안 작은 알림(5초). 누르면 설정 화면.</summary>
    public string? Toast
    {
        get => toast;
        private set
        {
            if (!Set(ref toast, value)) return;
            Raise(nameof(HasToast));
            toastTimer.Stop();
            if (value != null) toastTimer.Start();
        }
    }
    public bool HasToast => Toast != null;

    public ICommand CheckNow { get; }
    public ICommand Download { get; }
    public ICommand CancelDownload { get; }
    public ICommand ApplyNow { get; }
    public ICommand DiscardPending { get; }
    public ICommand Rollback { get; }
    public ICommand OpenInfo { get; }

    bool CanNetwork => !s.Volatile;

    Task<string?> Ask(string title, params string[] lines) =>
        main.Show(new AskVm(title, lines, [new AskButton("ok", "닫기", Primary: true, Default: true)]));

    // ---- 시작 ----

    /// <summary>
    /// 시작할 때(캡처·자체 시험 제외, 정식 버전만): 끊긴 교체 복구 제안, 버린 폴더 지우기, 정리, 도우미 결과 알림, 3초 뒤 확인, 1시간 주기.
    /// </summary>
    public async Task StartAsync()
    {
        if (!CanNetwork || !Updater.CanSelfUpdate) return;
        try
        {
            Updater.DropStaleLock(dir);
            if (Updater.Diagnose(install) is { } fix && await OfferRepairAsync(fix)) return;
            var left = await Task.Run(() => Updater.EmptyTrash(install));
            if (left.Count > 0) await main.Show(new AskVm("남긴 폴더", left, [new AskButton("ok", "닫기", Primary: true, Default: true)]));
            await Task.Run(() => Updater.Cleanup(dir, install, current));
            RefreshPending();
            switch (Updater.TakeResult(dir))
            {
                case null: break;
                case "ok": Toast = $"적용 {VersionText}"; break;
                case "ok-rollback": Toast = $"되돌림 {VersionText}"; break;
                case "ok-recover": Toast = $"복구 {VersionText}"; break;
                case var w: await Ask("업데이트 실패", ResultWord(w)); break;
            }
            if (!s.UpdateAuto) return;
            await Task.Delay(3000); // 시작 화면 그리기와 겹치지 않게
            await CheckAsync(Origin.Start);
            if (s.UpdateAuto) hourly.Start();
        }
        catch (Exception e) { Fail(Word(e)); }
    }

    static string ResultWord(string w) => w switch
    {
        "none" => "결과 없음 · 도우미가 끝나지 않음",
        "fail-tasklist" => "tasklist 실패",
        "fail-wait" => "앱이 끝나지 않음",
        "fail-src" => "받은 버전 없음",
        "fail-old" => "직전 버전 폴더 못 옮김",
        "fail-notours" => "우리 것 아닌 폴더",
        "fail-busy" => "설치 폴더 사용 중",
        "fail-noold" => "직전 버전 없음",
        "fail-exists" => "설치 폴더 있음",
        "fail-lost" => "복구 실패 · .old 확인",
        _ => w,
    };

    /// <summary>교체가 끊긴 흔적: 옮길 경로를 보이고 [나중에](기본)·[복구]. 복구는 적용·되돌리기와 같은 검사를 거친다. 도우미를 띄웠으면 true.</summary>
    async Task<bool> OfferRepairAsync(Repair fix)
    {
        var title = fix.Mode == "recover" ? "설치 폴더 없음" : "설치 불완전";
        var key = await main.Show(new AskVm(title, [fix.Source, $"→ {fix.Target}"],
            [new AskButton("later", "나중에", Default: true), new AskButton("fix", "복구", Primary: true)]));
        if (key != "fix") return false;
        if (!await LaunchCheckedAsync(fix.Mode, fix.Target, fix.Mode == "recover" ? fix.Source : null, start: true)) return false;
        CloseForUpdate?.Invoke();
        return true;
    }

    enum Origin { Start, Tick, Manual }

    async Task CheckAsync(Origin from)
    {
        if (!CanNetwork || state is State.Checking or State.Downloading) return;
        Now = State.Checking;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(75)); // 방법 셋 + 프록시 한 번 더 + git 태그 읽기까지
            var r = await Updater.GetLatestAsync(cts.Token);
            if (s.UpdateRoute != Updater.Route.ToString()) { s.UpdateRoute = Updater.Route.ToString(); s.Save(); }
            checkedAt = DateTime.Now;
            latest = r;
            failure = null;
            if (Updater.CanSelfUpdate) pending = Updater.Pending(install, current);
            if (pending != null && (r == null || pending >= r.Version)) { Now = State.Ready; return; }
            if (r == null || r.Version <= current) { Now = State.None; return; }
            Now = State.Available;
            var v = r.Version.ToString(3);
            if (from == Origin.Manual || v == shown || v == s.UpdateSkip) return;
            shown = v;
            // 받을 수 없는 릴리스(매니페스트 없음)는 [받기] 팝업 없이 토스트만
            if (from == Origin.Start && r.CanDownload && !main.HasDialog) await OfferAsync(r);
            else Toast = $"새 버전 {v}";
        }
        finally
        {
            if (state == State.Checking) Fail(failure ?? "확인");
        }
    }

    /// <summary>이 릴리스가 요구하는 .NET 이 이 PC 에 없으면 알림 낱말(받기·교체를 하지 않는다), 아니면 null.</summary>
    static string? RuntimeWord(ReleaseInfo r) => Updater.RuntimeNeeded(r) is { } n ? $"런타임 필요 · .NET {n} Desktop" : null;

    /// <summary>받아 둔 버전이 요구하는 .NET 이 이 PC 에 없으면 알림 낱말(교체하지 않는다), 아니면 null.</summary>
    string? RuntimeBlocked() => latest is { } r && pending == r.Version ? RuntimeWord(r) : null;

    /// <summary>시작 팝업: 새 버전 · 현재 버전 · [나중에][받기].</summary>
    async Task OfferAsync(ReleaseInfo r)
    {
        var key = await main.Show(new AskVm("새 버전", [$"새 버전 {r.Version.ToString(3)}", $"현재 버전 {VersionText}"],
            [new AskButton("later", "나중에"), new AskButton("get", "받기", Primary: true, Default: true)]));
        if (key == "get") await DownloadAsync();
    }

    // ---- 받기 ----

    /// <summary>
    /// 이 PC·이 버전에서 할 수 있는가. 아니면 팝업. mode = prepare · apply · rollback(<see cref="Updater.Blockers"/>) · recover(<see cref="Updater.RecoverBlockers"/>).
    /// target = 설치 폴더(recover 면 되살릴 자리), source = recover 로 옮길 폴더.
    /// </summary>
    async Task<bool> AllowedAsync(string mode, string target, string? source = null)
    {
        if (!Updater.CanSelfUpdate) { await Ask("업데이트 불가", "로컬 빌드"); return false; }
        if (!Updater.CanReplace(mode == "recover" ? source! : target)) { await Ask("쓰기 권한 없음", target); return false; }
        if (Updater.DataInside(target, [Updater.DataDir, Settings.FilePath, s.LastRepo, main.Repo, main.Site?.Folder]) is { } inside)
        {
            await Ask("사용자 데이터가 설치 폴더 안", inside);
            return false;
        }
        var why = await Task.Run(() => mode == "recover" ? Updater.RecoverBlockers(source!, target) : Updater.Blockers(target, mode));
        if (why.Count > 0) { await main.Show(new AskVm("막힘", why, [new AskButton("ok", "닫기", Primary: true, Default: true)])); return false; }
        return true;
    }

    async Task DownloadAsync()
    {
        if (!CanNetwork || latest is not { } r || state == State.Downloading) return;
        if (!r.CanDownload) { Toast = $"받기 불가 · 매니페스트 없음 {r.Tag}"; return; }
        if (!await AllowedAsync("prepare", install)) return;
        if (Updater.SpaceShort(r, install, dir) is { } space) { await Ask("공간 부족", space); return; }
        var gate = Updater.TryGate(dir);
        if (gate == null) { await Ask("받기 불가", "다른 창이 받는 중"); return; }
        var cts = dl = new CancellationTokenSource();
        Progress = 0;
        Now = State.Downloading;
        try
        {
            await Updater.PrepareAsync(r, install, dir, new Progress<double>(p => Progress = p), cts.Token);
            pending = r.Version;
            s.UpdateSkip = null;
            s.Save();
            Now = State.Ready;
            Toast = $"준비됨 {r.Version.ToString(3)}";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { Now = State.Available; }
        catch (Exception e)
        {
            Fail("받기 · " + Word(e));
            await Ask("받기 실패", Word(e));
        }
        finally
        {
            gate.Dispose();
            if (state == State.Downloading) Now = State.Available;
            cts.Dispose();
            if (ReferenceEquals(dl, cts)) dl = null;
        }
    }

    async Task DiscardAsync()
    {
        using (var gate = Updater.TryGate(dir))
        {
            if (gate == null) { await Ask("버리기 불가", "다른 창이 받는 중"); return; }
            if (!await Task.Run(() => Updater.Discard(install))) { await Ask("버리기 불가", Path.GetFileName(Updater.NewDir(install))); return; }
        }
        pending = null;
        Now = latest is { } r && r.Version > current ? State.Available : State.None;
        Toast = "버림";
    }

    // ---- 적용·되돌리기·복구 ----

    /// <summary>받아 둔 버전이 있는가(닫을 때 적용). .new 를 다시 본다(다른 창이 받았을 수 있다).</summary>
    public bool HasPending
    {
        get
        {
            if (!Updater.CanSelfUpdate || s.Volatile) return false;
            try { return (pending = Updater.Pending(install, current)) != null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>옮길 폴더들(설치·.old·.new·.trash-*)에서 도는 다른 프로그램(tedit, 옛 버전 등)이 끝날 때까지 팝업으로 묻는다. 계속해도 되는가.</summary>
    async Task<bool> NotBusyAsync(IReadOnlyList<string> dirs)
    {
        while (true)
        {
            var busy = Updater.RunningIn(dirs, Environment.ProcessId);
            if (busy.Count == 0) return true;
            var lines = busy.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Select(g => g.Count() > 1 ? $"{g.Key} {g.Count()}" : g.Key).ToArray();
            var key = await main.Show(new AskVm("실행 중", lines,
                [new AskButton("later", "나중에"), new AskButton("again", "다시 확인", Primary: true, Default: true)]));
            if (key != "again") return false;
        }
    }

    /// <summary>검사(권한·사용자 데이터·막힘·실행 중)를 모두 거친 뒤 도우미를 띄운다(이 프로세스가 끝나길 기다렸다가). 띄웠는가.</summary>
    async Task<bool> LaunchCheckedAsync(string mode, string target, string? source, bool start)
    {
        if (!await AllowedAsync(mode, target, source)) return false;
        IReadOnlyList<string> family = mode == "recover" ? [source!, .. Updater.Family(target)] : Updater.Family(target);
        if (!await NotBusyAsync(family)) return false;
        var helperDir = Updater.UpdateDirFor(target);
        try
        {
            Updater.StartHelper(Updater.HelperStart(mode, target, source, Environment.ProcessId, start, helperDir), helperDir).Dispose();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or UpdateException or System.ComponentModel.Win32Exception)
        {
            await Ask("도우미 실패", e.Message);
            return false;
        }
    }

    /// <summary>창을 닫을 때(받아 둔 버전이 있으면): 도우미를 띄우고 끝낸다. 다시 열지 않는다. 막히면 팝업 뒤 그대로 닫는다.</summary>
    public async Task ApplyOnCloseAsync()
    {
        try { if (HasPending && RuntimeBlocked() == null) await LaunchCheckedAsync("apply", install, Updater.NewDir(install), start: false); }
        catch (Exception e) { failure = Word(e); } // 닫기는 막지 않는다
    }

    /// <summary>[닫고 적용]: 저장 확인 → 도우미(끝나면 새 버전을 연다) → 창 닫기.</summary>
    async Task ApplyNowAsync()
    {
        if (!HasPending) { RefreshPending(); await Ask("받은 버전 없음"); return; }
        if (RuntimeBlocked() is { } need) { await Ask("업데이트 불가", need); return; }
        if (!await main.ConfirmLeaveAsync()) return;
        if (await LaunchCheckedAsync("apply", install, Updater.NewDir(install), start: true)) CloseForUpdate?.Invoke();
    }

    /// <summary>[되돌리기]: 직전 버전(.old)으로. 현재 버전은 자동으로 다시 알리지 않는다.</summary>
    async Task RollbackAsync()
    {
        if (OldVersion is not { } old) return;
        var key = await main.Show(new AskVm("되돌리기", [$"현재 버전 {VersionText}", $"직전 버전 {old}"],
            [new AskButton("cancel", "취소", Default: true), new AskButton("ok", "되돌리기", Primary: true)]));
        if (key != "ok" || !await main.ConfirmLeaveAsync()) return;
        if (!await LaunchCheckedAsync("rollback", install, null, start: true)) return;
        s.UpdateSkip = VersionText;
        s.Save();
        CloseForUpdate?.Invoke();
    }

    // ---- 캡처 훅 ----

    /// <summary>
    /// <c>--shot … --fake-update start|tick</c>: 네트워크 없이 현재 버전을 0.0.1 로 낮추고 이 버전을 새 버전으로 둔다.
    /// start = 시작 팝업, tick = 주기 확인 토스트.
    /// </summary>
    public void Fake(string mode)
    {
        current = new Version(0, 0, 1);
        latest = new ReleaseInfo(Updater.Current, $"TableEditor-v{Updater.Current.ToString(3)}-net10-x64.zip", new string('0', 64), 98L << 20,
            $"{Updater.Releases}/tag/v{Updater.Current.ToString(3)}", "", DateTimeOffset.Now);
        checkedAt = DateTime.Now;
        foreach (var n in new[] { nameof(VersionText), nameof(BuildText) }) Raise(n);
        Now = State.Available;
        shown = latest.Version.ToString(3);
        if (mode == "start") _ = OfferAsync(latest);
        else Toast = $"새 버전 {shown}";
    }
}
