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

        if (settings.Format == OutputFormat.Exfat)
        {
            _log.Info("Формат EXFAT: сырой образ без PFSC; содержимое папки (включая fakelib/AMPR) копируется как есть.");
            if (settings.Compress)
            {
                _log.Info("Параметры сжатия PFSC для EXFAT не применяются — используйте FFPFSC или pack file поверх .exfat.");
            }
        }

        // FFPFSC: two-pass exFAT → pack file (MkPFS Option 1 — maximum console compatibility).
        // Nested raw PFS + PFSC (Option 5) verifies locally but often dies at splash when compressed.
        if (settings.Format == OutputFormat.Ffpfsc)
        {
            return await PackFfpfscTwoPassAsync(settings, detect, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        WarnPackageOnlyLeftovers(settings.SourceFolder);

        var args = BuildPackArguments(settings);
        _log.Info("Starting MkPFS");
        _log.Info($"Version: {detect.Version}");
        _log.Info($"Source: {settings.SourceFolder}");
        _log.Info($"Output: {settings.OutputPath}");
        _log.Info($"Format: {settings.Format}");
        _log.Info($"Compress: {settings.Compress}");
        if (settings.Format != OutputFormat.Exfat)
        {
            _log.Info($"CPU count: {(settings.CpuCount == 0 ? "Auto" : settings.CpuCount.ToString())}");
            if (settings.Compress)
            {
                _log.Info($"Compression level: {settings.CompressionLevel}");
            }

            _log.Info($"PS5 mode enabled: {settings.Version}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settings.OutputPath))!);

        var sw = Stopwatch.StartNew();
        long inputBytes = EstimateFolderSize(settings.SourceFolder);
        progress?.Report(new OperationProgress
        {
            Phase = "prepare",
            StatusText = "Подготовка…",
            TotalBytes = inputBytes
        });

        try
        {
            if (settings.Format == OutputFormat.Exfat)
            {
                await EnsureAmprIndexIfNeededAsync(settings.SourceFolder, cancellationToken).ConfigureAwait(false);
            }

            var result = await RunPackProcessAsync(settings, args, inputBytes, progress, sw, cancellationToken)
                .ConfigureAwait(false);
            return FinishPack(settings, result, inputBytes, sw);
        }
        catch (Exception ex)
        {
            _log.Error(ex.Message);
            return OperationResult.Fail("Сжатие завершилось с ошибкой.", ex.ToString());
        }
    }

    /// <summary>
    /// Build <c>.ffpfsc</c> the way MkPFS recommends for console backups: uncompressed exFAT of the
    /// game folder, then <c>pack file</c> (optional PFSC). Nested raw PFS + PFSC (Option 5) often
    /// passes local verify but fails at splash on compressed titles; exFAT→FFPFSC matches the layout
    /// that already works when the user picks EXFAT alone.
    /// </summary>
    private async Task<OperationResult> PackFfpfscTwoPassAsync(
        CompressionSettings settings,
        ToolInfo detect,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        WarnPackageOnlyLeftovers(settings.SourceFolder);

        var outputFull = Path.GetFullPath(settings.OutputPath);
        var outputDir = Path.GetDirectoryName(outputFull)!;
        Directory.CreateDirectory(outputDir);

        // Same volume as the final image; renamed away before background delete.
        var innerExfat = Path.Combine(
            outputDir,
            Path.GetFileNameWithoutExtension(outputFull) + ".inner.exfat");

        var sw = Stopwatch.StartNew();
        long inputBytes = EstimateFolderSize(settings.SourceFolder);

        _log.Info(
            $"FFPFSC: двухпроходная сборка (pack exfat → pack file)" +
            $"{(settings.Compress ? " со сжатием PFSC" : " без сжатия")} — максимальная совместимость.");
        _log.Info("Starting MkPFS (FFPFSC two-pass wrapper)");
        _log.Info($"Version: {detect.Version}");
        _log.Info($"Source: {settings.SourceFolder}");
        _log.Info($"Output: {settings.OutputPath}");
        _log.Info($"Inner exFAT: {innerExfat}");
        _log.Info("Format: Ffpfsc");
        _log.Info($"Compress: {settings.Compress}");
        if (settings.Compress)
        {
            _log.Info($"CPU count: {(settings.CpuCount == 0 ? "Auto" : settings.CpuCount.ToString())}");
            _log.Info($"Compression level: {settings.CompressionLevel}");
            _log.Info("Compression backend: auto");
        }

        WarnLowDiskSpace(outputDir, inputBytes);

        progress?.Report(new OperationProgress
        {
            Phase = "inner",
            StatusText = "[1/2] Создание образа exFAT…",
            TotalBytes = inputBytes,
            Percent = 0
        });

        try
        {
            TryDeleteFile(innerExfat);
            ScheduleStaleTempCleanup(outputDir);

            await EnsureAmprIndexIfNeededAsync(settings.SourceFolder, cancellationToken)
                .ConfigureAwait(false);

            var innerSettings = new CompressionSettings
            {
                SourceFolder = settings.SourceFolder,
                OutputPath = innerExfat,
                Format = OutputFormat.Exfat,
                Compress = false,
                Verbose = settings.Verbose,
                ProcessPriority = settings.ProcessPriority,
                TempFolder = settings.TempFolder,
                Version = settings.Version
            };
            var innerArgs = BuildExfatPackArguments(innerSettings);
            _log.Info("MkPFS [1/2]: " + FormatArgs(innerArgs));
            var innerResult = await RunPackProcessAsync(
                    innerSettings, innerArgs, inputBytes, progress, sw, cancellationToken,
                    rangeStart: 0, rangeEnd: settings.Compress ? 45 : 90, compressed: false)
                .ConfigureAwait(false);
            if (!innerResult.Succeeded || innerResult.Cancelled || innerResult.TimedOut)
            {
                return FinishPack(settings, innerResult, inputBytes, sw, failureLabel: "Упаковка");
            }

            if (!File.Exists(innerExfat))
            {
                return OperationResult.Fail(
                    "Упаковка завершилась с ошибкой.",
                    $"Внутренний exFAT-образ не создан: {innerExfat}");
            }

            TryDeleteFile(settings.OutputPath);

            progress?.Report(new OperationProgress
            {
                Phase = "wrap",
                StatusText = settings.Compress
                    ? "[2/2] Сжатие обёртки PFSC (pack file)…"
                    : "[2/2] Обёртка без сжатия (pack file)…",
                TotalBytes = inputBytes
            });

            var wrapArgs = BuildFfpfscFromFileArguments(settings, innerExfat);
            _log.Info("MkPFS [2/2]: " + FormatArgs(wrapArgs));
            var wrapSettings = new CompressionSettings
            {
                SourceFolder = settings.SourceFolder,
                OutputPath = settings.OutputPath,
                Format = OutputFormat.Ffpfsc,
                Compress = settings.Compress,
                ProcessPriority = settings.ProcessPriority
            };
            var wrapResult = await RunPackProcessAsync(
                    wrapSettings, wrapArgs, inputBytes, progress, sw, cancellationToken,
                    rangeStart: settings.Compress ? 45 : 90, rangeEnd: 100, compressed: settings.Compress)
                .ConfigureAwait(false);
            return FinishPack(
                settings,
                wrapResult,
                inputBytes,
                sw,
                failureLabel: "Упаковка",
                doneText: settings.Compress ? null : "Упаковка завершена");
        }
        catch (Exception ex)
        {
            _log.Error(ex.Message);
            return OperationResult.Fail("Упаковка завершилась с ошибкой.", ex.ToString());
        }
        finally
        {
            ScheduleLargeTempCleanup(innerExfat);
        }
    }

    private void WarnLowDiskSpace(string outputDir, long inputBytes)
    {
        if (inputBytes <= 0)
        {
            return;
        }

        try
        {
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(outputDir))!).AvailableFreeSpace;
            // Peak usage: uncompressed inner image (~source size) + the final compressed image.
            var needed = inputBytes + (long)(inputBytes * 0.75);
            if (free < needed)
            {
                _log.Warning(
                    $"Мало места на диске: свободно {free / 1024 / 1024 / 1024.0:0.0} GB, " +
                    $"двухпроходной упаковке нужно примерно {needed / 1024 / 1024 / 1024.0:0.0} GB " +
                    "(несжатый образ + итоговый .ffpfsc одновременно).");
            }
        }
        catch
        {
            // free-space probing is advisory only
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    /// <summary>
    /// Frees the canonical temp name immediately, then deletes the (possibly huge) file off the UI path.
    /// </summary>
    private void ScheduleLargeTempCleanup(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        string pending = path;
        try
        {
            pending = path + ".deleting-" + Guid.NewGuid().ToString("N");
            File.Move(path, pending);
        }
        catch
        {
            // If rename fails (locked / cross-volume), still attempt a background delete of the original.
            pending = path;
        }

        _log.Info("[3/3] Удаление временного образа в фоне…");
        DeleteLargeFileInBackground(pending, announceSuccess: true);
    }

    private void ScheduleStaleTempCleanup(string outputDir)
    {
        try
        {
            foreach (var leftover in Directory.EnumerateFiles(outputDir, "pfs_image.dat.deleting-*")
                         .Concat(Directory.EnumerateFiles(outputDir, "*.inner.exfat.deleting-*")))
            {
                DeleteLargeFileInBackground(leftover, announceSuccess: false);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private void DeleteLargeFileInBackground(string target, bool announceSuccess)
    {
        _ = Task.Run(() =>
        {
            try
            {
                // Truncate first so free space returns sooner; Delete then finishes the directory entry.
                try
                {
                    using var fs = new FileStream(
                        target, FileMode.Open, FileAccess.Write, FileShare.None, 1, FileOptions.None);
                    fs.SetLength(0);
                }
                catch
                {
                    // Fall through to Delete even if truncate is denied.
                }

                File.Delete(target);
                if (announceSuccess)
                {
                    _log.Info("Временный образ удалён.");
                }
            }
            catch (Exception ex)
            {
                _log.Warning($"Не удалось удалить временный образ ({Path.GetFileName(target)}): {ex.Message}");
            }
        });
    }

    /// <summary>
    /// <c>pack exfat</c> does not call MkPFS <c>ensure_ampr_index</c> (<c>pack folder</c> does), so generate it here.
    /// </summary>
    private async Task EnsureAmprIndexIfNeededAsync(string sourceFolder, CancellationToken cancellationToken)
    {
        var status = AmprEmulator.Inspect(sourceFolder, cancellationToken);
        if (!status.ModulePresent)
        {
            return;
        }

        var indexPath = Path.Combine(sourceFolder, AmprEmulator.IndexName);
        if (File.Exists(indexPath))
        {
            _log.Info($"AMPR: найден {AmprEmulator.IndexName}, оставляем как есть перед pack exfat.");
            return;
        }

        // Prefer the same Python interpreter that hosts `python -m mkpfs`.
        var python = ResolvePythonForMkPfsModule();
        if (python is null)
        {
            _log.Warning(
                $"AMPR: {AmprEmulator.IndexName} отсутствует, а Python/mkpfs module недоступен для генерации. " +
                "Для AMPR+FFPFSC надёжнее формат EXFAT.");
            return;
        }

        var code =
            "from pathlib import Path; " +
            "from mkpfs.ampr import ensure_ampr_index; " +
            $"ensure_ampr_index(Path(r'{Path.GetFullPath(sourceFolder)}'))";

        _log.Info($"AMPR: {AmprEmulator.IndexName} отсутствует — генерируем через mkpfs.ampr перед pack exfat…");
        try
        {
            var request = new ProcessStartRequest
            {
                Executable = python,
                Arguments = ["-c", code],
                WorkingDirectory = Path.GetFullPath(sourceFolder)
            };
            var result = await _runner.RunAsync(
                    request,
                    new Progress<ProcessOutput>(o => _log.Info(o.Line)),
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Succeeded && File.Exists(indexPath))
            {
                _log.Success($"AMPR: создан {AmprEmulator.IndexName}");
            }
            else
            {
                _log.Warning(
                    $"AMPR: не удалось создать {AmprEmulator.IndexName} (exit {result.ExitCode}). " +
                    "На консоли эмулятор попробует runtime scan; для FFPFSC надёжнее EXFAT.");
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"AMPR: генерация индекса не выполнена: {ex.Message}");
        }
    }

    private string? ResolvePythonForMkPfsModule()
    {
        if (_prefixArgs.Count >= 2 &&
            string.Equals(_prefixArgs[0], "-m", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_prefixArgs[1], "mkpfs", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(_executable))
        {
            return _executable;
        }

        var configured = _settings.Current.PythonPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return "python";
    }

    private void WarnPackageOnlyLeftovers(string sourceFolder)
    {
        var leftovers = PackageOnlyFiles.SceSysNames
            .Where(name => File.Exists(Path.Combine(sourceFolder, "sce_sys", name)))
            .ToList();
        if (leftovers.Count == 0)
        {
            return;
        }

        _log.Warning(
            $"sce_sys contains package-only files from an FPKG CNT: {string.Join(", ", leftovers)}. " +
            "They are not part of the game file system and may make the image fail to launch. " +
            "Re-extract with «Подготовить для образа» or remove them before packing.");
    }

    /// <summary>
    /// MkPFS prints each phase (scan, compress, write, exfat, …) as its own 0–100%. Mapping those
    /// raw values straight onto the bar makes it jump to 100% when an early phase finishes.
    /// Each known phase owns a slice of <paramref name="rangeStart"/>–<paramref name="rangeEnd"/>;
    /// unknown phases (e.g. <c>exfat</c> on pass 1) still map linearly into that range so the bar moves.
    /// </summary>
    private static double? ScalePhasePercent(string? phase, double percent, double rangeStart, double rangeEnd, bool compressed)
    {
        if (string.IsNullOrWhiteSpace(phase))
        {
            return null;
        }

        // Verify drives the violet overlay from the raw 0–100 value.
        if (MkPfsProgressParser.IsVerifyPhase(phase))
        {
            return null;
        }

        var span = rangeEnd - rangeStart;
        var clamped = Math.Clamp(percent, 0, 100) / 100.0;

        // Uncompressed / inner passes: pack exfat is a single dominant phase.
        // Compressed wrap: classic scan → compress → write.
        (string Name, double Weight)[] slices = compressed
            ? [("scan", 0.04), ("compress", 0.72), ("write", 0.24)]
            : [("scan", 0.04), ("exfat", 0.92), ("write", 0.04)];

        var cursor = 0.0;
        foreach (var (name, weight) in slices)
        {
            if (phase.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                var local = cursor + weight * clamped;
                return rangeStart + span * local;
            }

            cursor += weight;
        }

        // Fallback (any other MkPFS phase token): use the whole assigned range.
        return rangeStart + span * clamped;
    }

    private async Task<ProcessResult> RunPackProcessAsync(
        CompressionSettings settings,
        List<string> args,
        long inputBytes,
        IProgress<OperationProgress>? progress,
        Stopwatch sw,
        CancellationToken cancellationToken,
        double rangeStart = 0,
        double rangeEnd = 100,
        bool? compressed = null)
    {
        var phaseCompressed = compressed ?? settings.Compress;
        var request = new ProcessStartRequest
        {
            Executable = _executable!,
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(settings.SourceFolder)),
            Priority = MapPriority(settings.ProcessPriority)
        };

        var lastLoggedPhase = string.Empty;
        var lastLoggedBucket = -1;
        var processProgress = new Progress<ProcessOutput>(o =>
        {
            if (MkPfsProgressParser.TryParse(o.Line, out var parsed))
            {
                parsed.TotalBytes ??= inputBytes;
                parsed.Elapsed = sw.Elapsed;
                var rawPhase = parsed.Phase ?? string.Empty;
                var rawPct = parsed.Percent;

                if (MkPfsProgressParser.IsVerifyPhase(rawPhase))
                {
                    // Full --verify is two passes (verify, then compare). Scale into one 0–100 bar,
                    // but keep the status/size lines on the current pass so they match each other.
                    var isCompare = rawPhase.Contains("compare", StringComparison.OrdinalIgnoreCase)
                                    || rawPhase.Contains("сравнен", StringComparison.OrdinalIgnoreCase);
                    double? overallPct = null;
                    if (rawPct is { } rawVerify)
                    {
                        var clamped = Math.Clamp(rawVerify, 0, 100);
                        overallPct = (isCompare ? 50.0 : 0.0) + 50.0 * (clamped / 100.0);
                        parsed.Percent = clamped;
                        if (inputBytes > 0)
                        {
                            parsed.ProcessedBytes = (long)(inputBytes * (clamped / 100.0));
                        }
                    }

                    parsed.Phase = isCompare ? "compare" : "verify";
                    MkPfsProgressParser.AlignStatusWithPercent(parsed);
                    if (overallPct is { } scaled)
                    {
                        parsed.Percent = scaled;
                    }
                }
                else if (rawPct is { } pct)
                {
                    parsed.Percent = ScalePhasePercent(parsed.Phase, pct, rangeStart, rangeEnd, phaseCompressed);
                    if (parsed.Percent is { } scaledPct && inputBytes > 0)
                    {
                        parsed.ProcessedBytes = (long)(inputBytes * (scaledPct / 100.0));
                    }

                    MkPfsProgressParser.AlignStatusWithPercent(parsed);
                }
                else
                {
                    MkPfsProgressParser.AlignStatusWithPercent(parsed);
                }

                // Progress ticks arrive many times per second — keep the log readable.
                var bucket = parsed.Percent is { } p ? (int)(p / 2) : -1;
                var phaseKey = parsed.Phase ?? string.Empty;
                if (phaseKey != lastLoggedPhase || bucket != lastLoggedBucket)
                {
                    lastLoggedPhase = phaseKey;
                    lastLoggedBucket = bucket;
                    _log.Info(parsed.StatusText ?? o.Line);
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
                catch
                {
                    // ignore
                }

                progress?.Report(parsed);
            }
            else
            {
                _log.Info(o.Line);
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

        return await RunWithMonitorAsync(request, processProgress, progress, cancellationToken).ConfigureAwait(false);
    }

    private OperationResult FinishPack(
        CompressionSettings settings,
        ProcessResult result,
        long inputBytes,
        Stopwatch sw,
        string failureLabel = "Сжатие",
        string? doneText = null)
    {
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
            var reason = ExtractMkPfsFailure(result);
            if (!string.IsNullOrWhiteSpace(reason))
            {
                _log.Error(reason);
            }

            return OperationResult.Fail(
                failureLabel + " завершилось с ошибкой.",
                $"Exit code: {result.ExitCode}\n\n{reason}\n\n{result.StdErr}\n{result.StdOut}",
                result.ExitCode);
        }

        long? outputBytes = File.Exists(settings.OutputPath)
            ? new FileInfo(settings.OutputPath).Length
            : null;

        doneText ??= settings.Format == OutputFormat.Exfat || !settings.Compress
            ? "Упаковка завершена"
            : "Сжатие завершено";
        _log.Success("Completed");
        return OperationResult.Ok(
            "✓ " + doneText,
            settings.OutputPath,
            sw.Elapsed,
            inputBytes,
            outputBytes);
    }

    /// <summary>Pull the MkPFS error out of a stderr stream that is mostly progress bars.</summary>
    private static string ExtractMkPfsFailure(ProcessResult result)
    {
        var text = (result.StdErr ?? string.Empty) + "\n" + (result.StdOut ?? string.Empty);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var interesting = lines.Where(line =>
                line.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Traceback", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("cannot be used", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Exception", StringComparison.OrdinalIgnoreCase))
            .TakeLast(6)
            .ToList();
        return interesting.Count == 0 ? string.Empty : string.Join(Environment.NewLine, interesting);
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
        if (settings.Format == OutputFormat.Exfat)
        {
            return BuildExfatPackArguments(settings);
        }

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

        args.Add(Path.GetFullPath(settings.SourceFolder));
        args.Add(Path.GetFullPath(settings.OutputPath));
        return args;
    }

    private List<string> BuildExfatPackArguments(CompressionSettings settings)
    {
        var args = new List<string>(_prefixArgs)
        {
            "pack", "exfat",
            "--cluster-size", "65536"
        };

        if (settings.Verbose)
        {
            args.Add("--verbose");
        }

        args.Add("--overwrite");
        args.Add(Path.GetFullPath(settings.SourceFolder));
        args.Add(Path.GetFullPath(settings.OutputPath));
        return args;
    }

    /// <summary>Step 1 of the FFPFSC wrapper flow: uncompressed inner PFS image of the game folder.</summary>
    private List<string> BuildUncompressedInnerPfsArguments(CompressionSettings settings)
    {
        // Matches MkPFS Option 5 / the working ps5_pack.sh recipe. --no-verify-structure is required
        // with --skip-verification on MkPFS 1.0.x (otherwise it exits 1 after a successful write).
        var args = new List<string>(_prefixArgs)
        {
            "pack", "folder",
            "--raw",
            "--no-compress",
            "--no-adjust-output-file-extension",
            "--skip-verification",
            "--no-verify-structure",
            "--version", "PS5",
            "--inode-bits", "32"
        };

        args.Add(Path.GetFullPath(settings.SourceFolder));
        args.Add(Path.GetFullPath(settings.OutputPath));
        return args;
    }

    /// <summary>Step 2 of the FFPFSC wrapper flow: wrap/compress one image file into the container.</summary>
    private List<string> BuildFfpfscFromFileArguments(CompressionSettings settings, string innerImagePath)
    {
        var args = new List<string>(_prefixArgs)
        {
            "pack", "file",
            "--version", "PS5",
            "--inode-bits", "32"
        };

        if (settings.Compress)
        {
            args.Add("--compress");
            args.Add("--compression-backend");
            args.Add("auto");
            args.Add("--compression-level");
            args.Add(settings.CompressionLevel.ToString());
            args.Add("--cpu-count");
            args.Add(settings.CpuCount.ToString());
        }
        else
        {
            args.Add("--no-compress");
        }

        // MkPFS rejects --verify together with --skip-verification, and --skip-verification
        // together with the default --verify-structure.
        if (settings.Verify)
        {
            args.Add("--verify");
        }
        else
        {
            args.Add("--skip-verification");
            args.Add("--no-verify-structure");
        }

        args.Add(Path.GetFullPath(innerImagePath));
        args.Add(Path.GetFullPath(settings.OutputPath));
        return args;
    }

    private static string FormatArgs(IReadOnlyList<string> args) =>
        string.Join(" ", args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));

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

        // Attach as soon as the child starts — waiting until WaitForExit returns is too late.
        var monitored = request;
        if (monitored.OnStarted is null)
        {
            monitored = new ProcessStartRequest
            {
                Executable = request.Executable,
                Arguments = request.Arguments,
                WorkingDirectory = request.WorkingDirectory,
                Environment = request.Environment,
                Timeout = request.Timeout,
                Priority = request.Priority,
                ProcessorAffinity = request.ProcessorAffinity,
                OnStarted = pid => _monitor.Attach(pid)
            };
        }
        else
        {
            var prior = monitored.OnStarted;
            monitored = new ProcessStartRequest
            {
                Executable = request.Executable,
                Arguments = request.Arguments,
                WorkingDirectory = request.WorkingDirectory,
                Environment = request.Environment,
                Timeout = request.Timeout,
                Priority = request.Priority,
                ProcessorAffinity = request.ProcessorAffinity,
                OnStarted = pid =>
                {
                    _monitor.Attach(pid);
                    prior(pid);
                }
            };
        }

        _monitor.Updated += OnMonitor;
        await _monitor.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _runner.RunAsync(monitored, processProgress, cancellationToken).ConfigureAwait(false);
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
