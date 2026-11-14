using TableEditor.Engine.Check;
using TableEditor.Engine.Text;

namespace TableEditor.Engine.Model;

public enum SaveStatus { Saved, NoChange, Errors, Conflict, Failed }

/// <summary>
/// 저장 결과. Site = 저장했으면 디스크에서 다시 읽은 사이트, 아니면 받은 사이트. Written = 쓴 파일 이름.
/// Conflicts = 읽은 뒤 바깥에서 바뀐 파일(이때 아무것도 안 쓴다. 다시 읽기 / overwrite 로 덮기). Errors = 저장을 막은 검사 오류.
/// Lines = 쓴 상태의 (파일, 줄) → 조각 표(빌드 오류를 블록으로 잇는다).
/// </summary>
public sealed record SaveResult(SaveStatus Status, Site Site, IReadOnlyList<string> Written, IReadOnlyList<string> Conflicts,
    IReadOnlyList<Finding> Errors, string? Reason, LineMap Lines)
{
    public bool Ok => Status is SaveStatus.Saved or SaveStatus.NoChange;
}

/// <summary>
/// 저장(설계서 4.7절 차례 1~4, 사양 5.4·5.7절, R-3): 검사 오류 0 → 바깥에서 바뀐 파일 확인 → 바뀐 파일만 임시 파일에 쓰고 한꺼번에 바꿔치기
/// → 다시 읽어 모델 견줌 → 실패하면 쓴 파일을 모두 저장 전 바이트·시각으로 되돌린다. 안 바뀐 파일은 쓰지 않는다(시각도 그대로).
/// </summary>
public static class SaveFiles
{
    /// <summary>시험 주입점: 파일 하나를 바꿔치기한 바로 뒤에 부른다(경로). 던지면 저장 중 실패, 파일을 고치면 R-3.</summary>
    internal static Action<string>? AfterSwap;

    const string TempSuffix = ".tedit_tmp";

    sealed class R3Exception(string message) : Exception(message);

    /// <summary>읽은 뒤(또는 저장 뒤) 편집으로 바이트가 달라진 파일. 읽기 전용 파일은 늘 빠진다.</summary>
    public static IReadOnlyList<string> Changed(Site site) =>
        site.Files.Where(f => !f.ReadOnly && (!site.Stamps.TryGetValue(f.Name, out var s) || s.Sha256 != FileStamp.Hash(f.ToBytes())))
            .Select(f => f.Name).ToList();

    /// <summary>읽은 뒤 디스크에서 바뀐 파일(크기·시각이 같으면 그대로로 본다. 다르면 해시로 가른다). 없어진 파일·읽은 기록 없는 파일도 든다.</summary>
    public static IReadOnlyList<string> Conflicts(Site site, IEnumerable<string>? names = null)
    {
        var list = new List<string>();
        foreach (var n in names ?? site.Files.Select(f => f.Name))
        {
            var path = Path.Combine(site.Folder, n);
            if (!site.Stamps.TryGetValue(n, out var s) || !File.Exists(path)) { list.Add(n); continue; }
            var fi = new FileInfo(path);
            if (fi.Length == s.Size && fi.LastWriteTimeUtc == s.WriteUtc) continue;
            if (FileStamp.Hash(File.ReadAllBytes(path)) != s.Sha256) list.Add(n);
        }
        return list;
    }

    /// <param name="overwrite">바깥에서 바뀐 파일이 있어도 내 것으로 덮는다(팝업 "내 것으로 덮기").</param>
    public static SaveResult Save(Site edited, bool overwrite = false)
    {
        SaveResult R(SaveStatus st, Site s, IReadOnlyList<string> w, IReadOnlyList<string> c, IReadOnlyList<Finding> e, string? why) =>
            new(st, s, w, c, e, why, LineMap.Of(s));

        var errors = Checker.Run(edited).Where(f => f.Severity == Severity.Error).ToList();
        if (errors.Count > 0)
        {
            string? draft = null;
            try { if (edited.Folder.Length > 0) Drafts.Write(edited); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { draft = " · 초안 못 씀 " + e.Message; }
            return R(SaveStatus.Errors, edited, [], [], errors, $"검사 오류 {errors.Count}{draft}");
        }
        var changed = Changed(edited);
        if (changed.Count == 0)
        {
            try { if (edited.Folder.Length > 0) Drafts.Clear(edited); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* 낡은 초안: 다음 저장이 지운다 */ }
            return R(SaveStatus.NoChange, edited, [], [], [], null);
        }
        if (!overwrite && Conflicts(edited, changed) is { Count: > 0 } c) return R(SaveStatus.Conflict, edited, [], c, [], "바깥에서 바뀜");

        var plan = changed.Select(n => (Name: n, Path: Path.Combine(edited.Folder, n), Bytes: edited.File(n)!.ToBytes())).ToList();
        var backup = new List<(string Path, byte[]? Bytes, DateTime Time)>();
        var swapped = new List<int>();
        try
        {
            foreach (var p in plan)
                backup.Add(File.Exists(p.Path) ? (p.Path, File.ReadAllBytes(p.Path), File.GetLastWriteTimeUtc(p.Path)) : (p.Path, null, default));
            foreach (var p in plan) File.WriteAllBytes(p.Path + TempSuffix, p.Bytes);
            for (int i = 0; i < plan.Count; i++)
            {
                File.Move(plan[i].Path + TempSuffix, plan[i].Path, overwrite: true);
                swapped.Add(i);
                AfterSwap?.Invoke(plan[i].Path);
            }
            foreach (var p in plan)
            {
                var back = File.ReadAllBytes(p.Path);
                if (!back.AsSpan().SequenceEqual(p.Bytes)) throw new R3Exception($"R-3 다시 읽은 바이트 다름 · {p.Name}");
                if (!SameModel(SourceFile.Parse(p.Name, back), edited.File(p.Name)!)) throw new R3Exception($"R-3 다시 읽은 모델 다름 · {p.Name}");
            }
        }
        catch (Exception e)
        {
            var why = e is R3Exception ? e.Message : $"쓰기 실패 · {e.Message}";
            foreach (var i in swapped)
            {
                var (path, bytes, time) = backup[i];
                try
                {
                    if (bytes == null) File.Delete(path);
                    else
                    {
                        File.WriteAllBytes(path, bytes);
                        File.SetLastWriteTimeUtc(path, time);
                    }
                }
                catch (Exception re) { why += $" · 복구 실패 {Path.GetFileName(path)} {re.Message}"; }
            }
            foreach (var p in plan)
                try { File.Delete(p.Path + TempSuffix); } catch (Exception) { /* 남은 임시 파일: 다음 저장이 덮는다 */ }
            return R(SaveStatus.Failed, edited, [], [], [], why);
        }

        var saved = Site.Open(edited.Folder, edited.CoreFolder);
        try { Drafts.Clear(saved); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* 초안 지우기 실패는 저장을 막지 않는다 */ }
        return R(SaveStatus.Saved, saved, changed, [], [], null);
    }

    /// <summary>R-3: 조각 목록이 같은가(종류·글자·줄·구역·문장 이름).</summary>
    internal static bool SameModel(SourceFile a, SourceFile b) =>
        a.ReadOnly == b.ReadOnly && a.Pieces.Count == b.Pieces.Count
        && a.Pieces.Zip(b.Pieces).All(x => x.First.Kind == x.Second.Kind && x.First.Text == x.Second.Text
            && x.First.StartLine == x.Second.StartLine && x.First.EndLine == x.Second.EndLine && x.First.Zone == x.Second.Zone
            && (x.First as Statement)?.Name == (x.Second as Statement)?.Name);
}

/// <summary>(파일, 줄) → 조각. 저장한 상태로 만든다(사양 7.3절 끝: 빌드 오류 줄을 블록으로).</summary>
public sealed class LineMap
{
    readonly Dictionary<string, IReadOnlyList<Piece>> byFile;

    LineMap(Dictionary<string, IReadOnlyList<Piece>> map) => byFile = map;

    public static LineMap Of(Site site) =>
        new(site.Files.ToDictionary(f => f.Name, f => f.Pieces, StringComparer.OrdinalIgnoreCase));

    /// <summary>그 줄을 품은 조각(없으면 null). 파일 이름은 사이트 폴더 기준(<see cref="Ops.BuildError.File"/>).</summary>
    public Piece? At(string file, int line)
    {
        if (!byFile.TryGetValue(Path.GetFileName(file), out var ps)) return null;
        int lo = 0, hi = ps.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (ps[mid].EndLine < line) lo = mid + 1;
            else if (ps[mid].StartLine > line) hi = mid - 1;
            else return ps[mid];
        }
        return null;
    }
}

/// <summary>초안 읽기 결과. Files = 초안에서 가져온 파일, Stale = 초안을 쓴 뒤 디스크 파일이 바뀐 것.</summary>
public sealed record DraftInfo(Site Site, IReadOnlyList<string> Files, IReadOnlyList<string> Stale);

/// <summary>
/// 초안(사양 6절 머리): 오류로 저장이 막힌 편집 내용. <c>&lt;앱 루트&gt;/build/table_editor/draft/&lt;사이트&gt;/</c>
/// (사이트 폴더가 <c>site/</c> 밑이 아니면 <c>%LOCALAPPDATA%\AGV_DCU_TableEditor\draft\&lt;사이트&gt;\</c>).
/// 바뀐 파일의 바이트 그대로와 <c>base.txt</c>(파일마다 읽은 때의 해시).
/// </summary>
public static class Drafts
{
    const string BaseFile = "base.txt";

    public static string Folder(Site site)
    {
        var full = Path.GetFullPath(site.Folder).TrimEnd('\\', '/');
        var name = Path.GetFileName(full);
        var parent = Path.GetDirectoryName(full);
        if (parent != null && string.Equals(Path.GetFileName(parent), "site", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(parent) is { } app)
            return Path.Combine(app, "build", "table_editor", "draft", name);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AGV_DCU_TableEditor", "draft", name);
    }

    /// <summary>바뀐 파일을 초안으로 쓴다(전 초안은 지운다). 바뀐 것이 없으면 초안을 지운다. 쓴 파일 이름.</summary>
    public static IReadOnlyList<string> Write(Site edited)
    {
        var changed = SaveFiles.Changed(edited);
        Clear(edited);
        if (changed.Count == 0) return [];
        var dir = Directory.CreateDirectory(Folder(edited)).FullName;
        foreach (var n in changed) File.WriteAllBytes(Path.Combine(dir, n), edited.File(n)!.ToBytes());
        var lines = changed.Select(n => $"{n}\t{(edited.Stamps.TryGetValue(n, out var s) ? s.Sha256 : "")}\n");
        File.WriteAllText(Path.Combine(dir, BaseFile), string.Concat(lines));
        return changed;
    }

    /// <summary>초안이 있으면 읽은 사이트에 얹어 돌려준다. 없으면 null.</summary>
    public static DraftInfo? Read(Site loaded)
    {
        var dir = Folder(loaded);
        var basePath = Path.Combine(dir, BaseFile);
        if (!File.Exists(basePath)) return null;
        var files = loaded.Files.ToList();
        var got = new List<string>();
        var stale = new List<string>();
        foreach (var line in File.ReadAllLines(basePath))
        {
            var parts = line.Split('\t');
            var n = Path.GetFileName(parts[0]);
            var i = files.FindIndex(f => f.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            var path = Path.Combine(dir, n);
            if (i < 0 || !File.Exists(path)) continue;
            files[i] = SourceFile.Parse(files[i].Name, File.ReadAllBytes(path));
            got.Add(files[i].Name);
            if (parts.Length < 2 || !loaded.Stamps.TryGetValue(files[i].Name, out var s) || s.Sha256 != parts[1]) stale.Add(files[i].Name);
        }
        return got.Count == 0 ? null : new DraftInfo(loaded.With(files), got, stale);
    }

    public static void Clear(Site site)
    {
        if (site.Folder.Length == 0) return;
        var dir = Folder(site);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
