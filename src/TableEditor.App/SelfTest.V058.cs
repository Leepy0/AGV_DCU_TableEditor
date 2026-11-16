using System.IO;
using System.Security.Cryptography;
using System.Text;
using TableEditor.App.Blocks;
using TableEditor.App.ViewModels;
using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.App;

/// <summary>
/// 자체 시험 v0.2.1 B: 코어 v0.58.0 새 DSL 형식. 새 형식 fixture(tests/fixtures/T900_DEMO/dsl_v058)를 지어낸 core(VERSION v0.58.0)로 열어
/// 읽기·칩, 새 형식 조건 편집, 버전별 쓰기(새 코어는 새 형식, 옛 코어는 옛 형식), 새 오류(D-1)와 저장 막힘을 본다.
/// 캡처: tag_cond_and.png, tag_new_dsl.png, seq_new_dsl.png.
/// </summary>
public sealed partial class SelfTest
{
    /// <summary>VERSION 과 MANIFEST.sha256 만 있는 지어낸 core(V-1·V-3 통과, 헤더 없음은 경고).</summary>
    static string FakeCore(string folder, string version)
    {
        Clear(folder);
        Directory.CreateDirectory(folder);
        var text = version + " 0000000 cl2000 v0\n";
        File.WriteAllText(Path.Combine(folder, "VERSION"), text, Utf8);
        File.WriteAllText(Path.Combine(folder, "MANIFEST.sha256"), Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(text))) + "  VERSION\n", Utf8);
        return folder;
    }

    async Task V058()
    {
        var S = vm.Seq;
        var dir = Path.Combine(Path.GetFullPath(work), "dsl_v058");
        Clear(dir);
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(Path.Combine(RepoRoot(), "tests", "fixtures", "T900_DEMO", "dsl_v058")))
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        var core = FakeCore(Path.Combine(Path.GetFullPath(work), "dsl_v058_core"), "v0.58.0");

        Step("B1 새 코어 v0.58.0: 새 형식 fixture 읽기 · 바이트 왕복 · 오류 0 · 칩");
        await vm.TryOpen(() => vm.OpenSiteAsync(dir, core));
        await Idle();
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        await Idle();
        Check(vm.Site!.NewDsl && vm.CoreVersion == "v0.58.0" && vm.ErrorCount == 0, $"새 형식 판단 · 오류 0 ({vm.CoreVersion}, {vm.ErrorCount}: {string.Join(" | ", vm.Findings.Where(f => f.IsError).Take(3).Select(f => f.Text))})");
        Check(vm.Site.Files.All(f => f.ToBytes().AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dir, f.Name)))), "읽은 그대로 쓰면 바이트 같음");
        var r62 = Row(62, "PF_CART");
        Check(r62.Chips.Select(c => c.Head).SequenceEqual(["호기 범위", "스테이션 범위"]), $"AND 안 상태 칩 ({string.Join("/", r62.Chips.Select(c => c.Head))})");
        var r63 = Row(63, "PF_CART");
        Check(r63.Chips.Select(c => c.Head).SequenceEqual(["NOT", "켜짐", "꺼짐"]), $"NOT·켜짐·꺼짐 칩 ({string.Join("/", r63.Chips.Select(c => c.Head))})");
        var r60 = Row(60, "PF_BOX");
        Check(r60.Chips.Select(c => c.Head).SequenceEqual(["하나라도"]), "ANY 칩 = 하나라도");
        await Shot("tag_new_dsl.png");
        var tagBase = TagText;

        Step("B2 새 형식 조건 편집(팝업): 상태 낱말 목록 · AND 읽기 · 다시 쓰기");
        var (cond, done) = await Open<CondVm>(() => { Tag.SelectOnly(r62); return vm.EditConditionAsync(r62); });
        Check(cond.XRows.Count == 2 && cond.XRows[0].Kind.Name == "ID" && cond.XRows[1].Kind.Name == "STATION" && cond.In.Groups.Count == 0, $"AND 를 확장·입력으로 읽음 ({string.Join(",", cond.XRows.Select(x => x.Kind.Name))})");
        var kinds = cond.XKinds.Select(k => k.Name).ToList();
        Check(kinds.Contains("FORWARD") && kinds.Contains("CROSS_IN_WAIT") && kinds.Contains("TAG_IS") && !kinds.Any(k => k.StartsWith("X_", StringComparison.Ordinal)), $"새 낱말 목록 ({kinds.Count}개)");
        cond.RemoveX.Execute(cond.XRows[1]);
        cond.AddX.Execute(null);
        cond.AddGroup.Execute(null);
        cond.Groups[0].Signals.First(s => s.Name == "DEMO_SEN_2").On = true;
        await Shot("tag_cond_and.png");
        cond.Ok.Execute(null);
        await done;
        await Idle();
        var rule = Row(62, "PF_CART").Rule!;
        Check(rule.St.Args[2].Text == "AND( ID(20, 25), FORWARD, ON(DEMO_SEN_2) )", $"새 형식으로 씀 ({rule.St.Args[2].Text})");
        var d = Diff(tagBase, TagText);
        Check(d == (1, 1) && vm.ErrorCount == 0, $"한 줄만 바뀜 {d} · 오류 {vm.ErrorCount}");
        var cv = new CondVm(rule, vm.Site!.Names);
        Check(cv.XRows.Count == 2 && cv.Groups.Count == 1 && cv.Groups[0].Signals.Single(s => s.On).Name == "DEMO_SEN_2", "다시 읽기");
        // 안 고친 AND 는 팝업을 그냥 닫아도 한 글자도 안 바뀐다(하나라도 = ANY 로 돌려 씀)
        var any = Row(60, "PF_BOX");
        var before = TagText;
        var (c2, done2) = await Open<CondVm>(() => { Tag.SelectOnly(any); return vm.EditConditionAsync(any); });
        Check(c2.Groups.Count == 1 && c2.Groups[0].KindName == "ANY_ON", "ANY → 하나라도 묶음");
        c2.Ok.Execute(null);
        await done2;
        await Idle();
        Check(TagText == before, "그대로 닫으면 바이트 같음");

        Step("B2 버전별 쓰기: 새 코어는 새 형식, 옛 코어는 옛 형식");
        var tagChoices = Tag.ActionChoices(Row(28)).Select(c => c.Entry).ToList();
        var speed = tagChoices.First(e => e.Name == "AGV_SPEED");
        var speedForms = ActionForms.Of(speed, true).Select(e => e.FormName).ToList();
        Check(speedForms.SequenceEqual(["단계", "m/min"]) && !tagChoices.Any(e => e.Name is "AGV_SPEED_MMIN" or "ACS_SEND"), $"새 코어 형식 칸 {string.Join("/", speedForms)}");
        var acs = GrammarTable.Find("ACS", Place.TagAction).First(e => e.Since.Length > 0);
        var speedNum = GrammarTable.Find("AGV_SPEED", Place.TagAction).First(e => e.Since.Length > 0);
        Tag.AddAction(Row(28), speedNum);
        await Idle();
        Tag.AddAction(Row(28), acs);
        await Idle();
        Check(Row(28).Rule!.Actions.Select(a => a.Text).SequenceEqual(["OBS_ON", "AGV_SPEED( 4 )", "ACS( LOADING, 0 )"]), $"새 동작 글 ({string.Join(" · ", Row(28).Rule!.Actions.Select(a => a.Text))})");
        var stepNew = SeqEdit.StepRows(false, true).Select(e => e.Name).Distinct().ToList();
        var stepOld = SeqEdit.StepRows(false, false).Select(e => e.Name).Distinct().ToList();
        Check(new[] { "OUT_SET", "WAIT_TO", "ACS", "AGV_SPEED" }.All(stepNew.Contains) && !new[] { "WAIT_TO_PARAM", "WAIT_PARAM_TO", "AGV_SPEED_MMIN", "ACS_SEND", "WHEN_IF", "WHEN_ANY2", "WAIT_IF", "WAIT_ANY2" }.Any(stepNew.Contains),
            "새 코어 스텝 목록: 새 이름만");
        Check(!stepOld.Contains("OUT_SET") && !stepOld.Contains("WAIT_PARAM_TO") && new[] { "AGV_SPEED_MMIN", "ACS_SEND", "WHEN_IF", "WAIT_TO_PARAM" }.All(stepOld.Contains), "옛 코어 스텝 목록: 옛 이름");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.SeqTab);
        S.SelectTable("cart_load");
        await Idle();
        var pt = S.Steps.First(r => r.Piece.Text.Contains("PARAM_TIME( P_ACS_RETRY_SEC"));
        S.SelectStep(pt, true);
        await Idle();
        var form = S.Fields[0];
        Check(form.Label == "형식" && form.Text == "파라미터" && form.Info.Spec.Words!.SequenceEqual(["없음", "시간", "파라미터"]), $"대기 형식 칸 ({form.Label}={form.Text}: {string.Join("/", form.Info.Spec.Words!)})");
        Check(S.Fields.Select(f => f.Label).Contains("단위") && S.Fields.Select(f => f.Label).Contains("파라미터"), "PARAM_TIME 칸 펼침");
        await Shot("seq_new_dsl.png");

        Step("B5 새 오류: 상태 셋 AND → D-1 · 저장 막힘 · 디스크 그대로");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        await Idle();
        var bad = Row(62, "PF_CART").Rule!;
        vm.Apply(SiteEdit.SetArg(vm.Site!, bad.St, bad.St.Args[2], "AND( FORWARD, ID(1, 9), LOW_BATTERY )"));
        await Idle();
        var d1 = vm.Findings.FirstOrDefault(f => f.Finding.Rule == "D-1");
        Check(d1 != null && d1.IsError && d1.Text.Contains("AND 상태 3개 · 태그"), $"D-1 ({d1?.Text})");
        var disk = File.ReadAllText(Path.Combine(dir, "tag_table.c"));
        bool saved = await vm.SaveAsync();
        Check(!saved && File.ReadAllText(Path.Combine(dir, "tag_table.c")) == disk, "저장 막힘 · 디스크 그대로");
        vm.Undo.Execute(null);
        await Idle();
        Check(vm.ErrorCount == 0 && !vm.Findings.Any(f => f.Finding.Rule == "D-1"), "되돌리면 오류 0");
        var x = Row(62, "PF_CART").Rule!;
        vm.Apply(SiteEdit.SetArg(vm.Site!, x.St, x.St.Args[3], "X_STATION( 1, 5, 9 )"));
        await Idle();
        Check(vm.Findings.Any(f => f.Finding.Rule == "N-2" && f.IsError) || vm.ErrorCount > 0, "AND 뒤 X_… 동작 칸 → 오류");
        vm.Undo.Execute(null);
        await Idle();

        Step("검토 1·5: 새 코어 사이트에서 옛 줄을 열고 OK 만 = 바이트 동일, 옛 전용 조건에 상태를 더하면 줄 전체가 옛 형식");
        var dir2 = Path.Combine(Path.GetFullPath(work), "dsl_v058_old_line");
        Clear(dir2);
        Directory.CreateDirectory(dir2);
        foreach (var f in Directory.GetFiles(dir))
            File.Copy(f, Path.Combine(dir2, Path.GetFileName(f)));
        var tagFile = Path.Combine(dir2, "tag_table.c");
        File.WriteAllText(tagFile, File.ReadAllText(tagFile, Utf8).Replace("TAG_TABLE_END", "    TAG_IF( 960, ANY_PROFILE, IF_X_IN( X_FORWARD, NO_IN ), AGV_STOP )\nTAG_TABLE_END"), Utf8);
        await vm.TryOpen(() => vm.OpenSiteAsync(dir2, core));
        await Idle();
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        await Idle();
        var r960 = Row(960);
        int ch0 = vm.ChangeCount;
        var tag960 = TagText;
        Check(r960.Rule!.St.Args[2].Text == "IF_X_IN( X_FORWARD, NO_IN )", $"옛 줄 ({r960.Rule!.St.Args[2].Text})");
        var (c960, d960) = await Open<CondVm>(() => { Tag.SelectOnly(r960); return vm.EditConditionAsync(r960); });
        Check(c960.XRows.Count == 1 && c960.XRows[0].Kind.Name == "FORWARD", $"팝업은 새 낱말로 보임 ({string.Join(",", c960.XRows.Select(x => x.Kind.Name))})");
        c960.Ok.Execute(null);
        await d960;
        await Idle();
        Check(TagText == tag960 && vm.ChangeCount == ch0, "옛 줄 열고 OK = 줄·파일 바이트 동일 · 단계 안 생김");
        var r65 = Row(65);
        var (c65, d65) = await Open<CondVm>(() => { Tag.SelectOnly(r65); return vm.EditConditionAsync(r65); });
        c65.AddX.Execute(null);
        c65.Ok.Execute(null);
        await d65;
        await Idle();
        var t65 = Row(65).Rule!.St.Args[2].Text;
        Check(t65 == "IF_X2( X_STATION_RUNNING(1, 5, 9), X_FORWARD )" && vm.ErrorCount == 0 && Diff(tag960, TagText) == (1, 1), $"옛 전용 조건 + FORWARD = 옛 형식 한 줄 ({t65})");
        vm.Undo.Execute(null);
        await Idle();
        Check(TagText == tag960, "되돌리면 바이트 동일");
        Clear(dir2);

        Step("B2 옛 코어(core 없음): 같은 조건 편집은 옛 형식으로 쓴다");
        await vm.TryOpen(() => vm.OpenSiteAsync(site));
        await Idle();
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        await Idle();
        var o62 = Row(62, "PF_CART");
        var (oc, odone) = await Open<CondVm>(() => { Tag.SelectOnly(o62); return vm.EditConditionAsync(o62); });
        Check(oc.XKinds.Any(k => k.Name == "X_FORWARD") && !oc.XKinds.Any(k => k.Name == "FORWARD"), "옛 코어 목록은 X_ 이름");
        oc.RemoveX.Execute(oc.XRows[1]);
        oc.AddX.Execute(null);
        oc.AddGroup.Execute(null);
        oc.Groups[0].Signals.First(s => s.Name == "DEMO_SEN_2").On = true;
        oc.Ok.Execute(null);
        await odone;
        await Idle();
        Check(Row(62, "PF_CART").Rule!.CondText == "IF_X2_IN(X_ID(20,25),X_FORWARD,ON(DEMO_SEN_2))", $"옛 형식 ({Row(62, "PF_CART").Rule!.CondText})");
        var oldSpeedForms = ActionForms.Of(GrammarTable.Find("AGV_SPEED", Place.TagAction).First(e => e.Since.Length == 0), false).Select(e => e.Name + ":" + e.FormName).ToList();
        Check(oldSpeedForms.SequenceEqual(["AGV_SPEED:단계", "AGV_SPEED_MMIN:m/min"]), $"옛 코어 형식 칸 {string.Join("/", oldSpeedForms)}");
        while (vm.Undo.CanExecute(null)) vm.Undo.Execute(null);
        await Idle();
        Clear(dir);
        Clear(core);
    }
}
