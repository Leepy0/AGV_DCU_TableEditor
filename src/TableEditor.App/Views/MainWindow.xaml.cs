using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using TableEditor.App.ViewModels;

namespace TableEditor.App.Views;

public partial class MainWindow : Window
{
    /// <summary>F6: 표 ↔ 검사 목록.</summary>
    public static readonly RoutedCommand SwitchPane = new();

    readonly MainVm vm;
    readonly Settings settings;
    readonly bool remember;

    public MainWindow(MainVm vm, Settings settings, bool remember)
    {
        this.vm = vm;
        this.settings = settings;
        this.remember = remember;
        InitializeComponent();
        DataContext = vm;
        CommandBindings.Add(new CommandBinding(SwitchPane, (_, _) => Switch()));
        vm.Went += _ => TagPane.FocusRows();
        if (remember) Restore();
    }

    void Switch()
    {
        if (TagPane.RowsHaveFocus || !TagPane.IsVisible)
        {
            if (!vm.CheckOpen) vm.CheckOpen = true;
            Dispatcher.BeginInvoke(() =>
            {
                if (Checks.SelectedIndex < 0 && Checks.Items.Count > 0) Checks.SelectedIndex = 0;
                if (Checks.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, Checks.SelectedIndex)) is IInputElement item) Keyboard.Focus(item);
                else Keyboard.Focus(Checks);
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
        else TagPane.FocusRows();
    }

    // ---- 창 크기·위치 ----

    void Restore()
    {
        if (settings.Width is double w && settings.Height is double h && settings.Left is double l && settings.Top is double t)
        {
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (screen.IntersectsWith(new Rect(l, t, w, h)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                (Left, Top, Width, Height) = (l, t, Math.Max(w, MinWidth), Math.Max(h, MinHeight));
            }
        }
        if (settings.Maximized) WindowState = WindowState.Maximized;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!remember) return;
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        (settings.Left, settings.Top, settings.Width, settings.Height) = (b.Left, b.Top, b.Width, b.Height);
        settings.Maximized = WindowState == WindowState.Maximized;
        settings.Save();
    }

    // ---- 빈 화면 ----

    async void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog(this) != true) return;
        var path = dlg.FolderName;
        bool repo = Directory.Exists(Path.Combine(path, "site"));
        await vm.TryOpen(() => repo ? vm.OpenRepoAsync(path) : vm.OpenSiteAsync(path));
        if (repo && !vm.IsEmpty) { settings.LastRepo = vm.Repo; settings.Save(); }
    }
}
