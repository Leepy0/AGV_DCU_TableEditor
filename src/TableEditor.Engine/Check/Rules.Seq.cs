using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.Engine.Check;

// 6.5 시퀀스·ACT
public static partial class Checker
{
    static readonly HashSet<string> WhenFamily = ["WHEN", "WHEN_STATE", "WHEN_IF", "WHEN_IF_IN", "WHEN_ANY2", "WHEN_ID", "OTHERWISE", "SELECT_END_TO"];
    static readonly HashSet<string> EndSteps = ["END_STOP", "END_RESTART", "END_ERROR", "GOTO"];

    /// <summary>표의 실행 스텝(SELECT_BEGIN·SELECT_END 는 스텝이 아니다).</summary>
    static List<Statement> RealSteps(SeqTable t) => t.Steps.Where(s => s.Name is not ("SELECT_BEGIN" or "SELECT_END")).ToList();

    /// <summary>스텝의 라벨 칸(분기 대상). L( ) 은 정의라 뺀다.</summary>
    static IEnumerable<Arg> Targets(Facts f, Statement st)
    {
        if (st.Name == "L" || !f.Rows.TryGetValue(st, out var row)) yield break;
        var specs = Facts.Expand(row, st.Args.Count);
        if (specs == null) yield break;
        for (int i = 0; i < st.Args.Count; i++) if (specs[i].Kind == ArgKind.Label) yield return st.Args[i];
    }

    static IEnumerable<Statement> Ls(SeqTable t) => t.Steps.Where(s => s.Name == "L" && s.Args.Count == 1);

    static bool Declared(Facts f, SeqTable t, string label) => f.Names.Find(label, t.Zone.File) is { Kind: NameKind.Label };

    /// <summary>S-1: 선언 안 된 라벨(SEQ_LABELS 에 없음).</summary>
    static IEnumerable<Finding> S1(Facts f)
    {
        foreach (var t in f.Site.Seqs)
            foreach (var st in t.Steps)
                foreach (var a in Targets(f, st).Concat(st.Name == "L" ? st.Args : []))
                    if (!Declared(f, t, a.Text)) yield return f.Error("S-1", st, $"선언 없는 라벨 {a.Text}", a,
                        NameDict.Near(a.Text, f.Names.Of(NameKind.Label).Where(d => d.File == t.Zone.File).Select(d => d.Name)));
    }

    /// <summary>S-2: 분기 대상 라벨의 L( ) 이 그 표 안에 없다.</summary>
    static IEnumerable<Finding> S2(Facts f)
    {
        foreach (var t in f.Site.Seqs)
        {
            var defined = Ls(t).Select(s => s.Args[0].Text).ToHashSet();
            foreach (var st in t.Steps)
                foreach (var a in Targets(f, st).Where(a => Declared(f, t, a.Text) && !defined.Contains(a.Text)))
                    yield return f.Error("S-2", st, $"라벨 위치 없음 {a.Text}", a);
        }
    }

    /// <summary>S-3: 아무도 가리키지 않는 L( ), 어느 표에서도 안 쓰는 선언.</summary>
    static IEnumerable<Finding> S3(Facts f)
    {
        var used = new HashSet<(SourceFile, string)>();
        foreach (var t in f.Site.Seqs)
        {
            var targets = t.Steps.SelectMany(s => Targets(f, s)).Select(a => a.Text).ToHashSet();
            foreach (var l in Ls(t))
            {
                used.Add((t.Zone.File, l.Args[0].Text));
                if (!targets.Contains(l.Args[0].Text)) yield return f.Warn("S-3", l, $"안 쓰는 라벨 {l.Args[0].Text}", l.Args[0]);
            }
            foreach (var x in targets) used.Add((t.Zone.File, x));
        }
        foreach (var d in f.Names.Of(NameKind.Label).Where(d => !used.Contains((d.File!, d.Name))))
            yield return f.Warn("S-3", d.Def!, $"안 쓰는 라벨 {d.Name}", ((Statement)d.Def!).Args[(int)d.Value!.Value - 1]);
    }

    /// <summary>S-4: 한 표에 같은 L( ) 이 둘.</summary>
    static IEnumerable<Finding> S4(Facts f) =>
        f.Site.Seqs.SelectMany(t => Ls(t).GroupBy(s => s.Args[0].Text).SelectMany(g => g.Skip(1)
            .Select(s => f.Error("S-4", s, $"라벨 두 번 {g.Key} ({t.Zone.Ordinal(g.First())}줄)", s.Args[0], related: [g.First().StartLine]))));

    /// <summary>S-5: 닿지 않는 스텝. END_…·GOTO·분기 묶음 바로 뒤의 라벨 아닌 스텝, OTHERWISE 뒤의 WHEN.</summary>
    static IEnumerable<Finding> S5(Facts f)
    {
        foreach (var t in f.Site.Seqs.Where(t => !t.Zone.HasRaw))
        {
            var s = RealSteps(t);
            for (int i = 1; i < s.Count; i++)
            {
                var prev = s[i - 1];
                var cur = s[i];
                bool groupEnd = WhenFamily.Contains(prev.Name) && !WhenFamily.Contains(cur.Name);
                if ((EndSteps.Contains(prev.Name) || groupEnd) && cur.Name != "L")
                    yield return f.Warn("S-5", cur, $"닿지 않음 · {prev.Name} 뒤");
                else if (WhenFamily.Contains(cur.Name) && WhenFamily.Contains(prev.Name) && s.Take(i).Reverse().TakeWhile(x => WhenFamily.Contains(x.Name)).Any(x => x.Name == "OTHERWISE"))
                    yield return f.Warn("S-5", cur, "닿지 않음 · OTHERWISE 뒤");
            }
        }
    }

    /// <summary>S-6: 끝 스텝 없이 시퀀스 표가 끝난다(재출발 안 함). ACT 표는 끝나면 저절로 멈추므로 보지 않는다.</summary>
    static IEnumerable<Finding> S6(Facts f)
    {
        foreach (var t in f.Site.Seqs.Where(t => !t.IsAct && !t.Zone.HasRaw))
        {
            var last = RealSteps(t).LastOrDefault();
            if (last == null || !(EndSteps.Contains(last.Name) || WhenFamily.Contains(last.Name) || last.Name == "ERROR_HOLD"))
                yield return f.Warn("S-6", t.Zone.Begin, $"끝 스텝 없음 {t.Name}");
        }
    }

    /// <summary>S-7: ACT 표에 금지 스텝(사양 3.7절).</summary>
    static IEnumerable<Finding> S7(Facts f) =>
        f.Site.Seqs.Where(t => t.IsAct).SelectMany(t => t.Steps)
            .Where(st => f.Rows.TryGetValue(st, out var r) && r.ActForbidden)
            .Select(st => f.Error("S-7", st, $"ACT 금지 {st.Name}"));

    /// <summary>S-8: 시퀀스·ACT·대응표의 범위 밖 값(회전 각도는 S-16).</summary>
    static IEnumerable<Finding> S8(Facts f) =>
        Ranges(f, "S-8", u => u.Area is Area.Seq or Area.Act or Area.Map && u.Spec.Unit != "도")
            .Concat(Pairs(f, "S-8", a => a is Area.Seq or Area.Act));

    /// <summary>S-9: 한 표에서 UNTIL 을 건 벨트가 4 개 이상.</summary>
    static IEnumerable<Finding> S9(Facts f)
    {
        foreach (var t in f.Site.Seqs)
        {
            var belts = t.Steps.Where(s => s.Name == "BELT_ON" && s.Args.Count == 3).Select(s => s.Args[0].Text).Distinct().ToList();
            if (belts.Count >= 4) yield return f.Warn("S-9", t.Zone.Begin, $"UNTIL 벨트 {belts.Count} · {string.Join(", ", belts)}");
        }
    }

    /// <summary>S-10: SELECT_BEGIN·SELECT_END 짝, 묶음 밖 SELECT_END_TO, SELECT_END_TO( 0, … ).</summary>
    static IEnumerable<Finding> S10(Facts f)
    {
        foreach (var t in f.Site.Seqs)
        {
            int depth = 0;
            Statement? prev = null;
            foreach (var st in t.Steps)
            {
                if (st.Name == "SELECT_BEGIN") { if (depth > 0) yield return f.Warn("S-10", st, "SELECT_BEGIN 짝 없음"); depth = 1; }
                else if (st.Name == "SELECT_END") { if (depth == 0) yield return f.Warn("S-10", st, "SELECT_END 짝 없음"); depth = 0; }
                if (st.Name == "SELECT_END_TO")
                {
                    if (prev == null || !WhenFamily.Contains(prev.Name)) yield return f.Warn("S-10", st, "묶음 밖 SELECT_END_TO");
                    if (Val(f, st.Args[0]) == 0) yield return f.Warn("S-10", st, "SELECT_END_TO 0", st.Args[0]);
                }
                if (st.Name is not ("SELECT_BEGIN" or "SELECT_END")) prev = st;
            }
            if (depth > 0) yield return f.Warn("S-10", t.Zone.End ?? t.Zone.Begin, "SELECT_END 없음");
        }
    }

    /// <summary>S-11: 대응표가 가리키지 않는 표.</summary>
    static IEnumerable<Finding> S11(Facts f)
    {
        var seqs = f.Site.SeqMap.Select(m => m.Table).ToHashSet();
        var acts = f.Site.ActMap.Select(m => m.Table).ToHashSet();
        return f.Site.Seqs.Where(t => !(t.IsAct ? acts : seqs).Contains(t.Name)).Select(t => f.Warn("S-11", t.Zone.Begin, $"대응표 없음 {t.Name}"));
    }

    /// <summary>S-12: 대응표의 없는 표 이름, (프로파일, ID) 중복, 프로파일 0 줄 뒤의 같은 ID(죽은 줄).</summary>
    static IEnumerable<Finding> S12(Facts f)
    {
        foreach (var rows in new[] { f.Site.SeqMap, f.Site.ActMap })
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var kind = r.IsAct ? NameKind.ActTable : NameKind.SeqTable;
                var tbl = f.Names.Get(r.Table).FirstOrDefault(d => d.Kind == kind && d.File == r.Zone.File && d.Def!.StartLine < r.St.StartLine);
                if (tbl == null)
                    yield return f.Error("S-12", r.St, $"없는 표 {r.Table}", r.St.Args[2],
                        NameDict.Near(r.Table, f.Names.Of(kind).Where(d => d.File == r.Zone.File).Select(d => d.Name)));
                if (r.Zone.HasRaw) continue;
                var hider = rows.Take(i).FirstOrDefault(e => e.Zone == r.Zone && e.Id == r.Id && (e.AnyProfile || e.Profile == r.Profile));
                if (hider != null) yield return f.Error("S-12", r.St, Facts.Dead([r.Zone.Ordinal(hider.St)]), related: [hider.St.StartLine]);
            }
    }

    /// <summary>S-13: 사이트 시퀀스 ID 가 32~63 밖이거나 겹친다.</summary>
    static IEnumerable<Finding> S13(Facts f)
    {
        var seen = new Dictionary<long, NameDef>();
        foreach (var d in f.Names.Of(NameKind.SeqId).Where(d => !d.Core))
        {
            if (d.Value is not long v) continue;
            if (v < CoreConstants.SeqSiteBase || v >= CoreConstants.SeqMax) yield return f.Error("S-13", d.Def!, $"사이트 ID 범위 밖 {d.Name} {v} · 32~63");
            if (seen.TryGetValue(v, out var e)) yield return f.Error("S-13", d.Def!, $"ID 겹침 {v} · {e.Name}", related: [e.Def!.StartLine]);
            else seen[v] = d;
        }
    }

    /// <summary>S-14: 시퀀스 표에서 못 쓰는 것(회전 TAPE, 태그 전용 이름, ACS_SEND 의 STATION(k) ± n). 스텝 자리의 태그 전용 이름은 원문 블록이 된다.</summary>
    static IEnumerable<Finding> S14(Facts f) =>
        FromProblems(f, "S-14", p => p.Kind == "tagonly").Concat(
            f.Site.Files.SelectMany(file => file.RawInZones.Where(p => p.Zone is "SEQ_BEGIN" or "ACT_BEGIN")
                .Select(p => (p, name: new string(p.Text.TrimStart().TakeWhile(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray())))
                .Where(x => Facts.TagOnly.Contains(x.name))
                .Select(x => f.Error("S-14", x.p, $"시퀀스 표 금지 {x.name}"))));

    /// <summary>S-15: CALL 번호가 함수 표에 없다. ACT_START·WAIT_ACT 의 ID 가 ACT 대응표에 없다.</summary>
    static IEnumerable<Finding> S15(Facts f) => Targets(f, "S-15", a => a is Area.Seq or Area.Act);

    /// <summary>S-16: 시퀀스 회전 각도 1~359 밖.</summary>
    static IEnumerable<Finding> S16(Facts f) => Ranges(f, "S-16", u => u.Area is Area.Seq or Area.Act && u.Spec.Unit == "도");

    /// <summary>S-16b: 시퀀스 회전 속도 1~3.</summary>
    static IEnumerable<Finding> S16b(Facts f) => TurnSpeed(f, "S-16b", a => a is Area.Seq or Area.Act);

    /// <summary>S-17: 한 표 안에서 켜지 않는 벨트를 BELT_OFF·WAIT_BELTS 한다.</summary>
    static IEnumerable<Finding> S17(Facts f)
    {
        foreach (var t in f.Site.Seqs)
        {
            var on = t.Steps.Where(s => s.Name == "BELT_ON").Select(s => s.Args[0].Text).ToHashSet();
            foreach (var st in t.Steps.Where(s => s.Name is "BELT_OFF" or "WAIT_BELTS"))
                foreach (var a in st.Args.Where(a => !on.Contains(a.Text)))
                    yield return f.Warn("S-17", st, $"안 켠 벨트 {a.Text}", a);
        }
    }

    /// <summary>S-18: 표당 스텝 64 이하(SEQ_END·SELECT_BEGIN·SELECT_END 는 스텝이 아니다).</summary>
    static IEnumerable<Finding> S18(Facts f) =>
        f.Site.Seqs.Select(t => (t, n: RealSteps(t).Count)).Where(x => x.n > 64)
            .Select(x => f.Error("S-18", x.t.Zone.Begin, $"스텝 {x.n} · 64 이하 {x.t.Name}"));
}
