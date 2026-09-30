using System.Diagnostics;
using System.Text.Json;
using PS5Craft.Core;
using PS5Craft.Core.Abstractions;
using PS5Craft.Core.Models;
using PS5Craft.Core.Process;
using PS5Craft.Infrastructure;
using PS5Craft.Services.Parsing;
using PS5Craft.Services.Tools;
using ProcessPriorityClass = PS5Craft.Core.Process.ProcessPriorityClass;
using ViethoaInspector = PsViethoa.FpkgBuilder.Core.Services.PackageInspector;

namespace PS5Craft.Services;

public sealed class FpkgService : IFpkgService
{
    private readonly IExternalProcessRunner _runner;
    private readonly ISettingsService _settings;
    private readonly ILogService _log;
    private readonly IResourceMonitor _monitor;
    private string? _cliPath;

    public FpkgService(
        IExternalProcessRunner runner,
        ISettingsService settings,
        ILogService log,
        IResourceMonitor monitor)
    {
        _runner = runner;
        _settings = settings;
        _log = log;
        _monitor = monitor;
        PsViethoa.FpkgBuilder.Core.Localization.Loc.Current.SetLanguage("en");
    }

    public Task<ToolInfo> DetectAsync(CancellationToken cancellationToken = default)
    {
        var path = ToolLocator.FirstExisting(ToolLocator.CandidateFpkgCliPaths(_settings));
        if (path is null)
        {
            // Prefer launching via `dotnet tools/fpkg/fpkg-cli.dll`
            var dll = ToolLocator.CandidateFpkgCliPaths(_settings)
                .Select(p => Path.ChangeExtension(p, ".dll"))
                .FirstOrDefault(File.Exists);
            if (dll != null)
            {
                _cliPath = dll;
                return Task.FromResult(new ToolInfo
                {
                    Available = true,
                    Path = dll,
                    Version = "fpkg-cli (dotnet)",
                    DisplayName = "fpkg-cli",
                    Details = "PSVIETHOA fpkg-cli via dotnet host"
                });
            }

            return Task.FromResult(new ToolInfo
            {
                Available = false,
                DisplayName = "fpkg-cli",
                Details = "fpkg-cli not found under tools/fpkg. Build PSVIETHOA Cli into tools/fpkg."
            });
        }

        _cliPath = path;
        return Task.FromResult(new ToolInfo
        {
            Available = true,
            Path = path,
            Version = "fpkg-cli",
            DisplayName = "fpkg-cli",
            Details = path
        });
    }

    public async Task<GameInfo> InspectAsync(string packagePath, string? passcode = null, CancellationToken cancellationToken = default)
    {
        packagePath = PathValidator.Normalize(packagePath);
        if (!File.Exists(packagePath))
        {
            var missing = GameInfo.Unknown(packagePath);
            missing.Error = "Файл не найден";
            return missing;
        }

        // Fast in-process inspect (metadata only) using PSVIETHOA PackageInspector —
        // not a multi-GB extract. Runs off UI via caller Task.
        return await Task.Run(() =>
        {
            try
            {
                passcode ??= new string('0', 32);
                var info = ViethoaInspector.Inspect(packagePath, passcode, cancellationToken);
                return MapPackageInfo(info);
            }
            catch (Exception ex)
            {
                var g = GameInfo.Unknown(packagePath);
                g.Error = ex.Message;
                g.FileSizeBytes = TryFileSize(packagePath);
                return g;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> ExtractAsync(
        ExtractionSettings settings,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var detect = await DetectAsync(cancellationToken).ConfigureAwait(false);
        if (!detect.Available || _cliPath is null)
        {
            return OperationResult.Fail("fpkg-cli не найден.", detect.Details);
        }

        PathValidator.EnsureExistingFile(settings.PackagePath, "Package");
        Directory.CreateDirectory(settings.OutputFolder);

        // Pre-check extractability
        var game = await InspectAsync(settings.PackagePath, settings.Passcode, cancellationToken).ConfigureAwait(false);
        if (!game.CanExtract)
        {
            return OperationResult.Fail(
                "Распаковка недоступна для этого пакета.",
                game.ExtractBlockedReason ?? "Only debug FIH packages can be extracted without a supplied image key.");
        }

        var (exe, args) = BuildCliInvocation(
            "pkg-extract",
            settings.PackagePath,
            "--output", settings.OutputFolder,
            "--passcode", settings.Passcode,
            "--lang", "en");

        if (settings.Threads > 0)
        {
            args.Add("--threads");
            args.Add(settings.Threads.ToString());
        }

        var temp = settings.TempFolder ?? _settings.TempDirectory;
        args.Add("--temp");
        args.Add(temp);

        _log.Info("Starting fpkg-cli pkg-extract");
        _log.Info($"Package: {settings.PackagePath}");
        _log.Info($"Output: {settings.OutputFolder}");
        _log.Info($"Title: {game.Title}");
        _log.Info($"Game ID: {game.TitleId}");

        var sw = Stopwatch.StartNew();
        var inputBytes = game.FileSizeBytes ?? TryFileSize(settings.PackagePath) ?? 0;
        progress?.Report(new OperationProgress
        {
            Phase = "extract",
            StatusText = "Извлечение…",
            TotalBytes = inputBytes,
            Percent = null
        });

        void OnMonitor(object? _, OperationProgress p)
        {
            progress?.Report(new OperationProgress
            {
                CpuUsage = p.CpuUsage,
                MemoryUsageBytes = p.MemoryUsageBytes,
                Phase = "monitor",
                Elapsed = sw.Elapsed
            });
        }

        _monitor.Updated += OnMonitor;
        await _monitor.StartAsync(cancellationToken).ConfigureAwait(false);

        ProcessResult result;
        try
        {
            var runTask = _runner.RunAsync(new ProcessStartRequest
            {
                Executable = exe,
                Arguments = args,
                Priority = MapPriority(settings.ProcessPriority),
                Environment = new Dictionary<string, string> { ["FPKG_LANG"] = "en" }
            }, new Progress<ProcessOutput>(o =>
            {
                _log.Info(o.Line);
                if (FpkgProgressParser.TryParse(o.Line, out var parsed))
                {
                    parsed.Elapsed = sw.Elapsed;
                    parsed.TotalBytes ??= inputBytes;
                    progress?.Report(parsed);
                }
            }), cancellationToken);

            _ = AttachCliMonitorAsync(cancellationToken);
            result = await runTask.ConfigureAwait(false);
            if (result.ProcessId is { } pid)
            {
                _monitor.Attach(pid);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex.Message);
            return OperationResult.Fail("Распаковка завершилась с ошибкой.", ex.ToString());
        }
        finally
        {
            _monitor.Updated -= OnMonitor;
            _monitor.Detach();
            await _monitor.StopAsync().ConfigureAwait(false);
        }

        sw.Stop();
        if (result.Cancelled)
        {
            return OperationResult.Cancelled();
        }

        if (!result.Succeeded)
        {
            return OperationResult.Fail(
                "Распаковка завершилась с ошибкой.",
                $"Exit code: {result.ExitCode}\n\n{result.StdErr}\n{result.StdOut}",
                result.ExitCode);
        }

        if (settings.VerifyAfterExtraction)
        {
            var param = Path.Combine(settings.OutputFolder, "sce_sys", "param.json");
            if (!File.Exists(param))
            {
                _log.Warning("sce_sys/param.json not found after extraction");
            }
            else
            {
                _log.Success("Extraction verification: sce_sys/param.json present");
            }
        }

        if (settings.ExtractCoverAndMetadata && game.CoverImageBytes is { Length: > 0 })
        {
            try
            {
                var iconPath = Path.Combine(settings.OutputFolder, "sce_sys", "icon0.png");
                Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
                if (!File.Exists(iconPath))
                {
                    await File.WriteAllBytesAsync(iconPath, game.CoverImageBytes, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log.Warning($"Could not write cover: {ex.Message}");
            }
        }

        _log.Success("Extraction completed");
        progress?.Report(new OperationProgress
        {
            Percent = 100,
            Phase = "done",
            StatusText = "Распаковка завершена",
            Elapsed = sw.Elapsed,
            TotalBytes = inputBytes,
            ProcessedBytes = inputBytes
        });

        return OperationResult.Ok("✓ Распаковка завершена", settings.OutputFolder, sw.Elapsed, inputBytes);
    }

    public async Task<OperationResult> VerifyAsync(string packagePath, bool full = false, CancellationToken cancellationToken = default)
    {
        var detect = await DetectAsync(cancellationToken).ConfigureAwait(false);
        if (!detect.Available || _cliPath is null)
        {
            return OperationResult.Fail("fpkg-cli не найден.", detect.Details);
        }

        PathValidator.EnsureExistingFile(packagePath, "Package");
        var (exe, args) = BuildCliInvocation("verify", packagePath, "--lang", "en");
        if (full)
        {
            args.Add("--full");
        }

        var result = await _runner.RunAsync(new ProcessStartRequest
        {
            Executable = exe,
            Arguments = args
        }, new Progress<ProcessOutput>(o => _log.Info(o.Line)), cancellationToken).ConfigureAwait(false);

        if (result.Cancelled)
        {
            return OperationResult.Cancelled();
        }

        if (!result.Succeeded)
        {
            return OperationResult.Fail("Проверка пакета не удалась.", result.StdErr, result.ExitCode);
        }

        return OperationResult.Ok("Package verification OK", packagePath, result.Elapsed);
    }

    private (string Executable, List<string> Arguments) BuildCliInvocation(params string[] commandAndArgs)
    {
        if (_cliPath!.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var args = new List<string> { _cliPath };
            args.AddRange(commandAndArgs);
            return ("dotnet", args);
        }

        return (_cliPath, commandAndArgs.ToList());
    }

    private async Task AttachCliMonitorAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 40 && !cancellationToken.IsCancellationRequested; i++)
        {
            try
            {
                var candidates = System.Diagnostics.Process.GetProcessesByName("fpkg-cli")
                    .Concat(System.Diagnostics.Process.GetProcessesByName("dotnet"));
                var newest = candidates.OrderByDescending(p =>
                {
                    try { return p.StartTime; } catch { return DateTime.MinValue; }
                }).FirstOrDefault();
                if (newest != null)
                {
                    _monitor.Attach(newest.Id);
                    return;
                }
            }
            catch { /* ignore */ }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static GameInfo MapPackageInfo(PsViethoa.FpkgBuilder.Core.Models.PackageInfo info)
    {
        var languages = new List<string>();
        if (info.Params?.Fields != null)
        {
            foreach (var field in info.Params.Fields)
            {
                if (field.Key.Contains("language", StringComparison.OrdinalIgnoreCase) ||
                    field.Key.Contains("titleName", StringComparison.OrdinalIgnoreCase))
                {
                    // Collect language codes from nested structures if present as flat strings.
                }
            }

            if (!string.IsNullOrWhiteSpace(info.Params.DefaultLanguage))
            {
                languages.Add(info.Params.DefaultLanguage);
            }
        }

        // Try parse localizedTitles from param.json bytes
        languages = ParseLanguages(info.ParamJsonBytes) is { Count: > 0 } parsed
            ? parsed
            : languages;

        var contentId = info.ContentId;
        return new GameInfo
        {
            Title = string.IsNullOrWhiteSpace(info.Title) ? "Unknown" : info.Title,
            TitleId = info.Params?.TitleId ?? ExtractTitleId(contentId) ?? "Unknown",
            ContentId = string.IsNullOrWhiteSpace(contentId) ? "Unknown" : contentId,
            Region = DetectRegion(contentId),
            Console = "PS5",
            Version = info.Params?.ContentVersion ?? info.Params?.MasterVersion ?? "Unknown",
            PackageType = info.Params?.CategoryLabel ?? info.KindLabel,
            FileSizeBytes = info.FileSize,
            InstalledSizeBytes = info.Fih is { } fih ? (long)fih.InnerImageBlockCount * 65536L : info.FileSize,
            SdkVersion = info.Params?.SdkVersion ?? "Unknown",
            RequiredFirmware = info.Params?.RequiredSystemSoftwareVersion ?? "Unknown",
            Languages = languages,
            CoverImageBytes = info.IconBytes,
            SourcePath = info.Path,
            CanExtract = info.CanExtract,
            ExtractBlockedReason = info.ExtractBlockedReason,
            KindLabel = info.KindLabel,
            ImageMode = info.ImageModeLabel
        };
    }

    private static List<string> ParseLanguages(byte[]? paramJson)
    {
        var list = new List<string>();
        if (paramJson is null || paramJson.Length == 0)
        {
            return list;
        }

        try
        {
            using var doc = JsonDocument.Parse(paramJson);
            if (doc.RootElement.TryGetProperty("localizedLanguages", out var langs) && langs.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in langs.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.String)
                    {
                        list.Add(e.GetString()!);
                    }
                }
            }
            else if (doc.RootElement.TryGetProperty("localizedParameters", out var lp) && lp.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in lp.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object && prop.Name != "defaultLanguage")
                    {
                        list.Add(prop.Name);
                    }
                }
            }
            else if (doc.RootElement.TryGetProperty("titleName", out var titles) && titles.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in titles.EnumerateObject())
                {
                    list.Add(prop.Name);
                }
            }
        }
        catch
        {
            // ignore
        }

        return list;
    }

    private static string DetectRegion(string? contentId)
    {
        if (string.IsNullOrWhiteSpace(contentId) || contentId.Length < 2)
        {
            return "Unknown";
        }

        return contentId[..2].ToUpperInvariant() switch
        {
            "UP" => "USA",
            "EP" => "EUR",
            "JP" => "JPN",
            "HP" or "AP" => "ASIA",
            "KP" => "KOR",
            _ => "Unknown"
        };
    }

    private static string? ExtractTitleId(string? contentId)
    {
        if (string.IsNullOrWhiteSpace(contentId) || contentId.Length < 16)
        {
            return null;
        }

        // XXYYYY-XXXXYYYYY_00-...
        var dash = contentId.IndexOf('-');
        if (dash < 0)
        {
            return null;
        }

        var rest = contentId[(dash + 1)..];
        var under = rest.IndexOf('_');
        return under > 0 ? rest[..under] : rest[..Math.Min(9, rest.Length)];
    }

    private static long? TryFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return null; }
    }

    private static ProcessPriorityClass MapPriority(ProcessPriorityChoice choice) => choice switch
    {
        ProcessPriorityChoice.Low => ProcessPriorityClass.Idle,
        ProcessPriorityChoice.Normal => ProcessPriorityClass.Normal,
        _ => ProcessPriorityClass.BelowNormal
    };
}
