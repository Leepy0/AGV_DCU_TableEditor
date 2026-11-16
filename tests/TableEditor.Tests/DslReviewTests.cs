using System.Text;
using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Ops;
using TableEditor.Engine.Text;

/// <summary>
/// v0.2.1 독립 검토 10건의 재현 시험. 조건 쓰기의 옛·새 낱말 섞임(1), 같은 뜻 규칙의 죽은 줄(2), 옛 코어의 새 문법(3), ANY_ON 여럿(4),
/// 팝업 OK 만으로 줄이 바뀜(5), 사이트 #define 이 새 낱말을 덮음(6), 형식 바꾸기가 남기는 상태(7), D-5 칸 앞 주석(8), check.ps1 의 $Cgt(9).
/// 10(Ctrl+Z)은 화면이라 자체 시험(SelfTest.V021)에서 본다. 이름은 모두 지어낸 것.
/// </summary>
static class DslReviewTests
{
    static readonly UTF8Encoding Utf8 = new(false);
    static Action<bool, string> check = (_, _) => { };

    static string Core(string name, string version)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tedit_dslreview_tests", name);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "VERSION"), version + " 0000000 cl2000 v0\n", Utf8);
        return dir;
    }

    static Site TagSite(string? core, string head, params string[] lines) =>
        Site.FromFiles([SourceFile.Parse("tag_table.c", Utf8.GetBytes(head + "\nTAG_TABLE_BEGIN( site_tags )\n" + string.Concat(lines.Select(l => "    " + l + "\n")) + "TAG_TABLE_END\n"))], "", core);

    static Site SeqSite(string? core, string head, params string[] lines) =>
        Site.FromFiles([SourceFile.Parse("seq_table.c", Utf8.GetBytes(head + "\nSEQ_LABELS( DONE, ERR )\nSEQ_BEGIN( t )\n" + string.Concat(lines.Select(l => "    " + l + "\n")) + "    L( DONE )\n    END_STOP\nSEQ_END\n"))], "", core);

    static List<Finding> Rule(Site s, string rule) => Checker.Run(s).Where(f => f.Rule == rule).ToList();

    static XCond X(string n, params string[] a) => new(n, a);

    public static void Run(Action<bool, string> c)
    {
        check = c;
        var n = Core("n", "v0.58.0");
        var o = Core("o", "v0.57.0");
        Item1(n);
        Item2(n);
        Item3(n, o);
        Item4(n);
        Item5(n);
        Item6(n);
        Item7(n);
        Item8(n);
        Item9();
    }

    // 1: X_STATION_RUNNING 처럼 새 낱말이 없는 조건이 하나라도 있으면 줄 전체를 옛 형식으로, 상태 낱말은 X_… 로 되돌린다
    static void Item1(string n)
    {
        string? C(IReadOnlyList<XCond> x, string? input) => TagEdit.CondText(x, input, true);
        check(C([X("FORWARD"), X("X_STATION_RUNNING", "1", "5", "9")], null) == "IF_X2( X_FORWARD, X_STATION_RUNNING(1, 5, 9) )", "1 새 낱말 + 옮길 수 없는 옛 조건 = 옛 형식 · " + C([X("FORWARD"), X("X_STATION_RUNNING", "1", "5", "9")], null));
        check(C([X("X_STATION_RUNNING", "1", "5", "9"), X("NOT", "FORWARD")], "ON(DEMO_SEN_1)") == "IF_X2_IN( X_STATION_RUNNING(1, 5, 9), X_NOT(X_FORWARD), ON(DEMO_SEN_1) )", "1 NOT·입력도 옛 이름으로");
        check(C([X("PARAM_MIN", "ACC", "10"), X("X_STATION_RUNNING", "1", "5", "9")], null) == "IF_X2( X_PARAM_GE(P_ACC, 10), X_STATION_RUNNING(1, 5, 9) )", "1 PARAM_MIN → X_PARAM_GE(P_…)");
        check(C([X("CROSS_IN_WAIT"), X("ERR_IS", "281")], null) == "AND( CROSS_IN_WAIT, ERR_IS(281) )", "1 옮길 수 있으면 그대로 AND");
        check(C([X("FORWARD"), X("CROSS_IN_WAIT")], "ON(DEMO_SEN_1)") == "AND( FORWARD, CROSS_IN_WAIT, ON(DEMO_SEN_1) )", "1 AND 그대로");
        check(!TagEdit.IsMixed("IF_X2( X_FORWARD, X_STATION_RUNNING(1, 5, 9) )") && !TagEdit.IsMixed("AND( FORWARD, ON(DEMO_SEN_1) )")
            && TagEdit.IsMixed("IF_X2( FORWARD, X_STATION_RUNNING(1, 5, 9) )") && TagEdit.IsMixed("AND( FORWARD, X_STATION_RUNNING(1, 5, 9) )"), "1 섞임 판정");
        // 사이트에서: 옛 줄 + 상태 하나 더 = 옛 형식
        var site = TagSite(n, "", "TAG_IF( 65, ANY_PROFILE, IF_X( X_STATION_RUNNING(1, 5, 9) ), AGV_STOP )");
        var r = TagEdit.SetCondition(site, site.TagRules[0], [X("FORWARD"), X("X_STATION_RUNNING", "1", "5", "9")], null);
        check(r.Ok && r.Touched[0].Text.Contains("IF_X2( X_FORWARD, X_STATION_RUNNING(1, 5, 9) )", StringComparison.Ordinal), "1 사이트에서 쓴 줄 · " + (r.Ok ? r.Touched[0].Text : r.Error));
        // 못 옮기는 새 낱말(FND 이름 모름) + 옛 전용 조건 = 한 줄에 못 씀(섞인 줄을 쓰지 않는다)
        var bad = TagEdit.SetCondition(site, site.TagRules[0], [X("PARAM_MIN", "NOPE", "10"), X("X_STATION_RUNNING", "1", "5", "9")], null);
        check(!bad.Ok && bad.Error!.Contains("섞임"), "1 못 옮기면 섞인 줄 대신 막음 · " + bad.Error);
    }

    // 2: 옛 형식·새 형식으로 같은 뜻을 쓴 규칙 = 같은 조건(죽은 줄·이미 있음)
    static void Item2(string n)
    {
        var s = TagSite(n, "", "TAG_IF( 960, ANY_PROFILE, IF_FORWARD, AGV_STOP )", "TAG_IF( 960, ANY_PROFILE, AND( FORWARD ), OBS_ON )");
        check(Rule(s, "T-10").Count == 1, "2 IF_FORWARD 다음 AND( FORWARD ) = 죽은 줄 · " + Rule(s, "T-10").Count);
        var s2 = TagSite(n, "", "TAG_IF( 961, ANY_PROFILE, IF_X_IN( X_FORWARD, IN( ON(DEMO_SEN_1), ON(DEMO_SEN_2) ) ), AGV_STOP )", "TAG_IF( 961, ANY_PROFILE, AND( ON(DEMO_SEN_2), FORWARD, ON(DEMO_SEN_1) ), OBS_ON )");
        check(Rule(s2, "T-10").Count == 1, "2 상태·입력 순서·묶음이 달라도 같은 뜻 · " + Rule(s2, "T-10").Count);
        var s3 = TagSite(n, "", "TAG_IF( 962, ANY_PROFILE, IF_FORWARD, AGV_STOP )", "TAG_IF( 962, ANY_PROFILE, AND( BACKWARD ), OBS_ON )");
        check(Rule(s3, "T-10").Count == 0, "2 다른 조건은 죽은 줄 아님");
        var s4 = TagSite(n, "", "TAG_IF( 963, ANY_PROFILE, IF_FORWARD, AGV_STOP )", "TAG_IF( 964, ANY_PROFILE, AND( FORWARD ), AGV_STOP )");
        var plan = TagEdit.PlanAdd(s4, "964, 965", ["ANY_PROFILE"], s4.TagRules[0], TagRunMode.PerLine);
        check(plan.Error == null && plan.Rows.Count == 2 && plan.Rows[0].Exists && !plan.Rows[1].Exists, "2 PlanAdd: 새 형식으로 이미 있는 같은 뜻 = 있음");
    }

    // 3: 옛 코어에서 새 전용 문법 = V-4
    static void Item3(string n, string o)
    {
        var cases = new (string Kind, string Line, string Word)[]
        {
            ("seq", "ACS( NEXT, 5 )", "ACS 번호"), ("seq", "AGV_SPEED( 30 )", "AGV_SPEED"), ("seq", "WAIT( AND( FORWARD ) )", "AND"),
            ("seq", "WAIT_TO( IN( ON(DEMO_SEN_1) ), PARAM_TIME( AS, SEC ), ERR )", "PARAM_TIME"), ("seq", "OUT_SET( OUTS( DEMO_LAMP ), NO_OUT )", "OUT_SET"),
            ("tag", "TAG( 960, ANY_PROFILE, ACS( NEXT, 5 ) )", "ACS"), ("tag", "TAG_IF( 960, ANY_PROFILE, AND( FORWARD ), OBS_ON )", "AND"),
            ("tag", "TAG( 960, ANY_PROFILE, AGV_SPEED( 30 ) )", "AGV_SPEED"),
        };
        foreach (var (kind, line, word) in cases)
        {
            var old = kind == "seq" ? SeqSite(o, "", line) : TagSite(o, "", line);
            var neu = kind == "seq" ? SeqSite(n, "", line) : TagSite(n, "", line);
            var v = Rule(old, "V-4");
            check(v.Count == 1 && v[0].Severity == Severity.Error && v[0].Text.Contains("v0.58.0") && v[0].Text.Contains(word), $"3 옛 코어 V-4 · {line} · " + string.Join("|", v));
            check(Rule(neu, "V-4").Count == 0, $"3 새 코어는 없음 · {line}");
        }
        // 옛 문법·사이트가 직접 #define 한 같은 이름은 안 걸린다. 코어 폴더가 없으면 안 건다
        check(Rule(SeqSite(o, "", "ACS( NEXT, CONST )", "AGV_SPEED( LOW )", "WAIT( IN( ON(DEMO_SEN_1) ) )"), "V-4").Count == 0 && Rule(TagSite(o, "", "TAG_IF( 960, ANY_PROFILE, IF_FORWARD, AGV_STOP )"), "V-4").Count == 0, "3 옛 문법은 그대로");
        check(Rule(SeqSite(o, "#define OUT_SET( on, off )  DSL_STEP( SK_OUT, SEQ_NO_LABEL, 0, 0, 0, (on), (off), 0 )", "OUT_SET( 1, 2 )"), "V-4").Count == 0, "3 사이트 매크로 OUT_SET 은 안 걸림");
        check(Rule(TagSite(o, "#define NO_ACTION 0", "TAG( 960, ANY_PROFILE, NO_ACTION )"), "V-4").Count == 0, "3 사이트 별칭 NO_ACTION 은 안 걸림");
        check(Rule(SeqSite(null, "", "ACS( NEXT, 5 )"), "V-4").Count == 0, "3 코어 폴더 없음 = 안 건다");
    }

    // 4: ANY_ON 여럿 = ANY 하나로 합친다
    static void Item4(string n)
    {
        string? C(string input) => TagEdit.CondText([], input, true);
        check(C("IN( ANY_ON(DEMO_SEN_1, DEMO_SEN_2), ANY_ON(DEMO_SEN_3) )") == "AND( ANY(DEMO_SEN_1, DEMO_SEN_2, DEMO_SEN_3) )", "4 ANY_ON 둘 = ANY 하나 · " + C("IN( ANY_ON(DEMO_SEN_1, DEMO_SEN_2), ANY_ON(DEMO_SEN_3) )"));
        check(C("IN( ON(DEMO_SEN_1), ANY_ON(DEMO_SEN_2), OFF(DEMO_SEN_4), ANY_ON(DEMO_SEN_3) )") == "AND( ON(DEMO_SEN_1), ANY(DEMO_SEN_2, DEMO_SEN_3), OFF(DEMO_SEN_4) )", "4 첫 ANY_ON 자리에 합침");
        check(C("ANY_ON(DEMO_SEN_1, DEMO_SEN_2)") == "AND( ANY(DEMO_SEN_1, DEMO_SEN_2) )", "4 하나는 그대로 ANY");
        var many = string.Join(", ", Enumerable.Range(1, 9).Select(i => "S" + i));
        var many2 = string.Join(", ", Enumerable.Range(10, 8).Select(i => "S" + i));
        var over = C($"IN( ANY_ON({many}), ANY_ON({many2}) )");
        check(over == $"IF_IN( IN( ANY_ON({many}), ANY_ON({many2}) ) )", "4 합치면 16 넘음 = 옛 형식 유지 · " + over);
        var s = TagSite(n, "", "TAG_IF( 960, ANY_PROFILE, IF_IN( IN( ANY_ON(DEMO_SEN_1), ANY_ON(DEMO_SEN_2) ) ), AGV_STOP )");
        var r = TagEdit.SetCondition(s, s.TagRules[0], [X("FORWARD")], "IN( ANY_ON(DEMO_SEN_1), ANY_ON(DEMO_SEN_2) )");
        check(r.Ok && r.Touched[0].Text.Contains("AND( FORWARD, ANY(DEMO_SEN_1, DEMO_SEN_2) )", StringComparison.Ordinal) && Rule(r.Site!, "D-1").Count == 0 && Rule(r.Site!, "D-3").Count == 0, "4 쓴 줄에 D-1·D-3 없음 · " + (r.Ok ? r.Touched[0].Text : r.Error));
    }

    // 5: 뜻이 같으면 줄을 쓰지 않는다
    static void Item5(string n)
    {
        var s = TagSite(n, "", "TAG_IF( 960, ANY_PROFILE, IF_X_IN( X_FORWARD, NO_IN ), AGV_STOP )", "TAG_IF( 961, ANY_PROFILE, IF_X( X_STATION_RUNNING(1, 5, 9) ), AGV_STOP )",
            "TAG_IF( 962, ANY_PROFILE, IF_X2_IN( X_FORWARD, X_ID(1, 5), IN( ON(DEMO_SEN_1), ANY_ON(DEMO_SEN_2, DEMO_SEN_3) ) ), AGV_STOP )");
        foreach (var rule in s.TagRules)
        {
            var read = TagEdit.ReadCondition(rule)!.Value;
            var xs = read.X.Select(x => StateWords.ToState(x) ?? x).ToList(); // 팝업이 보이는 낱말
            var r = TagEdit.SetCondition(s, rule, xs, read.Input);
            check(r.Ok && ReferenceEquals(r.Site, s) && r.Touched.Count == 0, "5 열고 OK 만 = 줄 그대로 · " + rule.St.Args[0].Text);
        }
        var rr = TagEdit.SetCondition(s, s.TagRules[0], [X("FORWARD"), X("LOW_BATTERY")], null);
        check(rr.Ok && rr.Touched[0].Text.Contains("AND( FORWARD, LOW_BATTERY )", StringComparison.Ordinal), "5 바꾸면 새 형식 · " + (rr.Ok ? rr.Touched[0].Text : rr.Error));
        var no = TagEdit.SetCondition(s, s.TagRules[0], [], null);
        check(no.Ok && no.Touched[0].Text.TrimStart().StartsWith("TAG(", StringComparison.Ordinal), "5 조건을 없애면 TAG");
    }

    // 6: 사이트 #define 이 새 낱말을 덮는다
    static void Item6(string n)
    {
        var warn = (Site s) => Rule(s, "N-5").Where(f => f.Severity == Severity.Warning && f.Text.Contains("새 낱말")).ToList();
        var err = (Site s) => Rule(s, "N-5").Where(f => f.Severity == Severity.Error && f.Text.Contains("AND 안")).ToList();
        var used = TagSite(n, "#define LOW_BATTERY 1", "TAG_IF( 960, ANY_PROFILE, AND( LOW_BATTERY ), OBS_ON )");
        check(warn(used).Count == 1 && err(used).Count == 1, $"6 새 낱말 #define + AND 안 사용 = 경고 + 오류 ({warn(used).Count}, {err(used).Count})");
        var unused = TagSite(n, "#define NO_ACTION 0", "TAG( 960, ANY_PROFILE, NO_ACTION )");
        check(warn(unused).Count == 1 && err(unused).Count == 0, "6 AND 밖(NO_ACTION)은 경고만");
        var param = TagSite(n, "#define LDS 7", "TAG_IF( 960, ANY_PROFILE, AND( PARAM(LDS, 1) ), OBS_ON )");
        check(err(param).Count == 1 && err(param)[0].Text.Contains("PARAM"), "6 PARAM(FND) 이름이 매크로 = 오류(gcc 로 깨짐 확인)");
        var key = SeqSite(n, "#define UP 7", "WAIT( AND( KEY(UP) ) )");
        check(err(key).Count == 1 && err(key)[0].Text.Contains("KEY"), "6 KEY(키) 이름이 매크로 = 오류");
        var oldUse = TagSite(n, "#define LDS 7", "TAG_IF( 960, ANY_PROFILE, IF_PARAM(LDS, 1), OBS_ON )");
        check(err(oldUse).Count == 0 && warn(oldUse).Count == 0, "6 옛 IF_PARAM 은 안 깨짐");
        var oldCore = TagSite(Core("o6", "v0.57.0"), "#define LOW_BATTERY 1", "TAG_IF( 960, ANY_PROFILE, AND( LOW_BATTERY ), OBS_ON )");
        check(warn(oldCore).Count == 0 && err(oldCore).Count == 0, "6 옛 코어는 안 건다");
        check(warn(TagSite(null, "#define ID 5\n#define FORWARD 1", "TAG( 1, ANY_PROFILE, AGV_STOP )")).Count == 0, "6 코어 없음: 겹침 아님(기존 시험)");
    }

    // 7: 형식을 PARAM_TIME 으로 바꾸면 상태·둘째 ANY 는 버린다
    static void Item7(string n)
    {
        var from = GrammarTable.Find("WAIT", Place.Step).First();
        var to = GrammarTable.Find("WAIT_TO", Place.Step).First(e => e.Args.Any(a => a.Kind == ArgKind.ParamTime));
        var s = SeqSite(n, "", "WAIT( AND( FORWARD, ON(DEMO_SEN_1), ANY(DEMO_SEN_2, DEMO_SEN_3), ANY(DEMO_SEN_4, DEMO_SEN_5) ) )", "WAIT( AND( ID(1, 2) ) )", "WAIT( IN( ON(DEMO_SEN_1) ) )");
        var st = s.File("seq_table.c")!.Statements.Where(x => x.Name == "WAIT").ToList();
        string Fill(ArgSpec a) => a.Kind == ArgKind.ParamTime ? "PARAM_TIME( AS, SEC )" : "DONE";
        var text = ActionForms.Convert(to, from, st[0].Args, Fill);
        check(text == "WAIT_TO( AND( ON(DEMO_SEN_1), ANY(DEMO_SEN_2, DEMO_SEN_3) ), PARAM_TIME( AS, SEC ), DONE )", "7 상태·둘째 ANY 를 버림 · " + text);
        var drops = ActionForms.Dropped(to, from, st[0].Args);
        check(drops.SequenceEqual(["FORWARD", "ANY(DEMO_SEN_4, DEMO_SEN_5)"]), "7 버리는 인자 목록(값 버림 팝업) · " + string.Join(" / ", drops));
        var r = SeqEdit.Change(s, st[0], to, Fill);
        check(r.Ok && Rule(r.Site!, "D-1").Count == 0 && Rule(r.Site!, "D-3").Count == 0, "7 바꾼 줄에 D-1 없음");
        check(ActionForms.Convert(to, from, st[1].Args, Fill) == "WAIT_TO( NO_IN, PARAM_TIME( AS, SEC ), DONE )" && ActionForms.Dropped(to, from, st[1].Args).SequenceEqual(["ID(1, 2)"]), "7 입력이 없으면 NO_IN");
        check(ActionForms.Convert(to, from, st[2].Args, Fill) == "WAIT_TO( IN( ON(DEMO_SEN_1) ), PARAM_TIME( AS, SEC ), DONE )" && ActionForms.Dropped(to, from, st[2].Args).Count == 0, "7 입력만 있는 줄은 그대로");
    }

    // 8: D-5 괄호 검사는 문장으로 읽힌 줄도, 주석을 지운 글로 본다
    static void Item8(string n)
    {
        check(Rule(SeqSite(n, "", "ACS( NEXT, /* c */ (70 + 7) )"), "D-5").Count == 1, "8 칸 앞 주석이 있어도 걸림");
        check(Rule(SeqSite(n, "", "AGV_SPEED( /* c */ (20 + 10) )"), "D-5").Count == 1, "8 AGV_SPEED 칸 앞 주석");
        check(Rule(TagSite(n, "", "TAG( 960, ANY_PROFILE, ACS( NEXT, // x\n (7 + 7) ) )"), "D-5").Count == 1, "8 태그 동작 안 + // 주석");
        check(Rule(SeqSite(n, "", "DELAY( 5 )  /* ACS( NEXT, ( */", "ACS( NEXT, 7 ) // AGV_SPEED( ("), "D-5").Count == 0, "8 주석 글은 안 본다");
        check(Rule(SeqSite(n, "", "ACS( NEXT, (70 + 7) )"), "D-5").Count == 1, "8 기존: 주석 없는 괄호식");
    }

    // 9: check.ps1 의 param( … ) 블록 안 $Cgt 만
    static void Item9()
    {
        var root = Path.Combine(Path.GetTempPath(), "tedit_dslreview_tests", "app");
        Directory.CreateDirectory(Path.Combine(root, "tools"));
        bool Knows(string script)
        {
            File.WriteAllText(Path.Combine(root, "tools", "check.ps1"), script, Utf8);
            return SiteBuild.KnowsCgt(root);
        }
        check(Knows("param([string]$Site, [string]$Cgt = 'x')\n"), "9 param 안 $Cgt");
        check(Knows("# 설명\r\nparam(\r\n    [string]$Site = 'T1',\r\n    [string]$cgt = ''\r\n)\r\n"), "9 소문자·여러 줄");
        check(!Knows("param([string]$Site)\n$Cgt = 'x'\nWrite-Output $Cgt\n"), "9 지역 변수만 = 모름");
        check(!Knows("# -Cgt $Cgt 설명\nparam([string]$Site) # $Cgt\n"), "9 주석의 $Cgt = 모름");
        check(!Knows("param([string]$Site)\nfunction f { param($Cgt) }\n"), "9 다른 함수 매개변수 = 모름");
        check(!Knows("Write-Output 1\n"), "9 param 없음");
    }
}
