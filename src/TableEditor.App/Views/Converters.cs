using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TableEditor.App.Views;

/// <summary>분류 키 → 테마의 띠 색(<c>Cat.키</c>).</summary>
public sealed class CatBrush : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Application.Current.TryFindResource("Cat." + value) ?? Application.Current.TryFindResource("Cat.기타");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>bool → 보임. 매개변수 "not" 이면 거꾸로.</summary>
public sealed class Shown : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool v = value is true || value is string s && s.Length > 0 || value is int n && n > 0;
        if (parameter as string == "not") v = !v;
        return v ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>bool 뒤집기(양방향).</summary>
public sealed class Not : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
