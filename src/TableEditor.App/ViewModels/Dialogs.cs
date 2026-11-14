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

/// <summary>확장 조건 한 줄: 종류 드롭다운 + 칸들.</summary>
public sealed class XRowVm : Observable
{
    public static readonly IReadOnlyList<GrammarEntry> Kinds = GrammarTable.Rows.Where(r => r.Place == Place.ExtCond).ToList();

    GrammarEntry kind;
    public GrammarEntry Kind
    {
        get => kind;
        set
        {
            if (value == null || !Set(ref kind, value)) return;
            Args = value.Args.Select(a => new ArgBox(a.Label, a.Kind == ArgKind.Word ? a.Words![0] : "0")).ToList();
            Raise(nameof(Args));
        }
    }

    public IReadOnlyList<ArgBox> Args { get; private set; }

    public XRowVm(GrammarEntry kind, IReadOnlyList<string> args)
    {
        this.kind = kind;
        Args = kind.Args.Select((a, i) => new ArgBox(a.Label, i < args.Count ? args[i] : "0")).ToList();
    }

    public XCond ToCond() => new(Kind.Name, Args.Select(a => a.Text.Trim()).ToList());
}

/// <summary>칸 하나(라벨 + 글).</summary>
public sealed class ArgBox(string label, string text) : Observable
{
    public string Label { get; } = label;
    string t = text;
    public string Text { get => t; set => Set(ref t, value); }
}

/// <summary>입력 묶음: 켜짐 / 꺼짐 / 하나라도 + 신호 여러 개.</summary>
public sealed class GroupVm : Observable
{
    public static readonly IReadOnlyList<(string Name, string Text)> KindList = [("ON", "켜짐"), ("OFF", "꺼짐"), ("ANY_ON", "하나라도")];
    public IReadOnlyList<string> KindTexts => KindList.Select(k => k.Text).ToList();

    string kind;
    public string KindText { get => KindList.First(k => k.Name == kind).Text; set => Set(ref kind, KindList.First(k => k.Text == value).Name); }
    public string KindName => kind;

    public IReadOnlyList<SignalVm> Signals { get; }

    public GroupVm(string kind, IReadOnlyList<Choice> inputs, IEnumerable<string> on)
    {
        this.kind = kind;
        var set = on.ToHashSet();
        Signals = inputs.Select(c => new SignalVm(c.Name, c.Note, set.Contains(c.Name))).ToList();
    }

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

/// <summary>조건 팝업: 확장 조건 0~2개 + 입력 조건(묶음 여러 개). 읽을 수 없는 입력 글은 글 상자로 둔다.</summary>
public sealed class CondVm : DialogVm
{
    readonly IReadOnlyList<Choice> inputs;

    public override string Title => "조건";
    public ObservableCollection<XRowVm> XRows { get; } = [];
    public ObservableCollection<GroupVm> Groups { get; } = [];
    public IReadOnlyList<GrammarEntry> XKinds => XRowVm.Kinds;

    /// <summary>묶음으로 못 푼 입력 조건 글(있으면 이 글을 그대로 쓴다).</summary>
    public string? RawInput { get; set; }
    public bool HasRawInput => RawInput != null;

    public ICommand AddX { get; }
    public ICommand RemoveX { get; }
    public ICommand AddGroup { get; }
    public ICommand RemoveGroup { get; }
    public ICommand Ok { get; }

    public CondVm(TagRule rule, NameDict names)
    {
        inputs = Choices.For(new ArgSpec(ArgKind.Input), names);
        var read = TagEdit.ReadCondition(rule);
        foreach (var x in read?.X ?? [])
            if (XRowVm.Kinds.FirstOrDefault(k => k.Name == x.Name && k.Args.Count == x.Args.Count) is { } k) XRows.Add(new XRowVm(k, x.Args));
        if (read?.Input is { } input && rule.Cond is { } c)
        {
            var node = (c.Node as CallNode)?.Args[^1].Node;
            if (!ReadGroups(node)) RawInput = input;
        }
        AddX = new Command(() => XRows.Add(new XRowVm(XRowVm.Kinds.First(k => k.Name == "X_FORWARD"), [])), () => XRows.Count < 2);
        RemoveX = new Command(p => { if (p is XRowVm x) XRows.Remove(x); });
        AddGroup = new Command(() => Groups.Add(new GroupVm("ON", inputs, [])), () => RawInput == null);
        RemoveGroup = new Command(p => { if (p is GroupVm g) Groups.Remove(g); });
        Ok = new Command(() => Close("ok"));
    }

    bool ReadGroups(Node? n)
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

    public IReadOnlyList<XCond> X => XRows.Select(r => r.ToCond()).ToList();

    /// <summary>입력 조건 글: 묶음 하나면 그 묶음, 여럿이면 <c>IN( … )</c>, 없으면 null.</summary>
    public string? Input
    {
        get
        {
            if (RawInput != null) return RawInput;
            var gs = Groups.Select(g => g.Text).OfType<string>().ToList();
            return gs.Count switch { 0 => null, 1 => gs[0], _ => $"IN( {string.Join(", ", gs)} )" };
        }
    }
}

/// <summary>원문 글자 고치기(고급 모드).</summary>
public sealed class RawVm(string text) : DialogVm
{
    public override string Title => "원문";
    string t = text;
    public string Text { get => t; set => Set(ref t, value); }
    public ICommand Ok => new Command(() => Close("ok"));
}
