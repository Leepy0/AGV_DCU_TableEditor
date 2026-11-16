using System.Globalization;
using System.Text.RegularExpressions;
using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.App.Blocks;

/// <summary>
/// 문법 표(<see cref="GrammarTable"/>)의 칸 종류·라벨·팔레트만 보고 블록·칩을 만든다. 매크로마다 따로 짠 화면이 없다(설계서 6절).
/// </summary>
public sealed partial class BlockBuilder(NameDict names, IReadOnlyList<Finding> findings)
{
    // ---- 태그 동작 ----

    /// <summary>태그 동작 칸에 올 수 있는 문법 줄(칸을 차지하는 것).</summary>
    public static IEnumerable<GrammarEntry> ActionRows(string name) => GrammarTable.Find(name, Place.TagAction).Where(r => r.TagCells > 0);

    /// <summary>태그 규칙 동작 인자 하나 → 블록. index = 동작 순번.</summary>
    public BlockVm Action(Statement st, Arg a, int index)
    {
        var (name, args) = Split(a.Node);
        var entry = name == null ? null : Facts.Fit(ActionRows(name), args);
        var mark = MarkOf(st, a);
        if (entry == null)
        {
            if (names.Value(a.Node) == 0) // NO_ACTION: 여기서 멈춤
                return new BlockVm { Head = "동작 없음", Cat = "기타", Parts = [], Fields = [], Cells = 0, Arg = a, Mark = mark, Index = index };
            return RawBlock(a.Text, a, mark, index);
        }
        return new BlockVm
        {
            Head = entry.Head, Cat = entry.Palette, Parts = PartsOf(entry, args, true),
            Fields = ActionFields(entry, a, args), Cells = entry.TagCells, Arg = a, Mark = mark, Index = index,
        };
    }

    /// <summary>형식 칸 낱말(형식 낱말이 빈 형식).</summary>
    public const string Bare = "바로";

    /// <summary>
    /// 동작 칸들. 꼴이 여럿이면(<see cref="ActionForms"/>) 맨 앞에 위치 칸(바로·cm·TAPE·TAPE_GD·센서)을 두고, 꼴이 정한 낱말 하나뿐인 칸은 뺀다.
    /// 위치 칸을 바꾸면 그 꼴로 인자 전체를 다시 쓴다(같은 인자 바꾸기 명령).
    /// </summary>
    List<FieldInfo> ActionFields(GrammarEntry entry, Arg a, IReadOnlyList<Arg> args)
    {
        var fields = FieldsOf(entry, args);
        var forms = ActionForms.Of(entry);
        if (forms.Count < 2) return fields;
        var form = FormField(entry, forms, a, args);
        form = form with { Write = w => ActionForms.Convert(forms.First(e => WordOf(e) == w), entry, args, x => Choices.DefaultArg(x, names)) };
        return [form, .. fields.Where(f => f.Spec is not { Kind: ArgKind.Word, Words.Count: 1 })];
    }

    static string WordOf(GrammarEntry e) => ActionForms.Word(e) is { Length: > 0 } w ? w : Bare;

    /// <summary>형식 칸(<see cref="ActionForms"/>): 값 = 지금 형식 낱말, 고르면 그 형식으로. 버려지는 인자는 <see cref="FieldInfo.Drops"/>.</summary>
    static FieldInfo FormField(GrammarEntry entry, IReadOnlyList<GrammarEntry> forms, Arg a, IReadOnlyList<Arg> args)
    {
        var label = ActionForms.FieldLabel(forms);
        var spec = new ArgSpec(ArgKind.Word, Words: forms.Select(WordOf).ToList()) { Label = label };
        return new FieldInfo(label, a, spec)
        {
            Value = WordOf(entry),
            To = w => forms.First(e => WordOf(e) == w),
            Drops = w => ActionForms.Dropped(forms.First(e => WordOf(e) == w), entry, args),
        };
    }

    /// <summary>문장 하나 = 블록 하나(시퀀스 스텝, 수동 입력 줄). 문법 표의 그 자리 줄로 칸을 만든다. 모르면 원문 블록.</summary>
    public BlockVm Statement(Statement st, Place place, int index = -1)
    {
        var entry = Facts.Fit(GrammarTable.Find(st.Name, place), st.Args);
        if (entry == null) return RawBlock(st.Text, null, MarkOf(st, null), index);
        var fields = FieldsOf(entry, st.Args);
        var forms = ActionForms.Of(entry);
        if (forms.Count > 1 && st.Args.Count > 0) // 형식 칸: 맨 앞. 인자가 없는 형식은 칸이 없다
            fields = [FormField(entry, forms, st.Args[0], st.Args), .. fields.Where(f => f.Spec is not { Kind: ArgKind.Word, Words.Count: 1 })];
        return new BlockVm
        {
            Head = entry.Head, Cat = entry.Palette.Length > 0 ? entry.Palette : entry.Category, Parts = PartsOf(entry, st.Args, true),
            Fields = fields, Cells = 0, Mark = MarkOf(st, null), Index = index,
        };
    }

    // ---- 수동 표 ----

    static readonly ArgSpec OutSpec = new(ArgKind.Output) { Label = "출력" };

    /// <summary>수동 표 출력 칸 하나 → 블록: <c>ON</c> 켬, <c>OFF</c> 끔, <c>BELT</c> 벨트, <c>NO_OUT</c> 출력 없음.</summary>
    public BlockVm ManOut(Statement st, Arg a, int index)
    {
        var mark = MarkOf(st, a);
        var (name, args) = Split(a.Node);
        switch (name)
        {
            case "ON" or "OFF" when args.Count > 0:
                return new BlockVm
                {
                    Head = name == "ON" ? "켬" : "끔", Cat = "출력", Parts = args.Select(x => new Part(null, Collapse(x.Text), null)).ToList(),
                    Fields = args.Select(x => new FieldInfo("출력", x, OutSpec)).ToList(), Cells = 0, Arg = a, Mark = mark, Index = index,
                };
            case "BELT" when GrammarTable.Find("BELT", Place.Manual).FirstOrDefault() is { } e && args.Count == 2:
                return new BlockVm { Head = e.Head, Cat = "벨트", Parts = PartsOf(e, args, true), Fields = FieldsOf(e, args), Cells = 0, Arg = a, Mark = mark, Index = index };
            case "NO_OUT" when args.Count == 0:
                return new BlockVm { Head = "출력 없음", Cat = "기타", Parts = [], Fields = [], Cells = 0, Arg = a, Mark = mark, Index = index };
            default:
                return RawBlock(a.Text, a, mark, index);
        }
    }

    /// <summary>수동 조건 칸 → 칩: <c>IN( 묶음… )</c> 은 묶음마다, <c>KEY( 키 )</c>, <c>NO_IN</c> 은 없음.</summary>
    public IReadOnlyList<Chip> ManChips(Arg cond)
    {
        var (name, args) = Split(cond.Node);
        return name switch
        {
            "IN" => args.Select(x => ChipOf(x, Place.InputCond)).ToList(),
            "ON" or "OFF" or "ANY_ON" => [ChipOf(cond, Place.InputCond)],
            "KEY" when args.Count == 1 => [new Chip("키", [new(null, Collapse(args[0].Text), null)])],
            "NO_IN" => [new Chip("없음", [])],
            _ => [new Chip(Collapse(cond.Text), [])],
        };
    }

    /// <summary>프로파일 적재 칸 → 칩: 센서 묶음은 <c>센서 A B</c>, 적재 조건은 <c>조건 이름</c>.</summary>
    public static IReadOnlyList<Chip> LoadChips(Arg? load)
    {
        if (load == null) return [];
        var (name, args) = Split(load.Node);
        return name == "LOAD_SENSORS"
            ? [new Chip("센서", args.Select(x => new Part(null, Collapse(x.Text), null)).ToList())]
            : [new Chip("조건", [new(null, Collapse(load.Text), null)])];
    }

    /// <summary>원문 블록: 회색, 첫 줄만.</summary>
    public static BlockVm RawBlock(string text, Arg? a = null, Severity? mark = null, int index = -1) => new()
    {
        Head = "", Cat = "raw", Raw = true, Parts = [new(null, FirstLine(text), null)], Fields = [], Cells = 1, Arg = a, Mark = mark, Index = index,
    };

    /// <summary>칸에 걸린 검사 결과의 등급(a = null 이면 문장 어디든).</summary>
    Severity? MarkOf(Statement st, Arg? a)
    {
        Severity? worst = null;
        foreach (var f in findings)
        {
            if (!ReferenceEquals(f.Piece, st) || f.Cell is not { } c) continue;
            if (a != null && (c.Offset < a.Offset || c.Offset > a.Offset + a.Text.Length)) continue;
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
    /// 숫자 칸이 둘 이상인 매크로의 숫자 글자 앞에는 작은 라벨(목업의 <c>출발 1</c>). 안쪽 묶음은 첫 값 앞에 그 머리.
    /// </summary>
    List<Part> PartsOf(GrammarEntry e, IReadOnlyList<Arg> args, bool action)
    {
        var specs = Facts.Expand(e, args.Count) ?? [];
        int numbers = specs.Count(s => s.Kind is ArgKind.Number or ArgKind.Time or ArgKind.StopDist);
        var parts = new List<Part>();
        for (int i = 0; i < args.Count && i < specs.Count; i++)
        {
            var spec = specs[i];
            var a = args[i];
            if (i + 1 < args.Count && i + 1 < specs.Count && Starts.Contains(spec.Label) && Ends.Contains(specs[i + 1].Label))
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
                    // 안쪽 묶음 머리(꺼짐 등)가 있으면 앞에 붙인다. IN 은 묶음 머리만으로 뜻이 선다
                    sub[0] = sub[0] with { Label = sub[0].Label == null ? ie.Head : ie.Name == "IN" ? sub[0].Label : $"{ie.Head} {sub[0].Label}" };
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
            if (spec.SwitchText(a.Node) is { } sw)
            {
                parts.Add(new(numbers > 1 && spec.Kind == ArgKind.Number ? Short(specs, i) : null, sw, null) { On = sw == ArgSpec.On });
                continue;
            }
            if (spec.Unit == "도") parts.Add(new(null, text + "°", null));
            else if (spec.Unit == "틱") parts.Add(new(null, text, Seconds(names, a.Node)));
            else if (spec.Kind is ArgKind.Number && a.Node is NumberNode && numbers > 1) parts.Add(new(Short(specs, i), text, null));
            else if (SuffixUnits.Contains(spec.Unit) && a.Node is NumberNode) parts.Add(new(null, text, spec.Unit));
            else if (spec.Kind == ArgKind.SeqShort && a.Node is NameNode) parts.Add(new(null, text, null) { Seq = text });
            else parts.Add(new(null, text, null) { IsLabel = spec.Kind == ArgKind.Label });
        }
        return parts;
    }

    /// <summary>선택 줄 칸: 인자마다 하나. 안쪽 묶음(센서 등)은 그 칸들로 편다. 출력 조건(WAIT_OUT) 안 신호는 출력 이름에서 고른다.</summary>
    List<FieldInfo> FieldsOf(GrammarEntry e, IReadOnlyList<Arg> args, bool outs = false)
    {
        var specs = Facts.Expand(e, args.Count) ?? [];
        var list = new List<FieldInfo>();
        for (int i = 0; i < args.Count && i < specs.Count; i++)
        {
            var (name, inner) = Split(args[i].Node);
            var spec = outs && specs[i].Kind == ArgKind.Signal ? specs[i] with { Kind = ArgKind.Output, Label = "출력" } : specs[i];
            if (spec.Kind == ArgKind.OutCond) list.Add(OutKind(args[i], spec));
            if (args[i].Node is CallNode && name != null && InnerFor(spec.Kind, name, inner) is { } ie && ie.Args.Count > 0)
                list.AddRange(FieldsOf(ie, inner, outs || spec.Kind == ArgKind.OutCond));
            else if (spec.Kind != ArgKind.OutCond) list.Add(SwitchField(spec, args[i]) ?? new(spec.Label, args[i], spec));
            else if (name != "NO_IN") list.Add(new(spec.Label, args[i], spec));
        }
        return list;
    }

    public const string OutNone = "없음", OutOn = "켜짐", OutOff = "꺼짐";

    /// <summary>출력 조건 칸의 켬·끔 고르기: 없음(<c>NO_IN</c>) / 켜짐 / 꺼짐. 고르면 <c>IN( ON( 출력 ) )</c> 로 다시 쓴다(있던 출력은 그대로).</summary>
    FieldInfo OutKind(Arg a, ArgSpec spec)
    {
        var (name, inner) = Split(a.Node);
        var (group, sigs) = name == "IN" && inner.Count == 1 ? Split(inner[0].Node) : (null, []);
        var shown = name == "NO_IN" ? OutNone : group == "ON" ? OutOn : group == "OFF" ? OutOff : Collapse(a.Text);
        var sig = sigs.Count > 0 ? string.Join(", ", sigs.Select(x => Collapse(x.Text))) : Choices.For(new ArgSpec(ArgKind.Output), names).FirstOrDefault(c => c.Group == "사이트")?.Name ?? "OUT_EXT_1";
        return new FieldInfo("켬·끔", a, new ArgSpec(ArgKind.Word, Words: [OutNone, OutOn, OutOff]) { Label = "켬·끔" })
        {
            Value = shown,
            Write = w => w == OutNone ? "NO_IN" : $"IN( {(w == OutOn ? "ON" : "OFF")}({sig}) )",
        };
    }

    /// <summary>켬/끔 칸(값 1·0): ON·OFF 드롭다운. 고르면 원문에 1·0 을 쓴다. 다른 값이면 null(숫자 칸 그대로).</summary>
    static FieldInfo? SwitchField(ArgSpec spec, Arg a) => spec.SwitchText(a.Node) is { } sw
        ? new(spec.Label, a, new ArgSpec(ArgKind.Word, Words: [ArgSpec.On, ArgSpec.Off]) { Label = spec.Label }) { Value = sw, Write = ArgSpec.SwitchRaw }
        : null;

    /// <summary>틱 → 초 글자(300 → 3.00).</summary>
    public static string? Seconds(NameDict names, Node n) => names.Value(n) is long v ? (v / 100.0).ToString("0.00", CultureInfo.InvariantCulture) : null;

    /// <summary>작은 라벨: 라벨의 끝 낱말(<c>자동 출발</c> → <c>출발</c>). 앞 칸과 겹치면 전부.</summary>
    static string Short(IReadOnlyList<ArgSpec> specs, int i)
    {
        var last = specs[i].Label.Split(' ')[^1];
        return last.Length < 2 || specs.Take(i).Any(s => s.Label == last) ? specs[i].Label : last; // 회전 뒤 → 뒤 는 뜻이 없다
    }

    // ---- 문법 줄 고르기 ----

    static GrammarEntry? Inner(string name, IReadOnlyList<Arg> args, Place place) =>
        Facts.Fit(GrammarTable.Find(name, place).Where(r => r.Role != Role.Statement), args);

    static GrammarEntry? InnerFor(ArgKind kind, string name, IReadOnlyList<Arg> args)
    {
        Place? place = kind switch
        {
            ArgKind.XCond => Place.ExtCond,
            ArgKind.InCond or ArgKind.InGroup or ArgKind.OutCond or ArgKind.Any2 => Place.InputCond,
            ArgKind.FromSensor => Place.TagAction,
            ArgKind.Until or ArgKind.StationExpr or ArgKind.AcsSource or ArgKind.AcsNumber => Place.Step,
            ArgKind.Timeout => Place.InputCond,
            ArgKind.LoadSensors => Place.Frame,
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
