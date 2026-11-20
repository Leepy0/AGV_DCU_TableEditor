using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Check;
using TableEditor.Engine.Model;
using TableEditor.Engine.Ops;
using TableEditor.Engine.Text;

namespace TableEditor.App.ViewModels;

/// <summary>왼쪽 탭 하나. Group: 0 = 표, 1 = 이력·빌드, 2 = 설정(아래).</summary>
public sealed class TabVm(string name, int group, MainVm owner) : Observable
{
    public string Name { get; } = name;
    public int Group { get; } = group;

    int errors, warns;
    public int Errors { get => errors; set { if (Set(ref errors, value)) { Raise(nameof(HasErrors)); Raise(nameof(ShowWarns)); } } }
    public int Warns { get => warns; set { if (Set(ref warns, value)) { Raise(nameof(HasWarns)); Raise(nameof(ShowWarns)); } } }
    public bool HasErrors => Errors > 0;
    public bool HasWarns => Warns > 0;

    /// <summary>탭에는 한 가지만(오류가 있으면 오류 수). 둘 다 넣으면 탭 이름을 덮는다.</summary>
    public bool ShowWarns => HasWarns && !HasErrors;

    public override string ToString() => Name;

    public bool IsSelected
    {
        get => owner.Tab == this;
        set { if (value) owner.Tab = this; }
    }

    internal void Changed() => Raise(nameof(IsSelected));
}

/// <summary>검사 목록 한 줄: 아이콘 + 규칙 번호 + 글 + 위치.</summary>
public sealed record FindingVm(Finding Finding, string At, TabVm? Tab)
{
    public bool IsError => Finding.Severity == Severity.Error;
    public string Rule => Finding.Rule;
    public string Text => Finding.Text;
    public bool CanFix => Finding.Replace != null;
}

/// <summary>
/// 창 전체: 위 줄, 왼쪽 탭, 검사 목록, 지금 탭의 화면, 팝업. 편집은 <see cref="EditHistory"/> 위에서 돌고(되돌리기 = 사이트 바꿔 끼우기),
/// 명령마다 사이트 전체를 다시 검사한다(설계서 4.5절).
/// </summary>
public sealed partial class MainVm : Observable
{
    public const string TagTab = "태그";
    public const string SettingsName = "설정";

    public Settings Settings { get; }
    public IReadOnlyList<TabVm> Tabs { get; }
    public IEnumerable<TabVm> TableTabs => Tabs.Where(t => t.Group == 0);
    public IEnumerable<TabVm> OpsTabs => Tabs.Where(t => t.Group == 1);
    public TabVm SettingsTab => Tabs[^1];
    public TagVm Tag { get; }
    public ProfileVm Profile { get; }
    public ManualVm Manual { get; }
    public SeqVm Seq { get; }
    public MapVm Map { get; }
    public IoVm Io { get; }
    public SettingsVm Config { get; }
    public UpdateVm Update { get; }

    public const string ProfileTab = "프로파일", IoTab = "IO 맵", SeqTab = "시퀀스", MapTab = "대응표", ManualTab = "수동";

    readonly DispatcherTimer draftTimer;
    readonly DispatcherTimer noticeTimer;

    public MainVm(Settings settings)
    {
        Settings = settings;
        Tabs = new[] { ("프로파일", 0), ("IO 맵", 0), (TagTab, 0), ("시퀀스", 0), ("대응표", 0), ("수동", 0), ("이력", 1), ("빌드", 1), (SettingsName, 2) }
            .Select(t => new TabVm(t.Item1, t.Item2, this)).ToList();
        tab = Tabs.First(t => t.Name == TagTab);
        Tag = new TagVm(this);
        Profile = new ProfileVm(this);
        Manual = new ManualVm(this);
        Seq = new SeqVm(this);
        Map = new MapVm(this);
        Io = new IoVm(this);
        Config = new SettingsVm(settings, this);
        Update = new UpdateVm(this, settings);
        GoTo = new Command(p => { if (p is FindingVm f) Go(f); });
        Fix = new Command(p => { if (p is FindingVm f) FixFinding(f.Finding); }, _ => CanEdit);
        ToggleCheck = new Command(() => CheckOpen = !CheckOpen);
        Undo = new Command(() => Step(h => h.Undo()), () => CanEdit && history?.CanUndo == true);
        Redo = new Command(() => Step(h => h.Redo()), () => CanEdit && history?.CanRedo == true);
        Save = new Command(() => _ = SaveAsync(), () => CanEdit && op != "save");
        InitOps();
        draftTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        draftTimer.Tick += (_, _) => { draftTimer.Stop(); WriteDraft(); };
        noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        noticeTimer.Tick += (_, _) => { noticeTimer.Stop(); Notice = null; };
    }

    // ---- 열기 ----

    string? repo;
    /// <summary>앱 루트(저장소). 사이트 폴더만 열었으면 null(깃·빌드 꺼짐).</summary>
    public string? Repo { get => repo; private set { if (Set(ref repo, value)) Raise(nameof(HasRepo)); } }
    public bool HasRepo => Repo != null;

    IReadOnlyList<string> sites = [];
    public IReadOnlyList<string> Sites { get => sites; private set => Set(ref sites, value); }

    string? siteName;
    /// <summary>사이트 드롭다운. 바꾸면(저장 안 한 변경은 물어본 뒤) 그 사이트를 연다.</summary>
    public string? SiteName
    {
        get => siteName;
        set
        {
            if (value == null || value == siteName || Repo == null) return;
            _ = SwitchSiteAsync(value);
        }
    }

    async Task SwitchSiteAsync(string name)
    {
        var old = siteName;
        if (!await ConfirmLeaveAsync()) { siteName = old; Raise(nameof(SiteName)); return; }
        siteName = name;
        Raise(nameof(SiteName));
        await TryOpen(() => OpenRepoSiteAsync(Path.Combine(Repo!, "site", name)));
    }

    bool empty = true;
    /// <summary>열린 사이트가 없다(폴더 고르기 버튼만).</summary>
    public bool IsEmpty { get => empty; private set => Set(ref empty, value); }

    /// <summary>저장소를 연다: 사이트 = <c>site/</c> 아래 <c>T</c> 로 시작하는 폴더.</summary>
    public Task OpenRepoAsync(string root, string? site = null)
    {
        Repo = Path.GetFullPath(root);
        var dir = Path.Combine(Repo, "site");
        Sites = Directory.Exists(dir)
            ? Directory.GetDirectories(dir).Select(Path.GetFileName).OfType<string>().Where(n => n.StartsWith('T')).Order(StringComparer.Ordinal).ToList()
            : [];
        var pick = site != null && Sites.Contains(site) ? site : Sites.FirstOrDefault();
        siteName = pick;
        Raise(nameof(SiteName));
        return pick == null ? Task.CompletedTask : OpenRepoSiteAsync(Path.Combine(dir, pick));
    }

    async Task OpenRepoSiteAsync(string folder)
    {
        await OpenAsync(folder, Path.Combine(Repo!, "core"));
        History.Stale();
        await RefreshGitAsync();
    }

    /// <summary>사이트 폴더만 연다(저장소 없이). core 를 주면 그 폴더의 VERSION 등을 본다(자체 시험).</summary>
    public Task OpenSiteAsync(string folder, string? core = null)
    {
        Repo = null;
        var full = Path.GetFullPath(folder);
        Sites = [Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar))];
        siteName = Sites[0];
        Raise(nameof(SiteName));
        return OpenAsync(full, core);
    }

    EditHistory? history;
    IReadOnlyDictionary<string, int> baseline = new Dictionary<string, int>();

    public Site? Site => history?.Current;
    public string? CoreVersion => Site?.CoreVersion;

    bool busy;
    public bool IsBusy { get => busy; private set { if (Set(ref busy, value)) { Raise(nameof(CanEdit)); CommandManager.InvalidateRequerySuggested(); } } }

    string? busyText;
    /// <summary>오래 걸리는 일(태그 압축 등)의 낱말. 위 줄에 진행 표시와 함께.</summary>
    public string? BusyText { get => busyText; private set { if (Set(ref busyText, value)) Raise(nameof(HasBusyText)); } }
    public bool HasBusyText => BusyText != null;

    string? openError;
    /// <summary>열기 실패 글(빈 화면에 보인다).</summary>
    public string? OpenError { get => openError; private set => Set(ref openError, value); }

    /// <summary>열기를 돌리고, 실패하면 빈 화면에 까닭을 남긴다(죽지 않는다).</summary>
    public async Task TryOpen(Func<Task> open)
    {
        OpenError = null;
        try { await open(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            OpenError = "열기 실패 · " + e.Message;
            IsEmpty = Site == null;
        }
    }

    async Task OpenAsync(string folder, string? core)
    {
        IsBusy = true;
        try
        {
            if (core != null && !Directory.Exists(core)) core = null;
            var s = await Task.Run(() => Site.Open(folder, core));
            history = new EditHistory(s);
            baseline = Baseline(s);
            packFindings = [];
            Raise(nameof(CoreVersion));
            Refresh(null, draft: false);
            IsEmpty = false;
            OpenedFolder = folder;
            Opened?.Invoke();
        }
        finally { IsBusy = false; }

        if (DraftCheck && Site is { } loaded && Drafts.Read(loaded) is { } d)
        {
            var lines = d.Files.Select(f => d.Stale.Contains(f) ? f + " · 바뀜" : f).ToList();
            var key = await Show(new AskVm("초안", lines, [new("drop", "버리기"), new("restore", "복원", Primary: true, Default: true)]));
            if (key == "restore") Apply(new EditResult(d.Site, null, []));
            else if (key == "drop") TryDraft(() => Drafts.Clear(loaded));
        }
    }

    /// <summary>열 때 초안 묻기(자체 시험은 끈다).</summary>
    public bool DraftCheck { get; set; } = true;

    public string? OpenedFolder { get; private set; }

    /// <summary>사이트가 열렸다(창이 뮤텍스·제목 등을 맞춘다).</summary>
    public event Action? Opened;

    /// <summary>저장한 때의 표 줄 글자(바뀐 줄 점). 모든 표 구역 + 구역 밖 문장(적재 조건 등).</summary>
    static IReadOnlyDictionary<string, int> Baseline(Site s) =>
        s.Files.SelectMany(f => f.Pieces).Where(p => p.Kind is PieceKind.Statement or PieceKind.Raw)
            .GroupBy(p => p.Text).ToDictionary(g => g.Key, g => g.Count());

    // ---- 편집 ----

    public bool CanEdit => Site != null && Dialog == null && !IsBusy && !IsReadOnly;

    /// <summary>편집이 막혀 알림을 띄운 횟수(Ctrl+Z 가 칸 확정이 실패했는지 본다).</summary>
    public int ApplyFailures { get; private set; }

    /// <summary>명령 결과를 쌓고 다시 그린다. 막히면 까닭을 잠깐 보인다. 성공 여부.</summary>
    public bool Apply(EditResult r)
    {
        if (history == null) return false;
        if (IsReadOnly) { ApplyFailures++; Notify("읽기 전용"); Refresh(null, draft: false); return false; }
        if (!r.Ok) { ApplyFailures++; Notify(r.Error ?? "막힘"); return false; }
        if (ReferenceEquals(r.Site, history.Current)) return true;
        history.Push(r);
        Refresh(r.Touched, defer: true);
        return true;
    }

    void Step(Func<EditHistory, Site> f)
    {
        if (history == null) return;
        f(history);
        Refresh(null, defer: true);
    }

    public ICommand Undo { get; }
    public ICommand Redo { get; }
    public ICommand Save { get; }

    public int ChangeCount => history?.Changes ?? 0;
    public bool HasChanges => ChangeCount > 0;
    public bool Dirty => history?.Dirty == true;

    /// <summary>
    /// 검사 다시 → 표 화면들·검사 목록·변경 수. defer = 편집 한 번(안 보이는 표 화면은 그 탭이 보일 때까지 미룬다:
    /// 숨은 화면의 목록이 편집마다 요소 수천 개를 다시 만들어 한 번에 0.7~1초가 들었다). 검사 목록·변경 수·탭 배지는 바로 갱신한다.
    /// </summary>
    void Refresh(IReadOnlyList<Piece>? touched, bool draft = true, bool defer = false)
    {
        var s = Site!;
        var list = Checker.Run(s);
        (staleSite, staleList) = (s, list);
        foreach (var name in TableTabNames)
        {
            if (defer && Tab.Name != name) { stale.Add(name); continue; }
            stale.Remove(name);
            ReloadTab(name, s, list, touched);
        }
        SetFindings(s, list.Concat(packFindings).ToList());
        Raise(nameof(Site));
        Raise(nameof(ChangeCount));
        Raise(nameof(HasChanges));
        CommandManager.InvalidateRequerySuggested();
        if (draft) { draftTimer.Stop(); draftTimer.Start(); }
    }

    static readonly string[] TableTabNames = [TagTab, ProfileTab, ManualTab, SeqTab, MapTab, IoTab];

    readonly HashSet<string> stale = [];
    Site? staleSite;
    IReadOnlyList<Finding> staleList = [];

    void ReloadTab(string name, Site s, IReadOnlyList<Finding> list, IReadOnlyList<Piece>? touched)
    {
        switch (name)
        {
            case TagTab: Tag.Reload(s, list, baseline, touched); break;
            case ProfileTab: Profile.Reload(s, list, BlockHost.ChangedBy(baseline), touched); break;
            case ManualTab: Manual.Reload(s, list, BlockHost.ChangedBy(baseline), touched); break;
            case SeqTab: Seq.Reload(s, list, baseline, touched); break;
            case MapTab: Map.Reload(s, list, BlockHost.ChangedBy(baseline), touched); break;
            case IoTab: Io.Reload(s, list); break;
        }
    }

    /// <summary>미뤄 둔 그 탭의 화면을 지금 사이트로 만든다(탭이 보이기 전·그 화면 VM 을 건드리기 전).</summary>
    public void Flush(string name)
    {
        if (stale.Remove(name) && staleSite is { } s) ReloadTab(name, s, staleList, null);
    }

    /// <summary>초안: 편집을 잠깐 묶어 쓴다. 변경이 없으면 지운다.</summary>
    void WriteDraft()
    {
        if (Site is not { } s) return;
        TryDraft(() => { if (Dirty) Drafts.Write(s); else Drafts.Clear(s); });
    }

    void TryDraft(Action a)
    {
        try { a(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notify("초안 못 씀 · " + e.Message); }
    }

    /// <summary>곧 쓸 초안을 지금 쓴다(닫기·시험).</summary>
    public void FlushDraft()
    {
        if (!draftTimer.IsEnabled) return;
        draftTimer.Stop();
        WriteDraft();
    }

    string? notice;
    /// <summary>막힌 명령의 까닭(위 줄에 4초).</summary>
    public string? Notice { get => notice; private set { if (Set(ref notice, value)) Raise(nameof(HasNotice)); } }
    public bool HasNotice => Notice != null;

    public void ClearNotice()
    {
        noticeTimer.Stop();
        Notice = null;
    }

    bool noticeError = true;
    /// <summary>알림이 막힘·실패인가(빨간 칩). 아니면 회색 칩(받은 회차 번호 올림, 보관 가지 이름 등).</summary>
    public bool NoticeIsError { get => noticeError; private set => Set(ref noticeError, value); }

    public void Notify(string text, bool error = true)
    {
        NoticeIsError = error;
        Notice = text;
        noticeTimer.Stop();
        noticeTimer.Start();
    }

    // ---- 저장 ----

    IReadOnlyList<Finding> packFindings = [];

    /// <summary>
    /// [저장](Ctrl+S): 오류가 있으면 검사 목록의 첫 오류로 가서 고른다(저장 안 함, 초안은 남는다).
    /// 저장소로 열었으면 사유 한 줄 팝업 → 파일 → 압축 → 커밋(사양 8.2절). 바뀐 것이 없으면 조용히 끝.
    /// 바깥에서 바뀌었으면 팝업. 저장했는가.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        if (Site is not { } s || !CanEdit || op == "save") return false;
        string? reason = null;
        if (Repo != null && siteName != null && !Checker.Run(s).Any(f => f.Severity == Severity.Error))
        {
            if (SaveFiles.Changed(s).Count == 0) return true;
            var rv = new ReasonVm();
            if (await Show(rv) != "ok") return false;
            reason = rv.Text.Trim();
        }
        return await Locked("save", "저장", () => SaveFilesAsync(s, reason));
    }

    async Task<bool> SaveFilesAsync(Site s, string? reason)
    {
        IsBusy = true;
        SaveResult r;
        try { r = await Task.Run(() => SaveFiles.Save(s)); }
        finally { IsBusy = false; }

        if (r.Status == SaveStatus.Conflict)
        {
            var key = await Show(new AskVm("바깥에서 바뀜", r.Conflicts,
                [new("reload", "다시 읽기"), new("overwrite", "내 것으로 덮기"), new("cancel", "취소", Default: true)]));
            if (key == "reload") { await ReloadAsync(); return false; }
            if (key != "overwrite") return false;
            IsBusy = true;
            try { r = await Task.Run(() => SaveFiles.Save(s, overwrite: true)); }
            finally { IsBusy = false; }
        }

        switch (r.Status)
        {
            case SaveStatus.Errors:
                if (Findings.FirstOrDefault(f => f.IsError) is { } first) Go(first);
                Notify(r.Reason ?? "검사 오류");
                return false;
            case SaveStatus.Failed:
                // R-3: 파일은 저장 전으로 되돌렸다. 화면의 편집과 디스크가 어긋날 수 있어 다시 읽기만 길이다
                bool r3 = r.Reason?.StartsWith("R-3", StringComparison.Ordinal) == true;
                var k = await Show(new AskVm("저장 실패", [r.Reason ?? ""], r3
                    ? [new("close", "닫기", Default: true), new("reload", "다시 읽기", Primary: true)]
                    : [new("close", "닫기", Primary: true, Default: true)]));
                if (k == "reload") await ReloadAsync();
                return false;
            case SaveStatus.NoChange:
                return true;
        }

        history!.MarkSaved(r.Site);
        baseline = Baseline(r.Site);
        packFindings = [];
        external = [];
        Raise(nameof(HasExternal));
        Refresh(null, draft: false);
        await AfterSaveAsync(reason);
        return true;
    }

    /// <summary>저장 안 한 변경이 있으면 [저장][버리기][취소]. 나가도 되는가. reload = 버리면 디스크에서 다시 연다(받기·되돌리기 전).</summary>
    public async Task<bool> ConfirmLeaveAsync(bool reload = false)
    {
        if (!Dirty) return true;
        var key = await Show(new AskVm("저장 안 함", [$"변경 {ChangeCount}"],
            [new("save", "저장", Primary: true), new("drop", "버리기"), new("cancel", "취소", Default: true)]));
        if (key == "save") return await SaveAsync() && !Dirty;
        if (key == "drop")
        {
            if (reload) { await ReloadAsync(); return true; }
            draftTimer.Stop();
            if (Site is { } s) TryDraft(() => Drafts.Clear(s));
            return true;
        }
        return false;
    }

    // ---- 팝업 ----

    DialogVm? dialog;
    public DialogVm? Dialog
    {
        get => dialog;
        private set { if (Set(ref dialog, value)) { Raise(nameof(HasDialog)); Raise(nameof(CanEdit)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public bool HasDialog => Dialog != null;

    public async Task<string?> Show(DialogVm d)
    {
        var prev = Dialog;
        Dialog = d;
        DialogShown?.Invoke(d);
        try { return await d.Result; }
        finally { Dialog = prev; DialogClosed?.Invoke(); }
    }

    /// <summary>팝업이 닫혔다(앞 팝업이 있으면 그것으로 돌아간 뒤). 창이 포커스를 돌려놓는다.</summary>
    public event Action? DialogClosed;

    /// <summary>팝업이 떴다(자체 시험·캡처가 기다린다).</summary>
    public event Action<DialogVm>? DialogShown;

    public async Task AddRowsAsync()
    {
        if (Site is not { } s) return;
        var vm = new AddVm(s, Tag.SelectedRows);
        if (await Show(vm) == "ok") Apply(TagEdit.Add(s, vm.Tags, vm.ProfileList, vm.Template.Rule, vm.Mode));
    }

    public async Task EditConditionAsync(TagRowVm row)
    {
        if (Site is not { } s || row.Rule == null || !CanEdit) return;
        var vm = new CondVm(row.Rule, s.Names, TagVm.ScopeOf(s, row.Rule));
        if (await Show(vm) == "ok") Apply(TagEdit.SetCondition(s, row.Rule, vm.X, vm.Input));
    }

    public async Task EditRawAsync(Piece p)
    {
        if (Site is not { } s || !Settings.Advanced) return;
        var vm = new RawVm(p.Text.TrimEnd('\r', '\n'));
        if (await Show(vm) == "ok") Apply(SiteEdit.ReplaceText(s, p, vm.Text));
    }

    // ---- 검사 목록 ----

    IReadOnlyList<FindingVm> findings = [];
    public IReadOnlyList<FindingVm> Findings { get => findings; private set => Set(ref findings, value); }

    public int ErrorCount => Findings.Count(f => f.IsError);
    public int WarnCount => Findings.Count - ErrorCount;
    public bool HasErrors => ErrorCount > 0;
    public bool HasWarns => WarnCount > 0;

    bool checkOpen = true;
    public bool CheckOpen { get => checkOpen; set => Set(ref checkOpen, value); }
    public ICommand ToggleCheck { get; }

    void SetFindings(Site s, IReadOnlyList<Finding> list)
    {
        Findings = list.OrderBy(f => f.Severity).Select(f => new FindingVm(f, At(s, f), TabOf(f))).ToList();
        foreach (var t in Tabs)
        {
            t.Errors = Findings.Count(f => f.Tab == t && f.IsError);
            t.Warns = Findings.Count(f => f.Tab == t && !f.IsError);
        }
        Raise(nameof(ErrorCount));
        Raise(nameof(WarnCount));
        Raise(nameof(HasErrors));
        Raise(nameof(HasWarns));
    }

    TabVm? TabOf(Finding f)
    {
        var zone = f.Piece?.Zone;
        string? name = zone switch
        {
            "TAG_TABLE_BEGIN" => TagTab,
            "SEQ_MAP_BEGIN" or "ACT_MAP_BEGIN" => "대응표",
            "SEQ_BEGIN" or "ACT_BEGIN" => "시퀀스",
            "PROFILE_TABLE_BEGIN" => "프로파일",
            "MANUAL_BEGIN" => "수동",
            _ => f.File switch
            {
                "tag_table.c" or "tag_custom.h" or "tag_packed.c" => TagTab,
                "seq_table.c" or "act_table.c" or "seq_custom.h" or "act_ids.h" => "시퀀스",
                "profiles.c" or "site.h" => "프로파일",
                "manual_table.c" => "수동",
                "io_map.h" or "custom_io.h" => "IO 맵",
                _ => null,
            },
        };
        return Tabs.FirstOrDefault(t => t.Name == name);
    }

    /// <summary>위치 글: 태그 줄은 <c>태그 7 · PF_X · 6줄</c>, 표 줄은 <c>탭 · 표 이름 · n줄</c>, 그 밖은 <c>파일:줄</c>.</summary>
    static string At(Site s, Finding f)
    {
        if (f.Piece is Statement st && s.ZoneOf(st) is { } z)
        {
            int n = z.Ordinal(st);
            if (z.Open == "TAG_TABLE_BEGIN")
            {
                var r = new TagRule(st);
                return $"태그 {Blocks.BlockBuilder.Collapse(r.FirstArg.Text)} · {(r.AnyProfile ? TagVm.AllProfiles : r.Profile)} · {n}줄";
            }
            var what = z.Open switch
            {
                "SEQ_BEGIN" => "시퀀스", "ACT_BEGIN" => "ACT", "SEQ_MAP_BEGIN" or "ACT_MAP_BEGIN" => "대응표",
                "PROFILE_TABLE_BEGIN" => "프로파일", "MANUAL_BEGIN" => "수동", _ => z.Open,
            };
            return $"{what} · {z.Name} · {n}줄";
        }
        return f.Line > 0 ? $"{f.File}:{f.Line}" : f.File;
    }

    // ---- 탭 ----

    TabVm tab;
    public TabVm Tab
    {
        get => tab;
        set
        {
            var old = tab;
            if (!Set(ref tab, value)) return;
            Flush(value.Name);
            old.Changed();
            value.Changed();
            foreach (var n in new[] { nameof(IsTagTab), nameof(IsSettingsTab), nameof(IsBuildTab), nameof(IsHistoryTab), nameof(IsProfileTab), nameof(IsIoTab), nameof(IsSeqTab), nameof(IsMapTab), nameof(IsManualTab) })
                Raise(n);
            if (IsSettingsTab && Config.Tools.Count == 0 && Settings.Volatile == false) _ = Config.ProbeAsync();
            if (IsHistoryTab) _ = History.ShownAsync();
        }
    }

    public bool IsTagTab => Tab.Name == TagTab;
    public bool IsSettingsTab => Tab.Name == SettingsName;
    public bool IsProfileTab => Tab.Name == ProfileTab;
    public bool IsIoTab => Tab.Name == IoTab;
    public bool IsSeqTab => Tab.Name == SeqTab;
    public bool IsMapTab => Tab.Name == MapTab;
    public bool IsManualTab => Tab.Name == ManualTab;
    public bool IsBuildTab => Tab.Name == BuildTabName;
    public bool IsHistoryTab => Tab.Name == HistoryTabName;

    public ICommand GoTo { get; }
    public ICommand Fix { get; }

    /// <summary>[바꾸기] 빠른 고침: 걸린 인자의 맨 앞 이름만 맞는 낱말로(<see cref="Finding.Replace"/>). 한 명령.</summary>
    public bool FixFinding(Finding f)
    {
        if (f.Replace is not { } to || f.Cell is not { } cell || f.Piece is not Statement st) return false;
        var text = System.Text.RegularExpressions.Regex.Replace(cell.Text, @"^(\s*)\w+", "${1}" + to);
        return Apply(SiteEdit.SetArg(Site!, st, cell, text));
    }

    /// <summary>검사 줄을 누르면 그 탭·그 줄로 가서 고른다.</summary>
    public void Go(FindingVm f)
    {
        if (f.Tab != null) Tab = f.Tab;
        if (f.Finding.Piece is { } p)
            switch (f.Tab?.Name)
            {
                case TagTab: Tag.SelectPiece(p); break;
                case ProfileTab:
                    if (p is Statement { Zone: "PROFILE_TABLE_BEGIN" }) Profile.SelectPiece(p);
                    else if (p is Statement st) Profile.SelectName(st.Shape == StatementShape.Macro && st.Args.Count > 0 ? st.Args[0].Text : st.Name);
                    break;
                case ManualTab: Manual.SelectPiece(p); break;
                case SeqTab: Seq.SelectPiece(p); break;
                case MapTab: Map.SelectPiece(p); break;
                case IoTab: if (p is Statement d) Io.SelectName(d.Name); break;
            }
        Went?.Invoke(f);
    }

    /// <summary>대응표의 표 이름 → 시퀀스 화면의 그 표.</summary>
    public void GoTable(string name)
    {
        Tab = Tabs.First(t => t.Name == SeqTab);
        Seq.SelectTable(name);
    }

    /// <summary>태그 동작의 시퀀스 → 시퀀스 화면의 그 시퀀스 ID 표. 대응표에 없으면 화면을 안 바꾸고 false.</summary>
    public bool GoSeq(string? profile, string id)
    {
        Flush(SeqTab);
        if (!Seq.HasSeq(id)) return false;
        Tab = Tabs.First(t => t.Name == SeqTab);
        return Seq.SelectSeq(profile, id);
    }

    /// <summary>줄로 간 뒤 화면이 스크롤·포커스를 맞춘다.</summary>
    public event Action<FindingVm>? Went;

    /// <summary>설정 화면 [폴더]: 창이 폴더 고르기를 띄운다.</summary>
    public Action? PickRepoRequested { get; set; }

}
