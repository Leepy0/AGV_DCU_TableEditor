using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TableEditor.App.ViewModels;

namespace TableEditor.App.Views;

/// <summary>태그 화면: 여러 줄 고르기, 끌어 옮기기, 동작 추가 메뉴, 칸 확정, Del.</summary>
public partial class TagView : UserControl
{
    public static readonly RoutedCommand CommitField = new();
    public static readonly RoutedCommand RevertField = new();

    TagVm? vm;
    bool pushing;

    public TagView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as TagVm);
        CommandBindings.Add(new CommandBinding(CommitField, (_, e) => (e.Parameter as FieldVm)?.Commit()));
        CommandBindings.Add(new CommandBinding(RevertField, (_, e) => (e.Parameter as FieldVm)?.Revert()));
    }

    void Attach(TagVm? next)
    {
        if (vm != null) vm.SelectionPushed -= OnPushed;
        vm = next;
        if (vm != null) vm.SelectionPushed += OnPushed;
    }

    /// <summary>VM 이 고르기를 바꿨다(명령 뒤 새 줄 등): 목록에 옮기고 첫 줄로 스크롤.</summary>
    void OnPushed(IReadOnlyList<TagRowVm> list, TagRowVm? primary, bool scroll)
    {
        pushing = true;
        try
        {
            Rows.SelectedItems.Clear();
            if (primary != null) Rows.SelectedItems.Add(primary);
            foreach (var r in list) if (!ReferenceEquals(r, primary)) Rows.SelectedItems.Add(r);
        }
        finally { pushing = false; }
        if (primary != null && scroll) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Rows.ScrollIntoView(primary));
    }

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (pushing || vm == null || !ReferenceEquals(e.OriginalSource, Rows)) return; // 줄 안 드롭다운의 고르기는 올라와도 무시
        var primary = e.AddedItems.Count > 0 ? e.AddedItems[^1] as TagRowVm : Rows.SelectedItem as TagRowVm;
        vm.FromView(Rows.SelectedItems.Cast<TagRowVm>().ToList(), primary);
        if (primary != null) Rows.ScrollIntoView(primary);
    }

    /// <summary>Del: 글 상자 밖에서만(상자 안의 Del 은 글자 지우기).</summary>
    void OnRowsKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Keyboard.FocusedElement is not TextBox && vm != null)
        {
            vm.DeleteKey();
            e.Handled = true;
        }
    }

    /// <summary>줄 표에 키보드 포커스(F6). 고른 줄이 있으면 그 줄에.</summary>
    public void FocusRows()
    {
        var list = Rows.IsVisible ? Rows : Look;
        if (list.SelectedItem is { } item) list.ScrollIntoView(item);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            var target = list.SelectedItem != null ? list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) as ListBoxItem : null;
            if (target != null) Keyboard.Focus(target);
            else Keyboard.Focus(list);
        });
    }

    public bool RowsHaveFocus => IsKeyboardFocusWithin;

    // ---- 칸 ----

    void OnFieldFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is FieldVm f) vm?.Hot(f);
    }

    void OnFieldLost(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is FieldVm f) f.Commit();
    }

    void OnNoteLost(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TagRowVm r) vm?.CommitComment.Execute(r);
    }

    // ---- 동작 추가 메뉴 ----

    void OnPlus(object sender, RoutedEventArgs e)
    {
        if (vm == null || ((FrameworkElement)sender).DataContext is not TagRowVm row) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var group in vm.ActionChoices(row).GroupBy(c => c.Palette))
        {
            var head = new MenuItem { Header = group.Key, IsEnabled = group.Any(c => c.Enabled) };
            foreach (var c in group)
            {
                var item = new MenuItem { Header = c.Text, IsEnabled = c.Enabled };
                var entry = c.Entry;
                item.Click += (_, _) => vm.AddAction(row, entry);
                head.Items.Add(item);
            }
            menu.Items.Add(head);
        }
        menu.IsOpen = true;
    }

    // ---- 줄 끌기 ----

    Point gripStart;
    TagRowVm? gripRow;

    void OnGripDown(object sender, MouseButtonEventArgs e)
    {
        gripStart = e.GetPosition(this);
        gripRow = ((FrameworkElement)sender).DataContext as TagRowVm;
    }

    void OnGripMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || gripRow == null || vm == null) return;
        var d = e.GetPosition(this) - gripStart;
        if (Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var moving = vm.SelectedRows.Contains(gripRow) ? vm.SelectedRows.ToList() : [gripRow];
        gripRow = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(RowDrag), new RowDrag(moving)), DragDropEffects.Move);
    }

    sealed record RowDrag(IReadOnlyList<TagRowVm> Rows);

    void OnRowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(RowDrag)) is RowDrag d && ((FrameworkElement)sender).DataContext is TagRowVm target)
        {
            e.Handled = true;
            vm?.MoveRows(d.Rows, target);
        }
    }

    void OnListDrop(object sender, DragEventArgs e)
    {
        if (!e.Handled && e.Data.GetData(typeof(RowDrag)) is RowDrag d) vm?.MoveRows(d.Rows, null);
    }
}
