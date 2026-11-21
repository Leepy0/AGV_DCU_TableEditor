using System.Text;
using TableEditor.Engine.Check;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

/// <summary>
/// 작업 2 시험: 편집 명령·되돌리기·저장·초안. fixture 는 <c>$env:TEMP</c> 아래에 바이트째 복사해서 쓴다(원본·줄 끝·줄 끝 공백을 건드리지 않는다).
/// </summary>
static class EditTests
{
    static readonly UTF8Encoding Utf8 = new(false);
    static Action<bool, string> check = (_, _) => { };
    static string root = "";

    public static void Run(Action<bool, string> c, string repoRoot)
    {
        check = c;
        root = repoRoot;
        Args();
        Comments();
        Lines();
        Actions();
        Conditions();
        TagAdd();
        Renumber();
        Bundle();
        History();
        Save();
        Draft();
        OutsideChange();
        SaveFailure();
    }

    // ---- 도움 ----

    static string Copy(string name)
    {
        var top = Path.Combine(Path.GetTempPath(), "tedit_edit_tests", name);
        if (Directory.Exists(top))
        {
            foreach (var f in Directory.GetFiles(top, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(top, true);
        }
        var dir = Directory.CreateDirectory(Path.Combine(top, "app", "site", "T900_DEMO")).FullName;
        foreach (var f in Directory.GetFiles(Path.Combine(root, "tests", "fixtures", "T900_DEMO")))
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        return dir;
    }

    static Site Open(string name) => Site.Open(Copy(name));

    static string T(Site s, string file) => s.File(file)!.ToText();

    static Statement St(Site s, string file, string start) =>
        s.File(file)!.Statements.Single(x => x.Text.TrimStart().StartsWith(start, StringComparison.Ordinal));

    static TagRule Rule(Site s, string start) => new(St(s, "tag_table.c", start));

    static List<string> SplitLines(string t)
    {
        var list = new List<string>();
        int p = 0;
        while (p < t.Length)
        {
            int e = t.IndexOf('\n', p);
            e = e < 0 ? t.Length : e + 1;
            list.Add(t[p..e]);
            p = e;
        }
        return list;
    }

    /// <summary>바뀐 구간: 앞뒤 같은 줄을 빼고 남은 (옛 줄, 새 줄).</summary>
    static (List<string> Old, List<string> New) Diff(string a, string b)
    {
        var x = SplitLines(a);
        var y = SplitLines(b);
        int p = 0;
        while (p < x.Count && p < y.Count && x[p] == y[p]) p++;
        int q = 0;
        while (q < x.Count - p && q < y.Count - p && x[^(q + 1)] == y[^(q + 1)]) q++;
        return (x.Skip(p).Take(x.Count - p - q).ToList(), y.Skip(p).Take(y.Count - p - q).ToList());
    }

    /// <summary>file 밖의 파일은 바이트 동일, file 은 옛 줄 old → 새 줄 neu 만 바뀜.</summary>
    static void Only(Site a, EditResult r, string file, string[] old, string[] neu, string what)
    {
        if (!r.Ok) { check(false, $"{what}: 막힘 {r.Error}"); return; }
        var b = r.Site!;
        foreach (var f in a.Files.Where(f => f.Name != file))
            check(f.ToBytes().AsSpan().SequenceEqual(b.File(f.Name)!.ToBytes()), $"{what}: {f.Name} 그대로");
        var (o, n) = Diff(T(a, file), T(b, file));
        check(o.SequenceEqual(old) && n.SequenceEqual(neu), $"{what}: 차이 [{string.Join("|", o)}] → [{string.Join("|", n)}]");
    }

    static void NoErrors(EditResult r, string what)
    {
        if (!r.Ok) { check(false, $"{what}: 막힘 {r.Error}"); return; }
        var e = Checker.Run(r.Site!).Where(f => f.Severity == Severity.Error).ToList();
        check(e.Count == 0, $"{what}: 검사 오류 " + string.Join(" | ", e.Take(3)));
    }

    static void Blocked(EditResult r, string word, string what) =>
        check(!r.Ok && r.Error!.Contains(word, StringComparison.Ordinal), $"{what}: 막혀야 함 '{word}' · {r.Error}");

    // ---- 문장 한 개 ----

    static void Args()
    {
        var s = Open("args");
        var r21 = Rule(s, "TAG ( 21,");
        var turn = (CallNode)r21.Actions[0].Node;
        var r = SiteEdit.SetArg(s, r21.St, turn.Args[1], "45");
        Only(s, r, "tag_table.c", ["    TAG ( 21,  ANY_PROFILE,  AGV_TURN( CW,  90,  0, 1, 0 ) )\r\n"],
            ["    TAG ( 21,  ANY_PROFILE,  AGV_TURN( CW,  45,  0, 1, 0 ) )\r\n"], "인자 바꾸기(안쪽)");
        check(r.Touched.Count == 1 && r.Touched[0] is Statement { Name: "TAG" } t && t.Text.Contains("45"), "고친 줄 Touched");
        check(T(s, "tag_table.c").Contains("CW,  90,"), "받은 사이트는 그대로");

        // 식 안의 인자: STATION(0) + 100 의 0
        var rg = Rule(s, "TAG_RANGE( 101");
        var acs = (CallNode)rg.Actions[0].Node;
        var station = (CallNode)((BinaryNode)acs.Args[1].Node).Left;
        Only(s, SiteEdit.SetArg(s, rg.St, station.Args[0], "1"), "tag_table.c",
            ["    TAG_RANGE( 101, 120, PF_BELT, ACS_SEND( ARRIVE, STATION(0) + 100 ) )\n"],
            ["    TAG_RANGE( 101, 120, PF_BELT, ACS_SEND( ARRIVE, STATION(1) + 100 ) )\n"], "식 안 인자");

        // 여러 줄 문장: 그 줄만
        var r70 = Rule(s, "TAG_IF( 70,");
        var err = ((CallNode)((CallNode)r70.Cond!.Node).Args[0].Node).Args[0];
        Only(s, SiteEdit.SetArg(s, r70.St, err, "282"), "tag_table.c",
            ["            IF_X( X_ERR_EQ(281) ),\n"], ["            IF_X( X_ERR_EQ(282) ),\n"], "여러 줄 문장 한 줄");

        Blocked(SiteEdit.SetArg(s, r21.St, turn.Args[1], " "), "빈 글자", "빈 인자");
        Blocked(SiteEdit.SetArg(s, r21.St, turn.Args[1], "90 /* x */"), "주석", "인자 안 주석");
        Blocked(SiteEdit.SetArg(s, r21.St, rg.FirstArg, "1"), "인자 없음", "다른 문장의 인자");
        Blocked(SiteEdit.SetArg(s, r21.St, turn.Args[1], "90 ?"), "식 하나 아님", "읽히지 않는 글자");
    }

    static void Comments()
    {
        var s = Open("comments");
        Only(s, SiteEdit.SetComment(s, St(s, "tag_table.c", "TAG ( 28,"), "끝"), "tag_table.c",
            ["    TAG ( 28,  ANY_PROFILE,  OBS_ON )   \r\n"], ["    TAG ( 28,  ANY_PROFILE,  OBS_ON )    /* 끝 */\r\n"], "꼬리 주석 더하기(줄 끝 공백 줄)");
        var t1 = St(s, "tag_table.c", "TAG ( 1,");
        Only(s, SiteEdit.SetComment(s, t1, "새"), "tag_table.c", [t1.Text], [t1.Text.Replace(t1.TailComment, "/* 새 */")], "꼬리 주석 바꾸기");
        Only(s, SiteEdit.SetComment(s, t1, null), "tag_table.c", [t1.Text],
            [t1.Text[..(t1.Text.IndexOf(t1.TailComment, StringComparison.Ordinal))].TrimEnd(' ') + "\r\n"], "꼬리 주석 지우기");
        var lift = St(s, "site.h", "PF_LIFT");
        Only(s, SiteEdit.SetComment(s, lift, ""), "site.h", [lift.Text], ["    PF_LIFT,\n"], "열거자 주석 지우기(쉼표 남김)");
        Blocked(SiteEdit.SetComment(s, t1, "a */ b"), "주석 글", "주석 닫기 글자");
    }

    // ---- 줄 단위 ----

    static void Lines()
    {
        var s = Open("lines");
        var man = s.ZonesOf("MANUAL_BEGIN").Single();
        var boxElse = St(s, "manual_table.c", "MAN_ELSE( PF_BOX");
        int at = man.Ordinal(boxElse);
        var r = SiteEdit.Insert(s, man, at,
        [
            new NewStatement("MAN", ["PF_CART", "IDLE", "IN( ON(DEMO_SEN_3) )", "BELT( 2, CW )"]),
            new NewStatement("MAN_ELSE", ["PF_CART", "IDLE", "BELT( 2, OFF )"], "카트"),
        ]);
        Only(s, r, "manual_table.c", [],
        [
            "    MAN( PF_CART,        IDLE,  IN( ON(DEMO_SEN_3) ),       BELT( 2, CW ) )\r\n",
            "    MAN_ELSE( PF_CART,   IDLE,                              BELT( 2, OFF ) )    /* 카트 */\r\n",
        ], "문장 넣기(사슬, 위 같은 매크로 본뜸)");
        check(r.Ok && r.Site!.ManualChains.Any(c => c.Profile == "PF_CART" && c.Rows.Count == 2 && c.Closed), "새 사슬");
        check(r.Ok && r.Touched.Count == 2, "넣은 줄 Touched 2");
        NoErrors(r, "문장 넣기");

        // 지우기 둘
        var del = SiteEdit.Delete(s, [St(s, "tag_table.c", "TAG ( 2,"), St(s, "tag_table.c", "TAG ( 3,")]);
        Only(s, del, "tag_table.c", ["    TAG ( 2,   ANY_PROFILE,  AGV_SPEED( LLOW ) )\r\n", "    TAG ( 3,   ANY_PROFILE,  AGV_SPEED_MMIN( 20 ) )\r\n"], [], "지우기 둘");
        Blocked(SiteEdit.Delete(s, [s.ZonesOf("TAG_TABLE_BEGIN").Single().End!]), "구역 표지", "구역 표지 지우기");

        // 옮기기 둘(맨 앞으로)
        var tz = s.ZonesOf("TAG_TABLE_BEGIN").Single();
        var mv = SiteEdit.Move(s, [St(s, "tag_table.c", "TAG ( 28,"), St(s, "tag_table.c", "TAG ( 27,")], tz, 0);
        check(mv.Ok && mv.Site!.TagRules.Take(3).Select(x => x.FirstArg.Text).SequenceEqual(["27", "28", "1"]), "옮기기: 27, 28, 1 차례 " + mv.Error);
        if (mv.Ok)
        {
            var (o, n) = Diff(T(s, "tag_table.c"), T(mv.Site!, "tag_table.c"));
            check(T(mv.Site!, "tag_table.c").Contains("    TAG ( 28,  ANY_PROFILE,  OBS_ON )   \r\n", StringComparison.Ordinal), "옮긴 줄 줄 끝 공백 그대로");
            check(T(mv.Site!, "tag_table.c").Length == T(s, "tag_table.c").Length && o.Count == n.Count, "옮기기: 글자 수 같음");
        }

        // 원문 글자 바꾸기(고급) → 원문 블록, 그 원문 블록 옮기기
        var raw = SiteEdit.ReplaceText(s, St(s, "tag_table.c", "TAG ( 9,"), "    { 41, PF_BELT, 0, 0, 0, { TA_STOP } },");
        check(raw.Ok && raw.Site!.File("tag_table.c")!.RawInZones.Count() == 1 && raw.Touched.Single().Kind == PieceKind.Raw, "원문 글자 바꾸기 " + raw.Error);
        if (raw.Ok)
        {
            var s2 = raw.Site!;
            var tz2 = s2.ZonesOf("TAG_TABLE_BEGIN").Single();
            var rm = SiteEdit.Move(s2, [s2.File("tag_table.c")!.RawInZones.Single()], tz2, tz2.Statements.Count);
            check(rm.Ok && T(rm.Site!, "tag_table.c").Contains("ACS_SEND( ARRIVE, TAG_NUM - 1 ) )\n    { 41, PF_BELT, 0, 0, 0, { TA_STOP } },\r\n", StringComparison.Ordinal), "원문 블록 옮기기 " + rm.Error);
        }
    }

    static void Actions()
    {
        var s = Open("actions");
        var r6 = Rule(s, "TAG ( 6,");
        string L(string body) => $"    TAG ( 6,   ANY_PROFILE,  {body} )\r\n";
        string[] old = [L("AGV_STOP( 30 ), AGV_AUTOSTART( 5 )")];
        Only(s, TagEdit.InsertAction(s, r6, 2, "OBS_ON"), "tag_table.c", old, [L("AGV_STOP( 30 ), AGV_AUTOSTART( 5 ), OBS_ON")], "동작 넣기(끝)");
        Only(s, TagEdit.InsertAction(s, r6, 0, "OBS_ON"), "tag_table.c", old, [L("OBS_ON,  AGV_STOP( 30 ), AGV_AUTOSTART( 5 )")], "동작 넣기(앞)");
        Only(s, TagEdit.DeleteAction(s, r6, 0), "tag_table.c", old, ["    TAG ( 6,   ANY_PROFILE, AGV_AUTOSTART( 5 ) )\r\n"], "동작 지우기(앞)");
        Only(s, TagEdit.DeleteAction(s, r6, 1), "tag_table.c", old, [L("AGV_STOP( 30 )")], "동작 지우기(끝)");
        Only(s, TagEdit.MoveAction(s, r6, 0, 1), "tag_table.c", old, [L("AGV_AUTOSTART( 5 ), AGV_STOP( 30 )")], "동작 순서");
        Blocked(TagEdit.DeleteAction(s, Rule(s, "TAG ( 5,"), 0), "자리", "마지막 동작");
        var a = TagEdit.InsertAction(s, r6, 2, "OBS_ON");
        var b = TagEdit.InsertAction(a.Site!, Rule(a.Site!, "TAG ( 6,"), 3, "OBS_OFF");
        check(b.Ok, "동작 넷 " + b.Error);
        Blocked(TagEdit.InsertAction(b.Site!, Rule(b.Site!, "TAG ( 6,"), 4, "STATION_RESET"), "동작 칸 넘침", "동작 다섯");
    }

    static void Conditions()
    {
        var s = Open("cond");
        var r5 = Rule(s, "TAG ( 5,");
        var old = r5.St.Text;
        string L(string name, string cond) => $"    {name} ( 5,   ANY_PROFILE,  {cond},  AGV_BRANCH( LEFT ) )\r\n";
        var a = TagEdit.SetCondition(s, r5, [new XCond("X_FORWARD", [])], null);
        Only(s, a, "tag_table.c", [old], [L("TAG_IF", "IF_FORWARD")], "조건 더하기 줄임 꼴");
        var ra = Rule(a.Site!, "TAG_IF ( 5,");
        Only(s, TagEdit.SetCondition(a.Site!, ra, [new XCond("X_ID", ["1", "2"])], null), "tag_table.c", [old], [L("TAG_IF", "IF_ID( 1, 2 )")], "IF_ID");
        Only(s, TagEdit.SetCondition(a.Site!, ra, [new XCond("X_ID", ["1", "2"]), new XCond("X_STATION", ["0", "70", "70"])], null), "tag_table.c", [old],
            [L("TAG_IF", "IF_X2( X_ID(1, 2), X_STATION(0, 70, 70) )")], "IF_X2");
        Only(s, TagEdit.SetCondition(a.Site!, ra, [new XCond("X_NOT", ["X_ID(1, 2)"])], null), "tag_table.c", [old], [L("TAG_IF", "IF_X( X_NOT(X_ID(1, 2)) )")], "줄임 꼴 없는 하나");
        Only(s, TagEdit.SetCondition(a.Site!, ra, [], "IN( ON(DEMO_SEN_1) )"), "tag_table.c", [old], [L("TAG_IF", "IF_IN( IN( ON(DEMO_SEN_1) ) )")], "IF_IN");
        Only(s, TagEdit.SetCondition(a.Site!, ra, [new XCond("X_ID", ["1", "2"])], "IN( ON(DEMO_SEN_1) )"), "tag_table.c", [old],
            [L("TAG_IF", "IF_X_IN( X_ID(1, 2), IN( ON(DEMO_SEN_1) ) )")], "IF_X_IN");
        Only(s, TagEdit.SetCondition(a.Site!, ra, [new XCond("X_FORWARD", []), new XCond("X_LOW_BATTERY", [])], "NO_IN"), "tag_table.c", [old],
            [L("TAG_IF", "IF_X2_IN( X_FORWARD, X_LOW_BATTERY, NO_IN )")], "IF_X2_IN");
        var back = TagEdit.SetCondition(a.Site!, ra, [], null);
        check(back.Ok && back.Site!.Files.All(f => f.ToBytes().AsSpan().SequenceEqual(s.File(f.Name)!.ToBytes())), "조건 지우기 → 원래 바이트 " + back.Error);
        var rr = Rule(s, "TAG_RANGE( 101");
        check(TagEdit.SetCondition(s, rr, [new XCond("X_FORWARD", [])], null) is { Ok: true } x && Rule(x.Site!, "TAG_RANGE_IF( 101").CondText == "IF_FORWARD", "범위 규칙 → TAG_RANGE_IF");
        Blocked(TagEdit.SetCondition(s, r5, [new XCond("Y_FOO", [])], null), "차량 상태 아님", "모르는 확장 조건");
        Blocked(TagEdit.SetCondition(s, r5, [new XCond("X_FORWARD", []), new XCond("X_FORWARD", []), new XCond("X_FORWARD", [])], null), "2개", "확장 셋");
        foreach (var rule in s.TagRules.Where(t => t.HasCond))
            check(TagEdit.ReadCondition(rule) != null, "조건 풀기 " + rule.St.Text.Trim());
        var c64 = TagEdit.ReadCondition(Rule(s, "TAG_IF( 64,"))!.Value;
        check(c64.X.Select(x => x.Text).SequenceEqual(["X_FORWARD", "X_PARAM(LDS, 1)"]) && c64.Input == "IN( OFF(DEMO_SEN_3) )", "IF_X2_IN 풀기");
    }

    // ---- 태그 여러 줄 추가 ----

    static void TagAdd()
    {
        check(TagEdit.ParseTags("101-103, 110", out _)!.SequenceEqual([101L, 102, 103, 110]), "번호 글 101-103, 110");
        check(TagEdit.ParseTags(" 7 ~ 5 ", out var e1) == null && e1!.Contains("처음 > 끝"), "번호 글 처음 > 끝");
        foreach (var bad in new[] { "", " ", "1-", "a", "2000", "5 6", "1, ,2" })
            check(TagEdit.ParseTags(bad, out var e) == null && e != null, $"번호 글 틀림 '{bad}'");

        var s = Open("tagadd");
        var p = TagEdit.PlanAdd(s, "104-106, 300", ["PF_BELT"], null, TagRunMode.PerLine);
        check(p.Error == null && p.Rows.Count(r => r.Exists) == 3 && p.Lines.Single().First == 300, "미리 보기: 범위 규칙이 덮으면 있음");
        check(TagEdit.PlanAdd(s, "1", ["PF_NOPE"], null, TagRunMode.PerLine).Error!.Contains("없는 프로파일"), "없는 프로파일");
        check(TagEdit.PlanAdd(s, "1-", ["PF_BELT"], null, TagRunMode.PerLine).Error != null, "번호 글 틀림 → 까닭");

        var r = TagEdit.Add(s, "40, 45, 0", ["ANY_PROFILE", "PF_BOX"], null, TagRunMode.PerLine);
        NoErrors(r, "여러 줄 추가");
        if (r.Ok)
        {
            var seq = r.Site!.TagRules.Select(x => x.FirstArg.Text + " " + x.Profile).ToList();
            int i40 = seq.IndexOf("40 PF_CART");
            check(seq[0] == "0 PF_BOX" && seq[1] == "0 ANY_PROFILE" && seq[2] == "1 ANY_PROFILE", "태그 0: 표 맨 앞, 프로파일 줄이 ANY 앞");
            check(seq[i40 + 1] == "40 PF_BOX" && seq[i40 + 2] == "40 ANY_PROFILE" && seq[i40 + 3] == "41 PF_BELT", "태그 40: 묶음 끝(ANY 없음)");
            int i41 = seq.IndexOf("41 PF_BOX");
            check(seq[i41 + 1] == "45 PF_BOX" && seq[i41 + 2] == "45 ANY_PROFILE", "태그 45: 더 작은 가장 가까운 묶음 뒤");
            var t = T(r.Site!, "tag_table.c");
            check(t.Contains("SEQ_START( CART_LOAD ) )\r\n    TAG( 40, PF_BOX,   AGV_STOP )\r\n    TAG( 40, ANY_PROFILE,   AGV_STOP )\r\n", StringComparison.Ordinal), "새 줄 꼴: 위 같은 매크로 본뜸, CRLF");
            check(t.Contains("    /* ---- 공통 ---- */\r\n    TAG( 0, PF_BOX, AGV_STOP )\r\n", StringComparison.Ordinal), "본뜰 문장 없음: 한 칸");
            check(r.Touched.Count == 6, "Touched 6");
        }

        // 있는 묶음의 ANY 앞
        var any = TagEdit.Add(s, "1", ["PF_LIFT"], null, TagRunMode.PerLine);
        check(any.Ok && any.Site!.TagRules.Select(x => x.FirstArg.Text + " " + x.Profile).Take(2).SequenceEqual(["1 PF_LIFT", "1 ANY_PROFILE"]), "묶음의 ANY_PROFILE 줄 앞");

        // 범위 방식 + 본뜰 줄(조건·동작 베낌), LF 구간
        var tpl = Rule(s, "TAG_IF( 50, ANY_PROFILE, IF_FORWARD");
        var rg = TagEdit.Add(s, "200-203, 205", ["PF_LIFT"], tpl, TagRunMode.Range);
        NoErrors(rg, "범위 추가");
        if (rg.Ok)
        {
            var t = T(rg.Site!, "tag_table.c");
            check(t.Contains("ACS_SEND( ARRIVE, TAG_NUM - 1 ) )\n    TAG_RANGE_IF( 200, 203, PF_LIFT, IF_FORWARD, AGV_TURN( CW,  90, 0, 1, 0 ) )\n"
                + "    TAG_IF( 205, PF_LIFT, IF_FORWARD, AGV_TURN( CW,  90, 0, 1, 0 ) )\n", StringComparison.Ordinal), "TAG_RANGE_IF 한 줄 + 외톨이 TAG_IF, LF");
            var again = TagEdit.PlanAdd(rg.Site!, "200-203, 205", ["PF_LIFT"], tpl, TagRunMode.Range);
            check(again.Rows.All(x => x.Exists) && again.Lines.Count == 0, "다시: 모두 있음");
            Blocked(TagEdit.Add(rg.Site!, "200-203, 205", ["PF_LIFT"], tpl, TagRunMode.Range), "새 줄 없음", "모두 있음");
        }
        var split = TagEdit.PlanAdd(s, "118-123", ["PF_BELT"], null, TagRunMode.Range);
        check(split.Lines.Select(l => (l.First, l.Last)).SequenceEqual([(121L, 123L)]), "범위: 있는 번호에서 끊음");
    }

    static void Renumber()
    {
        var s = Open("renumber");
        var r = TagEdit.Renumber(s, [Rule(s, "TAG ( 27,"), Rule(s, "TAG( 41, PF_BOX")], 40);
        NoErrors(r, "번호 바꾸기");
        if (r.Ok)
        {
            var t = T(r.Site!, "tag_table.c");
            check(t.Contains("SEQ_START( CART_LOAD ) )\r\n    TAG( 40, PF_BOX,    SEQ_START( DEMO_TURN ) )\r\n    TAG ( 40,  ANY_PROFILE,  OBS_OFF )\r\n    TAG( 41, PF_BELT,", StringComparison.Ordinal),
                "바꾼 줄이 새 번호 자리로(프로파일 줄 먼저)");
            check(!t.Contains("TAG ( 27,", StringComparison.Ordinal) && !t.Contains("TAG( 41, PF_BOX", StringComparison.Ordinal), "옛 자리 비움");
            check(r.Touched.Count == 2, "Touched 2");
        }
        var rg = TagEdit.Renumber(s, [Rule(s, "TAG_RANGE( 101")], 140);
        check(rg.Ok && T(rg.Site!, "tag_table.c").Contains("TAG_RANGE( 140, 159, PF_BELT,", StringComparison.Ordinal), "범위 규칙은 끝도 민다");
        Blocked(TagEdit.Renumber(s, [Rule(s, "TAG_RANGE( 101")], 1010), "범위 밖", "끝이 1023 넘음");
        Blocked(TagEdit.Renumber(s, [Rule(s, "TAG ( 1,")], 1024), "범위 밖", "1024");
        var same = TagEdit.Renumber(s, [Rule(s, "TAG ( 1,")], 1);
        check(same.Ok && ReferenceEquals(same.Site, s), "같은 번호: 그대로");
    }

    // ---- 묶음 명령 ----

    static void Bundle()
    {
        var s = Open("bundle");
        var add = BundleEdit.AddProfile(s, "PF_CRATE", 0, 40, 49);
        NoErrors(add, "프로파일 추가");
        if (add.Ok)
        {
            check(T(add.Site!, "site.h").Contains("    PF_BOX,\n    PF_CRATE\n};", StringComparison.Ordinal), "site.h enum 끝 + 쉼표");
            check(T(add.Site!, "profiles.c").Contains("PROFILE( PF_BOX,            0, 30, 39  )\r\n    PROFILE( PF_CRATE,            0, 40, 49  )\r\nPROFILE_TABLE_END", StringComparison.Ordinal), "profiles.c 줄");
            check(add.Site!.Profiles.Any(p => p.Name == "PF_CRATE" && p.Number == 5), "번호 5");
            var del = BundleEdit.DeleteProfile(add.Site!, "PF_CRATE");
            check(del.Ok && s.Files.Where(f => f.Name != "site.h").All(f => T(del.Site!, f.Name) == T(s, f.Name))
                && T(del.Site!, "site.h").Contains("    PF_BOX,\n};", StringComparison.Ordinal), "프로파일 삭제(안 쓰는 것) " + del.Error);
        }
        Blocked(BundleEdit.AddProfile(s, "PF_BOX", 0, 1, 2), "N-4", "있는 이름");
        Blocked(BundleEdit.AddProfile(s, "BOX2", 0, 1, 2), "PF_", "머리 글자");
        Blocked(BundleEdit.DeleteProfile(s, "PF_BOX"), "쓰는 줄", "쓰는 프로파일 삭제");

        var ren = BundleEdit.RenameProfile(s, "PF_BOX", "PF_CRATE");
        NoErrors(ren, "프로파일 이름 바꾸기");
        if (ren.Ok)
        {
            int Count(Site x, string n) => x.Files.SelectMany(f => f.Statements).Sum(st => st.Args.Sum(a => a.Node.Names().Count(y => y == n)) + (st.Name == n ? 1 : 0));
            check(Count(s, "PF_BOX") > 3 && Count(ren.Site!, "PF_BOX") == 0 && Count(ren.Site!, "PF_CRATE") == Count(s, "PF_BOX"), "모든 사용처");
            var changedFiles = s.Files.Where(f => T(s, f.Name) != T(ren.Site!, f.Name)).Select(f => f.Name).ToList();
            check(changedFiles.SequenceEqual(["site.h", "profiles.c", "tag_table.c", "seq_table.c", "manual_table.c"]), "고친 파일 " + string.Join(",", changedFiles));
        }
        Blocked(BundleEdit.RenameProfile(s, "PF_BOX", "PF_BELT"), "N-4", "이름 겹침");
        Blocked(BundleEdit.RenameProfile(s, "PF_NOPE", "PF_X"), "프로파일 아님", "없는 프로파일");
        Blocked(BundleEdit.RenameProfile(s, "PF_BOX", "ON"), "이름 틀림", "PF_ 아님");

        // N-6: 원문 블록에서 쓰는 이름
        var raw = SiteEdit.ReplaceText(s, St(s, "tag_table.c", "TAG ( 9,"), "    { 41, PF_BOX, 0, 0, 0, { TA_STOP } },");
        Blocked(BundleEdit.RenameProfile(raw.Site!, "PF_BOX", "PF_CRATE"), "N-6", "원문 블록 N-6");
        var rawSite = raw.Site!;
        check(rawSite.Files.Select(f => T(rawSite, f.Name)).SequenceEqual(rawSite.Files.Select(f => f.ToText())), "막히면 아무 파일도 안 바뀜");

        // 별칭: 읽지 않는 파일에서 쓰면 막는다
        var extra = Path.Combine(s.Folder, "site.c");
        File.WriteAllText(extra, "/* DEMO_SEN_2 는 주석 */\nint x = DEMO_SEN_1;\n", Utf8);
        Blocked(BundleEdit.RenameAlias(s, "DEMO_SEN_1", "DEMO_IN_1"), "N-6 원문에서 씀 · site.c:2", "읽지 않는 파일 N-6");
        var al = BundleEdit.RenameAlias(s, "DEMO_SEN_2", "DEMO_IN_2");
        File.Delete(extra);
        NoErrors(al, "별칭 이름 바꾸기(주석 안 이름은 막지 않음)");
        if (al.Ok)
        {
            check(T(al.Site!, "io_map.h").Contains("#define DEMO_IN_2      IN_EXT_2\n", StringComparison.Ordinal) && !al.Site!.Files.Any(f => f.Statements.Any(st => st.Args.Any(a => a.Text.Contains("DEMO_SEN_2")))), "정의·사용처");
        }
        Blocked(BundleEdit.RenameAlias(s, "PF_BOX", "X"), "별칭 아님", "프로파일은 별칭 아님");

        // 벨트
        var bl = BundleEdit.RenameBelt(s, "LIFT", "HOIST");
        NoErrors(bl, "벨트 이름 바꾸기");
        if (bl.Ok)
        {
            check(bl.Site!.Names.Belts.ContainsKey("HOIST") && !bl.Site!.Names.Belts.ContainsKey("LIFT"), "벨트 정의");
            check(T(bl.Site!, "manual_table.c").Contains("BELT( HOIST, CW )", StringComparison.Ordinal) && T(bl.Site!, "seq_table.c").Contains("BELT_ON( HOIST, CW )", StringComparison.Ordinal)
                && T(bl.Site!, "io_map.h").Contains("#define DEMO_LIFT_UP", StringComparison.Ordinal), "벨트 칸만(별칭 DEMO_LIFT_* 그대로)");
        }
        var b1 = BundleEdit.RenameBelt(s, "1", "9");
        NoErrors(b1, "벨트 번호 바꾸기");
        check(b1.Ok && T(b1.Site!, "seq_table.c").Contains("BELT_ON( 9, CW, UNTIL", StringComparison.Ordinal) && T(b1.Site!, "seq_table.c").Contains("DELAY(100)", StringComparison.Ordinal), "벨트 번호 칸만");
        Blocked(BundleEdit.RenameBelt(s, "LIFT", "2"), "N-4", "벨트 겹침");

        // 시퀀스 ID·라벨·시퀀스 표
        var id = BundleEdit.AddSeqId(s, "SEQ_DEMO_NEW");
        NoErrors(id, "시퀀스 ID 추가");
        check(id.Ok && T(id.Site!, "seq_custom.h").Contains("    SEQ_DEMO_SHUTTLE,\n    SEQ_DEMO_NEW\n};", StringComparison.Ordinal), "seq_custom.h SEQ_ enum 끝");
        var lb = BundleEdit.AddLabel(s, "seq_table.c", "WAIT2");
        check(lb.Ok && T(lb.Site!, "seq_table.c").Contains("SEQ_LABELS( DONE, REQ, RUN, ERR, BELT_STOP, WAIT2 )\r\n", StringComparison.Ordinal), "라벨 추가 " + lb.Error);
        Blocked(BundleEdit.AddLabel(s, "seq_table.c", "REQ"), "N-4", "라벨 겹침");
        var tb = BundleEdit.AddSeqTable(s, "t_new", "PF_CART", "SEQ_UNLOAD");
        NoErrors(tb, "시퀀스 표 추가");
        if (tb.Ok)
        {
            var t = T(tb.Site!, "seq_table.c");
            check(t.Contains("SEQ_END\r\n\r\nSEQ_BEGIN( t_new )\r\n    END_STOP\r\nSEQ_END\r\n\r\nSEQ_MAP_BEGIN( site_seqs )\r\n", StringComparison.Ordinal), "표: SEQ_MAP_BEGIN 앞");
            check(t.Contains("    SEQ_MAP( PF_CART,         SEQ_UNLOAD,   t_new )\r\nSEQ_MAP_END", StringComparison.Ordinal), "대응표 끝 줄");
        }
        Blocked(BundleEdit.AddSeqTable(s, "shuttle", "PF_CART", "SEQ_UNLOAD"), "N-4", "표 이름 겹침");
    }

    static void History()
    {
        var s = Open("history");
        var h = new EditHistory(s);
        var a = SiteEdit.SetComment(s, St(s, "tag_table.c", "TAG ( 2,"), "a");
        h.Push(a);
        check(h.Changes == 1 && h.CanUndo && !h.CanRedo, "쌓기");
        check(ReferenceEquals(h.Undo(), s) && h.Changes == 0 && !h.Dirty, "되돌리기 = 앞 사이트");
        check(ReferenceEquals(h.Redo(), a.Site) && h.Changes == 1, "다시 하기");
        h.Push(SiteEdit.SetComment(h.Current, St(h.Current, "tag_table.c", "TAG ( 3,"), "b"));
        check(h.Changes == 2, "둘");
        h.MarkSaved(h.Current);
        check(h.Changes == 0 && h.Undo() != null && h.Changes == 1, "저장 지점");
        h.Push(SiteEdit.SetComment(h.Current, St(h.Current, "tag_table.c", "TAG ( 5,"), "c"));
        check(h.Changes == 3 && h.Dirty, "저장 지점이 버려짐");
        var before = h.Current;
        h.Push(EditResult.Fail("x") with { });
        check(ReferenceEquals(h.Current, before), "실패한 결과는 안 쌓음");

        // M1: 낡은 사이트(s)를 기준으로 만든 편집은 그 사이 편집(지금 사이트)을 지우지 않게 쌓지 않는다
        var live = h.Current;
        int steps = h.Changes;
        var stale = SiteEdit.SetComment(s, St(s, "tag_table.c", "TAG ( 2,"), "낡음");
        check(stale.Ok && ReferenceEquals(stale.Base, s) && !ReferenceEquals(stale.Base, live), "편집 결과가 기준 사이트를 가짐");
        h.Push(stale);
        check(ReferenceEquals(h.Current, live) && h.Changes == steps && !h.CanRedo, "낡은 기준 편집은 안 쌓음(지금 사이트·변경 수 그대로)");
        var fresh = SiteEdit.SetComment(live, St(live, "tag_table.c", "TAG ( 2,"), "새");
        h.Push(fresh);
        check(ReferenceEquals(h.Current, fresh.Site) && h.Changes == steps + 1, "지금 사이트 기준 편집은 쌓음");
    }

    // ---- 저장 ----

    static readonly DateTime Old = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    static void Age(string dir)
    {
        foreach (var f in Directory.GetFiles(dir)) File.SetLastWriteTimeUtc(f, Old);
    }

    static Dictionary<string, byte[]> Disk(string dir) => Directory.GetFiles(dir).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);

    static bool SameDisk(Dictionary<string, byte[]> a, Dictionary<string, byte[]> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v.AsSpan().SequenceEqual(kv.Value));

    static void Save()
    {
        var dir = Copy("save");
        Age(dir);
        var s = Site.Open(dir);
        check(SaveFiles.Save(s).Status == SaveStatus.NoChange, "바뀐 것 없음");
        var r21 = Rule(s, "TAG ( 21,");
        var e = SiteEdit.SetArg(s, r21.St, ((CallNode)r21.Actions[0].Node).Args[1], "45");
        var res = SaveFiles.Save(e.Site!);
        check(res.Status == SaveStatus.Saved && res.Written.SequenceEqual(["tag_table.c"]), $"저장 {res.Status} {res.Reason}");
        check(File.ReadAllBytes(Path.Combine(dir, "tag_table.c")).AsSpan().SequenceEqual(e.Site!.File("tag_table.c")!.ToBytes()), "디스크 = 편집");
        check(Directory.GetFiles(dir).Where(f => !f.EndsWith("tag_table.c", StringComparison.Ordinal)).All(f => File.GetLastWriteTimeUtc(f) == Old), "안 바뀐 파일은 시각도 그대로");
        check(!Directory.GetFiles(dir, "*.tedit_tmp").Any(), "임시 파일 없음");
        check(SaveFiles.Changed(res.Site).Count == 0 && res.Site.Stamps["tag_table.c"].Sha256 == FileStamp.Hash(File.ReadAllBytes(Path.Combine(dir, "tag_table.c"))), "다시 읽은 사이트");
        var at = res.Lines.At(Path.Combine(dir, "tag_table.c"), Rule(res.Site, "TAG ( 21,").St.StartLine);
        check(at is Statement { Name: "TAG" } st && st.Text.Contains("45"), "줄 표: (파일, 줄) → 조각");
        var r70 = Rule(res.Site, "TAG_IF( 70,");
        check(ReferenceEquals(res.Lines.At("tag_table.c", r70.St.StartLine + 2), r70.St) && res.Lines.At("tag_table.c", 100000) == null, "줄 표: 여러 줄 문장 안 줄");
    }

    static void Draft()
    {
        var dir = Copy("draft");
        var s = Site.Open(dir);
        var before = Disk(dir);
        var bad = SiteEdit.SetArg(s, Rule(s, "TAG ( 2,").St, Rule(s, "TAG ( 2,").ProfileArg, "PF_NOPE");
        var res = SaveFiles.Save(bad.Site!);
        check(res.Status == SaveStatus.Errors && res.Errors.Any(f => f.Rule == "N-1") && SameDisk(before, Disk(dir)), "검사 오류: 저장 막음, 디스크 그대로");
        var draftDir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(dir))!, "build", "table_editor", "draft", "T900_DEMO");
        check(Drafts.Folder(s) == draftDir && File.Exists(Path.Combine(draftDir, "tag_table.c")), "초안 자리 " + Drafts.Folder(s));
        var back = Drafts.Read(Site.Open(dir));
        check(back != null && back.Files.SequenceEqual(["tag_table.c"]) && back.Stale.Count == 0 && T(back.Site, "tag_table.c") == T(bad.Site!, "tag_table.c"), "초안 복원");
        // 초안 뒤 디스크가 바뀌면 낡음
        File.AppendAllText(Path.Combine(dir, "custom_io.h"), "", Utf8);
        File.AppendAllText(Path.Combine(dir, "tag_table.c"), "\n", Utf8);
        check(Drafts.Read(Site.Open(dir))!.Stale.SequenceEqual(["tag_table.c"]), "초안 낡음");
        var good = SiteEdit.SetComment(back!.Site, Rule(back.Site, "TAG ( 2,").St, null);
        var fixedSite = SiteEdit.SetArg(back.Site, Rule(back.Site, "TAG ( 2,").St, Rule(back.Site, "TAG ( 2,").ProfileArg, "ANY_PROFILE");
        // 오류를 고쳐 읽은 그대로가 되면 쓸 것이 없다(NoChange). 초안은 지운다
        var sv = SaveFiles.Save(fixedSite.Site!, overwrite: true);
        check(good.Ok && sv.Status == SaveStatus.NoChange && !Directory.Exists(draftDir), $"고치면 초안 지움 {sv.Status} {sv.Reason}");
        var other = Site.FromFiles(s.Files, Path.Combine(Path.GetTempPath(), "tedit_edit_tests", "loose", "T900_X"));
        check(Drafts.Folder(other).EndsWith(Path.Combine("AGV_DCU_TableEditor", "draft", "T900_X"), StringComparison.Ordinal), "사이트 폴더만: LOCALAPPDATA");
    }

    static void OutsideChange()
    {
        // 살펴볼 것 4: 읽은 뒤 바깥에서 바뀐 파일 → 조용히 덮지 않는다
        var dir = Copy("outside");
        var s = Site.Open(dir);
        var e = SiteEdit.SetComment(s, St(s, "tag_table.c", "TAG ( 2,"), "내 것").Site!;
        var path = Path.Combine(dir, "tag_table.c");
        var theirs = File.ReadAllBytes(path).Concat(Utf8.GetBytes("/* 남의 것 */\n")).ToArray();
        File.WriteAllBytes(path, theirs);
        var r = SaveFiles.Save(e);
        check(r.Status == SaveStatus.Conflict && r.Conflicts.SequenceEqual(["tag_table.c"]) && File.ReadAllBytes(path).AsSpan().SequenceEqual(theirs), "바깥 변경: 저장 안 함, 목록");
        var o = SaveFiles.Save(e, overwrite: true);
        check(o.Status == SaveStatus.Saved && File.ReadAllBytes(path).AsSpan().SequenceEqual(e.File("tag_table.c")!.ToBytes()), "내 것으로 덮기");

        // 시각만 바뀌고 내용이 같으면 바뀐 것이 아니다
        var s2 = Site.Open(dir);
        File.SetLastWriteTimeUtc(path, Old);
        var e2 = SiteEdit.SetComment(s2, St(s2, "tag_table.c", "TAG ( 3,"), "x").Site!;
        check(SaveFiles.Save(e2).Status == SaveStatus.Saved, "시각만 바뀜: 저장");
        // 없어진 파일
        var s3 = Site.Open(dir);
        var e3 = SiteEdit.SetComment(s3, St(s3, "tag_table.c", "TAG ( 5,"), "y").Site!;
        File.Delete(path);
        check(SaveFiles.Save(e3).Status == SaveStatus.Conflict && !File.Exists(path), "없어진 파일: 저장 안 함");
    }

    static void SaveFailure()
    {
        // 살펴볼 것 5: 저장 중 실패 → 모든 파일이 저장 전 그대로
        var dir = Copy("fail");
        Age(dir);
        var s = Site.Open(dir);
        var a = SiteEdit.SetComment(s, St(s, "tag_table.c", "TAG ( 2,"), "a").Site!;
        var b = SiteEdit.SetComment(a, St(a, "manual_table.c", "MAN_OBS_B"), "b").Site!;
        var before = Disk(dir);
        bool Untouched() => SameDisk(before, Disk(dir)) && Directory.GetFiles(dir).All(f => File.GetLastWriteTimeUtc(f) == Old);

        var ro = Path.Combine(dir, "manual_table.c");
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        try
        {
            var r = SaveFiles.Save(b);
            check(r.Status == SaveStatus.Failed && r.Reason!.StartsWith("쓰기 실패", StringComparison.Ordinal), $"읽기 전용 대상: 실패 {r.Status} {r.Reason}");
        }
        finally { File.SetAttributes(ro, FileAttributes.Normal); }
        check(Untouched(), "읽기 전용 대상: 앞서 바꾼 tag_table.c 도 되돌림(바이트·시각), 임시 파일 없음");

        // R-3: 바꿔치기 뒤 파일이 다르게 읽힌다
        SaveFiles.AfterSwap = p => File.AppendAllText(p, "x");
        try
        {
            var r = SaveFiles.Save(b);
            check(r.Status == SaveStatus.Failed && r.Reason!.StartsWith("R-3", StringComparison.Ordinal), $"R-3 {r.Status} {r.Reason}");
        }
        finally { SaveFiles.AfterSwap = null; }
        check(Untouched(), "R-3: 모든 파일 복구");

        // 둘째 파일에서 디스크 오류
        int n = 0;
        SaveFiles.AfterSwap = _ => { if (++n == 2) throw new IOException("디스크 가득"); };
        try { check(SaveFiles.Save(b).Status == SaveStatus.Failed, "디스크 오류 실패"); }
        finally { SaveFiles.AfterSwap = null; }
        check(Untouched(), "디스크 오류: 모든 파일 복구");

        check(SaveFiles.Save(b).Status == SaveStatus.Saved && SaveFiles.Changed(Site.Open(dir)).Count == 0, "그 뒤 저장은 된다");
    }
}
