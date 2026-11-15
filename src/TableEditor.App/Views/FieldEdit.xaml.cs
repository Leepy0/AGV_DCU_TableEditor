using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TableEditor.App.ViewModels;

namespace TableEditor.App.Views;

/// <summary>칸 편집기 하나. 포커스를 받은 칸을 강조하고, 포커스를 잃으면 확정한다.</summary>
public partial class FieldEdit : UserControl
{
    public FieldEdit() => InitializeComponent();

    void OnFocus(object sender, KeyboardFocusChangedEventArgs e) => (DataContext as FieldVm)?.Focus();

    void OnLost(object sender, KeyboardFocusChangedEventArgs e) => (DataContext as FieldVm)?.Commit();

    /// <summary>고칠 수 있는 칸이면 키보드 포커스를 준다(글 상자는 글 전체 고르기).</summary>
    public bool FocusEditor()
    {
        if (List.IsVisible) return List.Focus();
        if (!Box.IsVisible) return false;
        Box.SelectAll();
        return Box.Focus();
    }
}
