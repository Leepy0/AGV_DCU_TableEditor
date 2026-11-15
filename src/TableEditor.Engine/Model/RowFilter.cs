namespace TableEditor.Engine.Model;

/// <summary>
/// 표 화면 거르기(태그·시퀀스가 같이 쓴다): 번호 칸 + 프로파일 드롭다운. 번호는 화면마다 뜻이 다르다(태그 번호, 호기).
/// </summary>
public static class RowFilter
{
    /// <summary>프로파일 드롭다운의 "모두"(ANY_PROFILE 줄의 프로파일 글이기도 하다).</summary>
    public const string All = "전체";

    /// <summary>번호 칸 글의 숫자. 숫자가 아니면 null.</summary>
    public static long? Number(string text) => long.TryParse(text.Trim(), out var n) ? n : null;

    /// <summary>
    /// 줄 하나가 보이는가. 번호 칸이 차 있으면 숫자이고 covers 가 참이어야 한다. 프로파일이 전체가 아니면
    /// 줄에 프로파일이 있고(없으면 원문 줄·연결 없음), 모든 프로파일 줄(any)이거나 같은 이름이어야 한다.
    /// </summary>
    public static bool Match(string text, string profile, string? rowProfile, bool any, Func<long, bool> covers) =>
        (text.Trim().Length == 0 || Number(text) is long n && covers(n))
        && (profile == All || rowProfile != null && (any || rowProfile == profile));
}
