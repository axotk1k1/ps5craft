using System.Diagnostics;
using System.Text;
using PS5Craft.Core.Process;
using ProcessPriorityClass = PS5Craft.Core.Process.ProcessPriorityClass;

namespace PS5Craft.Infrastructure.Process;

public sealed class ExternalProcessRunner : IExternalProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        ProcessStartRequest request,
        IProgress<ProcessOutput>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Executable);

        if (!File.Exists(request.Executable) &&
            !LooksLikeCommandOnPath(request.Executable))
        {
            // Allow bare command names resolved via PATH (python, mkpfs).
            if (Path.IsPathRooted(request.Executable) || request.Executable.Contains(Path.DirectorySeparatorChar) ||
                request.Executable.Contains(Path.AltDirectorySeparatorChar))
            {
                throw new FileNotFoundException($"Executable not found: {request.Executable}", request.Executable);
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = request.Executable,
            WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in request.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (request.Environment != null)
        {
            foreach (var (key, value) in request.Environment)
            {
                psi.Environment[key] = value;
            }
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var sw = Stopwatch.StartNew();

        using var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {request.Executable}");
        }

        try
        {
            ApplyPriority(process, request.Priority);
            if (request.ProcessorAffinity is { } affinity && OperatingSystem.IsWindows())
            {
                process.ProcessorAffinity = affinity;
            }
        }
        catch
        {
            // Priority/affinity are best-effort.
        }

        var pid = process.Id;
        try
        {
            request.OnStarted?.Invoke(pid);
        }
        catch
        {
            // Monitoring hooks must not fail the pack.
        }

        using var timeoutCts = request.Timeout is { } t
            ? new CancellationTokenSource(t)
            : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var stdoutTask = PumpAsync(process.StandardOutput, stdout, isError: false, progress, linked.Token);
        var stderrTask = PumpAsync(process.StandardError, stderr, isError: true, progress, linked.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var timedOut = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            try
            {
                await KillProcessTreeAsync(pid, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Ignore kill failures after cancel.
            }

            sw.Stop();
            return new ProcessResult
            {
                ExitCode = process.HasExited ? process.ExitCode : -1,
                TimedOut = timedOut,
                Cancelled = !timedOut,
                StdOut = stdout.ToString(),
                StdErr = stderr.ToString(),
                Elapsed = sw.Elapsed,
                ProcessId = pid
            };
        }

        sw.Stop();
        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StdOut = stdout.ToString(),
            StdErr = stderr.ToString(),
            Elapsed = sw.Elapsed,
            ProcessId = pid
        };
    }

    public Task KillProcessTreeAsync(int processId, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            return KillWindowsProcessTreeAsync(processId, cancellationToken);
        }

        try
        {
            var p = System.Diagnostics.Process.GetProcessById(processId);
            p.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // Already exited.
        }

        return Task.CompletedTask;
    }

    private static async Task KillWindowsProcessTreeAsync(int processId, CancellationToken cancellationToken)
    {
        // Prefer taskkill /T for reliable tree termination on Windows.
        var psi = new ProcessStartInfo
        {
            FileName = "taskkill",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("/PID");
        psi.ArgumentList.Add(processId.ToString());
        psi.ArgumentList.Add("/T");
        psi.ArgumentList.Add("/F");

        try
        {
            using var killer = System.Diagnostics.Process.Start(psi);
            if (killer != null)
            {
                await killer.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            try
            {
                var p = System.Diagnostics.Process.GetProcessById(processId);
                p.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignored
            }
        }

        // Brief grace period.
        await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task PumpAsync(
        StreamReader reader,
        StringBuilder buffer,
        bool isError,
        IProgress<ProcessOutput>? progress,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            buffer.AppendLine(line);
            progress?.Report(new ProcessOutput { IsError = isError, Line = line });
        }
    }

    private static void ApplyPriority(System.Diagnostics.Process process, ProcessPriorityClass priority)
    {
        process.PriorityClass = priority switch
        {
            ProcessPriorityClass.Idle => System.Diagnostics.ProcessPriorityClass.Idle,
            ProcessPriorityClass.BelowNormal => System.Diagnostics.ProcessPriorityClass.BelowNormal,
            _ => System.Diagnostics.ProcessPriorityClass.Normal
        };
    }

    private static bool LooksLikeCommandOnPath(string name)
    {
        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
        {
            return false;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : new[] { string.Empty };

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, name + (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? string.Empty : ext));
                if (File.Exists(candidate))
                {
                    return true;
                }
            }

            if (File.Exists(Path.Combine(dir, name)))
            {
                return true;
            }
        }

        return false;
    }
}
