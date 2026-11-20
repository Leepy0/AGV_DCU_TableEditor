using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TableEditor.App.ViewModels;
using TableEditor.Engine.Model;

namespace TableEditor.App;

/// <summary>자체 시험 v0.2.3: 팝업이 떠 있는 동안 아래 화면에 키·글자·포커스가 닿지 않고, 닫으면 포커스가 돌아온다(IsEnabled 를 쓰지 않는다).</summary>
public sealed partial class SelfTest
{
    static T? FindAbove<T>(DependencyObject d, Func<T, bool> pred) where T : DependencyObject
    {
        if (d is T t && pred(t)) return t;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindAbove(VisualTreeHelper.GetChild(d, i), pred) is { } r) return r;
        return null;
    }

    async Task V023()
    {
        Step("23 팝업 아래 입력 차단 · 포커스 복귀");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        await Idle();
        var row = Tag.Rows.First(r => r.Rule != null);
        Tag.SelectOnly(row);
        await Idle();
        var lb = FindAbove<ListBox>(w, x => x.Name == "Rows" && x.DataContext is TagVm)!;
        var item = (ListBoxItem)lb.ItemContainerGenerator.ContainerFromItem(Tag.Selected!);
        Keyboard.Focus(item);
        Check(ReferenceEquals(Keyboard.FocusedElement, item), "팝업 전: 줄에 포커스");
        string textBefore = TagText, tagBefore = Tag.Selected!.TagText;
        int changes = vm.ChangeCount;

        var (dlg, done) = await Open<AddVm>(() => vm.AddRowsAsync());
        Check(w.InDialog(Keyboard.FocusedElement as DependencyObject), "팝업이 뜨면 포커스가 팝업 안");
        Check(!(w.Root.IsEnabled == false) && item.IsEnabled && lb.IsEnabled, "아래 화면에 IsEnabled 를 걸지 않음(UIA 폭주 방지)");

        Keyboard.Focus(item);
        Check(!ReferenceEquals(Keyboard.FocusedElement, item) && w.InDialog(Keyboard.FocusedElement as DependencyObject), "아래 줄로 포커스를 옮겨도 팝업 안에 남음");
        var below = FindAbove<TextBox>(w, x => !w.InDialog(x) && x.IsVisible);
        if (below != null)
        {
            Keyboard.Focus(below);
            Check(!ReferenceEquals(Keyboard.FocusedElement, below), "아래 글 상자로 포커스 못 옮김");
        }
        for (int i = 0; i < 6; i++) w.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        Check(w.InDialog(Keyboard.FocusedElement as DependencyObject), "Tab 이동이 팝업 밖으로 안 나감");

        UIElement target = (UIElement?)below ?? item;
        bool Key(Key k)
        {
            var e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w), 0, k) { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = target };
            target.RaiseEvent(e);
            return e.Handled;
        }
        Check(Key(System.Windows.Input.Key.Enter) && Key(System.Windows.Input.Key.Space) && Key(System.Windows.Input.Key.Z) && Key(System.Windows.Input.Key.Delete) && Key(System.Windows.Input.Key.F2), "아래 대상의 Enter·Space·Z·Del·F2 는 막힘");
        var txt = new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, target, "x")) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent, Source = target };
        target.RaiseEvent(txt);
        Check(txt.Handled, "아래 대상의 글자 입력은 막힘");
        Check(!w.HandleUndoKey(System.Windows.Input.Key.Z, ModifierKeys.Control) && !vm.Undo.CanExecute(null), "Ctrl+Z 는 팝업 중 되돌리지 않음");
        var inside = Keyboard.FocusedElement as UIElement;
        var free = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w), 0, System.Windows.Input.Key.F13) { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = inside };
        inside!.RaiseEvent(free);
        Check(!free.Handled, "팝업 안 대상의 키는 막지 않음");
        Check(TagText == textBefore && Tag.Selected!.TagText == tagBefore && vm.ChangeCount == changes, "아래 화면 상태·원문 그대로");

        dlg.Cancel.Execute(null);
        await done;
        await Idle();
        Check(ReferenceEquals(Keyboard.FocusedElement, item), "팝업을 닫으면 포커스가 줄로 돌아옴");
    }
}
