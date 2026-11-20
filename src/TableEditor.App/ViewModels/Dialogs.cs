using System.Collections.ObjectModel;
using System.Windows.Input;
using TableEditor.App.Blocks;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.App.ViewModels;

/// <summary>창 안 팝업(어둡게 덮고 가운데 카드). Close(열쇠) 로 끝난다. null = 취소(Esc).</summary>
public abstract class DialogVm : Observable
{
    readonly TaskCompletionSource<string?> done = new();

    public abstract string Title { get; }
    public Task<string?> Result => done.Task;
    public ICommand Cancel { get; }

    protected DialogVm() => Cancel = new Command(() => Close(null));

    public void Close(string? key) => done.TrySetResult(key);
}

/// <summary>팝업 단추. Primary = 강조 색, Default = 처음 포커스(위험한 팝업은 취소).</summary>
public sealed record AskButton(string Key, string Label, bool Primary = false, bool Default = false);

/// <summary>짧은 확인 팝업: 제목, 낱말 몇 개(목록), 단추(같은 크기).</summary>
public sealed class AskVm(string title, IReadOnlyList<string> lines, IReadOnlyList<AskButton> buttons) : DialogVm
{
    public override string Title => title;
    public IReadOnlyList<string> Lines => lines;
    public IReadOnlyList<AskButton> Buttons => buttons;
    public ICommand Press => new Command(p => Close(p as string));
}

// ---- 태그 추가 ----

public sealed record TemplateChoice(string Text, TagRule? Rule)
{
    public override string ToString() => Text;
}

/// <summary>미리 보기 한 줄: 태그 + 프로파일마다 새 줄 / 있음.</summary>
public sealed record PreviewRow(long Tag, IReadOnlyList<bool> Exists);

/// <summary>[추가] 팝업: 태그 글 × 프로파일 여럿 × 본뜰 줄 → 미리 보기(<see cref="TagEdit.PlanAdd"/>).</summary>
public sealed class AddVm : DialogVm
{
    readonly Site site;

    public override string Title => "태그 추가";
    public ObservableCollection<string> Profiles { get; } = [];
    public IReadOnlyList<string> ProfileChoices { get; }
    public IReadOnlyList<TemplateChoice> Templates { get; }

    public ICommand RemoveProfile { get; }
    public ICommand Ok { get; }

    public AddVm(Site site, IReadOnlyList<TagRowVm> selected)
    {
        this.site = site;
        ProfileChoices = ["ANY_PROFILE", .. site.Profiles.Select(p => p.Name)];
        Templates = [.. selected.Where(r => r.Rule != null).Select(r => new TemplateChoice($"{r.OrdinalText}줄 · 태그 {r.TagText} · {r.ProfileText}", r.Rule)),
            new TemplateChoice("빈 줄", null)];
        template = Templates[0];
        foreach (var p in selected.Where(r => r.Rule != null).Select(r => r.Rule!.Profile).Distinct()) Profiles.Add(p);
        Profiles.CollectionChanged += (_, _) => Replan();
        RemoveProfile = new Command(p => { if (p is string s) Profiles.Remove(s); });
        Ok = new Command(() => { if (LineCount > 0) Close("ok"); }, () => LineCount > 0);
        Replan();
    }

    string tags = "";
    public string Tags { get => tags; set { if (Set(ref tags, value)) Replan(); } }

    TemplateChoice template;
    public TemplateChoice Template { get => template; set { if (value != null && Set(ref template, value)) Replan(); } }

    bool range;
    public bool PerLine { get => !range; set { if (value) Range = false; } }
    public bool Range { get => range; set { if (Set(ref range, value)) { Raise(nameof(PerLine)); Replan(); } } }

    public TagRunMode Mode => range ? TagRunMode.Range : TagRunMode.PerLine;

    /// <summary>프로파일 칩 더하기(드롭다운에서 고름).</summary>
    public string? AddPick
    {
        get => null;
        set { if (value != null && !Profiles.Contains(value)) Profiles.Add(value); Raise(); }
    }

    string? error;
    /// <summary>번호 글 틀림(상자 빨간 테 + 짧은 낱말). 빈 글은 오류로 보이지 않는다.</summary>
    public string? Error { get => error; private set { if (Set(ref error, value)) Raise(nameof(HasError)); } }
    public bool HasError => Error != null;

    IReadOnlyList<PreviewRow> preview = [];
    public IReadOnlyList<PreviewRow> Preview { get => preview; private set => Set(ref preview, value); }
    public IReadOnlyList<string> Columns => [.. Profiles.Select(p => p == "ANY_PROFILE" ? TagVm.AllProfiles : p)];

    public int NewCount { get; private set; }
    public int HasCount { get; private set; }
    public int LineCount { get; private set; }
    public string OkText => $"{LineCount}줄 추가";

    void Replan()
    {
        TagAddPlan? plan = null;
        if (Tags.Trim().Length > 0 && Profiles.Count > 0) plan = TagEdit.PlanAdd(site, Tags, Profiles.ToList(), Template?.Rule, Mode);
        Error = Tags.Trim().Length == 0 ? null : plan?.Error ?? (TagEdit.ParseTags(Tags, out var e) == null ? e : null);
        var rows = plan is { Error: null } ? plan.Rows : [];
        Preview = rows.GroupBy(r => r.Tag).Select(g => new PreviewRow(g.Key, Profiles.Select(p => g.FirstOrDefault(x => x.Profile == p)?.Exists ?? false).ToList())).ToList();
        NewCount = rows.Count(r => !r.Exists);
        HasCount = rows.Count(r => r.Exists);
        LineCount = plan is { Error: null } ? plan.Lines.Count : 0;
        foreach (var n in new[] { nameof(Columns), nameof(NewCount), nameof(HasCount), nameof(LineCount), nameof(OkText) }) Raise(n);
        CommandManager.InvalidateRequerySuggested();
    }

    public List<string> ProfileList => Profiles.ToList();
}

// ---- 조건 ----

/// <summary>
/// 상태 조건 한 줄: 종류 드롭다운 + 반전 토글 + 칸들. 새 코어(v0.58.0 이상)는 상태 낱말(<c>FORWARD</c> …), 옛 코어는 확장 조건(<c>X_FORWARD</c> …).
/// 반전을 켜면 <c>NOT( … )</c>(옛 형식·옛 전용 조건은 <c>X_NOT( … )</c>)로 감싸 쓴다. 코어가 <c>NOT</c> 을 못 붙이는 종류는 없다(헤더·D-4 확인):
/// 못 누르는 것은 겹친 <c>NOT( NOT( … ) )</c> 처럼 풀 수 없어 원문 그대로 둔 줄뿐이다.
/// </summary>
public sealed class XRowVm : Observable
{
    public static readonly IReadOnlyList<GrammarEntry> Kinds = GrammarTable.Rows.Where(r => r.Place == Place.ExtCond).ToList();

    public static readonly IReadOnlyList<GrammarEntry> StateKinds = GrammarTable.Rows.Where(r => r.Place == Place.StateCond).ToList();

    public static bool IsNot(string name) => name is "NOT" or "X_NOT";

    readonly NameDict names;

    /// <summary>이 줄이 고를 수 있는 종류(드롭다운).</summary>
    public IReadOnlyList<GrammarEntry> Items { get; }

    GrammarEntry kind;
    public GrammarEntry Kind
    {
        get => kind;
        set
        {
            if (value == null || !Set(ref kind, value)) return;
            Args = value.Args.Select(a => new ArgBox(a, ArgBox.Default(a, names), names)).ToList();
            Raise(nameof(Args));
            if (!CanNegate) Negate = false;
            Raise(nameof(CanNegate));
        }
    }

    public IReadOnlyList<ArgBox> Args { get; private set; }

    bool negate;
    public bool Negate { get => negate; set => Set(ref negate, value && CanNegate); }

    /// <summary>겹친 NOT 처럼 원문 그대로 둔 줄은 반전을 못 바꾼다.</summary>
    public bool CanNegate => !IsNot(kind.Name);

    /// <summary>칸이 모두 맞다(숫자 칸은 범위 안). 아니면 [확인] 을 막는다.</summary>
    public bool Valid => Args.All(a => !a.Invalid);

    public XRowVm(GrammarEntry kind, IReadOnlyList<string> args, IReadOnlyList<GrammarEntry>? items = null, NameDict? names = null, bool negate = false)
    {
        this.names = names ?? new NameDict();
        Items = items ?? Kinds;
        this.kind = kind;
        Args = kind.Args.Select((a, i) => new ArgBox(a, i < args.Count ? args[i] : ArgBox.Default(a, this.names), this.names)).ToList();
        this.negate = negate && CanNegate;
    }

    public XCond ToCond()
    {
        var x = new XCond(Kind.Name, Args.Select(a => a.Text.Trim()).ToList());
        return Negate ? new XCond(StateWords.IsWord(x.Name) ? "NOT" : "X_NOT", [x.Text]) : x;
    }
}

/// <summary>칸 하나: 종류에 맞는 편집기(고를 수 있으면 드롭다운, 숫자는 범위 칸, 그 밖은 글).</summary>
public sealed class ArgBox : Observable
{
    public string Label { get; }
    public IReadOnlyList<string>? Options { get; }
    public bool IsList => Options != null;
    public bool IsBox => Options == null;
    public bool IsNumber { get; }
    public long Min { get; }
    public long Max { get; }
    public string Range => IsNumber ? $"{Min}~{Max}" : "";

    string t;
    public string Text
    {
        get => t;
        set { if (Set(ref t, value)) Raise(nameof(Invalid)); }
    }

    public bool Invalid => IsNumber && !(long.TryParse(t.Trim(), out var v) && v >= Min && v <= Max);

    public ArgBox(ArgSpec spec, string text, NameDict names)
    {
        Label = spec.Label;
        t = text;
        switch (Choices.EditorOf(spec))
        {
            case FieldEditor.Words:
                Options = spec.Words!.Contains(text) ? spec.Words : [text, .. spec.Words!];
                break;
            case FieldEditor.Names when Choices.For(spec, names).Select(c => c.Name).ToList() is { Count: > 0 } list:
                Options = list.Contains(text) ? list : [text, .. list];
                break;
            case FieldEditor.Number when long.TryParse(text.Trim(), out _): // 이름 상수가 든 칸은 글 상자로 둔다(막지 않는다)
                IsNumber = true;
                (Min, Max) = (spec.Min, Math.Max(spec.Min, spec.Max));
                break;
        }
    }

    /// <summary>새 칸의 기본 글: 목록 첫 값, 숫자는 범위 최소.</summary>
    public static string Default(ArgSpec spec, NameDict names) => Choices.EditorOf(spec) switch
    {
        FieldEditor.Words => spec.Words![0],
        FieldEditor.Names => Choices.For(spec, names).FirstOrDefault()?.Name ?? "0",
        FieldEditor.Number => spec.Min.ToString(),
        _ => "0",
    };
}

/// <summary>입력 묶음: 켜짐 / 꺼짐 / 하나라도 + 신호 여러 개.</summary>
public sealed class GroupVm : Observable
{
    public static readonly IReadOnlyList<(string Name, string Text)> KindList = [("ON", "켜짐"), ("OFF", "꺼짐"), ("ANY_ON", "하나라도")];
    public IReadOnlyList<string> KindTexts => KindList.Select(k => k.Text).ToList();

    string kind;
    public string KindText { get => KindList.First(k => k.Name == kind).Text; set => Set(ref kind, KindList.First(k => k.Text == value).Name); }
    public string KindName => kind;

    public IReadOnlyList<SignalVm> Signals { get; private set; } = [];

    public GroupVm(string kind, IReadOnlyList<Choice> inputs, IEnumerable<string> on)
    {
        this.kind = kind;
        SetChoices(inputs, on.ToHashSet());
    }

    /// <summary>목록을 다시 만든다(걸러 보기 토글). 켜 둔 신호는 그대로 둔다.</summary>
    public void SetChoices(IReadOnlyList<Choice> inputs, ISet<string>? on = null)
    {
        var set = on ?? Signals.Where(s => s.On).Select(s => s.Name).ToHashSet();
        Signals = inputs.Select(c => new SignalVm(c.Name, c.Note, set.Contains(c.Name))).ToList();
        Raise(nameof(Signals));
    }

    public bool IsOn(string name) => Signals.Any(s => s.On && s.Name == name);

    public string? Text
    {
        get
        {
            var picked = Signals.Where(s => s.On).Select(s => s.Name).ToList();
            return picked.Count == 0 ? null : $"{kind}({string.Join(", ", picked)})";
        }
    }
}

public sealed class SignalVm(string name, string? note, bool on) : Observable
{
    public string Name { get; } = name;
    public string? Note { get; } = note;
    bool o = on;
    public bool On { get => o; set => Set(ref o, value); }
}

/// <summary>
/// 입력 조건 한 칸(묶음 여러 개: 켜짐 / 꺼짐 / 하나라도 + 신호). 읽을 수 없는 글은 글 상자로 둔다.
/// 조건 팝업·적재 조건 팝업·수동 조건 팝업이 같이 쓴다. wrap = 묶음 하나여도 <c>IN( … )</c> 로 쓴다.
/// </summary>
public sealed class InputVm : Observable
{
    IReadOnlyList<Choice> inputs;
    readonly bool wrap;
    readonly NameDict names;
    readonly SignalScope? scope;
    readonly string? raw;

    public string Label { get; }
    public ObservableCollection<GroupVm> Groups { get; } = [];

    /// <summary>묶음으로 못 푼 입력 조건 글(있으면 이 글을 그대로 쓴다).</summary>
    public string? RawInput { get; set; }
    public bool HasRawInput => RawInput != null;

    public ICommand AddGroup { get; }
    public ICommand RemoveGroup { get; }

    public InputVm(string label, Arg? arg, NameDict names, bool wrap, SignalScope? scope = null) : this(label, arg?.Node, arg?.Text, names, wrap, scope) { }

    public InputVm(string label, Node? node, string? raw, NameDict names, bool wrap, SignalScope? scope = null)
    {
        Label = label;
        this.wrap = wrap;
        this.names = names;
        this.scope = scope;
        this.raw = raw;
        inputs = Build();
        if (node != null && !ReadGroups(node)) RawInput = raw;
        AddGroup = new Command(() => Groups.Add(new GroupVm("ON", inputs, [])), () => RawInput == null);
        RemoveGroup = new Command(p => { if (p is GroupVm g) Groups.Remove(g); });
    }

    /// <summary>
    /// 고를 입력: 조건에는 논리 입력 ID 32 이상(IN_AMP_*)·IO_NONE 을 못 쓴다(이미 쓴 것만 남긴다).
    /// 그 줄 프로파일에 맞는 신호 + 지정 없는 신호만(이미 쓴 것은 남긴다).
    /// </summary>
    IReadOnlyList<Choice> Build() =>
        Choices.For(new ArgSpec(ArgKind.Input), names, scope, n => raw?.Contains(n) == true || Groups.Any(g => g.IsOn(n)))
            .Where(c => !(long.TryParse(c.Note, out var id) && id >= 32) || raw?.Contains(c.Name) == true).ToList();

    /// <summary>"전체" 토글(끄면 걸러 보기, 켜면 전부). 가려질 신호가 없으면 토글을 보이지 않는다.</summary>
    public bool CanToggle => scope?.CanToggle == true;
    public bool ShowAll
    {
        get => SignalScope.ShowAll;
        set
        {
            if (SignalScope.ShowAll == value) return;
            SignalScope.ShowAll = value;
            inputs = Build();
            foreach (var g in Groups) g.SetChoices(inputs);
            Raise();
        }
    }

    bool ReadGroups(Node n)
    {
        IEnumerable<string> Names(CallNode c) => c.Args.Select(a => a.Text.Trim());
        switch (n)
        {
            case CallNode { Name: "ON" or "OFF" or "ANY_ON" } g:
                Groups.Add(new GroupVm(g.Name, inputs, Names(g)));
                return true;
            case CallNode { Name: "IN" } all when all.Args.All(a => a.Node is CallNode { Name: "ON" or "OFF" or "ANY_ON" }):
                foreach (var a in all.Args) Groups.Add(new GroupVm(((CallNode)a.Node).Name, inputs, Names((CallNode)a.Node)));
                return true;
            case NameNode { Name: "NO_IN" }:
                return true;
            default:
                return false;
        }
    }

    /// <summary>입력 조건 글: 묶음 하나면 그 묶음(wrap 이면 <c>IN( … )</c>), 여럿이면 <c>IN( … )</c>, 없으면 null.</summary>
    public string? Text()
    {
        if (RawInput != null) return RawInput;
        var gs = Groups.Select(g => g.Text).OfType<string>().ToList();
        return gs.Count switch { 0 => null, 1 when !wrap => gs[0], _ => $"IN( {string.Join(", ", gs)} )" };
    }
}

/// <summary>조건 팝업: 확장 조건 0~2개 + 입력 조건(묶음 여러 개).</summary>
public sealed class CondVm : DialogVm
{
    public override string Title => "조건";
    public ObservableCollection<XRowVm> XRows { get; } = [];
    /// <summary>드롭다운 종류: 새 코어는 상태 낱말, 옛 코어는 확장 조건. 고치는 규칙에 옛 <c>X_…</c> 가 남아 있으면(옮길 수 없는 것) 그 종류도 든다.</summary>
    public IReadOnlyList<GrammarEntry> XKinds { get; }
    public InputVm In { get; }

    public ObservableCollection<GroupVm> Groups => In.Groups;
    public ICommand AddX { get; }
    public ICommand RemoveX { get; }
    public ICommand AddGroup => In.AddGroup;
    public ICommand Ok { get; }

    public CondVm(TagRule rule, NameDict names, SignalScope? scope = null)
    {
        var read = TagEdit.ReadCondition(rule);
        var xs = (read?.X ?? []).Select(x => names.NewDsl ? StateWords.ToState(x) ?? x : x)
            .Select(Unwrap).ToList(); // (조건, 반전) : NOT( x ) → x + 반전
        var kinds = (names.NewDsl ? XRowVm.StateKinds : XRowVm.Kinds).Where(k => !XRowVm.IsNot(k.Name)).ToList();
        // 새 코어에서 옮길 수 없는 옛 조건(X_STATION_RUNNING)은 그 종류를 남긴다: 안 그러면 줄이 사라진다
        XKinds = [.. kinds, .. xs.Where(x => kinds.All(k => k.Name != x.Cond.Name)).Select(x => XRowVm.Kinds.Concat(XRowVm.StateKinds).FirstOrDefault(k => k.Name == x.Cond.Name)).OfType<GrammarEntry>().Distinct()];
        foreach (var (x, neg) in xs)
            if (XKinds.FirstOrDefault(k => k.Name == x.Name && k.Args.Count == x.Args.Count) is { } k) XRows.Add(new XRowVm(k, x.Args, XKinds, names, neg));
        // 입력 조건 글은 읽는 쪽(ReadCondition)이 새 형식·옛 형식 모두 IN( … ) 하나나 묶음 하나로 돌려준다
        var text = read?.Input;
        var node = text != null ? Expr.Parse(text) : null;
        In = new InputVm("입력", node, text, names, wrap: false, scope);
        AddX = new Command(() => XRows.Add(new XRowVm(XKinds.First(k => k.Name == (names.NewDsl ? "FORWARD" : "X_FORWARD")), [], XKinds, names)), () => XRows.Count < 2);
        RemoveX = new Command(p => { if (p is XRowVm x) XRows.Remove(x); });
        Ok = new Command(() => Close("ok"), () => XRows.All(r => r.Valid));
    }

    /// <summary>NOT( x ) / X_NOT( x ) 를 x + 반전으로 푼다(안이 또 NOT 이거나 못 읽으면 그대로).</summary>
    static (XCond Cond, bool Negate) Unwrap(XCond x) =>
        XRowVm.IsNot(x.Name) && x.Args.Count == 1 && Expr.Parse(x.Args[0]) is { } n && XCond.FromNode(n) is { } inner && !XRowVm.IsNot(inner.Name) ? (inner, true) : (x, false);

    public IReadOnlyList<XCond> X => XRows.Select(r => r.ToCond()).ToList();

    public string? Input => In.Text();
}

/// <summary>적재 조건 팝업(<c>LOAD_IF( 이름, 조건 1[, 조건 2] )</c>): 조건 둘, 둘째는 없어도 된다.</summary>
public sealed class LoadVm : DialogVm
{
    public LoadVm(Statement st, NameDict names, SignalScope? scope = null)
    {
        Name = st.Args[0].Text;
        First = new InputVm("조건 1", st.Args.Count > 1 ? st.Args[1] : null, names, wrap: true, scope);
        Second = new InputVm("조건 2", st.Args.Count > 2 ? st.Args[2] : null, names, wrap: true, scope);
        Ok = new Command(() => Close("ok"), () => First.Groups.Any(g => g.Text != null) || First.RawInput != null);
    }

    public override string Title => "적재 조건";
    public string Name { get; }
    public InputVm First { get; }
    public InputVm Second { get; }
    public ICommand Ok { get; }
}

/// <summary>수동 가지 조건 팝업: 입력 / 키 / 없음.</summary>
public sealed class ManCondVm : DialogVm
{
    public static readonly IReadOnlyList<string> Keys = CoreConstants.Keys;

    public ManCondVm(Arg cond, NameDict names, SignalScope? scope = null)
    {
        var n = cond.Node;
        if (n is CallNode { Name: "KEY", Args.Count: 1 } k) { mode = 1; key = k.Args[0].Text.Trim(); }
        else if (n is NameNode { Name: "NO_IN" }) mode = 2;
        In = new InputVm("입력", mode == 0 ? cond : null, names, wrap: true, scope);
        Ok = new Command(() => Close("ok"));
    }

    public override string Title => "조건";
    public InputVm In { get; }
    public ICommand Ok { get; }

    int mode;
    public bool IsInput { get => mode == 0; set { if (value) SetMode(0); } }
    public bool IsKey { get => mode == 1; set { if (value) SetMode(1); } }
    public bool IsNone { get => mode == 2; set { if (value) SetMode(2); } }

    void SetMode(int m)
    {
        mode = m;
        Raise(nameof(IsInput));
        Raise(nameof(IsKey));
        Raise(nameof(IsNone));
    }

    string key = Keys[2];
    public string Key { get => key; set { if (value != null) Set(ref key, value); } }

    /// <summary>조건 칸 글: <c>IN( … )</c> / <c>KEY( 키 )</c> / <c>NO_IN</c>(입력 묶음이 없어도).</summary>
    public string Text => mode switch { 1 => $"KEY( {key} )", 2 => "NO_IN", _ => In.Text() ?? "NO_IN" };
}

/// <summary>IO 맵 신호가 쓰는 프로파일 고르기(토큰). 비면 모두.</summary>
public sealed class PfPickVm : DialogVm
{
    public PfPickVm(string title, IReadOnlyList<string> choices, IEnumerable<string> current)
    {
        this.title = title;
        ProfileChoices = choices;
        foreach (var p in current) Profiles.Add(p);
        Profiles.CollectionChanged += (_, _) => Raise(nameof(IsAll));
        RemoveProfile = new Command(p => { if (p is string s) Profiles.Remove(s); });
        Ok = new Command(() => Close("ok"));
    }

    readonly string title;
    public override string Title => title;
    public ObservableCollection<string> Profiles { get; } = [];
    public IReadOnlyList<string> ProfileChoices { get; }
    public ICommand RemoveProfile { get; }
    public ICommand Ok { get; }

    /// <summary>고른 것이 없다 = 모든 프로파일.</summary>
    public bool IsAll => Profiles.Count == 0;

    public string? AddPick
    {
        get => null;
        set { if (value != null && !Profiles.Contains(value)) Profiles.Add(value); Raise(); }
    }

    public List<string> List => Profiles.ToList();
}

/// <summary>이름 하나 받기(새 프로파일, 이름 바꾸기).</summary>
public sealed class NameVm(string title, string text, string ok) : DialogVm
{
    public override string Title => title;
    string t = text;
    public string Text { get => t; set => Set(ref t, value); }
    public string OkText => ok;
    public ICommand Ok => new Command(() => { if (Text.Trim().Length > 0) Close("ok"); });
}

/// <summary>원문 글자 고치기(고급 모드).</summary>
public sealed class RawVm(string text) : DialogVm
{
    public override string Title => "원문";
    string t = text;
    public string Text { get => t; set => Set(ref t, value); }
    public ICommand Ok => new Command(() => Close("ok"));
}

// ---- 시퀀스 스텝·표 ----

/// <summary>명령 목록 항목: 머리 이름 + 대표 문법 줄(무리마다 하나) + 무리 전체(검색용). Enabled = 넣을 수 있는가(태그 칸 수).</summary>
public sealed record StepChoice(string Text, GrammarEntry Entry, bool Enabled = true, IReadOnlyList<GrammarEntry>? Family = null)
{
    public override string ToString() => Text;
}

/// <summary>
/// 스텝·동작 [추가]·[명령 바꾸기] 팝업: 분류 버튼 한 줄 → 명령(무리마다 하나, 형식은 칸에서 고른다) → 인자 설명·C 글자 미리 보기.
/// 검색어가 있으면 분류는 무시하고 이름표·매크로 이름·분류·설명에서 찾는다.
/// </summary>
public sealed class StepVm : DialogVm
{
    readonly IReadOnlyList<StepChoice> all;
    readonly Func<GrammarEntry, string> preview;

    readonly Func<GrammarEntry, IReadOnlyList<string>> drops;

    /// <summary>시퀀스 스텝용. drops = 그 명령으로 바꾸면 버려지는 인자, comment = 바꿀 줄의 꼬리 주석(있으면 "주석 확인").</summary>
    public StepVm(string title, string okText, bool act, GrammarEntry? current, Func<GrammarEntry, string> preview,
        Func<GrammarEntry, IReadOnlyList<string>>? drops = null, string? comment = null, bool newDsl = false)
        : this(title, okText, Group(SeqEdit.StepRows(act, newDsl), null), GrammarTable.StepPalette, current, preview, drops, comment) { }

    public StepVm(string title, string okText, IReadOnlyList<StepChoice> all, IReadOnlyList<string> palette, GrammarEntry? current,
        Func<GrammarEntry, string> preview, Func<GrammarEntry, IReadOnlyList<string>>? drops = null, string? comment = null)
    {
        Title = title;
        OkText = okText;
        this.preview = preview;
        this.drops = drops ?? (_ => []);
        Comment = string.IsNullOrWhiteSpace(comment) ? null : comment;
        this.all = all;
        Categories = [.. palette.Append(Other).Distinct().Where(c => all.Any(x => Cat(x.Entry) == c))];
        category = current != null && Categories.Contains(Cat(current)) ? Cat(current) : Categories[0];
        Commands = Of(category);
        command = Commands.FirstOrDefault(c => c.Family!.Contains(current!)) ?? Commands[0];
        Ok = new Command(() => { if (command is { Enabled: true }) Close("ok"); });
    }

    /// <summary>문법 줄들 → 명령 목록: 무리(<see cref="GrammarEntry.Family"/>)마다 하나. 대표 = enabled 인 첫 줄, 없으면 첫 줄.</summary>
    public static IReadOnlyList<StepChoice> Group(IEnumerable<GrammarEntry> rows, Func<GrammarEntry, bool>? enabled)
    {
        return rows.GroupBy(r => r.Family).Select(g =>
        {
            var list = g.ToList();
            var rep = enabled == null ? list[0] : list.FirstOrDefault(enabled) ?? list[0];
            return new StepChoice(rep.Head, rep, enabled?.Invoke(rep) ?? true, list);
        }).ToList();
    }

    const string Other = "기타";
    static string Cat(GrammarEntry e) => e.Palette.Length > 0 ? e.Palette : Other;
    IReadOnlyList<StepChoice> Of(string cat) => all.Where(x => Cat(x.Entry) == cat).ToList();

    /// <summary>검색: 무리 안 어느 줄이든 이름표·매크로 이름·분류·설명에 검색어가 들면 맞다.</summary>
    IReadOnlyList<StepChoice> Find(string q) =>
        all.Where(x => x.Family!.Any(e => new[] { e.Head, e.Name, Cat(e), e.Note }.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)))).ToList();

    public override string Title { get; }
    public string OkText { get; }
    public IReadOnlyList<string> Categories { get; }
    public ICommand Ok { get; }

    string category;
    public string Category
    {
        get => category;
        set
        {
            if (value == null || !Set(ref category, value)) return;
            if (!IsSearching) Show(Of(value));
        }
    }

    string search = "";
    public string Search
    {
        get => search;
        set
        {
            value = (value ?? "").Trim();
            if (!Set(ref search, value)) return;
            Raise(nameof(IsSearching));
            Show(IsSearching ? Find(value) : Of(category));
        }
    }

    public bool IsSearching => search.Length > 0;

    void Show(IReadOnlyList<StepChoice> list)
    {
        Commands = list;
        Raise(nameof(Commands));
        command = list.Contains(command!) ? command : list.FirstOrDefault();
        Raise(nameof(Command));
        RaiseCommand();
    }

    public IReadOnlyList<StepChoice> Commands { get; private set; }

    StepChoice? command;
    /// <summary>고른 명령. 검색 결과가 없으면 null(그때 [확인]은 안 닫힌다).</summary>
    public StepChoice Command { get => command!; set { if (value != null && Set(ref command, value)) RaiseCommand(); } }

    void RaiseCommand()
    {
        Raise(nameof(Preview));
        Raise(nameof(Drops));
        Raise(nameof(HasDrops));
        Raise(nameof(Note));
    }

    public string Preview => command == null ? "" : preview(command.Entry);

    /// <summary>고른 명령의 인자 설명(단위·범위·낱말).</summary>
    public string Note => command?.Entry.Note ?? "";

    /// <summary>버릴 인자(쉼표로). 없으면 빈 글자.</summary>
    public string Drops => command == null ? "" : string.Join(", ", drops(command.Entry));
    public bool HasDrops => Drops.Length > 0;

    /// <summary>바꿀 줄의 꼬리 주석(그대로 남는다).</summary>
    public string? Comment { get; }
    public bool HasComment => Comment != null;
}

/// <summary>[표 추가] 팝업: 이름 + 대응표 줄(프로파일, 시퀀스 ID).</summary>
public sealed class SeqTableVm : DialogVm
{
    public SeqTableVm(Site site)
    {
        Profiles = [TagVm.AllProfiles, .. site.Profiles.Select(p => p.Name)];
        Ids = site.Names.All.Where(d => d.Kind == NameKind.SeqId && !d.Weak).Select(d => d.Name).Distinct().ToList();
        id = Ids.FirstOrDefault() ?? "";
        Ok = new Command(() => { if (Name.Trim().Length > 0 && Id.Length > 0) Close("ok"); });
    }

    public override string Title => "표 추가";
    public IReadOnlyList<string> Profiles { get; }
    public IReadOnlyList<string> Ids { get; }
    public ICommand Ok { get; }

    string name = "";
    public string Name { get => name; set => Set(ref name, value ?? ""); }

    string profile = TagVm.AllProfiles;
    public string Profile { get => profile; set { if (value != null) Set(ref profile, value); } }

    string id;
    public string Id { get => id; set { if (value != null) Set(ref id, value); } }

    /// <summary>대응표에 쓸 프로파일 글(전체 = 0).</summary>
    public string ProfileArg => Profile == TagVm.AllProfiles ? "0" : Profile;
}
