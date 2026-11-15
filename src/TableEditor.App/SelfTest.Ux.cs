using System.IO;
using TableEditor.App.Blocks;
using TableEditor.App.ViewModels;

namespace TableEditor.App;

/// <summary>
/// 자체 시험의 화면 손질 단계: 동작 위치(TAPE) 고르기, 시퀀스 검색, 두 번 누르기 이동, 표 안 칸 편집, 프로파일 구분. 앞 단계가 저장까지 마친 뒤(변경 0)에 돈다.
/// 캡처: seq_search.png, tag_cell.png, tag_cell_error.png, profile_look.png, profile_seq.png.
/// </summary>
public sealed partial class SelfTest
{
    /// <summary>태그 줄의 동작 블록 하나를 고른다(줄 고르기 + 블록 누르기와 같다).</summary>
    async Task<BlockVm> PickBlock(long tag, string profile, int index)
    {
        var row = Row(tag, profile);
        Tag.SelectOnly(row);
        row.Blocks[index].Pick!.Execute(null);
        await Idle();
        return Tag.Picked!;
    }

    async Task Ux()
    {
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        await Idle();
        Check(vm.ChangeCount == 0, $"시작 변경 0 ({vm.ChangeCount})");
        var start = TagText;

        Step("위치 고르기: SEQ_START 바로 → TAPE → TAPE_GD, AGV_STOP 바로 → TAPE");
        await PickBlock(40, "PF_BELT", 0);
        var pos = Tag.Fields.FirstOrDefault(f => f.Label == "위치");
        Check(pos != null && pos.IsList && pos.Text == BlockBuilder.Bare, $"위치 칸 ({pos?.Text})");
        var words = pos?.Items?.Cast<Choice>().Select(c => c.Name).ToList() ?? [];
        Check(words.SequenceEqual([BlockBuilder.Bare, "cm", "TAPE", "TAPE_GD"]), $"고를 것 {string.Join("/", words)}");
        pos!.Picked = pos.Items!.Cast<Choice>().First(c => c.Name == "TAPE");
        await Idle();
        Check(Row(40, "PF_BELT").Rule!.St.Text.Contains("SEQ_START( TAPE, LOAD ), AGV_FORBACK( TOGGLE )"), "SEQ_START( TAPE, LOAD )");
        Check(Diff(start, TagText) == (1, 1) && vm.ErrorCount == 0, $"한 줄·오류 0 {Diff(start, TagText)}");
        Check(Tag.Picked?.Head == "시퀀스" && Tag.Fields[0].Text == "TAPE" && Tag.Fields.Count == 2, $"같은 블록·칸 TAPE ({string.Join("/", Tag.Fields.Select(f => f.Label + "=" + f.Text))})");
        Tag.Fields[0].Picked = Tag.Fields[0].Items!.Cast<Choice>().First(c => c.Name == "TAPE_GD");
        await Idle();
        Check(Row(40, "PF_BELT").Rule!.St.Text.Contains("SEQ_START( TAPE_GD, LOAD )") && vm.ErrorCount == 0, "TAPE_GD");
        await PickBlock(1, "ANY_PROFILE", 0);
        Tag.Fields.First(f => f.Label == "위치").Picked = Tag.Fields[0].Items!.Cast<Choice>().First(c => c.Name == "TAPE");
        await Idle();
        Check(Row(1).Rule!.St.Text.Contains("AGV_STOP( TAPE ), AGV_AUTOSTART_PARAM") && vm.ErrorCount == 0, "AGV_STOP( TAPE )");
        Check(vm.ChangeCount == 3, $"변경 3 ({vm.ChangeCount})");
        for (int i = 0; i < 3; i++) { vm.Undo.Execute(null); await Idle(); }
        Check(TagText == start && vm.ChangeCount == 0, "되돌리면 그대로");

        Step("시퀀스 검색: 호기 12, 프로파일 PF_BOX, 숫자 아님 — 태그 화면과 같은 막대");
        var S = vm.Seq;
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.SeqTab);
        string Heads() => string.Join("/", S.Tree.Where(t => t.Level == 0).Select(t => t.Text));
        Check(Heads() == "PF_BELT/PF_LIFT/PF_CART/PF_BOX/전체", $"처음 묶음 {Heads()}");
        Check(S.Tree.Count(t => t.IsGroupStart) == 4 && !S.Tree[0].IsGroupStart, "둘째 묶음부터 구분선");
        S.Filter.Text = "12";
        await Idle();
        Check(Heads() == "PF_LIFT/전체" && S.SelectedTable == "lift_load" && S.Steps.Count > 0, $"호기 12 → {Heads()} · {S.SelectedTable}");
        Check(S.Filter.Profiles.SequenceEqual(Tag.Filter.Profiles), "프로파일 목록 = 태그 화면");
        S.Filter.Text = "";
        S.Filter.Profile = "PF_BOX";
        await Idle();
        Check(Heads() == "PF_BOX/전체" && S.SelectedTable == "box_turn_unload", $"PF_BOX → {Heads()} · {S.SelectedTable}");
        await Shot("seq_search.png");
        S.Filter.Text = "x";
        await Idle();
        Check(S.Tree.Count == 0 && S.IsEmpty, "숫자 아님 → 없음");
        S.Filter.Text = "";
        S.Filter.Profile = TagVm.AllProfiles;
        Check(Heads().StartsWith("PF_BELT/", StringComparison.Ordinal), "풀면 모두");
        Tag.Filter.Text = "40";
        await Idle();
        Check(Tag.Rows.Count > 0 && Tag.Rows.All(r => r.Covers(40)), "태그 화면도 같은 거르기");
        Tag.Filter.Text = "";

        Step("시퀀스 이름 두 번 누르기: 태그 40 PF_LIFT SEQ_START( TAPE, LOAD ) → lift_load");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        S.Filter.Profile = "PF_BOX"; // 가려 있어도 간다
        var r40 = Row(40, "PF_LIFT");
        var seqPart = r40.Blocks[1].Parts.FirstOrDefault(p => p.Seq != null);
        Check(seqPart?.Seq == "LOAD" && r40.Blocks[0].Parts.All(p => p.Seq == null), $"시퀀스 값 ({seqPart?.Seq})");
        Tag.OpenSeq(r40, seqPart!.Seq!);
        await Idle();
        Check(vm.IsSeqTab && S.SelectedTable == "lift_load" && S.Node?.Table == "lift_load" && S.Node.Group == "PF_LIFT", $"시퀀스 화면 lift_load ({S.SelectedTable}, {S.Node?.Group})");
        Check(S.SelectedStep == S.Steps.FirstOrDefault() && S.Filter.Profile == TagVm.AllProfiles, "첫 스텝·거르기 풂");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        Tag.OpenSeq(Row(40, "PF_BELT"), "LOAD");
        Check(S.SelectedTable == "belt_load", $"PF_BELT 줄 → belt_load ({S.SelectedTable})");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        Tag.OpenSeq(Row(40, "PF_BELT"), "NOPE");
        Check(vm.IsTagTab && vm.Notice?.Contains("NOPE") == true, $"없는 시퀀스는 그대로 ({vm.Notice})");
        vm.ClearNotice();

        Step("표 안 칸 편집: 태그 6 자동 출발 5 → 300(틀림) → 7, 되돌리기·저장 원문 비교");
        start = TagText;
        var r6 = Row(6);
        Tag.BeginCellEdit(r6.Blocks[1]);
        await Idle();
        var edits = CellEditors();
        Check(Tag.CellEditing && Tag.Picked?.Head.StartsWith("자동 출발") == true && edits == 1, $"줄 안 편집기 {edits} ({Tag.Picked?.Head})");
        var sec = Tag.Fields[0];
        sec.Text = "300";
        sec.Commit();
        await Idle();
        Check(sec.Invalid && vm.ChangeCount == 0 && TagText == start && Tag.CellEditing, "틀린 값: 칸만 빨갛게, 원문 그대로");
        await Shot("tag_cell_error.png");
        sec.Text = "7";
        sec.Commit();
        await Idle();
        var expect = start.Replace("AGV_STOP( 30 ), AGV_AUTOSTART( 5 )", "AGV_STOP( 30 ), AGV_AUTOSTART( 7 )");
        Check(expect != start && TagText == expect && vm.ChangeCount == 1 && vm.ErrorCount == 0, "그 인자 글자만 바뀜");
        Check(vm.Notice == null, $"알림 없음 ({vm.Notice})");
        Check(Tag.CellEditing && CellEditors() == 1 && Tag.Fields[0].IsHot && Tag.Fields[0].Text == "7", $"편집 이어짐·같은 칸 ({Tag.CellEditing}, {CellEditors()}, {Tag.Fields.FirstOrDefault()?.IsHot}, {Tag.Fields.FirstOrDefault()?.Text})");
        await Shot("tag_cell.png");
        Tag.EndCellEdit();
        await Idle();
        Check(CellEditors() == 0, "끝내면 블록으로");
        vm.Undo.Execute(null);
        await Idle();
        Check(TagText == start && vm.ChangeCount == 0, "되돌리기");
        vm.Redo.Execute(null);
        await Idle();
        bool saved = await vm.SaveAsync();
        await Idle();
        Check(saved && DiskText == expect, "저장: 디스크 = 그 인자만 바꾼 원문(바이트)");
        Tag.BeginCellEdit(Row(6).Blocks[0]);
        Check(Tag.CellEditing, "다시 들어감");
        Tag.FromView([Row(1)], Row(1));
        Check(!Tag.CellEditing, "다른 줄 고르면 끝");

        Step("프로파일 구분: 태그 보기 40, 시퀀스 나무, 수동 카드(같은 ProfileHead·ProfileRule)");
        Tag.Filter.Text = "40";
        Tag.IsLook = true;
        await Idle();
        int pfs = Tag.Profiles.Count - 1;
        Check(Tag.LookRows.Count(r => r.IsGroupStart) == pfs, $"보기 묶음 머리 {Tag.LookRows.Count(r => r.IsGroupStart)} = 프로파일 {pfs}");
        await Shot("profile_look.png");
        Tag.IsLook = false;
        Tag.Filter.Text = "";
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.SeqTab);
        S.SelectTable("cart_load");
        await Idle();
        Check(S.Tree.Where(t => t.Level == 0).Skip(1).All(t => t.IsGroupStart), "나무 둘째 묶음부터 선");
        await Shot("profile_seq.png");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
    }

    /// <summary>머리 줄에 보이는 표 안 칸 편집기 수.</summary>
    int CellEditors()
    {
        var list = w.FindName("TagPane") as Views.TagView;
        if (list?.FindName("Rows") is not System.Windows.Controls.ListBox rows || Tag.Selected == null) return -1;
        rows.UpdateLayout();
        if (rows.ItemContainerGenerator.ContainerFromItem(Tag.Selected) is not System.Windows.DependencyObject item) return -1;
        return Below<System.Windows.Controls.ScrollViewer>(item).Where(x => x.Name == "CellEd" && x.IsVisible).SelectMany(Below<Views.FieldEdit>).Count();
    }

    static IEnumerable<T> Below<T>(System.Windows.DependencyObject o) where T : System.Windows.DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(o); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(o, i);
            if (c is T t) yield return t;
            foreach (var x in Below<T>(c)) yield return x;
        }
    }
}
