using System.IO;
using TableEditor.App.Blocks;
using TableEditor.App.ViewModels;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.App;

/// <summary>
/// 자체 시험의 화면 손질 단계: 동작 위치(TAPE) 고르기, 시퀀스 검색, 두 번 누르기 이동, 표 안 칸 편집, 프로파일 구분. 앞 단계가 저장까지 마친 뒤(변경 0)에 돈다.
/// 캡처: seq_search.png, tag_cell.png, tag_cell_error.png, profile_look.png, profile_seq.png, tag_chips.png,
/// seq_add.png, seq_label_block.png, seq_chips.png, seq_table_add.png, seq_delete_block.png, tag_drop_ask.png, seq_comment_check.png, seq_pair_delete.png.
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
        Check(Tag.Picked?.Head == "정지·시퀀스" && Tag.Fields[0].Text == "TAPE" && Tag.Fields.Count == 2, $"같은 블록·칸 TAPE ({string.Join("/", Tag.Fields.Select(f => f.Label + "=" + f.Text))})");
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

        Step("동작 칩·켬/끔: 회전 자동 출발 1 = ON, 고르면 OFF → 원문 0, 저장 바이트 비교");
        start = TagText;
        var r21 = Row(21);
        var turnParts = r21.Blocks[0].Parts;
        Check(turnParts.Any(p => p.Label == "출발" && p.Value == "ON" && p.On == true), "출발 ON " + string.Join(" | ", turnParts));
        Check(turnParts.Any(p => p.Label == "속도" && p.Value == "0" && p.On == null) && turnParts.Any(p => p.Label == "회전 뒤" && p.Value == "0"), "속도 0·회전 뒤 0 은 숫자");
                await PickBlock(21, "ANY_PROFILE", 0);
        var auto = Tag.Fields.First(f => f.Label == "자동 출발");
        Check(auto.IsList && auto.Text == "ON" && auto.Items!.Cast<Choice>().Select(c => c.Name).SequenceEqual(["ON", "OFF"]), "ON·OFF 드롭다운");
        await Shot("tag_chips.png");
        auto.Picked = auto.Items!.Cast<Choice>().First(c => c.Name == "OFF");
        await Idle();
        var want = start.Replace("AGV_TURN( CW,  90,  0, 1, 0 )", "AGV_TURN( CW,  90,  0, 0, 0 )");
        Check(want != start && TagText == want && Row(21).Blocks[0].Parts.Any(p => p.Value == "OFF" && p.On == false), "원문 0 · 화면 OFF");
        saved = await vm.SaveAsync();
        await Idle();
        Check(saved && File.ReadAllBytes(Path.Combine(site, "tag_table.c")).AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(want)), "저장 바이트 = 그 칸만 1 → 0");

        await SeqEditing();
    }

    /// <summary>시퀀스 구조 편집: 스텝 추가·바꾸기·끌기·지우기, 라벨·표 참조 막힘, 켬/끔, 표 추가·지우기, 저장 바이트.</summary>
    async Task SeqEditing()
    {
        var S = vm.Seq;
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.SeqTab);
        S.SelectTable("belt_unload");
        await Idle();
        var seqOrig = Text("seq_table.c");
        int changes = vm.ChangeCount;

        Step("시퀀스 스텝: 추가(분류·명령 팝업) → 명령 바꾸기 → 끝으로 끌기 → 라벨 막힘 → 삭제 = 원문 그대로");
        S.SelectStep(S.Steps.First(r => r.Ordinal == 1), true);
        var (add, addDone) = await Open<StepVm>(() => S.AddStepAsync());
        Check(add.Categories.Contains("대기") && add.Categories.Contains("출력"), $"분류 {string.Join("/", add.Categories)}");
        Check(!add.Categories.SelectMany(c => { add.Category = c; return add.Commands; }).Any(c => c.Entry.Place != TableEditor.Engine.Grammar.Place.Step), "스텝 줄만");
        add.Category = "대기";
        add.Command = add.Commands.First(c => c.Entry.Name == "DELAY");
        Check(add.Preview == "DELAY( 100 )", $"미리 보기 {add.Preview}");
        await Shot("seq_add.png");
        add.Ok.Execute(null);
        await addDone;
        await Idle();
        Check(S.SelectedStep?.Block.Head == "지연" && S.SelectedStep.Ordinal == 2 && Diff(seqOrig, Text("seq_table.c")) == (0, 1) && vm.ErrorCount == 0,
            $"2줄에 지연 ({S.SelectedStep?.Ordinal}, {Diff(seqOrig, Text("seq_table.c"))}, {vm.ErrorCount})");
        var (chg, chgDone) = await Open<StepVm>(() => S.ChangeStepAsync());
        Check(chg.Category == "대기" && chg.Command.Entry.Name == "DELAY", "지금 명령 골라 둠");
        chg.Command = chg.Commands.First(c => c.Entry.Name == "WAIT_STABLE");
        Check(chg.Preview == "WAIT_STABLE( NO_IN, 100 )", $"시간 옮김 {chg.Preview}");
        chg.Ok.Execute(null);
        await chgDone;
        await Idle();
        Check(S.SelectedStep?.Piece.Text.Trim() == "WAIT_STABLE( NO_IN, 100 )" && vm.ErrorCount == 0, $"바꿈 ({S.SelectedStep?.Piece.Text.Trim()})");
        S.MoveStep(S.SelectedStep!, null);
        await Idle();
        Check(S.Steps.Last(r => r.Ordinal > 0).Block.Head == "유지 대기" && S.SelectedStep?.Block.Head == "유지 대기", "끝으로 끌기");
        S.SelectStep(S.Steps.First(r => r.Label == "RUN"), true);
        var (ask, askDone) = await Open<AskVm>(() => S.DeleteStepAsync());
        Check(ask.Title == "지울 수 없음" && ask.Lines.Count == 2, $"라벨 RUN 막힘 ({ask.Title})");
        await Shot("seq_label_block.png");
        ask.Close("ok");
        await askDone;
        Check(vm.ChangeCount == changes + 3, $"변경 3 ({vm.ChangeCount - changes})");
        S.SelectStep(S.Steps.Last(r => r.Ordinal > 0), true);
        await S.DeleteStepAsync();
        await Idle();
        Check(Text("seq_table.c") == seqOrig && vm.ChangeCount == changes + 4, "지우니 원문 그대로");

        Step("시퀀스 켬/끔: 회전 자동 출발 OFF → ON(원문 0 → 1)");
        S.SelectTable("box_turn_unload");
        var turn = S.Steps.First(r => r.Block.Head == "회전");
        Check(turn.Block.Parts.Any(p => p.Value == "OFF" && p.On == false), "칩 OFF");
        S.SelectStep(turn, true);
        var au = S.Fields.First(f => f.Label == "자동 출발");
        Check(au.IsList && au.Text == "OFF", "ON·OFF 드롭다운");
        au.Picked = au.Items!.Cast<Choice>().First(c => c.Name == "ON");
        await Idle();
        Check(Text("seq_table.c").Contains("AGV_TURN( CW,  10, 0, 1, 0, ALIGN )") && Diff(seqOrig, Text("seq_table.c")) == (1, 1), "원문 1");
        await Shot("seq_chips.png");

        Step("시퀀스 표: 추가 → belt_load 지우기 막힘 → 새 표 지우기(확인 팝업, 기본 취소) → 다시 추가 → 저장 바이트");
        async Task AddTable(string name)
        {
            var (tb, tbDone) = await Open<SeqTableVm>(() => S.AddTableAsync());
            tb.Name = name;
            tb.Profile = "PF_BOX";
            tb.Id = "SEQ_LOAD";
            if (name == "demo_new") await Shot("seq_table_add.png");
            tb.Ok.Execute(null);
            await tbDone;
            await Idle();
        }
        await AddTable("demo_new");
        Check(S.SelectedTable == "demo_new" && S.Steps.Count == 1 && vm.ErrorCount == 0 && vm.Site!.SeqMap.Any(m => m.Table == "demo_new"), $"새 표·대응표 ({S.SelectedTable}, {vm.ErrorCount})");
        S.SelectTable("belt_load");
        var (blk, blkDone) = await Open<AskVm>(() => S.DeleteTableAsync());
        Check(blk.Title == "지울 수 없음", $"참조 막힘 ({blk.Title}: {string.Join(" / ", blk.Lines)})");
        await Shot("seq_delete_block.png");
        blk.Close("ok");
        await blkDone;
        S.SelectTable("demo_new");
        var (cf, cfDone) = await Open<AskVm>(() => S.DeleteTableAsync());
        Check(cf.Title == "표 삭제" && cf.Buttons.First(b => b.Default).Key == "cancel", "확인 팝업·기본 취소");
        cf.Close("cancel");
        await cfDone;
        Check(vm.Site!.Seqs.Any(t => t.Name == "demo_new"), "취소면 그대로");
        (cf, cfDone) = await Open<AskVm>(() => S.DeleteTableAsync());
        cf.Close("delete");
        await cfDone;
        await Idle();
        Check(!vm.Site!.Seqs.Any(t => t.Name == "demo_new") && !vm.Site.SeqMap.Any(m => m.Table == "demo_new") && Diff(seqOrig, Text("seq_table.c")) == (1, 1), "표·대응표 줄 지움");
        await AddTable("demo_new2");
        var bytes = vm.Site!.File("seq_table.c")!.ToBytes();
        bool saved = await vm.SaveAsync();
        await Idle();
        var disk = File.ReadAllBytes(Path.Combine(site, "seq_table.c"));
        Check(saved && disk.AsSpan().SequenceEqual(bytes) && Diff(seqOrig, System.Text.Encoding.UTF8.GetString(disk)) == (1, 6),
            $"저장 바이트 = 편집본, 원문과 차이 (1, 6) {Diff(seqOrig, System.Text.Encoding.UTF8.GetString(disk))}");

        await ReviewFixes();
    }

    /// <summary>검토 5~8: 값 버림 팝업, 명령 바꾸기 주석 확인·버릴 인자, 원문 줄 자리·같은 자리 끌기, 짝 표지·끝 줄·원문 줄 지우기 팝업.</summary>
    async Task ReviewFixes()
    {
        var S = vm.Seq;
        int changes = vm.ChangeCount;

        Step("값 버림 팝업: 태그 6 AGV_STOP( 30 ) → TAPE(30 버림) 취소 → 바꾸기");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        await PickBlock(6, "ANY_PROFILE", 0);
        var tagBefore = TagText;
        var pos = Tag.Fields.First(f => f.Label == "위치");
        var (drop, dropDone) = await Open<AskVm>(() => { pos.Picked = pos.Items!.Cast<Choice>().First(c => c.Name == "TAPE"); return Task.CompletedTask; });
        Check(drop.Title == "값 버림" && drop.Lines.Contains("30") && drop.Buttons.First(b => b.Default).Key == "cancel", $"버릴 인자 팝업 ({string.Join(" / ", drop.Lines)})");
        await Shot("tag_drop_ask.png");
        drop.Close("cancel");
        await Idle();
        Check(TagText == tagBefore && Tag.Fields.First(f => f.Label == "위치").Text == "cm", "취소면 그대로·칸 cm");
        pos = Tag.Fields.First(f => f.Label == "위치");
        (drop, dropDone) = await Open<AskVm>(() => { pos.Picked = pos.Items!.Cast<Choice>().First(c => c.Name == "TAPE"); return Task.CompletedTask; });
        drop.Close("ok");
        await Idle();
        Check(Row(6).Rule!.St.Text.Contains("AGV_STOP( TAPE ),"), "바꾸기면 TAPE");

        Step("명령 바꾸기: 주석 확인(주석 그대로·칸 표시), 버릴 인자 보임, 라벨 줄 바꾸기 막힘");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.SeqTab);
        S.SelectTable("belt_unload");
        var off = (Statement)S.Steps.First(r => r.Piece.Text.Contains("BELT_OFF( 1 )")).Piece;
        vm.Apply(SiteEdit.SetComment(vm.Site!, off, "벨트 끔"));
        await Idle();
        S.SelectStep(S.Steps.First(r => r.Piece.Text.Contains("BELT_OFF( 1 )")), true);
        var (chg, chgDone) = await Open<StepVm>(() => S.ChangeStepAsync());
        Check(chg.HasComment && chg.Comment == "벨트 끔" && !chg.HasDrops, "주석 확인 · 버릴 것 없음");
        chg.Command = chg.Commands.First(c => c.Entry.Name == "BELT_ON");
        await Shot("seq_comment_check.png");
        chg.Ok.Execute(null);
        await chgDone;
        await Idle();
        var on = S.Steps.First(r => r.Piece.Text.Contains("BELT_ON( 1, CW )"));
        Check(on.Piece.Text.Contains("/* 벨트 끔 */") && on.CommentCheck, $"주석 그대로·주석 확인 표시 ({on.Piece.Text.Trim()})");
        S.SelectTable("box_turn_unload");
        S.SelectStep(S.Steps.First(r => r.Piece.Text.Contains("AGV_MOVE( 200, TAPE_GD )")), true);
        (chg, chgDone) = await Open<StepVm>(() => S.ChangeStepAsync());
        chg.Category = "대기";
        chg.Command = chg.Commands.First(c => c.Entry.Name == "DELAY");
        Check(chg.HasDrops && chg.Drops.Contains("200") && chg.Drops.Contains("TAPE_GD"), $"버릴 인자 {chg.Drops}");
        chg.Close(null);
        await chgDone;
        S.SelectTable("belt_unload");
        S.SelectStep(S.Steps.First(r => r.Label == "RUN"), true);
        var (lab, labDone) = await Open<AskVm>(() => S.ChangeStepAsync());
        Check(lab.Title == "바꿀 수 없음", "라벨 줄 바꾸기 막힘");
        lab.Close("ok");
        await labDone;

        Step("원문 줄 자리·같은 자리 끌기");
        var pulse = (Statement)S.Steps.First(r => r.Piece.Text.Contains("OUT_PULSE")).Piece;
        vm.Apply(SiteEdit.ReplaceText(vm.Site!, pulse, "        OUT_PULSE( /* 원문 */ DEMO_LAMP, 50, 50, 3 )"));
        await Idle();
        var raw = S.Steps.First(r => r.IsRaw);
        int nextAfterRaw = S.Steps.SkipWhile(r => !ReferenceEquals(r, raw)).First(r => r.Ordinal > 0).Ordinal;
        int c0 = vm.ChangeCount;
        var first = S.Steps.First(r => r.Ordinal == 1);
        S.MoveStep(first, S.Steps.First(r => r.Ordinal == 2));
        Check(vm.ChangeCount == c0, "같은 자리 끌기 = 단계 없음");
        var mover = S.Steps.Last(r => r.Ordinal > 0 && r.Piece.Text.Contains("AGV_FORBACK"));
        S.MoveStep(mover, raw);
        await Idle();
        var moved = S.Steps.First(r => r.Piece.Text.Contains("AGV_FORBACK"));
        Check(vm.ChangeCount == c0 + 1 && moved.Ordinal == nextAfterRaw && S.Steps.Last(r => r.Ordinal > 0).Block.Head == "끝·재출발",
            $"원문 줄 위에 놓으면 다음 문장 앞 ({moved.Ordinal}, {nextAfterRaw})");

        Step("지우기 팝업: 고르기 시작·끝 짝째, 끝 줄, 원문 줄");
        S.SelectStep(S.Steps.First(r => r.Block.Head == "고르기 시작(대기)"), true);
        var (pair, pairDone) = await Open<AskVm>(() => S.DeleteStepAsync());
        Check(pair.Title == "스텝 삭제" && pair.Lines[0].Contains("짝") && pair.Buttons.First(b => b.Default).Key == "cancel", $"짝 팝업 ({pair.Lines[0]})");
        await Shot("seq_pair_delete.png");
        pair.Close("delete");
        await pairDone;
        await Idle();
        Check(!S.Steps.Any(r => r.Block.Head is "고르기 시작(대기)" or "고르기 끝") && vm.ErrorCount == 0, $"짝 둘 다 지움·오류 0 ({vm.ErrorCount})");
        S.SelectStep(S.Steps.Last(r => r.Ordinal > 0), true);
        var (endAsk, endDone) = await Open<AskVm>(() => S.DeleteStepAsync());
        Check(endAsk.Lines[0].StartsWith("끝 줄", StringComparison.Ordinal), "끝 줄 팝업");
        int c1 = vm.ChangeCount;
        endAsk.Close("cancel");
        await endDone;
        S.SelectStep(S.Steps.First(r => r.IsRaw), true);
        var (rawAsk, rawDone) = await Open<AskVm>(() => S.DeleteStepAsync());
        Check(rawAsk.Lines[0].StartsWith("원문", StringComparison.Ordinal), "원문 줄 팝업");
        rawAsk.Close("cancel");
        await rawDone;
        Check(vm.ChangeCount == c1, "취소면 그대로");
        Step("A4 형식 칸: 대기 제한 시간 → 파라미터(버림 팝업·취소) → 없음 → 시간, 인자 보존·원문 바이트");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.SeqTab);
        S.SelectTable("belt_load");
        var formBase = Text("seq_table.c");
        S.SelectStep(S.Steps.First(r => r.Piece.Text.Contains("WAIT_TO( IN( ON(DEMO_IF_IN1) ), 3000, ERR )")), true);
        await Idle();
        var form = S.Fields[0];
        Check(form.Label == "형식" && form.Info.Value == "시간" && form.Info.Spec.Words!.SequenceEqual(["없음", "시간", "상태", "상태·시간", "상태·입력", "상태·입력·시간", "하나라도 2", "하나라도 2·시간", "파라미터"]), $"형식 칸 {form.Label} {form.Info.Value} {string.Join("/", form.Info.Spec.Words!)}");
        var (fAsk, fAskDone) = await Open<AskVm>(() => { form.Picked = form.Items!.Cast<Choice>().First(c => c.Name == "파라미터"); return Task.CompletedTask; });
        Check(fAsk.Title == "값 버림" && fAsk.Lines.Contains("3000"), $"시간 값 버림 팝업 ({string.Join(" / ", fAsk.Lines)})");
        fAsk.Close("cancel");
        await fAskDone;
        await Idle();
        Check(Text("seq_table.c") == formBase && S.Fields[0].Text == "시간", "취소면 그대로·칸 시간");
        form = S.Fields[0];
        (fAsk, fAskDone) = await Open<AskVm>(() => { form.Picked = form.Items!.Cast<Choice>().First(c => c.Name == "파라미터"); return Task.CompletedTask; });
        fAsk.Close("ok");
        await fAskDone;
        await Idle();
        var fNow = S.SelectedStep!.Piece.Text.Trim();
        Check(fNow.StartsWith("WAIT_TO_PARAM( IN( ON(DEMO_IF_IN1) ), ", StringComparison.Ordinal) && fNow.EndsWith(", ERR )", StringComparison.Ordinal) && vm.ErrorCount == 0 && Diff(formBase, Text("seq_table.c")) == (1, 1),
            $"조건·라벨 보존, 그 줄만 바뀜 ({fNow}, {Diff(formBase, Text("seq_table.c"))}, {vm.ErrorCount})");
        form = S.Fields[0];
        (fAsk, fAskDone) = await Open<AskVm>(() => { form.Picked = form.Items!.Cast<Choice>().First(c => c.Name == "없음"); return Task.CompletedTask; });
        Check(fAsk.Lines.Count == 4 && fAsk.Lines.Contains("ERR"), $"파라미터·단위·라벨 버림 ({string.Join(" / ", fAsk.Lines)})");
        fAsk.Close("ok");
        await fAskDone;
        await Idle();
        Check(S.SelectedStep!.Piece.Text.Trim() == "WAIT( IN( ON(DEMO_IF_IN1) ) )", $"없음 ({S.SelectedStep.Piece.Text.Trim()})");
        S.Fields[0].Picked = S.Fields[0].Items!.Cast<Choice>().First(c => c.Name == "시간");
        await Idle();
        Check(S.SelectedStep!.Piece.Text.Trim().StartsWith("WAIT_TO( IN( ON(DEMO_IF_IN1) ), ", StringComparison.Ordinal) && Diff(formBase, Text("seq_table.c")).Added == 1, $"버릴 것 없으면 팝업 없이 시간 ({S.SelectedStep.Piece.Text.Trim()})");

        Step("A5·A6 스텝 추가 팝업: 무리마다 하나·인자 설명·검색");
        var (add2, add2Done) = await Open<StepVm>(() => S.AddStepAsync());
        add2.Category = "대기";
        Check(add2.Commands.Select(c => c.Entry.Family).Distinct().Count() == add2.Commands.Count, "무리마다 하나");
        Check(add2.Commands.Any(c => c.Entry.Name == "WAIT") && !add2.Commands.Any(c => c.Entry.Name is "WAIT_TO" or "WAIT_TO_PARAM" or "DELAY_PARAM" or "WAIT_STABLE_TO"), "대기 목록에 형식 변형 없음");
        add2.Command = add2.Commands.First(c => c.Entry.Name == "DELAY");
        Check(add2.Note == "시간 틱 0~65535", $"인자 설명 ({add2.Note})");
        await Shot("seq_add.png");
        add2.Search = "대기";
        Check(add2.IsSearching && add2.Commands.Any(c => c.Entry.Name == "WAIT") && add2.Commands.Any(c => c.Entry.Name == "WAIT_STABLE") && add2.Commands.Any(c => c.Entry.Name == "WAIT_BELTS")
            && !add2.Commands.Any(c => c.Entry.Name is "OUT_ON" or "L"), $"검색 대기 ({add2.Commands.Count})");
        add2.Search = "WAIT_TO";
        Check(add2.Commands.Count == 1 && add2.Commands[0].Entry.Family == "WAIT", $"검색 WAIT_TO → 그 무리 ({string.Join(",", add2.Commands.Select(c => c.Entry.Name))})");
        add2.Search = "분기";
        Check(add2.Commands.Any(c => c.Entry.Name == "WHEN") && add2.Commands.Any(c => c.Entry.Name == "GOTO") && add2.Commands.All(c => c.Family!.Any(e => e.Palette == "분기" || e.Head.Contains("분기") || e.Note.Contains("분기"))), $"검색 분류 이름 ({add2.Commands.Count})");
        add2.Search = "";
        Check(!add2.IsSearching && add2.Commands.All(c => (c.Entry.Palette.Length > 0 ? c.Entry.Palette : "기타") == add2.Category), "검색어 지우면 분류");
        add2.Close(null);
        await add2Done;

        Step("A5·A6 태그 동작 추가 팝업(메뉴 대신)");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        var (tadd, taddDone) = await Open<StepVm>(() => Tag.AddActionAsync(Row(28)));
        Check(tadd.Categories.Count == 8 && tadd.Categories.SequenceEqual(TableEditor.Engine.Grammar.GrammarTable.TagPalette), $"태그 분류 {string.Join("/", tadd.Categories)}");
        tadd.Category = "정지";
        Check(tadd.Commands.Count(c => c.Entry.Name == "AGV_STOP") == 1 && tadd.Commands[0].Entry.Name == "AGV_STOP" && tadd.Note.Length == 0, $"정지 무리 하나 ({tadd.Commands.Count})");
        tadd.Category = "속도·방향";
        tadd.Command = tadd.Commands.First(c => c.Entry.Name == "AGV_SPEED");
        Check(tadd.Note == "단계 HIGH·LOW·LLOW·MIN", $"속도 설명 ({tadd.Note})");
        await Shot("tag_action_add.png");
        tadd.Close(null);
        await taddDone;
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.SeqTab);
        while (vm.ChangeCount > changes && vm.Undo.CanExecute(null)) { vm.Undo.Execute(null); await Idle(); }
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
