using System.Windows.Input;
using TableEditor.App.Blocks;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Check;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.App.ViewModels;

/// <summary>대응표 한 줄(SEQ_MAP·ACT_MAP). 머리 줄에서 드롭다운 셋으로 고친다.</summary>
public sealed class MapRowVm : Observable
{
    public MapVm Owner { get; }
    public Piece Piece { get; }
    public MapRow? Row { get; }
    public int Ordinal { get; }
    public string OrdinalText => Ordinal > 0 ? Ordinal.ToString() : "";
    public bool IsAct => Row?.IsAct == true;
    public bool IsAny => Row?.AnyProfile == true;
    public string ProfileText { get; } = "";
    public string Id { get; } = "";
    public string Table { get; } = "";
    public string Comment { get; } = "";
    public string? Status { get; }
    public Severity? StatusSeverity { get; }
    public bool IsRaw => Row == null;
    public bool IsChanged { get; }
    public bool IsGroupStart { get; init; }
    public override string ToString() => $"{OrdinalText}줄 {ProfileText} {Id} {Table}";

    public MapRowVm(MapVm owner, Site site, IReadOnlyList<Finding> findings, Piece piece, ZoneBlock zone, bool changed)
    {
        Owner = owner;
        Piece = piece;
        IsChanged = changed;
        var worst = findings.Where(f => ReferenceEquals(f.Piece, piece)).OrderBy(f => f.Severity).FirstOrDefault();
        Status = worst?.Text;
        StatusSeverity = worst?.Severity;
        if (piece is not Statement { Args.Count: 3 } st) return;
        Ordinal = zone.Ordinal(st);
        Row = new MapRow(st, zone);
        ProfileText = Row.AnyProfile ? TagVm.AllProfiles : Row.Profile;
        Id = BlockBuilder.Collapse(Row.Id);
        Table = BlockBuilder.Collapse(Row.Table);
        Comment = BlockHost.Strip(st.TailComment);
    }

    bool current;
    public bool IsCurrent { get => current; set { if (Set(ref current, value) && value) ResetDrafts(); } }

    string pf = "", id = "", table = "";
    public string ProfileDraft { get => pf; set { if (value != null && Set(ref pf, value) && value != ProfileText) Owner.SetCell(this, 0, value); } }
    public string IdDraft { get => id; set { if (value != null && Set(ref id, value) && value != Id) Owner.SetCell(this, 1, value); } }
    public string TableDraft { get => table; set { if (value != null && Set(ref table, value) && value != Table) Owner.SetCell(this, 2, value); } }

    public IReadOnlyList<string> IdChoices => IsAct ? Owner.ActIds : Owner.SeqIds;
    public IReadOnlyList<string> TableChoices => IsAct ? Owner.ActTables : Owner.SeqTables;

    public void ResetDrafts()
    {
        pf = ProfileText;
        id = Id;
        table = Table;
        Raise(nameof(ProfileDraft));
        Raise(nameof(IdDraft));
        Raise(nameof(TableDraft));
    }
}

/// <summary>대응표 화면(사양 9.2절): 줄 표(프로파일, 시퀀스 ID, 표 이름), 드롭다운 셋, 줄 추가·끌기·삭제. 표 이름을 누르면 시퀀스 화면의 그 표.</summary>
public sealed class MapVm : BlockHost
{
    public MapVm(MainVm main) : base(main)
    {
        Add = new Command(AddRow, () => Main.CanEdit && Zones.Any());
        Delete = new Command(DeleteRow, () => Main.CanEdit && Selected != null);
        GoTable = new Command(p => { if (p is MapRowVm r && r.Row != null) Main.GoTable(r.Row.Table); });
    }

    protected override Piece? CodePiece => Selected?.Piece;

    IEnumerable<ZoneBlock> Zones => Site == null ? [] : Site.Zones.Where(z => z.Open is "SEQ_MAP_BEGIN" or "ACT_MAP_BEGIN");

    public IReadOnlyList<MapRowVm> Rows { get; private set; } = [];
    public bool IsEmpty => Rows.Count == 0;
    public string CountText => $"{Rows.Count(r => r.Row != null)}줄";
    public IReadOnlyList<string> Profiles { get; private set; } = [];
    public IReadOnlyList<string> SeqIds { get; private set; } = [];
    public IReadOnlyList<string> ActIds { get; private set; } = [];
    public IReadOnlyList<string> SeqTables { get; private set; } = [];
    public IReadOnlyList<string> ActTables { get; private set; } = [];

    public void Reload(Site s, IReadOnlyList<Finding> findings, Func<Piece, bool> changed, IReadOnlyList<Piece>? touched)
    {
        int prev = Selected == null ? -1 : Rows.ToList().IndexOf(Selected);
        Site = s;
        Profiles = [TagVm.AllProfiles, .. s.Profiles.Select(p => p.Name)];
        SeqIds = s.Names.Of(NameKind.SeqId).Select(d => d.Name).Distinct().ToList();
        ActIds = s.Names.Of(NameKind.ActId).Select(d => d.Name).Distinct().ToList();
        SeqTables = s.Seqs.Where(t => !t.IsAct).Select(t => t.Name).ToList();
        ActTables = s.Seqs.Where(t => t.IsAct).Select(t => t.Name).ToList();
        var rows = new List<MapRowVm>();
        foreach (var z in Zones)
        {
            bool first = rows.Count > 0;
            foreach (var p in z.Body.Where(p => p.Kind is PieceKind.Statement or PieceKind.Raw))
            {
                rows.Add(new MapRowVm(this, s, findings, p, z, changed(p)) { IsGroupStart = first });
                first = false;
            }
        }
        Rows = rows;
        foreach (var n in new[] { nameof(Profiles), nameof(SeqIds), nameof(ActIds), nameof(SeqTables), nameof(ActTables), nameof(Rows), nameof(IsEmpty), nameof(CountText) }) Raise(n);
        var pick = touched is { Count: > 0 } ? rows.FirstOrDefault(r => touched.Any(t => ReferenceEquals(t, r.Piece))) : null;
        Select(pick ?? (prev >= 0 && prev < rows.Count ? rows[prev] : null), touched is { Count: > 0 });
    }

    // ---- 고르기 ----

    MapRowVm? selected;
    public MapRowVm? Selected { get => selected; set => Select(value, false); }
    public bool HasSelection => Selected != null;
    public event Action<object>? ScrollTo;

    public void Select(MapRowVm? r, bool scroll)
    {
        if (!ReferenceEquals(selected, r))
        {
            if (selected != null) selected.IsCurrent = false;
            selected = r;
            if (selected != null) selected.IsCurrent = true;
            Raise(nameof(Selected));
            Raise(nameof(HasSelection));
            Raise(nameof(DetailTitle));
        }
        RaiseCode();
        CommandManager.InvalidateRequerySuggested();
        if (scroll && r != null) ScrollTo?.Invoke(r);
    }

    public void SelectPiece(Piece p) => Select(Rows.FirstOrDefault(r => ReferenceEquals(r.Piece, p)), true);

    public string DetailTitle => Selected == null ? "" : Selected.Row == null ? "원문" : $"{Selected.OrdinalText}줄 · {(Selected.IsAct ? "ACT" : "시퀀스")}";

    // ---- 명령 ----

    public ICommand Add { get; }
    public ICommand Delete { get; }
    public ICommand GoTable { get; }

    /// <summary>칸 하나(0 프로파일, 1 ID, 2 표 이름). 전체 = 0.</summary>
    public void SetCell(MapRowVm r, int index, string value)
    {
        if (r.Row is not { } row) return;
        if (index == 0 && value == TagVm.AllProfiles) value = "0";
        if (!Main.Apply(SiteEdit.SetArg(Site, row.St, row.St.Args[index], value))) r.ResetDrafts();
    }

    /// <summary>[추가]: 고른 줄 아래. 같은 프로파일·표, ID 는 그 프로파일에 아직 없는 첫 ID.</summary>
    void AddRow()
    {
        var src = Selected?.Row;
        var zone = src?.Zone ?? Zones.First();
        bool act = zone.Open == "ACT_MAP_BEGIN";
        var profile = src?.Profile ?? "0";
        var used = zone.Statements.Where(s => s.Args.Count == 3 && s.Args[0].Text == profile).Select(s => s.Args[1].Text.Trim()).ToHashSet();
        var ids = act ? ActIds : SeqIds;
        var id = ids.FirstOrDefault(i => !used.Contains(i)) ?? src?.Id ?? ids.FirstOrDefault() ?? "0";
        var table = src?.Table ?? (act ? ActTables : SeqTables).FirstOrDefault() ?? "";
        if (table.Length == 0) { Main.Notify("표 없음"); return; }
        int at = Selected?.Ordinal > 0 && src != null ? Selected.Ordinal : zone.Statements.Count;
        Main.Apply(SiteEdit.Insert(Site, zone, at, [new NewStatement(act ? "ACT_MAP" : "SEQ_MAP", [profile, id, table])]));
    }

    void DeleteRow()
    {
        if (Selected is not { } r) return;
        int i = Rows.ToList().IndexOf(r);
        if (Main.Apply(SiteEdit.Delete(Site, [r.Piece]))) Select(Rows.ElementAtOrDefault(Math.Min(i, Rows.Count - 1)), true);
    }

    /// <summary>줄 끌기: 같은 구역 안에서만(target 줄 앞, null 이면 그 구역 끝).</summary>
    public void MoveRow(MapRowVm moving, MapRowVm? target)
    {
        if (moving.Row is not { } row || ReferenceEquals(moving, target)) return;
        if (target?.Row != null && !ReferenceEquals(target.Row.Zone, row.Zone)) { Main.Notify("다른 표"); return; }
        int index = target?.Ordinal > 0 ? target.Ordinal - 1 : row.Zone.Statements.Count;
        Main.Apply(SiteEdit.Move(Site, [moving.Piece], row.Zone, index));
    }
}
