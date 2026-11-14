using TableEditor.Engine.Check;
using TableEditor.Engine.Model;

/// <summary><c>tedit check &lt;사이트 폴더&gt; [--core &lt;core 폴더&gt;]</c>: 줄마다 <c>등급 규칙 파일:줄 글</c>. 종료 코드 0 = 오류 없음, 1 = 오류, 2 = 인자.</summary>
static class CheckCommand
{
    public static int Run(string[] args)
    {
        string? site = null, core = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--core" && i + 1 < args.Length) core = args[++i];
            else if (site == null) site = args[i];
            else site = "";
        }
        if (string.IsNullOrEmpty(site) || !Directory.Exists(site) || core != null && !Directory.Exists(core))
        {
            Console.Error.WriteLine("tedit check <사이트 폴더> [--core <core 폴더>]");
            return 2;
        }
        var findings = Checker.Run(Site.Open(site, core));
        foreach (var f in findings) Console.WriteLine(f);
        int errors = findings.Count(f => f.Severity == Severity.Error);
        Console.WriteLine($"오류 {errors}, 경고 {findings.Count - errors}");
        return errors == 0 ? 0 : 1;
    }
}
