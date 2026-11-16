using System.Text.RegularExpressions;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.Engine.Check;

// 6.8 새 DSL 형식(코어 v0.58.0 이상 사이트만). 코어가 새로 컴파일 오류로 막는 것을 저장 전에 잡는다. 모두 오류 등급.
public static partial class Checker
{
    /// <summary>AND 한 개의 항목 분류.</summary>
    sealed record AndShape(CallNode And, int Items, int States, int Anys, bool AnyOn, bool NotNot)
    {
        public IReadOnlyList<Arg> Args => And.Args;
    }

    static bool IsState(Node n) => n is CallNode { Name: var cn } && Facts.StateWords.Contains(cn) && cn != "ALWAYS" || n is NameNode { Name: var nn } && Facts.StateWords.Contains(nn) && nn != "ALWAYS";

    static IEnumerable<Node> Items(CallNode and) => and.Args.Select(a => a.Node);

    /// <summary>IN( … ) 안과 항목 자리의 <c>ANY_ON</c> 이 있는가(ANY 와 같이 쓰면 둘이 합쳐진다).</summary>
    static bool HasAnyOn(CallNode and) => Items(and).Any(n => n is CallNode { Name: "ANY_ON" } || n is CallNode { Name: "IN" } c && c.Args.Any(a => a.Node is CallNode { Name: "ANY_ON" }));

    static AndShape Shape(CallNode and) => new(and, and.Args.Count, Items(and).Count(IsState), Items(and).Count(n => n is CallNode { Name: "ANY" }), HasAnyOn(and),
        Items(and).Any(n => n is CallNode { Name: "NOT", Args: [{ Node: CallNode { Name: "NOT" } }] }));

    /// <summary>AND 가 놓인 칸: (문장 이름, 칸 이름). 칸마다 받는 상태·둘째 ANY 한계가 다르다.</summary>
    static IEnumerable<(Use Use, AndShape Shape, string Slot)> Ands(Facts f)
    {
        foreach (var u in f.Uses.Where(u => u.Spec.Kind is ArgKind.AndCond or ArgKind.TagCond or ArgKind.ManCond && u.Node is CallNode { Name: "AND" }))
        {
            var st = u.St;
            string slot = st.Name switch
            {
                "WHEN" => "WHEN",
                "WAIT" => "WAIT",
                "WAIT_TO" => st.Args.Count > 1 && st.Args[1].Node is CallNode { Name: "PARAM_TIME" } ? "입력만" : "WAIT_TO",
                "WAIT_STABLE" or "WAIT_STABLE_TO" or "ERROR_HOLD" => "입력만",
                "OUT_FLICKER_UNTIL_IN" or "OUT_FLICKER_UNTIL_IN_TO" => "점멸",
                "TAG_IF" or "TAG_RANGE_IF" => "태그",
                "MAN" => "수동",
                _ => "",
            };
            if (slot.Length > 0) yield return (u, Shape((CallNode)u.Node), slot);
        }
    }

    /// <summary>D-1: AND 칸 한계(항목 7, 칸마다 상태·둘째 ANY 한계). 넘으면 코어가 컴파일 오류로 막는다.</summary>
    static IEnumerable<Finding> D1(Facts f)
    {
        if (!f.Site.NewDsl) yield break;
        foreach (var (u, a, slot) in Ands(f))
        {
            if (a.Items > 7) yield return f.Error("D-1", u.St, $"AND 항목 {a.Items}개 · 7 이하", u.Loc);
            int maxState = slot switch { "WHEN" or "WAIT" or "WAIT_TO" or "점멸" => 1, "태그" => 2, "수동" => 1, _ => 0 };
            string ok = slot switch { "입력만" => "입력만", "수동" => "KEY 하나 + 입력", _ => $"{maxState}개" };
            if (a.States > maxState) yield return f.Error("D-1", u.St, $"AND 상태 {a.States}개 · {slot} {ok}", u.Loc);
            if (slot == "수동" && Items(a.And).Any(n => IsState(n) && NameOf(n) != "KEY"))
                yield return f.Error("D-1", u.St, "AND 수동 조건 · 상태는 KEY 만", u.Loc);
            int maxAny = slot switch { "WHEN" or "WAIT" or "WAIT_TO" => a.States > 0 ? 1 : 2, "태그" or "점멸" or "입력만" or "수동" => 1, _ => 2 };
            if (a.Anys > maxAny)
                yield return f.Error("D-1", u.St, a.Anys > 2 ? $"AND ANY {a.Anys}개 · 2 이하" : $"AND 둘째 ANY · {slot}{(a.States > 0 ? " 상태와 함께 못 씀" : " 못 씀")}", u.Loc);
        }
    }

    /// <summary>D-2: 태그 규칙의 동작 칸에 <c>X_…</c> 상태가 들어갔다(<c>TAG_IF( t, p, AND( … ), X_…, 동작 )</c> 처럼 둘째 상태가 동작으로 조용히 실행된다).</summary>
    static IEnumerable<Finding> D2(Facts f)
    {
        if (!f.Site.NewDsl) yield break;
        foreach (var r in f.Site.TagRules.Where(r => r.HasCond))
            foreach (var a in r.Actions.Where(a => NameOf(a.Node) is { } n && n.StartsWith("X_", StringComparison.Ordinal) && GrammarTable.Find(n).Any(x => x.Place == Place.ExtCond)))
                yield return f.Error("D-2", r.St, $"조건 칸 밖 {NameOf(a.Node)} · TAG_IF 는 IF_… 나 AND( … ) 하나만", a);
    }

    /// <summary>D-3: AND 안에서 <c>ANY_ON</c>(<c>IN( ANY_ON(…) )</c> 포함)과 <c>ANY(…)</c> 를 같이 쓰면 두 묶음이 합쳐져 조건이 약해진다.</summary>
    static IEnumerable<Finding> D3(Facts f)
    {
        if (!f.Site.NewDsl) yield break;
        foreach (var u in f.Uses.Where(u => u.Node is CallNode { Name: "AND" }))
        {
            var a = Shape((CallNode)u.Node);
            if (a.Anys > 0 && a.AnyOn) yield return f.Error("D-3", u.St, "AND 안 ANY_ON + ANY 섞임 · ANY 로 통일", u.Loc);
        }
    }

    /// <summary>D-4: NOT 겹침, PARAM_MIN·PARAM_MAX·PARAM_TIME 에 자리 있는 파라미터(<c>TFB_1</c>·<c>TFB_10</c>).</summary>
    static IEnumerable<Finding> D4(Facts f)
    {
        if (!f.Site.NewDsl) yield break;
        foreach (var u in f.Uses)
        {
            if (u.Node is not CallNode c) continue;
            if (c.Name == "NOT" && c.Args.Count == 1 && c.Args[0].Node is CallNode { Name: "NOT" })
                yield return f.Error("D-4", u.St, "NOT 겹침 · 같은 뜻이 아니다", u.Loc);
            if (c.Name is "PARAM_MIN" or "PARAM_MAX" or "PARAM_TIME" && c.Args.Count > 0 && c.Args[0].Node is NameNode { Name: var p } && CoreConstants.PlacedParams.Contains(p))
                yield return f.Error("D-4", u.St, $"{c.Name} 자리 있는 파라미터 {p}", c.Args[0]);
        }
    }

    [GeneratedRegex(@"\b(?:ACS|AGV_SPEED)\s*\(\s*(?:[A-Z_]+\s*,\s*)?\(")]
    private static partial Regex ParenArgRx();

    [GeneratedRegex(@"/\*.*?\*/|//[^\n]*", RegexOptions.Singleline)]
    private static partial Regex CommentRx();

    /// <summary>D-5: <c>ACS( 종류, CONST )</c>(늘 0 을 보고하는 낱말이라 숫자 0 을 쓴다), 번호·속도 칸이 <c>(</c> 로 시작하는 식(<c>ACS( 종류, (70 + 7) )</c>, <c>AGV_SPEED( (20 + 10) )</c>).</summary>
    static IEnumerable<Finding> D5(Facts f)
    {
        if (!f.Site.NewDsl) yield break;
        foreach (var file in f.Site.Files)
            foreach (var st in file.Statements.Where(s => s.Shape == StatementShape.Macro && s.Name == "ACS" && f.AreaOf(s) is Area.Seq or Area.Act))
                if (st.Args.Count == 2 && st.Args[1].Node is NameNode { Name: "CONST" })
                    yield return f.Error("D-5", st, "ACS CONST · 숫자 0 을 쓴다", st.Args[1]);
        // 원문 블록과 문장으로 읽힌 줄(태그 동작 안의 ACS·AGV_SPEED 포함) 모두, 주석을 지운 글로 본다(칸 앞 주석이 있어도 같다)
        foreach (var p in f.Site.Files.SelectMany(file => file.RawInZones.Concat(file.Statements.Where(s => s.Shape == StatementShape.Macro && s.Zone != null))))
            if (ParenArgRx().Match(CommentRx().Replace(p.Text, " ")) is { Success: true } m)
                yield return f.Error("D-5", p, $"괄호로 시작하는 식 · {m.Value.Split('(')[0].Trim()} 은 ( 로 시작하는 칸을 못 받는다");
    }

    /// <summary>D-6: 제한 칸. <c>SELECT_END_TO</c> 제한 1~32767, <c>WAIT_TO</c>·<c>HANDSHAKE</c>·<c>OUT_FLICKER_UNTIL_IN_TO</c> 제한 0x8000 미만(<c>UNMATCHED</c> 는 <c>WAIT_STABLE_TO</c> 만).</summary>
    static IEnumerable<Finding> D6(Facts f)
    {
        if (!f.Site.NewDsl) yield break;
        foreach (var file in f.Site.Files)
            foreach (var st in file.Statements.Where(s => s.Shape == StatementShape.Macro && f.AreaOf(s) is Area.Seq or Area.Act))
            {
                int at = st.Name switch { "WAIT_TO" => 1, "HANDSHAKE" => 2, "OUT_FLICKER_UNTIL_IN_TO" => 3, _ => -1 };
                if (st.Name == "SELECT_END_TO" && st.Args.Count == 2 && Val(f, st.Args[0]) is long t && (t < 1 || t > 0x7FFF))
                    yield return f.Error("D-6", st, $"SELECT_END_TO 제한 {t} · 1~32767", st.Args[0]);
                if (at < 0 || st.Args.Count <= at) continue;
                var a = st.Args[at];
                if (a.Node is CallNode { Name: "UNMATCHED" }) yield return f.Error("D-6", st, $"{st.Name} UNMATCHED · WAIT_STABLE_TO 만", a);
                else if (a.Node is not CallNode && Val(f, a) is long v && (v >= 0x8000 || v < 0)) yield return f.Error("D-6", st, $"{st.Name} 제한 {v} · 32767 이하", a);
            }
    }
}
