using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TableEditor.App.Blocks;
using TableEditor.App.ViewModels;

namespace TableEditor.App.Views;

/// <summary>
/// 블록 컨트롤(하나뿐). DataContext = <see cref="BlockVm"/>. 같은 줄 안에서 끌어 순서를 바꾼다.
/// 태그 줄에서 두 번 누르면: 시퀀스 이름 위면 시퀀스 화면으로, 아니면 표 안 칸 편집.
/// </summary>
public partial class BlockView : UserControl
{
    Point start;
    bool down;

    public BlockView()
    {
        InitializeComponent();
        AllowDrop = true;
        PreviewMouseLeftButtonDown += (_, e) => { start = e.GetPosition(this); down = true; if (e.ClickCount == 2) OnDouble(e); };
        PreviewMouseLeftButtonUp += (_, _) => down = false;
        MouseMove += OnMove;
        Drop += OnDrop;
    }

    void OnDouble(MouseButtonEventArgs e)
    {
        if (DataContext is BlockVm { Row: StepRowVm sr } && (e.OriginalSource as FrameworkElement)?.DataContext is Part { IsLabel: true } lp)
        {
            e.Handled = true;
            down = false;
            sr.Owner.GoLabel(lp.Value);
            return;
        }
        if (DataContext is not BlockVm { Row: TagRowVm row }) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Part { Seq: { } seq })
        {
            e.Handled = true;
            down = false;
            row.Owner.OpenSeq(row, seq);
            return;
        }
        e.Handled = true;
        down = false;
        row.Owner.BeginCellEdit((BlockVm)DataContext);
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
