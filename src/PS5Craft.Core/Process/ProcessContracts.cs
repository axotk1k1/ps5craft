namespace PS5Craft.Core.Process;

public sealed class ProcessOutput
{
    public bool IsError { get; init; }
    public string Line { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed class ProcessStartRequest
{
    public required string Executable { get; init; }
    public List<string> Arguments { get; init; } = new();
    public string? WorkingDirectory { get; init; }
    public Dictionary<string, string>? Environment { get; init; }
    public TimeSpan? Timeout { get; init; }
    public ProcessPriorityClass Priority { get; init; } = ProcessPriorityClass.BelowNormal;
    public IntPtr? ProcessorAffinity { get; init; }
    /// <summary>Called on the runner thread right after the process has started (PID available).</summary>
    public Action<int>? OnStarted { get; init; }
}

public enum ProcessPriorityClass
{
    Normal,
    BelowNormal,
    Idle
}

public sealed class ProcessResult
{
    public int ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public bool Cancelled { get; init; }
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
    public TimeSpan Elapsed { get; init; }
    public int? ProcessId { get; init; }

    public bool Succeeded => !Cancelled && !TimedOut && ExitCode == 0;
}

public interface IExternalProcessRunner
{
    Task<ProcessResult> RunAsync(
        ProcessStartRequest request,
        IProgress<ProcessOutput>? progress = null,
        CancellationToken cancellationToken = default);

    Task KillProcessTreeAsync(int processId, CancellationToken cancellationToken = default);
}
