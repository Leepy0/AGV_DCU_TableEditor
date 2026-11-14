using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TableEditor.Engine.Ops;

/// <summary>압축 결과. 실패면 Step(예 "tag 3-1 생성기 빌드")과 Reason. Written = 바꿔 쓴 생성물 파일 이름.</summary>
public sealed record PackResult(bool Ok, bool Changed, string? Step, string? Reason, IReadOnlyList<string> Written);

/// <summary>
/// 사양 7.2절 3(태그)·3b(시퀀스). 사이트 폴더의 생성물(*_packed.c)마다 휴대용 gcc 로 생성기를 빌드·실행하고
/// 원본 해시 주석을 넣어 지금 파일과 바이트로 견준다(다르면 바꿔 쓴다). python 은 쓰지 않는다.
/// </summary>
public static class SitePack
{
    /// <summary>생성물 하나의 규칙. 사양 7.2절 끝: 지금은 태그·시퀀스 둘.</summary>
    sealed record Kind(string Tag, string Packed, string Gen, string[] Tables, string Define, string Symbol);

    static readonly Kind[] Kinds =
    [
        new("tag", "tag_packed.c", "tools/tag_pack_gen.c", ["tag_table.c"], "AGV_TAG_EXPANDED=1", "site_tags_pk"),
        new("seq", "seq_packed.c", "tools/seq_pack_gen.c", ["seq_table.c", "act_table.c"], "AGV_SEQ_EXPANDED=1", ""),
    ];

    static readonly Regex Steps = new(@"sizeof\s*\(\s*(\w+)_steps\s*\)");

    /// <summary>gcc 는 영어로 말하게 한다: 경고를 <c>warning:</c> 글자로 찾는다(한글 메시지면 놓친다).</summary>
    static readonly IReadOnlyDictionary<string, string?> GccEnv = new Dictionary<string, string?> { ["LC_ALL"] = "C", ["LANG"] = "C", ["LANGUAGE"] = "C" };

    /// <summary>
    /// 봐주는 경고 한 가지: 현장 이미지용 <c>site.h</c> 의 <c>#define AGV_DEV_TESTS 0</c> 이 코어 기본값을 다시 정의한다(사양 7.2절).
    /// 표 값과 상관없다. 다른 경고는 값이 잘린 압축 표일 수 있어 모두 실패로 본다(7.3절 excess elements).
    /// </summary>
    static readonly Regex AllowedWarning = new(@"warning: [""']AGV_DEV_TESTS[""'] redefined");

    /// <summary>gcc 출력에서 실패로 볼 첫 경고 줄. 없으면 null.</summary>
    internal static string? BadWarning(IEnumerable<string> lines) =>
        lines.FirstOrDefault(t => t.Contains("warning:", StringComparison.Ordinal) && !AllowedWarning.IsMatch(t));

    public static async Task<PackResult> RunAsync(string appRoot, string site, string? gccPath = null, Action<OutputLine>? onLine = null, CancellationToken ct = default)
    {
        gccPath ??= ToolProbe.DefaultGcc();
        if (site.Length == 0 || site.IndexOfAny(['/', '\\', ':']) >= 0 || site == "..") return Fail("입력", $"사이트 이름이 잘못됨: {site}");
        if (!File.Exists(gccPath)) return Fail("gcc", $"gcc 없음: {gccPath}");
        var sdir = $"site/{site}";
        var full = Path.Combine(appRoot, "site", site);
        if (!Directory.Exists(full)) return Fail("입력", $"사이트 폴더 없음: {sdir}");

        // 사이트 폴더의 생성물이 규칙이다. 모르는 *_packed.c 는 조용히 두지 않는다
        var known = Kinds.Select(k => k.Packed).ToHashSet();
        var unknown = Directory.GetFiles(full, "*_packed.c").Select(Path.GetFileName).FirstOrDefault(n => !known.Contains(n!));
        if (unknown != null) return Fail("입력", $"모르는 생성물: {unknown}");

        var written = new List<string>();
        foreach (var k in Kinds)
        {
            // 사양 7.2절: 사이트 폴더에 있는 생성물이 규칙이다. 원본 표만 있고 생성물이 없는 사이트(손으로 쓴 압축 표 등)에는 만들지 않는다
            if (!File.Exists(Path.Combine(full, k.Packed))) continue;
            var r = await RunKind(k, appRoot, sdir, gccPath, onLine, ct);
            if (r.Reason != null) return Fail($"{k.Tag} {r.Step}", r.Reason);
            if (r.Changed) written.Add(k.Packed);
        }
        return new PackResult(true, written.Count > 0, null, null, written);
    }

    static PackResult Fail(string step, string reason) => new(false, false, step, reason, []);

    sealed record KindResult(bool Changed, string? Step, string? Reason);

    static string? Why(ProcessResult r) =>
        r.Cancelled ? "취소됨" : r.TimedOut ? "시간 초과" : r.StartError ?? (r.ExitCode != 0 ? $"종료 코드 {r.ExitCode}" : null);

    static async Task<KindResult> RunKind(Kind k, string appRoot, string sdir, string gcc, Action<OutputLine>? onLine, CancellationToken ct)
    {
        var enc = ProcessRunner.Utf8;
        var def = new[] { "-DAGV_HOST_BUILD", "-D" + k.Define };
        var inc = new[] { "-I", "core/include", "-I", sdir };
        var tables = k.Tables.Select(t => $"{sdir}/{t}").ToArray();
        foreach (var need in tables.Prepend(k.Gen))
            if (!File.Exists(Path.Combine(appRoot, need))) return new(false, "입력", $"파일 없음: {need}");
        var dir = $"build/{k.Tag}_pack";
        Directory.CreateDirectory(Path.Combine(appRoot, dir));
        var exe = $"{dir}/{k.Tag}_pack_gen.exe";
        var exeFull = Path.Combine(appRoot, exe);
        var gen = $"{dir}/{k.Packed}";

        async Task<ProcessResult> Run(string file, IReadOnlyList<string> args, bool retry = false)
        {
            var spec = new ProcessSpec(file, args, appRoot, TimeSpan.FromMinutes(2), enc, GccEnv);
            var r = await ProcessRunner.RunAsync(spec, onLine, ct);
            // 방금 만든 exe 의 첫 실행이 막힐 때가 있다(백신 검사 추정): 2초 뒤 한 번만 다시
            if (retry && r.StartError != null && !ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
                r = await ProcessRunner.RunAsync(spec, onLine, ct);
            }
            return r;
        }

        // 이름 파일(3b-0): 대응표 줄마다 표 이름, ACT 먼저
        string? namesArg = null;
        if (k.Tag == "seq")
        {
            var acts = await MapNames(gcc, appRoot, def, inc, tables[1], "site_acts", ct);
            var seqs = await MapNames(gcc, appRoot, def, inc, tables[0], "site_seqs", ct);
            if (acts == null || seqs == null) return new(false, "3b-0 이름 파일", "gcc -E 실패");
            namesArg = $"{dir}/seq_names.txt";
            // 지금 python 도구는 텍스트 모드로 써서 Windows 에서 줄 끝이 CRLF 다
            File.WriteAllBytes(Path.Combine(appRoot, namesArg), enc.GetBytes(string.Concat(acts.Concat(seqs).Select(n => n + "\r\n"))));
        }

        var n1 = k.Tag == "tag" ? "3-1" : "3b-1";
        string[] build = ["-std=gnu99", "-O0", "-Wall", .. def, .. inc, k.Gen, .. tables, "-o", exe];
        var r1 = await Run(gcc, build);
        if (Why(r1) is { } w1) return new(false, $"{n1} 생성기 빌드", w1);
        if (BadWarning(r1.Texts) is { } warn) return new(false, $"{n1} 생성기 빌드", "gcc 출력에 warning: 이 있음 · " + warn.Trim());

        if (k.Tag == "tag")
        {
            var r2 = await Run(exeFull, ["--selftest"], retry: true);
            if (Why(r2) is { } w2) return new(false, "3-2 자체 시험", w2);
        }
        var r3 = await Run(exeFull, k.Tag == "tag" ? [gen, k.Symbol] : [gen, namesArg!], retry: true);
        if (Why(r3) is { } w3) return new(false, k.Tag == "tag" ? "3-3 압축 표 생성" : "3b-2 압축 표 생성", w3);

        var last = k.Tag == "tag" ? "3-4 원본 해시" : "3b-3 원본 해시";
        try
        {
            var deps = await SiteDeps(appRoot, gcc, def, inc, tables, sdir, ct);
            if (deps == null) return new(false, last, "gcc -MM 실패");
            var fresh = Stamp(File.ReadAllBytes(Path.Combine(appRoot, gen)), deps, Path.Combine(appRoot, sdir));
            var dst = Path.Combine(appRoot, sdir, k.Packed);
            if (File.Exists(dst) && File.ReadAllBytes(dst).AsSpan().SequenceEqual(fresh)) return new(false, null, null);
            File.WriteAllBytes(dst, fresh);
            return new(true, null, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return new(false, last, e.Message);
        }
    }

    /// <summary>gcc -E -P 로 펼친 뒤 대응표 변수(var[] = { … };)의 sizeof(이름_steps) 이름들. 표가 없으면 빈 목록, gcc 실패면 null.</summary>
    static async Task<List<string>?> MapNames(string gcc, string appRoot, string[] def, string[] inc, string table, string var, CancellationToken ct)
    {
        var spec = new ProcessSpec(gcc, ["-E", "-P", .. def, .. inc, table], appRoot, TimeSpan.FromMinutes(1), ProcessRunner.Utf8, GccEnv);
        var r = await ProcessRunner.RunAsync(spec, null, ct);
        if (!r.Ok) return null;
        var pre = string.Join("\n", r.Lines.Where(l => !l.IsError).Select(l => l.Text));
        var m = Regex.Match(pre, @"\b" + var + @"\s*\[\s*\]\s*=\s*\{(.*?)\}\s*;", RegexOptions.Singleline);
        return m.Success ? Steps.Matches(m.Groups[1].Value).Select(x => x.Groups[1].Value).ToList() : [];
    }

    /// <summary>gcc -MM 으로 원본 표 파일들이 include 하는 파일 가운데 사이트 폴더 안 것. 사이트 기준 경로(/), 처음 나온 차례.</summary>
    static async Task<List<string>?> SiteDeps(string appRoot, string gcc, string[] def, string[] inc, string[] tables, string sdir, CancellationToken ct)
    {
        var root = Path.GetFullPath(Path.Combine(appRoot, sdir)).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var seen = new List<string>();
        foreach (var t in tables)
        {
            var spec = new ProcessSpec(gcc, ["-MM", "-MT", "x", .. def, .. inc, t], appRoot, TimeSpan.FromMinutes(1), ProcessRunner.Utf8, GccEnv);
            var r = await ProcessRunner.RunAsync(spec, null, ct);
            if (!r.Ok) return null;
            var body = string.Join(" ", r.Lines.Where(l => !l.IsError).Select(l => l.Text.TrimEnd('\\')));
            var colon = body.IndexOf(':');
            if (colon < 0) return null;
            foreach (Match m in Regex.Matches(body[(colon + 1)..], @"(?:\\ |\S)+"))
            {
                var p = Path.GetFullPath(m.Value.Replace("\\ ", " "), appRoot);
                if (!p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                var rel = p[root.Length..].Replace('\\', '/');
                if (!seen.Contains(rel)) seen.Add(rel);
            }
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
