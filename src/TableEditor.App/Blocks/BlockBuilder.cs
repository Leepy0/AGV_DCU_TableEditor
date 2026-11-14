using System.Globalization;
using System.Text.RegularExpressions;
using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.App.Blocks;

/// <summary>
/// 문법 표(<see cref="GrammarTable"/>)의 칸 종류만 보고 블록·칩을 만든다. 매크로마다 따로 짠 화면이 없다(설계서 6절).
/// </summary>
public sealed partial class BlockBuilder(Facts facts, IReadOnlyList<Finding> findings)
{
    NameDict Names => facts.Names;

    // ---- 태그 동작 ----

    /// <summary>태그 규칙 동작 인자 하나 → 블록.</summary>
    public BlockVm Action(Statement st, Arg a)
    {
        var (name, args) = Split(a.Node);
        var entry = name == null ? null : facts.FitRow(GrammarTable.Find(name, Place.TagAction).Where(r => r.TagCells > 0), args);
        var mark = MarkOf(st, a);
        if (entry == null)
        {
            if (Names.Value(a.Node) == 0) // NO_ACTION: 여기서 멈춤
                return new BlockVm { Head = "동작 없음", Cat = "etc", Parts = [], Fields = [], Cells = 0, Arg = a, Mark = mark };
            return RawBlock(a.Text, a, mark);
        }
        return new BlockVm
        {
            Head = entry.Head, Cat = Palette.Of(entry.Name), Parts = PartsOf(entry, args, true),
            Fields = FieldsOf(entry, args), Cells = entry.TagCells, Arg = a, Mark = mark,
        };
    }

    /// <summary>원문 블록: 회색, 첫 줄만.</summary>
    public static BlockVm RawBlock(string text, Arg? a = null, Severity? mark = null) => new()
    {
        Head = "", Cat = "raw", Raw = true, Parts = [new(null, FirstLine(text), null)], Fields = [], Cells = 1, Arg = a, Mark = mark,
    };

    Severity? MarkOf(Statement st, Arg a)
    {
        Severity? worst = null;
        foreach (var f in findings)
        {
            if (!ReferenceEquals(f.Piece, st) || f.Cell is not { } c) continue;
            if (c.Offset < a.Offset || c.Offset > a.Offset + a.Text.Length) continue;
            if (worst == null || f.Severity == Severity.Error) worst = f.Severity;
        }
        return worst;
    }

    // ---- 조건 칩 ----

    /// <summary>조건 → 칩: 확장 조건 0~2개 + 입력 조건 0~1개(사양 4.6절 끝). 그 밖의 IF_… 는 칩 하나.</summary>
    public IReadOnlyList<Chip> Chips(Arg? cond)
    {
        if (cond == null) return [];
        var (name, args) = Split(cond.Node);
        return name switch
        {
            "IF_X" or "IF_X2" => args.Select(x => ChipOf(x, Place.ExtCond)).ToList(),
            "IF_IN" => [ChipOf(args[0], Place.InputCond)],
            "IF_X_IN" or "IF_X2_IN" => args.SkipLast(1).Select(x => ChipOf(x, Place.ExtCond)).Append(ChipOf(args[^1], Place.InputCond)).ToList(),
            _ => [ChipOf(cond, Place.TagCond)],
        };
    }

    Chip ChipOf(Arg a, Place place)
    {
        var (name, args) = Split(a.Node);
        var entry = name == null ? null : Inner(name, args, place);
        return entry == null ? new Chip(Collapse(a.Text), []) : new Chip(entry.Head, PartsOf(entry, args, false));
    }

    // ---- 칸 ----

    static readonly HashSet<string> Starts = ["처음", "하한"];
    static readonly HashSet<string> Ends = ["끝", "상한"];
    static readonly HashSet<string> SuffixUnits = ["cm", "초", "m/min"];

    /// <summary>
    /// 블록 안 값들. 처음·끝 짝은 <c>a–b</c>(같으면 하나), 각도는 <c>°</c>, 틱은 옆에 초를 작게,
    /// 숫자 칸이 둘 이상인 매크로의 숫자 글자 앞에는 작은 라벨(목업의 <c>출발 1</c>).
    /// </summary>
    List<Part> PartsOf(GrammarEntry e, IReadOnlyList<Arg> args, bool action)
    {
        var specs = Facts.Expand(e, args.Count) ?? [];
        var labels = Expand(Labels(e), e, args.Count);
        int numbers = specs.Count(s => s.Kind is ArgKind.Number or ArgKind.Time or ArgKind.StopDist);
        var parts = new List<Part>();
        for (int i = 0; i < args.Count && i < specs.Count; i++)
        {
            var spec = specs[i];
            var a = args[i];
            if (i + 1 < args.Count && Starts.Contains(labels[i]) && Ends.Contains(labels[i + 1]))
            {
                var (x, y) = (Collapse(a.Text), Collapse(args[i + 1].Text));
                parts.Add(new(null, x == y ? x : $"{x}–{y}", null));
                i++;
                continue;
            }
            var (name, inner) = Split(a.Node);
            if (a.Node is CallNode && name != null && InnerFor(spec.Kind, name, inner) is { } ie)
            {
                var sub = PartsOf(ie, inner, action);
                if (sub.Count == 0) parts.Add(new(null, ie.Head, null));
                else
                {
                    if (!action) sub[0] = sub[0] with { Label = ie.Head };
                    parts.AddRange(sub);
                }
                continue;
            }
            if (a.Node is NameNode && name != null && !action && InnerFor(spec.Kind, name, []) is { } ne)
            {
                parts.Add(new(null, ne.Head, null)); // X_FORWARD 같은 인자 없는 조건
                continue;
            }
            var text = Collapse(a.Text);
            if (spec.Unit == "도") parts.Add(new(null, text + "°", null));
            else if (spec.Unit == "틱") parts.Add(new(null, text, Seconds(a.Node)));
            else if (spec.Kind is ArgKind.Number && a.Node is NumberNode && numbers > 1) parts.Add(new(Short(labels, i), text, null));
            else if (SuffixUnits.Contains(spec.Unit) && a.Node is NumberNode) parts.Add(new(null, text, spec.Unit));
            else parts.Add(new(null, text, null));
        }
        return parts;
    }

    /// <summary>선택 줄 칸: 라벨 + 값(원문 글자) + 작은 단위.</summary>
    List<Field> FieldsOf(GrammarEntry e, IReadOnlyList<Arg> args)
    {
        var specs = Facts.Expand(e, args.Count) ?? [];
        var labels = Expand(Labels(e), e, args.Count);
        var list = new List<Field>();
        for (int i = 0; i < args.Count && i < specs.Count; i++)
        {
            var s = specs[i];
            string? small = s.Unit switch
            {
                "도" => "°",
                "틱" => Seconds(args[i].Node),
                "cm" or "초" or "m/min" => s.Unit,
                _ => null,
            };
            bool mono = s.Kind is ArgKind.Number or ArgKind.Time or ArgKind.Timeout or ArgKind.StopDist or ArgKind.AcsNumber;
            list.Add(new(labels[i], Collapse(args[i].Text), small, mono));
        }
        return list;
    }

    string? Seconds(Node n) => Names.Value(n) is long v ? (v / 100.0).ToString("0.00", CultureInfo.InvariantCulture) : null;

    /// <summary>작은 라벨: 라벨의 끝 낱말(<c>자동 출발</c> → <c>출발</c>). 앞 칸과 겹치면 전부.</summary>
    static string Short(IReadOnlyList<string> labels, int i)
    {
        var last = labels[i].Split(' ')[^1];
        return labels.Take(i).Contains(last) ? labels[i] : last;
    }

    // ---- 문법 줄 고르기 ----

    GrammarEntry? Inner(string name, IReadOnlyList<Arg> args, Place place) =>
        facts.FitRow(GrammarTable.Find(name, place).Where(r => r.Role != Role.Statement), args);

    GrammarEntry? InnerFor(ArgKind kind, string name, IReadOnlyList<Arg> args)
    {
        Place? place = kind switch
        {
            ArgKind.XCond => Place.ExtCond,
            ArgKind.InCond or ArgKind.InGroup or ArgKind.OutCond or ArgKind.Any2 or ArgKind.Until => Place.InputCond,
            ArgKind.FromSensor => Place.TagAction,
            _ => null,
        };
        return place is { } p && Facts.Allowed(kind, Area.Tag).Contains(name) ? Inner(name, args, p) : null;
    }

    static (string? Name, IReadOnlyList<Arg> Args) Split(Node n) => n switch
    {
        CallNode c => (c.Name, c.Args),
        NameNode m => (m.Name, []),
        _ => (null, []),
    };

    // ---- 칸 라벨: 문법 표의 꼴 글자에서 ----

    static readonly Dictionary<GrammarEntry, string[]> labelCache = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// 칸마다 라벨(문법 줄의 칸 하나에 하나). 꼴 글자(<c>AGV_TURN( 방향, 각도, … )</c>)의 괄호 안을 쉼표로 나눈다.
    /// 꼴이 <c>( … )</c> 처럼 줄여 적혀 수가 안 맞으면 같은 이름의 다른 줄에서 가져온다.
    /// </summary>
    static string[] Labels(GrammarEntry e)
    {
        lock (labelCache)
        {
            if (labelCache.TryGetValue(e, out var c)) return c;
            var got = FromForm(e.Form);
            if (got.Length != e.Args.Count)
                got = GrammarTable.Find(e.Name).Select(r => FromForm(r.Form)).FirstOrDefault(l => l.Length == e.Args.Count)
                      ?? e.Args.Select(a => a.Unit).ToArray();
            return labelCache[e] = got;
        }
    }

    static string[] FromForm(string form)
    {
        int open = form.IndexOf('('), close = form.LastIndexOf(')');
        if (open < 0 || close <= open) return [];
        var body = form[(open + 1)..close].Replace("[", "").Replace("]", "");
        var list = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i <= body.Length; i++)
        {
            if (i < body.Length && body[i] == '(') depth++;
            else if (i < body.Length && body[i] == ')') depth--;
            else if (i == body.Length || body[i] == ',' && depth == 0)
            {
                var t = body[start..i].Trim().TrimEnd('…').Trim();
                if (t.Contains('(')) t = GrammarTable.Find(t[..t.IndexOf('(')].Trim()).FirstOrDefault()?.Head ?? t;
                if (t.Length > 0 && t != "…") list.Add(t);
                start = i + 1;
            }
        }
        return list.ToArray();
    }

    /// <summary>라벨을 실제 인자 수로 편다(<see cref="Facts.Expand"/> 와 같은 차례).</summary>
    static List<string> Expand(string[] labels, GrammarEntry e, int n)
    {
        var list = new List<string>(n);
        int extra = n - e.MinArgs;
        for (int k = 0; k < e.Args.Count; k++)
        {
            var a = e.Args[k];
            int m = a.Least + Math.Max(0, Math.Min(a.Most - a.Least, extra));
            extra -= m - a.Least;
            for (int i = 0; i < m; i++) list.Add(k < labels.Length ? labels[k] : "");
        }
        while (list.Count < n) list.Add("");
        return list;
    }

    // ---- 글자 ----

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>공백·줄바꿈을 한 칸으로.</summary>
    public static string Collapse(string s) => Spaces().Replace(s.Trim(), " ");

    public static string FirstLine(string s)
    {
        var t = s.TrimStart();
        int i = t.IndexOfAny(['\r', '\n']);
        return (i < 0 ? t : t[..i]).TrimEnd();
    }
}
