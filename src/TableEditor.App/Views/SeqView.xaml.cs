using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TableEditor.App.ViewModels;

namespace TableEditor.App.Views;

/// <summary>시퀀스 화면: 고른 표·스텝으로 스크롤, 스텝 끌기, Del.</summary>
public partial class SeqView : UserControl
{
    readonly GripDrag<StepRowVm> drag;
    SeqVm? vm;

    public SeqView()
    {
        InitializeComponent();
        RowMenu.Attach(StepList, _ => vm == null ? [] : [new("추가", vm.AddStep), new("바꾸기", vm.ChangeStep), new("삭제", vm.DeleteStep)]);
        RowMenu.Attach(TreeList, _ => vm == null ? [] : [new("표 추가", vm.AddTable), new("표 삭제", vm.DeleteTable)]);
        drag = new(this, (a, b) => vm?.MoveStep(a, b));
        DataContextChanged += (_, _) =>
        {
            if (vm != null) vm.ScrollTo -= Scroll;
            vm = DataContext as SeqVm;
            if (vm != null) vm.ScrollTo += Scroll;
        };
    }

    void Scroll(object item) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        StepList.ScrollIntoView(item);
        if (TreeList.SelectedItem is { } n) TreeList.ScrollIntoView(n);
    });

    public void FocusRows() => ListFocus.Focus(StepList);

    void OnGripDown(object sender, MouseButtonEventArgs e) => drag.Down(sender, e);
    void OnGripMove(object sender, MouseEventArgs e) => drag.Move(sender, e);
    void OnRowDrop(object sender, DragEventArgs e) => drag.DropOn(sender, e);
    void OnListDrop(object sender, DragEventArgs e) => drag.DropEnd(sender, e);

    void OnStepsKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Keyboard.FocusedElement is not (TextBox or ComboBox or ComboBoxItem) && vm?.DeleteStep.CanExecute(null) == true)
        {
            vm.DeleteStep.Execute(null);
            e.Handled = true;
        }
    }
}
