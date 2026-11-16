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
        Guards(root);
        Refs(root);
        Followups();
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

        // 버려지는 인자(검토 7): 꼴 낱말(TAPE)은 꼴 자체라 세지 않는다
        IReadOnlyList<string> Drop(Arg act, string w)
        {
            var c = act.Node as CallNode;
            var args = c?.Args ?? [];
            var from = Facts.Fit(GrammarTable.Find(c?.Name ?? ((NameNode)act.Node).Name, Place.TagAction).Where(r => r.TagCells > 0), args)!;
            return ActionForms.Dropped(ActionForms.Of(from).First(e => (ActionForms.Word(e) is { Length: > 0 } x ? x : "바로") == w), from, args);
        }
        check(Drop(stop30, "TAPE").SequenceEqual(["30"]), "cm → TAPE 는 30 버림");
        check(Drop(a2, "TAPE_GD").Count == 0 && Drop(a2, "바로").Count == 0, "TAPE → TAPE_GD·바로 는 버릴 것 없음");
        check(Drop(fl, "cm").SequenceEqual(["FL", "FROM_SENSOR( DEMO_SEN_4, 100 )"]), "센서 → cm 은 FL·FROM_SENSOR 버림 " + string.Join(" | ", Drop(fl, "cm")));
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
        string Fill(ArgSpec a) => a.Kind is ArgKind.InCond or ArgKind.AndCond ? "NO_IN" : a.Kind == ArgKind.Word ? a.Words![0] : "0";

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
        check(SeqEdit.TableUses(s, "belt_load").Blocking.Any(u => u.Name.StartsWith("TAG", StringComparison.Ordinal)), "belt_load: 태그 SEQ_START( LOAD )");
        var uses = SeqEdit.TableUses(s, "shuttle");
        check(uses.Maps.Count == 1 && uses.Blocking.Count == 0 && uses.Maybe.Count == 0, $"shuttle: 대응표 1줄만 ({uses.Maps.Count}, {uses.Blocking.Count}, {uses.Maybe.Count})");
        check(SeqEdit.TableUses(s, "belt2_run_5s").Blocking.Any(u => u.Name == "ACT_START" || u.Name == "WAIT_ACT"), "ACT 표: 스텝 ACT_START·WAIT_ACT");

        var nt = BundleEdit.AddSeqTable(s, "demo_new", "PF_BOX", "SEQ_LOAD");
        var ntErr = nt.Ok ? Checker.Run(nt.Site!).Where(f => f.Severity == Severity.Error).Select(f => f.Text).ToList() : [nt.Error!];
        check(ntErr.Count == 0, "표 추가 오류 0: " + string.Join(", ", ntErr));
        check(SeqEdit.TableUses(nt.Site!, "demo_new") is { Maps.Count: 1, Blocking.Count: 0 }, "PF_BOX SEQ_LOAD 표: 다른 프로파일 태그는 참조 아님");
        var nd = SeqEdit.DeleteTable(nt.Site!, "demo_new");
        check(nd.Ok && nd.Site!.File("seq_table.c")!.ToBytes().AsSpan().SequenceEqual(before), "추가한 표 지우면 바이트 그대로(빈 줄 포함)");

        var d = SeqEdit.DeleteTable(s, "shuttle");
        check(d.Ok && Checker.Run(d.Site!).All(f => f.Severity != Severity.Error), "shuttle 지움·오류 0");
        check(SaveFiles.Save(d.Site!).Status == SaveStatus.Saved, "저장");
        var text = System.Text.Encoding.UTF8.GetString(before);
        int a0 = text.IndexOf("SEQ_BEGIN( shuttle )", StringComparison.Ordinal);
        a0 = text.LastIndexOf('\n', a0 - 2) + 1; // 붙은 머리 주석 줄부터
        check(text[a0..].StartsWith("/* ---- ", StringComparison.Ordinal), "머리 주석 줄");
        int a1 = text.IndexOf("SEQ_END", a0, StringComparison.Ordinal);
        a1 = text.IndexOf('\n', a1) + 1;
        check(text[a1..].StartsWith("\r\n", StringComparison.Ordinal), "뒤 빈 줄");
        a1 += 2; // 위가 빈 줄이라 뒤 빈 줄 한 줄도
        var map = "    SEQ_MAP( 0,         SEQ_DEMO_SHUTTLE,   shuttle )\r\n";
        var want = text[..a0] + text[a1..];
        check(want.Contains(map), "대응표 줄 글자");
        want = want.Replace(map, "");
        check(File.ReadAllBytes(path).AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(want)), "저장 바이트 = 머리 주석·표·뒤 빈 줄 하나·대응표 줄만 빠짐");

        // 이어진 빈 줄은 한 줄만, 붙은 머리 주석은 같이(검토 E5b)
        var two = "SEQ_LABELS( A )\n\nSEQ_BEGIN( t1 )\n    END_STOP\nSEQ_END\n\n\n\n/* 다음 */\nSEQ_BEGIN( t2 )\n    END_STOP\nSEQ_END\n";
        var ts = Site.FromFiles([SourceFile.Parse("seq_table.c", System.Text.Encoding.UTF8.GetBytes(two))]);
        var r1 = SeqEdit.DeleteTable(ts, "t1");
        check(r1.Ok && r1.Site!.File("seq_table.c")!.ToText() == "SEQ_LABELS( A )\n\n\n\n/* 다음 */\nSEQ_BEGIN( t2 )\n    END_STOP\nSEQ_END\n",
            "뒤 빈 줄 셋 가운데 하나만: " + r1.Site?.File("seq_table.c")!.ToText().Replace("\n", "⏎"));
        var r2 = SeqEdit.DeleteTable(ts, "t2");
        check(r2.Ok && r2.Site!.File("seq_table.c")!.ToText() == "SEQ_LABELS( A )\n\nSEQ_BEGIN( t1 )\n    END_STOP\nSEQ_END\n\n\n\n",
            "붙은 머리 주석 같이: " + r2.Site?.File("seq_table.c")!.ToText().Replace("\n", "⏎"));
    }

    /// <summary>칸 글 막기(검토 1·3): 쉼표·괄호로 칸을 넘는 글, 앞자리 0. 막히면 원문 바이트 그대로. 8진 읽기·N-7.</summary>
    static void Guards(string root)
    {
        var s = Site.Open(Path.Combine(root, "tests", "fixtures", "T900_DEMO"));
        var tagBytes = s.File("tag_table.c")!.ToBytes();
        var seqBytes = s.File("seq_table.c")!.ToBytes();
        int errs = Checker.Run(s).Count(f => f.Severity == Severity.Error);

        // 1: 태그 53 ACS_SEND 번호 칸에 괄호를 닫는 글, TAG_IF 52 조건 칸에 쉼표
        var r53 = s.TagRules.First(r => r.St.Text.Contains("TAG_IF( 53, PF_LIFT,     IF_ID( 10, 12 )"));
        var num = ((CallNode)r53.Actions[0].Node).Args[1];
        var e = SiteEdit.SetArg(s, r53.St, num, "115 ), AGV_STOP( 30");
        check(!e.Ok && e.Error == "식 하나 아님", "괄호 닫는 글 막힘 " + e.Error);
        var r52 = s.TagRules.First(r => r.St.Text.Contains("TAG_IF( 52"));
        e = SiteEdit.SetArg(s, r52.St, r52.Cond!, "IF_LOW_BATTERY, AGV_STOP");
        check(!e.Ok, "조건 칸 쉼표 막힘");
        foreach (var bad in new[] { "1, 2", "(3", "3)", "AGV_STOP( 30", "a ? b", "" })
            check(SiteEdit.BadArg(bad) != null, "막힘: " + bad);
        foreach (var good in new[] { "30", "0", "0x1E", "STATION(0) + 1", "IN( ON(DEMO_SEN_1), OFF(DEMO_SEN_2) )", "-5" })
            check(SiteEdit.BadArg(good) == null, "됨: " + good);
        int blocked = 0;
        foreach (var st in s.Seqs.SelectMany(t => t.Steps).Where(x => x.Args.Count > 0))
            for (int i = 0; i < st.Args.Count; i++)
                if (!SiteEdit.SetArg(s, st, st.Args[i], st.Args[i].Text + ", 0").Ok) blocked++;
        check(blocked == s.Seqs.SelectMany(t => t.Steps).Sum(x => x.Args.Count), "시퀀스 칸 모두 ', 0' 막힘");
        check(s.File("tag_table.c")!.ToBytes().AsSpan().SequenceEqual(tagBytes) && s.File("seq_table.c")!.ToBytes().AsSpan().SequenceEqual(seqBytes)
            && Checker.Run(s).Count(f => f.Severity == Severity.Error) == errs, "막힌 뒤 원문 바이트 그대로");

        // 3: 앞자리 0
        var r6 = s.TagRules.First(r => r.St.Text.Contains("TAG ( 6,"));
        var stop = ((CallNode)r6.Actions[0].Node).Args[0];
        e = SiteEdit.SetArg(s, r6.St, stop, "030");
        check(!e.Ok && e.Error!.StartsWith("앞자리 0", StringComparison.Ordinal), "030 막힘 " + e.Error);
        check(SiteEdit.BadArg("-010") != null && SiteEdit.BadArg("08") != null, "-010·08 막힘");
        check(CNum.Value("030") == 24 && CNum.Value("0x1E") == 30 && CNum.Value("0") == 0 && CNum.Value("08") == null && CNum.Value("-010") == -8 && CNum.Value("17u") == 17, "C 정수 값");
        var text = System.Text.Encoding.UTF8.GetString(tagBytes).Replace("AGV_STOP( 30 ), AGV_AUTOSTART( 5 )", "AGV_STOP( 030 ), AGV_AUTOSTART( 5 )");
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var f = SourceFile.Parse("tag_table.c", bytes);
        check(f.ToBytes().AsSpan().SequenceEqual(bytes), "030 왕복 바이트");
        var s8 = Site.FromFiles(s.Files.Select(x => x.Name == "tag_table.c" ? f : x));
        var st8 = s8.TagRules.First(r => r.St.Text.Contains("TAG ( 6,"));
        check(s8.Names.Value(((CallNode)st8.Actions[0].Node).Args[0].Node) == 24, "030 = 24(8진)");
        check(Checker.Run(s8).Any(x => x.Rule == "N-7" && x.Severity == Severity.Warning), "N-7 경고");
        var e8 = SiteEdit.SetArg(s8, st8.St, ((CallNode)st8.Actions[1].Node).Args[0], "7");
        check(e8.Ok && e8.Site!.File("tag_table.c")!.ToBytes().AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(text.Replace("AGV_STOP( 030 ), AGV_AUTOSTART( 5 )", "AGV_STOP( 030 ), AGV_AUTOSTART( 7 )"))),
            "옆 칸 고쳐도 030 글자 그대로(바이트)");
    }

    /// <summary>표 참조(검토 2): 함수 꼴 별칭·원문 태그 줄은 알림, 식 ID·숫자 프로파일·숫자 ACT 번호는 값으로 막음.</summary>
    static void Refs(string root)
    {
        var fix = Path.Combine(root, "tests", "fixtures", "T900_DEMO");
        var utf8 = new System.Text.UTF8Encoding(false);
        var files = Directory.GetFiles(fix).Select(p => SourceFile.Parse(Path.GetFileName(p), File.ReadAllBytes(p))).ToList();
        var tag = files.Single(f => f.Name == "tag_table.c").ToText();
        var seq = files.Single(f => f.Name == "seq_table.c").ToText();
        Site With(params (string Name, string Text)[] repl) => Site.FromFiles(files.Select(f => repl.FirstOrDefault(r => r.Name == f.Name) is { Name: not null } r ? SourceFile.Parse(f.Name, utf8.GetBytes(r.Text)) : f));
        const string obs = "TAG ( 27,  ANY_PROFILE,  OBS_OFF )";
        check(tag.Contains(obs) && tag.Contains("#define CALL_T(n)"), "fixture 글");

        // (a) 함수 꼴 별칭
        var a = SeqEdit.TableUses(With(("tag_table.c", tag.Replace("#define CALL_T(n)", "#define SEQ_T(n)       SEQ_START( n )\n#define CALL_T(n)")
            .Replace(obs, "TAG ( 27,  ANY_PROFILE,  SEQ_T( DEMO_SHUTTLE ) )"))), "shuttle");
        check(a.Maybe.Any(p => p.Text.Contains("SEQ_T( DEMO_SHUTTLE )")), "(a) 함수 꼴 별칭 → 확인 필요");
        // (b) 인자 안 주석으로 원문인 태그 줄
        var b = SeqEdit.TableUses(With(("tag_table.c", tag.Replace(obs, "TAG ( 27,  ANY_PROFILE,  /* 셔틀 */ SEQ_START( DEMO_SHUTTLE ) )"))), "shuttle");
        check(b.Maybe.Any(p => p.Kind == PieceKind.Raw && p.Text.Contains("DEMO_SHUTTLE")), "(b) 원문 태그 줄 → 확인 필요");
        // (c) 대응표 ID 를 식으로
        var c = SeqEdit.TableUses(With(("seq_table.c", seq.Replace("SEQ_MAP( 0,         SEQ_DEMO_SHUTTLE,   shuttle )", "SEQ_MAP( 0,         SEQ_SITE_BASE + 1,  shuttle )")),
            ("tag_table.c", tag.Replace(obs, "TAG ( 27,  ANY_PROFILE,  SEQ_START( DEMO_SHUTTLE ) )"))), "shuttle");
        check(c.Blocking.Count == 1, "(c) 식 ID 값으로 막음 " + c.Blocking.Count);
        // (d) 태그 프로파일 숫자(PF_BOX = 4)
        var dd = SeqEdit.TableUses(With(("tag_table.c", tag.Replace("TAG( 41, PF_BOX,    SEQ_START( DEMO_TURN ) )", "TAG( 41, 4,    SEQ_START( DEMO_TURN ) )"))), "box_turn_unload");
        check(dd.Blocking.Count == 1, "(d) 숫자 프로파일 값으로 막음 " + dd.Blocking.Count);
        // (e) ACT 번호 숫자
        var site = Site.FromFiles(files);
        var actVal = site.Names.Find("ACT_DEMO_BELT")!.Value!.Value;
        var e = SeqEdit.TableUses(With(("tag_table.c", tag.Replace(obs, $"TAG ( 27,  ANY_PROFILE,  ACT_START( {actVal} ) )"))), "belt2_run_5s");
        check(e.Blocking.Any(x => x.Text.Contains($"ACT_START( {actVal} )")), "(e) 숫자 ACT 번호 값으로 막음");
    }

    /// <summary>재검토: 머리 주석 여러 조각·앞 표 꼬리 주석(바이트), 분기 짝 깊이, 원문 줄 뒤에 넣기.</summary>
    static void Followups()
    {
        var utf8 = new System.Text.UTF8Encoding(false);
        Site S(string t) => Site.FromFiles([SourceFile.Parse("seq_table.c", utf8.GetBytes(t))]);
        bool Same(EditResult r, string want) => r.Ok && r.Site!.File("seq_table.c")!.ToBytes().AsSpan().SequenceEqual(utf8.GetBytes(want));

        const string head = "SEQ_LABELS( A )\n\nSEQ_BEGIN( t1 )\n    END_STOP\nSEQ_END\n/* t1 꼬리 */\nSEQ_BEGIN( t2 )\n    END_STOP\nSEQ_END\n";
        const string tail = "\n/* 머리 1 */\n/* 머리 2 */\nSEQ_BEGIN( t3 )\n    END_STOP\nSEQ_END\n";
        var site = S(head + tail);
        check(Same(SeqEdit.DeleteTable(site, "t3"), head + "\n"), "(3) 이어 붙은 머리 주석 모두 지움(바이트)");
        check(Same(SeqEdit.DeleteTable(site, "t2"), "SEQ_LABELS( A )\n\nSEQ_BEGIN( t1 )\n    END_STOP\nSEQ_END\n/* t1 꼬리 */\n" + tail),
            "(4) 앞 표 끝에 붙은 꼬리 주석은 남김(바이트)");

        var nest = S("SEQ_LABELS( A )\nSEQ_BEGIN( t )\n    SELECT_BEGIN\n    SELECT_BEGIN\n    SELECT_END\n    SELECT_END\n    END_STOP\nSEQ_END\n");
        var z = nest.Seqs[0].Zone;
        var st = z.Statements;
        check(ReferenceEquals(SeqEdit.SelectPartner(z, st[0]), st[3]) && ReferenceEquals(SeqEdit.SelectPartner(z, st[1]), st[2])
            && ReferenceEquals(SeqEdit.SelectPartner(z, st[3]), st[0]), "(2) 분기 짝 깊이");
        var broken = S("SEQ_LABELS( A )\nSEQ_BEGIN( t )\n    SELECT_BEGIN\n    SELECT_BEGIN\n    SELECT_END\n    END_STOP\nSEQ_END\n");
        check(SeqEdit.SelectPartner(broken.Seqs[0].Zone, broken.Seqs[0].Zone.Statements[0]) == null, "(2) 짝 없음 = null");

        var raw = S("SEQ_LABELS( A )\nSEQ_BEGIN( t )\n    OUT_ON( /* x */ O1 )\n    END_STOP\nSEQ_END\n");
        var rz = raw.Seqs[0].Zone;
        var rawPiece = rz.Body.First(p => p.Kind == PieceKind.Raw);
        check(Same(SiteEdit.InsertAfter(raw, rz, rawPiece, [new NewStatement("DELAY", ["100"])]),
            "SEQ_LABELS( A )\nSEQ_BEGIN( t )\n    OUT_ON( /* x */ O1 )\n    DELAY( 100 )\n    END_STOP\nSEQ_END\n"), "(5) 원문 줄 뒤에 넣기(바이트)");
    }
}
