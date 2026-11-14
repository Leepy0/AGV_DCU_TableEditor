using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace TableEditor.App.Mvvm;

/// <summary>알림 바탕 클래스. Set 은 값이 바뀔 때만 알린다.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>명령 클래스. CanRun 이 없으면 늘 된다.</summary>
public sealed class Command(Action<object?> run, Func<object?, bool>? canRun = null) : ICommand
{
    public Command(Action run, Func<bool>? canRun = null) : this(_ => run(), canRun == null ? null : _ => canRun()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canRun?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => run(parameter);

    /// <summary>아직 동작이 없는 버튼(모양만). 눌러도 아무 일 없다.</summary>
    public static readonly Command None = new(() => { });
}
