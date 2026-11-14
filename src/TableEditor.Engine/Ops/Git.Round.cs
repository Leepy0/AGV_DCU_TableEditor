using System.Text;
using System.Text.RegularExpressions;

namespace TableEditor.Engine.Ops;

/// <summary>회차(사양 7.5절)의 깃 단계. 인자는 여기서만 만든다.</summary>
public sealed partial class Git
{
    /// <summary>README 회차 줄: "nn차 이미지(" 로 시작.</summary>
    static readonly Regex RoundLine = new(@"^(\d+)차 이미지\(");

    /// <summary>회차 파일 이름 agv_&lt;타입&gt;_nn[_field].out|.map → (nn 글자, 번호).</summary>
    internal static (string Text, int Number)? RoundOf(string fileName, string image)
    {
        var m = Regex.Match(Path.GetFileName(fileName), "^" + Regex.Escape(image) + @"_(\d+)(_field)?\.(out|map)$");
        return m.Success ? (m.Groups[1].Value, int.Parse(m.Groups[1].Value)) : null;
    }

    internal static string Image(string site) => SiteBuild.DefaultName(site);

    /// <summary>7.5절 받을 때 1·2: 올리지 않은 내 회차 번호가 원격에 이미 있으면 다음 빈 번호로 git mv, README 줄의 번호·파일 이름만 바꿔 커밋.</summary>
    async Task<GitResult?> RenumberRoundsAsync(string site, List<string> notes, CancellationToken ct)
    {
        var rel = ReleasePath(site);
        var image = Image(site);
        var remote = await GitAsync(false, ct, "-c", "core.quotepath=off", "ls-tree", "--name-only", $"origin/{Branch}", "--", rel + "/");
        if (!remote.Ok) return null; // 원격 가지가 없다(빈 원격): 겹칠 것이 없다
        var remoteNums = remote.Out.Select(f => RoundOf(f, image)).OfType<(string, int)>().Select(x => x.Item2).ToHashSet();
        var mine = await GitAsync(false, ct, "-c", "core.quotepath=off", "diff", "--name-only", "--diff-filter=A", $"origin/{Branch}...HEAD", "--", rel);
        if (!mine.Ok) return null; // 공통 조상이 없다
        var mineRounds = mine.Out.Select(f => RoundOf(f, image)).OfType<(string Text, int Number)>().Distinct().OrderBy(x => x.Number).ToList();
        foreach (var (text, n) in mineRounds)
        {
            if (!remoteNums.Contains(n)) continue;
            var local = Directory.Exists(Path.Combine(Root, rel))
                ? Directory.GetFiles(Path.Combine(Root, rel)).Select(f => RoundOf(f, image)).OfType<(string, int)>().Select(x => x.Item2)
                : [];
            int mm = remoteNums.Concat(local).Max() + 1;
            var to = mm.ToString("D2");
            foreach (var suffix in new[] { ".out", ".map", "_field.out", "_field.map" })
            {
                var from = $"{rel}/{image}_{text}{suffix}";
                if (!File.Exists(Path.Combine(Root, from))) continue;
                var mv = await GitAsync(false, ct, "mv", "--", from, $"{rel}/{image}_{to}{suffix}");
                if (!mv.Ok) return Fail(mv, "회차 번호 올림");
            }
            var readme = Path.Combine(Root, rel, "README.md");
            if (File.Exists(readme)) RenumberReadme(readme, image, text, to);
            var why = $"회차 {text} → {to}(원격에 {text} 있음)";
            var c = await CommitPathsAsync(why, ct, rel);
            if (!c.Ok) return c;
            notes.Add(why);
            remoteNums.Add(mm);
        }
        return null;
    }

    /// <summary>"nn차 이미지(" 로 시작하는 줄만: 머리 번호와 그 줄의 회차 파일 이름을 mm 으로. 다른 줄과 줄 끝은 그대로.</summary>
    internal static void RenumberReadme(string path, string image, string from, string to)
    {
        var (lines, bom) = ReadLines(path);
        for (int i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith(from + "차 이미지(", StringComparison.Ordinal)) continue;
            var body = to + Body(lines[i])[from.Length..];
            body = body.Replace($"{image}_{from}.out", $"{image}_{to}.out").Replace($"{image}_{from}_field.out", $"{image}_{to}_field.out");
            lines[i] = body + Eol(lines[i]);
        }
        WriteLines(path, lines, bom);
    }

    /// <summary>7.5절 받을 때 3: README 회차 줄 충돌은 두 쪽 줄을 모두 남기고 번호가 큰 줄을 위에. 회차 줄 아닌 것이 충돌하면 false(손대지 않음).</summary>
    static bool TryCombineReadme(string path)
    {
        var (lines, bom) = ReadLines(path);
        var combined = CombineRoundConflict(string.Concat(lines));
        if (combined == null) return false;
        WriteLines(path, [combined], bom);
        return true;
    }

    /// <summary>
    /// 충돌 표식 덩이마다: 두 쪽 줄이 모두 회차 줄이면 합쳐 번호 큰 차례로(같은 줄은 한 번). 아니면 null.
    /// 줄마다 제 줄 끝을 그대로 둔다(표식 줄만 없어진다).
    /// </summary>
    internal static string? CombineRoundConflict(string text)
    {
        var lines = SplitKeep(text);
        var nl = FirstEol(lines);
        var output = new List<string>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith("<<<<<<<")) { output.Add(lines[i]); continue; }
            var block = new List<string>();
            int j = i + 1;
            for (; j < lines.Count && !lines[j].StartsWith(">>>>>>>"); j++)
                if (!lines[j].StartsWith("=======")) block.Add(lines[j]);
            if (j >= lines.Count) return null;
            if (block.Any(l => l.StartsWith("|||||||") || !RoundLine.IsMatch(l))) return null;
            output.AddRange(block.Select(l => Eol(l).Length > 0 ? l : l + nl).DistinctBy(Body).Select((l, k) => (l, k))
                .OrderByDescending(x => int.Parse(RoundLine.Match(x.l).Groups[1].Value)).ThenBy(x => x.k).Select(x => x.l));
            i = j;
        }
        return string.Concat(output);
    }

    /// <summary>7.5절 받을 때 4: 폴더에 회차가 둘 이상이면 가장 큰 번호만 남기고 git rm, 커밋.</summary>
    async Task<GitResult?> PruneRoundsAsync(string site, List<string> notes, CancellationToken ct)
    {
        var rel = ReleasePath(site);
        var image = Image(site);
        var files = await GitAsync(false, ct, "-c", "core.quotepath=off", "ls-files", "--", rel);
        if (!files.Ok) return Fail(files, "git ls-files");
        var rounds = files.Out.Select(f => (f, r: RoundOf(f, image))).Where(x => x.r != null).ToList();
        if (rounds.Count == 0) return null;
        int max = rounds.Max(x => x.r!.Value.Number);
        var old = rounds.Where(x => x.r!.Value.Number != max).Select(x => x.f).ToArray();
        if (old.Length == 0) return null;
        var rm = await GitAsync(false, ct, ["rm", "--quiet", "--", .. old]);
        if (!rm.Ok) return Fail(rm, "이전 회차 삭제");
        var why = $"이전 회차 삭제: {string.Join(", ", rounds.Where(x => x.r!.Value.Number != max).Select(x => x.r!.Value.Text).Distinct())}";
        var c = await CommitPathsAsync(why, ct, rel);
        if (!c.Ok) return c;
        notes.Add(why);
        return null;
    }

    // ---- 회차 내기(Round 가 부른다) ----

    /// <summary>회차 줄의 "표 커밋"과 미공유: HEAD 짧은 해시, origin/master..HEAD 가 1 이상이거나 모르면 미공유.</summary>
    internal async Task<(string? Head, bool Unshared)> TableCommitAsync(CancellationToken ct)
    {
        var head = await HeadShortAsync(ct);
        var pending = await PendingAsync(ct);
        return (head, pending is not 0);
    }

    /// <summary>회차 폴더에서 깃이 아는 다른 번호의 회차 파일을 git rm.</summary>
    internal async Task<GitResult> RemoveRoundsExceptAsync(string site, int keep, CancellationToken ct)
    {
        var rel = ReleasePath(site);
        var files = await GitAsync(false, ct, "-c", "core.quotepath=off", "ls-files", "--", rel);
        if (!files.Ok) return Fail(files, "git ls-files");
        var old = files.Out.Where(f => RoundOf(f, Image(site)) is { } r && r.Number != keep).ToArray();
        if (old.Length == 0) return new(GitOutcome.NoChange);
        var rm = await GitAsync(false, ct, ["rm", "--quiet", "--", .. old]);
        return rm.Ok ? new(GitOutcome.Ok) : Fail(rm, "이전 회차 삭제");
    }

    /// <summary>
    /// 회차 내기 전에(빌드·복사 전에) 본다: 새 회차 파일이 .gitignore 에 걸리는가, 커밋 작성자가 있는가.
    /// 걸리면 까닭, 통과면 null. 여기서 못 거르는 커밋 실패는 <see cref="RestoreRoundAsync"/> 로 되돌린다.
    /// </summary>
    internal async Task<GitResult?> RoundReadyAsync(string site, string number, CancellationToken ct)
    {
        var rel = ReleasePath(site);
        var ignored = await GitAsync(false, ct, "check-ignore", "-q", "--", $"{rel}/{Image(site)}_{number}.out");
        if (ignored.Code == 0 && ignored.Why == null) return new(GitOutcome.Failed, $".gitignore 에 !{rel}/* 가 필요");
        foreach (var who in new[] { "GIT_AUTHOR_IDENT", "GIT_COMMITTER_IDENT" })
        {
            var id = await GitAsync(false, ct, "var", who);
            if (!id.Ok) return new(GitOutcome.Failed, "작성자 없음(git config user.name·user.email): " + Last(id));
        }
        return null;
    }

    /// <summary>
    /// 회차 내기 실패 복구: release/&lt;사이트&gt; 의 올린 것과 작업 폴더를 HEAD 로 되돌린다
    /// (git restore --source=HEAD --staged --worktree -- release/&lt;사이트&gt;). 회차 실패 복구에서만 부른다.
    /// 깃이 모르는 새 파일은 호출자가 지운다.
    /// </summary>
    internal async Task<GitResult> RestoreRoundAsync(string site, CancellationToken ct)
    {
        Note($"round-restore {site}");
        var rel = ReleasePath(site);
        var index = await GitAsync(false, ct, "ls-files", "--", rel);
        var head = await GitAsync(false, ct, "ls-tree", "-r", "--name-only", "HEAD", "--", rel);
        if (index.Out.Count == 0 && head.Out.Count == 0) return new(GitOutcome.NoChange); // 깃이 아는 것이 없다
        var r = await GitAsync(false, ct, "restore", "--source=HEAD", "--staged", "--worktree", "--", rel);
        return r.Ok ? new(GitOutcome.Ok) : Fail(r, "회차 되돌리기");
    }

    /// <summary>회차 커밋: git add -- release/&lt;사이트&gt; → 회차 파일이 깃에 들어갔는지(.gitignore 예외) → 커밋.</summary>
    internal async Task<GitResult> CommitRoundAsync(string site, string number, string message, CancellationToken ct)
    {
        var rel = ReleasePath(site);
        Note($"round {site} {number}");
        var add = await GitAsync(false, ct, "add", "--", rel);
        if (!add.Ok) return Fail(add, "git add");
        var known = await GitAsync(false, ct, "ls-files", "--", $"{rel}/{Image(site)}_{number}.out");
        if (known.Out.Count == 0) return new(GitOutcome.Failed, $".gitignore 에 !{rel}/* 가 필요");
        return await CommitPathsAsync(message, ct, rel);
    }

    // README 는 줄마다 읽은 줄 끝을 그대로 둔다(고친 줄·넣은 줄만 바뀐다)

    /// <summary>글자를 줄로 가른다. 줄마다 제 줄 끝(<c>\r\n</c>, <c>\n</c>, 끝 줄은 없을 수 있음)을 달고 있다.</summary>
    internal static List<string> SplitKeep(string text)
    {
        var list = new List<string>();
        for (int p = 0; p < text.Length;)
        {
            int e = text.IndexOf('\n', p);
            e = e < 0 ? text.Length : e + 1;
            list.Add(text[p..e]);
            p = e;
        }
        return list;
    }

    static string Eol(string line) => line.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : line.EndsWith('\n') ? "\n" : "";

    static string Body(string line) => line[..^Eol(line).Length];

    /// <summary>새 줄에 쓸 줄 끝: 그 파일의 첫 줄 끝, 없으면 LF.</summary>
    static string FirstEol(List<string> lines) => lines.Select(Eol).FirstOrDefault(e => e.Length > 0) ?? "\n";

    internal static (List<string> Lines, bool Bom) ReadLines(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bool bom = bytes is [0xEF, 0xBB, 0xBF, ..];
        return (SplitKeep(Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0))), bom);
    }

    internal static void WriteLines(string path, List<string> lines, bool bom)
    {
        var body = ProcessRunner.Utf8.GetBytes(string.Concat(lines));
        File.WriteAllBytes(path, bom ? [0xEF, 0xBB, 0xBF, .. body] : body);
    }

    /// <summary>README 맨 위 회차 줄 자리: 첫 회차 줄 앞. 회차 줄이 없으면 끝(빈 줄 하나 띄움).</summary>
    internal static void InsertRoundLine(string path, string line)
    {
        if (!File.Exists(path)) { File.WriteAllBytes(path, ProcessRunner.Utf8.GetBytes(line + "\n")); return; }
        var (lines, bom) = ReadLines(path);
        var nl = FirstEol(lines);
        int at = lines.FindIndex(l => RoundLine.IsMatch(l));
        if (at >= 0) lines.Insert(at, line + (Eol(lines[at]).Length > 0 ? Eol(lines[at]) : nl));
        else
        {
            if (lines.Count > 0 && Eol(lines[^1]).Length == 0) lines[^1] += nl;
            if (lines.Count > 0 && Body(lines[^1]).Length > 0) lines.Add(nl);
            lines.Add(line + nl);
        }
        WriteLines(path, lines, bom);
    }
}
