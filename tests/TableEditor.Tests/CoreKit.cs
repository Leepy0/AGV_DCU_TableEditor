using System.Diagnostics;
using System.Text;
using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

/// <summary>
/// 코어 v0.60.0 이후 시험이 같이 쓰는 도구: 지어낸 core 폴더(VERSION 만), 한 표짜리 사이트, 줄 → 검사 결과,
/// 그리고 환경 변수 <c>DCU_CORE_ROOT</c>(코어 v0.63.0 폴더: <c>include/agv</c> 가 든 곳)와 gcc 가 있으면 같은 글을 코어 헤더로 컴파일해 본다.
/// 이름은 모두 지어낸 것.
/// </summary>
static class CoreKit
{
    static readonly UTF8Encoding Utf8 = new(false);

    public static string Core(string name, string version)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tedit_corekit_tests", name);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "VERSION"), version + " 0000000 cl2000 v0\n", Utf8);
        return dir;
    }

    /// <summary>입출력 별칭·프로파일 번호(코어 헤더로도 컴파일되는 글).</summary>
    public const string Io =
        "#define SEN_1 IN_EXT_1\n#define SEN_2 IN_EXT_2\n#define SEN_3 IN_EXT_3\n#define SEN_4 IN_EXT_4\n#define SEN_H IN_ACTIVE_HIGH( IN_EXT_6 )\n"
        + "#define OUT_A OUT_EXT_1\n#define OUT_B OUT_EXT_2\n#define OUT_C OUT_EXT_3\n";

    public const string SiteH = "enum {\n    PF_A = 1,\n    PF_B,\n    PF_C\n};\n";

    public static string SeqText(params string[] lines) =>
        "SEQ_LABELS( A, B )\nSEQ_BEGIN( n )\n" + string.Concat(lines.Select(l => "    " + l + "\n")) + "    L( A )\n    L( B )\n    END_STOP\nSEQ_END\n";

    public static string TagText(params string[] lines) =>
        "TAG_TABLE_BEGIN( site_tags )\n" + string.Concat(lines.Select(l => "    " + l + "\n")) + "TAG_TABLE_END\n";

    public static string ManText(params string[] lines) =>
        "MANUAL_BEGIN( site_manual_rows )\n" + string.Concat(lines.Select(l => "    " + l + "\n")) + "MANUAL_END\n";

    public static string ProfText(params string[] lines) =>
        "PROFILE_TABLE_BEGIN( site_profiles )\n" + string.Concat(lines.Select(l => "    " + l + "\n")) + "PROFILE_TABLE_END\n";

    static SourceFile F(string name, string text) => SourceFile.Parse(name, Utf8.GetBytes(text));

    public static Site Mk(string? core, string? seq = null, string? tag = null, string? man = null, string? prof = null, string? io = null)
    {
        var files = new List<SourceFile> { F("io_map.h", io ?? Io), F("site.h", SiteH) };
        if (seq != null) files.Add(F("seq_table.c", seq));
        if (tag != null) files.Add(F("tag_table.c", tag));
        if (man != null) files.Add(F("manual_table.c", man));
        if (prof != null) files.Add(F("profiles.c", prof));
        return Site.FromFiles(files, "", core);
    }

    public static Site Seq(string? core, params string[] lines) => Mk(core, seq: SeqText(lines));
    public static Site Tag(string? core, params string[] lines) => Mk(core, tag: TagText(lines));
    public static Site Man(string? core, params string[] lines) => Mk(core, man: ManText(lines));
    public static Site Prof(string? core, params string[] lines) => Mk(core, prof: ProfText(lines));

    public static List<Finding> Errors(Site s) => Checker.Run(s).Where(f => f.Severity == Severity.Error).ToList();

    public static string Dump(IEnumerable<Finding> fs) => string.Join(" | ", fs.Take(4).Select(f => $"{f.Rule} {f.Text}"));

    // ---- gcc ----

    public static string? CoreRoot
    {
        get
        {
            var r = Environment.GetEnvironmentVariable("DCU_CORE_ROOT");
            return r != null && Directory.Exists(Path.Combine(r, "include", "agv")) ? r : null;
        }
    }

    static string? GccPath()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            try { if (File.Exists(Path.Combine(dir, "gcc.exe"))) return Path.Combine(dir, "gcc.exe"); } catch (ArgumentException) { }
        return null;
    }

    public static bool GccReady => CoreRoot != null && GccPath() != null;

    /// <summary>코어 헤더로 컴파일(-fsyntax-only)이 되는가. header = <c>agv/seq_dsl.h</c> 또는 <c>agv/tag_dsl.h</c>.</summary>
    public static bool GccCompiles(string header, string body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tedit_corekit_gcc");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "t" + Environment.CurrentManagedThreadId + ".c");
        File.WriteAllText(file, $"#include \"{header}\"\n{Io}{SiteH}{body}", Utf8);
        var psi = new ProcessStartInfo(GccPath()!, $"-std=gnu99 -DAGV_HOST_BUILD -I\"{Path.Combine(CoreRoot!, "include")}\" -fsyntax-only \"{file}\"")
        { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    /// <summary>사이트 파일(fixture 의 표 파일)을 코어 헤더로 컴파일(-fsyntax-only)이 되는가. 표를 펼쳐 컴파일한다(<c>AGV_*_EXPANDED</c> 1).</summary>
    public static bool GccFile(string path, string siteFolder)
    {
        var psi = new ProcessStartInfo(GccPath()!, $"-std=gnu99 -DAGV_HOST_BUILD -DAGV_SEQ_EXPANDED=1 -DAGV_TAG_EXPANDED=1 -DAGV_ACT_EXPANDED=1 -I\"{Path.Combine(CoreRoot!, "include")}\" -I\"{siteFolder}\" -fsyntax-only \"{path}\"")
        { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    /// <summary>
    /// 코어 컴파일 오류 사례 하나: 편집기 검사가 expect 규칙의 오류로 잡고, (gcc 가 있으면) 코어가 정말 컴파일 오류로 막는지도 본다.
    /// expect 가 빈 글자면 편집기 검사에서 아무 오류나. coreRejects = false 면 코어는 컴파일하지만 뜻이 없는 줄(편집기만 오류로 막는다).
    /// </summary>
    public static void Neg(Action<bool, string> check, string label, string core, string kind, string[] lines, string expect, ref int gcc, bool coreRejects = true)
    {
        var (site, header, text) = kind switch
        {
            "seq" => (Seq(core, lines), "agv/seq_dsl.h", SeqText(lines)),
            "tag" => (Tag(core, lines), "agv/tag_dsl.h", TagText(lines)),
            "man" => (Man(core, lines), "agv/seq_dsl.h", ManText(lines)),
            _ => (Prof(core, lines), "agv/seq_dsl.h", ProfText(lines)),
        };
        var errs = Errors(site);
        check(errs.Any(f => expect.Length == 0 || f.Rule == expect), $"{label}: 편집기 오류 {(expect.Length == 0 ? "" : expect)} 안 걸림 → " + Dump(Checker.Run(site)));
        if (GccReady)
        {
            gcc++;
            check(GccCompiles(header, text) != coreRejects, $"{label}: 코어 헤더 컴파일 결과가 {(coreRejects ? "오류여야 한다" : "컴파일은 되어야 한다(뜻 없는 줄)")}");
        }
    }

    /// <summary>코어가 컴파일하는 줄(편집기가 쓰는 새 줄)이 편집기 검사에서 오류 0, gcc 로도 컴파일되는가.</summary>
    public static void Ok(Action<bool, string> check, string label, string core, string kind, string[] lines, ref int gcc)
    {
        var (site, header, text) = kind switch
        {
            "seq" => (Seq(core, lines), "agv/seq_dsl.h", SeqText(lines)),
            "tag" => (Tag(core, lines), "agv/tag_dsl.h", TagText(lines)),
            "man" => (Man(core, lines), "agv/seq_dsl.h", ManText(lines)),
            _ => (Prof(core, lines), "agv/seq_dsl.h", ProfText(lines)),
        };
        var errs = Errors(site).Where(f => f.Rule is not ("V-2" or "V-3")).ToList();
        check(errs.Count == 0, $"{label}: 오류 0 아님 → " + Dump(errs));
        check(site.Files.All(f => !f.RawInZones.Any()), $"{label}: 원문 블록 없음");
        if (GccReady)
        {
            gcc++;
            check(GccCompiles(header, text), $"{label}: 코어 헤더로 컴파일 안 됨");
        }
    }
}
