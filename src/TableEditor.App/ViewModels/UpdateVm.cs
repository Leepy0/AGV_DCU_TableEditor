using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Ops;

namespace TableEditor.App.ViewModels;

/// <summary>
/// 프로그램 업데이트(설정 화면의 업데이트 칸 + 위 줄 칩 + 토스트). 참고: AmrMapEditor MainWindow.AppUpdate.
/// 시작 3초 뒤 확인 → 새 판이면 팝업, 그 뒤 1시간마다 확인 → 처음 보는 새 판이면 토스트.
/// 받기 → &lt;설치&gt;.new 완성 → 닫을 때(또는 [닫고 적용]) 바깥 도우미가 이름 바꾸기 둘로 교체.
/// 받아 둔 판은 창마다 기억하지 않고 .new 폴더가 기준이다(여러 창). 캡처·자체 시험(<see cref="Settings.Volatile"/>)은 네트워크를 쓰지 않는다.
/// 업데이트 때문에 앱이 꺼지지 않게 명령·주기 확인의 예외는 모두 잡아 실패 칩으로 둔다.
/// </summary>
public sealed class UpdateVm : Observable
{
    public enum State { None, Checking, Available, Downloading, Ready, Failed }

    readonly MainVm main;
    readonly Settings s;
    readonly DispatcherTimer hourly = new() { Interval = TimeSpan.FromHours(1) };
    readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly string install = Updater.InstallDir;
    readonly string dir = Updater.UpdateDir;
    Version current = Updater.Current;
    Version? pending;
    ReleaseInfo? latest;
    CancellationTokenSource? dl;
    string? shown; // 팝업·토스트로 이미 알린 판
    string? failure;
    DateTime? checkedAt;

    // 받기·.new 만들기는 창 하나만(이름 붙인 세마포: 스레드에 묶이지 않는다)
    static readonly Semaphore Gate = new(1, 1, @"Local\AGV_DCU_TableEditor_update");

    public UpdateVm(MainVm main, Settings s)
    {
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
        OpenInfo = new Command(() => { Toast = null; main.Tab = main.SettingsTab; });
        if (Updater.CanSelfUpdate && !s.Volatile) Safe(() => { RefreshPending(); return Task.CompletedTask; });
    }

    /// <summary>창을 닫아 달라(도우미를 띄운 뒤). MainWindow 가 단다.</summary>
    public event Action? CloseForUpdate;

    ICommand Cmd(Func<Task> run, Func<bool> can) => new Command(() => Safe(run), can);

    /// <summary>명령·주기 확인 래퍼: 예외는 실패 칩으로(앱은 계속 돈다).</summary>
    async void Safe(Func<Task> run)
    {
        try { await run(); }
        catch (Exception e)
        {
            failure = Word(e);
            Now = State.Failed;
            Raise(nameof(ChipText));
        }
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
            foreach (var n in new[] { nameof(ChipText), nameof(HasChip), nameof(IsDownloading), nameof(CheckPrimary), nameof(DownloadPrimary), nameof(ApplyPrimary), nameof(ChipAccent) })
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
        State.Available => $"새 판 {latest?.Version.ToString(3)}",
        State.Downloading => $"받는 중 {Progress:0}%",
        State.Ready => $"준비됨 {pending?.ToString(3)}",
        State.Failed => $"실패 · {failure}",
        _ => checkedAt is { } t ? $"최신 · {t:HH:mm}" : "확인 안 함",
    };

    /// <summary>위 줄 칩: 할 일이 있을 때만.</summary>
    public bool HasChip => state is State.Available or State.Downloading or State.Ready;
    public bool ChipAccent => state is State.Available or State.Ready;
    public bool IsDownloading => state == State.Downloading;

    // 강조 = 색만: 지금 누를 단추 하나
    public bool CheckPrimary => state is State.None or State.Failed;
    public bool DownloadPrimary => state == State.Available;
    public bool ApplyPrimary => state == State.Ready;

    /// <summary>.old 판(되돌릴 곳). 시작할 때 한 번 읽는다(명령 재질의마다 파일을 읽지 않게).</summary>
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
    /// 시작할 때(캡처·자체 시험 제외, 정식 판만): 끊긴 교체 복구 제안, 정리, 도우미 결과 알림, 3초 뒤 확인, 1시간 주기.
    /// </summary>
    public async Task StartAsync()
    {
        if (!CanNetwork || !Updater.CanSelfUpdate) return;
        try
        {
            Updater.DropStaleLock(dir);
            if (Updater.Diagnose(install) is { } fix && await OfferRepairAsync(fix)) return;
            Updater.Cleanup(dir, install, current);
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
        catch (Exception e)
        {
            failure = Word(e);
            Now = State.Failed;
            Raise(nameof(ChipText));
        }
    }

    static string ResultWord(string w) => w switch
    {
        "none" => "결과 없음 · 도우미가 끝나지 않음",
        "fail-tasklist" => "tasklist 실패",
        "fail-wait" => "앱이 끝나지 않음",
        "fail-src" => "받은 판 없음",
        "fail-old" => ".old 못 지움",
        "fail-notours" => "우리 것 아닌 폴더",
        "fail-busy" => "설치 폴더 사용 중",
        "fail-noold" => ".old 없음",
        "fail-exists" => "설치 폴더 있음",
        "fail-lost" => "복구 실패 · .old 확인",
        _ => w,
    };

    /// <summary>교체가 끊긴 흔적: [나중에][복구]. 복구 도우미를 띄웠으면 true(창을 닫는다).</summary>
    async Task<bool> OfferRepairAsync(Repair fix)
    {
        var title = fix.Mode == "recover" ? "설치 폴더 없음" : "설치 불완전";
        var key = await main.Show(new AskVm(title, [$"{Path.GetFileName(fix.Source)} → {Path.GetFileName(fix.Target)}"],
            [new AskButton("later", "나중에"), new AskButton("fix", "복구", Primary: true, Default: true)]));
        if (key != "fix") return false;
        if (!Launch(fix.Mode, fix.Target, fix.Mode == "recover" ? fix.Source : null, start: true, out var error))
        {
            await Ask("도우미 실패", error ?? "");
            return false;
        }
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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var r = await Updater.GetLatestAsync(cts.Token);
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
            if (from == Origin.Start && !main.HasDialog) await OfferAsync(r);
            else Toast = $"새 판 {v}";
        }
        finally
        {
            if (state == State.Checking) { failure ??= "확인"; Now = State.Failed; }
        }
    }

    /// <summary>시작 팝업: 새 판 · 지금 판 · [나중에][받기].</summary>
    async Task OfferAsync(ReleaseInfo r)
    {
        var key = await main.Show(new AskVm("새 판", [$"새 판 {r.Version.ToString(3)}", $"지금 {VersionText}"],
            [new AskButton("later", "나중에"), new AskButton("get", "받기", Primary: true, Default: true)]));
        if (key == "get") await DownloadAsync();
    }

    // ---- 받기 ----

    /// <summary>이 PC·이 판에서 할 수 있는가. 아니면 팝업. mode = <see cref="Updater.Blockers"/>.</summary>
    async Task<bool> AllowedAsync(string mode)
    {
        if (!Updater.CanSelfUpdate) { await Ask("업데이트 불가", "로컬 빌드"); return false; }
        if (!Updater.CanReplace(install)) { await Ask("쓰기 권한 없음", install); return false; }
        if (Updater.DataInside(install, [Updater.DataDir, Settings.FilePath, s.LastRepo, main.Repo, main.Site?.Folder]) is { } inside)
        {
            await Ask("사용자 데이터가 설치 폴더 안", inside);
            return false;
        }
        var why = await Task.Run(() => Updater.Blockers(install, mode));
        if (why.Count > 0) { await main.Show(new AskVm("막힘", why, [new AskButton("ok", "닫기", Primary: true, Default: true)])); return false; }
        return true;
    }

    async Task DownloadAsync()
    {
        if (latest is not { } r || state == State.Downloading) return;
        if (!r.CanDownload) { Toast = $"받기 불가 · 매니페스트 없음 {r.Tag}"; return; }
        if (!await AllowedAsync("prepare")) return;
        if (Updater.SpaceShort(r, install, dir) is { } space) { await Ask("공간 부족", space); return; }
        if (!Gate.WaitOne(0)) { await Ask("받기 불가", "다른 창이 받는 중"); return; }
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
            failure = "받기 · " + Word(e);
            Now = State.Failed;
            Raise(nameof(ChipText));
            await Ask("받기 실패", Word(e));
        }
        finally
        {
            Gate.Release();
            if (state == State.Downloading) Now = State.Available;
            cts.Dispose();
            if (ReferenceEquals(dl, cts)) dl = null;
        }
    }

    async Task DiscardAsync()
    {
        if (!Gate.WaitOne(0)) { await Ask("버리기 불가", "다른 창이 받는 중"); return; }
        bool done;
        try { done = await Task.Run(() => Updater.Discard(install)); }
        finally { Gate.Release(); }
        if (!done) { await Ask("버리기 불가", Path.GetFileName(Updater.NewDir(install)) + " 우리 것 아님"); return; }
        pending = null;
        Now = latest is { } r && r.Version > current ? State.Available : State.None;
        Toast = "버림";
    }

    // ---- 적용·되돌리기 ----

    /// <summary>받아 둔 판이 있는가(닫을 때 적용). .new 를 다시 본다(다른 창이 받았을 수 있다).</summary>
    public bool HasPending
    {
        get
        {
            if (!Updater.CanSelfUpdate || s.Volatile) return false;
            try { return (pending = Updater.Pending(install, current)) != null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>설치 폴더에서 도는 다른 프로그램(tedit 등)이 끝날 때까지 팝업으로 묻는다. 계속해도 되는가.</summary>
    async Task<bool> NotBusyAsync()
    {
        while (true)
        {
            var busy = Updater.RunningIn(install, Environment.ProcessId);
            if (busy.Count == 0) return true;
            var lines = busy.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Select(g => g.Count() > 1 ? $"{g.Key} {g.Count()}" : g.Key).ToArray();
            var key = await main.Show(new AskVm("실행 중", lines,
                [new AskButton("later", "나중에"), new AskButton("again", "다시 확인", Primary: true, Default: true)]));
            if (key != "again") return false;
        }
    }

    /// <summary>바깥 도우미를 띄운다(이 프로세스가 끝나길 기다렸다가). 띄웠는가.</summary>
    bool Launch(string mode, string target, string? source, bool start, out string? error)
    {
        error = null;
        try
        {
            Process.Start(Updater.HelperStart(mode, target, source, Environment.ProcessId, start, dir))?.Dispose();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            error = e.Message;
            return false;
        }
    }

    async Task<bool> LaunchCheckedAsync(string mode, bool start)
    {
        if (!await AllowedAsync(mode) || !await NotBusyAsync()) return false;
        if (Launch(mode, install, mode == "apply" ? Updater.NewDir(install) : null, start, out var error)) return true;
        await Ask("도우미 실패", error ?? "");
        return false;
    }

    /// <summary>창을 닫을 때(받아 둔 판이 있으면): 도우미를 띄우고 끝낸다. 다시 열지 않는다. 막히면 팝업 뒤 그대로 닫는다.</summary>
    public async Task ApplyOnCloseAsync()
    {
        try { if (HasPending) await LaunchCheckedAsync("apply", start: false); }
        catch (Exception e) { failure = Word(e); } // 닫기는 막지 않는다
    }

    /// <summary>[닫고 적용]: 저장 확인 → 도우미(끝나면 새 판을 연다) → 창 닫기.</summary>
    async Task ApplyNowAsync()
    {
        if (!HasPending) { RefreshPending(); await Ask("받은 판 없음"); return; }
        if (!await main.ConfirmLeaveAsync()) return;
        if (await LaunchCheckedAsync("apply", start: true)) CloseForUpdate?.Invoke();
    }

    /// <summary>[되돌리기]: .old 판으로. 지금 판은 자동으로 다시 알리지 않는다.</summary>
    async Task RollbackAsync()
    {
        if (OldVersion is not { } old) return;
        var key = await main.Show(new AskVm("되돌리기", [$"{VersionText} → {old}"],
            [new AskButton("cancel", "취소", Default: true), new AskButton("ok", "되돌리기", Primary: true)]));
        if (key != "ok" || !await main.ConfirmLeaveAsync()) return;
        if (!await LaunchCheckedAsync("rollback", start: true)) return;
        s.UpdateSkip = VersionText;
        s.Save();
        CloseForUpdate?.Invoke();
    }

    // ---- 캡처 훅 ----

    /// <summary>
    /// <c>--shot … --fake-update start|tick</c>: 네트워크 없이 지금 판을 0.0.1 로 낮추고 이 판을 새 판으로 둔다.
    /// start = 시작 팝업, tick = 주기 확인 토스트.
    /// </summary>
    public void Fake(string mode)
    {
        current = new Version(0, 0, 1);
        latest = new ReleaseInfo(Updater.Current, $"TableEditor-v{Updater.Current.ToString(3)}-win-x64.zip", new string('0', 64), 98L << 20,
            $"{Updater.Releases}/tag/v{Updater.Current.ToString(3)}", "", DateTimeOffset.Now);
        checkedAt = DateTime.Now;
        foreach (var n in new[] { nameof(VersionText), nameof(BuildText) }) Raise(n);
        Now = State.Available;
        shown = latest.Version.ToString(3);
        if (mode == "start") _ = OfferAsync(latest);
        else Toast = $"새 판 {shown}";
    }
}
