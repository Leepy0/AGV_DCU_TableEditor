using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace TableEditor.App.Views;

/// <summary>태그 화면. 고른 줄을 보이게 스크롤한다.</summary>
public partial class TagView : UserControl
{
    public TagView() => InitializeComponent();

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Rows.SelectedItem is { } item) Rows.ScrollIntoView(item);
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

    public bool RowsHaveFocus => Rows.IsKeyboardFocusWithin || Look.IsKeyboardFocusWithin;
}
