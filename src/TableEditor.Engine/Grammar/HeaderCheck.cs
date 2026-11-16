using System.Text.RegularExpressions;

namespace TableEditor.Engine.Grammar;

/// <summary>헤더 대조 결과(사양 4.9절, V-2). Header = 헤더 공개 이름, OnlyHeader·OnlyGrammar = 한쪽에만 있는 이름.</summary>
public sealed record HeaderReport(
    int AllDefines, int Internal, int Guards,
    IReadOnlyList<string> Header, IReadOnlyList<string> OnlyHeader, IReadOnlyList<string> OnlyGrammar)
{
    public bool Same => OnlyHeader.Count == 0 && OnlyGrammar.Count == 0;
}

/// <summary>
/// 코어 <c>agv/seq_dsl.h</c>·<c>tag_dsl.h</c>(v0.58.0 부터 <c>dsl_and.h</c> 도)의 공개 <c>#define</c> 이름을 문법 표와 견준다.
/// <c>AND( … )</c> 의 상태 낱말(<c>FORWARD</c> 등)은 매크로가 아니라 <c>#define DSL_AX_낱말</c> 로 붙여 고르므로 그 이름도 공개 이름으로 센다.
/// </summary>
public static partial class HeaderCheck
{
    public static readonly IReadOnlyList<string> HeaderFiles = ["seq_dsl.h", "tag_dsl.h"];

    /// <summary>v0.58.0 에서 생긴 헤더. 옛 코어에는 없다.</summary>
    public const string AndHeader = "dsl_and.h";

    [GeneratedRegex(@"^[ \t]*#[ \t]*define[ \t]+([A-Za-z_]\w*)(.*)$", RegexOptions.Multiline)]
    private static partial Regex DefineRx();

    [GeneratedRegex(@"^[ \t]*#[ \t]*define[ \t]+DSL_AX_([A-Za-z]\w*)", RegexOptions.Multiline)]
    private static partial Regex WordRx();

    [GeneratedRegex(@"^[ \t]*#[ \t]*ifndef[ \t]+([A-Za-z_]\w*)", RegexOptions.Multiline)]
    private static partial Regex IfndefRx();

    /// <summary><paramref name="coreFolder"/> = 앱의 <c>core</c> 폴더(그 안 <c>include/agv/</c>).</summary>
    public static HeaderReport Run(string coreFolder)
    {
        var texts = HeaderFiles.Select(f => File.ReadAllText(Path.Combine(coreFolder, "include", "agv", f))).ToList();
        var and = Path.Combine(coreFolder, "include", "agv", AndHeader);
        bool newDsl = File.Exists(and);
        if (newDsl) texts.Add(File.ReadAllText(and));
        return Compare(texts, newDsl);
    }

    /// <summary>newDsl = false 면(v0.58.0 앞 코어) 그때 없던 문법 이름은 "문법 표에만" 으로 세지 않는다.</summary>
    public static HeaderReport Compare(IEnumerable<string> headerTexts, bool newDsl = true)
    {
        var all = new SortedSet<string>(StringComparer.Ordinal);
        var guards = new SortedSet<string>(StringComparer.Ordinal);
        var words = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var text in headerTexts)
        {
            foreach (Match m in WordRx().Matches(text)) words.Add(m.Groups[1].Value);
            var ifndef = IfndefRx().Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
            foreach (Match m in DefineRx().Matches(text))
            {
                var name = m.Groups[1].Value;
                all.Add(name);
                // 포함 가드: #ifndef X 와 값 없는 #define X
                if (ifndef.Contains(name) && m.Groups[2].Value.Trim().Length == 0) guards.Add(name);
            }
        }
        var internals = all.Count(n => n.StartsWith("DSL_", StringComparison.Ordinal));
        var header = all.Where(n => !n.StartsWith("DSL_", StringComparison.Ordinal) && !guards.Contains(n)).ToList();
        foreach (var w in words.Where(w => !w.StartsWith("DSL_", StringComparison.Ordinal) && !header.Contains(w))) header.Add(w);
        header.Sort(StringComparer.Ordinal);
        var grammar = GrammarTable.Names.ToHashSet(StringComparer.Ordinal);
        var expected = newDsl ? GrammarTable.Names : GrammarTable.OldNames.ToList();
        return new HeaderReport(all.Count, internals, guards.Count, header,
            header.Where(n => !grammar.Contains(n)).ToList(),
            expected.Where(n => !header.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList());
    }
}
