using System.Text;
using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

/// <summary>
/// 코어 v0.58.0 새 DSL 형식(사양 03 B): 새 형식 fixture 읽기·왕복, 상태 낱말 대응, 조건 쓰기(버전별), 헤더 대조, 이름 겹침 제외.
/// 새 줄 문법은 gcc + 코어 v0.58.0 헤더로 따로 확인했다(저장소 밖). 여기서는 편집기 쪽만 본다.
/// </summary>
static class DslTests
{
    static readonly UTF8Encoding Utf8 = new(false);

    static string Core(string name, string version)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tedit_dsl_tests", name);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "VERSION"), version + " 0000000 cl2000 v0\n", Utf8);
        return dir;
    }

    public static void Run(Action<bool, string> check, string root)
    {
        var fx = Path.Combine(root, "tests", "fixtures", "T900_DEMO", "dsl_v058");
        var site = Site.Open(fx, Core("new", "v0.58.0"));
        var old = Site.Open(fx, Core("old", "v0.57.0"));
        var none = Site.Open(fx);
        check(site.NewDsl && site.Names.NewDsl && !old.NewDsl && !none.NewDsl && !old.Names.NewDsl, "코어 버전으로 새 형식 판단");
        check(Checker.Run(site).Count(f => f.Rule is not ("V-2" or "V-3") && !(f.Rule == "N-5" && f.Text.Contains("NO_ACTION", StringComparison.Ordinal))) == 0, "새 형식 fixture + v0.58.0 = 결과 0(헤더·MANIFEST 없는 지어낸 core 의 V-2·V-3, fixture 가 정의한 NO_ACTION 겹침 경고만 뺀다)");

        // 상태 낱말 대응
        XCond X(string n, params string[] a) => new(n, a);
        string? S(XCond x) => StateWords.ToState(x)?.Text;
        check(S(X("X_FORWARD")) == "FORWARD" && S(X("X_CROSS_IN_ON")) == "CROSS_IN_WAIT" && S(X("X_CROSS_OUT_ON")) == "CROSS_OUT_WAIT" && S(X("X_CROSS_IN")) == "CROSS_IN", "대응: 이름");
        check(S(X("X_ERR_EQ", "281")) == "ERR_IS(281)" && S(X("X_ID", "5", "6")) == "ID(5, 6)" && S(X("X_TAG_NUM", "STATION(0) + 1")) == "TAG_IS(STATION(0) + 1)", "대응: 인자");
        check(S(X("X_PARAM_GE", "P_ACC", "10")) == "PARAM_MIN(ACC, 10)" && S(X("X_PARAM_LE", "P_DCC", "40")) == "PARAM_MAX(DCC, 40)" && S(X("X_PARAM_EQ", "P_SPEED_HIGH", "30")) == "PARAM(DRS, 30)", "대응: 코어 파라미터 → FND");
        check(S(X("X_NOT", "X_FORWARD")) == "NOT(FORWARD)" && S(X("X_STATION_RUNNING", "1", "5", "9")) == null && S(X("FORWARD")) == "FORWARD", "대응: NOT 안, 대응 없는 것, 새 낱말 그대로");

        // 조건 글자: 새 코어는 AND, 아니면 옛 꼴
        string? C(IReadOnlyList<XCond> x, string? input, bool n) => TagEdit.CondText(x, input, n);
        check(C([X("FORWARD")], null, true) == "AND( FORWARD )" && C([X("X_FORWARD")], null, false) == "IF_FORWARD", "상태 하나");
        check(C([X("X_ID", "5", "6"), X("X_FORWARD")], "ON(DEMO_SEN_1)", true) == "AND( ID(5, 6), FORWARD, ON(DEMO_SEN_1) )", "옛 낱말을 새 낱말로 옮겨 씀");
        check(C([], "IN( ON(DEMO_SEN_1), ANY_ON(DEMO_SEN_2, DEMO_SEN_3) )", true) == "AND( ON(DEMO_SEN_1), ANY(DEMO_SEN_2, DEMO_SEN_3) )", "입력만: IN 풀기 · ANY_ON → ANY");
        check(C([], "NO_IN", true) == null && C([], null, true) == null && C([], "ON(DEMO_SEN_1)", false) == "IF_IN( ON(DEMO_SEN_1) )", "조건 없음 · 옛 형식 입력만");
        check(C([X("X_STATION_RUNNING", "1", "5", "9")], null, true) == "IF_STATION_RUNNING( 1, 5, 9 )", "옮길 수 없는 옛 조건은 옛 꼴");

        // 읽기: AND → (상태 낱말, 입력 글)
        var rule = site.TagRules.First(r => r.St.Args[0].Text.Trim() == "63");
        var read = TagEdit.ReadCondition(rule)!.Value;
        check(read.X.Select(x => x.Text).SequenceEqual(["NOT(ID(20, 21))"]) && read.Input == "IN( ON(DEMO_SEN_1), OFF(DEMO_SEN_2) )", $"AND 읽기 ({string.Join(",", read.X.Select(x => x.Text))} / {read.Input})");
        var any = site.TagRules.First(r => r.St.Args[0].Text.Trim() == "60" && r.HasCond);
        check(TagEdit.ReadCondition(any)!.Value is { X.Count: 0, Input: "ANY_ON(DEMO_SEN_1, DEMO_SEN_2)" }, "AND( ANY ) → 입력 ANY_ON");

        // 쓰기: 새 코어 사이트는 AND, 옛 코어는 옛 꼴. 한 줄만 바뀐다
        var r62 = site.TagRules.First(r => r.St.Args[0].Text.Trim() == "62");
        var w = TagEdit.SetCondition(site, r62, [X("FORWARD"), X("ID", "1", "2")], "ON(DEMO_SEN_2)");
        check(w.Ok && w.Touched[0].Text.Contains("AND( FORWARD, ID(1, 2), ON(DEMO_SEN_2) )", StringComparison.Ordinal), "새 코어 사이트: AND 로 씀 " + w.Error);
        var o62 = old.TagRules.First(r => r.St.Args[0].Text.Trim() == "62");
        var wo = TagEdit.SetCondition(old, o62, [X("X_FORWARD"), X("X_ID", "1", "2")], "ON(DEMO_SEN_2)");
        check(wo.Ok && wo.Touched[0].Text.Contains("IF_X2_IN( X_FORWARD, X_ID(1, 2), ON(DEMO_SEN_2) )", StringComparison.Ordinal), "옛 코어 사이트: 옛 꼴로 씀 " + wo.Error);
        var bad = TagEdit.SetCondition(site, r62, [X("NOPE")], null);
        check(!bad.Ok, "모르는 낱말은 막음");
        // 조건을 없애면 TAG_IF → TAG
        var none62 = TagEdit.SetCondition(site, r62, [], null);
        check(none62.Ok && none62.Touched[0].Text.TrimStart().StartsWith("TAG(", StringComparison.Ordinal), "조건 없애면 TAG");

        // 안 고친 줄·파일은 바이트 그대로
        check(w.Site!.Files.Where(f => f.Name != "tag_table.c").All(f => f.ToBytes().AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(fx, f.Name)))), "안 고친 파일 바이트 그대로");

        // 팔레트: 새 코어는 옛 별칭을 뺀다, 옛 코어는 새 줄을 뺀다
        var names = (bool n) => SeqEdit.StepRows(false, n).Select(e => e.Name).Distinct().ToList();
        check(!names(true).Contains("WAIT_IF") && !names(true).Contains("AGV_SPEED_MMIN") && names(true).Contains("OUT_SET") && names(false).Contains("WAIT_IF") && !names(false).Contains("OUT_SET"), "스텝 목록 버전별");
        var forms = ActionForms.Of(GrammarTable.Find("WAIT", Place.Step).First(), true).Select(e => e.FormName).ToList();
        check(forms.SequenceEqual(["없음", "시간", "파라미터"]), "대기 형식(새 코어) " + string.Join("/", forms));

        // 헤더 대조: 새 코어는 dsl_and.h 의 DSL_AX_ 낱말을 공개 이름으로 센다. 옛 코어는 새 이름을 안 요구한다
        var hdr = "#ifndef AGV_X_H\n#define AGV_X_H\n" + string.Join("\n", GrammarTable.OldNames.Select(n => $"#define {n} 1")) + "\n#endif\n";
        check(!HeaderCheck.Compare([hdr], newDsl: true).Same && HeaderCheck.Compare([hdr], newDsl: false).Same, "옛 코어 헤더는 새 이름 없어도 같음");
        var and = "#ifndef AGV_DSL_AND_H\n#define AGV_DSL_AND_H\n#define AND(...) 1\n#define ANY(...) 1\n#define DSL_AX_FORWARD 1\n#define DSL_AX_DSL_STI(a) 1\n#endif\n";
        var r = HeaderCheck.Compare([hdr, and], newDsl: true);
        check(r.Header.Contains("FORWARD") && r.Header.Contains("AND") && !r.Header.Any(n => n.StartsWith("DSL_", StringComparison.Ordinal)) && !r.OnlyGrammar.Contains("FORWARD") && r.OnlyGrammar.Contains("ID"), "DSL_AX_ 낱말 = 공개 이름");

        // N-5: 새 이름은 사이트가 같은 이름을 정의해도 겹침으로 안 센다(쓰기 전에는 안 깨진다)
        var io = new Edit("io_map.h", "#define DEMO_LAMP       OUT_LAMP_Y", "#define DEMO_LAMP       OUT_LAMP_Y\n#define ID 5\n#define FORWARD 1");
        var files = FileZones.Files.Select(z => Path.Combine(root, "tests", "fixtures", "T900_DEMO", z.FileName)).Where(File.Exists)
            .Select(p => SourceFile.Parse(Path.GetFileName(p), Utf8.GetBytes(File.ReadAllText(p, Utf8).Replace(io.Find, Path.GetFileName(p) == io.File ? io.Replace : io.Find)))).ToList();
        var n5 = Checker.Run(Site.FromFiles(files, "", null)).Where(f => f.Rule == "N-5").ToList();
        check(n5.Count == 0, "N-5: 새 이름(ID·FORWARD) 정의는 겹침 아님 " + string.Join(" | ", n5));

        // 새 형식 fixture 의 모든 줄을 읽기 → 쓰기 왕복(고치지 않으면 바이트 같음)은 T900_DEMO 시험이 이미 본다. 여기서는 새 낱말이 칸 종류로 풀리는지 한 번 더
        var uses = new Facts(site).Uses.Where(u => u.Node is CallNode { Name: "AND" }).Select(u => u.Spec.Kind).Distinct().ToList();
        check(uses.Contains(ArgKind.AndCond) && uses.Contains(ArgKind.TagCond) && uses.Contains(ArgKind.ManCond), $"AND 가 놓인 칸 종류 ({string.Join(",", uses)})");
    }

    sealed record Edit(string File, string Find, string Replace);
}
