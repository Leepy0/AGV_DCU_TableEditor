using System.Collections.ObjectModel;
using System.Windows.Input;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Ops;

namespace TableEditor.App.ViewModels;

/// <summary>도구 한 줄: 이름, 찾음 여부, 경로·버전.</summary>
public sealed record ToolRow(string Name, bool Found, string Text);

/// <summary>설정 화면(사양 9.2절): 저장소 폴더, 앱 주소, 코어 주소, 도구 경로와 상태, 이름, 고급 모드. 바꾸면 바로 저장한다.</summary>
public sealed class SettingsVm : Observable
{
    readonly Settings s;
    readonly MainVm main;
    CancellationTokenSource? probe;

    public SettingsVm(Settings s, MainVm main)
    {
        this.s = s;
        this.main = main;
        PickRepo = new Command(() => main.PickRepoRequested?.Invoke());
        Probe = new Command(() => _ = ProbeAsync());
    }

    void Save([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        s.Save();
        Raise(name);
    }

    public string RepoFolder => s.LastRepo ?? "";
    public void RepoChanged() => Raise(nameof(RepoFolder));

    /// <summary>설정 값이 바깥에서 바뀌었다: 칸 모두 다시 읽기.</summary>
    public void Reload() => Raise(string.Empty);

    public string AppUrl { get => s.AppUrl; set { s.AppUrl = value.Trim(); Save(); } }
    public string CoreUrl { get => s.CoreUrl; set { s.CoreUrl = value.Trim().Length == 0 ? Settings.CoreUrlDefault : value.Trim(); Save(); } }
    public string GccPath { get => s.GccPath ?? ToolProbe.DefaultGcc(); set { s.GccPath = value.Trim().Length == 0 ? null : value.Trim(); Save(); _ = ProbeAsync(); } }
    public string Cl2000Path { get => s.Cl2000Path ?? ToolProbe.Cl2000Default; set { s.Cl2000Path = value.Trim().Length == 0 ? null : value.Trim(); Save(); _ = ProbeAsync(); } }
    public string UserName { get => s.UserName; set { s.UserName = value.Trim(); Save(); } }
    public bool Advanced { get => s.Advanced; set { s.Advanced = value; Save(); CommandManager.InvalidateRequerySuggested(); } }

    public ObservableCollection<ToolRow> Tools { get; } = [];

    bool probing;
    public bool IsProbing { get => probing; private set => Set(ref probing, value); }

    public ICommand PickRepo { get; }
    public ICommand Probe { get; }

    /// <summary>도구 확인(사양 7.1절). 화면을 막지 않는다. 없으면 빨간 표시.</summary>
    public async Task ProbeAsync()
    {
        probe?.Cancel();
        var cts = probe = new CancellationTokenSource();
        IsProbing = true;
        try
        {
            var list = await ToolProbe.ProbeAllAsync(s.GccPath, s.Cl2000Path ?? ToolProbe.Cl2000Default, cts.Token);
            if (cts.IsCancellationRequested) return;
            Tools.Clear();
            foreach (var t in list) Tools.Add(new ToolRow(t.Name, t.Found, t.Found ? $"{t.Version} · {t.Path}" : t.Detail ?? t.Path ?? ""));
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(probe, cts)) IsProbing = false; }
    }

    /// <summary>캡처용: 도구 줄을 직접 둔다(이 PC 경로가 그림에 들지 않게).</summary>
    public void ShowTools(IEnumerable<ToolRow> rows)
    {
        Tools.Clear();
        foreach (var r in rows) Tools.Add(r);
    }

    internal MainVm Main => main;

    /// <summary>프로그램 업데이트 칸(판·채널·커밋, 확인·받기·적용·되돌리기, 자동 확인).</summary>
    public UpdateVm Update => main.Update;
}
