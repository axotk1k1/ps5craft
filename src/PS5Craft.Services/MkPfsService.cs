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

    /// <summary>
    /// PFSC block compressor: stdlib <c>zlib</c>.
    /// The PS5 decompresses PFSC blocks in hardware (ZCN inflater), which rejects some streams that
    /// software zlib accepts, so <c>mkpfs verify</c> passes but the game crashes right after launch.
    /// MkPFS issue #132 reproduced this on PPSA11386 (007 First Light): the <c>auto</c>-selected ISA-L
    /// backend failed, and only <c>--compression-backend zlib</c> was confirmed to fix it. Our own
    /// zlib-ng level-9 image of the same dump failed the same way, so stdlib zlib is the one backend
    /// with a known-good result on this title. Slightly slower, smallest output.
    /// </summary>
    private const string CompressionBackend = "zlib";

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

        WarnPackageOnlyLeftovers(settings.SourceFolder);

        var args = BuildPackArguments(settings);
        if (settings.Format == OutputFormat.Ffpfsc)
        {
            // Same pipeline as PS5 FFPFSC PRO v1.3.0: one `pack folder` → exFAT-wrapped .ffpfsc.
            // Explicit pack exfat → pack file produced images that failed to launch on console.
            _log.Info(
                $"FFPFSC: single-pass (pack folder → .ffpfsc)" +
                $"{(settings.Compress ? " со сжатием PFSC" : " без сжатия")} — как PS5 FFPFSC PRO 1.3.0.");
        }

        _log.Info("Starting MkPFS");
        _log.Info($"Version: {detect.Version}");
        _log.Info($"Source: {settings.SourceFolder}");
        _log.Info($"Output: {settings.OutputPath}");
        _log.Info($"Format: {settings.Format}");
        _log.Info($"Compress: {settings.Compress}");
        _log.Info("MkPFS: " + FormatArgs(args));
        if (settings.Format != OutputFormat.Exfat)
        {
            _log.Info($"CPU count: {(settings.CpuCount == 0 ? "Auto" : settings.CpuCount.ToString())}");
            if (settings.Compress)
            {
                _log.Info($"Compression level: {settings.CompressionLevel}");
                _log.Info($"Compression backend: {CompressionBackend} (stdlib; единственный бэкенд с подтверждённым запуском PPSA11386 — MkPFS issue #132, аппаратный инфлятор PS5 отвергает блоки isal)");
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
    /// Slices are absolute ranges (not cumulative offsets of unseen phases) so a long
    /// <c>compress</c> phase does not start the UI at ~30%.
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

        // Absolute [start, end) fractions of the overall bar for each phase.
        // Single-pass pack folder spends almost all wall time in "Compressing inner exFAT".
        // Absolute ranges. Compress starts at 0 — single-pass often jumps straight here.
        (string Name, double Start, double End)[] ranges = compressed
            ? [
                ("scan", 0.00, 0.03),
                ("read", 0.03, 0.06),
                ("exfat", 0.06, 0.10),
                ("compress", 0.00, 0.88),
                ("write", 0.88, 1.00)
              ]
            : [
                ("scan", 0.00, 0.04),
                ("exfat", 0.04, 0.96),
                ("write", 0.96, 1.00)
              ];

        foreach (var (name, start, end) in ranges)
        {
            if (phase.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                var local = start + (end - start) * clamped;
                return rangeStart + span * local;
            }
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
                    var clampedRaw = Math.Clamp(pct, 0, 100);
                    parsed.Percent = ScalePhasePercent(parsed.Phase, clampedRaw, rangeStart, rangeEnd, phaseCompressed);
                    // Byte counter follows the current MkPFS phase (0–100), not the scaled overall bar.
                    // Otherwise compress@0% with a 30% bar offset showed ~30 GB instantly.
                    if (inputBytes > 0)
                    {
                        parsed.ProcessedBytes = (long)(inputBytes * (clampedRaw / 100.0));
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

        // FFPFSC: MkPFS default = single-pass exFAT-wrapped .ffpfsc (PS5 FFPFSC PRO v1.3.0).
        // FFPFS: --raw = direct PFS (console compatibility warning when compressed).
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

        // Explicit like PRO; MkPFS default is already 32.
        args.Add("--inode-bits");
        args.Add("32");

        // Like AIO/PRO: let MkPFS auto-size the worker pool (min(16, cores-1)) unless the user
        // explicitly picked a core count in settings.
        if (settings.CpuCount > 0)
        {
            args.Add("--cpu-count");
            args.Add(settings.CpuCount.ToString());
        }

        // Preflight: sce_sys/param.json with titleId + eboot.bin must exist (same as AIO 1.3.0).
        args.Add("--require-game-files");

        if (settings.Compress)
        {
            args.Add("--compress");
            args.Add("--compression-level");
            args.Add(settings.CompressionLevel.ToString());

            // Never leave this to `auto`: bundled MkPFS 1.0.0 picks ISA-L, whose blocks the PS5
            // hardware inflater rejects (issue #132, reproduced on this very title). See the
            // CompressionBackend doc comment for why stdlib zlib rather than zlib-ng.
            args.Add("--compression-backend");
            args.Add(CompressionBackend);

            // MkPFS default (0): keep any block that shrinks → closer to ~35–37% savings.
            // PRO uses 5 (stricter → larger files ~30%). Layout stays single-pass pack folder.
            if (settings.Format == OutputFormat.Ffpfsc)
            {
                args.Add("--threshold-gain");
                args.Add("0");
            }
        }
        else
        {
            args.Add("--no-compress");
        }

        // PRO only forwards block-size when it is not "auto".
        if (!string.Equals(settings.BlockSize, "auto", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(settings.BlockSize))
        {
            args.Add("--block-size");
            args.Add(settings.BlockSize);
        }

        if (settings.SkipExecutableCompression)
        {
            args.Add("--skip-executable-compression");
        }

        var temp = settings.TempFolder ?? _settings.TempDirectory;
        if (!string.IsNullOrWhiteSpace(temp))
        {
            args.Add("--temp-folder");
            args.Add(temp);
        }

        // PRO leaves structure verify on by default and only adds --verify when requested.
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
