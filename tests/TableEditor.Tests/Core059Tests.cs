using System.Text;
using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

/// <summary>v0.2.2(코어 v0.59.0): 보고 종류 IDLE 의 최소 버전(V-4), 수동 MAN 의 AND( KEY, 상태 ) 한계(D-1). 이름은 모두 지어낸 것.</summary>
static class Core059Tests
{
    static readonly UTF8Encoding Utf8 = new(false);

    static string Core(string name, string version)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tedit_core059_tests", name);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "VERSION"), version + " 0000000 cl2000 v0\n", Utf8);
        return dir;
    }

    static Site Make(string? core, string file, string text) => Site.FromFiles([SourceFile.Parse(file, Utf8.GetBytes(text))], "", core);

    static Site Seq(string? core, params string[] lines) => Make(core, "seq_table.c",
        "SEQ_LABELS( DONE, ERR )\nSEQ_BEGIN( t )\n" + string.Concat(lines.Select(l => "    " + l + "\n")) + "    L( DONE )\n    END_STOP\nSEQ_END\n");

    static Site Tag(string? core, params string[] lines) => Make(core, "tag_table.c",
        "TAG_TABLE_BEGIN( site_tags )\n" + string.Concat(lines.Select(l => "    " + l + "\n")) + "TAG_TABLE_END\n");

    static Site Man(string? core, string line) => Make(core, "manual_table.c",
        "#define SEN_1 IN_EXT_1\nMANUAL_BEGIN( m )\n    " + line + "\n    MAN_ELSE( 5, JOG, NO_OUT )\nMANUAL_END\n");

    static List<Finding> V4(Site s) => Checker.Run(s).Where(f => f.Rule == "V-4" && f.Text.Contains("IDLE")).ToList();

    public static void Run(Action<bool, string> check)
    {
        var c59 = Core("c59", "v0.59.0");
        var c58 = Core("c58", "v0.58.0");
        var c57 = Core("c57", "v0.57.0");

        check(GrammarTable.IdleOk("v0.59.0") && GrammarTable.IdleOk("v0.60.1") && !GrammarTable.IdleOk("v0.58.0") && !GrammarTable.IdleOk(null), "IDLE 최소 버전 v0.59.0");
        check(Seq(c59, "ACS( IDLE, 77 )").Names.IdleOk && !Seq(c58, "ACS( IDLE, 77 )").Names.IdleOk && !Seq(null, "ACS( IDLE, 77 )").Names.IdleOk, "사이트 IdleOk");

        var ok = Seq(c59, "ACS( IDLE, STATION0 )", "ACS( IDLE, 77 )", "ACS( IDLE, STATION(0) + 1 )", "ACS( IDLE, AGV_ID )", "ACS_SEND( IDLE, STATION(2) )");
        check(V4(ok).Count == 0 && Checker.Run(ok).All(f => !f.Text.Contains("IDLE")), "v0.59.0: 시퀀스 IDLE 오류 없음");
        var low = Seq(c58, "ACS( IDLE, 77 )", "ACS_SEND( IDLE, STATION(2) )", "ACS( NEXT, 5 )");
        var lowF = V4(low);
        check(lowF.Count == 2 && lowF.All(f => f.Severity == Severity.Error && f.Text == "코어 v0.59.0 필요 · IDLE"), $"v0.58.0: 시퀀스 IDLE 줄마다 오류 ({lowF.Count})");
        check(V4(Seq(c57, "ACS_SEND( IDLE, 5 )")).Count == 1, "v0.57.0: 옛 이름 ACS_SEND( IDLE ) 도 오류");
        check(V4(Seq(null, "ACS( IDLE, 77 )")).Count == 0, "코어 폴더 없음: 기존 규칙대로(안 건다)");

        check(V4(Tag(c59, "TAG( 22, ANY_PROFILE, ACS( IDLE, STATION0 ) )", "TAG( 23, ANY_PROFILE, ACS( IDLE, 200 ), ACS_SEND( IDLE, AGV_ID_MINUS(3) ) )")).Count == 0, "v0.59.0: 태그 1칸·2칸 IDLE 오류 없음");
        check(V4(Tag(c58, "TAG( 22, ANY_PROFILE, ACS( IDLE, STATION0 ) )", "TAG( 23, ANY_PROFILE, ACS( IDLE, 200 ), ACS_SEND( IDLE, AGV_ID_MINUS(3) ) )")).Count == 2, "v0.58.0: 태그 IDLE 줄마다 오류");
        check(V4(Man(c58, "MAN( 5, IDLE, KEY( UP ), BELT( 1, CW ) )")).Count == 0, "수동 표 모드 IDLE 은 다른 낱말(오류 아님)");

        // D-1 수동 AND: KEY 하나 + 입력만
        bool D1(string cond) => Checker.Run(Man(c59, $"MAN( 5, JOG, {cond}, BELT( 1, CW ) )")).Any(f => f.Rule == "D-1");
        check(D1("AND( KEY( UP ), FORWARD )") && D1("AND( KEY( UP ), KEY( DOWN ) )") && D1("AND( FORWARD, KEY( UP ) )"), "D-1: 수동 AND( KEY, 상태 )·AND( KEY, KEY ) 오류");
        check(!D1("AND( KEY( UP ), IN( ON(SEN_1) ) )") && !D1("AND( KEY( UP ) )"), "D-1: KEY 하나 + 입력은 통과");

        // 리뷰 6: 사이트가 보고 종류 낱말을 #define 하면 DSL_INFO_##종류 가 먼저 펼쳐져 컴파일이 깨진다(gcc 로 확인) → V-4
        Site Shadow(string? core, string def, string line) => Make(core, "seq_table.c", def + "\nSEQ_LABELS( DONE, ERR )\nSEQ_BEGIN( t )\n    " + line + "\n    L( DONE )\n    END_STOP\nSEQ_END\n");
        List<Finding> Shad(Site s) => Checker.Run(s).Where(f => f.Rule == "V-4" && f.Text.Contains("덮음")).ToList();
        check(Shad(Shadow(c59, "#define IDLE 7", "ACS( IDLE, 5 )")).Count == 1 && Shad(Shadow(c59, "#define LOADING 1", "ACS_SEND( LOADING, 5 )")).Count == 1, "사이트 #define 이 보고 종류를 덮음 = V-4 오류(IDLE·LOADING)");
        check(Shad(Shadow(c59, "#define IDLE 7", "ACS( NEXT, 5 )")).Count == 0 && Shad(Shadow(c59, "", "ACS( IDLE, 5 )")).Count == 0 && Shad(Shadow(null, "#define IDLE 7", "ACS( IDLE, 5 )")).Count == 0, "쓴 줄이 없거나 정의가 없으면(코어 모름 포함) 안 건다");
        check(V4(Shadow(c58, "#define IDLE 7", "ACS( IDLE, 5 )")).Count == 2, "v0.58.0 + 사이트 IDLE 정의: 최소 버전 오류도 사이트 정의와 상관없이");
    }
}
