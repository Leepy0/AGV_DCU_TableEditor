using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

/// <summary>화면 손질 시험: 동작 꼴 바꾸기(TAPE), 표 거르기(호기·프로파일), 칸 고치기 반영.</summary>
static class UxTests
{
    static Action<bool, string> check = (_, _) => { };

    public static void Run(Action<bool, string> c, string root)
    {
        check = c;
        var site = Site.Open(Path.Combine(root, "tests", "fixtures", "T900_DEMO"));
        Forms(site);
        Filter(site);
        Switches(root);
        Seq(root);
    }

    /// <summary>fixture 를 임시 앱 폴더(app/site/T900_DEMO)로 바이트째 복사한다(저장 시험).</summary>
    static string Copy(string root, string name)
    {
        var top = Path.Combine(Path.GetTempPath(), "tedit_ux_tests", name);
        if (Directory.Exists(top)) Directory.Delete(top, true);
        var dir = Directory.CreateDirectory(Path.Combine(top, "app", "site", "T900_DEMO")).FullName;
        foreach (var f in Directory.GetFiles(Path.Combine(root, "tests", "fixtures", "T900_DEMO"))) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        return dir;
    }

    static Statement Tag(Site s, string start) =>
        s.File("tag_table.c")!.Statements.Single(x => x.Text.TrimStart().StartsWith(start, StringComparison.Ordinal));

    /// <summary>태그 규칙 줄의 동작 하나를 꼴 낱말(바로·cm·TAPE·TAPE_GD) w 로 바꾼 글자.</summary>
    static string Switch(Site s, Arg action, string w)
    {
        var call = (CallNode?)(action.Node as CallNode);
        var name = call?.Name ?? ((NameNode)action.Node).Name;
        var args = call?.Args ?? [];
        var rows = GrammarTable.Find(name, Place.TagAction).Where(r => r.TagCells > 0).ToList();
        var from = Facts.Fit(rows, args)!;
        var forms = ActionForms.Of(from);
        var to = forms.First(e => (ActionForms.Word(e) is { Length: > 0 } x ? x : "바로") == w);
        return ActionForms.Convert(to, from, args, spec => spec.Kind == ArgKind.Word ? spec.Words![0] : "0");
    }

    static void Forms(Site s)
    {
        var seq = GrammarTable.Find("SEQ_START", Place.TagAction).First();
        var stop = GrammarTable.Find("AGV_STOP", Place.TagAction).First();
        check(ActionForms.Of(seq).Select(ActionForms.Word).SequenceEqual(["", "cm", "TAPE", "TAPE_GD"]), "SEQ_START 꼴 넷");
        check(ActionForms.Of(stop).Select(ActionForms.Word).SequenceEqual(["", "cm", "TAPE", "TAPE_GD", "cm 센서"]), "AGV_STOP 꼴 다섯");
        check(ActionForms.Of(GrammarTable.Find("AGV_TURN", Place.TagAction).First()).Count == 1, "AGV_TURN 꼴 하나");

        // TAG( 40, PF_BELT, SEQ_START( LOAD ), … ): 바로 → TAPE → TAPE_GD → cm → 바로, 시퀀스는 그대로
        var st = Tag(s, "TAG( 40, PF_BELT");
        var r = new TagRule(st);
        var a = r.Actions[0];
        check(Switch(s, a, "TAPE") == "SEQ_START( TAPE, LOAD )", "SEQ_START 바로 → TAPE " + Switch(s, a, "TAPE"));
        var e = SiteEdit.SetArg(s, st, a, Switch(s, a, "TAPE"));
        check(e.Ok, "TAPE 쓰기");
        var s2 = e.Site!;
        var st2 = (Statement)e.Touched[0];
        check(st2.Text.Contains("SEQ_START( TAPE, LOAD ), AGV_FORBACK( TOGGLE )", StringComparison.Ordinal), "그 인자만: " + st2.Text.Trim());
        check(Checker.Run(s2).All(f => f.Severity != Severity.Error), "TAPE 오류 0");
        var a2 = new TagRule(st2).Actions[0];
        check(Switch(s2, a2, "TAPE_GD") == "SEQ_START( TAPE_GD, LOAD )", "TAPE → TAPE_GD");
        check(Switch(s2, a2, "cm") == "SEQ_START( 0, LOAD )", "TAPE → cm " + Switch(s2, a2, "cm"));
        check(Switch(s2, a2, "바로") == "SEQ_START( LOAD )", "TAPE → 바로");

        // TAG( 41, PF_BELT, SEQ_START( 20, UNLOAD ) ): cm → TAPE 는 거리를 버린다
        var a3 = new TagRule(Tag(s, "TAG( 41, PF_BELT")).Actions[0];
        check(Switch(s, a3, "TAPE") == "SEQ_START( TAPE, UNLOAD )", "cm → TAPE");

        // AGV_STOP: 바로 → TAPE, cm(30) → 센서 꼴은 거리를 옮긴다
        var stopBare = new TagRule(Tag(s, "TAG ( 1,")).Actions[0];
        check(Switch(s, stopBare, "TAPE") == "AGV_STOP( TAPE )", "AGV_STOP 바로 → TAPE " + Switch(s, stopBare, "TAPE"));
        var stop30 = new TagRule(Tag(s, "TAG ( 6,")).Actions[0];
        check(Switch(s, stop30, "TAPE_GD") == "AGV_STOP( TAPE_GD )", "AGV_STOP cm → TAPE_GD");
        check(Switch(s, stop30, "cm 센서").StartsWith("AGV_STOP( 30, ", StringComparison.Ordinal), "AGV_STOP cm → 센서 거리 옮김 " + Switch(s, stop30, "cm 센서"));
        var fl = new TagRule(Tag(s, "TAG( 40, PF_CART")).Actions[0];
        check(Switch(s, fl, "cm") == "AGV_STOP( 0 )", "센서 FL → cm 은 FL 안 옮김 " + Switch(s, fl, "cm"));
        check(Switch(s, new TagRule(Tag(s, "TAG ( 8,")).Actions[0], "바로") == "AGV_STOP", "TAPE → 바로");
    }

    /// <summary>거르기: 태그 화면(태그 번호)과 시퀀스 화면(호기)이 같은 <see cref="RowFilter.Match"/> 를 쓴다.</summary>
    static void Filter(Site s)
    {
        check(s.ProfilesAt(5).SequenceEqual(["PF_BELT"]) && s.ProfilesAt(25).SequenceEqual(["PF_CART"]) && s.ProfilesAt(50).Count == 0, "호기 → 프로파일");

        // 시퀀스 대응표를 호기·프로파일로(나무와 같은 규칙: 전체 줄은 모든 호기)
        List<string> Seqs(string text, string profile) => s.SeqMap.Where(m =>
        {
            var key = m.AnyProfile ? RowFilter.All : m.Profile;
            return RowFilter.Match(text, profile, key, m.AnyProfile, n => m.AnyProfile || s.ProfilesAt(n).Contains(key));
        }).Select(m => m.Table).ToList();
        check(Seqs("", RowFilter.All).Count == s.SeqMap.Count, "거르기 없음 = 모두");
        check(Seqs("12", RowFilter.All).SequenceEqual(["lift_load", "lift_unload", "shuttle"]), "호기 12 " + string.Join(",", Seqs("12", RowFilter.All)));
        check(Seqs("", "PF_BOX").SequenceEqual(["box_turn_unload", "shuttle"]), "PF_BOX " + string.Join(",", Seqs("", "PF_BOX")));
        check(Seqs("12", "PF_BOX").SequenceEqual(["shuttle"]), "호기 12 + PF_BOX = 전체 줄만");
        check(Seqs("abc", RowFilter.All).Count == 0, "숫자 아님 = 없음");

        // 태그 화면 꼴: 원문 줄(프로파일 없음)은 프로파일을 고르면 빠진다
        check(RowFilter.Match("", "PF_BELT", null, false, _ => true) == false && RowFilter.Match("", RowFilter.All, null, false, _ => true), "원문 줄");
        check(RowFilter.Match(" 40 ", "PF_LIFT", "ANY_PROFILE", true, n => n == 40), "태그 40 + 전체 줄");
    }

    /// <summary>켬/끔 칸: 문법 표 표시 자리, 화면 글 ON·OFF, 고치면 원문에 1·0(저장 바이트 비교).</summary>
    static void Switches(string root)
    {
        var marked = GrammarTable.Rows.SelectMany(r => r.Args.Select((a, i) => (r, a, i))).Where(x => x.a.Switch)
            .Select(x => $"{x.r.Name}/{x.r.Place}/{x.i}").ToList();
        check(marked.SequenceEqual(["AGV_TURN/Step/3", "AGV_TURN/TagAction/3"]), "켬/끔 칸 = AGV_TURN 자동 출발 둘: " + string.Join(",", marked));

        var dir = Copy(root, "switch");
        var before = File.ReadAllBytes(Path.Combine(dir, "tag_table.c"));
        var s = Site.Open(dir);
        var st = Tag(s, "TAG ( 21,");
        var turn = (CallNode)new TagRule(st).Actions[0].Node;
        var specs = Facts.Expand(Facts.Fit(GrammarTable.Find("AGV_TURN", Place.TagAction), turn.Args)!, turn.Args.Count)!;
        check(specs[3].SwitchText(turn.Args[3].Node) == ArgSpec.On, "자동 출발 1 = ON");
        check(specs[2].SwitchText(turn.Args[2].Node) == null && specs[4].SwitchText(turn.Args[4].Node) == null, "회전 뒤 0·속도 0 은 숫자 그대로");
        check(specs[3].SwitchText(new NumberNode(5, "5")) == null, "켬/끔 칸의 다른 값(5)은 숫자");

        var e = SiteEdit.SetArg(s, st, turn.Args[3], ArgSpec.SwitchRaw(ArgSpec.Off));
        check(e.Ok, "OFF 쓰기");
        var res = SaveFiles.Save(e.Site!);
        check(res.Status == SaveStatus.Saved, "저장 " + res.Status);
        var text = System.Text.Encoding.UTF8.GetString(before);
        var want = System.Text.Encoding.UTF8.GetBytes(text.Replace("AGV_TURN( CW,  90,  0, 1, 0 )", "AGV_TURN( CW,  90,  0, 0, 0 )"));
        check(File.ReadAllBytes(Path.Combine(dir, "tag_table.c")).AsSpan().SequenceEqual(want), "저장 바이트 = 그 칸만 1 → 0");
    }

    /// <summary>시퀀스 구조 편집: 넣기·명령 바꾸기·옮기기·지우기 뒤 원문 그대로, 라벨·표 참조, 표 지우기 저장 바이트 비교.</summary>
    static void Seq(string root)
    {
        var dir = Copy(root, "seq");
        var path = Path.Combine(dir, "seq_table.c");
        var before = File.ReadAllBytes(path);
        var s = Site.Open(dir);
        ZoneBlock Z(Site x, string n) => x.Zones.Single(z => z.Open == "SEQ_BEGIN" && z.Name == n);
        string Fill(ArgSpec a) => a.Kind == ArgKind.InCond ? "NO_IN" : a.Kind == ArgKind.Word ? a.Words![0] : "0";

        check(SeqEdit.StepRows(act: false).All(r => r.Place == Place.Step && r.Role == Role.Statement), "스텝 줄만");
        check(SeqEdit.StepRows(act: true).All(r => !r.ActForbidden) && SeqEdit.StepRows(false).Any(r => r.ActForbidden), "ACT 표면 ACT 금지 줄 뺌");
        check(!SeqEdit.StepRows(false).Any(r => r.Args.Any(a => a.Words?.Contains("TAPE") == true && r.Name == "AGV_TURN")), "회전 TAPE(태그 전용) 없음");

        var e = SiteEdit.Insert(s, Z(s, "belt_unload"), 1, [new NewStatement("DELAY", ["100"])]);
        check(e.Ok, "넣기");
        var st = (Statement)e.Touched[0];
        var e2 = SeqEdit.Change(e.Site!, st, GrammarTable.Find("WAIT_STABLE", Place.Step).Single(), Fill);
        check(e2.Ok && ((Statement)e2.Touched[0]).Text.Trim() == "WAIT_STABLE( NO_IN, 100 )", "명령 바꾸기: 시간 옮김 " + (e2.Ok ? e2.Touched[0].Text.Trim() : e2.Error));
        var s3 = e2.Site!;
        var e3 = SiteEdit.Move(s3, [e2.Touched[0]], Z(s3, "belt_unload"), Z(s3, "belt_unload").Statements.Count);
        check(e3.Ok && Z(e3.Site!, "belt_unload").Statements[^1].Name == "WAIT_STABLE", "끝으로 옮김");
        var e4 = SiteEdit.Delete(e3.Site!, [e3.Touched[0]]);
        check(e4.Ok && e4.Site!.File("seq_table.c")!.ToBytes().AsSpan().SequenceEqual(before), "지우면 바이트 그대로");

        var run = Z(s, "belt_unload").Statements.Single(x => x.Name == "L" && x.Args[0].Text.Trim() == "RUN");
        check(SeqEdit.LabelUses(Z(s, "belt_unload"), run).Count == 1, "라벨 RUN 쓰는 줄 1");
        check(SeqEdit.TableUses(s, "belt_load").Any(u => u.Name.StartsWith("TAG", StringComparison.Ordinal)), "belt_load: 태그 SEQ_START( LOAD )");
        var uses = SeqEdit.TableUses(s, "shuttle");
        check(uses.Count == 1 && uses[0].Name == "SEQ_MAP", "shuttle: 대응표 1줄만 " + uses.Count);
        check(SeqEdit.TableUses(s, "belt2_run_5s").Any(u => u.Name == "ACT_START" || u.Name == "WAIT_ACT"), "ACT 표: 스텝 ACT_START·WAIT_ACT");

        var nt = BundleEdit.AddSeqTable(s, "demo_new", "PF_BOX", "SEQ_LOAD");
        var ntErr = nt.Ok ? Checker.Run(nt.Site!).Where(f => f.Severity == Severity.Error).Select(f => f.Text).ToList() : [nt.Error!];
        check(ntErr.Count == 0, "표 추가 오류 0: " + string.Join(", ", ntErr));
        check(SeqEdit.TableUses(nt.Site!, "demo_new") is { Count: 1 } nu && nu[0].Name == "SEQ_MAP", "PF_BOX SEQ_LOAD 표: 다른 프로파일 태그는 참조 아님");
        var nd = SeqEdit.DeleteTable(nt.Site!, "demo_new");
        check(nd.Ok && nd.Site!.File("seq_table.c")!.ToBytes().AsSpan().SequenceEqual(before), "추가한 표 지우면 바이트 그대로(빈 줄 포함)");

        var d = SeqEdit.DeleteTable(s, "shuttle");
        check(d.Ok && Checker.Run(d.Site!).All(f => f.Severity != Severity.Error), "shuttle 지움·오류 0");
        check(SaveFiles.Save(d.Site!).Status == SaveStatus.Saved, "저장");
        var text = System.Text.Encoding.UTF8.GetString(before);
        int a0 = text.IndexOf("SEQ_BEGIN( shuttle )", StringComparison.Ordinal);
        int a1 = text.IndexOf("SEQ_END", a0, StringComparison.Ordinal);
        a1 = text.IndexOf('\n', a1) + 1;
        var map = "    SEQ_MAP( 0,         SEQ_DEMO_SHUTTLE,   shuttle )\r\n";
        var want = text[..a0] + text[a1..];
        check(want.Contains(map), "대응표 줄 글자");
        want = want.Replace(map, "");
        check(File.ReadAllBytes(path).AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(want)), "저장 바이트 = 그 표·그 대응표 줄만 빠짐(주석·빈 줄 그대로)");
    }
}
