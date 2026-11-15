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

    /// <summary>
    /// 라벨 줄 <c>L( 이름 )</c> 을 지우거나 바꾸면 끊기는 곳: 같은 표 안에서 그 라벨을 쓰는 문장, 그리고 그 이름 글자가 든 원문 조각.
    /// </summary>
    public static IReadOnlyList<Piece> LabelUses(ZoneBlock zone, Statement label)
    {
        if (label.Name != "L" || label.Args.Count == 0) return [];
        var name = label.Args[0].Text.Trim();
        return zone.Body.Where(p => !ReferenceEquals(p, label) && (p is Statement s
            ? s.Args.Any(a => a.Node.Names().Contains(name))
            : p.Kind == PieceKind.Raw && Token(name).IsMatch(p.Text))).ToList();
    }

    static System.Text.RegularExpressions.Regex Token(string name) => new($@"(?<![A-Za-z0-9_]){System.Text.RegularExpressions.Regex.Escape(name)}(?![A-Za-z0-9_])");

    /// <summary>표 하나를 지우면 걸리는 곳. Maps = 같이 지울 대응표 줄, Blocking = 그 표를 시작하는 태그 동작·스텝(막는다), Maybe = 글자로만 찾은 곳(알린다).</summary>
    public sealed record TableRefs(IReadOnlyList<Statement> Maps, IReadOnlyList<Statement> Blocking, IReadOnlyList<Piece> Maybe);

    /// <summary>
    /// 표를 지우면 끊기는 곳. ID·프로파일은 글자가 아니라 값으로 견준다(식·숫자로 쓴 ID, 숫자 프로파일).
    /// Blocking: 그 대응표 줄의 ID 를 시작하는 태그 <c>SEQ_START</c>·<c>ACT_START</c>(태그 줄과 대응표 줄 가운데 하나가 전체이거나 프로파일 값이 같음),
    /// 다른 표의 <c>ACT_START</c>·<c>WAIT_ACT</c>.
    /// Maybe: 표 이름·그 ID 값을 가진 이름(<c>SEQ_</c> 뺀 꼴 포함) 글자가 든 원문 조각·<c>#define</c>(함수 꼴 별칭 등)·위 둘이 아닌 칸.
    /// </summary>
    public static TableRefs TableUses(Site site, string table)
    {
        var maps = site.SeqMap.Concat(site.ActMap).Where(m => m.Table.Trim() == table).ToList();
        var zone = site.Zones.FirstOrDefault(z => z.Open is "SEQ_BEGIN" or "ACT_BEGIN" && z.Name == table);
        long? Val(Arg a) => site.Num(a);
        bool AnyPf(Arg a) => a.Text.Trim() is "ANY_PROFILE" || Val(a) == 0;
        var seqIds = maps.Where(m => !m.IsAct).Select(m => (Id: Val(m.St.Args[1]), Map: m)).Where(x => x.Id != null).ToList();
        var actIds = maps.Where(m => m.IsAct).Select(m => Val(m.St.Args[1])).OfType<long>().ToHashSet();

        bool Starts(Node n, TagRule r) => n is CallNode c && (
            c.Name == "SEQ_START" && c.Args.Count > 0 && c.Args[^1].Node is NameNode s && site.Names.Find("SEQ_" + s.Name)?.Value is long v
                && seqIds.Any(x => x.Id == v && (AnyPf(x.Map.St.Args[0]) || AnyPf(r.ProfileArg) || Val(x.Map.St.Args[0]) == Val(r.ProfileArg)))
            || c.Name == "ACT_START" && c.Args.Count == 1 && Val(c.Args[0]) is long a && actIds.Contains(a));
        bool Structured(Node n) => n is CallNode { Name: "SEQ_START" or "ACT_START" };

        var blocking = new List<Statement>();
        blocking.AddRange(site.TagRules.Where(r => r.Actions.Any(a => Starts(a.Node, r))).Select(r => r.St));
        blocking.AddRange(site.Seqs.Where(t => t.Name != table).SelectMany(t => t.Steps)
            .Where(s => s.Name is "ACT_START" or "WAIT_ACT" && s.Args.Count == 1 && Val(s.Args[0]) is long a && actIds.Contains(a)));

        // 글자로 찾을 이름: 표 이름 + 그 ID 값을 가진 시퀀스·ACT ID 이름(SEQ_ 뺀 꼴도)
        var values = seqIds.Select(x => x.Id!.Value).ToHashSet();
        var names = new HashSet<string>([table]);
        foreach (var d in site.Names.All.Where(d => d.Kind == NameKind.SeqId && d.Value is long v && values.Contains(v) || d.Kind == NameKind.ActId && d.Value is long w && actIds.Contains(w)))
        {
            names.Add(d.Name);
            if (d.Name.StartsWith("SEQ_", StringComparison.Ordinal)) names.Add(d.Name[4..]);
        }
        var defs = site.Names.All.Where(d => names.Contains(d.Name) && d.Def != null).Select(d => d.Def!).ToHashSet<Piece>(ReferenceEqualityComparer.Instance);
        var skip = new HashSet<Piece>(ReferenceEqualityComparer.Instance);
        if (zone != null) { skip.Add(zone.Begin); foreach (var p in zone.Body) skip.Add(p); if (zone.End != null) skip.Add(zone.End); }
        foreach (var m in site.SeqMap.Concat(site.ActMap)) skip.Add(m.St);
        foreach (var b in blocking) skip.Add(b);
        var tokens = names.Select(Token).ToList();
        bool Hit(string text) => tokens.Any(t => t.IsMatch(text));
        var maybe = site.Files.SelectMany(f => f.Pieces).Where(p => !skip.Contains(p) && !defs.Contains(p) && p.Kind is PieceKind.Raw or PieceKind.Statement && p switch
        {
            Statement { Shape: StatementShape.Macro } st => st.Args.Any(a => !Structured(a.Node) && !(st.Name is "ACT_START" or "WAIT_ACT") && Hit(a.Text)),
            _ => Hit(p.Text),
        }).ToList();
        return new TableRefs(maps.Select(m => m.St).ToList(), blocking, maybe);
    }

    /// <summary>
    /// 표 하나를 통째로 지운다: 바로 위에 빈 줄 없이 붙은 머리 주석 + <c>SEQ_BEGIN</c> … <c>SEQ_END</c> + 그 표를 가리키는 대응표 줄.
    /// 그 위가 빈 줄(또는 파일 처음)이면 뒤 빈 줄 한 줄도 지운다(빈 줄이 겹치지 않게, 표 추가가 넣은 빈 줄). 태그·스텝 참조 확인은 부른 쪽(<see cref="TableUses"/>).
    /// </summary>
    public static EditResult DeleteTable(Site site, string table)
    {
        var zone = site.Zones.FirstOrDefault(z => z.Open is "SEQ_BEGIN" or "ACT_BEGIN" && z.Name == table);
        if (zone == null) return EditResult.Fail($"없는 표 · {table}");
        if (zone.End == null) return EditResult.Fail($"끝 표지 없음 · {table}");
        var sp = new Splice(site);
        var ps = zone.File.Pieces;
        int b = IndexOf(ps, zone.Begin), e = IndexOf(ps, zone.End);
        if (b > 0 && ps[b - 1].Kind == PieceKind.Comment) sp.Delete(ps[--b]);
        sp.Delete(zone.Begin);
        foreach (var p in zone.Body) sp.Delete(p);
        sp.Delete(zone.End);
        if ((b == 0 || ps[b - 1].Kind == PieceKind.Blank) && e + 1 < ps.Count && ps[e + 1].Kind == PieceKind.Blank)
        {
            var t = ps[e + 1].Text; // 이어진 빈 줄은 한 조각: 첫 줄만 뗀다
            int nl = t.IndexOf('\n');
            sp.Replace(ps[e + 1], nl < 0 ? "" : t[(nl + 1)..], mark: false);
        }
        foreach (var m in site.SeqMap.Concat(site.ActMap).Where(m => m.Table.Trim() == table)) sp.Delete(m.St);
        return sp.Commit();
    }

    static int IndexOf(IReadOnlyList<Piece> ps, Piece p)
    {
        for (int i = 0; i < ps.Count; i++) if (ReferenceEquals(ps[i], p)) return i;
        return -1;
    }
}
