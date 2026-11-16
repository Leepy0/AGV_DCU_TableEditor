namespace TableEditor.Engine.Text;

/// <summary>인자 글 하나를 식 나무로 푼다(화면이 입력 조건 글을 묶음으로 읽을 때). 남김없이 못 읽으면 null.</summary>
public static class Expr
{
    public static Node? Parse(string text) => Reader.ParseArg(text);
}
