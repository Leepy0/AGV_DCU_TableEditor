using TableEditor.Engine.Text;

namespace TableEditor.Engine.Model;

/// <summary>
/// 프로파일 칸의 열쇠(코어 v0.63.0): 프로파일 이름·번호, 프로파일 표의 <c>STOP_BACK_FL( 프로파일 )</c>, 수동 표의 <c>PROFILE_SET( k )</c> 묶음.
/// 걸러 보기·신호 범위·검사가 열쇠를 프로파일 이름 목록으로 푼다.
/// </summary>
public static class ProfileKey
{
    /// <summary>프로파일 표 줄의 프로파일 이름: <c>STOP_BACK_FL( PF_X )</c> 는 안쪽 <c>PF_X</c>.</summary>
    public static string Name(Arg a) => a.Node is CallNode { Name: "STOP_BACK_FL", Args: [var inner] } ? inner.Text.Trim() : a.Text;

    /// <summary>프로파일 표 줄의 프로파일 칸이 <c>STOP_BACK_FL( … )</c> 로 감싸였다.</summary>
    public static bool BackFl(Arg a) => a.Node is CallNode { Name: "STOP_BACK_FL", Args.Count: 1 };

    /// <summary>묶음 열쇠 <c>PROFILE_SET( k )</c> 의 k(아니면 null).</summary>
    public static long? SetNumber(Site s, Node n) => n is CallNode { Name: "PROFILE_SET", Args: [var k] } ? s.Num(k) : null;

    /// <summary>열쇠 글 → 프로파일 이름들. 이름·번호는 하나, 묶음은 그 묶음 줄들(<c>MAN_PROFILES</c>)의 합집합, 전체(<c>0</c>·<c>ANY_PROFILE</c>)·모르는 글은 빈 목록.</summary>
    public static IReadOnlyList<string> Names(Site s, string key)
    {
        var t = key.Trim();
        if (t is "" or "0" or "ANY_PROFILE") return [];
        var node = Expr.Parse(t);
        if (node is CallNode { Name: "STOP_BACK_FL", Args: [var inner] }) return Names(s, inner.Text);
        if (node is CallNode { Name: "PROFILE_SET" })
            return SetNumber(s, node) is long k ? Members(s, k) : [];
        if (long.TryParse(t, out var n)) t = s.Profiles.FirstOrDefault(p => p.Number == n)?.Name ?? t;
        return [t];
    }

    /// <summary>묶음 k 의 프로파일 이름들(<c>MAN_PROFILES( PROFILE_SET( k ), … )</c> 줄 모두).</summary>
    public static IReadOnlyList<string> Members(Site s, long k) =>
        s.ManualRows.Where(r => r.Name == "MAN_PROFILES" && r.Args.Count > 1 && SetNumber(s, r.Args[0].Node) == k)
            .SelectMany(r => r.Args.Skip(1)).SelectMany(a => Names(s, a.Text)).Distinct().ToList();
}
