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
        vm.DialogShown += _ => { focusBack.Push(Keyboard.FocusedElement); Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FocusDialog); };
        vm.DialogClosed += () => { var back = focusBack.Count > 0 ? focusBack.Pop() : null; Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RestoreFocus(back)); };
        vm.PickRepoRequested = () => _ = PickFolderAsync(repoOnly: true);
        vm.Update.CloseForUpdate += () => { closeConfirmed = updateDone = true; Close(); }; // 도우미를 띄웠다
        Activated += (_, _) => vm.CheckExternal(); // 바깥에서 바뀐 파일(사양 5.7절)
        if (remember) Restore();
    }

    /// <summary>
    /// Ctrl+Z·Ctrl+Y 를 글 상자보다 먼저 받는다(상자가 키를 먹어 창 단축키가 안 닿았다). 칸 편집 중이면 포커스를 풀어 칸을 확정한 뒤
    /// 표 되돌리기·다시하기 한 단계. 팝업·설정 화면의 글 상자는 상자 자신의 되돌리기에 맡긴다. 처리했으면 true.
    /// 칸 확정이 실패했거나(빨간 칸·막힘) 팝업 확인을 기다리면 그 키로는 되돌리지 않는다: 앞의 편집이 엉뚱하게 되돌려지지 않게.
    /// 확정으로 새 단계가 생겼으면 그 단계를 되돌린다.
    /// </summary>
    public bool HandleUndoKey(Key key, ModifierKeys mods)
    {
        if (mods != ModifierKeys.Control || key is not (Key.Z or Key.Y) || vm.HasDialog || vm.IsSettingsTab) return false;
        if (Keyboard.FocusedElement is TextBox or ComboBox)
        {
            int fails = vm.ApplyFailures;
            bool bad = (Keyboard.FocusedElement as FrameworkElement)?.DataContext is FieldVm { Invalid: true };
            Keyboard.ClearFocus(); // 확정은 칸이 포커스를 잃을 때
            if (bad || vm.HasDialog || vm.ApplyFailures != fails) return true;
        }
        var cmd = key == Key.Z ? vm.Undo : vm.Redo;
        if (cmd.CanExecute(null)) cmd.Execute(null);
        return true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (BlockedByDialog(e)) return;
        if (HandleUndoKey(e.Key, Keyboard.Modifiers)) e.Handled = true;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        BlockedByDialog(e);
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        base.OnPreviewTextInput(e);
        BlockedByDialog(e);
    }

    // ---- 팝업 아래로 키·포커스가 닿지 않게 ----
    // 큰 영역에 IsEnabled 를 걸면 UI 자동화(UIA) 이벤트가 트리 전체로 퍼져 팝업이 0.6~0.9초 걸린다. 그래서 입력 쪽에서 막는다:
    // 마우스는 오버레이가, 키·포커스는 여기서(팝업 밖 대상이면 취소).

    readonly Stack<IInputElement?> focusBack = new();

    /// <summary>이 대상이 팝업 안인가. 다른 창(드롭다운·메뉴 팝업)에 있는 것은 팝업이 연 것으로 본다(밖에서 연 메뉴는 포커스를 잃으며 닫힌다).</summary>
    public bool InDialog(DependencyObject? d)
    {
        if (d == null) return false;
        if (PresentationSource.FromDependencyObject(d) is { } src && !ReferenceEquals(src, PresentationSource.FromVisual(this))) return true;
        for (; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (ReferenceEquals(d, DialogHost)) return true;
        return false;
    }

    /// <summary>팝업이 떠 있고 입력 대상이 팝업 밖이면 막는다(Esc 는 팝업 취소). 막았으면 true.</summary>
    bool BlockedByDialog(RoutedEventArgs e)
    {
        if (!vm.HasDialog || InDialog(e.OriginalSource as DependencyObject)) return false;
        e.Handled = true;
        if (e is KeyEventArgs { Key: Key.Escape, IsDown: true }) vm.Dialog?.Cancel.Execute(null);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusDialogIfOut);
        return true;
    }

    protected override void OnPreviewGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnPreviewGotKeyboardFocus(e);
        if (!vm.HasDialog || e.NewFocus is not DependencyObject now || InDialog(now)) return;
        e.Handled = true;
        if (e.OldFocus is not DependencyObject old || !InDialog(old)) Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusDialogIfOut);
    }

    void FocusDialogIfOut()
    {
        if (vm.HasDialog && !InDialog(Keyboard.FocusedElement as DependencyObject)) FocusDialog();
    }

    /// <summary>팝업이 닫힌 뒤 원래 자리로. 그 자리가 사라졌으면 지금 탭의 줄 목록.</summary>
    void RestoreFocus(IInputElement? back)
    {
        if (vm.HasDialog) { FocusDialogIfOut(); if (back is DependencyObject b && InDialog(b) && back is UIElement { IsVisible: true } u) Keyboard.Focus(u); return; }
        if (back is UIElement { IsVisible: true, Focusable: true, IsEnabled: true } ue && Keyboard.Focus(ue) != null) return;
        FocusPane();
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
