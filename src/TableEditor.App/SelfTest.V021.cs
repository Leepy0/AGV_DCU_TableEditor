using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TableEditor.App.Blocks;
using TableEditor.App.ViewModels;
using TableEditor.App.Views;
using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Ops;
using TableEditor.Engine.Text;

namespace TableEditor.App;

/// <summary>
/// 자체 시험 v0.2.1 A10~A17: 출력 대기 고르기, 상태 숫자 입력, 도구 확인 상태, 보기 첫 줄, Ctrl+Z/Y, 우클릭 메뉴, 입력 목록, 자리 틀린 이름 빠른 고침.
/// 캡처: settings_probing.png, tag_menu.png.
/// </summary>
public sealed partial class SelfTest
{
    sealed class StubHost : IFieldHost
    {
        public string? Set;
        public bool IsTagArea => false;
        public void Hot(FieldVm f) { }
        public void SetField(FieldVm f, string text) => Set = text;
    }

    async Task V021()
    {
        var S = vm.Seq;
        int baseChanges = vm.ChangeCount;
        var seqBase = Text("seq_table.c");
        var tagBase = Text("tag_table.c");

        Step("A10 출력 대기 WAIT_OUT: 켬·끔 고르기 + 출력 이름 목록(입력 없음), 수동 표 출력 칸도 출력 목록");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.SeqTab);
        S.SelectTable("cart_load");
        S.SelectStep(S.Steps.First(r => r.Piece.Text.Contains("WAIT_OUT(")), true);
        await Idle();
        var kind = S.Fields[0];
        Check(kind.Label == "켬·끔" && kind.Text == "꺼짐" && kind.Info.Spec.Words!.SequenceEqual(["없음", "켜짐", "꺼짐"]), $"켬·끔 칸 ({kind.Label} {kind.Text})");
        var outs = S.Fields[1].Items!.Cast<Choice>().Select(c => c.Name).ToList();
        Check(S.Fields.Count == 2 && S.Fields[1].Label == "출력" && outs.Contains("DEMO_CV1_PWR") && outs.Contains("OUT_EXT_1") && !outs.Contains("DEMO_SEN_1") && !outs.Contains("IN_EXT_1"), $"출력 이름만 ({outs.Count}개)");
        kind.Picked = kind.Items!.Cast<Choice>().First(c => c.Name == "켜짐");
        await Idle();
        Check(S.SelectedStep!.Piece.Text.Trim() == "WAIT_OUT( IN( ON(DEMO_CV1_PWR) ) )" && Diff(seqBase, Text("seq_table.c")) == (1, 1), $"켜짐 → 출력 그대로 ({S.SelectedStep.Piece.Text.Trim()})");
        kind = S.Fields[0];
        kind.Picked = kind.Items!.Cast<Choice>().First(c => c.Name == "없음");
        await Idle();
        Check(S.SelectedStep!.Piece.Text.Trim() == "WAIT_OUT( NO_IN )" && S.Fields.Count == 1 && vm.ErrorCount == 0, $"없음 ({S.SelectedStep.Piece.Text.Trim()})");
        kind = S.Fields[0];
        kind.Picked = kind.Items!.Cast<Choice>().First(c => c.Name == "꺼짐");
        await Idle();
        Check(S.SelectedStep!.Piece.Text.Trim().StartsWith("WAIT_OUT( IN( OFF(", StringComparison.Ordinal) && vm.ErrorCount == 0 && S.Fields[1].Items!.Cast<Choice>().Any(c => c.Name == "OUT_EXT_1"), "없음 → 꺼짐: 첫 출력");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.ManualTab);
        await Idle();
        var manBlock = vm.Manual.Cards.SelectMany(c => c.Rows).SelectMany(r => r.Blocks).First(b => b.Head is "켬" or "끔");
        var manNames = Choices.For(manBlock.Fields[0].Spec, vm.Site!.Names).Select(c => c.Name).ToList();
        Check(manBlock.Fields[0].Spec.Kind == ArgKind.Output && manNames.Contains("OUT_EXT_1") && !manNames.Contains("IN_EXT_1"), $"수동 표 출력 칸 목록 ({manNames.Count}개)");

        Step("A11 상태 칸: ST_ 이름이 없으면 '상태 없음' + 숫자 직접 입력");
        var alt = Path.Combine(Path.GetFullPath(work), "alt_nostate");
        Clear(alt);
        Directory.CreateDirectory(alt);
        foreach (var f in Directory.GetFiles(site))
            File.Copy(f, Path.Combine(alt, Path.GetFileName(f)));
        File.WriteAllText(Path.Combine(alt, "seq_custom.h"), File.ReadAllText(Path.Combine(alt, "seq_custom.h")).Replace("ST_DEMO_READY = 0,", "").Replace("ST_DEMO_DONE", ""));
        var noState = Site.Open(alt).Names;
        var ws = vm.Site!.File("seq_table.c")!.Statements.First(s => s.Name == "WAIT_STATE");
        var stInfo = new FieldInfo("상태", ws.Args[0], new ArgSpec(ArgKind.State) { Label = "상태" });
        var host = new StubHost();
        var stField = new FieldVm(host, noState, stInfo, 0);
        Check(stField.IsBox && stField.NoStates && stField.Small == "상태 없음" && stField.Editor == FieldEditor.Number, $"숫자 상자 ({stField.Editor}, {stField.Small})");
        stField.Text = "5";
        stField.Commit();
        Check(!stField.Invalid && host.Set == "5", $"5 확정 ({host.Set})");
        stField.Text = "16";
        Check(stField.Invalid, "16 은 빨간 테");
        stField.Text = "ST_X";
        Check(stField.Invalid, "이름은 빨간 테");
        Check(new FieldVm(host, vm.Site!.Names, stInfo, 0).IsList, "ST_ 이름이 있으면 드롭다운");
        Clear(alt);

        Step("A12 설정 도구 다시 확인: 확인 중 흐림 → 끝나면 시각");
        var realProbe = vm.Probe;
        var gate = new TaskCompletionSource<IReadOnlyList<ToolStatus>>();
        vm.Probe = _ => gate.Task;
        vm.Tab = vm.SettingsTab;
        var run = vm.Config.ProbeAsync();
        await Idle();
        Check(vm.Config.IsProbing && vm.Config.ProbeText == "확인 중" && !vm.Config.Probe.CanExecute(null), $"확인 중 · 버튼 흐림 ({vm.Config.ProbeText})");
        await Shot("settings_probing.png");
        gate.SetResult([new("git", true, "git.exe", "git version 2", null), new("powershell", true, "powershell.exe", "5.1", null)]);
        await run;
        await Idle();
        Check(!vm.Config.IsProbing && vm.Config.ProbeText == "다시 확인" && vm.Config.Probe.CanExecute(null) && vm.Config.ProbedAt is { Length: 8 }, $"끝: 다시 확인 · 시각 {vm.Config.ProbedAt}");
        await Shot("settings_probed.png");
        vm.Probe = realProbe;

        Step("A13 태그 보기: 고른 줄이 없으면 보이는 첫 줄의 태그");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        Tag.SetSelection([], null, false);
        Tag.Filter.Clear();
        Tag.IsLook = true;
        await Idle();
        var first = Tag.Rows.First(r => r.Rule != null).First;
        Check(Tag.Selected == null && Tag.LookTag == first && Tag.LookRows.Count > 0 && Tag.LookTitle == $"태그 {first}", $"선택 없이 보기 → 태그 {Tag.LookTag}");
        Tag.IsLook = false;
        await Idle();

        Step("A14 Ctrl+Z / Ctrl+Y: 칸 편집 중이면 칸 확정 + 표 되돌리기 한 단계, 원문 바이트");
        var turn = Row(22);
        Tag.SelectOnly(turn);
        await Idle();
        int c0 = vm.ChangeCount;
        var angle = Tag.Fields.First(f => f.Label == "각도");
        angle.Text = "60";
        angle.Commit();
        await Idle();
        var after60 = TagText;
        Check(vm.ChangeCount == c0 + 1 && after60 != tagBase, $"각도 60 확정 (변경 {vm.ChangeCount - c0}, 같음 {after60 == tagBase}, 칸 '{angle.Text}', 원래 '{angle.Info.Arg.Text}', 오류 {angle.Invalid})");
        var box = Below<FieldEdit>(w).FirstOrDefault(f => ReferenceEquals(f.DataContext, Tag.Fields.First(x => x.Label == "각도")));
        box?.FocusEditor();
        await Idle();
        var edit = Tag.Fields.First(f => f.Label == "각도");
        edit.Text = "55";
        log.Add($"   포커스: {System.Windows.Input.Keyboard.FocusedElement?.GetType().Name} (창 활성 {w.IsActive})");
        Check(w.HandleUndoKey(Key.Z, ModifierKeys.Control), "Ctrl+Z 를 창이 먼저 받음");
        await Idle();
        Check(TagText == after60 && vm.ChangeCount == c0 + 1, $"칸 55 확정 뒤 한 단계 되돌림 → 60 그대로 ({vm.ChangeCount - c0})");
        Check(w.HandleUndoKey(Key.Y, ModifierKeys.Control), "Ctrl+Y");
        await Idle();
        Check(Row(22).Rule!.St.Text.Contains("55") && vm.ChangeCount == c0 + 2, $"다시하기 → 55 ({vm.ChangeCount - c0})");
        w.HandleUndoKey(Key.Z, ModifierKeys.Control);
        w.HandleUndoKey(Key.Z, ModifierKeys.Control);
        await Idle();
        Check(TagText == tagBase && vm.ChangeCount == c0, "되돌려 처음 바이트 그대로");
        Check(!w.HandleUndoKey(Key.Z, ModifierKeys.Control | ModifierKeys.Shift) && !w.HandleUndoKey(Key.A, ModifierKeys.Control), "다른 조합은 건드리지 않음");

        Step("A15 우클릭 메뉴: 선택 규칙(여러 줄 안이면 유지, 밖이면 그 줄만), 줄 메뉴 항목");
        var rows = (w.FindName("TagPane") as TagView)!.FindName("Rows") as ListBox ?? throw new InvalidOperationException("Rows");
        var vis = Tag.Rows.Where(r => r.Rule != null).Take(3).ToList();
        Tag.SetSelection([vis[0], vis[1]], vis[0], false);
        await Idle();
        System.Windows.UIElement Container(TagRowVm r) => (System.Windows.UIElement)rows.ItemContainerGenerator.ContainerFromItem(r);
        void Right(TagRowVm r)
        {
            var e = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = Container(r) };
            rows.RaiseEvent(e);
        }
        Right(vis[1]);
        await Idle();
        Check(Tag.SelectedRows.Count == 2 && Tag.SelectedRows.Contains(vis[0]) && Tag.SelectedRows.Contains(vis[1]), $"고른 두 줄 안에서 우클릭 = 유지 ({Tag.SelectedRows.Count})");
        Right(vis[2]);
        await Idle();
        Check(Tag.SelectedRows.Count == 1 && ReferenceEquals(Tag.Selected, vis[2]), "밖의 줄 우클릭 = 그 줄만");
        var heads = rows.ContextMenu!.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToList();
        Check(new[] { "추가", "복제", "삭제", "조건", "동작 추가", "동작 삭제", "칸 편집", "원문" }.All(heads.Contains), $"태그 메뉴 {string.Join("/", heads)}");
        var menu = rows.ContextMenu!;
        menu.PlacementTarget = rows;
        menu.IsOpen = true;
        await Idle();
        if (menu.ActualWidth > 0)
        {
            if (shots != null)
            {
                // 팝업 안 요소는 자리가 음수라 그대로 찍으면 잘린다: 붓으로 같은 크기 사각형에 옮겨 찍는다
                var pic = new System.Windows.Shapes.Rectangle { Width = menu.ActualWidth, Height = menu.ActualHeight, Fill = new System.Windows.Media.VisualBrush(menu) };
                pic.Measure(new Size(pic.Width, pic.Height));
                pic.Arrange(new Rect(0, 0, pic.Width, pic.Height));
                App.Save(pic, Path.Combine(shots, "tag_menu.png"));
            }
        }
        else log.Add("   메뉴 크기 0: 캡처 못 함");
        menu.IsOpen = false;
        Check(RowMenu.Pick(rows, null) == null, "줄 밖 우클릭 = 선택 안 건드림");

        Step("A16 조건 입력 목록: IN_START_SW·IN_STOP_SW·IN_REMOTE_START·IN_BUMPER_1·IN_OBS_F, IN_AMP_* 는 없음");
        var inputs = Choices.For(new ArgSpec(ArgKind.Input), vm.Site!.Names).Select(c => c.Name).ToList();
        Check(new[] { "IN_START_SW", "IN_STOP_SW", "IN_REMOTE_START", "IN_BUMPER_1", "IN_OBS_F" }.All(inputs.Contains), "논리 입력 다섯 개 선택지에 있음");
        var cond = new InputVm("입력", (Arg?)null, vm.Site!.Names, false);
        cond.AddGroup.Execute(null);
        var sig = cond.Groups[0].Signals.Select(s => s.Name).ToList();
        Check(sig.Contains("IN_OBS_F") && !sig.Any(n => n.StartsWith("IN_AMP_", StringComparison.Ordinal)) && !sig.Contains("IO_NONE"), $"조건 팝업 신호 {sig.Count}개 · AMP·미연결 제외");

        Step("A17 자리 틀린 이름: 태그 X_FORWARD → IF_FORWARD, 시퀀스 IF_FORWARD → X_FORWARD, [바꾸기] 한 낱말만(바이트)");
        var tagRule = Row(64).Rule!;
        var cell = tagRule.St.Args[2];
        vm.Apply(SiteEdit.SetArg(vm.Site!, tagRule.St, cell, "X_FORWARD"));
        await Idle();
        var bad = vm.Findings.FirstOrDefault(f => f.Finding.Replace != null);
        Check(bad != null && bad.IsError && bad.Finding.Replace == "IF_FORWARD" && bad.Text.Contains("IF_FORWARD"), $"후보 한 낱말 ({bad?.Text})");
        var beforeFix = TagText;
        Check(bad != null && vm.FixFinding(bad.Finding), "[바꾸기]");
        await Idle();
        Check(TagText == beforeFix.Replace("TAG_IF( 64, ANY_PROFILE, X_FORWARD,", "TAG_IF( 64, ANY_PROFILE, IF_FORWARD,") && Diff(beforeFix, TagText) == (1, 1) && vm.ErrorCount == 0 && vm.Findings.All(f => f.Finding.Replace == null),
            $"그 낱말만 바뀜 · 오류 {vm.ErrorCount}");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.SeqTab);
        S.SelectTable("cart_load");
        var wt = S.Steps.First(r => r.Piece.Text.Contains("WAIT_IF_TO( X_CROSS_OUT, 500, ERR )"));
        var seqBefore = Text("seq_table.c");
        vm.Apply(SiteEdit.SetArg(vm.Site!, (Statement)wt.Piece, ((Statement)wt.Piece).Args[0], "IF_FORWARD"));
        await Idle();
        var bad2 = vm.Findings.FirstOrDefault(f => f.Finding.Replace != null);
        Check(bad2 != null && bad2.Finding.Replace == "X_FORWARD", $"시퀀스 쪽 후보 ({bad2?.Text})");
        var seqMid = Text("seq_table.c");
        Check(bad2 != null && vm.FixFinding(bad2.Finding), "[바꾸기]");
        await Idle();
        Check(Text("seq_table.c") == seqBefore.Replace("WAIT_IF_TO( X_CROSS_OUT, 500, ERR )", "WAIT_IF_TO( X_FORWARD, 500, ERR )") && Diff(seqMid, Text("seq_table.c")) == (1, 1) && vm.ErrorCount == 0, "시퀀스: 그 낱말만");

        while (vm.ChangeCount > baseChanges && vm.Undo.CanExecute(null)) { vm.Undo.Execute(null); await Idle(); }
        Check(Text("seq_table.c") == seqBase && TagText == tagBase, "되돌리면 원문 그대로");
    }
}
