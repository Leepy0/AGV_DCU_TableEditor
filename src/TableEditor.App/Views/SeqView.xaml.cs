using System.Windows.Controls;
using System.Windows.Threading;
using TableEditor.App.ViewModels;

namespace TableEditor.App.Views;

/// <summary>시퀀스 화면: 고른 표·스텝으로 스크롤.</summary>
public partial class SeqView : UserControl
{
    SeqVm? vm;

    public SeqView()
    {
        InitializeComponent();
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
}
