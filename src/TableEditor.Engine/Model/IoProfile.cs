using System.Text.RegularExpressions;
using TableEditor.Engine.Text;

namespace TableEditor.Engine.Model;

/// <summary>
/// IO 맵 신호가 쓰는 프로파일: <c>#define</c> 줄 끝 주석 <c>/* pf: PF_A PF_B */</c>(기존 주석 뒤에 붙는다). 주석이 없으면 모든 프로파일.
/// 컴파일 결과에는 영향 없다(주석). pf 주석이 둘 이상이면 읽기는 모두의 합이고, 쓰기는 하나로 합친다.
/// 이름이 아닌 글자가 든 pf 주석(<c>/* pf: A*B */</c>)은 읽지도 건드리지도 않는다(지정을 막는다).
/// </summary>
public static partial class IoProfile
{
    [GeneratedRegex(@"/\*[ \t]*pf:(.*?)\*/", RegexOptions.Singleline)]
    private static partial Regex PfRx();

    static string[] Tokens(string body) => body.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);

    static bool Valid(Match m) => Tokens(m.Groups[1].Value).All(t => Form.Ident.IsMatch(t));

    /// <summary>줄의 프로파일 이름들(모든 pf 주석의 합). 주석이 없거나 이름이 없으면 null(모두).</summary>
    public static IReadOnlyList<string>? Of(Statement st)
    {
        var names = PfRx().Matches(st.Tail).Where(Valid).SelectMany(m => Tokens(m.Groups[1].Value)).Distinct().ToList();
        return names.Count == 0 ? null : names;
    }

    /// <summary>꼬리 주석 글에서 pf 주석을 뺀다(화면의 주석 칸).</summary>
    public static string StripComment(string comment) => PfRx().Replace(comment, "").Trim();

    /// <summary>pf 주석 안 이름 글자 자리(문장 Text 기준): 이름 바꾸기가 같이 고친다.</summary>
    internal static IEnumerable<(int Offset, string Name)> Spans(Statement st)
    {
        int ts = Form.TailStart(st);
        foreach (var m in PfRx().Matches(st.Tail).Where(Valid))
            foreach (Match t in Regex.Matches(m.Groups[1].Value, @"[^\s,]+")) yield return (ts + m.Groups[1].Index + t.Index, t.Value);
    }

    /// <summary>
    /// 프로파일 지정을 쓴다. 비면 pf 주석을 모두 지운다(앞 공백 포함: 지정하기 전 줄 바이트로 돌아간다).
    /// 없으면 꼬리 끝(기존 주석이 있으면 한 칸 띄고, 없으면 공백 4)에 붙인다. 있으면 첫 pf 주석 자리에 쓰고 나머지 pf 주석은 지운다.
    /// 줄 주석(//)이 든 줄, 이름 아닌 글자가 든 pf 주석이 있는 줄은 막는다.
    /// </summary>
    public static EditResult Set(Site site, Statement st, IReadOnlyList<string> profiles)
    {
        if (st.Shape != StatementShape.Define) return EditResult.Fail("정의 줄 아님");
        var names = profiles.Distinct().ToList();
        if (names.FirstOrDefault(n => !Form.Ident.IsMatch(n)) is { } bad) return EditResult.Fail($"이름 틀림 · {bad}");
        var tail = st.Tail;
        var found = PfRx().Matches(tail).ToList();
        if (found.Any(m => !Valid(m))) return EditResult.Fail("pf 글 틀림");
        var comment = names.Count > 0 ? $"/* pf: {string.Join(' ', names)} */" : null;
        string newTail = tail;
        if (found.Count > 0)
        {
            for (int i = found.Count - 1; i >= 0; i--)
            {
                var m = found[i];
                if (i == 0 && comment != null) { newTail = newTail[..m.Index] + comment + newTail[(m.Index + m.Length)..]; continue; }
                int from = m.Index;
                while (from > 0 && newTail[from - 1] is ' ' or '\t') from--;
                newTail = newTail[..from] + newTail[(m.Index + m.Length)..];
            }
        }
        else if (comment != null)
        {
            if (tail.Contains("//", StringComparison.Ordinal)) return EditResult.Fail("줄 주석 있음");
            var core = tail.TrimEnd(' ', '\t');
            newTail = core + (core.Length > 0 ? " " : "    ") + comment + tail[core.Length..];
        }
        int ts = Form.TailStart(st);
        var sp = new Splice(site);
        sp.Replace(st, st.Text[..ts] + newTail + st.Text[(ts + tail.Length)..]);
        return sp.Commit();
    }
}
