using System.Windows.Controls;

namespace TableEditor.App.Views;

/// <summary>블록 컨트롤(하나뿐). DataContext = <see cref="Blocks.BlockVm"/>.</summary>
public partial class BlockView : UserControl
{
    public BlockView() => InitializeComponent();
}
