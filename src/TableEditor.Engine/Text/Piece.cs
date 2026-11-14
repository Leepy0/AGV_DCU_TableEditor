namespace TableEditor.Engine.Text;

/// <summary>조각 종류(사양 5.1절).</summary>
public enum PieceKind { Statement, Comment, Blank, Raw }

/// <summary>
/// 파일의 조각 하나. Text = 원문 글자(줄 끝 문자 포함, 줄 단위). StartLine·EndLine 은 1부터.
/// Zone = 조각이 든 표 구역의 여는 이름(<c>TAG_TABLE_BEGIN</c>, <c>enum</c> 등). 최상위·구역 표지는 null.
/// </summary>
public record Piece(PieceKind Kind, string Text, int StartLine, int EndLine, string? Zone)
{
    public string LineEnd => Text.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : Text.EndsWith('\n') ? "\n" : "";
}

/// <summary>문장의 꼴. Macro = 표 매크로, Define = <c>#define 이름 값</c>, Enumerator = enum 안 열거자 줄.</summary>
public enum StatementShape { Macro, Define, Enumerator }

/// <summary>
/// 문장 조각. Indent = 줄 앞 공백, Gap = 이름과 <c>(</c> 사이 글자(열거자는 <c> = </c> 까지),
/// Args = 괄호 깊이 0 의 인자(define·열거자는 값 하나), Tail = 문장 뒤 그 줄 끝까지(공백·꼬리 주석, 열거자는 쉼표 포함).
/// </summary>
public sealed record Statement(
    StatementShape Shape, string Indent, string Name, string Gap, bool HasParens, IReadOnlyList<Arg> Args, string Tail,
    string Text, int StartLine, int EndLine, string? Zone)
    : Piece(PieceKind.Statement, Text, StartLine, EndLine, Zone)
{
    /// <summary>꼬리 주석 글자(<c>/* … */</c> 또는 <c>// …</c>). 없으면 빈 글자.</summary>
    public string TailComment
    {
        get
        {
            int i = Tail.IndexOf("/*", StringComparison.Ordinal);
            int j = Tail.IndexOf("//", StringComparison.Ordinal);
            int k = i < 0 ? j : j < 0 ? i : Math.Min(i, j);
            return k < 0 ? "" : Tail[k..].TrimEnd();
        }
    }
}

/// <summary>
/// 인자 하나. Lead·Trail = 앞뒤 공백(줄바꿈 포함 가능), Text = 인자 글자, Offset = 조각 Text 안에서 Text 가 시작하는 자리.
/// </summary>
public sealed record Arg(string Lead, string Text, string Trail, Node Node, int Offset);

/// <summary>인자의 풀이 나무.</summary>
public abstract record Node
{
    /// <summary>나무 안의 모든 이름(호출 이름 포함), 앞 차례.</summary>
    public IEnumerable<string> Names()
    {
        switch (this)
        {
            case NameNode n:
                yield return n.Name;
                break;
            case CallNode c:
                yield return c.Name;
                foreach (var a in c.Args)
                    foreach (var x in a.Node.Names()) yield return x;
                break;
            case BinaryNode b:
                foreach (var x in b.Left.Names()) yield return x;
                foreach (var x in b.Right.Names()) yield return x;
                break;
        }
    }
}

public sealed record NameNode(string Name) : Node;

/// <summary>숫자. Literal = 원문 글자(<c>0x10u</c> 등).</summary>
public sealed record NumberNode(long Value, string Literal) : Node;

/// <summary>안쪽 묶음 <c>이름( 인자… )</c>. Gap = 이름과 <c>(</c> 사이 공백.</summary>
public sealed record CallNode(string Name, string Gap, IReadOnlyList<Arg> Args) : Node;

/// <summary><c>a + b</c>, <c>a - b</c>(스테이션 식, <c>TAG_NUM ± n</c>).</summary>
public sealed record BinaryNode(Node Left, char Op, Node Right) : Node;
