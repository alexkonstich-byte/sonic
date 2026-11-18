namespace Sonic.Core.Abstractions;

public sealed class ProcessRequest
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public bool CaptureStandardOutput { get; init; }
    public TimeSpan? Timeout { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardErrorTail, bool TimedOut);

/// <summary>Runs external executables asynchronously with streaming output and reliable termination.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        ProcessRequest request,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default);
}
