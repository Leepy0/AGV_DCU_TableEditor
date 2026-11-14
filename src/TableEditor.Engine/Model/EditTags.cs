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

        var cond = template?.CondText ?? "";
        var rules = site.TagRules.Select(r => (Rule: r, First: site.Num(r.FirstArg), Last: site.Num(r.LastArg))).ToList();
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
                bool exists = rules.Any(x => x.First <= t && t <= x.Last && SameProfile(x.Rule, p) && x.Rule.CondText == cond);
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

    /// <summary>미리 보기의 <c>새 줄</c> 을 쓴다. 넣는 자리는 <see cref="InsertIndex"/>.</summary>
    public static EditResult Add(Site site, string tags, IReadOnlyList<string> profiles, TagRule? template, TagRunMode mode)
    {
        var plan = PlanAdd(site, tags, profiles, template, mode);
        if (plan.Error != null) return EditResult.Fail(plan.Error);
        if (plan.Lines.Count == 0) return EditResult.Fail("새 줄 없음 · 모두 있음");
        var zone = TagZone(site)!;
        var sp = new Splice(site);
        var cond = template?.Cond?.Text;
        var actions = template?.Actions.Select(a => a.Text).ToList() ?? ["AGV_STOP"];
        foreach (var l in plan.Lines.OrderBy(l => l.First).ThenBy(l => IsAny(l.Profile) ? 1 : 0))
        {
            bool range = l.First != l.Last;
            var name = (range ? "TAG_RANGE" : "TAG") + (cond != null ? "_IF" : "");
            var args = new List<string> { l.First.ToString() };
            if (range) args.Add(l.Last.ToString());
            args.Add(l.Profile);
            if (cond != null) args.Add(cond);
            args.AddRange(actions);
            SiteEdit.InsertAt(sp, zone, InsertIndex(site, zone, l.First, null), [new NewStatement(name, args)]);
        }
        return sp.Commit();
    }

    /// <summary>
    /// 새 규칙(첫 태그 tag)을 넣을 Body 자리. 같은 태그 묶음(첫 태그가 같은 규칙)이 있으면 그 묶음의 첫 ANY_PROFILE 줄 앞
    /// (없으면 묶음 끝 줄 뒤). 없으면 더 작은 태그 가운데 가장 가까운 묶음의 끝 줄 뒤, 그것도 없으면 첫 규칙 앞.
    /// skip = 셈에서 뺄 규칙(옮기는 줄).
    /// </summary>
    internal static int InsertIndex(Site site, ZoneBlock zone, long tag, ISet<Piece>? skip)
    {
        var rules = zone.Statements.Where(s => skip == null || !skip.Contains(s))
            .Select(s => (St: s, Rule: new TagRule(s), First: site.Num(new TagRule(s).FirstArg))).ToList();
        var group = rules.Where(r => r.First == tag).ToList();
        if (group.Count > 0)
        {
            var anyRow = group.FirstOrDefault(r => r.Rule.AnyProfile);
            return anyRow.St != null ? SiteEdit.BodyIndex(zone, anyRow.St) : SiteEdit.BodyIndex(zone, group[^1].St) + 1;
        }
        var smaller = rules.Where(r => r.First < tag).ToList();
        if (smaller.Count > 0)
        {
            var m = smaller.Max(r => r.First);
            return SiteEdit.BodyIndex(zone, smaller.Last(r => r.First == m).St) + 1;
        }
        return rules.Count > 0 ? SiteEdit.BodyIndex(zone, rules[0].St) : zone.Body.Count;
    }

    // ---- 번호 바꾸기 ----

    /// <summary>
    /// 고른 규칙들의 첫 태그를 tag 로(범위 규칙은 끝도 같은 만큼 민다). 바뀐 줄은 새 번호의 자리(<see cref="InsertIndex"/>)로 옮긴다.
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
        foreach (var m in moves.OrderBy(m => m.Rule.AnyProfile ? 1 : 0))
        {
            var (anchor, before) = SiteEdit.Anchor(zone, InsertIndex(site, zone, tag, skip));
            sp.Insert(anchor, before, m.Text);
        }
        return sp.Commit();
    }

    // ---- 조건 ----

    /// <summary>줄임 꼴이 있는 확장 조건(사양 5.4절 태그 조건 꼴 고르기): X_ 를 뗀 이름.</summary>
    static readonly HashSet<string> Short = ["FORWARD", "BACKWARD", "FORBACK_MODE", "LOW_BATTERY", "ID", "STATION", "STATION_RUNNING", "STATION_MOD", "STATION_DIV", "TAG_NUM", "PARAM"];

    static readonly HashSet<string> XNames = GrammarTable.Rows.Where(r => r.Place == Place.ExtCond).Select(r => r.Name).ToHashSet();

    /// <summary>조건 칸 글자(사양 5.4절 표). 확장·입력이 모두 없으면 null.</summary>
    public static string? CondText(IReadOnlyList<XCond> x, string? input)
    {
        input = string.IsNullOrWhiteSpace(input) ? null : input.Trim();
        return (x.Count, input) switch
        {
            (0, null) => null,
            (1, null) when x[0].Name.StartsWith("X_", StringComparison.Ordinal) && Short.Contains(x[0].Name[2..]) => Form.Call("IF_" + x[0].Name[2..], x[0].Args),
            (1, null) => Form.Call("IF_X", [x[0].Text]),
            (2, null) => Form.Call("IF_X2", [x[0].Text, x[1].Text]),
            (0, _) => Form.Call("IF_IN", [input!]),
            (1, _) => Form.Call("IF_X_IN", [x[0].Text, input!]),
            _ => Form.Call("IF_X2_IN", [x[0].Text, x[1].Text, input!]),
        };
    }

    /// <summary>규칙의 조건을 (확장 조건들, 입력 조건 글자)로 푼다. 조건 없음 = ([], null). 모르는 꼴이면 null.</summary>
    public static (IReadOnlyList<XCond> X, string? Input)? ReadCondition(TagRule rule)
    {
        if (rule.Cond == null) return ([], null);
        var n = rule.Cond.Node;
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
        if (x.Count > 2) return EditResult.Fail("확장 조건 2개까지");
        input = string.IsNullOrWhiteSpace(input) ? null : input;
        if (x.FirstOrDefault(c => !XNames.Contains(c.Name)) is { } bad) return EditResult.Fail($"확장 조건 아님 · {bad.Name}");
        if (x.SelectMany(c => c.Args).Concat(input == null ? [] : [input]).FirstOrDefault(t => Form.BadText(t) != null) is { } bt) return EditResult.Fail($"{Form.BadText(bt)} · {bt}");
        var cond = CondText(x, input);
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
        index < 0 || index > rule.Actions.Count ? EditResult.Fail("자리 틀림") : SiteEdit.InsertArg(site, rule.St, rule.ActionStart + index, text);

    /// <summary>동작 하나를 지운다. 마지막 동작은 지우지 않는다.</summary>
    public static EditResult DeleteAction(Site site, TagRule rule, int index) =>
        index < 0 || index >= rule.Actions.Count || rule.Actions.Count < 2 ? EditResult.Fail("자리 틀림") : SiteEdit.DeleteArg(site, rule.St, rule.ActionStart + index);

    /// <summary>동작 순서 바꾸기(동작 순번).</summary>
    public static EditResult MoveAction(Site site, TagRule rule, int from, int to) =>
        from < 0 || to < 0 || from >= rule.Actions.Count || to >= rule.Actions.Count ? EditResult.Fail("자리 틀림")
        : SiteEdit.MoveArg(site, rule.St, rule.ActionStart + from, rule.ActionStart + to);
}
