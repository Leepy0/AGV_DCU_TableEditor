using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TableEditor.App.Blocks;

namespace TableEditor.App.Views;

/// <summary>
/// 줄·블록 우클릭 메뉴(A15). 이미 있는 명령만 잇는다. 우클릭하면 그 줄을 먼저 고른다:
/// 여러 줄이 골라져 있고 그 안이면 선택을 그대로 둔다. 글 상자 안은 상자 자신의 메뉴.
/// </summary>
public static class RowMenu
{
    /// <summary>메뉴 항목: 글, 명령, 명령 인자.</summary>
    public sealed record Item(string Text, ICommand Command, object? Param = null);

    public static ContextMenu Build(IEnumerable<Item?> items)
    {
        var menu = new ContextMenu();
        foreach (var i in items.OfType<Item>())
            menu.Items.Add(new MenuItem { Header = i.Text, Command = i.Command, CommandParameter = i.Param });
        return menu;
    }

    /// <summary>우클릭한 자리 위로 올라가며 첫 상위 요소(T). 없으면 null.</summary>
    static T? Above<T>(DependencyObject? d) where T : DependencyObject
    {
        for (; d != null; d = Parent(d)) if (d is T t) return t;
        return null;
    }

    static DependencyObject? Parent(DependencyObject d) => d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    /// <summary>
    /// 선택 규칙: 줄 위면 그 줄을 고른다(여러 줄 선택 안이면 유지). 블록 위면 그 블록도 고른다. 고른 줄(줄 밖이면 null)을 돌려준다.
    /// </summary>
    public static object? Pick(ListBox list, DependencyObject? hit)
    {
        var item = Above<ListBoxItem>(hit);
        if (item?.DataContext is not { } row) return null;
        if (!(list.SelectedItems.Count > 1 && list.SelectedItems.Contains(row))) list.SelectedItem = row;
        for (var d = hit; d != null && !ReferenceEquals(d, item); d = Parent(d))
            if (d is FrameworkElement { DataContext: BlockVm { Pick: { } pick } }) { pick.Execute(null); break; }
        return row;
    }

    /// <summary>목록에 우클릭 메뉴를 단다. build 는 고른 줄을 받아 메뉴 항목을 만든다.</summary>
    public static void Attach(ListBox list, Func<object?, IEnumerable<Item?>> build)
    {
        list.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (Above<TextBox>(e.OriginalSource as DependencyObject) != null) return;
            list.ContextMenu = Build(build(Pick(list, e.OriginalSource as DependencyObject)));
        };
    }
}
