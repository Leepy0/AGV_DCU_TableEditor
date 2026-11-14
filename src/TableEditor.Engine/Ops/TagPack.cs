using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TableEditor.Engine.Ops;

/// <summary>압축 태그 표 결과. Reason 이 있으면 실패(그 단계 이름과 이유). Changed = site/&lt;사이트&gt;/tag_packed.c 를 바꿔 썼다.</summary>
public sealed record PackResult(bool Ok, bool Changed, string? Step, string? Reason);

/// <summary>사양 7.2절 3-1~3-4. python 없이 휴대용 gcc 로 tag_pack_gen.c 를 빌드·실행하고 원본 해시 주석을 넣는다.</summary>
public static class TagPack
{
    static readonly string[] Defs = ["-DAGV_HOST_BUILD", "-DAGV_TAG_EXPANDED=1"];

    public static async Task<PackResult> RunAsync(string appRoot, string site, string? gccPath = null, Action<OutputLine>? onLine = null, CancellationToken ct = default)
    {
        gccPath ??= ToolProbe.DefaultGcc();
        if (site.Length == 0 || site.IndexOfAny(['/', '\\', ':']) >= 0 || site == "..") return Fail("입력", $"사이트 이름이 잘못됨: {site}");
        if (!File.Exists(gccPath)) return Fail("gcc", $"gcc 없음: {gccPath}");
        var sdir = $"site/{site}";
        var work = Path.Combine(appRoot, "build", "tag_pack");
        foreach (var need in new[] { $"{sdir}/tag_table.c", "tools/tag_pack_gen.c" })
            if (!File.Exists(Path.Combine(appRoot, need))) return Fail("입력", $"파일 없음: {need}");
        Directory.CreateDirectory(work);
        var exe = "build/tag_pack/tag_pack_gen.exe";
        var gen = "build/tag_pack/tag_packed.c";
        var enc = ProcessRunner.Utf8;

        Task<ProcessResult> Run(string file, IReadOnlyList<string> args, bool retry = false) => RunStep(file, args, retry);
        async Task<ProcessResult> RunStep(string file, IReadOnlyList<string> args, bool retry)
        {
            var spec = new ProcessSpec(file, args, appRoot, TimeSpan.FromMinutes(2), enc);
            var r = await ProcessRunner.RunAsync(spec, onLine, ct);
            // 방금 만든 exe 의 첫 실행이 막힐 때가 있다(백신 검사 추정): 2초 뒤 한 번만 다시
            if (retry && r.StartError != null && !ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
                r = await ProcessRunner.RunAsync(spec, onLine, ct);
            }
            return r;
        }
        static string? Why(ProcessResult r) =>
            r.Cancelled ? "취소됨" : r.TimedOut ? "시간 초과" : r.StartError ?? (r.ExitCode != 0 ? $"종료 코드 {r.ExitCode}" : null);

        // 3-1
        string[] build = ["-std=gnu99", "-O0", "-Wall", .. Defs, "-I", "core/include", "-I", sdir, "tools/tag_pack_gen.c", $"{sdir}/tag_table.c", "-o", exe];
        var r1 = await Run(gccPath, build);
        if (Why(r1) is { } w1) return Fail("3-1 생성기 빌드", w1);
        if (r1.Texts.Any(t => t.Contains("warning:"))) return Fail("3-1 생성기 빌드", "gcc 출력에 warning: 이 있음");
        // 3-2
        var r2 = await Run(Path.Combine(appRoot, exe), ["--selftest"], retry: true);
        if (Why(r2) is { } w2) return Fail("3-2 자체 시험", w2);
        // 3-3
        var r3 = await Run(Path.Combine(appRoot, exe), [gen, "site_tags_pk"], retry: true);
        if (Why(r3) is { } w3) return Fail("3-3 압축 표 생성", w3);
        // 3-4
        try
        {
            var deps = await SiteDeps(appRoot, gccPath, sdir, ct);
            if (deps == null) return Fail("3-4 원본 해시", "gcc -MM 실패");
            var fresh = Stamp(File.ReadAllBytes(Path.Combine(appRoot, gen)), deps, Path.Combine(appRoot, sdir));
            var dst = Path.Combine(appRoot, sdir, "tag_packed.c");
            if (File.Exists(dst) && File.ReadAllBytes(dst).AsSpan().SequenceEqual(fresh)) return new PackResult(true, false, null, null);
            File.WriteAllBytes(dst, fresh);
            return new PackResult(true, true, null, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return Fail("3-4 원본 해시", e.Message);
        }

        static PackResult Fail(string step, string reason) => new(false, false, step, reason);
    }

    /// <summary>gcc -MM 으로 tag_table.c 가 include 하는 파일 가운데 사이트 폴더 안 것. 사이트 기준 경로(/), 처음 나온 차례.</summary>
    static async Task<List<string>?> SiteDeps(string appRoot, string gcc, string sdir, CancellationToken ct)
    {
        var spec = new ProcessSpec(gcc, ["-MM", "-MT", "x", .. Defs, "-I", "core/include", "-I", sdir, $"{sdir}/tag_table.c"], appRoot, TimeSpan.FromMinutes(1), ProcessRunner.Utf8);
        var r = await ProcessRunner.RunAsync(spec, null, ct);
        if (!r.Ok) return null;
        var body = string.Join(" ", r.Lines.Where(l => !l.IsError).Select(l => l.Text.TrimEnd('\\')));
        var colon = body.IndexOf(':');
        if (colon < 0) return null;
        var root = Path.GetFullPath(Path.Combine(appRoot, sdir)).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var seen = new List<string>();
        foreach (Match m in Regex.Matches(body[(colon + 1)..], @"(?:\\ |\S)+"))
        {
            var p = Path.GetFullPath(m.Value.Replace("\\ ", " "), appRoot);
            if (!p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = p[root.Length..].Replace('\\', '/');
            if (!seen.Contains(rel)) seen.Add(rel);
        }
        return seen;
    }

    /// <summary>core_manifest.ps1 Get-NormHash 와 같다: CRLF 를 LF 로 바꾼 바이트의 SHA-256 소문자 16진.</summary>
    public static string NormHash(byte[] bytes)
    {
        var o = new List<byte>(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 13 && i + 1 < bytes.Length && bytes[i + 1] == 10) continue;
            o.Add(bytes[i]);
        }
        return Convert.ToHexStringLower(SHA256.HashData(o.ToArray()));
    }

    /// <summary>생성물 첫 주석(*/ 줄) 바로 다음에 원본 해시 주석을 넣는다. 줄 끝은 생성물과 같게(tools/pack_src.py stamp).</summary>
    public static byte[] Stamp(byte[] data, IReadOnlyList<string> files, string siteDir)
    {
        var nl = Encoding.ASCII.GetBytes(Contains(data, "\r\n"u8) ? "\r\n" : "\n");
        var lines = new List<string> { "/* 원본 해시(SHA-256, 줄 끝 LF). 원본이 바뀌면 check.ps1·core_lib.ps1 이 빌드를 멈춘다(core_manifest.ps1 Test-PackedFresh)" };
        lines.AddRange(files.Select(f => $" * src {f} {NormHash(File.ReadAllBytes(Path.Combine(siteDir, f)))}"));
        lines.Add(" */");
        var block = new List<byte>();
        foreach (var l in lines) { block.AddRange(Encoding.UTF8.GetBytes(l)); block.AddRange(nl); }
        int end = data.AsSpan().IndexOf("*/"u8);
        if (end < 0) throw new InvalidOperationException("생성물에 첫 주석이 없음");
        end += 2;
        int nlAt = data.AsSpan(end).IndexOf(nl);
        if (nlAt < 0) throw new InvalidOperationException("생성물 첫 주석 뒤에 줄 끝이 없음");
        end += nlAt + nl.Length;
        return [.. data.AsSpan(0, end), .. block, .. data.AsSpan(end)];
    }

    static bool Contains(byte[] data, ReadOnlySpan<byte> pat) => data.AsSpan().IndexOf(pat) >= 0;
}
