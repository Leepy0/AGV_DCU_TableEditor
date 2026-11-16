using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Ops;

namespace TableEditor.App.ViewModels;

public enum StageState { Wait, Run, Pass, Fail, Cancel }

/// <summary>빌드 단계 하나(검사 · 압축 · 개발 · 현장): 아이콘 + 낱말.</summary>
public sealed class StageVm(string name, int weight) : Observable
{
    public string Name { get; } = name;
    /// <summary>진행 막대에서 차지하는 비중(%). 처음 값, 실측 뒤 조정.</summary>
    public int Weight { get; } = weight;
    StageState state;
    public StageState State
    {
        get => state;
        set { if (Set(ref state, value)) Raise(nameof(Word)); }
    }
    public string Word => State switch
    {
        StageState.Run => "도는 중", StageState.Pass => "통과", StageState.Fail => "실패", StageState.Cancel => "취소", _ => "대기",
    };
}

/// <summary>
/// 빌드 결과 화면(사양 9.2절): 단계, 출력 줄(끝 따라가기는 화면이), [취소], 결과 경로 둘, [폴더 열기][회차 내기].
/// 회차 내기·코어 올리기도 이 화면에 진행을 보인다.
/// </summary>
public sealed class BuildVm : Observable
{
    const int MaxLines = 20000;
    readonly MainVm main;
    CancellationTokenSource? cts;

    public const int ToolsWeight = 5;
    public StageVm Check { get; } = new("검사", 5);
    public StageVm Pack { get; } = new("압축", 15);
    public StageVm Dev { get; } = new("개발", 35);
    public StageVm Field { get; } = new("현장", 40);
    public IReadOnlyList<StageVm> Stages { get; }
    public ObservableCollection<string> Lines { get; } = [];

    public ICommand Cancel { get; }
    public ICommand OpenFolder { get; }
    public ICommand Round { get; }
    public ICommand OpenReadme { get; }
    public ICommand Run { get; }

    public BuildVm(MainVm main)
    {
        this.main = main;
        Stages = [Check, Pack, Dev, Field];
        Cancel = new Command(() => cts?.Cancel(), () => Running);
        OpenFolder = new Command(() => { if (Folder is { } f) main.OpenPath(f); }, () => Folder != null);
        Round = new Command(() => _ = main.RoundAsync(), () => !Running && main.CanGit);
        OpenReadme = new Command(() => { if (ReadmePath is { } p) main.OpenPath(p); }, () => ReadmePath != null);
        Run = new Command(() => _ = main.BuildAsync(), () => !Running && main.CanGit);
    }

    string title = "빌드";
    public string Title { get => title; private set => Set(ref title, value); }

    bool running, started;
    public bool Running { get => running; private set { if (Set(ref running, value)) CommandManager.InvalidateRequerySuggested(); } }
    /// <summary>한 번이라도 돌았다(빈 화면 아님).</summary>
    public bool Started { get => started; private set => Set(ref started, value); }

    bool toolsDone = true;
    /// <summary>진행 막대 0~100: 도구 확인 + 끝난 단계 비중(컴파일 줄 단위는 안 본다).</summary>
    public int Progress => (toolsDone ? ToolsWeight : 0) + Stages.Where(s => s.State == StageState.Pass).Sum(s => s.Weight);

    /// <summary>도구 확인 중(false) / 끝(true).</summary>
    public void ToolsChecked(bool done) { toolsDone = done; Raise(nameof(Progress)); }

    public CancellationToken Token => cts?.Token ?? CancellationToken.None;
    public bool Cancelled => cts?.IsCancellationRequested == true;

    /// <summary>출력 한 줄(어느 스레드에서나). Start 가 화면 스레드에서 만든 Progress 로 넘긴다.</summary>
    public Action<OutputLine> OnLine { get; private set; } = _ => { };

    string? devOut, fieldOut, readmeLine, resultLine;
    string? root;
    public string? DevOut { get => devOut; set { if (Set(ref devOut, value)) { Raise(nameof(DevText)); Raise(nameof(Folder)); } } }
    public string? FieldOut { get => fieldOut; set { if (Set(ref fieldOut, value)) { Raise(nameof(FieldText)); Raise(nameof(Folder)); } } }
    public string DevText => Show(DevOut);
    public string FieldText => Show(FieldOut);
    /// <summary>[폴더 열기]: 만든 .out 이 있는 폴더.</summary>
    public string? Folder => Path.GetDirectoryName(DevOut ?? FieldOut ?? "") is { Length: > 0 } d ? d : null;

    /// <summary>회차 내기 결과: README 회차 줄 머리. [README 열기]로 설명을 덧붙인다.</summary>
    public string? ReadmeLine { get => readmeLine; set { if (Set(ref readmeLine, value)) Raise(nameof(HasReadme)); } }
    string? readmePath;
    public string? ReadmePath { get => readmePath; set { readmePath = value; CommandManager.InvalidateRequerySuggested(); } }
    public bool HasReadme => ReadmeLine != null;

    /// <summary>코어 올리기 결과 한 줄(성공: <c>core = …</c>, 실패: 까닭).</summary>
    public string? ResultLine { get => resultLine; set { if (Set(ref resultLine, value)) Raise(nameof(HasResult)); } }
    public bool HasResult => ResultLine != null;
    string resultLabel = "코어";
    public string ResultLabel { get => resultLabel; set => Set(ref resultLabel, value); }

    /// <summary>경로는 저장소 기준으로 짧게(없으면 —).</summary>
    string Show(string? path)
    {
        if (path == null) return "—";
        if (root != null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return path[root.Length..].TrimStart('\\', '/');
        return path;
    }

    /// <summary>새로 돈다: 단계 대기, 출력·결과 비움. 화면 스레드에서 부른다.</summary>
    public void Start(string title, string? repo)
    {
        cts?.Dispose();
        cts = new CancellationTokenSource();
        root = repo == null ? null : Path.GetFullPath(repo).TrimEnd('\\', '/');
        Title = title;
        foreach (var s in Stages) s.State = StageState.Wait;
        Lines.Clear();
        DevOut = FieldOut = null;
        ReadmeLine = null;
        ReadmePath = null;
        ResultLine = null;
        toolsDone = true;
        Raise(nameof(Progress));
        Started = true;
        Running = true;
        var p = new Progress<OutputLine>(Add);
        OnLine = l => ((IProgress<OutputLine>)p).Report(l);
    }

    void Add(OutputLine l)
    {
        if (Lines.Count >= MaxLines) Lines.RemoveAt(0);
        Lines.Add(l.Text);
    }

    /// <summary>단계 시작: 그 단계 도는 중, 위 줄 낱말.</summary>
    public void Begin(StageVm s, string word)
    {
        s.State = StageState.Run;
        main.Word(word);
    }

    /// <summary>단계 끝. 실패면 취소였는지 본다.</summary>
    public void End(StageVm s, bool ok)
    {
        s.State = ok ? StageState.Pass : Cancelled ? StageState.Cancel : StageState.Fail;
        Raise(nameof(Progress));
    }

    /// <summary>도는 중인 단계를 실패(또는 취소)로 둔다.</summary>
    public void FailRunning()
    {
        foreach (var s in Stages.Where(s => s.State == StageState.Run)) End(s, false);
    }

    public void Finish()
    {
        FailRunning();
        Running = false;
        if (Stages.All(s => s.State == StageState.Pass)) { toolsDone = true; Raise(nameof(Progress)); }
    }
}
