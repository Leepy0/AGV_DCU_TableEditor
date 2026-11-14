using System.Text.RegularExpressions;
using TableEditor.Engine.Grammar;

namespace TableEditor.Engine.Text;

/// <summary>
/// 글자 → 조각 목록(사양 5.1~5.3절). 줄 단위로 빈 줄 / 주석 / 문장 / 원문을 가른다.
/// 문장으로 못 읽는 줄은 죽지 않고 원문 조각이 된다. 이어진 원문 줄·빈 줄은 한 조각으로 합친다.
/// </summary>
internal sealed partial class Reader
{
    readonly string s;
    readonly FileZoneDef def;
    readonly List<Piece> pieces = [];
    int pos;
    int line = 1;
    ZoneDef? zone;
    string? ifndef;

    sealed class MarkerException(string message) : Exception(message);

    Reader(string text, FileZoneDef def)
    {
        s = text;
        this.def = def;
    }

    /// <summary>조각 목록, 또는 구역 표지 짝이 안 맞으면 (null, 까닭).</summary>
    public static (List<Piece>? Pieces, string? Error) Run(string text, FileZoneDef def)
    {
        var r = new Reader(text, def);
        try
        {
            r.ReadAll();
            return (r.pieces, null);
        }
        catch (MarkerException e)
        {
            return (null, "R-2 " + e.Message);
        }
    }

    [GeneratedRegex(@"^[ \t]*(typedef[ \t]+)?enum([ \t]+[A-Za-z_]\w*)?[ \t]*\{[ \t]*(/\*.*?\*/[ \t]*)?(//.*)?$")]
    private static partial Regex EnumOpenRx();

    [GeneratedRegex(@"^[ \t]*\}[ \t]*([A-Za-z_]\w*)?[ \t]*;[ \t]*(/\*.*?\*/[ \t]*)?(//.*)?$")]
    private static partial Regex EnumCloseRx();

    void ReadAll()
    {
        while (pos < s.Length)
        {
            int eol = s.IndexOf('\n', pos);
            if (eol < 0) eol = s.Length;
            int next = eol < s.Length ? eol + 1 : eol;
            int contentEnd = eol > pos && s[eol - 1] == '\r' ? eol - 1 : eol;
            var content = s[pos..contentEnd];

            if (content.All(c => c is ' ' or '\t' or '\r' or '\f' or '\v'))
            {
                Emit(PieceKind.Blank, next, zone?.Open);
                continue;
            }

            int ind = pos;
            while (s[ind] is ' ' or '\t') ind++;

            int commentEnd = CommentOnlyEnd(ind);
            if (commentEnd >= 0)
            {
                Emit(PieceKind.Comment, commentEnd, zone?.Open);
                continue;
            }

            var prevIfndef = ifndef;
            ifndef = null;

            if (s[ind] == '#')
            {
                Directive(ind, prevIfndef);
                continue;
            }

            if (zone is { IsEnum: true })
            {
                if (EnumCloseRx().IsMatch(content))
                {
                    Emit(PieceKind.Raw, next, null);
                    zone = null;
                }
                else if (!TryEnumerator(ind))
                {
                    EmitRaw();
                }
                continue;
            }

            if (zone == null && def.Zones.Contains(FileZones.Enum) && EnumOpenRx().IsMatch(content))
            {
                Emit(PieceKind.Raw, next, null);
                zone = FileZones.Enum;
                continue;
            }

            if (IsIdentStart(s[ind]) && TryMacro(ind)) continue;
            EmitRaw();
        }
        if (zone != null) throw new MarkerException($"{zone.Open} 뒤에 {zone.Close} 가 없다");
    }

    // ---- 조각 내기 ----

    void Emit(PieceKind kind, int end, string? zoneName) => Add(new Piece(kind, s[pos..end], line, EndLineOf(end), zoneName), end);

    void Add(Piece p, int end)
    {
        var last = pieces.Count > 0 ? pieces[^1] : null;
        bool merge = last != null && last.Kind == p.Kind && last.Zone == p.Zone
            && p.Kind is PieceKind.Raw or PieceKind.Blank && last is not Statement;
        if (merge) pieces[^1] = last! with { Text = last.Text + p.Text, EndLine = p.EndLine };
        else pieces.Add(p);
        line += CountNewlines(pos, end);
        pos = end;
    }

    int EndLineOf(int end)
    {
        int n = CountNewlines(pos, end);
        // 마지막 글자가 줄 끝이면 그 줄에서 끝난다
        return line + n - (end > pos && s[end - 1] == '\n' ? 1 : 0);
    }

    int CountNewlines(int from, int to)
    {
        int n = 0;
        for (int i = from; i < to; i++) if (s[i] == '\n') n++;
        return n;
    }

    void EmitRaw() => Emit(PieceKind.Raw, RawEnd(pos), zone?.Open);

    /// <summary>
    /// 원문 줄의 끝. 열린 괄호·블록 주석·줄 잇기(<c>\</c>)가 줄 끝에 남아 있으면 닫힐 때까지 다음 줄로 늘린다.
    /// </summary>
    int RawEnd(int p)
    {
        int depth = 0;
        while (p < s.Length)
        {
            char c = s[p];
            if (c == '\n')
            {
                if (depth == 0) return p + 1;
                p++;
                continue;
            }
            if (c == '/' && At(p + 1, '*'))
            {
                int close = s.IndexOf("*/", p + 2, StringComparison.Ordinal);
                if (close < 0) return s.Length;
                p = close + 2;
                continue;
            }
            if (c == '/' && At(p + 1, '/'))
            {
                while (p < s.Length && s[p] != '\n') p++;
                continue;
            }
            if (c is '"' or '\'')
            {
                p++;
                while (p < s.Length && s[p] != c && s[p] != '\n') p += s[p] == '\\' ? 2 : 1;
                p = Math.Min(p + 1, s.Length);
                continue;
            }
            if (c == '\\' && (At(p + 1, '\n') || At(p + 1, '\r') && At(p + 2, '\n')))
            {
                p += At(p + 1, '\n') ? 2 : 3;
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')' && depth > 0) depth--;
            p++;
        }
        return s.Length;
    }

    /// <summary>줄이 공백·주석뿐이면 그 주석이 끝난 줄의 다음 줄 첫 자리. 아니면 -1.</summary>
    int CommentOnlyEnd(int p)
    {
        bool any = false;
        while (true)
        {
            SkipSpaces(ref p);
            if (p >= s.Length) return any ? p : -1;
            if (s[p] == '\n' || s[p] == '\r' && At(p + 1, '\n')) return any ? TerminatorEnd(p) : -1;
            if (s[p] == '/' && At(p + 1, '*'))
            {
                int close = s.IndexOf("*/", p + 2, StringComparison.Ordinal);
                if (close < 0) return -1;
                p = close + 2;
                any = true;
                continue;
            }
            if (s[p] == '/' && At(p + 1, '/'))
            {
                while (p < s.Length && s[p] != '\n') p++;
                return p < s.Length ? p + 1 : p;
            }
            return -1;
        }
    }

    // ---- 전처리 줄 ----

    void Directive(int ind, string? prevIfndef)
    {
        int p = ind + 1;
        SkipSpaces(ref p);
        var word = ReadIdent(ref p);
        if (word == "ifndef")
        {
            SkipSpaces(ref p);
            ifndef = ReadIdent(ref p);
        }
        else if (word == "define" && zone == null && def.Defines && TryDefine(ind, p, prevIfndef))
        {
            return;
        }
        EmitRaw();
    }

    /// <summary><c>#define 이름 값 [꼬리 주석]</c>. 포함 가드·<c>#ifndef</c> 기본값·함수 꼴·값 없음은 원문.</summary>
    bool TryDefine(int ind, int p, string? prevIfndef)
    {
        if (p >= s.Length || s[p] is not (' ' or '\t')) return false;
        SkipSpaces(ref p);
        var name = ReadIdent(ref p);
        if (name.Length == 0 || name == prevIfndef || At(p, '(')) return false;
        int leadStart = p;
        SkipSpaces(ref p);
        if (p == leadStart) return false;
        int valueStart = p;
        var node = ParseExpr(ref p, false);
        if (node == null) return false;
        int valueEnd = p;
        int lineEnd = ParseTail(valueEnd);
        if (lineEnd < 0) return false;
        var arg = new Arg(s[leadStart..valueStart], s[valueStart..valueEnd], "", node, valueStart - pos);
        AddStatement(StatementShape.Define, ind, name, "", false, [arg], valueEnd, lineEnd, null);
        return true;
    }

    // ---- enum 안 ----

    /// <summary>열거자 줄 <c>이름 [= 값][,] [꼬리 주석]</c>.</summary>
    bool TryEnumerator(int ind)
    {
        int p = ind;
        var name = ReadIdent(ref p);
        if (name.Length == 0) return false;
        int nameEnd = p;
        SkipSpaces(ref p);
        IReadOnlyList<Arg> args = [];
        string gap = "";
        int after = nameEnd;
        if (At(p, '='))
        {
            p++;
            int leadStart = p;
            SkipSpaces(ref p);
            int valueStart = p;
            var node = ParseExpr(ref p, false);
            if (node == null) return false;
            gap = s[nameEnd..leadStart];
            args = [new Arg(s[leadStart..valueStart], s[valueStart..p], "", node, valueStart - pos)];
            after = p;
        }
        int q = after;
        SkipSpaces(ref q);
        if (At(q, ',')) q++;
        int lineEnd = ParseTail(q);
        if (lineEnd < 0) return false;
        AddStatement(StatementShape.Enumerator, ind, name, gap, false, args, after, lineEnd, zone!.Open);
        return true;
    }

    // ---- 표 매크로 문장 ----

    bool TryMacro(int ind)
    {
        int p = ind;
        var name = ReadIdent(ref p);
        var rows = RowsFor(name);
        if (rows.Count == 0) return false;

        int nameEnd = p;
        SkipSpaces(ref p);
        IReadOnlyList<Arg> args = [];
        string gap = "";
        bool parens = At(p, '(');
        int bodyEnd = nameEnd;
        if (parens)
        {
            gap = s[nameEnd..p];
            p++;
            var list = ParseArgs(ref p);
            if (list == null) return false;
            args = list;
            bodyEnd = p;
        }
        int lineEnd = ParseTail(bodyEnd);
        if (lineEnd < 0) return false;
        if (!rows.Any(r => args.Count >= r.MinArgs && args.Count <= r.MaxArgs)) return false;

        bool opens = zone == null && def.Zones.Any(z => z.Open == name);
        string? zoneName = zone != null && name != zone.Close ? zone.Open : null;
        AddStatement(StatementShape.Macro, ind, name, gap, parens, args, bodyEnd, lineEnd, zoneName);
        if (zone != null && name == zone.Close) zone = null;
        else if (opens) zone = def.Zones.First(z => z.Open == name);
        return true;
    }

    /// <summary>
    /// 지금 자리에서 문장으로 읽을 수 있는 그 이름의 문법 줄. 구역 표지가 제자리가 아니면 R-2.
    /// </summary>
    List<GrammarEntry> RowsFor(string name)
    {
        if (name.Length == 0) return [];
        bool isOpen = def.Zones.Any(z => z.Open == name);
        bool isClose = def.Zones.Any(z => z.Close == name);
        if (zone == null)
        {
            if (isClose) throw new MarkerException($"{name} 앞에 여는 표지가 없다({line}줄)");
            if (!isOpen && !def.Top.Contains(name)) return [];
            return GrammarTable.Find(name, Place.Frame).ToList();
        }
        if (name == zone.Close) return GrammarTable.Find(name, Place.Frame).ToList();
        if (isOpen || isClose) throw new MarkerException($"{zone.Open} 안에 {name}({line}줄)");
        var z = zone;
        return GrammarTable.Find(name)
            .Where(r => r.Role == Role.Statement && z.Places.Contains(r.Place) && (z.Names == null || z.Names.Contains(name)))
            .ToList();
    }

    void AddStatement(StatementShape shape, int ind, string name, string gap, bool parens, IReadOnlyList<Arg> args,
        int bodyEnd, int lineEnd, string? zoneName)
    {
        int end = TerminatorEnd(lineEnd);
        var text = s[pos..end];
        var st = new Statement(shape, s[pos..ind], name, gap, parens, args, s[bodyEnd..lineEnd],
            text, line, EndLineOf(end), zoneName);
        Add(st, end);
    }

    /// <summary>
    /// 문장 뒤 그 줄의 나머지: 공백·블록 주석(여러 줄 가능)·줄 주석만 있어야 한다.
    /// 줄 끝 문자의 자리(<c>\r</c> 또는 <c>\n</c>, 파일 끝이면 길이), 아니면 -1.
    /// </summary>
    int ParseTail(int p)
    {
        while (true)
        {
            SkipSpaces(ref p);
            if (p >= s.Length) return p;
            if (s[p] == '\n') return p;
            if (s[p] == '\r' && At(p + 1, '\n')) return p;
            if (s[p] == '/' && At(p + 1, '*'))
            {
                int close = s.IndexOf("*/", p + 2, StringComparison.Ordinal);
                if (close < 0) return -1;
                p = close + 2;
                continue;
            }
            if (s[p] == '/' && At(p + 1, '/'))
            {
                while (p < s.Length && s[p] != '\n') p++;
                return p > 0 && p < s.Length && s[p - 1] == '\r' ? p - 1 : p;
            }
            return -1;
        }
    }

    int TerminatorEnd(int p) => p >= s.Length ? s.Length : s[p] == '\r' ? p + 2 : p + 1;

    // ---- 인자·식 ----

    /// <summary><c>(</c> 다음 자리부터 짝 맞는 <c>)</c> 뒤까지. 인자 안 주석·모르는 글자는 null(원문).</summary>
    List<Arg>? ParseArgs(ref int p)
    {
        var list = new List<Arg>();
        int save = p;
        SkipWs(ref p, true);
        if (At(p, ')'))
        {
            p++;
            return list;
        }
        p = save;
        while (true)
        {
            int leadStart = p;
            SkipWs(ref p, true);
            int textStart = p;
            var node = ParseExpr(ref p, true);
            if (node == null) return null;
            int textEnd = p;
            SkipWs(ref p, true);
            list.Add(new Arg(s[leadStart..textStart], s[textStart..textEnd], s[textEnd..p], node, textStart - pos));
            if (At(p, ',')) { p++; continue; }
            if (At(p, ')')) { p++; return list; }
            return null;
        }
    }

    Node? ParseExpr(ref int p, bool multiline)
    {
        var left = ParseTerm(ref p);
        if (left == null) return null;
        while (true)
        {
            int save = p;
            SkipWs(ref p, multiline);
            if (p < s.Length && s[p] is '+' or '-')
            {
                char op = s[p++];
                SkipWs(ref p, multiline);
                var right = ParseTerm(ref p);
                if (right == null) return null;
                left = new BinaryNode(left, op, right);
                continue;
            }
            p = save;
            return left;
        }
    }

    Node? ParseTerm(ref int p)
    {
        if (p >= s.Length) return null;
        char c = s[p];
        if (char.IsAsciiDigit(c) || c is '-' or '+' && p + 1 < s.Length && char.IsAsciiDigit(s[p + 1]))
            return ParseNumber(ref p);
        if (!IsIdentStart(c)) return null;
        var name = ReadIdent(ref p);
        int nameEnd = p;
        SkipSpaces(ref p);
        if (At(p, '('))
        {
            var gap = s[nameEnd..p];
            p++;
            var args = ParseArgs(ref p);
            return args == null ? null : new CallNode(name, gap, args);
        }
        p = nameEnd;
        return new NameNode(name);
    }

    NumberNode? ParseNumber(ref int p)
    {
        int start = p;
        bool neg = s[p] == '-';
        if (s[p] is '-' or '+') p++;
        int digits = p;
        long value;
        if (s[p] == '0' && (At(p + 1, 'x') || At(p + 1, 'X')))
        {
            p += 2;
            int h = p;
            while (p < s.Length && char.IsAsciiHexDigit(s[p])) p++;
            if (p == h || !long.TryParse(s.AsSpan(h, p - h), System.Globalization.NumberStyles.HexNumber, null, out value)) return null;
        }
        else
        {
            while (p < s.Length && char.IsAsciiDigit(s[p])) p++;
            if (!long.TryParse(s.AsSpan(digits, p - digits), out value)) return null;
        }
        while (p < s.Length && s[p] is 'u' or 'U' or 'l' or 'L') p++;
        if (p < s.Length && IsIdentChar(s[p])) return null;
        return new NumberNode(neg ? -value : value, s[start..p]);
    }

    // ---- 글자 ----

    bool At(int p, char c) => p < s.Length && s[p] == c;

    void SkipSpaces(ref int p)
    {
        while (p < s.Length && s[p] is ' ' or '\t') p++;
    }

    void SkipWs(ref int p, bool newlines)
    {
        if (!newlines) { SkipSpaces(ref p); return; }
        while (p < s.Length && s[p] is ' ' or '\t' or '\r' or '\n') p++;
    }

    string ReadIdent(ref int p)
    {
        int start = p;
        if (p < s.Length && IsIdentStart(s[p]))
            while (p < s.Length && IsIdentChar(s[p])) p++;
        return s[start..p];
    }

    static bool IsIdentStart(char c) => char.IsAsciiLetter(c) || c == '_';
    static bool IsIdentChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
