using System.IO;
using System.Windows.Input;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Check;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.App.ViewModels;

/// <summary>왼쪽 탭 하나. Group: 0 = 표, 1 = 이력·빌드, 2 = 설정(아래).</summary>
public sealed class TabVm(string name, int group, MainVm owner) : Observable
{
    public string Name { get; } = name;
    public int Group { get; } = group;

    int errors, warns;
    public int Errors { get => errors; set { if (Set(ref errors, value)) Raise(nameof(HasErrors)); } }
    public int Warns { get => warns; set { if (Set(ref warns, value)) Raise(nameof(HasWarns)); } }
    public bool HasErrors => Errors > 0;
    public bool HasWarns => Warns > 0;

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
}

/// <summary>창 전체: 위 줄, 왼쪽 탭, 검사 목록, 지금 탭의 화면.</summary>
public sealed class MainVm : Observable
{
    public const string TagTab = "태그";

    public IReadOnlyList<TabVm> Tabs { get; }
    public IEnumerable<TabVm> TableTabs => Tabs.Where(t => t.Group == 0);
    public IEnumerable<TabVm> OpsTabs => Tabs.Where(t => t.Group == 1);
    public TabVm SettingsTab => Tabs[^1];

    public MainVm()
    {
        Tabs = new[] { ("프로파일", 0), ("IO 맵", 0), (TagTab, 0), ("시퀀스", 0), ("대응표", 0), ("수동", 0), ("이력", 1), ("빌드", 1), ("설정", 2) }
            .Select(t => new TabVm(t.Item1, t.Item2, this)).ToList();
        tab = Tabs.First(t => t.Name == TagTab);
        GoTo = new Command(p => { if (p is FindingVm f) Go(f); });
        ToggleCheck = new Command(() => CheckOpen = !CheckOpen);
    }

    // ---- 열기 ----

    string? repo;
    /// <summary>앱 루트(저장소). 사이트 폴더만 열었으면 null(깃·빌드 꺼짐).</summary>
    public string? Repo { get => repo; private set { if (Set(ref repo, value)) Raise(nameof(HasRepo)); } }
    public bool HasRepo => Repo != null;

    IReadOnlyList<string> sites = [];
    public IReadOnlyList<string> Sites { get => sites; private set => Set(ref sites, value); }

    string? siteName;
    /// <summary>사이트 드롭다운. 바꾸면 그 사이트를 연다.</summary>
    public string? SiteName
    {
        get => siteName;
        set { if (Set(ref siteName, value) && Repo != null && value != null) _ = TryOpen(() => OpenAsync(Path.Combine(Repo, "site", value), Path.Combine(Repo, "core"))); }
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
        return pick == null ? Task.CompletedTask : OpenAsync(Path.Combine(dir, pick), Path.Combine(Repo, "core"));
    }

    /// <summary>사이트 폴더만 연다(저장소 없이).</summary>
    public Task OpenSiteAsync(string folder)
    {
        Repo = null;
        var full = Path.GetFullPath(folder);
        Sites = [Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar))];
        siteName = Sites[0];
        Raise(nameof(SiteName));
        return OpenAsync(full, null);
    }

    Site? site;
    public Site? Site { get => site; private set => Set(ref site, value); }

    public string? CoreVersion => Site?.CoreVersion;

    TagVm? tag;
    public TagVm? Tag { get => tag; private set => Set(ref tag, value); }

    bool busy;
    public bool IsBusy { get => busy; private set => Set(ref busy, value); }

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
            var (s, facts, findings) = await Task.Run(() =>
            {
                var s = Site.Open(folder, core);
                return (s, new Facts(s), Checker.Run(s));
            });
            Site = s;
            Raise(nameof(CoreVersion));
            Tag = new TagVm(s, facts, findings);
            SetFindings(s, findings);
            IsEmpty = false;
        }
        finally { IsBusy = false; }
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
        Findings = list.OrderBy(f => f.Severity).Select(f => new FindingVm(f, At(s, f), TabOf(s, f))).ToList();
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

    TabVm? TabOf(Site s, Finding f)
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
                "tag_table.c" or "tag_custom.h" => TagTab,
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
                return $"태그 {BlockBuilderText(r.FirstArg.Text)} · {(r.AnyProfile ? TagVm.AllProfiles : r.Profile)} · {n}줄";
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

    static string BlockBuilderText(string s) => Blocks.BlockBuilder.Collapse(s);

    // ---- 탭 ----

    TabVm tab;
    public TabVm Tab
    {
        get => tab;
        set
        {
            var old = tab;
            if (!Set(ref tab, value)) return;
            old.Changed();
            value.Changed();
            Raise(nameof(IsTagTab));
        }
    }

    public bool IsTagTab => Tab.Name == TagTab;

    public ICommand GoTo { get; }

    /// <summary>검사 줄을 누르면 그 탭·그 줄로 가서 고른다.</summary>
    void Go(FindingVm f)
    {
        if (f.Tab != null) Tab = f.Tab;
        if (f.Tab?.Name == TagTab && f.Finding.Piece is { } p) Tag?.SelectPiece(p);
        Went?.Invoke(f);
    }

    /// <summary>줄로 간 뒤 화면이 스크롤·포커스를 맞춘다.</summary>
    public event Action<FindingVm>? Went;

    // ---- 위 줄 ----

    public int ChangeCount => 0;
    public bool HasChanges => ChangeCount > 0;
    public ICommand Pull { get; } = Command.None;
    public ICommand Save { get; } = Command.None;
    public ICommand Push { get; } = Command.None;
    public ICommand Build { get; } = Command.None;
}
