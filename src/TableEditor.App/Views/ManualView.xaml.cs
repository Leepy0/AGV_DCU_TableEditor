using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TableEditor.App.ViewModels;

namespace TableEditor.App.Views;

/// <summary>수동 화면: 가지 끌기(사슬 안), 카드 끌기(사슬 단위), 출력 추가 메뉴, Del.</summary>
public partial class ManualView : UserControl
{
    readonly GripDrag<ManRowVm> rowDrag;
    readonly GripDrag<CardVm> cardDrag;
    ManualVm? vm;

    public ManualView()
    {
        InitializeComponent();
        rowDrag = new(this, (a, b) => vm?.MoveRow(a, b));
        cardDrag = new(this, (a, b) => vm?.MoveCard(a, b));
        DataContextChanged += (_, _) =>
        {
            if (vm != null) vm.ScrollTo -= OnScroll;
            vm = DataContext as ManualVm;
            if (vm != null) vm.ScrollTo += OnScroll;
        };
        PreviewKeyDown += OnKey;
    }

    void OnScroll(object item) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
    {
        foreach (var list in new[] { CardList, InputList })
            if (list.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement el) el.BringIntoView();
    });

    void OnGripDown(object sender, MouseButtonEventArgs e) => rowDrag.Down(sender, e);
    void OnGripMove(object sender, MouseEventArgs e) => rowDrag.Move(sender, e);
    void OnRowDrop(object sender, DragEventArgs e) => rowDrag.DropOn(sender, e);
    void OnCardGripDown(object sender, MouseButtonEventArgs e) => cardDrag.Down(sender, e);
    void OnCardGripMove(object sender, MouseEventArgs e) => cardDrag.Move(sender, e);
    void OnCardDrop(object sender, DragEventArgs e) => cardDrag.DropOn(sender, e);
    void OnCardsDrop(object sender, DragEventArgs e) => cardDrag.DropEnd(sender, e);

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Keyboard.FocusedElement is not TextBox and not ComboBox && vm?.DeleteRow.CanExecute(null) == true)
        {
            vm.DeleteRow.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>[+]: 켬 / 끔 / 벨트 / 출력 없음.</summary>
    void OnPlus(object sender, RoutedEventArgs e)
    {
        if (vm == null || ((FrameworkElement)sender).DataContext is not ManRowVm row) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var c in vm.OutChoices)
        {
            var item = new MenuItem { Header = c.Text };
            var kind = c.Kind;
            item.Click += (_, _) => vm.AddOut(row, kind);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
