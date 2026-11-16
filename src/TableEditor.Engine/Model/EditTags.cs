using System.Text.RegularExpressions;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Text;

namespace TableEditor.Engine.Model;

/// <summary>태그 여러 줄 추가의 연속 번호: 줄마다 TAG 한 줄 / 이어진 번호를 TAG_RANGE 한 줄로.</summary>
public enum TagRunMode { PerLine, Range }

/// <summary>미리 보기 한 줄: (태그, 프로파일)마다. Exists = 같은 태그·프로파일·조건의 규칙이 이미 있어 건너뛴다.</summary>
public sealed record TagAddRow(long Tag, string Profile, bool Exists);

/// <summary>쓸 줄 하나. First = Last 면 TAG(_IF), 아니면 TAG_RANGE(_IF).</summary>
public sealed record TagAddLine(long First, long Last, string Profile);

/// <summary>태그 여러 줄 추가 계획. Error 가 있으면 Rows·Lines 는 비었다.</summary>
public sealed record TagAddPlan(IReadOnlyList<TagAddRow> Rows, IReadOnlyList<TagAddLine> Lines, string? Error);

/// <summary>확장 조건 하나 <c>X_…</c>. Args = 인자 글자. Text = 문법 표의 꼴(<c>X_ID(20, 25)</c>).</summary>
public sealed record XCond(string Name, IReadOnlyList<string> Args)
{
    public string Text => Form.Call(Name, Args);

    public static XCond? FromNode(Node n) => n switch
    {
        CallNode c => new XCond(c.Name, c.Args.Select(a => a.Text).ToList()),
        NameNode m => new XCond(m.Name, []),
        _ => null,
    };
}

/// <summary>태그 표 명령: 여러 줄 추가, 번호 바꾸기, 조건 바꾸기, 동작 넣기·지우기·순서.</summary>
public static partial class TagEdit
{
    // ---- 태그 번호 글 ----

    [GeneratedRegex(@"^(\d+)\s*(?:[-~]\s*(\d+))?$")]
    private static partial Regex ItemRx();

    /// <summary><c>101-103, 110</c> → 태그 번호(작은 차례, 겹침 없음). 틀리면 null 과 까닭.</summary>
    public static IReadOnlyList<long>? ParseTags(string text, out string? error)
    {
        error = null;
        var set = new SortedSet<long>();
        foreach (var raw in text.Split(','))
        {
            var item = raw.Trim();
            if (item.Length == 0) { if (raw.Length > 0 || text.Trim().Length == 0) { error = "번호 없음"; return null; } continue; }
            var m = ItemRx().Match(item);
            if (!m.Success) { error = $"번호 글 틀림 · {item}"; return null; }
            if (!long.TryParse(m.Groups[1].Value, out var a) || !long.TryParse(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value, out var b)
                || a > 1023 || b > 1023) { error = $"범위 밖 · {item} · 0~1023"; return null; }
            if (a > b) { error = $"처음 > 끝 · {item}"; return null; }
            for (long t = a; t <= b; t++) set.Add(t);
        }
        if (set.Count == 0) { error = "번호 없음"; return null; }
        return set.ToList();
    }

    // ---- 여러 줄 추가 ----

    static ZoneBlock? TagZone(Site site) => site.ZonesOf("TAG_TABLE_BEGIN").FirstOrDefault();

    static bool IsAny(string profile) => profile is "ANY_PROFILE" or "0";

    static bool SameProfile(TagRule r, string profile) => IsAny(profile) ? r.AnyProfile : r.Profile == profile;

    /// <summary>
    /// 미리 보기. template = 본뜰 줄(조건·동작을 베낀다), 없으면 동작 <c>AGV_STOP</c> 한 칸, 조건 없음.
    /// 같은 태그(범위 규칙이 덮는 것 포함)·같은 프로파일·같은 조건 글자의 규칙이 있으면 그 (태그, 프로파일)은 <c>있음</c>.
    /// </summary>
    public static TagAddPlan PlanAdd(Site site, string tags, IReadOnlyList<string> profiles, TagRule? template, TagRunMode mode)
    {
        static TagAddPlan Bad(string why) => new([], [], why);
        if (TagZone(site) == null) return Bad("태그 표 없음");
        var list = ParseTags(tags, out var err);
        if (list == null) return Bad(err!);
        var pfs = profiles.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct().ToList();
        if (pfs.Count == 0) return Bad("프로파일 없음");
        foreach (var p in pfs)
            if (!IsAny(p) && site.Names.Find(p) is not { Kind: NameKind.Profile }) return Bad($"없는 프로파일 · {p}");

        var cond = template == null ? CondKey([], null) : CondKey(template);
        var rules = site.TagRules.Select(r => (Rule: r, First: site.Num(r.FirstArg), Last: site.Num(r.LastArg), Key: CondKey(r))).ToList();
        var rows = new List<TagAddRow>();
        var lines = new List<TagAddLine>();
        foreach (var p in pfs)
        {
            long? runStart = null, runEnd = null;
            void Flush()
            {
                if (runStart == null) return;
                if (mode == TagRunMode.Range) lines.Add(new(runStart.Value, runEnd!.Value, p));
                else for (long t = runStart.Value; t <= runEnd!.Value; t++) lines.Add(new(t, t, p));
                runStart = runEnd = null;
            }
            foreach (var t in list)
            {
                bool exists = rules.Any(x => x.First <= t && t <= x.Last && SameProfile(x.Rule, p) && x.Key == cond);
                rows.Add(new(t, p, exists));
                if (exists || runEnd != null && runEnd + 1 != t) Flush();
                if (exists) continue;
                runStart ??= t;
                runEnd = t;
            }
            Flush();
        }
        return new(rows, lines, null);
    }

    /// <summary>미리 보기의 <c>새 줄</c> 을 쓴다. 넣는 자리는 <see cref="Placer"/>.</summary>
    public static EditResult Add(Site site, string tags, IReadOnlyList<string> profiles, TagRule? template, TagRunMode mode)
    {
        var plan = PlanAdd(site, tags, profiles, template, mode);
        if (plan.Error != null) return EditResult.Fail(plan.Error);
        if (plan.Lines.Count == 0) return EditResult.Fail("새 줄 없음 · 모두 있음");
        var zone = TagZone(site)!;
        var sp = new Splice(site);
        var cond = template?.Cond?.Text;
        var actions = template?.Actions.Select(a => a.Text).ToList() ?? ["AGV_STOP"];
        var placer = new Placer(site, zone, null);
        foreach (var l in plan.Lines.OrderBy(l => l.First).ThenBy(l => IsAny(l.Profile) ? 1 : 0))
        {
            bool range = l.First != l.Last;
            var name = (range ? "TAG_RANGE" : "TAG") + (cond != null ? "_IF" : "");
            var args = new List<string> { l.First.ToString() };
            if (range) args.Add(l.Last.ToString());
            args.Add(l.Profile);
            if (cond != null) args.Add(cond);
            args.AddRange(actions);
            if (Probe(Form.Statement("", name, args, null, null)) is not { } rule) return EditResult.Fail($"문장으로 읽히지 않음 · {name}");
            placer.Place(rule, new NewStatement(name, args));
        }
        foreach (var (at, item) in placer.Placed) SiteEdit.InsertAt(sp, zone, at, [(NewStatement)item]);
        return sp.Commit();
    }

    /// <summary>줄 글자 하나를 태그 규칙으로 읽어 본다(자리 계산용. 사이트에 넣지 않는다). 규칙 한 줄이 아니면 null.</summary>
    static TagRule? Probe(string line)
    {
        var text = "TAG_TABLE_BEGIN( probe )\n" + line + (line.EndsWith('\n') ? "" : "\n") + "TAG_TABLE_END\n";
        var f = SourceFile.Parse("tag_table.c", System.Text.Encoding.UTF8.GetBytes(text));
        var st = f.Statements.Where(s => s.Zone != null).ToList();
        return f.ReadOnly || f.RawInZones.Any() || st.Count != 1 ? null : new TagRule(st[0]);
    }

    /// <summary>
    /// 새 규칙을 넣을 Body 자리를 정한다. 한 명령이 넣는 줄을 차례로 받아, 있는 줄과 앞서 넣은 줄을 함께 본다.
    /// 바라는 자리: 같은 태그 묶음(첫 태그가 같은 규칙)이 있으면 그 묶음의 첫 ANY_PROFILE 줄 앞(없으면 묶음 끝 줄 뒤),
    /// 없으면 더 작은 태그 가운데 가장 가까운 묶음의 끝 줄 뒤, 그것도 없으면 첫 규칙 앞.
    /// 그 자리가 죽은 줄(T-10·T-11)을 만들면 옮긴다: 새 줄을 가리는 규칙(범위 규칙, ANY_PROFILE, 조건 없는 줄)이 있으면 그 앞,
    /// 새 줄이 가리는 규칙이 있으면 그 뒤. 둘을 다 지킬 수 없으면 새 줄이 죽지 않는 쪽으로 넣는다(가려진 줄은 검사가 알린다).
    /// </summary>
    internal sealed class Placer
    {
        /// <summary>표 차례의 줄 하나. At = 있는 줄이면 Body 자리, 넣는 줄이면 그 줄이 들어갈 Body 자리(그 조각 앞).</summary>
        sealed record Row(TagRule Rule, long? First, int At, object? Item);

        readonly Site site;
        readonly ZoneBlock zone;
        readonly Check.Facts facts;
        readonly List<Row> order;

        /// <param name="skip">셈에서 뺄 규칙(옮기는 줄).</param>
        public Placer(Site site, ZoneBlock zone, ISet<Piece>? skip)
        {
            this.site = site;
            this.zone = zone;
            facts = new Check.Facts(site);
            order = zone.Statements.Where(s => skip == null || !skip.Contains(s))
                .Select(s => new Row(new TagRule(s), site.Num(s.Args[0]), SiteEdit.BodyIndex(zone, s), null)).ToList();
        }

        /// <summary>넣은 줄들(표 차례)과 그 Body 자리. 이 차례로 넣으면 같은 자리의 줄끼리도 차례가 맞는다.</summary>
        public IEnumerable<(int At, object Item)> Placed => order.Where(r => r.Item != null).Select(r => (r.At, r.Item!));

        public void Place(TagRule rule, object item)
        {
            long? tag = site.Num(rule.FirstArg);
            var group = Enumerable.Range(0, order.Count).Where(i => order[i].First == tag).ToList();
            int pos;
            bool before; // true = order[pos] 바로 앞에 붙는다, false = order[pos - 1] 바로 뒤에 붙는다
            if (group.Count > 0)
            {
                int any = group.FindIndex(i => order[i].Rule.AnyProfile);
                (pos, before) = any >= 0 ? (group[any], true) : (group[^1] + 1, false);
            }
            else
            {
                var smaller = Enumerable.Range(0, order.Count).Where(i => order[i].First < tag).ToList();
                if (smaller.Count > 0)
                {
                    var m = smaller.Max(i => order[i].First);
                    (pos, before) = (smaller.Last(i => order[i].First == m) + 1, false);
                }
                else (pos, before) = (0, true);
            }

            int lower = 0, upper = order.Count;
            for (int i = 0; i < order.Count; i++)
            {
                if (upper == order.Count && Check.Checker.Hides(facts, order[i].Rule, rule)) upper = i;
                if (Check.Checker.Hides(facts, rule, order[i].Rule)) lower = i + 1;
            }
            if (pos < lower) (pos, before) = (lower, false);
            if (pos > upper) (pos, before) = (upper, true);

            int at = before
                ? (pos < order.Count ? order[pos].At : zone.Body.Count)
                : order[pos - 1].At + (order[pos - 1].Item == null ? 1 : 0);
            order.Insert(pos, new Row(rule, tag, at, item));
        }
    }

    // ---- 번호 바꾸기 ----

    /// <summary>
    /// 고른 규칙들의 첫 태그를 tag 로(범위 규칙은 끝도 같은 만큼 민다). 바뀐 줄은 새 번호의 자리(<see cref="Placer"/>)로 옮긴다.
    /// </summary>
    public static EditResult Renumber(Site site, IReadOnlyList<TagRule> rules, long tag)
    {
        var zone = TagZone(site);
        if (zone == null) return EditResult.Fail("태그 표 없음");
        if (rules.Count == 0) return EditResult.Fail("고른 줄 없음");
        if (tag < 0 || tag > 1023) return EditResult.Fail($"범위 밖 · {tag} · 0~1023");
        var moves = new List<(TagRule Rule, string Text)>();
        foreach (var r in rules.DistinctBy(r => (object)r.St, ReferenceEqualityComparer.Instance)
                     .OrderBy(r => SiteEdit.BodyIndex(zone, r.St)))
        {
            if (SiteEdit.BodyIndex(zone, r.St) < 0) return EditResult.Fail("태그 표 밖의 줄");
            if (site.Num(r.FirstArg) is not long first || site.Num(r.LastArg) is not long last) return EditResult.Fail($"번호 모름 · {r.FirstArg.Text}");
            if (first == tag) continue;
            long newLast = r.IsRange ? last + (tag - first) : tag;
            if (newLast > 1023) return EditResult.Fail($"범위 밖 · 끝 {newLast} · 0~1023");
            var spans = new List<(int, int, string)> { (r.FirstArg.Offset, r.FirstArg.Text.Length, tag.ToString()) };
            if (r.IsRange) spans.Add((r.LastArg.Offset, r.LastArg.Text.Length, newLast.ToString()));
            moves.Add((r, Form.Spans(r.St.Text, spans)));
        }
        if (moves.Count == 0) return new EditResult(site, null, []);
        var skip = new HashSet<Piece>(moves.Select(m => (Piece)m.Rule.St), ReferenceEqualityComparer.Instance);
        var sp = new Splice(site);
        foreach (var m in moves) sp.Delete(m.Rule.St);
        var placer = new Placer(site, zone, skip);
        foreach (var m in moves.OrderBy(m => m.Rule.AnyProfile ? 1 : 0))
        {
            if (Probe(m.Text) is not { } rule) return EditResult.Fail($"문장으로 읽히지 않음 · {m.Rule.FirstArg.Text}");
            placer.Place(rule, m.Text);
        }
        foreach (var (at, item) in placer.Placed)
        {
            var (anchor, before) = SiteEdit.Anchor(zone, at);
            sp.Insert(anchor, before, (string)item);
        }
        return sp.Commit();
    }

    // ---- 조건 ----

    /// <summary>줄임 꼴이 있는 확장 조건(사양 5.4절 태그 조건 꼴 고르기): X_ 를 뗀 이름.</summary>
    static readonly HashSet<string> Short = ["FORWARD", "BACKWARD", "FORBACK_MODE", "LOW_BATTERY", "ID", "STATION", "STATION_RUNNING", "STATION_MOD", "STATION_DIV", "TAG_NUM", "PARAM"];

    static readonly HashSet<string> XNames = GrammarTable.Rows.Where(r => r.Place is Place.ExtCond or Place.StateCond).Select(r => r.Name).ToHashSet();

    /// <summary>
    /// 입력 조건 글 → <c>AND( … )</c> 안 항목 글자들. <c>IN( … )</c> 은 묶음마다, <c>NO_IN</c> 은 없음, 풀 수 없는 글은 한 항목으로 그대로.
    /// <c>ANY_ON</c> 은 <c>ANY</c> 하나로 합친다: 코어는 AND 안의 ANY 둘째를 칸마다 막고, 옛 <c>IN( ANY_ON, ANY_ON )</c> 도 한 묶음(하나라도 켜짐)으로 합쳐 읽는다.
    /// 합친 ANY 가 코어 한계(16)를 넘으면 null: 옛 형식으로 쓴다.
    /// </summary>
    static IReadOnlyList<string>? AndItems(string? input)
    {
        if (input == null) return [];
        var items = new List<string>();
        var anys = new List<string>();
        int anyAt = -1;
        void One(Node n, string text)
        {
            if (n is CallNode { Name: "ANY_ON" } c)
            {
                if (anyAt < 0) { anyAt = items.Count; items.Add(""); }
                anys.AddRange(c.Args.Select(a => a.Text));
            }
            else items.Add(text);
        }
        switch (Reader.ParseArg(input))
        {
            case NameNode { Name: "NO_IN" }: return [];
            case CallNode { Name: "IN" } c: foreach (var a in c.Args) One(a.Node, a.Text); break;
            case CallNode { Name: "ON" or "OFF" or "ANY_ON" } c: One(c, input); break;
            default: items.Add(input); break;
        }
        if (anyAt >= 0)
        {
            var merged = anys.Distinct().ToList();
            if (merged.Count > AnyLimit) return null;
            items[anyAt] = Form.Call("ANY", merged);
        }
        return items;
    }

    /// <summary><c>ANY( … )</c> 신호 개수 한계(코어 <c>DSL_MASK16</c>).</summary>
    const int AnyLimit = 16;

    /// <summary>
    /// 조건 칸 글자(사양 5.4절 표). 확장·입력이 모두 없으면 null.
    /// newDsl(코어 v0.58.0 이상)이면 <c>AND( 상태 낱말…, 입력 항목… )</c> 하나로 쓴다(옛 <c>X_…</c> 는 새 낱말로 옮김).
    /// 옮길 수 없는 항목(<c>X_STATION_RUNNING</c>)이 하나라도 있으면 줄 전체를 옛 형식으로 쓰고 모든 상태 낱말을 <c>X_…</c> 로 되돌린다(한 줄에 두 낱말을 섞지 않는다).
    /// </summary>
    public static string? CondText(IReadOnlyList<XCond> x, string? input, bool newDsl = false)
    {
        input = string.IsNullOrWhiteSpace(input) ? null : input.Trim();
        if (newDsl)
        {
            var states = x.Select(StateWords.ToState).ToList();
            if (states.All(s => s != null) && AndItems(input) is { } andItems)
            {
                var items = states.Select(s => s!.Text).Concat(andItems).ToList();
                var and = items.Count == 0 ? null : Form.Call("AND", items);
                if (and == null || !IsMixed(and)) return and;
            }
        }
        var old = x.Select(c => StateWords.ToLegacy(c) ?? c).ToList();
        return (old.Count, input) switch
        {
            (0, null) => null,
            (1, null) when old[0].Name.StartsWith("X_", StringComparison.Ordinal) && Short.Contains(old[0].Name[2..]) => Form.Call("IF_" + old[0].Name[2..], old[0].Args),
            (1, null) => Form.Call("IF_X", [old[0].Text]),
            (2, null) => Form.Call("IF_X2", [old[0].Text, old[1].Text]),
            (0, _) => Form.Call("IF_IN", [input!]),
            (1, _) => Form.Call("IF_X_IN", [old[0].Text, input!]),
            _ => Form.Call("IF_X2_IN", [old[0].Text, old[1].Text, input!]),
        };
    }

    /// <summary>
    /// 조건 글이 옛·새 낱말을 섞었는가: <c>AND( … )</c> 안에 <c>X_…</c> 가 있거나, 옛 형식(<c>IF_…</c>)에 새 상태 낱말이 있다. 코어가 컴파일하지 못하는 줄이다.
    /// </summary>
    public static bool IsMixed(string cond)
    {
        static IEnumerable<string> Names(Node n) => n switch
        {
            CallNode c => c.Args.SelectMany(a => Names(a.Node)).Prepend(c.Name),
            NameNode m => [m.Name],
            _ => [],
        };
        if (Reader.ParseArg(cond) is not { } node) return false;
        var names = Names(node).ToList();
        return node is CallNode { Name: "AND" }
            ? names.Any(n => n.StartsWith("X_", StringComparison.Ordinal) && XNames.Contains(n))
            : names.Skip(1).Any(StateWords.IsWord);
    }

    static string Sq(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>
    /// 조건의 같은 뜻 열쇠: 상태 낱말 집합(옛 <c>X_…</c> 는 새 낱말로 옮겨 견준다)과 입력 묶음(켬·끔 신호 집합, 하나라도 켜짐 신호 집합).
    /// 옛 형식·새 형식으로 같은 뜻을 쓴 규칙이 같은 열쇠를 갖는다(죽은 줄·이미 있음 판정, 조건을 안 바꾼 확인).
    /// </summary>
    public static string CondKey(IReadOnlyList<XCond> x, string? input)
    {
        var states = x.Select(c => Sq((StateWords.ToState(c) ?? c).Text)).Distinct().Order(StringComparer.Ordinal);
        SortedSet<string> on = [], off = [], any = [], other = [];
        void One(Node n, string text)
        {
            switch (n)
            {
                case CallNode { Name: "IN" } c: foreach (var a in c.Args) One(a.Node, a.Text); break;
                case CallNode { Name: "ON" } c: on.UnionWith(c.Args.Select(a => Sq(a.Text))); break;
                case CallNode { Name: "OFF" } c: off.UnionWith(c.Args.Select(a => Sq(a.Text))); break;
                case CallNode { Name: "ANY_ON" or "ANY" } c: any.UnionWith(c.Args.Select(a => Sq(a.Text))); break;
                case NameNode { Name: "NO_IN" }: break;
                default: other.Add(Sq(text)); break;
            }
        }
        if (input != null) One(Reader.ParseArg(input) ?? new NameNode(input), input);
        return string.Join("|", string.Join(",", states), string.Join(",", on), string.Join(",", off), string.Join(",", any), string.Join(",", other));
    }

    /// <summary>규칙의 조건 열쇠(<see cref="CondKey(IReadOnlyList{XCond}, string?)"/>). 모르는 꼴이면 글자(공백 뺀)로 견준다.</summary>
    public static string CondKey(TagRule rule) =>
        ReadCondition(rule) is { } c ? CondKey(c.X, c.Input) : "?" + Sq(rule.CondText);

    /// <summary>규칙의 조건을 (확장 조건들, 입력 조건 글자)로 푼다. 조건 없음 = ([], null). 모르는 꼴이면 null.</summary>
    public static (IReadOnlyList<XCond> X, string? Input)? ReadCondition(TagRule rule)
    {
        if (rule.Cond == null) return ([], null);
        var n = rule.Cond.Node;
        if (n is CallNode { Name: "AND" } ac)
        {
            // 새 형식: 상태 낱말은 확장 조건 칸으로, 나머지(ON·OFF·ANY·IN)는 입력 조건으로(ANY → ANY_ON, IN 은 묶음마다 풀어 IN( … ) 하나로)
            var states = ac.Args.Where(a => XCond.FromNode(a.Node) is { } xc && StateWords.IsWord(xc.Name)).ToList();
            var rest = ac.Args.Except(states).ToList();
            var groups = rest.SelectMany(a => a.Node is CallNode { Name: "IN" } c ? c.Args.Select(g => g.Text) : [a.Node is CallNode { Name: "ANY" } y ? Form.Call("ANY_ON", y.Args.Select(g => g.Text).ToList()) : a.Text]).ToList();
            string? input = groups.Count == 0 ? null : groups.Count == 1 ? groups[0] : Form.Call("IN", groups);
            return (states.Select(a => XCond.FromNode(a.Node)!).ToList(), input);
        }
        string name = n is CallNode c0 ? c0.Name : n is NameNode m0 ? m0.Name : "";
        var a = n is CallNode c ? c.Args : [];
        XCond? X(int i) => XCond.FromNode(a[i].Node);
        return (name, a.Count) switch
        {
            ("IF_X", 1) when X(0) is { } x => ([x], null),
            ("IF_X2", 2) when X(0) is { } x && X(1) is { } y => ([x, y], null),
            ("IF_IN", 1) => ([], a[0].Text),
            ("IF_X_IN", 2) when X(0) is { } x => ([x], a[1].Text),
            ("IF_X2_IN", 3) when X(0) is { } x && X(1) is { } y => ([x, y], a[2].Text),
            _ when name.StartsWith("IF_", StringComparison.Ordinal) && Short.Contains(name[3..]) => ([new XCond("X_" + name[3..], a.Select(x => x.Text).ToList())], null),
            _ => null,
        };
    }

    /// <summary>조건 바꾸기. 꼴은 사양 5.4절 표로 고른다(TAG ↔ TAG_IF, TAG_RANGE ↔ TAG_RANGE_IF). 확장 조건 2개까지.</summary>
    public static EditResult SetCondition(Site site, TagRule rule, IReadOnlyList<XCond> x, string? input)
    {
        bool newDsl = site.NewDsl;
        if (x.Count > 2) return EditResult.Fail("확장 조건 2개까지");
        input = string.IsNullOrWhiteSpace(input) ? null : input;
        if (x.FirstOrDefault(c => !XNames.Contains(c.Name)) is { } bad) return EditResult.Fail($"확장 조건 아님 · {bad.Name}");
        if (x.SelectMany(c => c.Args).Concat(input == null ? [] : [input]).FirstOrDefault(t => Form.BadText(t) != null) is { } bt) return EditResult.Fail($"{Form.BadText(bt)} · {bt}");
        if (CondKey(x, input) == CondKey(rule)) return new EditResult(site, null, []); // 뜻이 같으면 줄을 쓰지 않는다(옛 형식을 새 형식으로 몰래 바꾸지 않는다)
        var cond = CondText(x, input, newDsl);
        if (cond != null && IsMixed(cond)) return EditResult.Fail("한 줄에 못 씀 · 옛·새 낱말 섞임");
        var st = rule.St;
        var baseName = rule.IsRange ? "TAG_RANGE" : "TAG";
        var name = cond == null ? baseName : baseName + "_IF";
        var list = SiteEdit.ArgList(st);
        int at = rule.ActionStart - (rule.HasCond ? 1 : 0); // 조건 칸 자리(프로파일 다음)
        if (rule.HasCond && cond != null) list[at] = list[at] with { Text = cond };
        else if (rule.HasCond) list.RemoveAt(at);
        else if (cond != null) list.Insert(at, (list[at].Lead, cond, ""));
        var text = Form.Args(st, list);
        text = text[..st.Indent.Length] + name + text[(st.Indent.Length + st.Name.Length)..];
        var sp = new Splice(site);
        sp.Replace(st, text);
        return sp.Commit();
    }

    // ---- 동작 ----

    /// <summary>동작 하나를 넣는다. index = 동작 순번(0부터, 끝이면 동작 수).</summary>
    public static EditResult InsertAction(Site site, TagRule rule, int index, string text) =>
        index < 0 || index > rule.Actions.Count ? EditResult.Fail("자리 틀림")
        : rule.Actions.Count >= 4 ? EditResult.Fail("동작 칸 넘침 · 4칸")
        : SiteEdit.InsertArg(site, rule.St, rule.ActionStart + index, text);

    /// <summary>동작 하나를 지운다. 마지막 동작은 지우지 않는다.</summary>
    public static EditResult DeleteAction(Site site, TagRule rule, int index) =>
        index < 0 || index >= rule.Actions.Count || rule.Actions.Count < 2 ? EditResult.Fail("자리 틀림") : SiteEdit.DeleteArg(site, rule.St, rule.ActionStart + index);

    /// <summary>동작 순서 바꾸기(동작 순번).</summary>
    public static EditResult MoveAction(Site site, TagRule rule, int from, int to) =>
        from < 0 || to < 0 || from >= rule.Actions.Count || to >= rule.Actions.Count ? EditResult.Fail("자리 틀림")
        : SiteEdit.MoveArg(site, rule.St, rule.ActionStart + from, rule.ActionStart + to);
}
