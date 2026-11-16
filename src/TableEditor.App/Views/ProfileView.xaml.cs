using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TableEditor.App.ViewModels;

namespace TableEditor.App.Views;

/// <summary>프로파일 화면: 줄 끌기, 숫자 칸 확정, Del.</summary>
public partial class ProfileView : UserControl
{
    readonly GripDrag<ProfileRowVm> drag;
    ProfileVm? vm;

    public ProfileView()
    {
        InitializeComponent();
        RowMenu.Attach(Rows, _ => vm == null ? [] : [new("추가", vm.Add), new("삭제", vm.Delete), new("이름 바꾸기", vm.Rename), new("프로파일 삭제", vm.DeleteProfile), new("적재", vm.EditLoad)]);
        drag = new(this, (a, b) => vm?.MoveRow(a, b));
        DataContextChanged += (_, _) =>
        {
            if (vm != null) vm.ScrollTo -= Scroll;
            vm = DataContext as ProfileVm;
            if (vm != null) vm.ScrollTo += Scroll;
        };
    }

    void Scroll(object item) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Rows.ScrollIntoView(item));

    public void FocusRows() => ListFocus.Focus(Rows);

    void OnGripDown(object sender, MouseButtonEventArgs e) => drag.Down(sender, e);
    void OnGripMove(object sender, MouseEventArgs e) => drag.Move(sender, e);
    void OnRowDrop(object sender, DragEventArgs e) => drag.DropOn(sender, e);
    void OnListDrop(object sender, DragEventArgs e) => drag.DropEnd(sender, e);

    void OnNumLost(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ProfileRowVm r) vm?.CommitNumbers(r);
    }

    void OnRowsKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Keyboard.FocusedElement is not TextBox && vm?.Delete.CanExecute(null) == true)
        {
            vm.Delete.Execute(null);
            e.Handled = true;
        }
    }
}

/// <summary>목록에 키보드 포커스: 고른 줄이 있으면 그 줄에(F6, 검사 줄 누르기 뒤).</summary>
public static class ListFocus
{
    public static void Focus(ListBox list)
    {
        if (list.SelectedItem is { } item) list.ScrollIntoView(item);
        list.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            var target = list.SelectedItem != null ? list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) as ListBoxItem : null;
            if (target != null) Keyboard.Focus(target);
            else Keyboard.Focus(list);
        });
    }
}
