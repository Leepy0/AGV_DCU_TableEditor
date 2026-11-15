using System.Windows;
using System.Windows.Controls;

namespace TableEditor.App.Views;

/// <summary>거르기 막대. Label = 번호 칸 앞 낱말(태그, 호기).</summary>
public partial class FilterBar : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(FilterBar));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public FilterBar() => InitializeComponent();
}
