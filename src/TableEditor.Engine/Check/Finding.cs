using TableEditor.Engine.Text;

namespace TableEditor.Engine.Check;

public enum Severity { Error, Warning }

/// <summary>
/// 검사 결과 하나. Rule = 사양 6절 번호, File = 파일 이름(코어는 <c>core/…</c>), Piece = 걸린 조각,
/// Cell = 걸린 인자(없으면 문장 전체), Candidates = 가까운 이름(N-1), Related = 관련 줄(파일 줄 번호).
/// </summary>
public sealed record Finding(
    string Rule, Severity Severity, string File, Piece? Piece, Arg? Cell, string Text,
    IReadOnlyList<string> Candidates, IReadOnlyList<int> Related)
{
    /// <summary>파일 줄 번호(1부터). 인자가 있으면 그 인자가 시작하는 줄. 파일 밖 결과는 0.</summary>
    public int Line => Piece == null ? 0
        : Cell == null ? Piece.StartLine
        : Piece.StartLine + Piece.Text.AsSpan(0, Math.Min(Cell.Offset, Piece.Text.Length)).Count('\n');

    /// <summary>빠른 고침: 인자 맨 앞 이름을 이 낱말로 바꾼다(자리가 틀린 <c>X_…</c>↔<c>IF_…</c>). 없으면 null.</summary>
    public string? Replace { get; init; }

    public string Grade => Severity == Severity.Error ? "오류" : "경고";

    /// <summary>명령줄 한 줄: <c>등급 규칙 파일:줄 글</c>.</summary>
    public override string ToString() => $"{Grade} {Rule} {File}:{Line} {Text}";
}
