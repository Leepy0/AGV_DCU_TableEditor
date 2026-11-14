using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TableEditor.Engine.Ops;

/// <summary>실행할 프로세스. 인자는 목록으로 받는다(따옴표 문제 없음).</summary>
public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Args,
    string? WorkingDirectory = null,
    TimeSpan? Timeout = null,
    Encoding? OutputEncoding = null,
    IReadOnlyDictionary<string, string?>? Env = null);

/// <summary>한 줄 출력. 같은 줄 순서는 지키고 stdout·stderr 사이 순서는 도착 순서다.</summary>
public sealed record OutputLine(bool IsError, string Text);

/// <summary>StartError 가 있으면 프로세스를 시작하지 못한 것(ExitCode -1).</summary>
public sealed record ProcessResult(int ExitCode, bool TimedOut, bool Cancelled, string? StartError, IReadOnlyList<OutputLine> Lines)
{
    public bool Ok => ExitCode == 0 && !TimedOut && !Cancelled && StartError == null;
    public IEnumerable<string> Texts => Lines.Select(l => l.Text);
}

public static class ProcessRunner
{
    [DllImport("kernel32.dll")] private static extern uint GetOEMCP();

    /// <summary>콘솔 코드 페이지(한글 Windows 는 949). PowerShell 이 파이프로 내보내는 글자가 이것이다.</summary>
    public static Encoding Oem()
    {
        try { return CodePagesEncodingProvider.Instance.GetEncoding((int)GetOEMCP()) ?? Encoding.UTF8; }
        catch (Exception) { return Encoding.UTF8; }
    }

    public static readonly Encoding Utf8 = new UTF8Encoding(false);

    /// <summary>프로세스를 돌린다. 던지지 않는다: 못 시작하면 StartError, 시간 제한·취소는 프로세스 트리를 끝낸다.</summary>
    public static async Task<ProcessResult> RunAsync(ProcessSpec spec, Action<OutputLine>? onLine = null, CancellationToken ct = default)
    {
        var enc = spec.OutputEncoding ?? Utf8;
        var psi = new ProcessStartInfo(spec.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = enc,
            StandardErrorEncoding = enc,
        };
        foreach (var a in spec.Args) psi.ArgumentList.Add(a);
        if (spec.WorkingDirectory != null) psi.WorkingDirectory = spec.WorkingDirectory;
        if (spec.Env != null)
            foreach (var (k, v) in spec.Env)
            {
                if (v == null) psi.Environment.Remove(k); else psi.Environment[k] = v;
            }

        var lines = new List<OutputLine>();
        var gate = new object();
        void Add(bool err, string? text)
        {
            if (text == null) return;
            var l = new OutputLine(err, text);
            lock (gate)
            {
                lines.Add(l);
                try { onLine?.Invoke(l); } catch (Exception) { /* 콜백 오류가 실행을 막지 않는다 */ }
            }
        }

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => Add(false, e.Data);
        p.ErrorDataReceived += (_, e) => Add(true, e.Data);
        try
        {
            if (!p.Start()) return new ProcessResult(-1, false, false, "시작 실패", lines);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new ProcessResult(-1, false, false, $"{spec.FileName}: {e.Message}", lines);
        }
        try { p.StandardInput.Close(); } catch (IOException) { }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource();
        if (spec.Timeout is { } t) timeout.CancelAfter(t);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        bool timedOut = false, cancelled = false;
        try { await p.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            timedOut = timeout.IsCancellationRequested && !ct.IsCancellationRequested;
            cancelled = !timedOut;
            try { p.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
            try { await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch (InvalidOperationException) { }
        }
        int code = p.HasExited ? p.ExitCode : -1;
        return new ProcessResult(code, timedOut, cancelled, null, lines);
    }
}
