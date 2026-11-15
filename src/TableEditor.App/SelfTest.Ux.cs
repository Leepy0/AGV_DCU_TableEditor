using System.IO;
using TableEditor.App.Blocks;
using TableEditor.App.ViewModels;

namespace TableEditor.App;

/// <summary>
/// 자체 시험의 화면 손질 단계: 동작 위치(TAPE) 고르기. 앞 단계가 저장까지 마친 뒤(변경 0)에 돈다.
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
    }
}
