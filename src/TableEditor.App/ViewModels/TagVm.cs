using System.Text.RegularExpressions;
using System.Windows.Input;
using TableEditor.App.Blocks;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.App.ViewModels;

/// <summary>태그 보기 한 줄: 프로파일 하나에 맞는 줄(조건 있는 줄은 조건별, 사양 9.2절 태그 보기).</summary>
public sealed record LookRow(string Profile, bool IsAny, bool IsGroupStart, IReadOnlyList<Chip> Chips, string OrdinalText, IReadOnlyList<BlockVm> Blocks, bool None);

/// <summary>C 글자 한 줄을 셋으로: 앞, 강조, 뒤.</summary>
public sealed record CodeLine(string Before, string Mark, string After);

/// <summary>추가 메뉴 항목: 분류 → 동작. Enabled = 칸이 남는가.</summary>
public sealed record ActionChoice(string Palette, string Text, GrammarEntry Entry, bool Enabled);

/// <summary>
/// 태그 화면: 거르기, 줄 표, 줄 안 편집(태그·프로파일·주석), 선택 줄 칸(칸 편집기, C 글자), 동작 넣기·지우기·옮기기,
/// 줄 복제·삭제·옮기기, 보기 전환. 명령은 모두 <see cref="MainVm.Apply"/> 로 가서 새 사이트가 된다.
/// </summary>
public sealed partial class TagVm : Observable
{
    public const string AllProfiles = "전체";

    readonly MainVm main;
    Site site = null!;
    IReadOnlyList<TagRowVm> all = [];

    public TagVm(MainVm main)
    {
        this.main = main;
        Add = new Command(() => _ = main.AddRowsAsync(), () => main.CanEdit);
        Duplicate = new Command(DuplicateRows, () => main.CanEdit && SelectedRows.Any(r => r.Rule != null));
        Delete = new Command(DeleteRows, () => main.CanEdit && SelectedRows.Count > 0);
        DeleteBlock = new Command(() => DeleteAction(Picked), () => main.CanEdit && Picked is { Raw: false, Index: >= 0 } && Selected?.Blocks.Count > 1);
        CommitTag = new Command(p => CommitTagText(p as TagRowVm ?? Selected));
        CancelTag = new Command(p => (p as TagRowVm ?? Selected)?.ResetDrafts());
        CommitComment = new Command(p => CommitCommentText(p as TagRowVm ?? Selected));
        EditCond = new Command(p => { if (p is TagRowVm r) { SelectOnly(r); _ = main.EditConditionAsync(r); } });
        EditRaw = new Command(() => { if (Selected != null) _ = main.EditRawAsync(Selected.Piece); }, () => main.Settings.Advanced && Selected != null);
    }

    public bool IsTagArea => true;

    /// <summary>사이트를 다시 그린다. touched = 방금 명령이 쓴 조각(있으면 그 줄들을 고른다). 없으면 앞 고르기를 순번으로 되살린다.</summary>
    public void Reload(Site s, IReadOnlyList<Finding> findings, IReadOnlyDictionary<string, int> baseline, IReadOnlyList<Piece>? touched)
    {
        var left = new Dictionary<string, int>(baseline);
        bool Changed(Piece p)
        {
            if (!left.TryGetValue(p.Text, out var n) || n == 0) return true;
            left[p.Text] = n - 1;
            return false;
        }
        var prevOrd = SelectedRows.Select(r => r.Ordinal).ToList();
        var prevPrimary = Selected?.Ordinal;
        var prevBlock = Picked?.Index ?? -1;
        var prevField = HotFieldIndex;
        bool sameRow = touched == null || touched.Count == 0;

        site = s;
        var b = new BlockBuilder(s.Names, findings);
        var rows = new List<TagRowVm>();
        foreach (var z in s.ZonesOf("TAG_TABLE_BEGIN"))
            foreach (var p in z.Body.Where(p => p.Kind is PieceKind.Statement or PieceKind.Raw))
                rows.Add(new TagRowVm(this, s, b, findings, p, p is Statement st ? z.Ordinal(st) : 0, Changed(p)));
        all = rows;
        foreach (var r in all)
            foreach (var blk in r.Blocks)
            {
                var (row, block) = (r, blk);
                blk.Pick = new Command(() => { SelectOnly(row); Picked = block; BlockPickedByUser = true; });
            }
        var used = all.Where(r => r.Rule is { AnyProfile: false }).Select(r => r.Rule!.Profile);
        Profiles = [AllProfiles, .. s.Profiles.Select(p => p.Name).Union(used).Distinct()];
        Raise(nameof(Profiles));
        if (!Profiles.Contains(profileFilter)) { profileFilter = AllProfiles; Raise(nameof(ProfileFilter)); }
        Refilter(keepSelection: false);

        // 고르기 되살리기
        List<TagRowVm> pick;
        TagRowVm? primary;
        if (touched is { Count: > 0 })
        {
            pick = Rows.Where(r => touched.Any(t => ReferenceEquals(t, r.Piece))).ToList();
            if (pick.Count == 0)
            {
                // 거르기에 가려진 줄: 거르기를 푼다
                ClearFilter();
                pick = Rows.Where(r => touched.Any(t => ReferenceEquals(t, r.Piece))).ToList();
            }
            primary = pick.FirstOrDefault();
        }
        else
        {
            pick = Rows.Where(r => prevOrd.Contains(r.Ordinal) && r.Ordinal > 0).ToList();
            primary = Rows.FirstOrDefault(r => r.Ordinal == prevPrimary && r.Ordinal > 0) ?? pick.FirstOrDefault();
        }
        SetSelection(pick, primary, scroll: touched is { Count: > 0 });
        if (primary != null && (sameRow || pick.Count == 1))
        {
            if (prevBlock >= 0 && prevBlock < primary.Blocks.Count) Picked = primary.Blocks[prevBlock];
            if (prevField >= 0 && prevField < Fields.Count) Fields[prevField].IsHot = true;
        }
        RebuildLook();
    }

    public IReadOnlyList<string> Profiles { get; private set; } = [AllProfiles];
    public int Total => all.Count(r => r.Rule != null);

    /// <summary>줄 안 프로파일 드롭다운 항목: 전체(ANY_PROFILE) + 프로파일.</summary>
    public IReadOnlyList<string> ProfileChoices => Profiles;

    // ---- 거르기 ----

    string tagFilter = "";
    public string TagFilter { get => tagFilter; set { if (Set(ref tagFilter, value)) { Refilter(); RebuildLook(); } } }

    string profileFilter = AllProfiles;
    public string ProfileFilter { get => profileFilter; set { if (Set(ref profileFilter, value ?? AllProfiles)) Refilter(); } }

    IReadOnlyList<TagRowVm> rows = [];
    /// <summary>보이는 줄. 거를 때마다 목록을 통째 바꾼다(가상화 목록에 줄 단위로 넣지 않는다).</summary>
    public IReadOnlyList<TagRowVm> Rows { get => rows; private set => Set(ref rows, value); }

    public string CountText => $"{Rows.Count(r => r.Rule != null)} / {Total}줄";

    long? FilterTag => long.TryParse(TagFilter.Trim(), out var n) ? n : null;

    void ClearFilter()
    {
        tagFilter = "";
        profileFilter = AllProfiles;
        Raise(nameof(TagFilter));
        Raise(nameof(ProfileFilter));
        Refilter(keepSelection: false);
    }

    void Refilter(bool keepSelection = true)
    {
        var tag = FilterTag;
        bool tagSet = TagFilter.Trim().Length > 0;
        var list = all.Where(r =>
            (!tagSet || tag is long n && r.Covers(n))
            && (ProfileFilter == AllProfiles || r.Rule != null && (r.IsAny || r.Rule.Profile == ProfileFilter))).ToList();
        string? prev = null;
        foreach (var r in list)
        {
            r.IsRepeat = r.Rule != null && r.GroupKey == prev;
            r.IsGroupStart = r.Rule != null && r.GroupKey != prev;
            if (r.Rule != null) prev = r.GroupKey;
        }
        Rows = list;
        Raise(nameof(CountText));
        if (keepSelection) SetSelection(SelectedRows.Where(list.Contains).ToList(), Selected != null && list.Contains(Selected) ? Selected : null, false);
    }

    // ---- 고르기(여러 줄) ----

    IReadOnlyList<TagRowVm> selectedRows = [];
    /// <summary>고른 줄(화면 차례).</summary>
    public IReadOnlyList<TagRowVm> SelectedRows => selectedRows;

    TagRowVm? selected;
    /// <summary>머리 줄: 선택 줄 칸과 줄 안 편집기가 따른다.</summary>
    public TagRowVm? Selected => selected;

    public bool HasSelection => Selected != null;

    /// <summary>화면이 고르기를 바꿔야 할 때(명령 뒤 새 줄 고르기 등). 화면 → VM 은 <see cref="FromView"/>.</summary>
    public event Action<IReadOnlyList<TagRowVm>, TagRowVm?, bool>? SelectionPushed;

    bool fromView;

    /// <summary>화면에서 고르기가 바뀜(마우스·키보드).</summary>
    public void FromView(IReadOnlyList<TagRowVm> list, TagRowVm? primary)
    {
        fromView = true;
        try { Apply(list, primary); BlockPickedByUser = false; }
        finally { fromView = false; }
    }

    public void SetSelection(IReadOnlyList<TagRowVm> list, TagRowVm? primary, bool scroll)
    {
        Apply(list, primary);
        if (!fromView) SelectionPushed?.Invoke(selectedRows, selected, scroll);
    }

    public void SelectOnly(TagRowVm r) => SetSelection([r], r, true);

    void Apply(IReadOnlyList<TagRowVm> list, TagRowVm? primary)
    {
        var ordered = Rows.Where(list.Contains).ToList();
        primary ??= ordered.FirstOrDefault();
        if (primary != null && !ordered.Contains(primary)) ordered.Add(primary);
        selectedRows = ordered;
        if (!ReferenceEquals(selected, primary))
        {
            if (selected != null) selected.IsCurrent = false;
            selected = primary;
            if (selected != null) selected.IsCurrent = true;
            Picked = selected?.Blocks.FirstOrDefault();
            Raise(nameof(Selected));
            Raise(nameof(HasSelection));
            RebuildLook();
        }
        Raise(nameof(SelectedRows));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>순번 n 줄을 고른다(없으면 그대로).</summary>
    public void SelectOrdinal(int n)
    {
        if (Rows.FirstOrDefault(r => r.Ordinal == n) is { } r) SetSelection([r], r, true);
    }

    /// <summary>조각을 가진 줄을 고른다. 거르기에 가려 있으면 거르기를 푼다.</summary>
    public TagRowVm? SelectPiece(Piece p)
    {
        var r = all.FirstOrDefault(x => ReferenceEquals(x.Piece, p));
        if (r == null) return null;
        if (!Rows.Contains(r)) ClearFilter();
        IsLook = false;
        SetSelection([r], r, true);
        return r;
    }

    // ---- 블록·칸 ----

    /// <summary>사용자가 블록을 눌러 골랐다(Del 이 블록을 지운다). 줄을 다시 고르면 풀린다.</summary>
    public bool BlockPickedByUser { get; set; }

    BlockVm? picked;
    public BlockVm? Picked
    {
        get => picked;
        set
        {
            if (picked != null) picked.IsPicked = false;
            bool changed = Set(ref picked, value);
            if (picked != null) picked.IsPicked = true;
            if (!changed) return;
            Fields = picked == null ? [] : picked.Fields.Select((f, i) => new FieldVm(this, site.Names, f, i)).ToList();
            foreach (var f in Fields) f.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FieldVm.IsHot)) Raise(nameof(Code)); };
            Raise(nameof(Fields));
            Raise(nameof(HasFields));
            Raise(nameof(DetailTitle));
            Raise(nameof(DetailHead));
            Raise(nameof(Code));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public IReadOnlyList<FieldVm> Fields { get; private set; } = [];
    public bool HasFields => Fields.Count > 0;

    int HotFieldIndex => Fields.FirstOrDefault(f => f.IsHot)?.Index ?? -1;

    /// <summary>칸 강조: 하나만.</summary>
    public void Hot(FieldVm f)
    {
        foreach (var x in Fields) x.IsHot = ReferenceEquals(x, f);
    }

    /// <summary>선택 줄 머리 앞부분: <c>9줄 · 태그 34 · </c>(뒤에 블록 머리 이름을 굵게).</summary>
    public string DetailTitle => Selected == null ? "" : Selected.Rule == null ? "원문" : $"{Selected.OrdinalText}줄 · 태그 {Selected.TagText} · ";

    public string DetailHead => Picked == null || Picked.Raw ? "" : Picked.Head;

    /// <summary>문장의 C 글자 한 줄: 앞, 강조(강조 칸 또는 고른 블록), 뒤.</summary>
    public CodeLine Code
    {
        get
        {
            if (Selected == null) return new("", "", "");
            var text = Selected.Piece.Text.TrimEnd('\r', '\n');
            int lead = text.Length - text.TrimStart().Length;
            var a = Fields.FirstOrDefault(f => f.IsHot)?.Info.Arg ?? Picked?.Arg;
            if (a == null || a.Offset < lead || a.Offset + a.Text.Length > text.Length)
                return new(Flat(text.TrimStart()), "", "");
            return new(Flat(text[lead..a.Offset]), Flat(a.Text), Flat(text[(a.Offset + a.Text.Length)..]));
        }
    }

    [GeneratedRegex(@"[ \t]*\r?\n[ \t]*")]
    private static partial Regex Breaks();

    /// <summary>줄바꿈과 그 뒤 들여쓰기만 한 칸으로. 같은 줄 안 공백은 그대로 둔다.</summary>
    static string Flat(string s) => Breaks().Replace(s, " ");

    // ---- 명령 ----

    public ICommand Add { get; }
    public ICommand Duplicate { get; }
    public ICommand Delete { get; }
    public ICommand DeleteBlock { get; }
    public ICommand CommitTag { get; }
    public ICommand CancelTag { get; }
    public ICommand CommitComment { get; }
    public ICommand EditCond { get; }
    public ICommand EditRaw { get; }

    IReadOnlyList<TagRule> SelectedRules => SelectedRows.Select(r => r.Rule).OfType<TagRule>().ToList();

    /// <summary>칸 값 바꾸기 → 인자 바꾸기 명령. 같은 블록·칸을 강조로 남긴다.</summary>
    public void SetField(FieldVm f, string text)
    {
        if (Selected?.Piece is not Statement st) return;
        int fieldIndex = f.Index;
        if (main.Apply(SiteEdit.SetArg(site, st, f.Info.Arg, text)) && fieldIndex < Fields.Count) Hot(Fields[fieldIndex]);
    }

    /// <summary>태그 번호: 고른 줄 모두(<see cref="TagEdit.Renumber"/>). 범위 규칙은 <c>처음-끝</c> 으로 끝도 바꾼다.</summary>
    void CommitTagText(TagRowVm? row)
    {
        if (row?.Rule == null) return;
        var m = Regex.Match(row.TagDraft.Trim(), @"^(\d+)\s*(?:[-–~]\s*(\d+))?$");
        if (!m.Success) { main.Notify("번호 글 틀림"); row.ResetDrafts(); return; }
        long first = long.Parse(m.Groups[1].Value);
        var rules = SelectedRows.Contains(row) ? SelectedRules : [row.Rule];
        var r = rules.Count == 1 && row.Rule.IsRange && m.Groups[2].Success && site.Num(row.Rule.FirstArg) == first
            ? SiteEdit.SetArg(site, row.Rule.St, row.Rule.LastArg, m.Groups[2].Value)
            : TagEdit.Renumber(site, rules, first);
        if (r.Ok && r.Touched.Count > 0 && row.Rule.IsRange && m.Groups[2].Success && long.Parse(m.Groups[2].Value) is var last && rules.Count == 1
            && r.Touched.OfType<Statement>().FirstOrDefault() is { } moved && new TagRule(moved) is var nr && r.Site!.Num(nr.LastArg) != last)
            r = SiteEdit.SetArg(r.Site, moved, nr.LastArg, last.ToString());
        if (!main.Apply(r)) row.ResetDrafts();
    }

    /// <summary>프로파일: 고른 줄 모두. 전체 = ANY_PROFILE.</summary>
    public void SetProfile(string text)
    {
        var name = text == AllProfiles ? "ANY_PROFILE" : text;
        var targets = SelectedRules;
        if (targets.Count == 0) return;
        main.Apply(Chain(site, targets.Select(t => (Piece)t.St).ToList(), (s, p) => SiteEdit.SetArg(s, (Statement)p, new TagRule((Statement)p).ProfileArg, name)));
    }

    void CommitCommentText(TagRowVm? row)
    {
        if (row?.Piece is not Statement st || row.CommentDraft.Trim() == row.Comment) return;
        if (!main.Apply(SiteEdit.SetComment(site, st, row.CommentDraft))) row.ResetDrafts();
    }

    /// <summary>
    /// 줄 여럿에 같은 명령을 차례로: 줄 수가 안 바뀌는 명령이라 (파일, 조각 순번)으로 새 사이트에서 다시 찾는다. 하나라도 막히면 모두 안 한다.
    /// </summary>
    static EditResult Chain(Site s, IReadOnlyList<Piece> pieces, Func<Site, Piece, EditResult> step)
    {
        var keys = pieces.Select(p => Locate(s, p)).ToList();
        var touched = new List<Piece>();
        foreach (var (file, index) in keys)
        {
            var r = step(s, s.File(file)!.Pieces[index]);
            if (!r.Ok) return r;
            s = r.Site!;
            touched.Add(s.File(file)!.Pieces[index]);
        }
        return new EditResult(s, null, touched);
    }

    static (string File, int Index) Locate(Site s, Piece p)
    {
        var f = s.FileOf(p);
        for (int i = 0; i < f.Pieces.Count; i++) if (ReferenceEquals(f.Pieces[i], p)) return (f.Name, i);
        throw new InvalidOperationException("조각 없음");
    }

    // ---- 동작 ----

    /// <summary>줄 끝 [+] 메뉴: 팔레트 분류 → 동작. 칸이 모자라면 흐리게.</summary>
    public IReadOnlyList<ActionChoice> ActionChoices(TagRowVm row)
    {
        int left = 4 - row.CellCount;
        return GrammarTable.Rows.Where(r => r.Place == Place.TagAction && r.TagCells > 0)
            .OrderBy(r => GrammarTable.TagPalette.ToList().IndexOf(r.Palette))
            .Select(r => new ActionChoice(r.Palette, Choices.MenuText(r), r, r.TagCells <= left))
            .ToList();
    }

    public void AddAction(TagRowVm row, GrammarEntry e)
    {
        if (row.Rule == null) return;
        SelectOnly(row);
        int index = row.Rule.Actions.Count;
        if (main.Apply(TagEdit.InsertAction(site, row.Rule, index, Choices.DefaultText(e, site.Names))) && Selected?.Blocks.Count > index)
            Picked = Selected.Blocks[index];
    }

    public void DeleteAction(BlockVm? b)
    {
        if (b?.Row is not TagRowVm { Rule: { } rule } || b.Index < 0) return;
        main.Apply(TagEdit.DeleteAction(site, rule, b.Index));
    }

    /// <summary>블록 끌어 옮기기(같은 줄 안).</summary>
    public void MoveBlock(BlockVm from, BlockVm to)
    {
        if (from.Row is not TagRowVm { Rule: { } rule } row || !ReferenceEquals(row, to.Row) || from.Index == to.Index || from.Index < 0 || to.Index < 0) return;
        int target = to.Index;
        if (main.Apply(TagEdit.MoveAction(site, rule, from.Index, target)) && Selected?.Blocks.Count > target) Picked = Selected.Blocks[target];
    }

    /// <summary>Del: 블록을 눌러 골랐으면 블록, 아니면 줄.</summary>
    public void DeleteKey()
    {
        if (!main.CanEdit) return;
        if (BlockPickedByUser && Picked is { Raw: false, Index: >= 0 } && Selected?.Blocks.Count > 1) DeleteAction(Picked);
        else DeleteRows();
    }

    // ---- 줄 ----

    void DeleteRows()
    {
        if (SelectedRows.Count == 0) return;
        var next = Rows.SkipWhile(r => !SelectedRows.Contains(r)).SkipWhile(SelectedRows.Contains).FirstOrDefault();
        int nextOrd = next?.Ordinal ?? -1;
        int removed = SelectedRows.Count(r => r.Ordinal > 0 && r.Ordinal < nextOrd);
        if (main.Apply(SiteEdit.Delete(site, SelectedRows.Select(r => r.Piece).ToList())) && nextOrd > 0)
            SelectOrdinal(nextOrd - removed);
    }

    /// <summary>복제: 고른 줄들을 그 바로 아래에(마지막 고른 줄 뒤).</summary>
    void DuplicateRows()
    {
        var src = SelectedRows.Where(r => r.Piece is Statement).ToList();
        if (src.Count == 0) return;
        var zone = site.ZoneOf((Statement)src[0].Piece);
        if (zone == null) return;
        var items = src.Select(r => (Statement)r.Piece).Select(st =>
            new NewStatement(st.Name, st.Args.Select(a => BlockBuilder.Collapse(a.Text)).ToList(), Comment(st))).ToList();
        main.Apply(SiteEdit.Insert(site, zone, src.Max(r => r.Ordinal), items));
    }

    static string? Comment(Statement st)
    {
        var c = st.TailComment;
        if (c.StartsWith("/*", StringComparison.Ordinal)) c = c[2..].TrimEnd('/').TrimEnd('*');
        else if (c.StartsWith("//", StringComparison.Ordinal)) c = c[2..];
        c = c.Trim();
        return c.Length == 0 ? null : c;
    }

    /// <summary>줄 끌어 옮기기: 고른 줄 묶음째 target 줄 앞으로(target 이 null 이면 끝으로).</summary>
    public void MoveRows(IReadOnlyList<TagRowVm> moving, TagRowVm? target)
    {
        var pieces = moving.Where(r => r.Piece is Statement).Select(r => r.Piece).ToList();
        if (pieces.Count == 0 || target != null && moving.Contains(target)) return;
        var zone = site.ZoneOf((Statement)pieces[0]);
        if (zone == null) return;
        int index = target?.Ordinal > 0 ? target.Ordinal - 1 : zone.Statements.Count;
        main.Apply(SiteEdit.Move(site, pieces, zone, index));
    }

    // ---- 편집 | 보기 ----

    bool look;
    public bool IsLook { get => look; set { if (Set(ref look, value)) { Raise(nameof(IsEdit)); RebuildLook(); } } }
    public bool IsEdit { get => !look; set => IsLook = !value; }

    IReadOnlyList<LookRow> lookRows = [];
    public IReadOnlyList<LookRow> LookRows { get => lookRows; private set => Set(ref lookRows, value); }

    /// <summary>보기의 태그 번호: 거르기 칸의 번호, 없으면 선택 줄의 태그.</summary>
    public long? LookTag => FilterTag ?? Selected?.First;

    public string LookTitle => LookTag is long t ? $"태그 {t}" : "태그 —";

    /// <summary>태그 하나 × 프로파일 전부 → 위에서 첫 일치. 조건 있는 줄은 조건 없는 줄을 만날 때까지 모두.</summary>
    void RebuildLook()
    {
        Raise(nameof(LookTitle));
        if (!IsLook || LookTag is not long tag) { LookRows = []; return; }
        var rules = all.Where(r => r.Rule != null && r.Covers(tag)).ToList();
        var list = new List<LookRow>();
        foreach (var p in Profiles.Skip(1))
        {
            bool first = true, open = true;
            foreach (var r in rules.Where(r => r.IsAny || r.Rule!.Profile == p))
            {
                list.Add(new LookRow(p, false, first, r.Chips, r.OrdinalText, r.Blocks.Select(b => b.Copy()).ToList(), false));
                first = false;
                if (!r.Rule!.HasCond) { open = false; break; }
            }
            // 맞는 줄이 없거나 조건 줄뿐이면: 그 밖은 동작 없음
            if (open) list.Add(new LookRow(p, false, first, first ? [] : [new Chip("그 외", [])], "", [], true));
        }
        LookRows = list;
    }
}
