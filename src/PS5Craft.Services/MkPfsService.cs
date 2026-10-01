using System.Diagnostics;
using PS5Craft.Core;
using PS5Craft.Core.Abstractions;
using PS5Craft.Core.Models;
using PS5Craft.Core.Process;
using PS5Craft.Infrastructure;
using PS5Craft.Services.Parsing;
using PS5Craft.Services.Tools;
using ProcessPriorityClass = PS5Craft.Core.Process.ProcessPriorityClass;

namespace PS5Craft.Services;

public sealed class MkPfsService : IMkPfsService
{
    private readonly IExternalProcessRunner _runner;
    private readonly ISettingsService _settings;
    private readonly ILogService _log;
    private readonly IResourceMonitor _monitor;

    private string? _executable;
    private List<string> _prefixArgs = [];

    public MkPfsService(
        IExternalProcessRunner runner,
        ISettingsService settings,
        ILogService log,
        IResourceMonitor monitor)
    {
        _runner = runner;
        _settings = settings;
        _log = log;
        _monitor = monitor;
    }

    public async Task<ToolInfo> DetectAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (exe, prefix) in ToolLocator.CandidateMkPfsInvocations(_settings))
        {
            try
            {
                var args = new List<string>(prefix) { "-V" };
                var result = await _runner.RunAsync(new ProcessStartRequest
                {
                    Executable = exe,
                    Arguments = args,
                    Timeout = TimeSpan.FromSeconds(20),
                    Priority = ProcessPriorityClass.BelowNormal
                }, cancellationToken: cancellationToken).ConfigureAwait(false);

                var text = (result.StdOut + result.StdErr).Trim();
                if (result.ExitCode == 0 || text.Contains("MkPFS", StringComparison.OrdinalIgnoreCase))
                {
                    _executable = exe;
                    _prefixArgs = prefix;
                    var version = ExtractVersion(text);
                    return new ToolInfo
                    {
                        Available = true,
                        Path = DescribeInvocation(exe, prefix),
                        Version = version,
                        Details = text.Split('\n').FirstOrDefault()?.Trim(),
                        DisplayName = "MkPFS"
                    };
                }
            }
            catch
            {
                // try next
            }
        }

        return new ToolInfo
        {
            Available = false,
            DisplayName = "MkPFS",
            Details = "MkPFS not found. Install with: python -m pip install -U mkpfs  — or set path in Settings."
        };
    }

    public async Task<ToolInfo> TestAsync(CancellationToken cancellationToken = default)
    {
        var info = await DetectAsync(cancellationToken).ConfigureAwait(false);
        if (info.Available)
        {
            _log.Success($"MkPFS detected: {info.Version} @ {info.Path}");
        }
        else
        {
            _log.Error(info.Details ?? "MkPFS not found");
        }

        return info;
    }

    public async Task<OperationResult> PackFolderAsync(
        CompressionSettings settings,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        PathValidator.EnsureExistingDirectory(settings.SourceFolder, "Source folder");
        if (string.IsNullOrWhiteSpace(settings.OutputPath))
        {
            return OperationResult.Fail("Не указан путь выходного файла.");
        }

        var detect = await EnsureResolvedAsync(cancellationToken).ConfigureAwait(false);
        if (!detect.Available)
        {
            return OperationResult.Fail("MkPFS не найден.", detect.Details);
        }

        if (settings.Format == OutputFormat.Ffpfs && settings.Compress)
        {
            _log.Warning("Формат FFPFS (--raw) с сжатием: консоль может некорректно читать файлы. Рекомендуется FFPFSC.");
        }

        var leftovers = PackageOnlyFiles.SceSysNames
            .Where(name => File.Exists(Path.Combine(settings.SourceFolder, "sce_sys", name)))
            .ToList();
        if (leftovers.Count > 0)
        {
            _log.Warning(
                $"sce_sys contains package-only files from an FPKG CNT: {string.Join(", ", leftovers)}. " +
                "They are not part of the game file system and may make the image fail to launch. " +
                "Re-extract with «Подготовить для образа» or remove them before packing.");
        }

        var args = BuildPackArguments(settings);
        _log.Info("Starting MkPFS");
        _log.Info($"Version: {detect.Version}");
        _log.Info($"Source: {settings.SourceFolder}");
        _log.Info($"Output: {settings.OutputPath}");
        _log.Info($"Format: {settings.Format}");
        _log.Info($"CPU count: {(settings.CpuCount == 0 ? "Auto" : settings.CpuCount.ToString())}");
        _log.Info($"Compression level: {settings.CompressionLevel}");
        _log.Info($"PS5 mode enabled: {settings.Version}");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settings.OutputPath))!);

        var sw = Stopwatch.StartNew();
        long inputBytes = EstimateFolderSize(settings.SourceFolder);
        var opProgress = new OperationProgress
        {
            Phase = "prepare",
            StatusText = "Подготовка…",
            TotalBytes = inputBytes
        };
        progress?.Report(opProgress);

        var request = new ProcessStartRequest
        {
            Executable = _executable!,
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(settings.SourceFolder)),
            Priority = MapPriority(settings.ProcessPriority)
        };

        var processProgress = new Progress<ProcessOutput>(o =>
        {
            _log.Info(o.Line);
            if (MkPfsProgressParser.TryParse(o.Line, out var parsed))
            {
                parsed.TotalBytes ??= inputBytes;
                parsed.Elapsed = sw.Elapsed;
                if (parsed.Percent is { } pct && inputBytes > 0)
                {
                    parsed.ProcessedBytes = (long)(inputBytes * (pct / 100.0));
                }

                try
                {
                    if (File.Exists(settings.OutputPath))
                    {
                        parsed.OutputBytes = new FileInfo(settings.OutputPath).Length;
                        if (inputBytes > 0 && parsed.OutputBytes > 0)
                        {
                            parsed.CompressionRatioPercent = parsed.OutputBytes.Value * 100.0 / inputBytes;
                        }
                    }
                }
                catch { /* ignore */ }

                progress?.Report(parsed);
            }
            else
            {
                progress?.Report(new OperationProgress
                {
                    Phase = "running",
                    StatusText = o.Line,
                    Elapsed = sw.Elapsed,
                    TotalBytes = inputBytes,
                    Percent = null
                });
            }
        });

        ProcessResult result;
        try
        {
            // Attach monitor after start via wrapper: run and listen for PID from result only —
            // start monitoring by spawning with a custom approach: first line won't have PID.
            // We use a callback by starting then attaching — ExternalProcessRunner returns PID at end.
            // Start monitor after a short delay using a linked task is hard; instead attach when we get ProcessId from a custom start.
            result = await RunWithMonitorAsync(request, processProgress, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(ex.Message);
            return OperationResult.Fail("Сжатие завершилось с ошибкой.", ex.ToString());
        }

        sw.Stop();
        if (result.Cancelled)
        {
            _log.Warning("MkPFS cancelled");
            return OperationResult.Cancelled();
        }

        if (result.TimedOut)
        {
            return OperationResult.Fail("MkPFS превысил время ожидания.", result.StdErr);
        }

        if (!result.Succeeded)
        {
            _log.Error($"MkPFS exit code: {result.ExitCode}");
            return OperationResult.Fail(
                "Сжатие завершилось с ошибкой.",
                $"Exit code: {result.ExitCode}\n\n{result.StdErr}\n{result.StdOut}",
                result.ExitCode);
        }

        long? outputBytes = null;
        if (File.Exists(settings.OutputPath))
        {
            outputBytes = new FileInfo(settings.OutputPath).Length;
        }

        _log.Success("Completed");
        progress?.Report(new OperationProgress
        {
            Percent = 100,
            Phase = "done",
            StatusText = "Сжатие завершено",
            Elapsed = sw.Elapsed,
            ProcessedBytes = inputBytes,
            TotalBytes = inputBytes,
            OutputBytes = outputBytes
        });

        return OperationResult.Ok(
            "✓ Сжатие завершено",
            settings.OutputPath,
            sw.Elapsed,
            inputBytes,
            outputBytes);
    }

    public async Task<OperationResult> VerifyAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        PathValidator.EnsureExistingFile(imagePath, "Image");
        var detect = await EnsureResolvedAsync(cancellationToken).ConfigureAwait(false);
        if (!detect.Available)
        {
            return OperationResult.Fail("MkPFS не найден.", detect.Details);
        }

        var args = new List<string>(_prefixArgs) { "verify", imagePath };
        _log.Info($"Verification started: {imagePath}");
        var result = await _runner.RunAsync(new ProcessStartRequest
        {
            Executable = _executable!,
            Arguments = args,
            Priority = ProcessPriorityClass.BelowNormal
        }, new Progress<ProcessOutput>(o => _log.Info(o.Line)), cancellationToken).ConfigureAwait(false);

        if (result.Cancelled)
        {
            return OperationResult.Cancelled();
        }

        if (!result.Succeeded)
        {
            return OperationResult.Fail("Проверка не удалась.", result.StdErr, result.ExitCode);
        }

        _log.Success("Verification successful");
        return OperationResult.Ok("Verification successful", imagePath, result.Elapsed);
    }

    public async Task<OperationResult> UnpackAsync(string imagePath, string outputDir, bool deep = true, CancellationToken cancellationToken = default)
    {
        PathValidator.EnsureExistingFile(imagePath, "Image");
        Directory.CreateDirectory(outputDir);
        var detect = await EnsureResolvedAsync(cancellationToken).ConfigureAwait(false);
        if (!detect.Available)
        {
            return OperationResult.Fail("MkPFS не найден.", detect.Details);
        }

        var args = new List<string>(_prefixArgs) { "unpack" };
        if (deep)
        {
            args.Add("--deep");
        }

        args.Add(imagePath);
        args.Add(outputDir);
        args.Add("--overwrite");

        var result = await _runner.RunAsync(new ProcessStartRequest
        {
            Executable = _executable!,
            Arguments = args,
            Priority = ProcessPriorityClass.BelowNormal
        }, new Progress<ProcessOutput>(o => _log.Info(o.Line)), cancellationToken).ConfigureAwait(false);

        if (result.Cancelled)
        {
            return OperationResult.Cancelled();
        }

        if (!result.Succeeded)
        {
            return OperationResult.Fail("Распаковка образа не удалась.", result.StdErr, result.ExitCode);
        }

        return OperationResult.Ok("Unpack completed", outputDir, result.Elapsed);
    }

    private List<string> BuildPackArguments(CompressionSettings settings)
    {
        var args = new List<string>(_prefixArgs)
        {
            "pack", "folder"
        };

        if (settings.Format == OutputFormat.Ffpfs)
        {
            args.Add("--raw");
        }

        args.Add("--version");
        args.Add(settings.Version);

        args.Add("--cpu-count");
        args.Add(settings.CpuCount.ToString());

        if (settings.Compress)
        {
            args.Add("--compress");
            args.Add("--compression-level");
            args.Add(settings.CompressionLevel.ToString());
        }
        else
        {
            args.Add("--no-compress");
        }

        if (!string.Equals(settings.BlockSize, "auto", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(settings.BlockSize))
        {
            args.Add("--block-size");
            args.Add(settings.BlockSize);
        }
        else
        {
            args.Add("--block-size");
            args.Add("auto");
        }

        if (settings.SkipExecutableCompression)
        {
            args.Add("--skip-executable-compression");
        }

        var temp = settings.TempFolder ?? _settings.TempDirectory;
        args.Add("--temp-folder");
        args.Add(temp);

        if (settings.Verify)
        {
            args.Add("--verify");
        }

        if (settings.Verbose)
        {
            args.Add("--verbose");
        }

        if (settings.DryRun)
        {
            args.Add("--dry-run");
        }

        // Keep requested extension; do not silently rewrite in our code.
        // Still allow MkPFS adjustment messaging via default adjust flag.
        args.Add(Path.GetFullPath(settings.SourceFolder));
        args.Add(Path.GetFullPath(settings.OutputPath));
        return args;
    }

    private async Task<ProcessResult> RunWithMonitorAsync(
        ProcessStartRequest request,
        IProgress<ProcessOutput> processProgress,
        IProgress<OperationProgress>? opProgress,
        CancellationToken cancellationToken)
    {
        void OnMonitor(object? _, OperationProgress p)
        {
            opProgress?.Report(new OperationProgress
            {
                CpuUsage = p.CpuUsage,
                MemoryUsageBytes = p.MemoryUsageBytes,
                StatusText = p.StatusText,
                Phase = "monitor"
            });
        }

        _monitor.Updated += OnMonitor;
        await _monitor.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // ExternalProcessRunner doesn't expose PID mid-flight; poll by process name as fallback after start.
            var runTask = _runner.RunAsync(request, processProgress, cancellationToken);
            // Best-effort: find child shortly after start
            _ = Task.Run(async () =>
            {
                for (var i = 0; i < 50 && !cancellationToken.IsCancellationRequested; i++)
                {
                    try
                    {
                        var procs = System.Diagnostics.Process.GetProcessesByName("mkpfs")
                            .Concat(System.Diagnostics.Process.GetProcessesByName("python"))
                            .Concat(System.Diagnostics.Process.GetProcessesByName("python3"));
                        var newest = procs.OrderByDescending(p =>
                        {
                            try { return p.StartTime; } catch { return DateTime.MinValue; }
                        }).FirstOrDefault();
                        if (newest != null)
                        {
                            _monitor.Attach(newest.Id);
                            break;
                        }
                    }
                    catch { /* ignore */ }

                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                }
            }, cancellationToken);

            var result = await runTask.ConfigureAwait(false);
            if (result.ProcessId is { } pid)
            {
                _monitor.Attach(pid);
            }

            return result;
        }
        finally
        {
            _monitor.Updated -= OnMonitor;
            _monitor.Detach();
            await _monitor.StopAsync().ConfigureAwait(false);
        }
    }

    private async Task<ToolInfo> EnsureResolvedAsync(CancellationToken cancellationToken)
    {
        if (_executable != null)
        {
            return new ToolInfo { Available = true, Path = DescribeInvocation(_executable, _prefixArgs), Version = "?" };
        }

        return await DetectAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ExtractVersion(string text)
    {
        var line = text.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Contains("MkPFS", StringComparison.OrdinalIgnoreCase)) ?? text;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : line.Trim();
    }

    private static string DescribeInvocation(string exe, List<string> prefix) =>
        prefix.Count == 0 ? exe : $"{exe} {string.Join(' ', prefix)}";

    private static ProcessPriorityClass MapPriority(ProcessPriorityChoice choice) => choice switch
    {
        ProcessPriorityChoice.Low => ProcessPriorityClass.Idle,
        ProcessPriorityChoice.Normal => ProcessPriorityClass.Normal,
        _ => ProcessPriorityClass.BelowNormal
    };

    private static long EstimateFolderSize(string folder)
    {
        try
        {
            return new DirectoryInfo(folder)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f =>
                {
                    try { return f.Length; } catch { return 0L; }
                });
        }
        catch
        {
            return 0;
        }
    }
}
