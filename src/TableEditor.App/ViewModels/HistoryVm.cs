using System.Text.RegularExpressions;
using System.Windows.Input;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Ops;

namespace TableEditor.App.ViewModels;

/// <summary>이력 한 줄: 날짜, 사람, 사유, 회차(nn차), 아직 안 올림(대기).</summary>
public sealed record HistoryRow(HistoryEntry Entry, string Date, string Reason, string? Round, bool Waiting)
{
    public string Hash => Entry.Hash;
    public string Author => Entry.Author;
    public bool IsRound => Round != null;
}

/// <summary>이력 화면(사양 9.2절): 커밋 목록, 고른 줄의 [보기](읽기 전용) [되돌리기](팝업).</summary>
public sealed class HistoryVm : Observable
{
    static readonly Regex RoundSubject = new(@"^(\d+)차 이미지");
    readonly MainVm main;
    bool stale = true;

    public ICommand View { get; }
    public ICommand Revert { get; }

    public HistoryVm(MainVm main)
    {
        this.main = main;
        View = new Command(() => { if (Selected is { } r) _ = main.ViewCommitAsync(r.Entry); }, () => Selected != null && main.CanGit);
        Revert = new Command(() => { if (Selected is { } r) _ = main.RevertAsync(r.Entry); }, () => Selected != null && main.CanGit);
    }

    IReadOnlyList<HistoryRow> rows = [];
    public IReadOnlyList<HistoryRow> Rows { get => rows; private set { if (Set(ref rows, value)) Raise(nameof(IsEmpty)); } }

    HistoryRow? selected;
    public HistoryRow? Selected
    {
        get => selected;
        set { if (Set(ref selected, value)) { Raise(nameof(HasSelection)); CommandManager.InvalidateRequerySuggested(); } }
    }
    public bool HasSelection => Selected != null;

    bool loading;
    public bool IsLoading { get => loading; private set { if (Set(ref loading, value)) Raise(nameof(IsEmpty)); } }
    public bool IsEmpty => !IsLoading && Rows.Count == 0;

    /// <summary>깃이 움직였다: 화면에 있으면 다시 읽고, 아니면 다음에 볼 때 읽는다.</summary>
    public void Stale()
    {
        stale = true;
        if (main.IsHistoryTab) _ = LoadAsync();
    }

    public Task ShownAsync() => stale ? LoadAsync() : Task.CompletedTask;

    public async Task LoadAsync()
    {
        stale = false;
        if (main.Repo is not { } root || main.SiteName is not { } site) { Rows = []; return; }
        IsLoading = true;
        try
        {
            var git = new Git(root, main.Settings.AppUrl);
            var list = await git.HistoryAsync(site);
            var waiting = await GitRead.UnpushedAsync(root);
            var keep = Selected?.Hash;
            Rows = list.Select(e => new HistoryRow(e, e.Date.Length >= 16 ? e.Date[..16] : e.Date,
                e.Subject.StartsWith(site + ": ", StringComparison.Ordinal) ? e.Subject[(site.Length + 2)..] : e.Subject,
                RoundSubject.Match(e.Subject) is { Success: true } m ? m.Groups[1].Value + "차" : null,
                waiting.Any(h => h.StartsWith(e.Hash, StringComparison.OrdinalIgnoreCase)))).ToList();
            Selected = Rows.FirstOrDefault(r => r.Hash == keep);
        }
        finally { IsLoading = false; }
    }
}
