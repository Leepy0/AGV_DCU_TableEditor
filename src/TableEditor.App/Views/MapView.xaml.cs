using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TableEditor.App.ViewModels;

namespace TableEditor.App.Views;

/// <summary>대응표 화면: 줄 끌기, Del.</summary>
public partial class MapView : UserControl
{
    readonly GripDrag<MapRowVm> drag;
    MapVm? vm;

    public MapView()
    {
        InitializeComponent();
        RowMenu.Attach(Rows, row => vm == null ? [] : [new("추가", vm.Add), new("삭제", vm.Delete), row == null ? null : new("표 열기", vm.GoTable, row)]);
        drag = new(this, (a, b) => vm?.MoveRow(a, b));
        DataContextChanged += (_, _) =>
        {
            if (vm != null) vm.ScrollTo -= Scroll;
            vm = DataContext as MapVm;
            if (vm != null) vm.ScrollTo += Scroll;
        };
    }

    void Scroll(object item) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Rows.ScrollIntoView(item));

    public void FocusRows() => ListFocus.Focus(Rows);

    void OnGripDown(object sender, MouseButtonEventArgs e) => drag.Down(sender, e);
    void OnGripMove(object sender, MouseEventArgs e) => drag.Move(sender, e);
    void OnRowDrop(object sender, DragEventArgs e) => drag.DropOn(sender, e);
    void OnListDrop(object sender, DragEventArgs e) => drag.DropEnd(sender, e);

    void OnRowsKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Keyboard.FocusedElement is not TextBox && vm?.Delete.CanExecute(null) == true)
        {
            vm.Delete.Execute(null);
            e.Handled = true;
        }
    }
}
