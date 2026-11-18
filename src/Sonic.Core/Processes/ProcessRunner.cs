using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Sonic.Core.Abstractions;

namespace Sonic.Core.Processes;

public sealed class ProcessRunner(ILogger<ProcessRunner> logger) : IProcessRunner
{
    private const int ErrorTailLines = 40;

    public async Task<ProcessResult> RunAsync(
        ProcessRequest request,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
        };
        foreach (var arg in request.Arguments) psi.ArgumentList.Add(arg);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        if (request.Environment is not null)
        {
            foreach (var (k, v) in request.Environment) psi.Environment[k] = v;
        }

        using var process = new Process { StartInfo = psi };
        logger.LogDebug("Starting {File} {Args}", Path.GetFileName(request.FileName), string.Join(' ', request.Arguments));

        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Could not start {request.FileName}.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Could not start {Path.GetFileName(request.FileName)}: {ex.Message}", ex);
        }

        ChildProcessJob.TryAssign(process);
        process.StandardInput.Close();

        var stdout = request.CaptureStandardOutput ? new StringBuilder() : null;
        var errorTail = new Queue<string>();

        var outTask = PumpAsync(process.StandardOutput, line =>
        {
            stdout?.AppendLine(line);
            onOutputLine?.Invoke(line);
        });
        var errTask = PumpAsync(process.StandardError, line =>
        {
            lock (errorTail)
            {
                errorTail.Enqueue(line);
                while (errorTail.Count > ErrorTailLines) errorTail.Dequeue();
            }

            onErrorLine?.Invoke(line);
        });

        using var timeoutCts = request.Timeout is { } t ? new CancellationTokenSource(t) : null;
        using var linked = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            // Give the pumps a moment to drain after the kill.
            await Task.WhenAny(Task.WhenAll(outTask, errTask), Task.Delay(2000, CancellationToken.None)).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            return new ProcessResult(-1, stdout?.ToString() ?? string.Empty, JoinTail(errorTail), TimedOut: true);
        }

        await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, stdout?.ToString() ?? string.Empty, JoinTail(errorTail), TimedOut: false);
    }

    private static string JoinTail(Queue<string> tail)
    {
        lock (tail) return string.Join('\n', tail);
    }

    private async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                try
                {
                    onLine(line);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Output handler failed for line: {Line}", line);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The process was killed and its pipes closed.
        }
    }

    private void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            logger.LogDebug(ex, "Process already exited while killing.");
        }
    }
}
