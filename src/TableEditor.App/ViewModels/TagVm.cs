using System.Windows.Input;
using TableEditor.App.Blocks;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Check;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.App.ViewModels;

/// <summary>태그 보기 한 줄: 프로파일 하나에 맞는 줄(조건 있는 줄은 조건별, 사양 9.2절 태그 보기).</summary>
public sealed record LookRow(string Profile, bool IsAny, bool IsGroupStart, IReadOnlyList<Chip> Chips, string OrdinalText, IReadOnlyList<BlockVm> Blocks, bool None);

/// <summary>C 글자 한 줄을 셋으로: 앞, 강조, 뒤.</summary>
public sealed record CodeLine(string Before, string Mark, string After);

/// <summary>태그 화면(보기 전용 절반): 거르기, 줄 표, 선택 줄 칸, 태그 보기.</summary>
public sealed class TagVm : Observable
{
    public const string AllProfiles = "전체";

    readonly IReadOnlyList<TagRowVm> all;

    public TagVm(Site site, Facts facts, IReadOnlyList<Finding> findings)
    {
        var b = new BlockBuilder(facts, findings);
        var rows = new List<TagRowVm>();
        foreach (var z in site.ZonesOf("TAG_TABLE_BEGIN"))
            foreach (var p in z.Body.Where(p => p.Kind is PieceKind.Statement or PieceKind.Raw))
                rows.Add(new TagRowVm(site, b, findings, p, p is Statement st ? z.Ordinal(st) : 0));
        all = rows;
        foreach (var r in all)
            foreach (var blk in r.Blocks)
            {
                var (row, block) = (r, blk);
                blk.Pick = new Command(() => { Selected = row; Picked = block; });
            }

        var used = all.Where(r => r.Rule is { AnyProfile: false }).Select(r => r.Rule!.Profile);
        Profiles = [AllProfiles, .. site.Profiles.Select(p => p.Name).Union(used).Distinct()];
        Refilter();
    }

    public IReadOnlyList<string> Profiles { get; }
    public int Total => all.Count(r => r.Rule != null);

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

    void Refilter()
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
        if (Selected != null && !list.Contains(Selected)) Selected = null;
    }

    // ---- 선택 줄 ----

    TagRowVm? selected;
    public TagRowVm? Selected
    {
        get => selected;
        set
        {
            if (!Set(ref selected, value)) return;
            Picked = value?.Blocks.FirstOrDefault();
            Raise(nameof(HasSelection));
            RebuildLook();
        }
    }

    public bool HasSelection => Selected != null;

    BlockVm? picked;
    public BlockVm? Picked
    {
        get => picked;
        set
        {
            if (picked != null) picked.IsPicked = false;
            if (!Set(ref picked, value)) { if (picked != null) picked.IsPicked = true; return; }
            if (picked != null) picked.IsPicked = true;
            Raise(nameof(DetailTitle));
            Raise(nameof(DetailHead));
            Raise(nameof(Code));
        }
    }

    /// <summary>선택 줄 머리 앞부분: <c>9줄 · 태그 34 · </c>(뒤에 블록 머리 이름을 굵게).</summary>
    public string DetailTitle => Selected == null ? "" : Selected.Rule == null ? "원문" : $"{Selected.OrdinalText}줄 · 태그 {Selected.TagText} · ";

    public string DetailHead => Picked == null || Picked.Raw ? "" : Picked.Head;

    /// <summary>문장의 C 글자 한 줄: 앞, 블록 인자(강조), 뒤.</summary>
    public CodeLine Code
    {
        get
        {
            if (Selected == null) return new("", "", "");
            var text = Selected.Piece.Text.TrimEnd('\r', '\n');
            int lead = text.Length - text.TrimStart().Length;
            if (Picked?.Arg is not { } a || a.Offset < lead || a.Offset + a.Text.Length > text.Length)
                return new(Flat(text.TrimStart()), "", "");
            return new(Flat(text[lead..a.Offset]), Flat(a.Text), Flat(text[(a.Offset + a.Text.Length)..]));
        }
    }

    /// <summary>줄바꿈과 그 뒤 들여쓰기만 한 칸으로. 같은 줄 안 공백은 그대로 둔다.</summary>
    static string Flat(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"[ \t]*\r?\n[ \t]*", " ");

    /// <summary>순번 n 줄을 고른다(없으면 그대로).</summary>
    public void SelectOrdinal(int n)
    {
        if (Rows.FirstOrDefault(r => r.Ordinal == n) is { } r) Selected = r;
    }

    /// <summary>조각을 가진 줄을 고른다. 거르기에 가려 있으면 거르기를 푼다.</summary>
    public TagRowVm? SelectPiece(Piece p)
    {
        var r = all.FirstOrDefault(x => ReferenceEquals(x.Piece, p));
        if (r == null) return null;
        if (!Rows.Contains(r)) { tagFilter = ""; profileFilter = AllProfiles; Raise(nameof(TagFilter)); Raise(nameof(ProfileFilter)); Refilter(); }
        IsLook = false;
        Selected = r;
        return r;
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

    public ICommand Add { get; } = Command.None;
    public ICommand Duplicate { get; } = Command.None;
    public ICommand Delete { get; } = Command.None;
}
