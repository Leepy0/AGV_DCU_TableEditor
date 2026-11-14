using System.Text.RegularExpressions;

namespace TableEditor.Engine.Grammar;

/// <summary>헤더 대조 결과(사양 4.9절, V-2). Header = 헤더 공개 이름, OnlyHeader·OnlyGrammar = 한쪽에만 있는 이름.</summary>
public sealed record HeaderReport(
    int AllDefines, int Internal, int Guards,
    IReadOnlyList<string> Header, IReadOnlyList<string> OnlyHeader, IReadOnlyList<string> OnlyGrammar)
{
    public bool Same => OnlyHeader.Count == 0 && OnlyGrammar.Count == 0;
}

/// <summary>코어 <c>agv/seq_dsl.h</c>·<c>tag_dsl.h</c> 의 공개 <c>#define</c> 이름을 문법 표와 견준다.</summary>
public static partial class HeaderCheck
{
    public static readonly IReadOnlyList<string> HeaderFiles = ["seq_dsl.h", "tag_dsl.h"];

    [GeneratedRegex(@"^[ \t]*#[ \t]*define[ \t]+([A-Za-z_]\w*)(.*)$", RegexOptions.Multiline)]
    private static partial Regex DefineRx();

    [GeneratedRegex(@"^[ \t]*#[ \t]*ifndef[ \t]+([A-Za-z_]\w*)", RegexOptions.Multiline)]
    private static partial Regex IfndefRx();

    /// <summary><paramref name="coreFolder"/> = 앱의 <c>core</c> 폴더(그 안 <c>include/agv/</c>).</summary>
    public static HeaderReport Run(string coreFolder)
    {
        var texts = HeaderFiles.Select(f => File.ReadAllText(Path.Combine(coreFolder, "include", "agv", f)));
        return Compare(texts);
    }

    public static HeaderReport Compare(IEnumerable<string> headerTexts)
    {
        var all = new SortedSet<string>(StringComparer.Ordinal);
        var guards = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var text in headerTexts)
        {
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
        var grammar = GrammarTable.Names.ToHashSet(StringComparer.Ordinal);
        return new HeaderReport(all.Count, internals, guards.Count, header,
            header.Where(n => !grammar.Contains(n)).ToList(),
            GrammarTable.Names.Where(n => !header.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList());
    }
}
