using System.Text.RegularExpressions;
using System.Windows.Input;
using TableEditor.App.Blocks;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.App.ViewModels;

/// <summary>칸 편집기가 기대는 화면: 강조, 값 바꾸기, 태그 화면인가(각도 5의 배수).</summary>
public interface IFieldHost
{
    bool IsTagArea { get; }
    void Hot(FieldVm f);
    void SetField(FieldVm f, string text);
}

/// <summary>C 글자 한 줄을 셋으로: 앞, 강조, 뒤.</summary>
public sealed record CodeLine(string Before, string Mark, string After);

/// <summary>
/// 표 화면들의 바탕: 고른 블록 → 아래 선택 줄 칸(<see cref="FieldVm"/>)과 C 글자 한 줄. 칸 값 바꾸기 = 인자 바꾸기 명령.
/// 화면마다 <see cref="CodePiece"/>(고른 줄의 문장)만 정한다.
/// </summary>
public abstract partial class BlockHost(MainVm main) : Observable, IFieldHost
{
    protected readonly MainVm Main = main;
    protected Site Site = null!;

    public virtual bool IsTagArea => false;

    /// <summary>고른 줄의 조각(C 글자·칸 명령의 대상).</summary>
    protected abstract Piece? CodePiece { get; }

    /// <summary>이름·낱말 칸을 읽기 전용으로(시퀀스).</summary>
    protected virtual bool Locks(FieldInfo f) => false;

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
            Fields = picked == null ? [] : picked.Fields.Select((f, i) => new FieldVm(this, Site.Names, f, i, Locks(f))).ToList();
            foreach (var f in Fields) f.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FieldVm.IsHot)) Raise(nameof(Code)); };
            Raise(nameof(Fields));
            Raise(nameof(HasFields));
            Raise(nameof(Code));
            OnPicked();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    protected virtual void OnPicked() { }

    public IReadOnlyList<FieldVm> Fields { get; private set; } = [];
    public bool HasFields => Fields.Count > 0;

    protected int HotFieldIndex => Fields.FirstOrDefault(f => f.IsHot)?.Index ?? -1;

    /// <summary>칸 강조: 하나만.</summary>
    public void Hot(FieldVm f)
    {
        foreach (var x in Fields) x.IsHot = ReferenceEquals(x, f);
    }

    /// <summary>칸 값 바꾸기 → 인자 바꾸기 명령. 같은 칸을 강조로 남긴다.</summary>
    public virtual void SetField(FieldVm f, string text)
    {
        // 다시 그리는 중·뒤에 사라진 칸(지난 편집기가 포커스를 잃으며 확정)은 무시한다: 그 문장·인자는 지금 사이트의 것이 아니다
        if (CodePiece is not Statement st || !Fields.Contains(f) || !Site.Files.Any(x => x.Pieces.Any(p => ReferenceEquals(p, st)))) return;
        int fieldIndex = f.Index;
        if (Main.Apply(SiteEdit.SetArg(Site, st, f.Info.Arg, text)) && fieldIndex < Fields.Count) Hot(Fields[fieldIndex]);
    }

    /// <summary>되살리기: 블록 순번·칸 순번(다시 그린 뒤).</summary>
    protected void Restore(IReadOnlyList<BlockVm> blocks, int block, int field)
    {
        if (block >= 0 && block < blocks.Count) Picked = blocks[block];
        if (field >= 0 && field < Fields.Count) Fields[field].IsHot = true;
    }

    /// <summary>문장의 C 글자 한 줄: 앞, 강조(강조 칸 또는 고른 블록), 뒤.</summary>
    public CodeLine Code
    {
        get
        {
            if (CodePiece is not { } p) return new("", "", "");
            var text = p.Text.TrimEnd('\r', '\n');
            int lead = text.Length - text.TrimStart().Length;
            var a = Fields.FirstOrDefault(f => f.IsHot)?.Info.Arg ?? Picked?.Arg;
            if (a == null || a.Offset < lead || a.Offset + a.Text.Length > text.Length)
                return new(Flat(text.TrimStart()), "", "");
            return new(Flat(text[lead..a.Offset]), Flat(a.Text), Flat(text[(a.Offset + a.Text.Length)..]));
        }
    }

    protected void RaiseCode() => Raise(nameof(Code));

    [GeneratedRegex(@"[ \t]*\r?\n[ \t]*")]
    private static partial Regex Breaks();

    /// <summary>줄바꿈과 그 뒤 들여쓰기만 한 칸으로. 같은 줄 안 공백은 그대로 둔다.</summary>
    static string Flat(string s) => Breaks().Replace(s, " ");

    // ---- 같이 쓰는 편집 ----

    /// <summary>
    /// 줄 여럿에 같은 명령을 차례로: 줄 수가 안 바뀌는 명령이라 (파일, 조각 순번)으로 새 사이트에서 다시 찾는다. 하나라도 막히면 모두 안 한다.
    /// </summary>
    public static EditResult Chain(Site s, IReadOnlyList<Piece> pieces, Func<Site, Piece, EditResult> step)
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

    /// <summary>(파일, 조각 순번): 명령 뒤 새 사이트에서 같은 자리를 다시 찾는 열쇠.</summary>
    public static (string File, int Index) Locate(Site s, Piece p)
    {
        var f = s.FileOf(p);
        for (int i = 0; i < f.Pieces.Count; i++) if (ReferenceEquals(f.Pieces[i], p)) return (f.Name, i);
        throw new InvalidOperationException("조각 없음");
    }

    /// <summary>꼬리 주석에서 <c>/* */</c>, <c>//</c> 를 뗀다.</summary>
    public static string Strip(string c)
    {
        c = c.Trim();
        if (c.StartsWith("/*", StringComparison.Ordinal)) c = c[2..];
        if (c.EndsWith("*/", StringComparison.Ordinal)) c = c[..^2];
        if (c.StartsWith("//", StringComparison.Ordinal)) c = c[2..];
        return BlockBuilder.Collapse(c);
    }

    /// <summary>저장 뒤 바뀐 줄 찾기: 기준선(글자 → 개수)에서 하나씩 지운다. 없으면 바뀐 줄.</summary>
    public static Func<Piece, bool> ChangedBy(IReadOnlyDictionary<string, int> baseline)
    {
        var left = new Dictionary<string, int>(baseline);
        return p =>
        {
            if (!left.TryGetValue(p.Text, out var n) || n == 0) return true;
            left[p.Text] = n - 1;
            return false;
        };
    }
}
