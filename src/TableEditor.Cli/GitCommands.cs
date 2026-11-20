using TableEditor.Engine.Ops;

namespace TableEditor.Cli;

/// <summary>작업 6 시험용 명령줄: 회차 내기, 코어 버전 변경·버전 목록, git.log 검사. 종료 코드 0 = 성공, 1 = 실패, 2 = 인자 오류.</summary>
static class GitCommands
{
    const string Usage =
        "tedit round <앱 루트> <사이트> --app-url <주소> [--gcc <경로>]\n" +
        "tedit core-update <앱 루트> <사이트> <vX.Y.Z> --app-url <주소> [--from <주소>] [--gcc <경로>]\n" +
        "tedit core-versions <앱 루트> [--core-url <주소>]\n" +
        "tedit git-log-check <git.log> [--core-url <주소>]";

    public static async Task<int> Run(string[] args)
    {
        var pos = new List<string>();
        string? gcc = null, appUrl = null, from = null, coreUrl = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--gcc" && i + 1 < args.Length) gcc = args[++i];
            else if (args[i] == "--app-url" && i + 1 < args.Length) appUrl = args[++i];
            else if (args[i] == "--from" && i + 1 < args.Length) from = args[++i];
            else if (args[i] == "--core-url" && i + 1 < args.Length) coreUrl = args[++i];
            else pos.Add(args[i]);
        }
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        void Print(OutputLine l) => (l.IsError ? Console.Error : Console.Out).WriteLine(l.Text);

        switch (pos.FirstOrDefault())
        {
            case "git-log-check" when pos.Count == 2 && File.Exists(pos[1]):
            {
                var lines = File.ReadAllLines(pos[1]);
                var bad = Git.CheckLog(lines, coreUrl ?? CoreUpdate.CoreUrl);
                foreach (var b in bad) Console.WriteLine("어김 " + b);
                Console.WriteLine($"명령 {lines.Count(l => !l.StartsWith('#'))}, push {lines.Count(l => l.Contains("\tpush\t"))}, 어김 {bad.Count}");
                return bad.Count == 0 ? 0 : 1;
            }
            case "core-versions" when pos.Count == 2 && Directory.Exists(pos[1]):
            {
                var v = await CoreUpdate.ListVersionsAsync(new Git(pos[1], ""), coreUrl ?? CoreUpdate.CoreUrl, cts.Token);
                if (v == null) { Console.Error.WriteLine("연결 안 됨"); return 1; }
                foreach (var x in v) Console.WriteLine(x);
                return 0;
            }
            case "round" when pos.Count == 3 && appUrl != null && Directory.Exists(pos[1]):
            {
                var r = await Round.RunAsync(new Git(pos[1], appUrl), pos[2], Round.DefaultBuilder(gcc), Print, cts.Token);
                foreach (var e in r.Build?.Errors ?? []) Console.Error.WriteLine($"오류 · {e.Source} · {e.File}:{e.Line} · {e.Text}");
                if (!r.Ok) { Console.Error.WriteLine($"회차 실패 · {r.Outcome} · {r.Reason}"); return 1; }
                Console.WriteLine($"회차 {r.Number}{(r.Unshared ? " · 미공유" : "")} · 표 {r.TableCommit}");
                Console.WriteLine(r.Line);
                return 0;
            }
            case "core-update" when pos.Count == 4 && appUrl != null && Directory.Exists(pos[1]):
            {
                var r = await CoreUpdate.RunAsync(new Git(pos[1], appUrl), pos[2], pos[3], from, Round.DefaultBuilder(gcc), Print, cts.Token);
                foreach (var f in r.Findings.Where(f => f.Severity == TableEditor.Engine.Check.Severity.Error)) Console.Error.WriteLine(f);
                if (!r.Ok) { Console.Error.WriteLine($"코어 버전 변경 실패 · {r.Reason}"); return 1; }
                Console.WriteLine(r.Changed ? $"코어 받음 · {r.CoreLine}" : $"변경 없음 · {r.CoreLine}");
                return 0;
            }
            default:
                Console.Error.WriteLine(Usage);
                return 2;
        }
    }
}
