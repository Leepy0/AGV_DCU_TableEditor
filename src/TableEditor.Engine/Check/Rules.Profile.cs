using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.Engine.Check;

// 6.3 프로파일·IO, 6.6 수동 표
public static partial class Checker
{
    /// <summary>P-1: 처음 > 끝, 호기 0~99 밖, 타입 0~999 밖.</summary>
    static IEnumerable<Finding> P1(Facts f) =>
        Ranges(f, "P-1", u => u.Area == Area.Profile).Concat(Pairs(f, "P-1", a => a == Area.Profile));

    /// <summary>프로파일 줄마다 (앞 줄 가운데 이 줄과 겹치는 것, 온전히 가리는가). 원문 블록이 있는 표는 건너뛴다(R-1).</summary>
    static IEnumerable<(ProfileRow Row, ZoneBlock Zone, List<ProfileRow> Overlap, bool Dead)> ProfileOverlaps(Facts f)
    {
        foreach (var z in f.Site.ZonesOf("PROFILE_TABLE_BEGIN").Where(z => !z.HasRaw))
        {
            var rows = f.Site.ProfileRows.Where(r => z.Statements.Contains(r.St)).ToList();
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r.First is not long a || r.Last is not long b || r.Type is not long t || a > b) continue;
                var before = rows.Take(i).Where(e => e.First != null && e.Last != null && e.Type != null).ToList();
                var overlap = before.Where(e => (e.Type == 0 || t == 0 || e.Type == t) && e.First <= b && e.Last >= a).ToList();
                if (overlap.Count == 0) continue;
                // 이 줄의 타입을 온전히 가져가는 앞 줄(타입 0, 또는 같은 타입)이 호기를 모두 덮는가
                var takers = before.Where(e => e.Type == 0 || t != 0 && e.Type == t).ToList();
                bool dead = true;
                for (long id = a; id <= b && dead; id++) dead = takers.Any(e => e.First <= id && id <= e.Last);
                yield return (r, z, dead ? takers.Where(e => e.First <= b && e.Last >= a).ToList() : overlap, dead);
            }
        }
    }

    /// <summary>P-2: 죽은 줄. 위 줄들이 그 줄의 타입·호기를 모두 가져간다.</summary>
    static IEnumerable<Finding> P2(Facts f) =>
        ProfileOverlaps(f).Where(x => x.Dead).Select(x => f.Error("P-2", x.Row.St, Facts.Dead(x.Overlap.Select(e => x.Zone.Ordinal(e.St))),
            related: x.Overlap.Select(e => e.St.StartLine).ToList()));

    /// <summary>P-3: 호기 겹침. 앞 줄이 뒤 줄 범위 안에 온전히 든 "따로 빼기"는 표시만 한다(결과에 넣지 않는다).</summary>
    static IEnumerable<Finding> P3(Facts f)
    {
        foreach (var (row, zone, overlap, dead) in ProfileOverlaps(f))
        {
            if (dead) continue;
            var hits = overlap.Where(e => !(row.First <= e.First && e.Last <= row.Last)).ToList();
            if (hits.Count > 0)
                yield return f.Warn("P-3", row.St, $"호기 겹침 ({string.Join(", ", hits.Select(e => zone.Ordinal(e.St)))}줄)",
                    related: hits.Select(e => e.St.StartLine).ToList());
        }
    }

    /// <summary>P-4: 프로파일 번호 1~63 밖, 겹침.</summary>
    static IEnumerable<Finding> P4(Facts f)
    {
        var seen = new Dictionary<long, ProfileDef>();
        foreach (var p in f.Site.Profiles)
        {
            if (p.Number is not long n) continue;
            if (n < 1 || n > 63) yield return f.Error("P-4", p.St, $"번호 범위 밖 {p.Name} {n} · 1~63");
            if (seen.TryGetValue(n, out var e)) yield return f.Error("P-4", p.St, $"번호 겹침 {n} · {e.Name}", related: [e.St.StartLine]);
            else seen[n] = p;
        }
    }

    /// <summary>P-5: 프로파일 표에 줄이 없는 프로파일.</summary>
    static IEnumerable<Finding> P5(Facts f) =>
        f.Site.Profiles.Where(p => !f.Site.RowsOf(p.Name).Any()).Select(p => f.Warn("P-5", p.St, $"프로파일 줄 없음 {p.Name}"));

    /// <summary>P-6: 같은 프로파일의 줄마다 적재 설정이 다르다.</summary>
    static IEnumerable<Finding> P6(Facts f)
    {
        foreach (var g in f.Site.ProfileRows.GroupBy(r => r.Profile))
        {
            var first = g.First();
            foreach (var r in g.Skip(1).Where(r => r.Load != first.Load))
                yield return f.Warn("P-6", r.St, $"적재 설정 다름 {r.Profile}", related: [first.St.StartLine]);
        }
    }

    /// <summary>I-1: 조건·출력에 쓴 신호 ID 가 0~31 밖.</summary>
    static IEnumerable<Finding> I1(Facts f) => FromProblems(f, "I-1", p => p.Kind == "range" && IsPin(p.Use.Spec.Kind));

    /// <summary>조건 칸(IN( … ), 묶음 하나, 수동 표 조건, AND( … ))의 묶음들. AND 는 상태 낱말을 빼고 입력 항목만(<c>ANY</c> 는 첫째만 <c>ANY_ON</c> 묶음으로, 상태 낱말은 건너뜀).</summary>
    static IEnumerable<(Use Use, IReadOnlyList<CallNode> Groups)> Conditions(Facts f) =>
        f.Uses.Where(u => u.Spec.Kind is ArgKind.InCond or ArgKind.OutCond or ArgKind.ManCond or ArgKind.AndCond or ArgKind.TagCond && u.Node is CallNode)
            .Select(u => (u, (IReadOnlyList<CallNode>)(u.Node is CallNode { Name: "AND" } ac
                ? AndGroups(ac)
                : u.Node is CallNode { Name: "IN" } c
                ? c.Args.Select(a => a.Node).OfType<CallNode>().ToList()
                : [(CallNode)u.Node])))
            .Where(x => x.Item2.All(g => g.Name is "ON" or "OFF" or "ANY_ON"));

    static List<CallNode> AndGroups(CallNode and)
    {
        var list = new List<CallNode>();
        bool anyOn = HasAnyOn(and);
        bool first = true;
        foreach (var n in Items(and).OfType<CallNode>())
            switch (n.Name)
            {
                case "ON" or "OFF" or "ANY_ON": list.Add(n); break;
                case "IN": list.AddRange(n.Args.Select(a => a.Node).OfType<CallNode>()); break;
                case "ANY" when first && !anyOn: list.Add(new CallNode("ANY_ON", n.Gap, n.Args)); first = false; break; // 첫째 ANY 만 입력 조건 안으로 접힌다
            }
        return list;
    }

    /// <summary>I-2: 한 조건에서 같은 핀을 반대 뜻으로 겹쳐 썼다(ANY_ON 묶음의 핀을 다른 묶음에 또 씀 포함).</summary>
    static IEnumerable<Finding> I2(Facts f)
    {
        foreach (var (u, groups) in Conditions(f))
        {
            var on = new HashSet<long>();
            var off = new HashSet<long>();
            var any = new HashSet<long>();
            foreach (var g in groups)
                foreach (var a in g.Args)
                {
                    var d = a.Node is NameNode n ? f.Names.Find(n.Name) : null;
                    if ((d?.Value ?? Val(f, a)) is not long pin || d?.Kind == NameKind.IoNone) continue;
                    bool high = d?.Kind == NameKind.InputHigh;
                    if (g.Name == "ANY_ON") any.Add(pin);
                    else if (g.Name == "ON" ^ high) on.Add(pin);
                    else off.Add(pin);
                }
            var clash = on.Intersect(off).Concat(any.Intersect(on.Union(off))).Distinct().ToList();
            if (clash.Count > 0) yield return f.Error("I-2", u.St, $"같은 핀 반대 · ID {string.Join(", ", clash)}", u.Loc);
        }
    }

    /// <summary>I-3: 한 조건에 ANY_ON 묶음이 둘(하나로 합쳐진다).</summary>
    static IEnumerable<Finding> I3(Facts f) =>
        Conditions(f).Where(x => x.Groups.Count(g => g.Name == "ANY_ON") > 1).Select(x => f.Error("I-3", x.Use.St, "ANY_ON 둘", x.Use.Loc));

    /// <summary>I-4: 묶음 신호 17개 이상, IN 묶음 7개 이상.</summary>
    static IEnumerable<Finding> I4(Facts f) => FromProblems(f, "I-4", p => p.Kind == "groupcount");

    /// <summary>I-5: 벨트 CW·CCW 한쪽만 정의. 없는 방향(NO_OUT)을 켠다.</summary>
    static IEnumerable<Finding> I5(Facts f)
    {
        foreach (var b in f.Names.Belts.Values.Where(b => !b.Complete))
            yield return f.Error("I-5", (b.Cw ?? b.Ccw)!, $"벨트 한쪽만 {b.Name}");
        foreach (var file in f.Site.Files)
            foreach (var st in file.Statements)
            {
                var hits = new List<(Arg Loc, IReadOnlyList<Arg> Args)>();
                if (st.Name == "BELT_ON" && f.AreaOf(st) is Area.Seq or Area.Act) hits.Add((st.Args[0], st.Args));
                if (f.AreaOf(st) == Area.Manual) hits.AddRange(Calls(st).Where(c => c.Call.Name == "BELT").Select(c => (c.Loc, c.Call.Args)));
                foreach (var (loc, args) in hits)
                {
                    if (args.Count < 2 || args[1].Text is not ("CW" or "CCW")) continue;
                    var key = args[0].Node is NumberNode n ? n.Literal : args[0].Text;
                    if (f.Names.Belts.TryGetValue(key, out var b) && b.Complete && b.Pins(args[1].Text).Count == 0)
                        yield return f.Error("I-5", st, $"없는 방향 {key} {args[1].Text}", loc);
                }
            }
    }

    /// <summary>I-6: 미연결(IO_NONE) 별칭을 조건에 썼다.</summary>
    static IEnumerable<Finding> I6(Facts f) => FromProblems(f, "I-6", p => p.Kind == "ionone", warn: true);

    /// <summary>I-7: IO 맵 줄 끝 <c>/* pf: … */</c> 의 프로파일 이름이 사이트 프로파일 목록에 없다.</summary>
    static IEnumerable<Finding> I7(Facts f)
    {
        var known = f.Site.Profiles.Select(p => p.Name).ToHashSet();
        foreach (var d in f.Names.All.Where(d => !d.Core && !d.Weak && d.Def is Statement { Shape: StatementShape.Define }))
        {
            foreach (var n in (d.Profiles ?? []).Where(n => !known.Contains(n)))
                yield return f.Warn("I-7", d.Def!, $"프로파일 없음 {n}");
            if (IoProfile.HasBad((Statement)d.Def!)) yield return f.Warn("I-7", d.Def!, "pf 글 틀림");
        }
    }

    // ---- 수동 표 ----

    static bool ManualRaw(Facts f) => f.Site.ZonesOf("MANUAL_BEGIN").Any(z => z.HasRaw);

    static ZoneBlock ManualZone(Facts f, Statement st) => f.Site.ZoneOf(st)!;

    /// <summary>M-1: 사슬이 MAN_ELSE 로 안 닫혔다.</summary>
    static IEnumerable<Finding> M1(Facts f) => ManualRaw(f) ? [] :
        f.Site.ManualChains.Where(c => !c.Closed).Select(c => f.Error("M-1", c.Rows[^1], $"사슬 안 닫힘 {c.Profile} {c.Mode}"));

    /// <summary>M-2: 프로파일 0·64 이상, 한 출력을 켜면서 끔, 조건 꼴이 IN(·KEY(·NO_IN 이 아님.</summary>
    static IEnumerable<Finding> M2(Facts f)
    {
        foreach (var x in FromProblems(f, "M-2", p => p.Use.Area == Area.Manual && (p.Kind == "mancond" || p.Kind == "range" && !IsPin(p.Use.Spec.Kind))))
            yield return x;
        foreach (var st in f.Site.ManualRows.Where(s => s.Name is "MAN" or "MAN_ELSE"))
        {
            var (on, off) = ManOutputs(f, st);
            var both = on.Intersect(off).ToList();
            if (both.Count > 0) yield return f.Error("M-2", st, $"켜고 끔 · ID {string.Join(", ", both)}");
        }
    }

    /// <summary>수동 줄의 켬·끔 출력 ID. BELT( b, CW ) 는 그 방향을 켜고 그 벨트의 나머지를 끈다.</summary>
    static (HashSet<long> On, HashSet<long> Off) ManOutputs(Facts f, Statement st)
    {
        var on = new HashSet<long>();
        var off = new HashSet<long>();
        int first = st.Name == "MAN" ? 3 : 2;
        foreach (var a in st.Args.Skip(first))
        {
            if (a.Node is not CallNode c) continue;
            if (c.Name is "ON" or "OFF")
                foreach (var x in c.Args)
                    if (Val(f, x) is long v) (c.Name == "ON" ? on : off).Add(v);
            if (c.Name == "BELT" && c.Args.Count == 2)
            {
                var key = c.Args[0].Node is NumberNode n ? n.Literal : c.Args[0].Text;
                if (!f.Names.Belts.TryGetValue(key, out var b)) continue;
                var dir = c.Args[1].Text;
                var lit = dir == "OFF" ? [] : b.Pins(dir);
                on.UnionWith(lit);
                off.UnionWith(b.AllPins.Except(lit));
            }
        }
        return (on, off);
    }

    /// <summary>M-3: 죽은 가지. 같은 사슬에서 조건이 같은 줄, NO_IN 가지 뒤의 가지.</summary>
    static IEnumerable<Finding> M3(Facts f)
    {
        if (ManualRaw(f)) yield break;
        foreach (var c in f.Site.ManualChains)
        {
            var mans = c.Rows.Where(r => r.Name == "MAN").ToList();
            for (int i = 1; i < mans.Count; i++)
            {
                var hider = mans.Take(i).FirstOrDefault(e => e.Args[2].Text == "NO_IN" || Squash(e.Args[2].Text) == Squash(mans[i].Args[2].Text));
                if (hider != null)
                    yield return f.Error("M-3", mans[i], Facts.Dead([ManualZone(f, hider).Ordinal(hider)]), related: [hider.StartLine]);
            }
        }
    }

    /// <summary>M-4: MAN 없이 MAN_ELSE 만 있는 사슬.</summary>
    static IEnumerable<Finding> M4(Facts f) => ManualRaw(f) ? [] :
        f.Site.ManualChains.Where(c => c.Rows.Count == 1 && c.Closed).Select(c => f.Warn("M-4", c.Rows[0], $"MAN 없는 사슬 {c.Profile} {c.Mode}"));

    /// <summary>M-5: 같은 프로파일에 MAN_FB_SW(또는 MAN_OBS_B)가 둘.</summary>
    static IEnumerable<Finding> M5(Facts f) =>
        f.Site.ManualRows.Where(s => s.Name is "MAN_FB_SW" or "MAN_OBS_B").GroupBy(s => (s.Name, s.Args[0].Text))
            .SelectMany(g => g.Skip(1).Select(s => f.Warn("M-5", s, $"{s.Name} 둘 {s.Args[0].Text}", related: [g.First().StartLine])));

    /// <summary>M-6: 프로파일 표에 줄이 없는 프로파일의 수동 줄.</summary>
    static IEnumerable<Finding> M6(Facts f) =>
        f.Site.ManualRows.GroupBy(s => s.Args[0].Text).Where(g => f.Names.Find(g.Key)?.Kind == NameKind.Profile && !f.Site.RowsOf(g.Key).Any())
            .Select(g => f.Warn("M-6", g.First(), $"프로파일 줄 없음 {g.Key}"));

    /// <summary>M-7: IDLE 줄과 그 프로파일의 ACT 가 같은 출력을 쓴다.</summary>
    static IEnumerable<Finding> M7(Facts f)
    {
        var actOut = f.Site.Seqs.Where(s => s.IsAct).ToDictionary(s => s.Name, s => ActOutputs(f, s));
        foreach (var st in f.Site.ManualRows.Where(s => s.Name is "MAN" or "MAN_ELSE" && s.Args[1].Text == "IDLE"))
        {
            var (on, off) = ManOutputs(f, st);
            var mine = on.Union(off).ToHashSet();
            var acts = f.Site.ActMap.Where(m => m.AnyProfile || m.Profile == st.Args[0].Text).Select(m => m.Table);
            var shared = acts.Where(actOut.ContainsKey).SelectMany(a => actOut[a]).Where(mine.Contains).Distinct().ToList();
            if (shared.Count > 0) yield return f.Warn("M-7", st, $"ACT 와 같은 출력 · ID {string.Join(", ", shared)}");
        }
    }

    static HashSet<long> ActOutputs(Facts f, SeqTable t)
    {
        var set = new HashSet<long>();
        foreach (var st in t.Steps)
        {
            if (st.Name is "OUT_ON" or "OUT_OFF") foreach (var a in st.Args) if (Val(f, a) is long v) set.Add(v);
            if (st.Name is "OUT_PULSE" or "OUT_FLICKER_UNTIL_IN" or "OUT_FLICKER_UNTIL_IN_TO" or "HANDSHAKE" && Val(f, st.Args[0]) is long w) set.Add(w);
            if (st.Name is "BELT_ON" or "BELT_OFF" && f.Names.Belts.TryGetValue(st.Args[0].Node is NumberNode n ? n.Literal : st.Args[0].Text, out var b))
                set.UnionWith(b.AllPins);
        }
        return set;
    }
}
