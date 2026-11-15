using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    bool closeConfirmed;
    bool updateDone;

    public MainWindow(MainVm vm, Settings settings, bool remember)
    {
        this.vm = vm;
        this.settings = settings;
        this.remember = remember;
        InitializeComponent();
        DataContext = vm;
        CommandBindings.Add(new CommandBinding(SwitchPane, (_, _) => Switch()));
        vm.Went += _ => FocusPane();
        vm.DialogShown += _ => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FocusDialog);
        vm.PickRepoRequested = () => _ = PickFolderAsync(repoOnly: true);
        vm.Update.CloseForUpdate += () => { closeConfirmed = updateDone = true; Close(); }; // 도우미를 띄웠다
        Activated += (_, _) => vm.CheckExternal(); // 바깥에서 바뀐 파일(사양 5.7절)
        if (remember) Restore();
    }

    /// <summary>위 줄 칩(변경·바깥 변경)을 누르면 작은 메뉴.</summary>
    void OnChipMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } b) return;
        menu.PlacementTarget = b;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void Switch()
    {
        if (vm.HasDialog) return;
        if (TagPane.RowsHaveFocus || !TagPane.IsVisible)
        {
            if (!vm.CheckOpen) vm.CheckOpen = true;
            Dispatcher.BeginInvoke(() =>
            {
                if (Checks.SelectedIndex < 0 && Checks.Items.Count > 0) Checks.SelectedIndex = 0;
                if (Checks.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, Checks.SelectedIndex)) is IInputElement item) Keyboard.Focus(item);
                else Keyboard.Focus(Checks);
            }, DispatcherPriority.Input);
        }
        else FocusPane();
    }

    /// <summary>지금 탭의 줄 목록에 포커스.</summary>
    void FocusPane()
    {
        if (vm.IsTagTab) TagPane.FocusRows();
        else if (vm.IsProfileTab) ProfilePane.FocusRows();
        else if (vm.IsSeqTab) SeqPane.FocusRows();
        else if (vm.IsMapTab) MapPane.FocusRows();
    }

    /// <summary>팝업 처음 포커스: 기본 단추(Tag = true), 없으면 첫 글 상자, 없으면 첫 단추.</summary>
    void FocusDialog()
    {
        IEnumerable<DependencyObject> All(DependencyObject d)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                yield return c;
                foreach (var x in All(c)) yield return x;
            }
        }
        var items = All(DialogHost).OfType<Control>().Where(c => c.Focusable && c.IsVisible && c.IsEnabled).ToList();
        var target = items.OfType<Button>().FirstOrDefault(b => b.Tag is true) ?? items.OfType<TextBox>().FirstOrDefault() ?? (Control?)items.FirstOrDefault();
        if (target != null) Keyboard.Focus(target);
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

    /// <summary>닫기: 저장 안 한 변경이 있으면 [저장][버리기][취소].</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!closeConfirmed && vm.Dirty)
        {
            e.Cancel = true;
            if (vm.HasDialog) return;
            Dispatcher.BeginInvoke(async () =>
            {
                if (!await vm.ConfirmLeaveAsync()) return;
                closeConfirmed = true;
                Close();
            });
            return;
        }
        // 받아 둔 버전: 바깥 도우미를 띄우고(이 프로세스가 끝나길 기다렸다가 교체) 닫는다. 막히면 팝업 뒤 그대로 닫는다
        if (!updateDone && vm.Update.HasPending)
        {
            e.Cancel = true;
            if (vm.HasDialog) return;
            Dispatcher.BeginInvoke(async () =>
            {
                await vm.Update.ApplyOnCloseAsync();
                updateDone = true;
                Close();
            });
            return;
        }
        vm.FlushDraft();
        if (!remember) return;
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        (settings.Left, settings.Top, settings.Width, settings.Height) = (b.Left, b.Top, b.Width, b.Height);
        settings.Maximized = WindowState == WindowState.Maximized;
        settings.Save();
    }

    // ---- 폴더 ----

    async void OnPickFolder(object sender, RoutedEventArgs e) => await PickFolderAsync(repoOnly: false);

    async Task PickFolderAsync(bool repoOnly)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog(this) != true) return;
        var path = dlg.FolderName;
        bool repo = Directory.Exists(Path.Combine(path, "site"));
        if (repoOnly && !repo) { vm.Notify("site 폴더 없음"); return; }
        if (!await vm.ConfirmLeaveAsync()) return;
        if (!App.ClaimFolder(path)) { await App.AlreadyOpenAsync(vm); return; }
        await vm.TryOpen(() => repo ? vm.OpenRepoAsync(path) : vm.OpenSiteAsync(path));
        if (repo && !vm.IsEmpty)
        {
            settings.LastRepo = vm.Repo;
            settings.Save();
            vm.Config.RepoChanged();
            await vm.StartupAsync();
        }
    }
}
