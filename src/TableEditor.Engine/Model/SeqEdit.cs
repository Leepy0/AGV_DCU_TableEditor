using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Text;

namespace TableEditor.Engine.Model;

/// <summary>
/// 시퀀스 구조 편집(스텝 넣기·지우기·옮기기는 <see cref="SiteEdit"/> 그대로): 명령 바꾸기, 표 지우기, 지우기 전 참조 찾기.
/// 모두 한 명령 = 한 되돌리기 단계. 고치지 않은 줄·주석은 글자 그대로다(<see cref="Splice"/>).
/// </summary>
public static class SeqEdit
{
    /// <summary>시퀀스 표에 넣을 수 있는 스텝 문법 줄(ACT 표면 ACT 금지 줄을 뺀다).</summary>
    public static IEnumerable<GrammarEntry> StepRows(bool act) =>
        GrammarTable.Rows.Where(r => r.Place == Place.Step && r.Role == Role.Statement && !(act && r.ActForbidden));

    /// <summary>스텝 문장의 문법 줄(모르면 null).</summary>
    public static GrammarEntry? EntryOf(Statement st) => Facts.Fit(GrammarTable.Find(st.Name, Place.Step).Where(r => r.Role == Role.Statement), st.Args);

    /// <summary>
    /// 명령 바꾸기: 스텝 한 줄을 to 꼴로 다시 쓴다. 같은 종류 칸의 인자는 옮기고 없는 칸은 fill(<see cref="ActionForms.Convert"/>).
    /// 들여쓰기·꼬리 주석·줄 끝은 그대로.
    /// </summary>
    public static EditResult Change(Site site, Statement st, GrammarEntry to, Func<ArgSpec, string> fill)
    {
        var from = EntryOf(st);
        var call = from == null ? Form.Call(to.Name, to.Args.Where(a => a.Least > 0).Select(fill).ToList()) : ActionForms.Convert(to, from, st.Args, fill);
        if (Form.BadText(call) is { } why) return EditResult.Fail(why);
        var sp = new Splice(site);
        sp.Replace(st, st.Indent + call + st.Text[Form.TailStart(st)..]);
        return sp.Commit();
    }

    /// <summary>라벨 줄 <c>L( 이름 )</c> 을 지우면 끊기는 곳: 같은 표 안에서 그 라벨을 쓰는 문장.</summary>
    public static IReadOnlyList<Statement> LabelUses(ZoneBlock zone, Statement label)
    {
        if (label.Name != "L" || label.Args.Count == 0) return [];
        var name = label.Args[0].Text.Trim();
        return zone.Statements.Where(s => !ReferenceEquals(s, label) && s.Args.Any(a => a.Node.Names().Contains(name))).ToList();
    }

    /// <summary>
    /// 표를 지우면 끊기는 곳: 이 표를 가리키는 대응표 줄, 그 대응표 줄의 ID 를 시작하는 태그 동작(<c>SEQ_START</c>·<c>ACT_START</c>,
    /// 태그 줄과 대응표 줄 가운데 하나가 전체이거나 프로파일이 같은 것)·다른 표의 스텝(<c>ACT_START</c>·<c>WAIT_ACT</c>).
    /// </summary>
    public static IReadOnlyList<Statement> TableUses(Site site, string table)
    {
        var maps = site.SeqMap.Concat(site.ActMap).Where(m => m.Table.Trim() == table).ToList();
        var ids = maps.Select(m => m.Id.Trim()).ToHashSet();
        static bool Starts(Node n, string id) => n is CallNode c && (c.Name == "SEQ_START" && c.Args.Count > 0 && c.Args[^1].Node is NameNode s && "SEQ_" + s.Name == id
            || c.Name == "ACT_START" && c.Args.Count == 1 && c.Args[0].Node is NameNode a && a.Name == id);
        var tags = site.TagRules.Where(r => maps.Any(m => (m.AnyProfile || r.AnyProfile || r.Profile.Trim() == m.Profile.Trim())
            && r.Actions.Any(a => Starts(a.Node, m.Id.Trim())))).Select(r => r.St);
        var steps = site.Seqs.Where(t => t.Name != table).SelectMany(t => t.Steps)
            .Where(s => s.Name is "ACT_START" or "WAIT_ACT" && s.Args.Count == 1 && ids.Contains(s.Args[0].Text.Trim()));
        return [.. maps.Select(m => m.St), .. tags, .. steps];
    }

    /// <summary>
    /// 표 하나를 통째로 지운다(<c>SEQ_BEGIN</c> … <c>SEQ_END</c>)와 그 표를 가리키는 대응표 줄. 태그·스텝 참조 확인은 부른 쪽(<see cref="TableUses"/>).
    /// </summary>
    public static EditResult DeleteTable(Site site, string table)
    {
        var zone = site.Zones.FirstOrDefault(z => z.Open is "SEQ_BEGIN" or "ACT_BEGIN" && z.Name == table);
        if (zone == null) return EditResult.Fail($"없는 표 · {table}");
        if (zone.End == null) return EditResult.Fail($"끝 표지 없음 · {table}");
        var sp = new Splice(site);
        sp.Delete(zone.Begin);
        foreach (var p in zone.Body) sp.Delete(p);
        sp.Delete(zone.End);
        // 앞이 빈 줄이면 뒤 빈 줄 하나도 지운다(표 추가가 넣은 빈 줄, 빈 줄 둘이 겹치지 않게)
        var ps = zone.File.Pieces;
        int b = IndexOf(ps, zone.Begin), e = IndexOf(ps, zone.End);
        if (b > 0 && ps[b - 1].Kind == PieceKind.Blank && e + 1 < ps.Count && ps[e + 1].Kind == PieceKind.Blank) sp.Delete(ps[e + 1]);
        foreach (var m in site.SeqMap.Concat(site.ActMap).Where(m => m.Table.Trim() == table)) sp.Delete(m.St);
        return sp.Commit();
    }

    static int IndexOf(IReadOnlyList<Piece> ps, Piece p)
    {
        for (int i = 0; i < ps.Count; i++) if (ReferenceEquals(ps[i], p)) return i;
        return -1;
    }
}
