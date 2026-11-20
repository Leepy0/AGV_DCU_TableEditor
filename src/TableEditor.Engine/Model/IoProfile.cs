using System.Text.RegularExpressions;
using TableEditor.Engine.Text;

namespace TableEditor.Engine.Model;

/// <summary>
/// IO 맵 신호가 쓰는 프로파일: <c>#define</c> 줄 끝 주석 <c>/* pf: PF_A PF_B */</c>(기존 주석 뒤에 붙는다). 주석이 없으면 모든 프로파일.
/// 컴파일 결과에는 영향 없다(주석).
/// </summary>
public static partial class IoProfile
{
    [GeneratedRegex(@"/\*[ \t]*pf:([^*]*)\*/")]
    private static partial Regex PfRx();

    /// <summary>줄의 프로파일 이름들. 주석이 없거나 이름이 없으면 null(모두).</summary>
    public static IReadOnlyList<string>? Of(Statement st)
    {
        var m = PfRx().Match(st.Tail);
        if (!m.Success) return null;
        var names = m.Groups[1].Value.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
        return names.Length == 0 ? null : names;
    }

    /// <summary>꼬리 주석 글에서 pf 주석을 뺀다(화면의 주석 칸).</summary>
    public static string StripComment(string comment) => PfRx().Replace(comment, "").Trim();

    /// <summary>
    /// 프로파일 지정을 쓴다. 비면 그 주석만 지운다(앞 공백 포함: 지정하기 전 줄 바이트로 돌아간다).
    /// 없으면 꼬리 끝(기존 주석이 있으면 한 칸 띄고, 없으면 공백 4)에 붙인다. 줄 주석(//)이 든 줄은 막는다.
    /// </summary>
    public static EditResult Set(Site site, Statement st, IReadOnlyList<string> profiles)
    {
        if (st.Shape != StatementShape.Define) return EditResult.Fail("정의 줄 아님");
        var names = profiles.Distinct().ToList();
        if (names.FirstOrDefault(n => !Form.Ident.IsMatch(n)) is { } bad) return EditResult.Fail($"이름 틀림 · {bad}");
        var tail = st.Tail;
        var m = PfRx().Match(tail);
        var comment = names.Count > 0 ? $"/* pf: {string.Join(' ', names)} */" : null;
        string newTail;
        if (m.Success)
        {
            if (comment == null)
            {
                int from = m.Index;
                while (from > 0 && tail[from - 1] is ' ' or '\t') from--;
                newTail = tail[..from] + tail[(m.Index + m.Length)..];
            }
            else newTail = tail[..m.Index] + comment + tail[(m.Index + m.Length)..];
        }
        else if (comment != null)
        {
            if (tail.Contains("//", StringComparison.Ordinal)) return EditResult.Fail("줄 주석 있음");
            var core = tail.TrimEnd(' ', '\t');
            newTail = core + (core.Length > 0 ? " " : "    ") + comment + tail[core.Length..];
        }
        else newTail = tail;
        int ts = Form.TailStart(st);
        var sp = new Splice(site);
        sp.Replace(st, st.Text[..ts] + newTail + st.Text[(ts + tail.Length)..]);
        return sp.Commit();
    }
}
