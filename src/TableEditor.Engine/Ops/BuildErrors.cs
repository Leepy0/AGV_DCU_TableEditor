using System.Text.RegularExpressions;

namespace TableEditor.Engine.Ops;

/// <summary>
/// 빌드 출력 한 건. Source: cl2000, link, build, check, gcc, tag_pack_gen, seq_pack_gen.
/// File 은 사이트 폴더 기준 이름(-Field 사본은 원본 이름). Number 는 cl2000 오류 번호, tag_pack_gen 은 규칙 번호(0부터).
/// </summary>
public sealed record BuildError(string Source, string? File, int? Line, int? Number, string Text, bool IsWarning = false);

/// <summary>사양 7.3절 표의 꼴을 정규식으로 해석한다. 모르는 줄은 건너뛴다.</summary>
public static class BuildErrors
{
    static readonly Regex Cl2000 = new(@"^""(?<f>.+)"", line (?<l>\d+): (?<k>error|warning) #(?<n>\d+)(?:-D)?: (?<t>.*)$");
    static readonly Regex LinkAt = new(@"^""(?<f>.+)"", line (?<l>\d+): error: (?<t>.*)$");
    static readonly Regex LinkErr = new(@"^error: (?<t>.*)$");
    static readonly Regex NotBuilt = new(@"^"".+"" not built$");
    static readonly Regex BuildFail = new(@"^(?:c28: )?(?<t>COMPILE FAILED: \d+|LINK FAILED)\s*$");
    static readonly Regex Gcc = new(@"^(?<f>.+?):(?<l>\d+):(?<c>\d+): (?<k>error|warning|note): (?<t>.*)$");
    static readonly Regex PackRuleTag = new(@"^규칙 (?<n>\d+)\(태그 (?<g>\d+)\): (?<t>.*)$");
    static readonly Regex PackRule = new(@"^규칙 (?<n>\d+)(?: 개)?: (?<t>.*)$");
    static readonly Regex SeqPack = new(@"^seq_pack: (?<t>.*)$");
    static readonly Regex CoreHead = new(@"^(?<t>core 수정됨 \d+개.*)$");
    static readonly Regex CoreItem = new(@"^\s{2}(?<k>수정됨|없어짐|추가됨) (?<f>.+)$");
    static readonly Regex NoCore = new(@"^(?<t>core/ 가 없다.*)$");
    static readonly Regex Stale = new(@"^(?<f>\S+_packed\.c): (?<t>(?:바뀜|없어짐|원본 해시 없음).*)$");
    static readonly Regex StaleHint = new(@"^(?<t>압축 표가 낡았다.*)$");
    static readonly Regex FieldCopy = new(@"[\\/]site_field[\\/][^\\/]+[\\/](?<r>.+)$", RegexOptions.IgnoreCase);
    static readonly Regex SiteHDefine = new(@"^\s*#\s*define\s+SITE_H\b");

    /// <param name="siteDir">원본 사이트 폴더(있으면 경로를 이 폴더 기준으로 줄이고 site.h 한 줄 밀림을 되돌린다).</param>
    public static List<BuildError> Parse(IEnumerable<string> lines, string? siteDir = null)
    {
        var res = new List<BuildError>();
        BuildError? pending = null; // 코어 헤더 안에서 난 gcc 오류: 뒤따르는 note 의 사이트 파일 줄로 옮긴다
        int? siteHLine = null;
        bool siteHRead = false;

        void Flush() { if (pending != null) { res.Add(pending); pending = null; } }

        foreach (var raw in lines)
        {
            var s = raw.TrimEnd('\r', '\n');
            Match m;
            if ((m = Cl2000.Match(s)).Success)
            {
                Flush();
                var (f, l) = Map(m.Groups["f"].Value, int.Parse(m.Groups["l"].Value));
                res.Add(new BuildError("cl2000", f, l, int.Parse(m.Groups["n"].Value), m.Groups["t"].Value, m.Groups["k"].Value == "warning"));
            }
            else if ((m = LinkAt.Match(s)).Success)
            {
                Flush();
                res.Add(new BuildError("link", m.Groups["f"].Value, int.Parse(m.Groups["l"].Value), null, m.Groups["t"].Value));
            }
            else if ((m = LinkErr.Match(s)).Success || NotBuilt.IsMatch(s))
            {
                Flush();
                res.Add(new BuildError("link", null, null, null, m.Success ? m.Groups["t"].Value : s.Trim()));
            }
            else if ((m = BuildFail.Match(s)).Success) { Flush(); res.Add(new BuildError("build", null, null, null, m.Groups["t"].Value)); }
            else if ((m = Gcc.Match(s)).Success)
            {
                var kind = m.Groups["k"].Value;
                var (f, l) = Map(m.Groups["f"].Value, int.Parse(m.Groups["l"].Value));
                bool core = IsCoreHeader(m.Groups["f"].Value);
                if (kind == "note")
                {
                    if (pending != null && !core)
                    {
                        res.Add(pending with { File = f, Line = l });
                        pending = null;
                    }
                    continue;
                }
                Flush();
                var e = new BuildError("gcc", f, l, null, m.Groups["t"].Value, kind == "warning");
                if (kind == "error" && core) pending = e; else res.Add(e);
            }
            else if ((m = PackRuleTag.Match(s)).Success)
            {
                Flush();
                res.Add(new BuildError("tag_pack_gen", null, null, int.Parse(m.Groups["n"].Value), $"태그 {m.Groups["g"].Value}: {m.Groups["t"].Value}"));
            }
            else if ((m = PackRule.Match(s)).Success)
            {
                Flush();
                res.Add(new BuildError("tag_pack_gen", null, null, int.Parse(m.Groups["n"].Value), m.Groups["t"].Value));
            }
            else if ((m = SeqPack.Match(s)).Success) { Flush(); res.Add(new BuildError("seq_pack_gen", null, null, null, m.Groups["t"].Value)); }
            else if ((m = CoreHead.Match(s)).Success || (m = NoCore.Match(s)).Success || (m = StaleHint.Match(s)).Success)
            { Flush(); res.Add(new BuildError("check", null, null, null, m.Groups["t"].Value)); }
            else if ((m = CoreItem.Match(s)).Success) { Flush(); res.Add(new BuildError("check", m.Groups["f"].Value.Trim(), null, null, m.Groups["k"].Value)); }
            else if ((m = Stale.Match(s)).Success) { Flush(); res.Add(new BuildError("check", m.Groups["f"].Value, null, null, m.Groups["t"].Value)); }
        }
        Flush();
        return res;

        static bool IsCoreHeader(string path) => path.Replace('\\', '/').Contains("core/include/", StringComparison.OrdinalIgnoreCase);

        (string File, int Line) Map(string path, int line)
        {
            var fc = FieldCopy.Match(path);
            if (fc.Success)
            {
                var rel = fc.Groups["r"].Value.Replace('\\', '/');
                // -Field 사본의 site.h 는 '#define SITE_H' 다음 줄에 한 줄이 더 들어 있다
                if (rel == "site.h" && siteDir != null)
                {
                    if (!siteHRead) { siteHRead = true; siteHLine = FindSiteHDefine(siteDir); }
                    if (siteHLine is { } k) line = line > k + 1 ? line - 1 : line == k + 1 ? k : line;
                }
                return (rel, line);
            }
            if (siteDir != null)
            {
                try
                {
                    var root = Path.GetFullPath(siteDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                    var full = Path.GetFullPath(path, root);
                    if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return (full[root.Length..].Replace('\\', '/'), line);
                }
                catch (ArgumentException) { }
            }
            return (path, line);
        }
    }

    static int? FindSiteHDefine(string siteDir)
    {
        try
        {
            var all = File.ReadAllLines(Path.Combine(siteDir, "site.h"));
            for (int i = 0; i < all.Length; i++) if (SiteHDefine.IsMatch(all[i])) return i + 1;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}
