using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TableEditor.App.ViewModels;

namespace TableEditor.App.Views;

/// <summary>선택 줄 칸 + C 글자. 포커스를 받은 칸을 강조하고, 포커스를 잃으면 확정한다.</summary>
public partial class DetailView : UserControl
{
    public DetailView() => InitializeComponent();

    void OnFieldFocus(object sender, KeyboardFocusChangedEventArgs e) => (((FrameworkElement)sender).DataContext as FieldVm)?.Focus();

    void OnFieldLost(object sender, KeyboardFocusChangedEventArgs e) => (((FrameworkElement)sender).DataContext as FieldVm)?.Commit();
}
