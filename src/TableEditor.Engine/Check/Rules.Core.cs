using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.Engine.Check;

// 6.1 버전·읽기, 6.2 이름, 6.7 연결
public static partial class Checker
{
    /// <summary>V-1: 코어 버전이 문법 표가 아는 범위보다 낮다.</summary>
    static IEnumerable<Finding> V1(Facts f)
    {
        var v = f.Site.CoreVersion;
        if (f.Site.CoreFolder == null) yield break;
        if (v == null) yield return CoreFinding("V-1", Severity.Error, "VERSION", "VERSION 없음");
        else if (Compare(v, GrammarTable.Version) < 0)
            yield return CoreFinding("V-1", Severity.Error, "VERSION", $"코어 {v} · {GrammarTable.Version} 이상");
    }

    /// <summary>V-2: 코어 헤더의 공개 이름이 문법 표와 다르다.</summary>
    static IEnumerable<Finding> V2(Facts f)
    {
        if (f.Site.CoreFolder == null) yield break;
        HeaderReport r;
        try { r = HeaderCheck.Run(f.Site.CoreFolder); }
        catch (IOException) { r = null!; }
        if (r == null) yield return CoreFinding("V-2", Severity.Warning, "include/agv", "헤더 없음");
        else if (!r.Same) yield return CoreFinding("V-2", Severity.Warning, "include/agv",
            $"헤더 다름 {r.OnlyHeader.Count + r.OnlyGrammar.Count}개 · " + string.Join(", ", r.OnlyHeader.Concat(r.OnlyGrammar).Take(5)));
    }

    static readonly string[] CoreSourceExt = [".c", ".h", ".asm", ".cmd", ".ps1", ".py", ".lib"];

    /// <summary>V-3: <c>core/</c> 가 받은 그대로가 아니다(<c>MANIFEST.sha256</c>).</summary>
    static IEnumerable<Finding> V3(Facts f)
    {
        var core = f.Site.CoreFolder;
        if (core == null) yield break;
        var man = Path.Combine(core, "MANIFEST.sha256");
        if (!File.Exists(man)) { yield return CoreFinding("V-3", Severity.Error, "MANIFEST.sha256", "MANIFEST.sha256 없음"); yield break; }
        var want = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in File.ReadAllLines(man, Encoding.UTF8))
            if (l.Length > 66) want[l[66..]] = l[..64];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(core, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(core, path).Replace('\\', '/');
            if (rel == "MANIFEST.sha256" || rel.StartsWith("build/", StringComparison.Ordinal)) continue;
            seen.Add(rel);
            if (want.TryGetValue(rel, out var h))
            {
                if (NormHash(path) != h) yield return CoreFinding("V-3", Severity.Error, rel, $"core 수정됨 {rel}");
            }
            else if (CoreSourceExt.Contains(Path.GetExtension(path).ToLowerInvariant()))
                yield return CoreFinding("V-3", Severity.Error, rel, $"core 추가됨 {rel}");
        }
        foreach (var k in want.Keys.Where(k => !seen.Contains(k)).Order(StringComparer.Ordinal))
            yield return CoreFinding("V-3", Severity.Error, k, $"core 없어짐 {k}");
    }

    static readonly string[] OldAcsWords = ["CONST", "STATION0", "STATION0_PLUS1", "AGV_ID"];

    /// <summary>
    /// V-4: 사이트 코어가 그 문법이 생긴 버전보다 낮다. 낱말마다 최소 버전이 있다: v0.58.0(<c>AND</c>·<c>ANY</c>·새 상태 낱말·<c>PARAM_TIME</c>·<c>OUT_SET</c>·
    /// 숫자 <c>AGV_SPEED</c>·번호 <c>ACS</c> 등), 보고 종류 <c>IDLE</c> v0.59.0, <c>MAN_START_SW</c> v0.60.0, <c>WAIT_ACCUM</c>·<c>WHEN_STABLE</c>·<c>WHEN_OUT</c>·
    /// <c>STATE</c>·<c>DISPLAY</c> v0.61.0, 수동 표 묶음·모드 <c>ANY</c>·<c>STOP_BACK_FL</c>·태그 <c>SET_STATE</c>·<c>CROSS_IN</c>·<c>CROSS_OUT</c> v0.63.0.
    /// 줄마다 하나, 낱말을 모아 보인다. 사이트가 같은 이름을 직접 <c>#define</c> 했으면 사이트 매크로로 보고 건너뛴다. 코어 폴더가 없거나 버전을 모르면 걸지 않는다.
    /// </summary>
    static IEnumerable<Finding> V4(Facts f)
    {
        if (f.Site.CoreFolder == null || f.Site.CoreVersion == null) yield break;
        var core = f.Site.CoreVersion;
        // 보고 종류 칸: 낱말마다 최소 버전(사이트 정의와 상관없이 검사). 사이트가 보고 종류 낱말을 #define 하면 DSL_INFO_##종류 가 먼저 펼쳐져 컴파일이 깨진다
        foreach (var st in f.Site.Files.SelectMany(x => x.Statements).Where(s => s.Zone != null))
        {
            var seq = st.Name is "ACS" or "ACS_SEND" && st.Args.Count > 0 ? st.Args.Take(1) : [];
            var tag = Calls(st).Where(c => c.Call.Name is "ACS" or "ACS_SEND" && c.Call.Args.Count > 0).Select(c => (c.Call.Name, Kind: c.Call.Args[0]));
            var kinds = seq.Concat(tag.Select(t => t.Kind)).Where(a => a.Node is NameNode n && GrammarTable.ReportKinds.Contains(n.Name)).ToList();
            // gcc 실측: 종류 칸을 DSL_INFO_##종류 로 바로 붙이는 자리는 사이트 #define 이 안 펼쳐져 안 깨진다. 깨지는 자리 = 태그 ACS(모든 코어), 시퀀스 ACS·ACS_SEND(v0.58.0 이상)
            var breaks = (f.Site.NewDsl ? seq : []).Concat(tag.Where(t => t.Name == "ACS").Select(t => t.Kind))
                .Where(a => a.Node is NameNode n && GrammarTable.ReportKinds.Contains(n.Name)).ToList();
            foreach (var a in kinds)
            {
                var word = ((NameNode)a.Node).Name;
                if (GrammarTable.ReportSince.TryGetValue(word, out var since) && !GrammarTable.AtLeast(core, since))
                {
                    yield return f.Error("V-4", st, $"코어 {since} 필요 · {word}", a);
                    break;
                }
            }
            if (f.AreaOf(st) == Area.Manual && st.Name is "MAN" or "MAN_ELSE" && st.Args.Count > 1 && st.Args[1].Node is NameNode { Name: "ANY" } && !f.Site.Names.Has(GrammarTable.ModeAnyVersion))
                yield return f.Error("V-4", st, $"코어 {GrammarTable.ModeAnyVersion} 필요 · ANY", st.Args[1]);
            if (breaks.FirstOrDefault(a => f.Names.Get(((NameNode)a.Node).Name).Any(d => !d.Core)) is { } shadow)
                yield return f.Error("V-4", st, $"사이트 #define 이 보고 종류를 덮음 · {((NameNode)shadow.Node).Name}", shadow);
        }
        var found = new Dictionary<Statement, (Arg? Cell, string Ver, List<string> Names)>(ReferenceEqualityComparer.Instance);
        void Add(Statement st, Arg? cell, string name, string since)
        {
            if (f.Names.Get(name).Any(d => !d.Core)) return;
            if (!found.TryGetValue(st, out var x)) found[st] = x = (cell, since, []);
            if (!x.Names.Contains(name)) x.Names.Add(name);
            if (GrammarTable.Compare(since, x.Ver) > 0) found[st] = x with { Ver = since };
        }
        foreach (var (st, row) in f.Rows)
            if (row.Since.Length > 0 && !GrammarTable.AtLeast(core, row.Since)) Add(st, st.Args.FirstOrDefault(), row.Name, row.Since);
        foreach (var u in f.Uses)
        {
            // 수동 표 칸의 KEY( 키 ) 는 옛 코어에도 있었다(문법 표에는 새 상태 낱말 줄만 있다)
            if (f.Inners.TryGetValue(u.Node, out var e) && e.Since.Length > 0 && !GrammarTable.AtLeast(core, e.Since) && !(u.Spec.Kind == ArgKind.ManCond && e.Name == "KEY")) Add(u.St, u.Loc, e.Name, e.Since);
            if (!f.Site.NewDsl && u.Spec.Kind == ArgKind.AcsSource && (u.Node is NumberNode || u.Node is NameNode n && !OldAcsWords.Contains(n.Name) && f.Names.Value(n) != null))
                Add(u.St, u.Loc, "ACS 번호", GrammarTable.NewVersion);
        }
        foreach (var (st, x) in found)
            yield return f.Error("V-4", st, $"코어 {x.Ver} 필요 · {string.Join(", ", x.Names)}", x.Cell);
    }

    /// <summary>글 파일은 CRLF 를 LF 로 맞춰, <c>.lib</c> 은 그대로 해시(앱 tools/core_manifest.ps1 과 같다).</summary>
    static string NormHash(string path)
    {
        var b = File.ReadAllBytes(path);
        if (!path.EndsWith(".lib", StringComparison.OrdinalIgnoreCase))
            b = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(b).Replace("\r\n", "\n"));
        return Convert.ToHexStringLower(SHA256.HashData(b));
    }

    /// <summary>R-1: 표 구역 안 원문 블록.</summary>
    static IEnumerable<Finding> R1(Facts f) =>
        f.Site.Files.SelectMany(file => file.RawInZones).Select(p => f.Warn("R-1", p, "원문 블록"));

    /// <summary>R-2: 구역 표지 짝이 안 맞는다(파일 읽기 전용).</summary>
    static IEnumerable<Finding> R2(Facts f) =>
        f.Site.Files.Where(x => x.ReadOnlyReason?.StartsWith("R-2", StringComparison.Ordinal) == true)
            .Select(x => f.Error("R-2", x.Pieces[0], "표지 짝 · " + x.ReadOnlyReason![4..]));

    /// <summary>N-1: 없는 이름. 가까운 이름을 후보로.</summary>
    static IEnumerable<Finding> N1(Facts f) =>
        FromProblems(f, "N-1", p => p.Kind == "unknown").Select(x => x.Candidates.Count == 0 ? x : x with { Text = x.Text + " · 후보 " + string.Join(", ", x.Candidates) })
            .Concat(f.Problems.Where(p => p.Kind == "swap").Select(p => f.Error("N-1", p.Use.St, $"{p.Text} · 후보 {p.Candidates[0]}", p.Use.Loc, p.Candidates) with { Replace = p.Candidates[0] }));

    /// <summary>N-2: 이름 종류·꼴이 칸과 다르다(안쪽 묶음 인자 수 포함).</summary>
    static IEnumerable<Finding> N2(Facts f) => FromProblems(f, "N-2", p => p.Kind is "kind" or "form" or "count");

    /// <summary>N-3: 핀 1 활성 이름을 ON·OFF 밖에서 썼다.</summary>
    static IEnumerable<Finding> N3(Facts f) => FromProblems(f, "N-3", p => p.Kind == "high");

    /// <summary>N-4: 같은 이름을 두 번 정의. 라벨·시퀀스 표 이름은 파일 안에서만 본다.</summary>
    static IEnumerable<Finding> N4(Facts f)
    {
        foreach (var g in f.Names.All.Where(d => !d.Core && !d.Weak).GroupBy(d => d.Name))
        {
            var defs = g.ToList();
            for (int i = 1; i < defs.Count; i++)
            {
                var d = defs[i];
                var first = defs.Take(i).FirstOrDefault(e => Clash(e, d));
                if (first == null) continue;
                yield return f.Error("N-4", d.Def!, $"두 번 정의 {d.Name} · {first.File!.Name}:{first.Def!.StartLine}",
                    LabelArg(d), related: [first.Def.StartLine]);
            }
        }

        // 라벨·표 이름은 그 파일 안, 또는 헤더(.h) 이름과만 부딪힌다
        static bool Clash(NameDef a, NameDef b) =>
            a.File == b.File || !PerFile(a) && !PerFile(b) || (PerFile(a) ? b : a).File!.Name.EndsWith(".h", StringComparison.Ordinal);
        static bool PerFile(NameDef d) => d.Kind is NameKind.Label or NameKind.SeqTable or NameKind.ActTable;
        static Arg? LabelArg(NameDef d) => d.LabelCell;
    }

    static readonly Dictionary<string, string> FixedArrays = new()
    {
        ["PROFILE_TABLE_BEGIN"] = "site_profiles", ["TAG_TABLE_BEGIN"] = "site_tags", ["SEQ_MAP_BEGIN"] = "site_seqs",
        ["ACT_MAP_BEGIN"] = "site_acts", ["MANUAL_BEGIN"] = "site_manual_rows",
    };

    [GeneratedRegex(@"^PF_[A-Z0-9_]+$")]
    private static partial Regex ProfileNameRx();

    /// <summary>N-7: 앞자리 0 이 붙은 숫자(<c>030</c>). C 는 8진으로 읽는다: 값이 10진과 달라 경고, 8·9 가 들면 컴파일 실패라 오류. 표 문장·<c>#define</c>·열거자 모두.</summary>
    static IEnumerable<Finding> N7(Facts f)
    {
        static IEnumerable<NumberNode> Nums(Node n) => n switch
        {
            NumberNode x => [x],
            CallNode c => c.Args.SelectMany(a => Nums(a.Node)),
            BinaryNode b => Nums(b.Left).Concat(Nums(b.Right)),
            _ => [],
        };
        foreach (var file in f.Site.Files)
            foreach (var st in file.Statements) // 매크로 문장·#define 값·열거자 값
                foreach (var a in st.Args)
                    foreach (var n in Nums(a.Node).Where(n => CNum.LeadingZero(n.Literal)))
                        yield return CNum.Value(n.Literal) is long v
                            ? f.Warn("N-7", st, $"앞자리 0 · {n.Literal} = 8진 {v}", a)
                            : f.Error("N-7", st, $"앞자리 0 · {n.Literal} 8진 아님", a);
    }

    /// <summary>N-5: 이름 꼴이 틀리거나 코어 이름과 겹친다. 고정 배열 이름.</summary>
    static IEnumerable<Finding> N5(Facts f)
    {
        foreach (var d in f.Names.All.Where(d => !d.Core && !d.Weak && d.Def != null))
        {
            bool macro = d.Def is Statement { Shape: StatementShape.Define };
            var core = macro ? NameDict.CoreNamesAndWords : NameDict.CoreNames;
            Arg? cell = d.LabelCell;
            if (core.Contains(d.Name)) yield return f.Error("N-5", d.Def!, $"코어 이름과 겹침 {d.Name}", cell);
            else if (d.Kind == NameKind.Profile && !ProfileNameRx().IsMatch(d.Name)) yield return f.Error("N-5", d.Def!, $"이름 형식 {d.Name} · PF_ 대문자");
        }
        foreach (var z in f.Site.Zones)
            if (FixedArrays.TryGetValue(z.Open, out var want) && z.Name != want)
                yield return f.Error("N-5", z.Begin, $"배열 이름 {z.Name} · {want}", z.Begin.Args.FirstOrDefault());
        if (f.Site.NewDsl)
            foreach (var x in N5New(f)) yield return x;
    }

    [GeneratedRegex(@"/\*.*?\*/|//[^\n]*", RegexOptions.Singleline)]
    private static partial Regex CCommentRx();

    [GeneratedRegex(@"[A-Za-z_]\w*|\d+|\S")]
    private static partial Regex TokenRx();

    /// <summary>코어 <c>include/agv/*.h</c> 전부(주석 지움). 코어 폴더가 없으면 빈 글.</summary>
    static string CoreHeaderText(string? core)
    {
        var dir = core == null ? null : Path.Combine(core, "include", "agv");
        if (dir == null || !Directory.Exists(dir)) return "";
        try { return string.Join("\n", Directory.EnumerateFiles(dir, "*.h").Order(StringComparer.Ordinal).Select(File.ReadAllText)); }
        catch (IOException) { return ""; }
    }

    /// <summary>글 안의 <c>#define 이름 …</c> 마다 토큰열(주석·공백 무시, 함수형은 매개변수 목록 포함, 함수형·값형을 구별).</summary>
    static List<string> DefineTokens(string text, string name)
    {
        var rx = new Regex(@"^[ 	]*#[ 	]*define[ 	]+" + Regex.Escape(name) + @"(?![A-Za-z0-9_])(\()?([^\n]*)", RegexOptions.Multiline);
        var list = new List<string>();
        foreach (Match m in rx.Matches(CCommentRx().Replace(text, " ")))
            list.Add((m.Groups[1].Success ? "(" : "=") + string.Join(" ", TokenRx().Matches(m.Groups[2].Value).Select(t => t.Value)));
        return list;
    }

    /// <summary>v0.58.0 이후 새로 생긴 공개 이름(상태 낱말·AND·ANY·OUT_SET·NO_ACTION·STATE·WAIT_ACCUM·MAN_PROFILES …).</summary>
    static readonly IReadOnlySet<string> NewNames = GrammarTable.Names.Except(GrammarTable.OldNames).ToHashSet(StringComparer.Ordinal);

    /// <summary>사이트가 <c>#define</c> 한 이름(그 이름이 쓰인 곳은 매크로로 펼쳐진다). 원문 블록 안 정의(Weak)도 같다.</summary>
    static bool SiteDefine(Facts f, string name) => f.Names.Get(name).Any(d => !d.Core && (d.Weak || d.Def is Statement { Shape: StatementShape.Define }));

    /// <summary>
    /// N-5(새 코어): 새 낱말과 같은 이름의 <c>#define</c> 은 경고. 그 이름이 <c>AND( … )</c> 안에서 쓰였거나,
    /// <c>PARAM</c>·<c>PARAM_MIN</c>·<c>PARAM_MAX</c> 의 파라미터 이름·<c>KEY</c> 의 키 이름이 사이트 매크로면 오류(코어 헤더가 낱말을 이어 붙이기 전에 펼쳐 컴파일이 깨진다).
    /// </summary>
    static IEnumerable<Finding> N5New(Facts f)
    {
        var coreDefs = new Lazy<string>(() => CoreHeaderText(f.Site.CoreFolder));
        foreach (var d in f.Names.All.Where(d => !d.Core && d.Def != null && NewNames.Contains(d.Name) && (d.Weak || d.Def is Statement { Shape: StatementShape.Define })))
        {
            var mine = DefineTokens(d.Def!.Text, d.Name);
            if (mine.Count == 0) continue;
            var core = DefineTokens(coreDefs.Value, d.Name);
            if (!mine.Any(m => core.Contains(m))) // 코어와 토큰열이 같으면 겹쳐 정의해도 컴파일러가 아무 말 안 한다
                yield return f.Warn("N-5", d.Def!, $"코어와 다른 정의 {d.Name} · {GrammarTable.SinceOf(d.Name)} 부터 코어 낱말");
        }
        static IEnumerable<(Arg Loc, Node Node)> Inside(Arg loc) => loc.Node is CallNode c ? c.Args.SelectMany(Inside).Prepend((loc, loc.Node)) : [(loc, loc.Node)];
        foreach (var u in f.Uses.Where(u => u.Node is CallNode { Name: "AND" }))
            foreach (var (loc, n) in ((CallNode)u.Node).Args.SelectMany(Inside))
            {
                if (NameOf(n) is { Length: > 0 } name && NewNames.Contains(name) && SiteDefine(f, name))
                    yield return f.Error("N-5", u.St, $"AND 안 {name} · 사이트 #define 이 낱말을 덮는다", loc);
                if (n is CallNode { Name: "PARAM" or "PARAM_MIN" or "PARAM_MAX" or "KEY", Args: [{ Node: NameNode { Name: var arg } } first, ..] } c && SiteDefine(f, arg))
                    yield return f.Error("N-5", u.St, $"AND 안 {c.Name}( {arg} ) · 사이트 #define 이 이름을 덮는다", first);
            }
    }

    /// <summary>C-2: 처음으로 ACT 를 쓴다(<c>act_table.c</c> 가 빈 자리 꼴).</summary>
    static IEnumerable<Finding> C2(Facts f)
    {
        if (f.Site.ZonesOf("ACT_MAP_BEGIN").Any()) yield break;
        var act = f.Site.File("act_table.c");
        if (act == null || !act.Pieces.Any(p => p.Kind == PieceKind.Raw && p.Text.Contains("site_acts[1]", StringComparison.Ordinal))) yield break;
        var first = UsesOf(f, "ACT_START", "WAIT_ACT").FirstOrDefault();
        if (first.St != null) yield return f.Error("C-2", first.St, "ACT 처음 · act_table.c 손 작업", first.Loc);
    }

    /// <summary>C-3: 처음으로 CALL 을 쓴다(함수 0개, <c>site.c</c> 손 작업).</summary>
    static IEnumerable<Finding> C3(Facts f)
    {
        if (CallCount(f) != 0) yield break;
        var first = UsesOf(f, "CALL").FirstOrDefault();
        if (first.St != null) yield return f.Error("C-3", first.St, "CALL 처음 · site.c 손 작업", first.Loc);
    }
}
