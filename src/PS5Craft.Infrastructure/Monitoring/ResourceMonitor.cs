using System.Diagnostics;
using PS5Craft.Core.Abstractions;
using PS5Craft.Core.Models;

namespace PS5Craft.Infrastructure.Monitoring;

public sealed class ResourceMonitor : IResourceMonitor
{
    private readonly object _gate = new();
    private int? _processId;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private TimeSpan _lastCpu;
    private DateTime _lastSampleUtc = DateTime.UtcNow;

    public event EventHandler<OperationProgress>? Updated;

    public void Attach(int processId)
    {
        lock (_gate)
        {
            _processId = processId;
            _lastCpu = TimeSpan.Zero;
            _lastSampleUtc = DateTime.UtcNow;
        }
    }

    public void Detach()
    {
        lock (_gate)
        {
            _processId = null;
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
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            int? pid;
            lock (_gate) pid = _processId;
            if (pid is null)
            {
                continue;
            }

            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid.Value);
                process.Refresh();
                var now = DateTime.UtcNow;
                var cpu = process.TotalProcessorTime;
                var elapsed = (now - _lastSampleUtc).TotalSeconds;
                double? usage = null;
                if (elapsed > 0.05 && _lastCpu != TimeSpan.Zero)
                {
                    usage = Math.Clamp(
                        (cpu - _lastCpu).TotalSeconds / (Environment.ProcessorCount * elapsed) * 100.0,
                        0,
                        100);
                }

                _lastCpu = cpu;
                _lastSampleUtc = now;

                Updated?.Invoke(this, new OperationProgress
                {
                    CpuUsage = usage,
                    MemoryUsageBytes = process.WorkingSet64,
                    StatusText = process.ProcessName
                });
            }
            catch
            {
                // Process may have exited.
            }
        }
    }
}
