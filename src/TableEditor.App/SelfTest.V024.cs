using System.IO;
using TableEditor.App.Blocks;
using TableEditor.App.ViewModels;
using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.App;

/// <summary>
/// 자체 시험 v0.2.4: 코어 v0.60.0·v0.61.0·v0.63.0 새 문법. 지어낸 한 벌(tests/fixtures/T900_DEMO/dsl_v063)을 지어낸 core(VERSION v0.63.0)로 열어
/// 읽기·왕복·칩, 상태 칸 조건 팝업, 새 스텝 추가·묶음 유지 가지 셋째 막힘, 수동 표 묶음·입력 줄·걸러 보기, 프로파일 후진 FL, 옛 코어에서 숨김·V-4 를 본다.
/// 캡처: tag_cond_state.png, seq_hold.png, manual_v063.png, manual_input_add.png, profile_fl.png.
/// </summary>
public sealed partial class SelfTest
{
    async Task V024()
    {
        var dir = Path.Combine(Path.GetFullPath(work), "dsl_v063");
        Clear(dir);
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(Path.Combine(RepoRoot(), "tests", "fixtures", "T900_DEMO", "dsl_v063")))
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        var core63 = FakeCore(Path.Combine(Path.GetFullPath(work), "dsl_v063_core"), "v0.63.0");

        Step("24 코어 v0.63.0 새 문법 한 벌: 읽기 · 바이트 왕복 · 오류 0 · 원문 블록 없음 · 칩");
        await vm.TryOpen(() => vm.OpenSiteAsync(dir, core63));
        await Idle();
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        await Idle();
        Check(vm.CoreVersion == "v0.63.0" && vm.ErrorCount == 0, $"오류 0 ({vm.CoreVersion}, {vm.ErrorCount}: {string.Join(" | ", vm.Findings.Where(f => f.IsError).Take(3).Select(f => f.Text))})");
        Check(vm.Site!.Files.All(f => f.ToBytes().AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dir, f.Name)))), "읽은 그대로 쓰면 바이트 같음");
        Check(vm.Site.Files.All(f => !f.RawInZones.Any()), "표 구역 안 원문 블록 0(새 문법이 문장으로 읽힘)");
        var r71 = Row(71);
        Check(r71.Chips.Select(c => c.Head).First() == "상태 칸 값" && r71.Chips.Select(c => c.Head).Contains("반전"), $"STATE 칩 ({string.Join("/", r71.Chips.Select(c => c.Head))})");
        Check(Row(72).Blocks.Select(b => b.Head).SequenceEqual(["상태 설정", "보고"]) && Row(72).CellCount == 4 && Row(74).CellCount == 3 && Row(73).Blocks.All(b => b.Head == "보고"), "태그 SET_STATE 2칸 · ACS CROSS 보고 블록");

        Step("24 상태 칸 조건 팝업: 종류 STATE 둘(값·범위) · 반전 · 값 고치기 · 한 줄만 바뀜");
        var tagBase = TagText;
        var (cond, done) = await Open<CondVm>(() => { Tag.SelectOnly(r71); return vm.EditConditionAsync(r71); });
        Check(cond.XKinds.Count(k => k.Name == "STATE") == 2 && cond.XRows.Count == 2 && cond.XRows[0].Kind.Name == "STATE" && cond.XRows[1].Negate && cond.XRows[1].Kind.Args.Count == 3,
            $"STATE 두 형식 · 읽기 ({string.Join(",", cond.XRows.Select(x => x.Kind.Name))})");
        cond.XRows[0].Args[1].Text = "4";
        await Shot("tag_cond_state.png");
        cond.Ok.Execute(null);
        await done;
        await Idle();
        var t71 = Row(71).Rule!.St.Args[2].Text;
        Check(t71.Contains("STATE(2, 4)") && t71.Contains("NOT(STATE(3, 4, 9))") && Diff(tagBase, TagText) == (1, 1) && vm.ErrorCount == 0, $"값 고침 한 줄 ({t71})");
        vm.Undo.Execute(null);
        await Idle();
        Check(TagText == tagBase, "되돌리면 바이트 같음");

        Step("24 시퀀스: 새 스텝 읽기 · 형식 칸 · 추가 목록 · 묶음 유지 가지 셋째 = S-19 · 저장 막힘");
        var S = vm.Seq;
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.SeqTab);
        S.SelectTable("hold_demo");
        await Idle();
        var heads = S.Steps.Where(r => r.Ordinal > 0).Select(r => r.Block.Head).ToList();
        Check(new[] { "누적 대기", "누적 대기·제한", "유지 대기(파라미터)", "WHEN 유지", "WHEN 누적(파라미터)", "WHEN 출력", "보고" }.All(heads.Contains), $"새 스텝 머리 ({string.Join("/", heads.Distinct())})");
        S.SelectStep(S.Steps.First(r => r.Piece.Text.Contains("WAIT_ACCUM( IN( ON(DEMO_SEN_1) ), 450 )")), true);
        await Idle();
        var form = S.Fields[0];
        Check(form.Label == "형식" && form.Text == "없음" && form.Info.Spec.Words!.SequenceEqual(["없음", "시간", "파라미터", "시간·파라미터"]), $"누적 대기 형식 칸 ({form.Label}={form.Text}: {string.Join("/", form.Info.Spec.Words!)})");
        S.SelectStep(S.Steps.First(r => r.Piece.Text.Contains("WAIT_STABLE( IN( ON(DEMO_SEN_3) ), PARAM_TIME( AD, DSEC ) )")), true);
        await Idle();
        Check(S.Fields[0].Text == "파라미터" && S.Fields.Select(f => f.Label).Contains("단위") && S.Fields.Select(f => f.Label).Contains("파라미터"), $"유지 대기 파라미터 형식 · 칸 ({string.Join("/", S.Fields.Select(f => f.Label))})");
        var zoneH = vm.Site.ZonesOf("SEQ_BEGIN").First(z => z.Name == "hold_demo");
        var (add, addDone) = await Open<StepVm>(() => S.AddStepAsync());
        add.Category = "대기";
        Check(add.Commands.Any(c => c.Entry.Name == "WAIT_ACCUM"), "추가 목록 대기: 누적 대기");
        add.Category = "분기";
        Check(add.Commands.Any(c => c.Entry.Name == "WHEN_STABLE") && add.Commands.Any(c => c.Entry.Name == "WHEN_ACCUM") && add.Commands.Any(c => c.Entry.Name == "WHEN_OUT"), "추가 목록 분기: WHEN 유지·누적·출력");
        add.Category = "대기";
        add.Command = add.Commands.First(c => c.Entry.Name == "WAIT_ACCUM");
        Check(add.Preview == "WAIT_ACCUM( NO_IN, 100 )", $"미리 보기 {add.Preview}");
        add.Ok.Execute(null);
        await addDone;
        await Idle();
        Check(S.SelectedStep?.Piece.Text.Trim() == "WAIT_ACCUM( NO_IN, 100 )" && vm.ErrorCount == 0, $"새 스텝 추가 ({S.SelectedStep?.Piece.Text.Trim()}, 오류 {vm.ErrorCount})");
        vm.Undo.Execute(null);
        await Idle();
        var seqBase = Text("seq_table.c");
        await Shot("seq_hold.png");
        var zone = vm.Site.ZonesOf("SEQ_BEGIN").First(z => z.Name == "hold_demo");
        int at = zone.Ordinal(zone.Statements.First(s => s.Name == "WHEN_OUT"));
        vm.Apply(SiteEdit.Insert(vm.Site, zone, at, [new NewStatement("WHEN_STABLE", ["IN( ON(DEMO_SEN_1) )", "10", "DONE"])]));
        await Idle();
        var s19 = vm.Findings.FirstOrDefault(f => f.Finding.Rule == "S-19");
        Check(s19 != null && s19.IsError && s19.Text.Contains("290"), $"S-19 ({s19?.Text})");
        var disk = File.ReadAllText(Path.Combine(dir, "seq_table.c"));
        bool saved = await vm.SaveAsync();
        Check(!saved && File.ReadAllText(Path.Combine(dir, "seq_table.c")) == disk, "저장 막힘 · 디스크 그대로");
        vm.Undo.Execute(null);
        await Idle();
        Check(vm.ErrorCount == 0 && Text("seq_table.c") == seqBase, "되돌리면 오류 0 · 바이트 같음");

        Step("24 수동 화면: 묶음 줄·입력 줄 · 걸러 보기(묶음 = 프로파일 합집합) · 모드 목록 · 입력 추가");
        var M = vm.Manual;
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.ManualTab);
        await Idle();
        Check(M.Cards.Select(c => c.Profile).SequenceEqual(["PF_BELT", "PF_LIFT", "PF_BOX", "PROFILE_SET( 0 )"]) && M.Cards[3].Mode == "ANY", $"카드 ({string.Join(" · ", M.Cards.Select(c => c.Profile + " " + c.Mode))})");
        Check(M.Inputs.Select(r => r.St.Name).SequenceEqual(["MAN_PROFILES", "MAN_PROFILES", "MAN_FB_SW", "MAN_OBS_B", "MAN_START_SW", "MAN_FB_SW"]), $"입력 줄 ({string.Join(" ", M.Inputs.Select(r => r.St.Name))})");
        Check(M.ModeChoices.SequenceEqual(["IDLE", "JOG", "ANY"]) && M.Cards[0].ModeChoices.Contains("ANY"), "모드 목록: v0.63.0 은 ANY 도");
        Check(M.Filters.SequenceEqual([TagVm.AllProfiles, "PF_BELT", "PF_LIFT", "PF_BOX", "PF_CART"]), $"거르기 목록 ({string.Join("/", M.Filters)})");
        M.Filter = "PF_CART";
        Check(M.Cards.Select(c => c.Profile).SequenceEqual(["PROFILE_SET( 0 )"]) && M.Inputs.Select(r => r.St.Args[0].Text).SequenceEqual(["PROFILE_SET( 0 )"]), $"걸러 보기 PF_CART: 묶음 카드 · 묶음 줄 ({string.Join(" · ", M.Cards.Select(c => c.Profile))} | {string.Join(" · ", M.Inputs.Select(r => r.St.Name))})");
        M.Filter = "PF_LIFT";
        Check(M.Cards.Select(c => c.Profile).SequenceEqual(["PF_LIFT"]) && M.Inputs.Select(r => r.St.Name).SequenceEqual(["MAN_PROFILES", "MAN_OBS_B", "MAN_FB_SW"]), $"걸러 보기 PF_LIFT: 묶음 1 · 후방 · 방향(묶음 열쇠) ({string.Join(" · ", M.Inputs.Select(r => r.St.Name))})");
        M.Filter = "PF_BELT";
        Check(M.Inputs.Select(r => r.St.Name).SequenceEqual(["MAN_FB_SW", "MAN_START_SW"]), $"걸러 보기 PF_BELT: 입력 줄 ({string.Join(" · ", M.Inputs.Select(r => r.St.Name))})");
        M.Filter = TagVm.AllProfiles;
        await Idle();
        Check(ManualVm.ScopeOf(vm.Site, vm.Site.ManualRows.First(r => r.Name == "MAN_FB_SW" && r.Args[0].Text.StartsWith("PROFILE_SET", StringComparison.Ordinal))).Profiles!.SetEquals(["PF_LIFT"]), "신호 범위: 묶음 열쇠 = 그 프로파일들");
        vm.ClearNotice(); // 앞 단계 저장 막힘 알림(4초 칩)이 위 줄에 남아 아래 검사 목록과 다르게 보이지 않게
        Check(vm.Notice == null && vm.ErrorCount == 0, $"캡처 전 알림 없음 · 오류 0 ({vm.Notice}, {vm.ErrorCount})");
        await Shot("manual_v063.png");
        var manBase = Text("manual_table.c");
        var (ask, askDone) = await Open<AskVm>(() => { M.AddInput.Execute(null); return Task.CompletedTask; });
        Check(ask.Buttons.Select(b => b.Key).SequenceEqual(["cancel", "MAN_FB_SW", "MAN_OBS_B", "MAN_START_SW", "MAN_PROFILES"]), $"입력 추가 팝업 ({string.Join("/", ask.Buttons.Select(b => b.Key))})");
        await Shot("manual_input_add.png");
        ask.Close("MAN_PROFILES");
        await askDone;
        await Idle();
        var first = vm.Site.ManualRows[0];
        Check(first.Name == "MAN_PROFILES" && first.Args[0].Text == "PROFILE_SET( 2 )" && vm.ErrorCount == 0 && Diff(manBase, Text("manual_table.c")) == (0, 1), $"묶음 줄: 표 맨 앞 · 빈 번호 2 ({first.Text.Trim()}, 오류 {vm.ErrorCount})");
        vm.Undo.Execute(null);
        await Idle();
        var (ask2, askDone2) = await Open<AskVm>(() => { M.AddInput.Execute(null); return Task.CompletedTask; });
        ask2.Close("MAN_START_SW");
        await askDone2;
        await Idle();
        var last = vm.Site.ManualRows[^1];
        Check(last.Name == "MAN_START_SW" && last.Args.Count == 2 && vm.ErrorCount == 0, $"시작 스위치 줄: 표 끝 ({last.Text.Trim()}, 오류 {vm.ErrorCount})");
        Check(vm.Site.Names.Find(last.Args[1].Text) is { Kind: NameKind.Input, Value: not 16 } && vm.Site.Names.Of(NameKind.Input).Any(d => !d.Core), $"기본 입력은 평범한 입력 별칭 ({last.Args[1].Text})");
        vm.Undo.Execute(null);
        await Idle();
        Check(Text("manual_table.c") == manBase, "되돌리면 바이트 같음");
        var (cardAdd, cardDone) = await Open<CardAddVm>(() => { M.AddCard.Execute(null); return Task.CompletedTask; });
        Check(cardAdd.AnyOk, "카드 추가 팝업: ANY 모드 고름");
        cardAdd.Cancel.Execute(null);
        await cardDone;
        await Idle();

        Step("24 프로파일 화면: 후진 FL 표시 · 켜고 끄기 · 프로파일 바꾸기가 FL 을 지키지 않는 일이 없다");
        var P = vm.Profile;
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.ProfileTab);
        await Idle();
        Check(P.Rows[0].BackFl && P.Rows[0].Profile == "PF_BELT" && !P.Rows[1].BackFl && P.Rows[1].Profile == "PF_BELT", $"FL 줄 표시 ({string.Join(" · ", P.Rows.Select(r => r.Profile + (r.BackFl ? " FL" : "")))})");
        P.Select(P.Rows[1], false);
        await Idle();
        Check(P.CanBackFl, "[후진 FL] 보임(v0.63.0)");
        var profBase = Text("profiles.c");
        P.ToggleBackFl.Execute(null);
        await Idle();
        Check(vm.Site.ProfileRows[1].BackFl && vm.Site.ProfileRows[1].St.Args[0].Text == "STOP_BACK_FL( PF_BELT )" && vm.ErrorCount == 0 && Diff(profBase, Text("profiles.c")) == (1, 1), $"켬 ({vm.Site.ProfileRows[1].St.Args[0].Text})");
        await Shot("profile_fl.png");
        P.Select(P.Rows[1], false);
        P.SetProfile(P.Rows[1], "PF_LIFT");
        await Idle();
        Check(vm.Site.ProfileRows[1].St.Args[0].Text == "STOP_BACK_FL( PF_LIFT )", $"프로파일을 바꿔도 FL 유지 ({vm.Site.ProfileRows[1].St.Args[0].Text})");
        vm.Undo.Execute(null);
        await Idle();
        P.Select(P.Rows[1], false);
        P.ToggleBackFl.Execute(null);
        await Idle();
        Check(!vm.Site.ProfileRows[1].BackFl && Text("profiles.c") == profBase, "끔 = 원래 바이트");
        while (vm.Undo.CanExecute(null)) vm.Undo.Execute(null);
        await Idle();

        Step("24 쓰는 곳 목록: 코어 버전별 낱말(보고 종류 · 모드) · 이름 목록");
        var names = vm.Site.Names;
        IEnumerable<string> Words(ArgSpec s) => Choices.For(s, names).Select(c => c.Name);
        var acsKinds = GrammarTable.Find("ACS", Place.Step).Single().Args[0];
        Check(new[] { "IDLE", "DISPLAY", "CROSS_IN", "CROSS_OUT" }.All(Words(acsKinds).Contains) && names.Find("AD") != null, "v0.63.0: IDLE·DISPLAY·CROSS_IN·CROSS_OUT · 파라미터 AD");

        Step("24 옛 코어 v0.59.0: 새 문법이 목록에 없고 쓴 줄은 V-4");
        var core59 = FakeCore(Path.Combine(Path.GetFullPath(work), "dsl_v063_core59"), "v0.59.0");
        await vm.TryOpen(() => vm.OpenSiteAsync(dir, core59));
        await Idle();
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        await Idle();
        var v4 = vm.Findings.Where(f => f.Finding.Rule == "V-4").ToList();
        Check(v4.Count >= 10 && v4.All(f => f.IsError) && v4.Any(f => f.Text.Contains("MAN_START_SW")) && v4.Any(f => f.Text.Contains("WAIT_ACCUM")) && v4.Any(f => f.Text.Contains("STATE")) && v4.Any(f => f.Text.Contains("SET_STATE")),
            $"V-4 {v4.Count}줄 ({string.Join(" | ", v4.Take(4).Select(f => f.Text))})");
        Check(!await vm.SaveAsync(), "V-4 오류로 저장 막힘");
        names = vm.Site!.Names;
        Check(!Words(acsKinds).Contains("DISPLAY") && !Words(acsKinds).Contains("CROSS_IN") && Words(acsKinds).Contains("IDLE") && names.Find("AD") == null, "v0.59.0: 보고 종류에 DISPLAY·CROSS 없음 · IDLE 있음 · AD 없음");
        Check(!Tag.ActionChoices(Row(28)).Any(c => c.Entry.Name == "SET_STATE"), "태그 동작 추가 목록에 SET_STATE 없음");
        var (old, oldDone) = await Open<StepVm>(() => { vm.Tab = vm.Tabs.First(t => t.Name == MainVm.SeqTab); vm.Seq.SelectTable("hold_demo"); return vm.Seq.AddStepAsync(); });
        var all = old.Categories.SelectMany(c => { old.Category = c; return old.Commands; }).Select(c => c.Entry.Name).ToList();
        Check(!all.Any(n => n is "WAIT_ACCUM" or "WHEN_STABLE" or "WHEN_ACCUM" or "WHEN_OUT") && all.Contains("WAIT_STABLE") && all.Contains("WHEN"), "스텝 추가 목록에 새 스텝 없음");
        old.Close(null);
        await oldDone;
        var (cond59, done59) = await Open<CondVm>(() => { vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab); var r = Row(71); Tag.SelectOnly(r); return vm.EditConditionAsync(r); });
        Check(!cond59.XKinds.Any(k => k.Name == "STATE" && !cond59.XRows.Any(x => x.Kind.Name == "STATE")) || cond59.XRows.Any(x => x.Kind.Name == "STATE"), "조건 팝업 종류(쓴 줄의 종류는 남긴다)");
        cond59.Cancel.Execute(null);
        await done59;
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.ManualTab);
        await Idle();
        Check(vm.Manual.ModeChoices.SequenceEqual(["IDLE", "JOG"]) && vm.Manual.Cards[3].ModeChoices.Contains("ANY"), "모드 목록: v0.59.0 은 ANY 없음(쓴 줄의 ANY 는 남김)");
        var (ask59, ask59Done) = await Open<AskVm>(() => { vm.Manual.AddInput.Execute(null); return Task.CompletedTask; });
        Check(ask59.Buttons.Select(b => b.Key).SequenceEqual(["cancel", "MAN_FB_SW", "MAN_OBS_B"]), $"v0.59.0 입력 추가 팝업 ({string.Join("/", ask59.Buttons.Select(b => b.Key))})");
        ask59.Close("cancel");
        await ask59Done;
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.ProfileTab);
        await Idle();
        vm.Profile.Select(vm.Profile.Rows[1], false);
        Check(!vm.Profile.CanBackFl && vm.Profile.Rows[0].BackFl, "프로파일 화면: v0.59.0 은 [후진 FL] 숨김(쓴 줄 표시는 남김)");

        // 제자리: 기본 사이트로
        await vm.TryOpen(() => vm.OpenSiteAsync(site));
        await Idle();
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        await Idle();
        Clear(dir);
    }
}
