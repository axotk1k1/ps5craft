using System.Diagnostics;
using PS5Craft.Core.Abstractions;
using PS5Craft.Core.Models;
using DiagProcess = System.Diagnostics.Process;

namespace PS5Craft.Infrastructure.Monitoring;

public sealed class ResourceMonitor : IResourceMonitor
{
    private readonly object _gate = new();
    private int? _processId;
    private string? _processName;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private TimeSpan _lastCpu;
    private DateTime _lastSampleUtc = DateTime.UtcNow;

    public event EventHandler<OperationProgress>? Updated;

    public void Attach(int processId)
    {
        lock (_gate)
        {
            // Keep the CPU baseline when the same PID is re-attached (avoids a forever-"first sample" gap).
            if (_processId == processId)
            {
                return;
            }

            _processId = processId;
            _processName = null;
            try
            {
                using var p = DiagProcess.GetProcessById(processId);
                _processName = p.ProcessName;
            }
            catch
            {
                // Name resolved on first sample if the process is still starting.
            }

            _lastCpu = TimeSpan.Zero;
            _lastSampleUtc = DateTime.UtcNow;
        }
    }

    public void Detach()
    {
        lock (_gate)
        {
            _processId = null;
            _processName = null;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
        }

        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            int? pid;
            string? name;
            lock (_gate)
            {
                pid = _processId;
                name = _processName;
            }

            if (pid is null)
            {
                continue;
            }

            try
            {
                var sample = SampleTree(pid.Value, ref name);
                if (sample is null)
                {
                    continue;
                }

                lock (_gate)
                {
                    _processName ??= name;
                }

                var now = DateTime.UtcNow;
                var elapsed = (now - _lastSampleUtc).TotalSeconds;
                double? usage = null;
                if (elapsed > 0.05 && _lastCpu != TimeSpan.Zero)
                {
                    // Share of the whole machine (all logical cores). Multiprocessing workers are included.
                    usage = Math.Clamp(
                        (sample.Value.Cpu - _lastCpu).TotalSeconds / (Environment.ProcessorCount * elapsed) * 100.0,
                        0,
                        100);
                }

                _lastCpu = sample.Value.Cpu;
                _lastSampleUtc = now;

                Updated?.Invoke(this, new OperationProgress
                {
                    CpuUsage = usage,
                    MemoryUsageBytes = sample.Value.Memory,
                    StatusText = name ?? "process"
                });
            }
            catch
            {
                // Process may have exited.
            }
        }
    }

    /// <summary>
    /// Sums CPU/RAM for the attached process and any siblings with the same ProcessName
    /// (MkPFS PFSC workers are separate processes named <c>mkpfs</c>).
    /// </summary>
    private static (TimeSpan Cpu, long Memory)? SampleTree(int rootPid, ref string? processName)
    {
        DiagProcess[] group;
        try
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                using var root = DiagProcess.GetProcessById(rootPid);
                processName = root.ProcessName;
            }

            group = DiagProcess.GetProcessesByName(processName);
            if (group.Length == 0)
            {
                using var root = DiagProcess.GetProcessById(rootPid);
                root.Refresh();
                return (root.TotalProcessorTime, root.WorkingSet64);
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        var cpu = TimeSpan.Zero;
        long memory = 0;
        foreach (var p in group)
        {
            try
            {
                p.Refresh();
                cpu += p.TotalProcessorTime;
                memory += p.WorkingSet64;
            }
            catch
            {
                // Race: worker exited.
            }
            finally
            {
                p.Dispose();
            }
        }

        return (cpu, memory);
    }
}
