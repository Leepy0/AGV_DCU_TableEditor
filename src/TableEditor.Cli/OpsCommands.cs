using TableEditor.Engine.Ops;

namespace TableEditor.Cli;

/// <summary>tedit pack / build. 종료 코드 0 = 성공, 1 = 실패, 2 = 인자 오류.</summary>
static class OpsCommands
{
    const string Usage = "tedit pack <앱 루트> <사이트> [--gcc <경로>]\ntedit build <앱 루트> <사이트> [--field] [--name <이미지 이름>] [--gcc <경로>]";

    public static async Task<int> Run(string[] args)
    {
        var pos = new List<string>();
        string? gcc = null, name = null;
        bool field = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--field") field = true;
            else if (args[i] == "--gcc" && i + 1 < args.Length) gcc = args[++i];
            else if (args[i] == "--name" && i + 1 < args.Length) name = args[++i];
            else pos.Add(args[i]);
        }
        if (pos.Count != 3 || (field && pos[0] == "pack") || !Directory.Exists(pos[1]))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        var app = Path.GetFullPath(pos[1]);
        var site = pos[2];
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        void Print(OutputLine l) => (l.IsError ? Console.Error : Console.Out).WriteLine(l.Text);

        gcc ??= ToolProbe.DefaultGcc();
        var tools = pos[0] == "build"
            ? await ToolProbe.ProbeAllAsync(gcc, ct: cts.Token)
            : [await ToolProbe.GccAsync(gcc, cts.Token)];
        foreach (var t in tools)
            Console.WriteLine($"도구 {t.Name,-10} {(t.Found ? t.Version : "없음 · " + t.Detail)}{(t.Path != null && !t.Found ? "  " + t.Path : "")}");
        var must = tools.Where(t => !t.Found && t.Name is "gcc" or "cl2000").ToList();
        if (must.Count > 0)
        {
            Console.Error.WriteLine("필요한 도구가 없다: " + string.Join(", ", must.Select(t => t.Name)));
            return 1;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine("압축 태그 표");
        var pk = await SitePack.RunAsync(app, site, gcc, Print, cts.Token);
        if (!pk.Ok)
        {
            Console.Error.WriteLine($"압축 실패 · {pk.Step} · {pk.Reason}");
            return 1;
        }
        Console.WriteLine(pk.Changed ? "생성물 바뀜: " + string.Join(", ", pk.Written) : "생성물 같음");
        if (pos[0] == "pack") return 0;

        Console.WriteLine("빌드" + (field ? " (현장)" : ""));
        var b = await SiteBuild.RunAsync(app, site, name, field, Print, cts.Token);
        foreach (var e in b.Errors.Where(e => !e.IsWarning))
            Console.Error.WriteLine($"오류 · {e.Source} · {e.File}:{e.Line} · {e.Number} · {e.Text}");
        if (!b.Ok)
        {
            Console.Error.WriteLine($"빌드 실패 · {b.Reason}");
            return 1;
        }
        Console.WriteLine($"완료 {sw.Elapsed.TotalSeconds:F1}초 · {b.OutPath}");
        return 0;
    }
}
