using System.IO;
using System.Windows;
using TableEditor.App.ViewModels;
using TableEditor.App.Views;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.App;

/// <summary>
/// <c>--selftest &lt;작업 폴더&gt;</c>: 시험 사이트 사본을 만들고 화면에 띄운 채(실제 바인딩) VM 명령으로 편집 한 바퀴를 돈다.
/// 단계마다 파일 차이·검사 결과·변경 수를 견준다. 기록은 <c>&lt;작업 폴더&gt;/selftest.log</c>, 종료 코드 0 = 통과.
/// <c>--shots &lt;폴더&gt;</c> 를 주면 단계 중간에 tag.png, tag_add.png, tag_cond.png, settings.png 를 찍는다.
/// </summary>
public sealed partial class SelfTest(MainWindow w, MainVm vm, string work, string? shots)
{
    readonly List<string> log = [];
    int failures, steps;
    string site = "";
    string orig = "";

    TagVm Tag => vm.Tag;

    void Check(bool ok, string what)
    {
        log.Add((ok ? "통과  " : "실패  ") + what);
        if (!ok) failures++;
    }

    void Step(string name)
    {
        steps++;
        log.Add($"-- {steps}. {name}");
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TableEditor.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("TableEditor.sln 을 못 찾음");
    }

    string TagText => vm.Site!.File("tag_table.c")!.ToText();
    string DiskText => File.ReadAllText(Path.Combine(site, "tag_table.c"));

    /// <summary>줄 차이(빠진 줄 수, 더한 줄 수). 순서는 보지 않는다(옮긴 줄은 0, 0).</summary>
    static (int Removed, int Added) Diff(string a, string b)
    {
        var left = a.Split('\n').GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        int added = 0;
        foreach (var line in b.Split('\n'))
        {
            if (left.TryGetValue(line, out var n) && n > 0) left[line] = n - 1;
            else added++;
        }
        return (left.Values.Sum(), added);
    }

    TagRowVm Row(long tag, string? profile = null) =>
        Tag.Rows.First(r => r.First == tag && (profile == null || r.Rule?.Profile == profile));

    async Task Idle() => await App.Idle(w);

    async Task Shot(string name)
    {
        if (shots == null) return;
        await Idle();
        App.Save(w.Root, Path.Combine(shots, name));
    }

    /// <summary>팝업을 띄우는 일을 시작하고, 뜬 팝업을 돌려준다(일은 아직 안 끝났다).</summary>
    async Task<(T Dialog, Task Done)> Open<T>(Func<Task> start) where T : DialogVm
    {
        var shown = new TaskCompletionSource<DialogVm>();
        void On(DialogVm d) => shown.TrySetResult(d);
        vm.DialogShown += On;
        var done = start();
        if (await Task.WhenAny(shown.Task, Task.Delay(TimeSpan.FromSeconds(60))) != shown.Task)
        {
            vm.DialogShown -= On;
            throw new TimeoutException($"팝업 {typeof(T).Name} 이 뜨지 않음");
        }
        var d = await shown.Task;
        vm.DialogShown -= On;
        await Idle();
        return ((T)d, done);
    }

    public async Task<int> RunAsync()
    {
        try { await Scenario(); }
        catch (Exception e) { Check(false, "예외 · " + e); }
        log.Add(failures == 0 ? $"모두 통과 · {steps}단계" : $"실패 {failures} · {steps}단계");
        Directory.CreateDirectory(work);
        File.WriteAllLines(Path.Combine(work, "selftest.log"), log);
        return failures == 0 ? 0 : 1;
    }

    async Task Scenario()
    {
        Step("사본 열기");
        site = Path.Combine(Path.GetFullPath(work), "T900_DEMO");
        if (Directory.Exists(site)) Directory.Delete(site, true);
        Directory.CreateDirectory(site);
        foreach (var f in Directory.GetFiles(Path.Combine(RepoRoot(), "tests", "fixtures", "T900_DEMO")))
            File.Copy(f, Path.Combine(site, Path.GetFileName(f)));
        Drafts.Clear(Site.Open(site));
        await vm.TryOpen(() => vm.OpenSiteAsync(site));
        await Idle();
        orig = TagText;
        Check(vm.Site != null && vm.ErrorCount == 0, $"오류 0 ({vm.ErrorCount})");
        Check(vm.ChangeCount == 0, "변경 0");
        Check(Tag.Total == 42, $"줄 42 ({Tag.Total})");

        Step("칸 고치기: 회전 각도 90 → 45");
        var turn = Row(22);
        Tag.SelectOnly(turn);
        log.Add($"   고른 뒤 바로: {Tag.Selected?.TagText} {Tag.Picked?.Head}");
        await Idle();
        Check(Tag.Picked?.Head == "회전" && Tag.Fields.Count >= 4, $"회전 블록·칸 ({Tag.Selected?.TagText}, {Tag.Picked?.Head}, {string.Join("/", Tag.Fields.Select(f => f.Label))})");
        var angle = Tag.Fields.First(f => f.Label == "각도");
        angle.Text = "45";
        angle.Commit();
        await Idle();
        var d = Diff(orig, TagText);
        Check(d == (1, 1), $"한 줄만 바뀜 {d}");
        Check(Row(22).Rule!.St.Text.Contains("45"), "45 들어감");
        Check(vm.ChangeCount == 1 && vm.ErrorCount == 0, $"변경 1, 오류 0 ({vm.ChangeCount}, {vm.ErrorCount})");
        Check(Tag.Fields.FirstOrDefault(f => f.Label == "각도")?.IsHot == true, "고친 칸 강조");
        Check(Tag.Code.Mark == "45", $"C 글자 강조 '{Tag.Code.Mark}'");
        Check(Row(22).IsChanged, "고친 줄 점");
        await Shot("tag.png");

        Step("범위 밖 숫자는 확정 안 됨");
        angle = Tag.Fields.First(f => f.Label == "각도");
        angle.Text = "400";
        Check(angle.Invalid, "빨간 테");
        angle.Commit();
        Check(vm.ChangeCount == 1, "명령 안 됨");
        angle.Revert();

        Step("태그 번호 바꾸기: 27 → 29");
        var r27 = Row(27);
        Tag.SelectOnly(r27);
        await Idle();
        r27.TagDraft = "29";
        Tag.CommitTag.Execute(r27);
        await Idle();
        Check(vm.Site!.TagRules.Any(r => vm.Site.Num(r.FirstArg) == 29) && !vm.Site.TagRules.Any(r => vm.Site.Num(r.FirstArg) == 27), "29 있고 27 없음");
        Check(Tag.Selected?.First == 29, "옮긴 줄 고름");
        Check(vm.ChangeCount == 2, $"변경 2 ({vm.ChangeCount})");

        Step("여러 줄 프로파일: 2·3 → PF_LIFT");
        var two = Row(2);
        var three = Row(3);
        Tag.SetSelection([two, three], two, false);
        await Idle();
        two.ProfileDraft = "PF_LIFT";
        await Idle();
        Check(Row(2).Rule!.Profile == "PF_LIFT" && Row(3).Rule!.Profile == "PF_LIFT", "두 줄 모두");
        Check(vm.ChangeCount == 3 && vm.ErrorCount == 0, $"변경 3, 오류 0 ({vm.ChangeCount}, {vm.ErrorCount})");

        Step("여러 줄 추가(팝업): 201-203 × PF_BELT, PF_LIFT");
        Tag.SelectOnly(Row(1));
        var (add, addDone) = await Open<AddVm>(() => vm.AddRowsAsync());
        add.Tags = "201-203, 40";
        add.Profiles.Clear();
        add.AddPick = "PF_BELT";
        add.AddPick = "PF_LIFT";
        add.Template = add.Templates[^1];
        Check(add.Templates[^1].Rule == null, "빈 줄 고를 수 있음");
        add.Template = add.Templates[0];
        Check(add.Preview.Count == 4 && add.NewCount == 6 && add.HasCount == 2 && add.LineCount == 6,
            $"미리 보기 4줄 · 새 줄 6 · 있음 2 ({add.Preview.Count}, {add.NewCount}, {add.HasCount}, {add.LineCount})");
        add.Range = true;
        Check(add.LineCount == 2, $"범위 한 줄 = 2줄 ({add.LineCount})");
        add.Range = false;
        add.Tags = "201-";
        Check(add.HasError, "틀린 번호 글");
        add.Tags = "201-203, 40";
        await Shot("tag_add.png");
        add.Ok.Execute(null);
        await addDone;
        await Idle();
        Check(Tag.SelectedRows.Count == 6 && Tag.SelectedRows.All(r => r.First is >= 201 and <= 203), $"새 줄 6 고름 ({Tag.SelectedRows.Count})");
        var dd = Diff(orig, TagText);
        Check(dd.Added - dd.Removed == 6, $"더한 줄 6 {dd}");
        Check(vm.ChangeCount == 4 && vm.ErrorCount == 0, $"변경 4, 오류 0 ({vm.ChangeCount}, {vm.ErrorCount})");

        Step("조건 바꾸기(팝업): 태그 5 에 전진 중 + 입력 켜짐");
        var r5 = Row(5);
        var (cond, condDone) = await Open<CondVm>(() => { Tag.SelectOnly(r5); return vm.EditConditionAsync(r5); });
        cond.AddX.Execute(null);
        cond.AddGroup.Execute(null);
        cond.Groups[0].Signals.First(s => s.Name == "DEMO_SEN_1").On = true;
        Check(cond.X.Count == 1 && cond.Input == "ON(DEMO_SEN_1)", $"조건 글 {cond.Input}");
        await Shot("tag_cond.png");
        cond.Ok.Execute(null);
        await condDone;
        await Idle();
        var r5n = Row(5).Rule!;
        Check(r5n.St.Name == "TAG_IF" && r5n.CondText == "IF_X_IN(X_FORWARD,ON(DEMO_SEN_1))", $"TAG_IF {r5n.CondText}");
        Check(vm.ErrorCount == 0, $"오류 0 ({vm.ErrorCount})");
        var cv = new CondVm(r5n, vm.Site.Names);
        Check(cv.XRows.Count == 1 && cv.Groups.Count == 1 && cv.Groups[0].Signals.Single(s => s.On).Name == "DEMO_SEN_1", "다시 읽기");

        Step("동작 넣기·지우기");
        var r28 = Row(28);
        var stop = GrammarTable.Find("AGV_STOP", Place.TagAction).First(e => e.Args.Count == 0);
        Check(Tag.ActionChoices(r28).Any(c => c.Entry == stop && c.Enabled), "메뉴에 정지");
        Check(Row(40, "PF_CART").CellCount == 3 && Tag.ActionChoices(Row(40, "PF_CART")).Where(c => c.Entry.TagCells == 2).All(c => !c.Enabled), "칸 모자라면 흐림");
        Tag.AddAction(r28, stop);
        await Idle();
        Check(Row(28).Blocks.Count == 2 && Row(28).Blocks[1].Head == "정지", "정지 더함");
        Tag.DeleteAction(Row(28).Blocks[1]);
        await Idle();
        Check(Row(28).Blocks.Count == 1, "지움");
        Check(vm.ChangeCount == 7, $"변경 7 ({vm.ChangeCount})");

        Step("블록 순서 바꾸기");
        var r1 = Row(1);
        Tag.MoveBlock(r1.Blocks[0], r1.Blocks[1]);
        await Idle();
        Check(Row(1).Blocks[0].Head.StartsWith("자동 출발", StringComparison.Ordinal) && Row(1).Blocks[1].Head == "정지", "자리 바뀜");

        Step("줄 복제·삭제");
        var r6 = Row(6);
        Tag.SelectOnly(r6);
        Tag.Duplicate.Execute(null);
        await Idle();
        Check(Tag.Rows.Count(r => r.First == 6) == 2, "복제 둘");
        Check(vm.ErrorCount == 1 && Tag.Selected?.IsError == true, $"복제 줄 = 죽은 줄 ({vm.ErrorCount})");
        Tag.Delete.Execute(null);
        await Idle();
        Check(Tag.Rows.Count(r => r.First == 6) == 1 && vm.ErrorCount == 0, "지우니 오류 0");

        Step("줄 옮기기: 태그 8 을 맨 위로");
        var r8 = Row(8);
        Tag.MoveRows([r8], Tag.Rows.First(r => r.Ordinal == 1));
        await Idle();
        Check(Tag.Rows.First(r => r.Ordinal == 1).First == 8, "1줄 = 태그 8");
        Check(vm.ChangeCount == 11, $"변경 11 ({vm.ChangeCount})");

        Step("되돌리기 전부");
        while (vm.Undo.CanExecute(null)) { vm.Undo.Execute(null); await Idle(); }
        Check(vm.ChangeCount == 0, $"변경 0 ({vm.ChangeCount})");
        Check(TagText == orig, "글자 처음 그대로");
        vm.Redo.Execute(null);
        await Idle();
        Check(vm.ChangeCount == 1 && Diff(orig, TagText) == (1, 1), "다시 하기 1");

        Step("초안");
        vm.FlushDraft();
        Check(File.Exists(Path.Combine(Drafts.Folder(vm.Site!), "tag_table.c")), "초안 씀");

        Step("오류가 있으면 저장 안 함");
        Tag.SelectOnly(Row(6));
        Tag.Duplicate.Execute(null);
        await Idle();
        Tag.SelectOnly(Row(1));
        bool saved = await vm.SaveAsync();
        Check(!saved && DiskText == orig, "저장 막힘·디스크 그대로");
        Check(Tag.Selected?.IsError == true, "첫 오류 줄로 감");
        vm.Undo.Execute(null);
        await Idle();

        Step("저장");
        saved = await vm.SaveAsync();
        await Idle();
        Check(saved && vm.ChangeCount == 0, $"저장·변경 0 ({vm.ChangeCount})");
        Check(Diff(orig, DiskText) == (1, 1) && DiskText.Contains("AGV_TURN( CCW, 45,"), $"디스크 한 줄 {Diff(orig, DiskText)}");
        Check(!Directory.Exists(Drafts.Folder(vm.Site!)), "초안 지움");
        Check(!Row(22).IsChanged, "점 사라짐");

        Step("바깥에서 바뀐 파일");
        var other = DiskText.Replace("TAG ( 2,", "TAG ( 2, ");
        File.WriteAllText(Path.Combine(site, "tag_table.c"), other);
        Tag.SelectOnly(Row(23));
        Tag.Fields.First(f => f.Label == "각도").Text = "135";
        Tag.Fields.First(f => f.Label == "각도").Commit();
        await Idle();
        var (ask, saveDone) = await Open<AskVm>(() => vm.SaveAsync());
        Check(ask.Title == "바깥에서 바뀜" && ask.Buttons.Any(b => b.Key == "cancel" && b.Default), "팝업·기본 취소");
        ask.Close("cancel");
        await saveDone;
        Check(DiskText == other, "취소면 그대로");

        Step("설정 화면");
        vm.Settings.LastRepo = @"D:\work\AGV_DCU_App";
        vm.Settings.GccPath = @"D:\tools\gcc\bin\gcc.exe";
        vm.Settings.Cl2000Path = @"C:\ti\ti-cgt-c2000_18.12.2.LTS\bin\cl2000.exe";
        vm.Settings.UserName = "작업자";
        vm.Config.Reload();
        vm.ClearNotice();
        vm.Config.ShowTools([
            new ToolRow("git", true, @"git version 2.47 · C:\Program Files\Git\cmd\git.exe"),
            new ToolRow("powershell", true, "5.1 · powershell.exe"),
            new ToolRow("gcc", false, @"없음 · D:\tools\gcc\bin\gcc.exe"),
            new ToolRow("cl2000", true, @"18.12.2.LTS · C:\ti\ti-cgt-c2000_18.12.2.LTS\bin\cl2000.exe"),
        ]);
        vm.Tab = vm.SettingsTab;
        await Shot("settings.png");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        Check(vm.IsTagTab, "태그 탭으로");

        // 태그 단계의 남은 변경을 버리고 디스크에서 다시 연다(바깥에서 바꾼 tag_table.c 포함)
        while (vm.Undo.CanExecute(null)) vm.Undo.Execute(null);
        Drafts.Clear(vm.Site!);
        await vm.TryOpen(() => vm.OpenSiteAsync(site));
        await Idle();
        await Tables();
        await Ux();

        // 끝: 남은 변경 버리기(닫기 팝업 없이 끝낸다)
        while (vm.Undo.CanExecute(null)) vm.Undo.Execute(null);
        Drafts.Clear(vm.Site!);

        await GitScenario();
    }

    string Text(string file) => vm.Site!.File(file)!.ToText();

    /// <summary>프로파일·수동·시퀀스·대응표·IO 화면: 편집, 지도, 되돌리기 전부, 저장.</summary>
    async Task Tables()
    {
        var files = vm.Site!.Files.ToDictionary(f => f.Name, f => f.ToText());
        int ChangedFiles() => vm.Site!.Files.Count(f => f.ToText() != files[f.Name]);
        var P = vm.Profile;

        Step("프로파일: 1줄 끝 호기 9 → 8");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.ProfileTab);
        Check(P.Rows.Count == 4 && P.Cells.Count == 100, $"줄 4·지도 100 ({P.Rows.Count}, {P.Cells.Count})");
        Check(P.Cells[9].Profile == "PF_BELT" && P.Cells[0].IsEmpty, "지도: 9 = PF_BELT, 0 빈 칸");
        var r1 = P.Rows[0];
        P.Select(r1, true);
        await Idle();
        Check(P.Cells.Count(c => c.IsHot) == 9, $"고른 줄 호기 강조 9 ({P.Cells.Count(c => c.IsHot)})");
        r1.LastDraft = "120";
        Check(r1.LastBad, "범위 밖 빨간 테");
        P.CommitNumbers(r1);
        Check(vm.ChangeCount == 0, "범위 밖은 확정 안 됨");
        r1.LastDraft = "8";
        P.CommitNumbers(r1);
        await Idle();
        Check(Diff(files["profiles.c"], Text("profiles.c")) == (1, 1) && ChangedFiles() == 1, $"그 줄만 바뀜 {Diff(files["profiles.c"], Text("profiles.c"))}");
        Check(P.Cells[9].IsEmpty && P.Cells[8].Profile == "PF_BELT", "지도: 9 빈 칸");
        Check(P.Selected?.Ordinal == 1 && P.Selected.IsChanged, "고친 줄 고름·점");

        Step("프로파일: 4줄 처음 30 → 25, 3줄 위로 끌기 → 지도");
        var r4 = P.Rows[3];
        P.Select(r4, false);
        r4.FirstDraft = "25";
        P.CommitNumbers(r4);
        await Idle();
        Check(P.Cells[27].Profile == "PF_CART" && vm.WarnCount == 1, $"겹친 25~29 = PF_CART, 경고 1 ({P.Cells[27].Profile}, {vm.WarnCount})");
        P.MoveRow(P.Rows[3], P.Rows[2]);
        await Idle();
        Check(P.Rows[2].Profile == "PF_BOX" && P.Cells[27].Profile == "PF_BOX", $"끈 뒤 25~29 = PF_BOX ({P.Rows[2].Profile}, {P.Cells[27].Profile})");
        Check(vm.ErrorCount == 0 && vm.ChangeCount == 3, $"오류 0, 변경 3 ({vm.ErrorCount}, {vm.ChangeCount})");
        P.Select(P.Rows[2], true);
        await Shot("profile.png");

        Step("프로파일: 새 프로파일(이름 팝업) → 두 파일");
        var row = P.Rows[0];
        P.Select(row, false);
        var (name, _) = await Open<NameVm>(() => { row.ProfileDraft = ProfileVm.NewProfile; return Task.CompletedTask; });
        name.Text = "PF_DEMO_NEW";
        name.Ok.Execute(null);
        await Idle();
        Check(Text("site.h").Contains("PF_DEMO_NEW") && P.Rows[0].Profile == "PF_DEMO_NEW" && P.Rows.Count == 4, "site.h enum + 1줄 프로파일");
        Check(ChangedFiles() == 2 && Diff(files["site.h"], Text("site.h")).Added == 2, $"두 파일 ({ChangedFiles()}, {Diff(files["site.h"], Text("site.h"))})");

        Step("수동: 카드 추가(PF_LIFT JOG 사슬을 PF_CART 로 복사), 가지 추가");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.ManualTab);
        var M = vm.Manual;
        Check(M.Cards.Count == 3 && M.Inputs.Count == 2, $"카드 3, 입력 줄 2 ({M.Cards.Count}, {M.Inputs.Count})");
        var (card, cardDone) = await Open<CardAddVm>(() => { M.AddCard.Execute(null); return Task.CompletedTask; });
        card.Profile = "PF_CART";
        card.Template = card.Templates.First(t => t.Chain?.Profile == "PF_LIFT");
        Check(card.IsJog, "본뜬 사슬의 모드");
        card.Ok.Execute(null);
        await Idle();
        var added = M.Cards.FirstOrDefault(c => c.Profile == "PF_CART");
        Check(M.Cards.Count == 4 && added?.Rows.Count == 3 && added.Mode == "JOG", $"새 카드 3줄 ({added?.Rows.Count})");
        M.Select(added!.Rows[0], false);
        M.AddBranch.Execute(null);
        await Idle();
        added = M.Cards.First(c => c.Profile == "PF_CART");
        Check(added.Rows.Count == 4 && added.Rows[^1].IsElse && vm.ErrorCount == 0, $"가지 3 + 끝 줄, 오류 0 ({added.Rows.Count}, {vm.ErrorCount})");
        Check(Diff(files["manual_table.c"], Text("manual_table.c")) == (0, 4), $"수동 줄 4 더함 {Diff(files["manual_table.c"], Text("manual_table.c"))}");
        await Shot("manual.png");

        Step("시퀀스: belt_load DELAY 100 → 250");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.SeqTab);
        var S = vm.Seq;
        Check(S.Tree.Count(t => t.IsTable) >= 8, $"나무 표 {S.Tree.Count(t => t.IsTable)}");
        Check(S.SelectedTable == "belt_load" && S.Steps.Count > 0, $"다른 화면 편집 뒤에도 시퀀스 표 ({S.SelectedTable})");
        S.SelectTable("belt_load");
        var delay = S.Steps.First(r => r.Block.Head == "지연");
        S.SelectStep(delay, true);
        await Idle();
        Check(S.Fields.Count == 1 && !S.Fields[0].Locked && S.Fields[0].Small == "1.00", $"시간 칸·초 ({S.Fields.Count}, {S.Fields.FirstOrDefault()?.Small})");
        var label = S.Steps.First(r => r.Block.Head == "가기");
        S.SelectStep(label, false);
        Check(S.Fields.Count == 1 && !S.Fields[0].Locked && S.Fields[0].IsList, "라벨 칸 드롭다운(시퀀스 칸 모두 고침)");
        S.SelectStep(S.Steps.First(r => r.Block.Head == "지연"), false);
        S.Fields[0].Text = "250";
        S.Fields[0].Commit();
        await Idle();
        Check(Diff(files["seq_table.c"], Text("seq_table.c")) == (1, 1) && Text("seq_table.c").Contains("DELAY(250)"), $"한 줄 {Diff(files["seq_table.c"], Text("seq_table.c"))}");
        Check(S.SelectedStep?.Block.Head == "지연" && S.Fields[0].IsHot && S.Fields[0].Small == "2.50", "같은 스텝·칸, 2.50");
        Check(S.Steps.Any(r => r.IsLabel) && S.Steps.Any(r => r.InSelect && r.SelectTop) && S.Steps.Any(r => r.Level == 1), "라벨·묶음·들여쓰기");
        await Shot("seq.png");

        Step("대응표: 줄 추가·끌기, 표 이름 → 시퀀스");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.MapTab);
        var Mp = vm.Map;
        int seqRows = vm.Site!.SeqMap.Count;
        Mp.Select(Mp.Rows[0], false);
        Mp.Add.Execute(null);
        await Idle();
        Check(vm.Site!.SeqMap.Count == seqRows + 1 && Mp.Selected?.Ordinal == 2 && vm.ErrorCount == 0, $"2줄에 새 줄, 오류 0 ({Mp.Selected?.Ordinal}, {vm.ErrorCount})");
        var fresh = Mp.Selected!;
        var id = fresh.Id;
        Mp.MoveRow(fresh, null);
        await Idle();
        Check(vm.Site!.SeqMap[^1].Id.Trim() == id && Mp.Selected?.Ordinal == seqRows + 1, $"끝으로 끔 ({vm.Site.SeqMap[^1].Id})");
        Mp.Select(Mp.Rows[0], true);
        await Shot("map.png");
        Mp.GoTable.Execute(Mp.Rows[2]);
        Check(vm.IsSeqTab && S.SelectedTable == "lift_load", $"시퀀스 화면의 그 표 ({S.SelectedTable})");

        Step("IO 맵");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.IoTab);
        var io = vm.Io;
        Check(io.Inputs.Count == 32 && io.Outputs.Count == 32, "32 · 32");
        Check(io.Inputs[4].Aliases.Any(a => a.Name == "DEMO_SENH_1" && a.High) && io.Unwired.Count == 1 && io.Belts.Count == 3, "핀 1 활성·미연결·벨트");
        Check(io.Inputs[0].Aliases.First(a => a.Name == "DEMO_SEN_1").Uses > 0, "쓰인 곳 수");
        await Shot("io.png");

        Step("검사 줄(P-2 죽은 줄) → 프로파일 화면 그 줄");
        P.Select(P.Rows[2], false);
        P.Rows[2].FirstDraft = "10";
        P.Rows[2].LastDraft = "12";
        P.CommitNumbers(P.Rows[2]);
        await Idle();
        var f = vm.Findings.FirstOrDefault(x => x.Tab?.Name == MainVm.ProfileTab && x.Rule == "P-2");
        vm.Tab = vm.Tabs.First(t => t.Name == MainVm.TagTab);
        if (f != null) vm.Go(f);
        Check(f != null && vm.IsProfileTab && P.Selected?.Ordinal == 3 && P.Selected.IsError, $"P-2 줄로 감 ({f?.Text})");

        Step("되돌리기 전부");
        while (vm.Undo.CanExecute(null)) { vm.Undo.Execute(null); await Idle(); }
        Check(vm.ChangeCount == 0 && ChangedFiles() == 0, $"변경 0, 파일 그대로 ({ChangedFiles()})");
        S.SelectTable("belt_unload");
        S.SelectTable("belt_load");
        Check(S.Steps.All(r => !r.IsChanged) && P.Rows.All(r => !r.IsChanged) && vm.Manual.Cards.All(c => c.Rows.All(r => !r.IsChanged)), "바뀐 줄 점 없음(다시 연 표 포함)");

        Step("저장: 프로파일 끝 호기");
        P.Select(P.Rows[0], false);
        P.Rows[0].LastDraft = "8";
        P.CommitNumbers(P.Rows[0]);
        await Idle();
        bool saved = await vm.SaveAsync();
        await Idle();
        var disk = File.ReadAllText(Path.Combine(site, "profiles.c"));
        Check(saved && vm.ChangeCount == 0 && Diff(files["profiles.c"], disk) == (1, 1), $"저장·디스크 한 줄 {Diff(files["profiles.c"], disk)}");
        Check(!P.Rows[0].IsChanged, "점 사라짐");
    }
}
