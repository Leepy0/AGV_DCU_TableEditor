using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TableEditor.App.Blocks;
using TableEditor.App.ViewModels;

namespace TableEditor.App.Views;

/// <summary>블록 컨트롤(하나뿐). DataContext = <see cref="BlockVm"/>. 같은 줄 안에서 끌어 순서를 바꾼다.</summary>
public partial class BlockView : UserControl
{
    Point start;
    bool down;

    public BlockView()
    {
        InitializeComponent();
        AllowDrop = true;
        PreviewMouseLeftButtonDown += (_, e) => { start = e.GetPosition(this); down = true; };
        PreviewMouseLeftButtonUp += (_, _) => down = false;
        MouseMove += OnMove;
        Drop += OnDrop;
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (!down || e.LeftButton != MouseButtonState.Pressed || DataContext is not BlockVm { Row: TagRowVm, Index: >= 0 } b) return;
        var d = e.GetPosition(this) - start;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance) return;
        down = false;
        DragDrop.DoDragDrop(this, new DataObject(typeof(BlockVm), b), DragDropEffects.Move);
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(BlockVm)) is BlockVm from && DataContext is BlockVm { Row: TagRowVm row } to)
        {
            e.Handled = true;
            row.Owner.MoveBlock(from, to);
        }
    }
}
