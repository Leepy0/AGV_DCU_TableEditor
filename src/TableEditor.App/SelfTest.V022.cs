using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TableEditor.App.Blocks;
using TableEditor.App.ViewModels;
using TableEditor.App.Views;
using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.App;

/// <summary>
/// 자체 시험 v0.2.2: IO 맵 신호의 프로파일 지정(10), 걸러 보기, 시퀀스 고르기 <c>ID → 표</c>(11), 보고 기본 글(12).
/// 캡처: io_pf.png, tag_cond_pf.png, tag_seq_drop.png.
/// </summary>
public sealed partial class SelfTest
{
    async Task V022()
    {
        var S = vm.Seq;
        SignalScope.ShowAll = false;
        AliasVm Alias(string n) => vm.Io.Inputs.Concat(vm.Io.Outputs).SelectMany(r => r.Aliases).First(a => a.Name == n);

        Step("10 IO 맵 프로파일: 신호마다 지정 → 줄 글 · 지우면 원문 바이트");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.IoTab);
        await Idle();
        var ioBase = Text("io_map.h");
        Check(Alias("DEMO_SEN_1").PfText == "모두" && Alias("DEMO_LIFT_TOP").PfText == "PF_LIFT" && Alias("DEMO_SENH_1").PfText == "PF_BELT PF_CART", "프로파일 칸: 비면 모두");
        Check(Alias("DEMO_SENH_1").Comment == "핀 1 활성", $"주석 칸에는 pf 가 없다 ({Alias("DEMO_SENH_1").Comment})");
        var (pick, pd) = await Open<PfPickVm>(() => vm.Io.EditPfAsync(Alias("DEMO_SEN_1")));
        Check(pick.IsAll && pick.ProfileChoices.Contains("PF_BOX"), "팝업: 비면 모두 · 프로파일 목록");
        pick.AddPick = "PF_BOX";
        await Shot("io_pf.png");
        pick.Ok.Execute(null);
        await pd;
        await Idle();
        Check(Text("io_map.h").Contains("#define DEMO_SEN_1      IN_EXT_1    /* pf: PF_BOX */\n") && Diff(ioBase, Text("io_map.h")) == (1, 1) && Alias("DEMO_SEN_1").PfText == "PF_BOX" && vm.ErrorCount == 0,
            "줄 글 /* pf: PF_BOX */ · 한 줄만 바뀜");

        Step("10 걸러 보기: 조건 팝업·시퀀스 칸·수동 · 이미 쓴 신호는 남김 · 전체 토글");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        await Idle();
        var lift = Row(53, "PF_LIFT");
        var (c, cd) = await Open<CondVm>(() => { Tag.SelectOnly(lift); return vm.EditConditionAsync(lift); });
        c.AddGroup.Execute(null);
        var n1 = c.Groups[0].Signals.Select(s => s.Name).ToList();
        Check(n1.Contains("DEMO_LIFT_TOP") && n1.Contains("DEMO_SEN_2") && !n1.Contains("DEMO_SEN_1") && !n1.Contains("DEMO_SENH_1") && c.In.CanToggle && !c.In.ShowAll,
            $"PF_LIFT 줄: 맞는 신호 + 지정 없는 신호만 ({n1.Count}개)");
        await Shot("tag_cond_pf.png");
        c.In.ShowAll = true;
        var n2 = c.Groups[0].Signals.Select(s => s.Name).ToList();
        Check(n2.Contains("DEMO_SEN_1") && n2.Contains("DEMO_SENH_1") && n2.Count > n1.Count, $"전체 켬 → 전부 ({n2.Count}개)");
        c.In.ShowAll = false;
        c.Cancel.Execute(null);
        await cd;
        var cart = Row(63, "PF_CART");
        var (c2, cd2) = await Open<CondVm>(() => { Tag.SelectOnly(cart); return vm.EditConditionAsync(cart); });
        var n3 = c2.In.Groups.SelectMany(g => g.Signals).Select(s => s.Name).ToList();
        Check(n3.Contains("DEMO_SEN_1") && n3.Contains("DEMO_SEN_3") && !n3.Contains("DEMO_LIFT_TOP"), "이미 쓴 신호는 남는다(PF_BOX 지정인데 PF_CART 줄에 있음)");
        c2.Cancel.Execute(null);
        await cd2;

        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.SeqTab);
        S.SelectTable("lift_load");
        S.SelectStep(S.Steps.First(r => r.Piece.Text.Contains("HANDSHAKE(")), true);
        await Idle();
        var outF = S.Fields.First(f => f.Info.Spec.Kind is ArgKind.Output or ArgKind.Signal);
        var o1 = outF.Items!.Cast<Choice>().Select(x => x.Name).ToList();
        Check(S.Scope!.Profiles is { Count: 1 } ps && ps.Contains("PF_LIFT") && o1.Contains("DEMO_LIFT_UP") && o1.Contains(outF.Text) && !o1.Contains("DEMO_CV2_PWR") && o1[^1] == SignalScope.AllOff,
            $"시퀀스 칸: lift_load = PF_LIFT 만 · 끝에 전체 ({o1.Count}개)");
        outF.ToggleAll();
        var o2 = outF.Items!.Cast<Choice>().Select(x => x.Name).ToList();
        Check(o2.Contains("DEMO_CV2_PWR") && o2[^1] == SignalScope.AllOn && outF.Text == o1.First(x => x == outF.Text), "칸 목록 전체 켬 · 고른 값 그대로");
        outF.ToggleAll();
        Check(!SignalScope.ShowAll && !outF.Items!.Cast<Choice>().Any(x => x.Name == "DEMO_CV2_PWR"), "다시 끄면 걸러 봄");
        S.SelectTable("shuttle");
        Check(S.Scope!.Profiles == null && SeqVm.ScopeOf(vm.Site!, "no_such_table").Profiles == null, "전체 줄에 있는 표 · 대응표에 없는 표 = 전부");
        var man = vm.Site!.ManualRows.First(s => s.Name == "MAN");
        Check(ManualVm.ScopeOf(vm.Site, man).Profiles!.Contains(man.Args[0].Text), $"수동 줄 = 그 프로파일 ({man.Args[0].Text})");

        Step("10 I-7: pf 주석의 없는 프로파일 이름 = 경고, 지정 지우면 원문 바이트");
        var lamp = (Statement)vm.Site.Names.Find("DEMO_LAMP")!.Def!;
        vm.Apply(IoProfile.Set(vm.Site, lamp, ["PF_NOPE"]));
        await Idle();
        Check(vm.Findings.Any(f => f.Rule == "I-7" && !f.IsError && f.Text.Contains("PF_NOPE")), "I-7 경고");
        vm.Undo.Execute(null);
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.IoTab);
        await Idle();
        var (pick2, pd2) = await Open<PfPickVm>(() => vm.Io.EditPfAsync(Alias("DEMO_SEN_1")));
        pick2.RemoveProfile.Execute("PF_BOX");
        pick2.Ok.Execute(null);
        await pd2;
        await Idle();
        Check(Text("io_map.h") == ioBase && vm.ErrorCount == 0, "지정을 다 지우면 원문 바이트 동일");
        while (vm.Undo.CanExecute(null)) { vm.Undo.Execute(null); await Idle(); }

        Step("11 시퀀스 고르기: 그 프로파일에 연결된 ID → 표 · 연결 없음 흐리게 · 전체 줄은 연결 수");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        await Idle();
        var r40 = Row(40, "PF_BELT");
        Tag.SelectOnly(r40);
        await Idle();
        Tag.Picked = r40.Blocks.First(b => b.Fields.Any(f => f.Spec.Kind == ArgKind.SeqShort));
        await Idle();
        var sf = Tag.Fields.First(f => f.Info.Spec.Kind == ArgKind.SeqShort);
        var ch = sf.Items!.Cast<Choice>().ToList();
        var linked = ch.Where(x => x.Group == Choices.Linked).ToList();
        Check(linked.Select(x => x.Name + x.Note).OrderBy(x => x).SequenceEqual(["DEMO_SHUTTLE→ shuttle", "LOAD→ belt_load", "UNLOAD→ belt_unload"]),
            $"PF_BELT: 연결 ({string.Join(", ", linked.Select(x => x.Name + " " + x.Note))})");
        Check(ch.Take(linked.Count).All(x => x.Group == Choices.Linked) && ch.Skip(linked.Count).All(x => x.Group == Choices.Unlinked) && ch.Any(x => x.Name == "CART_LOAD" && x.Group == Choices.Unlinked) && ch.Any(x => x.Name == sf.Text),
            $"연결이 위 · 아래 연결 없음 ({ch.Count - linked.Count}개) · 고른 값 있음");
        var box = Below<FieldEdit>(w).FirstOrDefault(f => ReferenceEquals(f.DataContext, sf));
        if (box != null)
        {
            box.List.IsDropDownOpen = true;
            await Idle();
            if (box.List.Template.FindName("PART_Popup", box.List) is Popup { Child: System.Windows.FrameworkElement pc } && pc.ActualWidth > 0)
            {
                Below<ScrollViewer>(pc).FirstOrDefault()?.ScrollToTop();
                await Idle();
                App.Save(pc, Path.Combine(shots ?? work, "tag_seq_drop.png"));
            }
            box.List.IsDropDownOpen = false;
        }
        var any = Choices.For(sf.Info.Spec, vm.Site!.Names, TagVm.ScopeOf(vm.Site, null)).ToList();
        Check(any.First(x => x.Name == "LOAD").Note == vm.Site.SeqMap.Where(m => m.Id == "SEQ_LOAD").Select(m => m.Profile).Distinct().Count() + "곳" && any.First(x => x.Name == "DEMO_SHUTTLE").Note == "4곳" && any.First(x => x.Name == "CART_LOAD").Note == "1곳"
            && any.Any(x => x.Group == Choices.Unlinked), $"ANY_PROFILE 줄: 연결된 프로파일 수 ({string.Join(", ", any.Where(x => x.Name is "LOAD" or "DEMO_SHUTTLE" or "CART_LOAD").Select(x => x.Name + " " + x.Note))})");

        Step("12 보고 기본 글: 태그 새 동작 ACS 는 칸 0 = 1칸 ACS( 종류, STATION0 )");
        var acs = GrammarTable.Find("ACS", Place.TagAction).First(e => e.TagCells == 2 && e.Args[1].Kind == ArgKind.AcsStation);
        Check(Choices.DefaultText(acs, vm.Site.Names) == "ACS( LOADING, STATION0 )", $"기본 글 ({Choices.DefaultText(acs, vm.Site.Names)})");
        Check(vm.ErrorCount == 0, "끝: 오류 0");

        Step("13 보고 종류 IDLE: v0.59.0 사이트는 고를 수 있다(시퀀스·태그), v0.58.0 사이트는 목록에 없고 쓰면 V-4");
        var dir9 = Path.Combine(Path.GetFullPath(work), "dsl_v059");
        Clear(dir9);
        Directory.CreateDirectory(dir9);
        foreach (var f9 in Directory.GetFiles(Path.Combine(RepoRoot(), "tests", "fixtures", "T900_DEMO", "dsl_v058")))
            File.Copy(f9, Path.Combine(dir9, Path.GetFileName(f9)));
        var core9 = FakeCore(Path.Combine(Path.GetFullPath(work), "dsl_v059_core"), "v0.59.0");
        await vm.TryOpen(() => vm.OpenSiteAsync(dir9, core9));
        await Idle();
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        await Idle();
        var acsNew = GrammarTable.Find("ACS", Place.TagAction).First(e => e.Since.Length > 0);
        Tag.AddAction(Row(28), acsNew);
        await Idle();
        var kf = Tag.Fields.First(f => f.Info.Spec.Words?.Contains("LOADING") == true);
        Check(vm.Site!.Names.IdleOk && kf.Items!.Cast<Choice>().Any(c => c.Name == "IDLE"), "v0.59.0 태그: 종류 목록에 IDLE");
        kf.Picked = kf.Items!.Cast<Choice>().First(c => c.Name == "IDLE");
        await Idle();
        Check(Row(28).Rule!.Actions[^1].Text == "ACS( IDLE, STATION0 )" && vm.ErrorCount == 0, $"태그 1칸 IDLE ({Row(28).Rule!.Actions[^1].Text})");
        var tagIdle = Row(28).Rule!;
        vm.Apply(SiteEdit.SetArg(vm.Site, tagIdle.St, tagIdle.Actions[^1], "ACS( IDLE, STATION(2) )"));
        await Idle();
        Check(Row(28).Rule!.Actions[^1].Text == "ACS( IDLE, STATION(2) )" && vm.ErrorCount == 0, "태그 2칸 IDLE · 오류 0");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.SeqTab);
        await Idle();
        var tbl = vm.Site.Seqs.First(t => t.Steps.Any(s => s is Statement { Name: "ACS" })).Name;
        S.SelectTable(tbl);
        S.SelectStep(S.Steps.First(r => r.Piece is Statement { Name: "ACS" }), true);
        await Idle();
        var sk = S.Fields.First(f => f.Info.Spec.Words?.Contains("LOADING") == true);
        sk.Picked = sk.Items!.Cast<Choice>().First(c => c.Name == "IDLE");
        await Idle();
        Check(S.SelectedStep!.Piece.Text.Contains("ACS( IDLE,") && vm.ErrorCount == 0, $"시퀀스 IDLE ({S.SelectedStep.Piece.Text.Trim()})");
        while (vm.Undo.CanExecute(null)) { vm.Undo.Execute(null); await Idle(); }

        var core8 = FakeCore(Path.Combine(Path.GetFullPath(work), "dsl_v059_core58"), "v0.58.0");
        await vm.TryOpen(() => vm.OpenSiteAsync(dir9, core8));
        await Idle();
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        await Idle();
        Tag.AddAction(Row(28), acsNew);
        await Idle();
        var kf8 = Tag.Fields.First(f => f.Info.Spec.Words?.Contains("LOADING") == true);
        Check(!vm.Site!.Names.IdleOk && !kf8.Items!.Cast<Choice>().Any(c => c.Name == "IDLE") && kf8.Items!.Cast<Choice>().Any(c => c.Name == "STANDBY"), "v0.58.0 태그: 종류 목록에 IDLE 없음");
        var r8 = Row(28).Rule!;
        vm.Apply(SiteEdit.SetArg(vm.Site, r8.St, r8.Actions[^1], "ACS( IDLE, STATION0 )"));
        await Idle();
        Check(vm.Findings.Any(f => f.Rule == "V-4" && f.IsError && f.Text == "코어 v0.59.0 필요 · IDLE"), "쓰면 V-4 오류 · 코어 v0.59.0 필요");
        var mode = GrammarTable.Find("MAN").First(e => e.Place == Place.Manual).Args[1];
        Check(Choices.For(mode, vm.Site.Names).Any(c => c.Name == "IDLE") && ManualVm.Modes.Contains("IDLE"), "수동 표 모드 IDLE 은 그대로(다른 낱말)");
        while (vm.Undo.CanExecute(null)) { vm.Undo.Execute(null); await Idle(); }
        Clear(dir9);
    }
}
