using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TableEditor.App.Blocks;
using TableEditor.App.ViewModels;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

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

    async Task V023Stale()
    {
        Step("25 낡은 사이트 기준 편집은 적용 안 함");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        await Idle();
        var old = vm.Site!;
        var st = old.File("tag_table.c")!.Pieces.OfType<Statement>().First();
        Check(vm.Apply(SiteEdit.SetComment(old, st, "a")), "지금 사이트 기준 편집은 적용");
        await Idle();
        var live = vm.Site!;
        string text = TagText;
        int changes = vm.ChangeCount, fails = vm.ApplyFailures;
        vm.ClearNotice();
        var stale = SiteEdit.SetComment(old, st, "낡음");
        Check(stale.Ok && ReferenceEquals(stale.Base, old), "낡은 사이트에서 만든 편집 결과");
        Check(!vm.Apply(stale), "낡은 기준 편집은 거부");
        await Idle();
        Check(vm.Notice == "화면 다시 읽음" && vm.ApplyFailures == fails + 1, $"알림 ({vm.Notice})");
        Check(ReferenceEquals(vm.Site, live) && vm.ChangeCount == changes && TagText == text, "사이트·변경 수·원문 그대로(그 사이 편집이 안 지워짐)");
        Check(StaleViews().Count == 0, "거부 뒤 전체 다시 그림: 모든 줄 VM 이 현재 사이트");
        vm.ClearNotice();
        vm.Undo.Execute(null);
        await Idle();
        Check(ReferenceEquals(vm.Site, old), "되돌리면 처음 사이트");
    }

    /// <summary>L1 편집을 낸 팝업 뒤 포커스 · L2 연달아 뜨는 팝업 · L3 팝업 중 명령 없는 길 · L4 미룬 탭이 마지막 편집의 줄을 고름.</summary>
    async Task V023Leftovers()
    {
        Step("26 팝업 포커스 복귀(편집·연달아) · 팝업 중 이동 명령 · 미룬 탭의 줄 고르기");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        await Idle();
        var lb = FindAbove<ListBox>(w, x => x.Name == "Rows" && x.DataContext is TagVm)!;
        var rows = Tag.Rows.Where(r => r.Rule != null).Take(2).ToList();
        Tag.SelectOnly(rows[0]);
        await Idle();
        var itemA = (ListBoxItem)lb.ItemContainerGenerator.ContainerFromItem(Tag.Selected!);
        AskVm Ask() => new("시험", ["x"], [new AskButton("ok", "닫기", Primary: true, Default: true)]);

        // L2: 팝업이 닫히자마자 다음 팝업 → 둘 다 닫으면 처음 포커스 자리(선택 줄이 아닌 다른 줄에 있던 포커스)
        var itemB = (ListBoxItem)lb.ItemContainerGenerator.ContainerFromItem(rows[1]);
        Keyboard.Focus(itemB);
        Check(ReferenceEquals(Keyboard.FocusedElement, itemB), "선택 줄이 아닌 줄에 포커스");
        var (dlgA, doneTwo) = await Open<AskVm>(async () => { await vm.Show(Ask()); await vm.Show(Ask()); });
        var second = new TaskCompletionSource<DialogVm>();
        void OnSecond(DialogVm d) { if (!ReferenceEquals(d, dlgA)) second.TrySetResult(d); }
        vm.DialogShown += OnSecond;
        dlgA.Cancel.Execute(null);
        var dlgB = await second.Task; // 닫는 쪽 복귀(Loaded)보다 먼저 이어서 뜨는 둘째
        vm.DialogShown -= OnSecond;
        Check(!ReferenceEquals(dlgB, dlgA), "연달아 둘째 팝업");
        dlgB.Cancel.Execute(null);
        await doneTwo;
        await Idle();
        Check(ReferenceEquals(Keyboard.FocusedElement, itemB), "연달아 팝업을 닫으면 원래 포커스 자리");

        // L3: 팝업 중에는 명령 없는 이동 길이 아무것도 안 함
        var (dlg, done) = await Open<AskVm>(async () => { await vm.Show(Ask()); });
        var tab0 = vm.Tab;
        vm.ClearNotice();
        vm.Tabs.First(t => t.Name == MainVm.SeqTab).IsSelected = true;
        vm.Update.OpenInfo.Execute(null);
        vm.Map.GoTable.Execute(vm.Map.Rows[0]);
        if (vm.Findings.FirstOrDefault(x => x.Tab != null && x.Tab != tab0) is { } fnd) vm.GoTo.Execute(fnd);
        Tag.OpenSeq(rows[0], "ANY");
        Check(!vm.GoSeq(null, "SEQ_X"), "팝업 중 GoSeq 안 함");
        Check(ReferenceEquals(vm.Tab, tab0) && vm.Notice == null, $"팝업 중 탭 전환·열기·이동 안 함 ({vm.Tab.Name})");
        await Idle();
        Check(vm.Tabs.First(t => t.Name == MainVm.SeqTab).IsSelected == false, "탭 모양도 그대로(라디오 되돌림)");
        // L1: 팝업이 편집을 내면 저장한 요소 대신 선택 줄로
        Check(vm.Apply(SiteEdit.SetComment(vm.Site!, (Statement)rows[1].Piece, "L1")), "팝업 중 편집");
        dlg.Cancel.Execute(null);
        await done;
        await Idle();
        var fe = Keyboard.FocusedElement as FrameworkElement;
        Check(fe is ListBoxItem && ReferenceEquals(fe.DataContext, Tag.Selected) && Tag.Selected != null, $"편집을 낸 팝업 뒤 포커스 = 선택 줄 ({fe?.GetType().Name})");
        vm.Undo.Execute(null);
        await Idle();

        // L4: 보이지 않는 프로파일 탭이 여러 표에 걸친 편집의 줄을 보일 때 고른다
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.ProfileTab);
        await Idle();
        var named = vm.Profile.Rows.Where(x => x.Row != null).ToList();
        vm.Profile.Select(named[^1], false); // 마지막 줄을 골라 둔다: 첫 줄의 이름을 바꾸면 미룬 화면이 첫 줄을 골라야 한다
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        await Idle();
        var old = named[0].Row!.Profile;
        var r = BundleEdit.RenameProfile(vm.Site!, old, "PF_ZZ_TEST");
        Check(r.Ok && r.Touched.Count > 0 && vm.Apply(r), $"프로파일 이름 바꾸기 ({r.Error})");
        await Idle();
        Check(vm.IsTagTab, "편집 뒤에도 태그 탭");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.ProfileTab);
        await Idle();
        Check(vm.Profile.Selected?.Piece is { } sel && r.Touched.Any(t => ReferenceEquals(t, sel)) && !ReferenceEquals(vm.Profile.Selected, vm.Profile.Rows.Where(x => x.Row != null).Last()), "미룬 프로파일 탭이 보이면 마지막 편집의 줄이 골라짐(다른 줄에서 옮겨 옴)");
        vm.Undo.Execute(null);
        await Idle();
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        await Idle();
        Check(StaleViews().Count == 0, "되돌린 뒤 모든 줄 VM 이 현재 사이트");
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
