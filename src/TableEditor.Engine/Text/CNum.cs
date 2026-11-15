using System.Globalization;

namespace TableEditor.Engine.Text;

/// <summary>C 정수 글자: 10진, <c>0x</c> 16진, 앞자리 0 = 8진(C 와 같이), 접미사 u·l, 앞 부호.</summary>
public static class CNum
{
    /// <summary>글자의 값. 8진에 8·9 가 있으면(C 컴파일 실패) null.</summary>
    public static long? Value(string literal)
    {
        var s = literal.Trim().TrimEnd('u', 'U', 'l', 'L');
        bool neg = s.StartsWith('-');
        if (s.StartsWith('-') || s.StartsWith('+')) s = s[1..];
        long v;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(s[2..], NumberStyles.HexNumber, null, out v)) return null;
        }
        else if (s.Length > 1 && s[0] == '0')
        {
            if (s.Any(c => c is < '0' or > '7')) return null;
            try { v = Convert.ToInt64(s, 8); } catch (OverflowException) { return null; }
        }
        else if (!long.TryParse(s, NumberStyles.None, null, out v)) return null;
        return neg ? -v : v;
    }

    /// <summary>앞자리 0 이 붙은 10진 꼴 글자(<c>030</c>, <c>08</c>). <c>0</c>·<c>0x…</c> 는 아니다. C 는 8진으로 읽는다.</summary>
    public static bool LeadingZero(string literal)
    {
        var s = literal.Trim().TrimStart('-', '+');
        return s.Length > 1 && s[0] == '0' && char.IsAsciiDigit(s[1]);
    }
}
