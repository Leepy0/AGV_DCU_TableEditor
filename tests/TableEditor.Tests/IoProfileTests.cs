using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

/// <summary>v0.2.2: IO 맵 신호의 프로파일 지정(<c>/* pf: … */</c>) 읽기·쓰기·왕복 바이트.</summary>
static class IoProfileTests
{
    public static void Run(Action<bool, string> check, string root)
    {
        var folder = Path.Combine(root, "tests", "fixtures", "T900_DEMO");
        var s = Site.Open(folder);
        string Text(Site x) => x.File("io_map.h")!.ToText();
        Statement Def(Site x, string n) => (Statement)x.Names.Find(n)!.Def!;

        check(s.Names.Find("DEMO_LIFT_TOP")!.Profiles is ["PF_LIFT"] && s.Names.Find("DEMO_SEN_1")!.Profiles == null
            && s.Names.Find("DEMO_SENH_1")!.Profiles is ["PF_BELT", "PF_CART"], "pf 읽기");
        check(s.Names.Find("DEMO_SENH_1")!.Comment == "/* 핀 1 활성 */" && s.Names.Find("DEMO_LIFT_TOP")!.Comment == "", "pf 주석은 주석 칸에서 뺀다");
        check(File.ReadAllBytes(Path.Combine(folder, "io_map.h")).SequenceEqual(s.File("io_map.h")!.ToBytes()), "pf 줄 왕복 바이트");

        var orig = Text(s);
        var r = IoProfile.Set(s, Def(s, "DEMO_SEN_1"), ["PF_BELT", "PF_BOX"]);
        check(r.Ok && Text(r.Site!).Contains("#define DEMO_SEN_1      IN_EXT_1    /* pf: PF_BELT PF_BOX */\n") && r.Site!.Names.Find("DEMO_SEN_1")!.Profiles is ["PF_BELT", "PF_BOX"], "pf 쓰기: 주석 없는 줄");
        var back = IoProfile.Set(r.Site!, Def(r.Site!, "DEMO_SEN_1"), []);
        check(back.Ok && Text(back.Site!) == orig, "지정을 다 지우면 원래 줄 바이트");

        var r2 = IoProfile.Set(s, Def(s, "DEMO_SENH_1"), ["PF_BOX"]);
        check(r2.Ok && Text(r2.Site!).Contains("/* 핀 1 활성 */ /* pf: PF_BOX */"), "pf 바꾸기: 기존 주석 뒤");
        var r3 = IoProfile.Set(r2.Site!, Def(r2.Site!, "DEMO_SENH_1"), []);
        check(r3.Ok && Text(r3.Site!).Contains("IN_EXT_5 )      /* 핀 1 활성 */\n"), "지우면 pf 주석만 사라진다");

        var plain = Site.FromFiles([SourceFile.Parse("io_map.h", System.Text.Encoding.UTF8.GetBytes("#define A_1 IN_EXT_1 // note\n#define A_2 IN_EXT_2   \n#define A_3 IN_EXT_3 /* c */\n"))]);
        check(!IoProfile.Set(plain, Def(plain, "A_1"), ["PF_BELT"]).Ok, "줄 주석(//) 줄은 막는다");
        var sp = IoProfile.Set(plain, Def(plain, "A_2"), ["PF_BELT"]);
        var sp0 = IoProfile.Set(sp.Site!, Def(sp.Site!, "A_2"), []);
        check(sp.Ok && sp0.Ok && Text(sp0.Site!) == Text(plain) && Text(sp.Site!).Contains("IN_EXT_2    /* pf: PF_BELT */   \n"), "꼬리 공백 줄도 왕복");
        var sc = IoProfile.Set(plain, Def(plain, "A_3"), ["PF_BELT"]);
        check(sc.Ok && Text(sc.Site!).Contains("/* c */ /* pf: PF_BELT */\n") && !IoProfile.Set(plain, Def(plain, "A_3"), ["bad name"]).Ok, "기존 주석 뒤 한 칸 · 이름 검사");
    }
}
