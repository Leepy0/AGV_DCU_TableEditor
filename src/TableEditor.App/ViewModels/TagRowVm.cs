using TableEditor.App.Blocks;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Check;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.App.ViewModels;

/// <summary>
/// 태그 표 한 줄. 문장 줄이면 Rule 이 있고, 구역 안 원문 조각이면 Rule 이 null(회색 한 줄).
/// IsRepeat = 앞 보이는 줄과 같은 태그(흐리게), IsGroupStart = 태그가 바뀜(굵은 선). 둘은 거르기 뒤 다시 정한다.
/// </summary>
public sealed class TagRowVm : Observable
{
    public TagRule? Rule { get; }
    public Piece Piece { get; }
    public int Ordinal { get; }
    public string OrdinalText => Ordinal > 0 ? Ordinal.ToString() : "";
    public long? First { get; }
    public long? Last { get; }
    public string TagText { get; }
    public string ProfileText { get; }
    public bool IsAny { get; }
    public IReadOnlyList<Chip> Chips { get; }
    public IReadOnlyList<BlockVm> Blocks { get; }
    public string CellsText { get; }
    public bool IsFull { get; }
    public string Comment { get; }
    public string? Status { get; }
    public Severity? StatusSeverity { get; }
    public bool IsError => StatusSeverity == Severity.Error;
    public bool IsRaw => Rule == null;

    bool repeat, groupStart;
    public bool IsRepeat { get => repeat; set => Set(ref repeat, value); }
    public bool IsGroupStart { get => groupStart; set => Set(ref groupStart, value); }

    public TagRowVm(Site site, BlockBuilder b, IReadOnlyList<Finding> findings, Piece piece, int ordinal)
    {
        Piece = piece;
        Ordinal = ordinal;
        var mine = findings.Where(f => ReferenceEquals(f.Piece, piece)).ToList();
        var worst = mine.FirstOrDefault(f => f.Severity == Severity.Error) ?? mine.FirstOrDefault();
        Status = worst?.Text;
        StatusSeverity = worst?.Severity;

        if (piece is not Statement st)
        {
            TagText = ProfileText = CellsText = Comment = "";
            Chips = [];
            Blocks = [BlockBuilder.RawBlock(piece.Text)];
            return;
        }

        var rule = new TagRule(st);
        Rule = rule;
        First = site.Num(rule.FirstArg);
        Last = site.Num(rule.LastArg);
        TagText = rule.IsRange ? $"{Collapse(rule.FirstArg)}–{Collapse(rule.LastArg)}" : Collapse(rule.FirstArg);
        IsAny = rule.AnyProfile;
        ProfileText = IsAny ? "전체" : rule.Profile;
        Chips = b.Chips(rule.Cond);
        var blocks = rule.Actions.Select(a => b.Action(st, a)).ToList();
        Blocks = blocks;
        int cells = blocks.Sum(x => x.Cells);
        CellsText = $"{cells}/4";
        IsFull = cells == 4;
        Comment = Strip(st.TailComment);
    }

    static string Collapse(Arg a) => BlockBuilder.Collapse(a.Text);

    /// <summary>꼬리 주석에서 <c>/* */</c>, <c>//</c> 를 뗀다.</summary>
    static string Strip(string c)
    {
        c = c.Trim();
        if (c.StartsWith("/*", StringComparison.Ordinal)) c = c[2..];
        if (c.EndsWith("*/", StringComparison.Ordinal)) c = c[..^2];
        if (c.StartsWith("//", StringComparison.Ordinal)) c = c[2..];
        return BlockBuilder.Collapse(c);
    }

    /// <summary>태그 번호 n 이 이 줄에 맞는가.</summary>
    public bool Covers(long n) => First is long a && (Last ?? a) is long b && a <= n && n <= b;

    /// <summary>같은 태그 묶음 열쇠(태그 칸 글자).</summary>
    public string GroupKey => TagText;
}
