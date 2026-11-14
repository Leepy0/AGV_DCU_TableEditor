using System.Windows;
using System.Windows.Input;

namespace TableEditor.App.Views;

/// <summary>
/// 손잡이 끌기(한 줄·한 카드): 손잡이에서 누르고 움직이면 끌기, 줄 위에 놓으면 drop(끄는 것, 놓은 자리의 것).
/// 줄 밖(목록 빈 곳)에 놓으면 놓은 자리 = null(끝으로). 화면마다 끄는 것의 종류(T)가 다르다.
/// </summary>
public sealed class GripDrag<T>(UIElement owner, Action<T, T?> drop) where T : class
{
    sealed record Payload(T Item);

    Point start;
    T? item;

    public void Down(object sender, MouseButtonEventArgs e)
    {
        start = e.GetPosition(owner);
        item = ((FrameworkElement)sender).DataContext as T;
    }

    public void Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || item == null) return;
        if (Math.Abs((e.GetPosition(owner) - start).Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var it = item;
        item = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(Payload), new Payload(it)), DragDropEffects.Move);
    }

    /// <summary>줄 위에 놓음: 그 줄 앞으로.</summary>
    public void DropOn(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(Payload)) is Payload p && ((FrameworkElement)sender).DataContext is T target)
        {
            e.Handled = true;
            drop(p.Item, target);
        }
    }

    /// <summary>빈 곳에 놓음: 끝으로.</summary>
    public void DropEnd(object sender, DragEventArgs e)
    {
        if (!e.Handled && e.Data.GetData(typeof(Payload)) is Payload p)
        {
            e.Handled = true;
            drop(p.Item, null);
        }
    }
}
