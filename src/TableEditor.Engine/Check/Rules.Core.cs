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
        FromProblems(f, "N-1", p => p.Kind == "unknown").Select(x => x.Candidates.Count == 0 ? x : x with { Text = x.Text + " · 후보 " + string.Join(", ", x.Candidates) });

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
        static Arg? LabelArg(NameDef d) => d.Kind == NameKind.Label && d.Def is Statement s && d.Value is long i ? s.Args[(int)i - 1] : null;
    }

    static readonly Dictionary<string, string> FixedArrays = new()
    {
        ["PROFILE_TABLE_BEGIN"] = "site_profiles", ["TAG_TABLE_BEGIN"] = "site_tags", ["SEQ_MAP_BEGIN"] = "site_seqs",
        ["ACT_MAP_BEGIN"] = "site_acts", ["MANUAL_BEGIN"] = "site_manual_rows",
    };

    [GeneratedRegex(@"^PF_[A-Z0-9_]+$")]
    private static partial Regex ProfileNameRx();

    /// <summary>N-5: 이름 꼴이 틀리거나 코어 이름과 겹친다. 고정 배열 이름.</summary>
    static IEnumerable<Finding> N5(Facts f)
    {
        foreach (var d in f.Names.All.Where(d => !d.Core && !d.Weak && d.Def != null))
        {
            bool macro = d.Def is Statement { Shape: StatementShape.Define };
            var core = macro ? NameDict.CoreNamesAndWords : NameDict.CoreNames;
            Arg? cell = d.Kind == NameKind.Label && d.Def is Statement s && d.Value is long i ? s.Args[(int)i - 1] : null;
            if (core.Contains(d.Name)) yield return f.Error("N-5", d.Def!, $"코어 이름과 겹침 {d.Name}", cell);
            else if (d.Kind == NameKind.Profile && !ProfileNameRx().IsMatch(d.Name)) yield return f.Error("N-5", d.Def!, $"이름 꼴 {d.Name} · PF_ 대문자");
        }
        foreach (var z in f.Site.Zones)
            if (FixedArrays.TryGetValue(z.Open, out var want) && z.Name != want)
                yield return f.Error("N-5", z.Begin, $"배열 이름 {z.Name} · {want}", z.Begin.Args.FirstOrDefault());
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
