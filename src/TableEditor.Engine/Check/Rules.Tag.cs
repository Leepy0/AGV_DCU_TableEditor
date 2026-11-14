using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.Engine.Check;

// 6.4 태그 표
public static partial class Checker
{
    /// <summary>한 규칙의 동작 뜻. Stops = 정지 분류(1~4, 센서 정지 0), Crossing = 분류 3·4 수.</summary>
    sealed record RuleActs(int Cells, int Turns, bool TapeTurn, List<int> Stops, int Seqs, bool Speed, int SensorCell)
    {
        public int Crossing => Stops.Count(s => s is 3 or 4);
        public bool Sensor => SensorCell >= 0;
    }

    static RuleActs Acts(Facts f, TagRule r)
    {
        int cells = 0, turns = 0, seqs = 0, sensorCell = -1;
        bool tapeTurn = false, speed = false;
        var stops = new List<int>();
        foreach (var a in r.Actions)
        {
            var name = NameOf(a.Node);
            var args = ArgsOf(a.Node);
            var row = f.FitRow(GrammarTable.Find(name, Place.TagAction), args);
            int c = row?.TagCells ?? 1;
            switch (name)
            {
                case "AGV_TURN":
                    turns++;
                    tapeTurn |= args.Skip(5).Any(x => x.Text == "TAPE");
                    break;
                case "AGV_STOP":
                    if (args.Count == 2) { stops.Add(0); sensorCell = cells; }
                    else stops.Add(args.Count == 0 ? 1 : args[0].Text == "TAPE" ? 3 : args[0].Text == "TAPE_GD" ? 4 : 2);
                    break;
                case "SEQ_START":
                    seqs++;
                    stops.Add(args.Count == 1 ? 1 : args[0].Text == "TAPE" ? 3 : args[0].Text == "TAPE_GD" ? 4 : 2);
                    break;
                case "AGV_SPEED" or "AGV_SPEED_MMIN":
                    speed = true;
                    break;
            }
            cells += c;
        }
        return new RuleActs(cells, turns, tapeTurn, stops, seqs, speed, sensorCell);
    }

    static IEnumerable<Finding> Combo(Facts f, string rule, Func<RuleActs, bool> bad, string text) =>
        f.Site.TagRules.Where(r => bad(Acts(f, r))).Select(r => f.Error(rule, r.St, text));

    /// <summary>T-1: 회전 + 정지 분류 2·3·4.</summary>
    static IEnumerable<Finding> T1(Facts f) => Combo(f, "T-1", a => a.Turns > 0 && a.Stops.Any(s => s is 2 or 3 or 4), "회전 + 정지 분류 2·3·4");

    /// <summary>T-2: 회전 둘.</summary>
    static IEnumerable<Finding> T2(Facts f) => Combo(f, "T-2", a => a.Turns > 1, "회전 둘");

    /// <summary>T-3: 교차선 정지(분류 3·4) 둘.</summary>
    static IEnumerable<Finding> T3(Facts f) => Combo(f, "T-3", a => a.Crossing > 1, "교차선 정지 둘");

    /// <summary>T-4: 분류 3·4 규칙에 시퀀스 둘.</summary>
    static IEnumerable<Finding> T4(Facts f) => Combo(f, "T-4", a => a.Crossing > 0 && a.Seqs > 1, "교차선 정지 + 시퀀스 둘");

    /// <summary>T-5: 분류 3·4 + 속도.</summary>
    static IEnumerable<Finding> T5(Facts f) => Combo(f, "T-5", a => a.Crossing > 0 && a.Speed, "교차선 정지 + 속도");

    /// <summary>T-6: 센서 정지 + 교차선 요청(분류 3·4, TAPE 회전).</summary>
    static IEnumerable<Finding> T6(Facts f) => Combo(f, "T-6", a => a.Sensor && (a.Crossing > 0 || a.TapeTurn), "센서 정지 + 교차선");

    /// <summary>T-7: 센서 정지가 넷째 칸.</summary>
    static IEnumerable<Finding> T7(Facts f) => Combo(f, "T-7", a => a.SensorCell == 3, "센서 정지 넷째 칸");

    /// <summary>T-8: 동작 칸 합이 4 를 넘는다.</summary>
    static IEnumerable<Finding> T8(Facts f) =>
        f.Site.TagRules.Select(r => (r, c: Acts(f, r).Cells)).Where(x => x.c > 4).Select(x => f.Error("T-8", x.r.St, $"동작 칸 {x.c} · 4 이하"));

    /// <summary>T-9: 태그 표의 범위 밖 값(처음 ≤ 끝, 회전 각도 5의 배수 5~315 포함).</summary>
    static IEnumerable<Finding> T9(Facts f) => Ranges(f, "T-9", u => u.Area == Area.Tag).Concat(Pairs(f, "T-9", a => a == Area.Tag));

    /// <summary>조건의 호기·큐 범위 조각: ("ID", 처음, 끝), ("ST k", 하한, 상한). X_NOT 안은 뺀다.</summary>
    static List<(string Kind, long Lo, long Hi)> Atoms(Facts f, Node? cond)
    {
        var list = new List<(string, long, long)>();
        void Add(Node n)
        {
            if (n is not CallNode c) return;
            switch (c.Name)
            {
                case "IF_ID" or "X_ID" when c.Args.Count == 2 && Val(f, c.Args[0]) is long a && Val(f, c.Args[1]) is long b:
                    list.Add(("ID", a, b));
                    break;
                case "IF_STATION" or "X_STATION" when c.Args.Count == 3 && Val(f, c.Args[0]) is long k && Val(f, c.Args[1]) is long lo && Val(f, c.Args[2]) is long hi:
                    list.Add(("ST " + k, lo, hi));
                    break;
                case "IF_X" or "IF_X2" or "IF_X_IN" or "IF_X2_IN":
                    foreach (var a2 in c.Args) Add(a2.Node);
                    break;
            }
        }
        if (cond != null) Add(cond);
        return list;
    }

    /// <summary>태그 표 구역마다 (규칙, 구역). 원문 블록이 있는 구역은 가림 검사를 건너뛴다(R-1).</summary>
    static IEnumerable<(TagRule Rule, ZoneBlock Zone, List<TagRule> Before)> TagOrder(Facts f)
    {
        foreach (var z in f.Site.ZonesOf("TAG_TABLE_BEGIN").Where(z => !z.HasRaw))
        {
            var rules = f.Site.TagRules.Where(r => z.Statements.Contains(r.St)).ToList();
            for (int i = 0; i < rules.Count; i++) yield return (rules[i], z, rules.Take(i).ToList());
        }
    }

    static bool Covers(Facts f, TagRule hider, TagRule r) =>
        Val(f, hider.FirstArg) is long a && Val(f, hider.LastArg) is long b && Val(f, r.FirstArg) is long c && Val(f, r.LastArg) is long d
        && a <= c && d <= b && (hider.AnyProfile || hider.Profile == r.Profile);

    static bool HidesT10(Facts f, TagRule e, TagRule r) => Covers(f, e, r) && (!e.HasCond || e.CondText == r.CondText);

    static bool HidesT11(Facts f, TagRule e, TagRule r) =>
        Covers(f, e, r) && e.Cond?.Node is CallNode { Name: "IF_ID" or "IF_STATION" }
        && Atoms(f, e.Cond.Node) is [var x] && Atoms(f, r.Cond?.Node).Any(m => m.Kind == x.Kind && x.Lo <= m.Lo && m.Hi <= x.Hi);

    /// <summary>앞 규칙 e 가 뒤 규칙 r 을 죽은 줄로 만드는가(T-10·T-11 과 같은 판정). 편집의 넣는 자리 계산이 쓴다.</summary>
    internal static bool Hides(Facts f, TagRule e, TagRule r) => HidesT10(f, e, r) || HidesT11(f, e, r);

    static TagRule? HiddenByT10(Facts f, TagRule r, List<TagRule> before) => before.FirstOrDefault(e => HidesT10(f, e, r));

    /// <summary>T-10: 죽은 줄. 앞 규칙이 같은 태그·프로파일(또는 ANY_PROFILE)·조건(없음 또는 글자 같음)으로 온전히 가린다.</summary>
    static IEnumerable<Finding> T10(Facts f)
    {
        foreach (var (r, z, before) in TagOrder(f))
            if (HiddenByT10(f, r, before) is { } h)
                yield return f.Error("T-10", r.St, Facts.Dead([z.Ordinal(h.St)]), related: [h.St.StartLine]);
    }

    /// <summary>T-11: 죽은 줄. 앞 규칙의 IF_ID·IF_STATION 범위가 이 규칙의 같은 종류 범위를 포함한다.</summary>
    static IEnumerable<Finding> T11(Facts f)
    {
        foreach (var (r, z, before) in TagOrder(f))
        {
            if (HiddenByT10(f, r, before) != null) continue;
            var h = before.FirstOrDefault(e => HidesT11(f, e, r));
            if (h != null) yield return f.Error("T-11", r.St, Facts.Dead([z.Ordinal(h.St)]), related: [h.St.StartLine]);
        }
    }

    /// <summary>T-12: IF_ID·X_ID 범위가 그 프로파일의 호기와 겹치지 않는다.</summary>
    static IEnumerable<Finding> T12(Facts f)
    {
        foreach (var r in f.Site.TagRules.Where(r => !r.AnyProfile && r.Cond != null))
        {
            var rows = f.Site.RowsOf(r.Profile).Where(x => x.First != null && x.Last != null).ToList();
            if (rows.Count == 0) continue;
            foreach (var (_, lo, hi) in Atoms(f, r.Cond!.Node).Where(a => a.Kind == "ID"))
                if (!rows.Any(x => x.First <= hi && x.Last >= lo))
                    yield return f.Error("T-12", r.St, $"호기 안 맞음 {lo}~{hi} · {r.Profile} " + string.Join(", ", rows.Select(x => $"{x.First}~{x.Last}")), r.Cond);
        }
    }

    /// <summary>T-13: SEQ_START( X ) 의 대응표 줄이 없다. 어느 프로파일에도 없으면 오류, 그 규칙의 프로파일에만 없으면 경고.</summary>
    static IEnumerable<Finding> T13(Facts f)
    {
        foreach (var r in f.Site.TagRules)
            foreach (var a in r.Actions.Where(a => NameOf(a.Node) == "SEQ_START" && ArgsOf(a.Node).Count > 0))
            {
                var x = ArgsOf(a.Node)[^1].Text;
                var id = "SEQ_" + x;
                if (f.Names.Find(id)?.Kind != NameKind.SeqId) continue; // N-1
                var rows = f.Site.SeqMap.Where(m => m.Id == id).ToList();
                if (rows.Count == 0) yield return f.Error("T-13", r.St, $"대응표 없음 {x}", a);
                else if (!r.AnyProfile && !rows.Any(m => m.AnyProfile || m.Profile == r.Profile))
                    yield return f.Warn("T-13", r.St, $"대응표 없음 {x} · {r.Profile}", a);
            }
    }

    /// <summary>T-14: 태그 표에서 못 쓰는 것(AGV_MOVE, 회전 NOWAIT, 시퀀스 전용 스텝).</summary>
    static IEnumerable<Finding> T14(Facts f) => FromProblems(f, "T-14", p => p.Kind == "seqonly");

    /// <summary>T-15: 규칙 1024 개 초과, 서로 다른 확장 조건·입력 조건·동작 묶음 255 종 초과.</summary>
    static IEnumerable<Finding> T15(Facts f)
    {
        foreach (var z in f.Site.ZonesOf("TAG_TABLE_BEGIN"))
        {
            var rules = f.Site.TagRules.Where(r => z.Statements.Contains(r.St)).ToList();
            if (rules.Count > 1024) yield return f.Error("T-15", z.Begin, $"규칙 {rules.Count} · 1024 이하");
            var x = new HashSet<string>();
            var inp = new HashSet<string>();
            var act = new HashSet<string>();
            foreach (var r in rules)
            {
                act.Add(string.Join(",", r.Actions.Select(a => Squash(a.Text))));
                if (r.Cond?.Node is not CallNode and not NameNode) continue;
                var c = r.Cond!.Node;
                var name = NameOf(c);
                var args = ArgsOf(c);
                switch (name)
                {
                    case "IF_IN": inp.Add(Squash(args[0].Text)); break;
                    case "IF_X" or "IF_X2": foreach (var a in args) x.Add(Squash(a.Text)); break;
                    case "IF_X_IN" or "IF_X2_IN":
                        foreach (var a in args.SkipLast(1)) x.Add(Squash(a.Text));
                        inp.Add(Squash(args[^1].Text));
                        break;
                    default: x.Add(Squash(r.Cond.Text)); break;
                }
            }
            foreach (var (n, what) in new[] { (x.Count, "확장 조건"), (inp.Count, "입력 조건"), (act.Count, "동작 묶음") })
                if (n > 255) yield return f.Error("T-15", z.Begin, $"{what} {n}종 · 255 이하");
        }
    }

    static IEnumerable<Finding> TurnSpeed(Facts f, string rule, Func<Area, bool> area) =>
        f.Uses.Where(u => u.Parent == "AGV_TURN" && u.Spec.Unit == "m/min" && area(u.Area) && f.Names.Value(u.Node) is >= 1 and <= 3)
            .Select(u => f.Warn(rule, u.St, $"회전 속도 {u.Loc.Text}", u.Loc));

    /// <summary>T-16: 회전 속도 1~3(옛 표의 장애물 칸일 수 있다).</summary>
    static IEnumerable<Finding> T16(Facts f) => TurnSpeed(f, "T-16", a => a == Area.Tag);

    /// <summary>ACT ID·CALL 번호가 대응표·함수 표에 없는 쓰임(T-17·S-15 공통).</summary>
    static IEnumerable<Finding> Targets(Facts f, string rule, Func<Area, bool> area)
    {
        var acts = f.Site.ActMap.Select(m => Val(f, m.St.Args[1])).OfType<long>().ToHashSet();
        var calls = CallCount(f);
        foreach (var (st, loc, node, a) in UsesOf(f, "ACT_START", "WAIT_ACT", "CALL").Where(x => area(x.Area)))
        {
            var args = ArgsOf(node);
            if (args.Count != 1 || Val(f, args[0]) is not long v) continue;
            if (NameOf(node) == "CALL" && calls is long n && v >= n) yield return f.Error(rule, st, $"함수 없음 {args[0].Text}", loc);
            if (NameOf(node) != "CALL" && !acts.Contains(v)) yield return f.Error(rule, st, $"ACT 대응표 없음 {args[0].Text}", loc);
        }
    }

    /// <summary>T-17: ACT_START·CALL 의 번호가 대응표·함수 표에 없다.</summary>
    static IEnumerable<Finding> T17(Facts f) => Targets(f, "T-17", a => a == Area.Tag);
}
