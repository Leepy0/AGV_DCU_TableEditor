using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Text;

namespace TableEditor.Engine.Model;

/// <summary>읽을 때 디스크 파일의 크기·시각(UTC)·SHA-256(소문자 16진).</summary>
public sealed record FileStamp(long Size, DateTime WriteUtc, string Sha256)
{
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>
/// 편집 명령의 결과. Site = 새 사이트(받은 사이트는 그대로), 막히면 null 이고 Error 에 까닭.
/// Touched = 새 사이트에서 이 명령이 쓴 조각(고친·넣은·옮긴 줄). 화면이 고를 줄.
/// </summary>
public sealed record EditResult(Site? Site, string? Error, IReadOnlyList<Piece> Touched)
{
    public bool Ok => Site != null;
    internal static EditResult Fail(string why) => new(null, why, []);
}

/// <summary>새로 넣을 문장. Args = 인자 글자, Comment = 꼬리 주석 글(없으면 null).</summary>
public sealed record NewStatement(string Name, IReadOnlyList<string> Args, string? Comment = null);

/// <summary>
/// 한 명령 안의 글자 바꾸기 모음. 조각마다 칸을 두고(앞에 넣기 / 원문 / 뒤에 넣기) 마지막에 파일을 다시 읽는다.
/// 다시 읽은 조각 목록이 새 원본이다. 그래서 안 고친 조각은 글자가 그대로다(사양 5.4·5.5절).
/// </summary>
internal sealed class Splice(Site site)
{
    sealed class Slot(string orig)
    {
        public readonly List<(string Text, bool Mark)> Before = [];
        public string Orig = orig;
        public bool OrigMark;
        public readonly List<(string Text, bool Mark)> After = [];
    }

    readonly Dictionary<SourceFile, Slot[]> files = new(ReferenceEqualityComparer.Instance);

    public Site Site => site;
    public string? Error { get; private set; }
    public bool Failed => Error != null;

    public void Fail(string why) => Error ??= why;

    public bool Locate(Piece p, out SourceFile file, out int index)
    {
        foreach (var f in site.Files)
            for (int i = 0; i < f.Pieces.Count; i++)
                if (ReferenceEquals(f.Pieces[i], p)) { file = f; index = i; return true; }
        file = null!;
        index = -1;
        Fail("조각 없음 · 이 사이트의 것이 아니다");
        return false;
    }

    Slot[] SlotsOf(SourceFile f)
    {
        if (!files.TryGetValue(f, out var s)) files[f] = s = f.Pieces.Select(p => new Slot(p.Text)).ToArray();
        return s;
    }

    /// <summary>지금(이 명령 안에서 고친 뒤)의 조각 글자.</summary>
    public string Text(Piece p) => Locate(p, out var f, out var i) ? SlotsOf(f)[i].Orig : p.Text;

    public void Replace(Piece p, string text, bool mark = true)
    {
        if (!Locate(p, out var f, out var i)) return;
        var s = SlotsOf(f)[i];
        s.Orig = text;
        s.OrigMark = mark;
    }

    public void Delete(Piece p) => Replace(p, "", false);

    /// <summary>
    /// 조각 앞(before)이나 뒤에 줄을 넣는다. text 는 줄 끝 문자로 끝난다.
    /// 뒤에 넣는데 그 조각이 줄 끝 없이 끝나면(파일 끝) 줄 끝을 앞으로 옮긴다.
    /// </summary>
    public void Insert(Piece anchor, bool before, string text, bool mark = true)
    {
        if (!Locate(anchor, out var f, out var i)) return;
        var s = SlotsOf(f)[i];
        if (before) { s.Before.Add((text, mark)); return; }
        var cur = s.Orig + string.Concat(s.After.Select(a => a.Text));
        if (cur.Length > 0 && !cur.EndsWith('\n'))
        {
            var eol = Eol(anchor);
            text = eol + text[..^LineEndOf(text).Length];
        }
        s.After.Add((text, mark));
    }

    static string LineEndOf(string t) => t.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : t.EndsWith('\n') ? "\n" : "";

    /// <summary>새 줄의 줄 끝: 그 조각의 것, 없으면 가까운 조각의 것, 없으면 파일에 CRLF 가 있으면 CRLF.</summary>
    public string Eol(Piece p)
    {
        if (!Locate(p, out var f, out var i)) return "\n";
        for (int d = 0; d < f.Pieces.Count; d++)
        {
            if (i - d >= 0 && f.Pieces[i - d].LineEnd is { Length: > 0 } a) return a;
            if (i + d < f.Pieces.Count && f.Pieces[i + d].LineEnd is { Length: > 0 } b) return b;
        }
        return f.ToText().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    static readonly UTF8Encoding Strict = new(false, true);

    static int RawLines(SourceFile f) => f.Pieces.Where(p => p.Kind == PieceKind.Raw).Sum(p => p.EndLine - p.StartLine + 1);

    /// <summary>
    /// 고친 파일을 다시 읽어 새 사이트를 만든다. 막는 것: 읽기 전용 파일, 다시 읽으니 읽기 전용(R-2 등),
    /// 원문 줄이 늘어남(새 글자가 문장으로 읽히지 않는다. allowRaw 면 허용: 원문 글자 바꾸기).
    /// </summary>
    public EditResult Commit(bool allowRaw = false)
    {
        if (Error != null) return EditResult.Fail(Error);
        var list = site.Files.ToList();
        var touched = new List<Piece>();
        foreach (var (f, slots) in files)
        {
            if (f.ReadOnly) return EditResult.Fail($"읽기 전용 · {f.Name} · {f.ReadOnlyReason}");
            var sb = new StringBuilder();
            var marks = new List<(int From, int To)>();
            int line = 1;
            void Put(string t, bool mark)
            {
                if (t.Length == 0) return;
                int n = t.Count(c => c == '\n');
                if (mark) marks.Add((line, line + n - (t.EndsWith('\n') ? 1 : 0)));
                sb.Append(t);
                line += n;
            }
            foreach (var s in slots)
            {
                foreach (var (t, m) in s.Before) Put(t, m);
                Put(s.Orig, s.OrigMark);
                foreach (var (t, m) in s.After) Put(t, m);
            }
            byte[] bytes;
            try { bytes = Strict.GetBytes(sb.ToString()); }
            catch (EncoderFallbackException) { return EditResult.Fail($"글자 틀림 · {f.Name}"); }
            var nf = SourceFile.Parse(f.Name, bytes);
            if (nf.ReadOnly) return EditResult.Fail($"{f.Name} · {nf.ReadOnlyReason}");
            if (!allowRaw && RawLines(nf) > RawLines(f))
            {
                var bad = nf.Pieces.FirstOrDefault(p => p.Kind == PieceKind.Raw && marks.Any(m => p.StartLine <= m.To && p.EndLine >= m.From));
                return EditResult.Fail($"문장으로 읽히지 않음 · {f.Name}:{bad?.StartLine ?? 0}");
            }
            list[list.IndexOf(f)] = nf;
            touched.AddRange(nf.Pieces.Where(p => p.Kind != PieceKind.Blank && marks.Any(m => p.StartLine <= m.To && p.EndLine >= m.From)));
        }
        return new EditResult(site.With(list), null, touched);
    }
}

/// <summary>쓰기 꼴(사양 5.4절): 새 문장, 안쪽 묶음, 인자 목록 다시 쓰기.</summary>
internal static class Form
{
    /// <summary>본뜰 문장의 꼴(이름 뒤 공백, 인자마다 앞뒤 공백). 줄바꿈이 든 공백은 본뜨지 않는다(새 문장은 한 줄).</summary>
    internal sealed record Shape(string Gap, IReadOnlyList<string> Leads, IReadOnlyList<string> Trails)
    {
        public static Shape Of(Statement st) => new(st.Gap.Contains('\n') ? "" : st.Gap,
            st.Args.Select(a => a.Lead).ToList(), st.Args.Select(a => a.Trail).ToList());
    }

    /// <summary>
    /// <c>들여쓰기 이름( a, b )</c>. 인자 앞뒤 공백은 like(같은 구역 바로 위 같은 매크로 문장)를 본뜬다. 없으면 한 칸.
    /// </summary>
    public static string Statement(string indent, string name, IReadOnlyList<string> args, Shape? like, string? comment)
    {
        var sb = new StringBuilder(indent).Append(name);
        if (args.Count > 0)
        {
            int m = like?.Leads.Count ?? 0;
            sb.Append(like?.Gap ?? "").Append('(');
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) sb.Append(',');
                string lead = i < m ? like!.Leads[i] : " ";
                string trail = i == args.Count - 1 ? (m > 0 ? like!.Trails[m - 1] : " ") : i < m - 1 ? like!.Trails[i] : "";
                if (lead.Contains('\n')) lead = " ";
                if (trail.Contains('\n')) trail = i == args.Count - 1 ? " " : "";
                sb.Append(lead).Append(args[i]).Append(trail);
            }
            sb.Append(')');
        }
        return sb.Append(Tail(comment)).ToString();
    }

    /// <summary>새 꼬리 주석: 문장 뒤 공백 4 + <c>/* 글 */</c>.</summary>
    public static string Tail(string? comment) => string.IsNullOrEmpty(comment) ? "" : "    /* " + comment + " */";

    /// <summary>안쪽 묶음 한 개. 꼴은 문법 표(안쪽 공백 없음 <c>X_ID(1, 2)</c> / 있음 <c>IF_ID( 1, 2 )</c>).</summary>
    public static string Call(string name, IReadOnlyList<string> args)
    {
        if (args.Count == 0) return name;
        var tight = GrammarTable.Find(name).FirstOrDefault()?.Spacing == Spacing.Tight;
        return tight ? $"{name}({string.Join(", ", args)})" : $"{name}( {string.Join(", ", args)} )";
    }

    public static readonly Regex Ident = new(@"^[A-Za-z_][A-Za-z0-9_]*$");

    /// <summary>글자 칸 값으로 쓸 수 있는가(빈 글자·줄바꿈·주석 막음). 막히면 까닭.</summary>
    public static string? BadText(string text) =>
        text.Trim().Length == 0 ? "빈 글자"
        : text.IndexOfAny(['\r', '\n']) >= 0 ? "줄바꿈 안 됨"
        : text.Contains("/*", StringComparison.Ordinal) || text.Contains("//", StringComparison.Ordinal) || text.Contains("*/", StringComparison.Ordinal) ? "주석 안 됨"
        : null;

    /// <summary>인자 목록 다시 쓰기: 첫 인자 앞 공백부터 끝 인자 뒤 공백까지만 바꾼다. 나머지 글자(이름, 꼬리 주석)는 그대로.</summary>
    public static string Args(Statement st, IReadOnlyList<(string Lead, string Text, string Trail)> args)
    {
        var a0 = st.Args[0];
        var an = st.Args[^1];
        int from = a0.Offset - a0.Lead.Length;
        int to = an.Offset + an.Text.Length + an.Trail.Length;
        return st.Text[..from] + string.Join(",", args.Select(a => a.Lead + a.Text + a.Trail)) + st.Text[to..];
    }

    /// <summary>글자 구간 여럿 바꾸기(Offset 기준, 겹치지 않는다).</summary>
    public static string Spans(string text, IEnumerable<(int Offset, int Length, string New)> spans)
    {
        foreach (var (o, l, n) in spans.OrderByDescending(s => s.Offset)) text = text[..o] + n + text[(o + l)..];
        return text;
    }

    /// <summary>문장 Text 안에서 꼬리(Tail)가 시작하는 자리.</summary>
    public static int TailStart(Statement st) => st.Text.Length - st.LineEnd.Length - st.Tail.Length;

    /// <summary>인자 나무 안에 그 인자가 있는가(참조로).</summary>
    public static bool Owns(Statement st, Arg arg) => All(st.Args).Any(a => ReferenceEquals(a, arg));

    static IEnumerable<Arg> All(IEnumerable<Arg> args) => args.SelectMany(a => All(a.Node).Prepend(a));

    static IEnumerable<Arg> All(Node n) => n switch
    {
        CallNode c => All(c.Args),
        BinaryNode b => All(b.Left).Concat(All(b.Right)),
        _ => [],
    };
}
