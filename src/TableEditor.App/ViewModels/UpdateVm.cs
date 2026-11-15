using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using System.Windows.Threading;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Ops;

namespace TableEditor.App.ViewModels;

/// <summary>
/// 프로그램 업데이트(설정 화면의 업데이트 칸 + 위 줄 칩 + 토스트). 참고: AmrMapEditor MainWindow.AppUpdate.
/// 시작 3초 뒤 확인 → 새 판이면 팝업, 그 뒤 1시간마다 확인 → 처음 보는 새 판이면 토스트. 받기 → zip 검증·스테이징 → 닫을 때(또는 [닫고 적용]) 바깥 도우미가 교체.
/// 캡처·자체 시험(<see cref="Settings.Volatile"/>)은 네트워크를 쓰지 않는다.
/// </summary>
public sealed class UpdateVm : Observable
{
    public enum State { None, Checking, Available, Downloading, Ready, Failed }

    readonly MainVm main;
    readonly Settings s;
    readonly DispatcherTimer hourly = new() { Interval = TimeSpan.FromHours(1) };
    readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    Version current = Updater.Current;
    ReleaseInfo? latest;
    CancellationTokenSource? dl;
    string? shown; // 팝업·토스트로 이미 알린 판
    DateTime? checkedAt;

    public UpdateVm(MainVm main, Settings s)
    {
        this.main = main;
        this.s = s;
        hourly.Tick += async (_, _) => await CheckAsync(Origin.Tick);
        toastTimer.Tick += (_, _) => Toast = null;
        CheckNow = new Command(() => _ = CheckAsync(Origin.Manual), () => state is not (State.Checking or State.Downloading));
        Download = new Command(() => _ = DownloadAsync(), () => state == State.Available);
        CancelDownload = new Command(() => dl?.Cancel(), () => state == State.Downloading);
        ApplyNow = new Command(() => _ = ApplyNowAsync(), () => state == State.Ready);
        Rollback = new Command(() => _ = RollbackAsync(), () => OldVersion != null && state != State.Downloading);
        OpenInfo = new Command(() => { Toast = null; main.Tab = main.SettingsTab; });
        if (Updater.PendingOk(s.UpdatePending, current)) state = State.Ready;
    }

    /// <summary>창을 닫아 달라(도우미를 띄운 뒤). MainWindow 가 단다.</summary>
    public event Action? CloseForUpdate;

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
        State.Ready => $"준비됨 {s.UpdatePending?.Version}",
        State.Failed => "확인 실패",
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
    public ICommand Rollback { get; }
    public ICommand OpenInfo { get; }

    bool CanNetwork => !s.Volatile;

    Task<string?> Ask(string title, params string[] lines) =>
        main.Show(new AskVm(title, lines, [new AskButton("ok", "닫기", Primary: true, Default: true)]));

    // ---- 시작 ----

    /// <summary>시작할 때(캡처·자체 시험 제외): 도우미 결과 알림, 정리, 3초 뒤 확인, 1시간 주기.</summary>
    public async Task StartAsync()
    {
        if (!CanNetwork) return;
        Updater.Cleanup(Updater.UpdateDir, Updater.PendingOk(s.UpdatePending, current) ? s.UpdatePending!.Stage : null);
        if (!Updater.PendingOk(s.UpdatePending, current) && s.UpdatePending != null) { s.UpdatePending = null; s.Save(); }
        TryDeleteDir(Updater.InstallDir + ".rollback");
        switch (Updater.TakeResult(Updater.UpdateDir))
        {
            case null: break;
            case "ok": Toast = $"적용 {VersionText}"; break;
            case "ok-rollback": Toast = $"되돌림 {VersionText}"; break;
            case var w: _ = Ask("업데이트 실패", ResultWord(w)); break;
        }
        if (!Updater.CanSelfUpdate || !s.UpdateAuto) return;
        await Task.Delay(3000); // 시작 화면 그리기와 겹치지 않게
        await CheckAsync(Origin.Start);
        if (s.UpdateAuto) hourly.Start();
    }

    static string ResultWord(string w) => w switch
    {
        "fail-wait" => "앱이 끝나지 않음",
        "fail-src" => "받은 판 없음",
        "fail-old" => ".old 못 지움",
        "fail-busy" => "설치 폴더 사용 중",
        "fail-copy" => "복사 실패 · 원래 판으로 복구",
        "fail-noold" => ".old 없음",
        "fail-lost" => "복구 실패 · .old 확인",
        _ => w,
    };

    enum Origin { Start, Tick, Manual }

    async Task CheckAsync(Origin from)
    {
        if (!CanNetwork || state is State.Checking or State.Downloading) return;
        var before = state;
        Now = State.Checking;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var r = await Updater.GetLatestAsync(cts.Token);
            checkedAt = DateTime.Now;
            latest = r;
            if (r == null || r.Version <= current) { Now = Updater.PendingOk(s.UpdatePending, current) ? State.Ready : State.None; return; }
            if (Updater.PendingOk(s.UpdatePending, current) && Updater.TryParseVersion(s.UpdatePending!.Version, out var pv) && pv >= r.Version) { Now = State.Ready; return; }
            Now = State.Available;
            var v = r.Version.ToString(3);
            if (from == Origin.Manual || v == shown || v == s.UpdateSkip) return;
            shown = v;
            if (from == Origin.Start && !main.HasDialog) await OfferAsync(r);
            else Toast = $"새 판 {v}";
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or System.Text.Json.JsonException or UpdateException)
        {
            // 자동 확인 실패는 칩에만(오프라인·프록시)
            Now = before is State.Ready ? State.Ready : State.Failed;
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

    /// <summary>이 PC·이 판에서 교체할 수 있는가. 아니면 팝업. data = 설치 폴더 밖이어야 할 곳.</summary>
    async Task<bool> AllowedAsync()
    {
        var install = Updater.InstallDir;
        if (!Updater.CanSelfUpdate) { await Ask("업데이트 불가", "로컬 빌드"); return false; }
        if (!Updater.CanReplace(install)) { await Ask("쓰기 권한 없음", install); return false; }
        if (Updater.DataInside(install, [Updater.DataDir, Settings.FilePath, s.LastRepo, main.Repo, main.Site?.Folder]) is { } inside)
        {
            await Ask("사용자 데이터가 설치 폴더 안", inside);
            return false;
        }
        return true;
    }

    async Task DownloadAsync()
    {
        if (latest is not { } r || state == State.Downloading || !await AllowedAsync()) return;
        if (!r.CanDownload) { await Ask("받기 불가", "매니페스트 없음", r.Tag); return; }
        var cts = dl = new CancellationTokenSource();
        Progress = 0;
        Now = State.Downloading;
        try
        {
            // 같은 판을 이미 풀어 두었으면(다른 창이 받음) 그대로
            var root = Updater.StageRoot(Updater.StageDir(Updater.UpdateDir, r.Version), r.Version);
            if (root == null)
            {
                var zip = await Updater.DownloadAsync(r, Updater.UpdateDir, new Progress<double>(p => Progress = p), cts.Token);
                root = await Task.Run(() => Updater.Stage(zip, r.Version, Updater.UpdateDir), cts.Token);
            }
            s.UpdatePending = new PendingUpdate(r.Version.ToString(3), root, r.Sha256!);
            s.UpdateSkip = null;
            s.Save();
            Now = State.Ready;
            Toast = $"준비됨 {r.Version.ToString(3)}";
        }
        catch (OperationCanceledException) { Now = State.Available; }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or UpdateException)
        {
            Now = State.Available;
            await Ask("받기 실패", e is UpdateException ? e.Message : e is HttpRequestException ? "연결 실패" : "파일 오류");
        }
        finally
        {
            cts.Dispose();
            if (ReferenceEquals(dl, cts)) dl = null;
        }
    }

    // ---- 적용·되돌리기 ----

    /// <summary>받아 둔 판이 있는가(닫을 때 적용).</summary>
    public bool HasPending => Updater.CanSelfUpdate && Updater.PendingOk(s.UpdatePending, current);

    /// <summary>설치 폴더에서 도는 다른 프로그램(tedit 등)이 끝날 때까지 팝업으로 묻는다. 계속해도 되는가.</summary>
    async Task<bool> NotBusyAsync()
    {
        while (true)
        {
            var busy = Updater.RunningIn(Updater.InstallDir, Environment.ProcessId);
            if (busy.Count == 0) return true;
            var lines = busy.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Select(g => g.Count() > 1 ? $"{g.Key} {g.Count()}" : g.Key).ToArray();
            var key = await main.Show(new AskVm("실행 중", lines,
                [new AskButton("later", "나중에"), new AskButton("again", "다시 확인", Primary: true, Default: true)]));
            if (key != "again") return false;
        }
    }

    /// <summary>바깥 도우미를 띄운다(이 프로세스가 끝나길 기다렸다가 교체). 띄웠는가. 실패는 팝업.</summary>
    async Task<bool> LaunchAsync(string? source, bool start)
    {
        if (!await AllowedAsync() || !await NotBusyAsync()) return false;
        try
        {
            Process.Start(Updater.HelperStart(Updater.InstallDir, source, Environment.ProcessId, start, Updater.UpdateDir))?.Dispose();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            await Ask("도우미 실패", e.Message);
            return false;
        }
    }

    /// <summary>창을 닫을 때(받아 둔 판이 있으면): 도우미를 띄우고 끝낸다. 다시 열지 않는다.</summary>
    public Task<bool> ApplyOnCloseAsync() => HasPending ? LaunchAsync(s.UpdatePending!.Stage, start: false) : Task.FromResult(false);

    /// <summary>[닫고 적용]: 저장 확인 → 도우미(끝나면 새 판을 연다) → 창 닫기.</summary>
    async Task ApplyNowAsync()
    {
        if (!HasPending) { Now = State.None; await Ask("받은 판 없음"); return; }
        if (!await main.ConfirmLeaveAsync()) return;
        if (await LaunchAsync(s.UpdatePending!.Stage, start: true)) CloseForUpdate?.Invoke();
    }

    /// <summary>[되돌리기]: .old 판으로. 지금 판은 자동으로 다시 알리지 않는다.</summary>
    async Task RollbackAsync()
    {
        if (OldVersion is not { } old) return;
        var key = await main.Show(new AskVm("되돌리기", [$"{VersionText} → {old}"],
            [new AskButton("cancel", "취소", Default: true), new AskButton("ok", "되돌리기", Primary: true)]));
        if (key != "ok" || !await main.ConfirmLeaveAsync()) return;
        if (!await LaunchAsync(null, start: true)) return;
        s.UpdateSkip = VersionText;
        s.UpdatePending = null;
        s.Save();
        CloseForUpdate?.Invoke();
    }

    // ---- 캡처 훅 ----

    /// <summary>
    /// <c>--fake-update start|tick</c>: 네트워크 없이 지금 판을 0.0.1 로 낮추고 이 판을 새 판으로 둔다.
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

    static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
