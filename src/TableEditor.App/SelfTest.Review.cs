using System.IO;
using System.Text;
using TableEditor.App.Blocks;
using TableEditor.App.ViewModels;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.App;

/// <summary>
/// 자체 시험 v0.2.2 독립 검토 재현: 1 옛 코어 ACS_SEND 칸 0.
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

        return Task.CompletedTask;
    }
}
