using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TableEditor.App.ViewModels;

namespace TableEditor.App.Views;

/// <summary>IO 맵 화면(읽기 전용). 검사 줄에서 오면 그 별칭 줄로 스크롤.</summary>
public partial class IoView : UserControl
{
    IoVm? vm;

    public IoView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (vm != null) vm.ScrollTo -= OnScroll;
            vm = DataContext as IoVm;
            if (vm != null) vm.ScrollTo += OnScroll;
        };
    }

    void OnScroll(object item) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        foreach (var list in new[] { In, Out })
            if (list.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement el) el.BringIntoView();
    });
}
