using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TableEditor.App.Blocks;
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

    /// <summary>모든 표 화면의 줄이 지금 사이트의 조각을 가리키는가(낡은 줄 VM 이 남으면 false). 화면 이름 목록을 돌려준다.</summary>
    List<string> StaleViews()
    {
        var live = vm.Site!.Files.SelectMany(f => f.Pieces).ToHashSet<object>(ReferenceEqualityComparer.Instance);
        var bad = new List<string>();
        void Chk(string name, IEnumerable<object> pieces) { if (pieces.Any(p => !live.Contains(p))) bad.Add(name); }
        Chk("태그", Tag.Rows.Select(r => (object)r.Piece));
        Chk("프로파일", vm.Profile.Rows.Select(r => (object)r.Piece));
        Chk("수동", vm.Manual.Cards.SelectMany(c => c.Rows).Select(r => (object)r.St));
        Chk("시퀀스", vm.Seq.Steps.Select(r => (object)r.Piece));
        Chk("대응표", vm.Map.Rows.Select(r => (object)r.Piece));
        return bad;
    }

    async Task V023Defer()
    {
        Step("24 편집은 안 보이는 탭을 미룸 · 보이면 지금 사이트");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        await Idle();
        var steps0 = vm.Seq.Steps;
        var rows0 = vm.Profile.Rows;
        FieldVm? f = null;
        foreach (var r in Tag.Rows.Where(r => r.Rule != null))
        {
            Tag.SelectOnly(r);
            f = Tag.Fields.FirstOrDefault(x => x.Editor == FieldEditor.Number && !x.Locked && !x.NoStates && long.TryParse(x.Text, out _));
            if (f != null) break;
        }
        Check(f != null, "숫자 칸이 있는 태그 줄");
        await Idle();
        long v = long.Parse(f!.Text);
        foreach (var cand in new[] { v + 5, v - 5, v + 1, v - 1 }) { f.Text = cand.ToString(); if (!f.Invalid) break; }
        int changes = vm.ChangeCount;
        f.Commit();
        await Idle();
        Check(vm.ChangeCount == changes + 1, $"칸 고침 ({vm.ChangeCount})");
        Check(ReferenceEquals(vm.Seq.Steps, steps0) && ReferenceEquals(vm.Profile.Rows, rows0), "안 보이는 시퀀스·프로파일 화면은 다시 안 그림");
        Check(!StaleViews().Contains("태그"), "보이는 태그 화면은 바로 현재 사이트");
        foreach (var name in new[] { MainVm.SeqTab, MainVm.ProfileTab, MainVm.ManualTab, MainVm.MapTab, MainVm.IoTab, MainVm.TagTab })
        {
            vm.Tab = vm.Tabs.First(t => t.Name == name);
            await Idle();
            Check(!StaleViews().Contains(name), $"{name} 탭이 보이면 현재 사이트");
        }
        Check(StaleViews().Count == 0, "모든 줄 VM 이 현재 사이트의 조각");
        vm.Undo.Execute(null);
        await Idle();
        foreach (var name in new[] { MainVm.SeqTab, MainVm.ManualTab, MainVm.TagTab })
        {
            vm.Tab = vm.Tabs.First(t => t.Name == name);
            await Idle();
        }
        vm.Flush(MainVm.ProfileTab);
        vm.Flush(MainVm.MapTab);
        Check(vm.ChangeCount == changes && StaleViews().Count == 0, "되돌린 뒤에도 모든 줄 VM 이 현재 사이트");
    }
}
