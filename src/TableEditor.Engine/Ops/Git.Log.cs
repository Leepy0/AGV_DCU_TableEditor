namespace TableEditor.Engine.Ops;

/// <summary>git.log 검사(사양 11절 A-11).</summary>
public sealed partial class Git
{
    /// <summary>
    /// git.log 줄들을 검사해 어긴 것을 돌려준다(없으면 빈 목록): 강제 인자(--force…, push 의 -f·+가지), 코어 저장소로의 push,
    /// core 를 건드리는 명령이 코어 올리기 밖에서, git clean 이 코어 올리기 밖에서, reset --hard 가 보관 가지 동작 밖에서.
    /// </summary>
    public static IReadOnlyList<string> CheckLog(IEnumerable<string> lines, string coreUrl = CoreUpdate.CoreUrl)
    {
        var bad = new List<string>();
        string marker = "";
        string? pushUrl = null;
        int no = 0;
        foreach (var line in lines)
        {
            no++;
            if (line.StartsWith("# "))
            {
                var m = line[2..];
                if (m.StartsWith("push-url ")) pushUrl = m["push-url ".Length..];
                else marker = m;
                continue;
            }
            var f = line.Split('\t');
            if (f.Length < 4 || f[2] != "git") continue;
            var a = f[3..].ToList();
            // 앞머리 -C <루트>, --no-pager, -c <설정> 을 건너 하위 명령을 찾는다
            int i = 0;
            while (i < a.Count && (a[i] is "-C" or "-c" || a[i] == "--no-pager")) i += a[i] == "--no-pager" ? 1 : 2;
            if (i >= a.Count) continue;
            var cmd = a[i];
            var rest = a.Skip(i + 1).ToList();
            bool inCore = marker.StartsWith("core-update");
            void Bad(string why) => bad.Add($"{no}: {why}: {string.Join(" ", a.Skip(i))}");

            if (a.Any(x => x.StartsWith("--force"))) Bad("--force");
            if (cmd == "push")
            {
                if (rest.Any(x => x == "-f" || x.StartsWith('+') || x.Contains(":+"))) Bad("강제 push");
                var target = pushUrl ?? "";
                if (rest.Concat([target]).Any(x => x.Contains("AGV_DCU_Core", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.TrimEnd('/'), coreUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))) Bad("코어 주소로 push");
            }
            bool touchesCore = rest.Any(x => x == "core" || x.StartsWith("core/"));
            if (touchesCore && cmd is "add" or "rm" or "restore" or "clean" or "mv" or "checkout" or "reset" && !inCore) Bad("코어 올리기 밖에서 core 변경");
            if (cmd == "clean" && !inCore) Bad("git clean 자리 아님");
            if (cmd == "reset" && rest.Contains("--hard") && marker != "keep-and-reset") Bad("reset --hard 자리 아님");
        }
        return bad;
    }
}
