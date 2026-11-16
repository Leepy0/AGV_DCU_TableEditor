using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TableEditor.App.ViewModels;

namespace TableEditor.App.Views;

/// <summary>태그 화면: 여러 줄 고르기, 끌어 옮기기, 동작 추가 메뉴, 칸 확정, 표 안 칸 편집(F2·Enter·두 번 누르기), Del.</summary>
public partial class TagView : UserControl
{
    TagVm? vm;
    bool pushing;

    public TagView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as TagVm);
    }

    void Attach(TagVm? next)
    {
        if (vm != null) { vm.SelectionPushed -= OnPushed; vm.CellRefocus -= OnRefocus; }
        vm = next;
        if (vm != null) { vm.SelectionPushed += OnPushed; vm.CellRefocus += OnRefocus; }
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

    /// <summary>Del: 편집기 밖에서만(상자 안의 Del 은 글자 지우기). F2·Enter: 고른 블록을 표 안에서 고친다.</summary>
    void OnRowsKey(object sender, KeyEventArgs e)
    {
        if (vm == null || Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem) return;
        if (e.Key == Key.Delete) { vm.DeleteKey(); e.Handled = true; }
        else if (e.Key is Key.F2 or Key.Enter && !vm.CellEditing) { vm.BeginCellEdit(); e.Handled = true; }
    }

    // ---- 표 안 칸 편집 ----

    static IEnumerable<T> Below<T>(DependencyObject o) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
        {
            var c = VisualTreeHelper.GetChild(o, i);
            if (c is T t) yield return t;
            foreach (var x in Below<T>(c)) yield return x;
        }
    }

    void OnRefocus(int index) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FocusCell(index));

    /// <summary>머리 줄의 칸 편집기 index 번째(넘으면 끝)에 포커스. 고칠 수 없는 칸이면 다음 칸.</summary>
    void FocusCell(int index)
    {
        if (vm?.Selected is not { } row || !vm.CellEditing) return;
        Rows.ScrollIntoView(row);
        Rows.UpdateLayout();
        if (Rows.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem item) return;
        var edits = Below<ScrollViewer>(item).Where(x => x.Name == "CellEd").SelectMany(Below<FieldEdit>).ToList();
        if (edits.Count == 0) return;
        index = Math.Clamp(index, 0, edits.Count - 1);
        foreach (var f in edits.Skip(index).Concat(edits.Take(index)))
            if (f.FocusEditor()) return;
    }

    /// <summary>칸 편집 키: Enter 확정·끝(틀린 값이면 남음), Esc 되돌림·끝, Tab 확정·다음 칸.</summary>
    void OnCellKey(object sender, KeyEventArgs e)
    {
        if (vm == null || (Keyboard.FocusedElement as FrameworkElement)?.DataContext is not FieldVm f) return; // 펼친 드롭다운 안은 그대로
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                f.Commit();
                if (f.Invalid) return;
                vm.EndCellEdit();
                FocusRows();
                break;
            case Key.Escape:
                e.Handled = true;
                foreach (var x in vm.Fields) x.Revert();
                vm.EndCellEdit();
                FocusRows();
                break;
            case Key.Tab:
                e.Handled = true;
                f.Commit();
                if (f.Invalid) return;
                int next = f.Index + (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FocusCell(next));
                break;
        }
    }

    /// <summary>칸 편집 가로 스크롤은 휠을 먹지 않는다: 줄 목록으로 넘긴다.</summary>
    void OnCellWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var up = (UIElement)VisualTreeHelper.GetParent((DependencyObject)sender);
        up.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender });
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

    void OnNoteLost(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TagRowVm r) vm?.CommitComment.Execute(r);
    }

    // ---- 동작 추가 메뉴 ----

    void OnPlus(object sender, RoutedEventArgs e)
    {
        if (vm == null || ((FrameworkElement)sender).DataContext is not TagRowVm row) return;
        _ = vm.AddActionAsync(row);
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
