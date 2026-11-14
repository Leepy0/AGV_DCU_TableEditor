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

/// <summary>프로파일 색 순번 → 테마의 지도 색(<c>Map.Cats</c> 의 띠 색 키, 순번 % 개수). -1 = 빈 칸.</summary>
public sealed class MapBrush : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int i || i < 0 || Application.Current.TryFindResource("Map.Cats") is not string[] { Length: > 0 } cats)
            return Application.Current.TryFindResource("Well");
        return Application.Current.TryFindResource(cats[i % cats.Length]);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>들여쓰기 단 수 → 왼쪽 여백(테마 <c>Indent</c> × 단).</summary>
public sealed class Indent : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new Thickness((value is int n ? n : 0) * (Application.Current.TryFindResource("Indent") as double? ?? 0), 0, 0, 0);

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
