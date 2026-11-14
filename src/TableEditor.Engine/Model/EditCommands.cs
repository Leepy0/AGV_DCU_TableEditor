using TableEditor.Engine.Text;

namespace TableEditor.Engine.Model;

/// <summary>
/// 편집 명령(설계서 4.4절). 받은 사이트는 바꾸지 않고 새 사이트를 돌려준다. 하나라도 막히면 아무것도 안 하고 까닭을 돌려준다.
/// 쓰기 꼴은 사양 5.4절: 고친 인자의 글자만 바뀌고 나머지 공백·주석·줄 끝은 그대로다.
/// </summary>
public static class SiteEdit
{
    // ---- 문장 한 개 ----

    /// <summary>인자 하나(안쪽 묶음의 인자도 된다)의 글자를 바꾼다.</summary>
    public static EditResult SetArg(Site site, Statement st, Arg arg, string text)
    {
        if (!Form.Owns(st, arg)) return EditResult.Fail("인자 없음 · 이 문장의 것이 아니다");
        if (Form.BadText(text) is { } why) return EditResult.Fail(why);
        var sp = new Splice(site);
        sp.Replace(st, Form.Spans(st.Text, [(arg.Offset, arg.Text.Length, text.Trim())]));
        return sp.Commit();
    }

    /// <summary>꼬리 주석 글을 바꾼다. 없으면 더한다(문장 뒤 공백 4 + <c>/* 글 */</c>). null·빈 글자 = 지운다.</summary>
    public static EditResult SetComment(Site site, Statement st, string? text)
    {
        var sp = new Splice(site);
        sp.Replace(st, WithComment(st, text, out var why));
        return why != null ? EditResult.Fail(why) : sp.Commit();
    }

    internal static string WithComment(Statement st, string? text, out string? why)
    {
        why = null;
        text = text?.Trim();
        if (!string.IsNullOrEmpty(text) && Form.BadComment(text))
        {
            why = "주석 글 틀림";
            return st.Text;
        }
        var tail = st.Tail;
        int i = tail.IndexOf("/*", StringComparison.Ordinal), j = tail.IndexOf("//", StringComparison.Ordinal);
        int k = i < 0 ? j : j < 0 ? i : Math.Min(i, j);
        string newTail;
        if (k < 0)
            newTail = string.IsNullOrEmpty(text) ? tail : tail.TrimEnd(' ', '\t') + Form.Tail(text);
        else
        {
            var old = st.TailComment;
            var rest = tail[(k + old.Length)..];
            newTail = string.IsNullOrEmpty(text) ? tail[..k].TrimEnd(' ', '\t') + rest
                : tail[..k] + (old.StartsWith("//", StringComparison.Ordinal) ? "// " + text : "/* " + text + " */") + rest;
        }
        int ts = Form.TailStart(st);
        return st.Text[..ts] + newTail + st.Text[(ts + tail.Length)..];
    }

    /// <summary>인자를 index 자리에 넣는다(끝이면 index = 인자 수). 태그 동작은 <see cref="TagRule.ActionStart"/> 기준.</summary>
    public static EditResult InsertArg(Site site, Statement st, int index, string text)
    {
        if (Form.BadText(text) is { } why) return EditResult.Fail(why);
        if (!st.HasParens || st.Args.Count == 0 || index < 0 || index > st.Args.Count) return EditResult.Fail("자리 틀림");
        var list = ArgList(st);
        if (index == list.Count)
        {
            var last = list[^1];
            list[^1] = last with { Trail = "" };
            list.Add((" ", text.Trim(), last.Trail));
        }
        else list.Insert(index, (list[index].Lead, text.Trim(), ""));
        var sp = new Splice(site);
        sp.Replace(st, Form.Args(st, list));
        return sp.Commit();
    }

    /// <summary>인자 하나를 지운다. 마지막 남은 인자는 지우지 않는다.</summary>
    public static EditResult DeleteArg(Site site, Statement st, int index)
    {
        if (st.Args.Count < 2 || index < 0 || index >= st.Args.Count) return EditResult.Fail("자리 틀림");
        var list = ArgList(st);
        var gone = list[index];
        list.RemoveAt(index);
        if (index == list.Count) list[^1] = list[^1] with { Trail = gone.Trail };
        var sp = new Splice(site);
        sp.Replace(st, Form.Args(st, list));
        return sp.Commit();
    }

    /// <summary>인자 순서 바꾸기: from 자리의 글자를 to 자리로. 자리마다의 공백은 그 자리에 남는다.</summary>
    public static EditResult MoveArg(Site site, Statement st, int from, int to)
    {
        int n = st.Args.Count;
        if (from < 0 || from >= n || to < 0 || to >= n) return EditResult.Fail("자리 틀림");
        var texts = st.Args.Select(a => a.Text).ToList();
        var t = texts[from];
        texts.RemoveAt(from);
        texts.Insert(to, t);
        var list = st.Args.Select((a, i) => (a.Lead, texts[i], a.Trail)).ToList();
        var sp = new Splice(site);
        sp.Replace(st, Form.Args(st, list));
        return sp.Commit();
    }

    internal static List<(string Lead, string Text, string Trail)> ArgList(Statement st) => st.Args.Select(a => (a.Lead, a.Text, a.Trail)).ToList();

    // ---- 줄 단위 ----

    /// <summary>
    /// 구역에 문장을 넣는다. index = 구역 문장 순번 자리(0 = 첫 문장 앞, n = 끝 문장 뒤). 0 이 아니면 index-1 번 문장 바로 뒤에 붙는다.
    /// 들여쓰기 = 바로 앞 문장(없으면 공백 4), 인자 앞 공백 = 같은 구역 바로 위 같은 매크로 문장(없으면 한 칸).
    /// </summary>
    public static EditResult Insert(Site site, ZoneBlock zone, int index, IReadOnlyList<NewStatement> items)
    {
        var sp = new Splice(site);
        InsertInto(sp, zone, index, items);
        return sp.Commit();
    }

    internal static void InsertInto(Splice sp, ZoneBlock zone, int index, IReadOnlyList<NewStatement> items)
    {
        if (!sp.Site.Zones.Contains(zone)) { sp.Fail("구역 없음"); return; }
        if (index < 0 || index > zone.Statements.Count) { sp.Fail("자리 틀림"); return; }
        if (items.Count == 0) { sp.Fail("넣을 줄 없음"); return; }
        int body = index == 0
            ? (zone.Statements.Count > 0 ? BodyIndex(zone, zone.Statements[0]) : zone.Body.Count)
            : BodyIndex(zone, zone.Statements[index - 1]) + 1;
        InsertAt(sp, zone, body, items);
    }

    /// <summary>구역 Body 의 body 자리(그 조각 앞, Count 면 끝 표지 앞)에 새 문장들을 쓴다.</summary>
    internal static void InsertAt(Splice sp, ZoneBlock zone, int body, IReadOnlyList<NewStatement> items)
    {
        var above = zone.Body.Take(body).OfType<Statement>().ToList();
        var (anchor, before) = Anchor(zone, body);
        var eol = sp.Eol(anchor);
        var done = new List<(string Name, string Indent, Form.Shape? Shape)>();
        var text = new System.Text.StringBuilder();
        foreach (var it in items)
        {
            if (!Form.Ident.IsMatch(it.Name)) { sp.Fail($"이름 틀림 · {it.Name}"); return; }
            if (it.Args.FirstOrDefault(a => Form.BadText(a) != null) is { } bad) { sp.Fail($"{Form.BadText(bad)} · {it.Name}"); return; }
            if (it.Comment != null && Form.BadComment(it.Comment)) { sp.Fail("주석 글 틀림"); return; }
            var indent = done.Count > 0 ? done[^1].Indent : above.LastOrDefault()?.Indent ?? "    ";
            var like = done.LastOrDefault(d => d.Name == it.Name).Shape
                ?? (above.LastOrDefault(s => s.Name == it.Name) is { } t ? Form.Shape.Of(t) : null);
            var args = it.Args.Select(a => a.Trim()).ToList();
            text.Append(Form.Statement(indent, it.Name, args, like, it.Comment)).Append(eol);
            done.Add((it.Name, indent, like ?? new Form.Shape("", args.Select(_ => " ").ToList(), args.Select((_, i) => i == args.Count - 1 ? " " : "").ToList())));
        }
        sp.Insert(anchor, before, text.ToString());
    }

    internal static int BodyIndex(ZoneBlock zone, Piece p)
    {
        for (int i = 0; i < zone.Body.Count; i++) if (ReferenceEquals(zone.Body[i], p)) return i;
        return -1;
    }

    /// <summary>Body 의 body 자리에 넣을 때 기댈 조각: 그 자리 조각 앞, 끝이면 끝 표지 앞.</summary>
    internal static (Piece Anchor, bool Before) Anchor(ZoneBlock zone, int body) =>
        body < zone.Body.Count ? (zone.Body[body], true)
        : zone.End != null ? (zone.End, true)
        : zone.Body.Count > 0 ? (zone.Body[^1], false) : (zone.Begin, false);

    /// <summary>문장·원문·주석 조각 여럿을 지운다(줄 전체). 위의 독립 주석은 남는다. 구역 표지는 지우지 않는다.</summary>
    public static EditResult Delete(Site site, IReadOnlyList<Piece> pieces)
    {
        if (pieces.Count == 0) return EditResult.Fail("고른 줄 없음");
        if (pieces.FirstOrDefault(p => IsMarker(site, p)) is { } m) return EditResult.Fail($"구역 표지 · {m.StartLine}줄");
        var sp = new Splice(site);
        foreach (var p in pieces) sp.Delete(p);
        return sp.Commit();
    }

    static bool IsMarker(Site site, Piece p) => site.Zones.Any(z => ReferenceEquals(z.Begin, p) || ReferenceEquals(z.End, p));

    /// <summary>
    /// 조각 여럿(같은 파일)을 줄째 옮긴다(꼬리 주석 포함, 고른 차례 = 파일 차례). index = 옮기기 전 구역 문장 순번 자리:
    /// 옮긴 줄들이 index-1 번과 index 번 문장 사이(옮기는 줄은 빼고 센다)에 들어간다.
    /// </summary>
    public static EditResult Move(Site site, IReadOnlyList<Piece> pieces, ZoneBlock zone, int index)
    {
        if (pieces.Count == 0) return EditResult.Fail("고른 줄 없음");
        if (!site.Zones.Contains(zone)) return EditResult.Fail("구역 없음");
        if (index < 0 || index > zone.Statements.Count) return EditResult.Fail("자리 틀림");
        if (pieces.FirstOrDefault(p => IsMarker(site, p)) is { } m) return EditResult.Fail($"구역 표지 · {m.StartLine}줄");
        var sp = new Splice(site);
        var order = new List<(int Index, Piece Piece)>();
        foreach (var p in pieces.Distinct<Piece>(ReferenceEqualityComparer.Instance))
        {
            if (!sp.Locate(p, out var f, out var i)) return EditResult.Fail(sp.Error!);
            if (f != zone.File) return EditResult.Fail("다른 파일");
            order.Add((i, p));
        }
        order.Sort((a, b) => a.Index.CompareTo(b.Index));
        var moving = new HashSet<Piece>(order.Select(o => o.Piece), ReferenceEqualityComparer.Instance);
        var text = string.Concat(order.Select(o => o.Piece.Text));
        if (!text.EndsWith('\n')) return EditResult.Fail("줄 끝 없음");

        var before = zone.Statements.Take(index).LastOrDefault(s => !moving.Contains(s));
        var after = zone.Statements.Skip(index).FirstOrDefault(s => !moving.Contains(s));
        foreach (var (_, p) in order) sp.Delete(p);
        if (before != null) sp.Insert(before, false, text);
        else if (after != null) sp.Insert(after, true, text);
        else if (zone.End != null) sp.Insert(zone.End, true, text);
        else return EditResult.Fail("구역 끝 없음");
        return sp.Commit();
    }

    /// <summary>
    /// 고급: 조각 하나의 원문 글자를 통째로 바꾼다(원문 블록 고치기 등). 원문 줄이 늘어도 된다.
    /// 조각이 줄 끝으로 끝났는데 새 글자에 없으면 그 줄 끝을 붙인다. 빈 글자 = 지우기.
    /// </summary>
    public static EditResult ReplaceText(Site site, Piece piece, string text)
    {
        if (IsMarker(site, piece)) return EditResult.Fail("구역 표지");
        if (text.Length > 0 && piece.LineEnd.Length > 0 && !text.EndsWith('\n')) text += piece.LineEnd;
        var sp = new Splice(site);
        sp.Replace(piece, text);
        return sp.Commit(allowRaw: true);
    }
}
