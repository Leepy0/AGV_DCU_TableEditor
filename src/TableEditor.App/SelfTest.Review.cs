using System.IO;
using System.Text;
using TableEditor.App.Blocks;
using TableEditor.App.ViewModels;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.App;

/// <summary>
/// 자체 시험 v0.2.2 독립 검토 재현: 1 옛 코어 ACS_SEND 칸 0, 3 동작 4칸 한계, 4 숫자→스테이션이 종류를 지킴, 5 STATION0 → 숫자 값 버림,
/// 7 기존 값 그대로인 칸은 범위 검사 안 함, 10 대응표 프로파일 번호. 메모리 사이트로 칸·팝업 모델을 직접 부른다.
/// </summary>
public sealed partial class SelfTest
{
    static readonly UTF8Encoding Utf8Review = new(false);

    static Site Mem(string? core, params (string Name, string Text)[] files) =>
        Site.FromFiles(files.Select(f => SourceFile.Parse(f.Name, Utf8Review.GetBytes(f.Text))), "", core);

    static string RvTag(string line) => "TAG_TABLE_BEGIN( t )\n    " + line + "\nTAG_TABLE_END\n";

    Task Review()
    {
        var core8 = FakeCore(Path.Combine(Path.GetFullPath(work), "rv_core58"), "v0.58.0");

        Step("리뷰 1 옛 코어 시퀀스: ACS_SEND 칸 0 = STATION(0), ACS 칸 0 = STATION0");
        var old = Mem(null, ("seq_table.c", "SEQ_LABELS( DONE, ERR )\nSEQ_BEGIN( t )\n    ACS_SEND( ARRIVE, STATION(5) )\n    ACS( ARRIVE, STATION(5) )\n    L( DONE )\n    END_STOP\nSEQ_END\n"));
        var ob = new BlockBuilder(old.Names, []);
        string Cell0(Statement st) => ob.Statement(st, Place.Step).Fields.First(f => f.Label == "칸").Write!("0");
        var oldSteps = old.File("seq_table.c")!.Statements;
        Check(!old.NewDsl && Cell0(oldSteps.First(s => s.Name == "ACS_SEND")) == "STATION(0)" && Cell0(oldSteps.First(s => s.Name == "ACS")) == "STATION0", "ACS_SEND → STATION(0) · ACS → STATION0");

        Step("리뷰 3·4·5 태그 보고: 4칸 한계 · 종류 지킴 · STATION0 버림");
        var full = Mem(core8, ("tag_table.c", RvTag("TAG( 5, ANY_PROFILE, OBS_ON, AGV_SPEED( LOW ), OBS_OFF, ACS( LOADING, STATION0 ) )")));
        var fb = new BlockBuilder(full.Names, []);
        var fr = full.TagRules[0];
        var st4 = fb.Action(fr.St, fr.Actions[3], 3).Fields.First(f => f.Label == "칸");
        Check(st4.Check!("2") == "동작 칸 넘침 · 4칸" && st4.Check("0") == null, $"3 칸 0→2: 4칸이 차면 막음 ({st4.Check("2")})");
        var three = Mem(core8, ("tag_table.c", RvTag("TAG( 5, ANY_PROFILE, OBS_ON, AGV_SPEED( LOW ), ACS( LOADING, STATION0 ) )")));
        var tr3 = three.TagRules[0];
        Check(new BlockBuilder(three.Names, []).Action(tr3.St, tr3.Actions[2], 2).Fields.First(f => f.Label == "칸").Check!("2") == null, "3칸 줄에서는 통과");
        var reset = Mem(core8, ("tag_table.c", RvTag("TAG( 6, ANY_PROFILE, ACS( STANDBY_RESET, 14 ) )")));
        var rr = reset.TagRules[0];
        var form = new BlockBuilder(reset.Names, []).Action(rr.St, rr.Actions[0], 0).Fields.First(f => f.Label == "위치");
        Check(form.Write!("스테이션") == "ACS( STANDBY_RESET, STATION(0) )", $"4 숫자→스테이션: 종류 유지 ({form.Write("스테이션")})");
        var one = Mem(core8, ("tag_table.c", RvTag("TAG( 7, ANY_PROFILE, ACS( LOADING, STATION0 ) )")));
        var or1 = one.TagRules[0];
        var form1 = new BlockBuilder(one.Names, []).Action(or1.St, or1.Actions[0], 0).Fields.First(f => f.Label == "위치");
        Check(form1.Drops!("숫자").SequenceEqual(["STATION0"]) && form1.Drops("스테이션").Count == 0, $"5 STATION0 → 숫자는 값 버림 ({string.Join(",", form1.Drops("숫자"))})");

        Step("리뷰 7 조건 팝업: 열었을 때 값 그대로인 칸은 범위 밖이어도 막지 않는다");
        var idr = Mem(core8, ("tag_table.c", RvTag("TAG_IF( 5, ANY_PROFILE, IF_ID( 3, 150 ), OBS_ON )")));
        var cv = new CondVm(idr.TagRules[0], idr.Names);
        var idBox = cv.XRows[0].Args[1];
        Check(idBox.Text == "150" && !idBox.Invalid && cv.Ok.CanExecute(null), "150 그대로 = 막지 않음");
        cv.XRows[0].Args[0].Text = "4";
        Check(cv.Ok.CanExecute(null), "다른 칸만 고쳐도 열림");
        idBox.Text = "151";
        Check(idBox.Invalid && !cv.Ok.CanExecute(null), "바꾼 칸은 범위 검사(151 = 막힘)");
        idBox.Text = "99";
        Check(!idBox.Invalid && cv.Ok.CanExecute(null), "범위 안 값 = 열림");

        Step("리뷰 10 시퀀스 고르기: 대응표 프로파일이 번호(1)여도 이름으로 푼다");
        var num = Mem(null,
            ("site.h", "enum {\n    PF_A = 1,\n    PF_B\n};\n"),
            ("profiles.c", "PROFILE_TABLE_BEGIN( p )\n    PROFILE( PF_A, 0, 1, 2 )\n    PROFILE( PF_B, 0, 3, 4 )\nPROFILE_TABLE_END\n"),
            ("seq_table.c", "SEQ_LABELS( DONE )\nSEQ_BEGIN( t_a )\n    L( DONE )\n    END_STOP\nSEQ_END\nSEQ_BEGIN( t_b )\n    L( DONE )\n    END_STOP\nSEQ_END\nSEQ_MAP_BEGIN( m )\n    SEQ_MAP( 1, SEQ_LOAD, t_a )\n    SEQ_MAP( PF_B, SEQ_UNLOAD, t_b )\nSEQ_MAP_END\n"),
            ("tag_table.c", RvTag("TAG( 40, PF_A, SEQ_START( LOAD ) )")));
        var seqSpec = new ArgSpec(ArgKind.SeqShort);
        var pa = Choices.For(seqSpec, num.Names, TagVm.ScopeOf(num, num.TagRules[0])).ToList();
        Check(pa.First(c => c.Name == "LOAD") is { Group: Choices.Linked, Note: "→ t_a" } && pa.First(c => c.Name == "UNLOAD").Group == Choices.Unlinked, $"PF_A 줄: LOAD 연결(번호 1) · UNLOAD 연결 없음");
        var any = Choices.For(seqSpec, num.Names, TagVm.ScopeOf(num, null)).ToList();
        Check(any.First(c => c.Name == "LOAD").Note == "1곳" && any.First(c => c.Name == "UNLOAD").Note == "1곳", "ANY_PROFILE 줄: 번호도 이름으로 센다");
        return Task.CompletedTask;
    }
}
