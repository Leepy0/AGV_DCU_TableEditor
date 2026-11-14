using System.Text;
using System.Text.RegularExpressions;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Text;

// 패키지 없는 시험. 종료 코드 0 = 통과.
Console.OutputEncoding = new UTF8Encoding(false);
int failures = 0;
var utf8 = new UTF8Encoding(false);

void Check(bool ok, string what)
{
    if (ok) return;
    failures++;
    Console.WriteLine("실패  " + what);
}

SourceFile ParseText(string name, string text) => SourceFile.Parse(name, utf8.GetBytes(text));

bool SameBytes(SourceFile f, byte[] bytes) => f.ToBytes().AsSpan().SequenceEqual(bytes);

void RoundTrip(string name, string text, string what)
{
    var bytes = utf8.GetBytes(text);
    Check(SameBytes(SourceFile.Parse(name, bytes), bytes), what + ": 왕복 바이트");
}

string RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TableEditor.sln"))) dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("TableEditor.sln 을 못 찾음");
}

var tests = new (string Name, Action Run)[]
{
    ("문법 표 163", () =>
    {
        Check(GrammarTable.Names.Count == 163, $"공개 이름 {GrammarTable.Names.Count} != 163");
        int Distinct(params Place[] places) => GrammarTable.Rows.Where(r => places.Contains(r.Place)).Select(r => r.Name).Distinct().Count();
        Check(Distinct(Place.Frame) == 23, "4.1 표 틀 23");
        Check(Distinct(Place.InputCond) == 8, "4.2 입력 조건 8");
        Check(Distinct(Place.ExtCond) == 22, "4.3 확장 조건 22");
        Check(Distinct(Place.Step) == 62, "4.4 스텝 62");
        Check(Distinct(Place.Manual) == 5, "4.5 수동 5");
        Check(Distinct(Place.TagRule, Place.TagCond) == 20, "4.6 태그 규칙·조건 20");
        var step = GrammarTable.Rows.Where(r => r.Place == Place.Step).Select(r => r.Name).ToHashSet();
        var tagAct = GrammarTable.Rows.Where(r => r.Place == Place.TagAction).Select(r => r.Name).Distinct().ToList();
        Check(tagAct.Count(step.Contains) == 12, "4.7 같은 이름 12");
        Check(tagAct.Count(n => !step.Contains(n)) == 10, "4.7 태그 전용 10");
        Check(Distinct(Place.Packed) == 13, "4.8 압축 13");
        Check(GrammarTable.Rows.Where(r => r.ActForbidden).Select(r => r.Name).Distinct().Count() == 8, "ACT 금지 8 이름");
        var tableNames = CoreConstants.Params.SelectMany(p => p.TableNames).Distinct().Count();
        Check(tableNames == 47, $"파라미터 표 이름 {tableNames} != 47");
        Check(GrammarTable.Rows.All(r => r.MinArgs <= r.MaxArgs), "인자 수 범위");
        Check(GrammarTable.Find("ON").Single().Spacing == Spacing.Tight && GrammarTable.Find("IN").Single().Spacing == Spacing.Spaced
            && GrammarTable.Find("X_ID").Single().Spacing == Spacing.Tight && GrammarTable.Find("END_STOP").Single().Spacing == Spacing.None, "꼴(안쪽 공백)");
    }),

    ("문법 표 칸 라벨·팔레트", () =>
    {
        var turn = GrammarTable.Find("AGV_TURN", Place.TagAction).Single();
        Check(turn.Args.Select(a => a.Label).SequenceEqual(["방향", "각도", "회전 뒤", "자동 출발", "속도", "옵션", "옵션"]), "회전 칸 라벨");
        var sensor = GrammarTable.Find("AGV_STOP", Place.TagAction).Single(r => r.Args.Count == 2);
        Check(sensor.Args[1].Label == "센서", "안쪽 묶음 칸 라벨");
        Check(GrammarTable.Find("ON").Single().Args[0].Label == "신호", "… 뗀 라벨");
        Check(GrammarTable.Rows.Where(r => r.Place == Place.TagAction && r.TagCells > 0).All(r => GrammarTable.TagPalette.Contains(r.Palette)), "태그 동작 팔레트");
        Check(GrammarTable.Rows.Where(r => r.Place == Place.Step && r.Role == Role.Statement).All(r => GrammarTable.StepPalette.Contains(r.Palette)), "스텝 팔레트");
        Check(GrammarTable.Find("SEQ_START", Place.TagAction).All(r => r.Head == "시퀀스") && GrammarTable.Find("AGV_STOP", Place.TagAction).All(r => r.Head == "정지"), "같은 매크로 같은 머리");
    }),

    ("살펴볼 것 1: 줄 끝 섞임·끝 줄 끝 없음·줄 끝 공백", () =>
    {
        var text = "/* a */\r\n#include \"x.h\"\n\nTAG_TABLE_BEGIN( site_tags )\r\n    TAG ( 1, ANY_PROFILE, AGV_STOP )   \r\n    \t \n"
                 + "    TAG( 2, ANY_PROFILE, OBS_ON )\nTAG_TABLE_END";
        var f = ParseText("tag_table.c", text);
        Check(SameBytes(f, utf8.GetBytes(text)), "섞인 줄 끝 왕복");
        Check(!f.ReadOnly && !f.RawInZones.Any(), "원문 없음");
        var st = f.Statements.ToList();
        Check(st.Select(x => x.Name).SequenceEqual(["TAG_TABLE_BEGIN", "TAG", "TAG", "TAG_TABLE_END"]), "문장 넷");
        Check(st[1].Tail == "   " && st[1].LineEnd == "\r\n" && st[2].LineEnd == "\n" && st[3].LineEnd == "", "꼬리·줄 끝");
        RoundTrip("seq_table.c", "SEQ_BEGIN( a )\n    DELAY( 1 )\r\nSEQ_END\r\n\r\n\n", "빈 줄 섞임");
        RoundTrip("site.h", "enum {\r\n    PF_A = 1,  \n    PF_B\r\n};", "enum 끝 줄 끝 없음");
        RoundTrip("io_map.h", "#define A IN_EXT_1\t \r\n#define B OUT_EXT_1", "define 끝 줄 끝 없음");
        RoundTrip("tag_table.c", "", "빈 파일");
        RoundTrip("tag_table.c", "\n", "줄 끝 하나");
        RoundTrip("tag_table.c", "x\ry\r\n", "외톨이 CR");
    }),

    ("살펴볼 것 2: 여러 줄 문장·이름 뒤 공백·인자 안 주석", () =>
    {
        var text = "TAG_TABLE_BEGIN( site_tags )\n"
                 + "    TAG ( 1,   ANY_PROFILE,  AGV_STOP )   /* 꼬리 */\n"
                 + "    TAG_IF( 70, ANY_PROFILE,\r\n            IF_X( X_ERR_EQ(281) ),\n            AGV_STOP )      /* 여러\n     줄 */\n"
                 + "    TAG( 4, /* x */ PF_A, AGV_STOP )\n"
                 + "TAG_TABLE_END\n";
        var f = ParseText("tag_table.c", text);
        Check(SameBytes(f, utf8.GetBytes(text)), "왕복");
        var st = f.Statements.ToList();
        var t1 = st.Single(x => x.StartLine == 2);
        Check(t1.Gap == " " && t1.Args.Count == 3 && t1.Args[0].Text == "1" && t1.Args[0].Lead == " " && t1.Args[0].Trail == ""
              && t1.Args[1].Lead == "   " && t1.Args[2].Trail == " " && t1.TailComment == "/* 꼬리 */", "TAG ( 1, … 인자 공백");
        var t2 = st.Single(x => x.Name == "TAG_IF");
        Check(t2.StartLine == 3 && t2.EndLine == 6 && t2.Args.Count == 4 && t2.Args[2].Text == "IF_X( X_ERR_EQ(281) )"
              && t2.Args[2].Lead == "\r\n            ", "여러 줄 문장 한 조각");
        Check(t2.Args[2].Node is CallNode { Name: "IF_X" } c && c.Args[0].Node is CallNode { Name: "X_ERR_EQ" } x
              && x.Args[0].Node is NumberNode { Value: 281 }, "풀이 나무");
        var raw = f.RawInZones.ToList();
        Check(raw.Count == 1 && raw[0].Text.Contains("/* x */") && raw[0].StartLine == 7, "인자 안 주석 → 원문");
    }),

    ("살펴볼 것 3: 모르는 매크로·인자 꼴 → 원문", () =>
    {
        string[] bad =
        [
            "    NEW_MACRO( 1, 2 )",
            "    TAG( 1 )",
            "    TAG( 1, PF_A, ( TA_ACS | DSL_INFO_ARRIVE ) )",
            "    TAG( 1, PF_A, AGV_STOP ) TAG( 2, PF_A, AGV_STOP )",
            "    TAG( 1, PF_A, x ? 1 : 2 )",
            "    TAG( 1, PF_A, CALL( 'B' ) )",
            "    { 41, PF_A, 0, 0, 0, { TA_STOP } },",
            "#if X",
            "    LOAD_IF( A, IN( ON(B) ) )",
        ];
        foreach (var line in bad)
        {
            var text = "TAG_TABLE_BEGIN( site_tags )\n    TAG( 0, ANY_PROFILE, AGV_STOP )\n" + line + "\nTAG_TABLE_END\n";
            var f = ParseText("tag_table.c", text);
            Check(SameBytes(f, utf8.GetBytes(text)), $"왕복: {line.Trim()}");
            Check(!f.ReadOnly && f.RawInZones.Count() == 1 && f.Statements.Count() == 3, $"원문 블록 1: {line.Trim()}");
        }
        var seq = "SEQ_BEGIN( a )\n    NEW_STEP( 3 )\n    AGV_STOP\n    DELAY( 1 )\nSEQ_END\n";
        var fs = ParseText("seq_table.c", seq);
        Check(SameBytes(fs, utf8.GetBytes(seq)) && fs.RawInZones.Count() == 1 && fs.Statements.Count() == 3, "스텝 구역: 모르는 이름·태그 전용 이름");
    }),

    ("A-12 꼴: 직접 초기화 줄", () =>
    {
        var text = "TAG_TABLE_BEGIN( site_tags )\r\n    TAG( 1, ANY_PROFILE, AGV_STOP )\r\n    { 41, PF_BELT, 0, 0, 0, { TA_STOP } },\r\n    TAG( 2, ANY_PROFILE, AGV_STOP )\r\nTAG_TABLE_END\r\n";
        var f = ParseText("tag_table.c", text);
        Check(SameBytes(f, utf8.GetBytes(text)) && f.RawInZones.Count() == 1, "원문 1 + 바이트 동일");
    }),

    ("인자 자리(Offset)", () =>
    {
        var text = "TAG_TABLE_BEGIN( site_tags )\n    TAG_IF( 4, PF_A, IF_X2( X_ID(5, 6), X_STATION(0, 70, 71) ),\n      ACS_SEND( ARRIVE, STATION(1) + 100 ) )\nTAG_TABLE_END\n";
        var st = ParseText("tag_table.c", text).Statements.Single(x => x.Name == "TAG_IF");
        bool ok = true;
        void Walk(IEnumerable<Arg> args)
        {
            foreach (var a in args)
            {
                ok &= st.Text.Substring(a.Offset, a.Text.Length) == a.Text;
                if (a.Node is CallNode c) Walk(c.Args);
            }
        }
        Walk(st.Args);
        Check(ok, "모든 인자 Offset 이 글자를 가리킨다");
        Check(st.Args[3].Node is CallNode { Name: "ACS_SEND" } c && c.Args[1].Node is BinaryNode { Op: '+', Right: NumberNode { Value: 100 } }, "식 + n");
    }),

    ("BOM·UTF-8 아님 → 읽기 전용", () =>
    {
        byte[] bom = [0xEF, 0xBB, 0xBF, .. utf8.GetBytes("TAG_TABLE_BEGIN( a )\nTAG_TABLE_END\n")];
        var f = SourceFile.Parse("tag_table.c", bom);
        Check(f.ReadOnly && SameBytes(f, bom), "BOM");
        byte[] bad = [.. utf8.GetBytes("/* "), 0xB0, 0xA1, .. utf8.GetBytes(" */\n")];
        var g = SourceFile.Parse("tag_table.c", bad);
        Check(g.ReadOnly && SameBytes(g, bad), "UTF-8 아님");
    }),

    ("R-2 구역 표지 짝", () =>
    {
        string[] bad =
        [
            "TAG_TABLE_END\n",
            "TAG_TABLE_BEGIN( a )\n    TAG( 1, ANY_PROFILE, AGV_STOP )\n",
            "SEQ_BEGIN( a )\n    DELAY( 1 )\nSEQ_MAP_BEGIN( site_seqs )\n",
            "TAG_TABLE_BEGIN( a )\n    TAG( 1, PF_A, AGV_STOP\nTAG_TABLE_END\n", // 닫히지 않은 괄호가 끝 표지를 삼킨다
        ];
        foreach (var t in bad)
        {
            var name = t.StartsWith("SEQ", StringComparison.Ordinal) ? "seq_table.c" : "tag_table.c";
            var f = ParseText(name, t);
            Check(f.ReadOnly && f.ReadOnlyReason!.StartsWith("R-2", StringComparison.Ordinal) && SameBytes(f, utf8.GetBytes(t)), "R-2: " + t.Split('\n')[0]);
        }
        var e = ParseText("site.h", "enum {\n    PF_A = 1\n");
        Check(e.ReadOnly, "enum 닫힘 없음");
    }),

    ("열거자·define", () =>
    {
        var site = ParseText("site.h", "#ifndef SITE_H\n#define SITE_H\nenum {\n    PF_A = 1,\n    PF_B,       /* b */\n    PF_C\n};\n#endif\n");
        var en = site.Statements.ToList();
        Check(en.Select(x => x.Name).SequenceEqual(["PF_A", "PF_B", "PF_C"]) && en.All(x => x.Shape == StatementShape.Enumerator && x.Zone == "enum"), "열거자 셋");
        Check(en[0].Args.Single().Node is NumberNode { Value: 1 } && en[1].Args.Count == 0 && en[1].TailComment == "/* b */", "열거자 값·주석");
        var io = ParseText("io_map.h", "#ifndef IO_MAP_H\n#define IO_MAP_H\n#define A   IN_ACTIVE_HIGH( IN_EXT_5 )   /* c */\n#define F(n) (n)\n#define G\n#define B1_CCW NO_OUT\n#endif\n");
        var d = io.Statements.ToList();
        Check(d.Select(x => x.Name).SequenceEqual(["A", "B1_CCW"]) && d.All(x => x.Shape == StatementShape.Define), "define 둘(가드·함수 꼴·값 없음은 원문)");
        Check(d[0].Args[0].Node is CallNode { Name: "IN_ACTIVE_HIGH" } && d[0].Args[0].Lead == "   " && d[0].Tail == "   /* c */", "define 값");
    }),

    ("헤더 대조 방식", () =>
    {
        var h = "#ifndef AGV_X_H\n#define AGV_X_H\n#define DSL_A(x) x\n" + string.Join("\n", GrammarTable.Names.Select(n => $"#define {n} 1")) + "\n#define NEW_ONE 2\n#endif\n";
        var r = HeaderCheck.Compare([h]);
        Check(r.Guards == 1 && r.Internal == 1 && r.OnlyHeader.SequenceEqual(["NEW_ONE"]) && r.OnlyGrammar.Count == 0, "가드·DSL_ 빼기, 새 이름 찾기");
    }),

    ("T900_DEMO", () =>
    {
        var folder = Path.Combine(RepoRoot(), "tests", "fixtures", "T900_DEMO");
        var files = SiteFolder.Read(folder);
        Check(files.Count == FileZones.Files.Count, $"읽기 대상 {files.Count} != {FileZones.Files.Count}");
        foreach (var f in files)
        {
            Check(SameBytes(f, File.ReadAllBytes(Path.Combine(folder, f.Name))), f.Name + " 왕복");
            Check(!f.ReadOnly, f.Name + " 읽기 전용 아님");
            Check(!f.RawInZones.Any(), f.Name + " 표 구역 안 원문 0");
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var st in files.SelectMany(f => f.Statements))
        {
            used.Add(st.Name);
            foreach (var a in st.Args) used.UnionWith(a.Node.Names());
        }
        // 4.8 압축 표 이름은 생성물 자리(tag_packed.c)에만 나온다. 이 파일은 읽기 대상이 아니라 글자로 찾는다
        var packed = File.ReadAllText(Path.Combine(folder, "tag_packed.c"));
        used.UnionWith(Regex.Matches(packed, @"\b[A-Za-z_]\w*\b").Select(m => m.Value));
        var missing = GrammarTable.Names.Where(n => !used.Contains(n)).ToList();
        Check(missing.Count == 0, "fixture 에 없는 문법 이름: " + string.Join(", ", missing));
        var multi = files.Single(f => f.Name == "tag_table.c").Statements.Single(s => s.Name == "TAG_IF" && s.EndLine > s.StartLine);
        Check(multi.Args.Count == 4, "fixture 여러 줄 문장");
    }),

    ("Ops(작업 3)", () => OpsTests.Run(Check)),
    ("검사 규칙", () => CheckTests.Run(Check, RepoRoot())),
    ("편집·저장(작업 2)", () => EditTests.Run(Check, RepoRoot())),
    ("검토 결함: 읽기·편집·저장(작업 8)", () => ReviewTests.Run(Check, RepoRoot())),
    ("깃·회차·코어 올리기(작업 6)", () => GitTests.Run(Check)),

    ("DCU_APP_ROOT(있을 때만)", () =>
    {
        var root = Environment.GetEnvironmentVariable("DCU_APP_ROOT");
        if (string.IsNullOrEmpty(root)) { Console.WriteLine("건너뜀  DCU_APP_ROOT 없음"); return; }
        foreach (var dir in Directory.GetDirectories(Path.Combine(root, "site")))
            foreach (var f in SiteFolder.Read(dir))
                Check(SameBytes(f, File.ReadAllBytes(Path.Combine(dir, f.Name))), $"{Path.GetFileName(dir)}/{f.Name} 왕복");
        Check(HeaderCheck.Run(Path.Combine(root, "core")).Same, "core 헤더 대조");
    }),
};

foreach (var (name, run) in tests)
{
    int before = failures;
    try { run(); }
    catch (Exception e) { failures++; Console.WriteLine($"예외  {name}: {e}"); }
    Console.WriteLine($"{(failures == before ? "통과" : "실패")}  {name}");
}
Console.WriteLine(failures == 0 ? "모두 통과" : $"실패 {failures}");
return failures == 0 ? 0 : 1;
