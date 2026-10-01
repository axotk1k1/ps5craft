using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PS5Craft.Core;
using PS5Craft.Core.Abstractions;
using PS5Craft.Core.Models;

namespace PS5Craft.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IFpkgService _fpkg;
    private readonly IMkPfsService _mkpfs;
    private readonly IGameMetadataService _metadata;
    private readonly ILogService _log;
    private readonly ISettingsService _settings;
    private CancellationTokenSource? _cts;

    public MainViewModel(
        IFpkgService fpkg,
        IMkPfsService mkpfs,
        IGameMetadataService metadata,
        ILogService log,
        ISettingsService settings,
        IUpdateService updates,
        ILibraryService library,
        IUsbDriveService usb,
        IUsbExportService usbExport,
        IPs5NetworkDiscoveryService discovery,
        IPs5TransferService consoleTransfer,
        ITransferQueueService transferQueue)
    {
        _fpkg = fpkg;
        _mkpfs = mkpfs;
        _metadata = metadata;
        _log = log;
        _settings = settings;
        _updates = updates;
        _library = library;
        _usb = usb;
        _usbExport = usbExport;
        _discovery = discovery;
        _consoleTransfer = consoleTransfer;
        _transferQueue = transferQueue;

        LogLines = new ObservableCollection<string>();
        LanguageChips = new ObservableCollection<string>();
        _log.EntryAdded += (_, e) =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return;
            }

            // BeginInvoke: never block worker threads (network scan / process detect) on the UI.
            _ = dispatcher.BeginInvoke(() =>
            {
                LogLines.Add(e.ToString());
                while (LogLines.Count > 5000)
                {
                    LogLines.RemoveAt(0);
                }
            });
        };

        CpuOptions = new ObservableCollection<string>(["Auto", "1", "2", "4", "6", "8", "12", "16", "24", "32"]);
        CompressionLevels = new ObservableCollection<string>(Enumerable.Range(0, 10).Select(i => i.ToString()));
        BlockSizes = new ObservableCollection<string>(["Auto", "16384", "32768", "65536"]);
        OutputFormats = new ObservableCollection<string>(["FFPFSC", "FFPFS"]);
        Priorities = new ObservableCollection<string>(["Normal", "Below Normal", "Low"]);

        var s = _settings.Current;
        SelectedCpu = s.CpuCount == 0 ? "Auto" : s.CpuCount.ToString();
        SelectedCompressionLevel = s.CompressionLevel.ToString();
        SelectedBlockSize = string.IsNullOrWhiteSpace(s.BlockSize) ? "Auto" : (s.BlockSize.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "Auto" : s.BlockSize);
        SelectedOutputFormat = s.DefaultOutputFormat == OutputFormat.Ffpfs ? "FFPFS" : "FFPFSC";
        SelectedPriority = s.ProcessPriority switch
        {
            ProcessPriorityChoice.Normal => "Normal",
            ProcessPriorityChoice.Low => "Low",
            _ => "Below Normal"
        };
        VerifyAfterExtract = s.VerifyAfterExtraction;
        VerifyAfterPack = s.VerifyAfterCompression;
        ExtractCoverAndMetadata = true;
        CompressEnabled = true;
        SkipExecutableCompression = true;
        TempFolderDisplay = _settings.TempDirectory;
        MkPfsPathSetting = s.MkPfsPath ?? string.Empty;
        PythonPathSetting = s.PythonPath ?? string.Empty;
        FpkgCliPathSetting = s.FpkgCliPath ?? string.Empty;
        InitializeUpdateSettings();
        InitializeExportSubsystem();

        _log.Info("PS5Craft started");
        _ = InitializeToolsAsync();
    }

    public ObservableCollection<string> LogLines { get; }
    public ObservableCollection<string> LanguageChips { get; }
    public ObservableCollection<string> CpuOptions { get; }
    public ObservableCollection<string> CompressionLevels { get; }
    public ObservableCollection<string> BlockSizes { get; }
    public ObservableCollection<string> OutputFormats { get; }
    public ObservableCollection<string> Priorities { get; }

    [ObservableProperty] private AppPage _currentPage = AppPage.Extract;
    [ObservableProperty] private string _statusMessage = "Готово";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isProgressIndeterminate;
    private bool _hasRealPercent;

    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressPercentText = "0%";
    [ObservableProperty] private string _progressPhase = string.Empty;
    [ObservableProperty] private string _currentFile = "—";
    [ObservableProperty] private string _sizeText = "—";
    [ObservableProperty] private string _speedText = "—";
    [ObservableProperty] private string _etaText = "—";
    [ObservableProperty] private string _elapsedText = "—";
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private string _ramText = "—";
    [ObservableProperty] private string _outputSizeText = "—";
    [ObservableProperty] private string _ratioText = "—";
    [ObservableProperty] private int _workflowStep = 1;

    [ObservableProperty] private string _packagePath = string.Empty;
    [ObservableProperty] private string _extractOutputFolder = string.Empty;
    [ObservableProperty] private string _packSourceFolder = string.Empty;
    [ObservableProperty] private string _packOutputPath = string.Empty;

    [ObservableProperty] private bool _verifyAfterExtract = true;
    [ObservableProperty] private bool _extractCoverAndMetadata = true;
    [ObservableProperty] private bool _removePackageOnlyFiles = true;
    [ObservableProperty] private bool _verifyAfterPack = true;
    [ObservableProperty] private bool _compressEnabled = true;
    [ObservableProperty] private bool _skipExecutableCompression = true;
    [ObservableProperty] private bool _verbose;
    [ObservableProperty] private bool _dryRun;
    [ObservableProperty] private string _formatWarning = string.Empty;

    [ObservableProperty] private string _selectedCpu = "Auto";
    [ObservableProperty] private string _selectedCompressionLevel = "7";
    [ObservableProperty] private string _selectedBlockSize = "Auto";
    [ObservableProperty] private string _selectedOutputFormat = "FFPFSC";
    [ObservableProperty] private string _selectedPriority = "Below Normal";

    [ObservableProperty] private GameInfo _game = GameInfo.Unknown();
    [ObservableProperty] private BitmapImage? _coverImage;
    [ObservableProperty] private string _languagesText = "—";
    [ObservableProperty] private string _regionDisplay = "Unknown";
    [ObservableProperty] private string _regionCode = "—";
    [ObservableProperty] private string _regionName = "Unknown";
    [ObservableProperty] private string _consoleDisplay = "PS5 — PlayStation 5";
    [ObservableProperty] private string _consoleCode = "PS5";
    [ObservableProperty] private string _consoleName = "PlayStation 5";
    [ObservableProperty] private string _sizeDisplay = "Unknown";
    [ObservableProperty] private string _fileSizeDisplay = "Unknown";
    [ObservableProperty] private string _sdkDisplay = "Unknown";
    [ObservableProperty] private string _firmwareDisplay = "Unknown";
    [ObservableProperty] private string _languagesOverflow = string.Empty;
    [ObservableProperty] private string _extractAvailabilityText = "—";
    [ObservableProperty] private string _progressTitle = "Прогресс распаковки";
    [ObservableProperty] private string _resultSummary = string.Empty;
    [ObservableProperty] private bool _hasResult;

    [ObservableProperty] private string _mkPfsStatus = "Проверка MkPFS…";
    [ObservableProperty] private string _fpkgStatus = "Проверка fpkg-cli…";
    [ObservableProperty] private string _tempFolderDisplay = string.Empty;
    [ObservableProperty] private string _mkPfsPathSetting = string.Empty;
    [ObservableProperty] private string _pythonPathSetting = string.Empty;
    [ObservableProperty] private string _fpkgCliPathSetting = string.Empty;
    [ObservableProperty] private string? _resultBanner;

    partial void OnSelectedOutputFormatChanged(string value)
    {
        FormatWarning = value.Equals("FFPFS", StringComparison.OrdinalIgnoreCase)
            ? "⚠ FFPFS (--raw): при включённом сжатии консоль может некорректно читать файлы. Для бэкапов игр рекомендуется FFPFSC."
            : string.Empty;
    }

    partial void OnCurrentPageChanged(AppPage value)
    {
        WorkflowStep = value switch
        {
            AppPage.Extract => 1,
            AppPage.Info => 2,
            AppPage.Pack => 3,
            _ => WorkflowStep
        };

        if (!IsBusy)
        {
            ProgressTitle = value == AppPage.Pack ? "Прогресс сжатия" : "Прогресс распаковки";
        }
    }

    partial void OnResultBannerChanged(string? value)
    {
        HasResult = !string.IsNullOrWhiteSpace(value);
        ResultSummary = HasResult ? value!.Split('\n')[0].Trim() : string.Empty;
    }

    partial void OnPackagePathChanged(string value) => ExtractCommand.NotifyCanExecuteChanged();

    partial void OnExtractOutputFolderChanged(string value) => ExtractCommand.NotifyCanExecuteChanged();

    partial void OnPackSourceFolderChanged(string value) => PackCommand.NotifyCanExecuteChanged();

    partial void OnPackOutputPathChanged(string value) => PackCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value)
    {
        ExtractCommand.NotifyCanExecuteChanged();
        PackCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Selects a package (file picker, command line or drag and drop) and reads its metadata.</summary>
    public async Task OpenPackageAsync(string path)
    {
        PackagePath = path;
        SuggestExtractOutput();
        await LoadPackageMetadataAsync();
    }

    private async Task InitializeToolsAsync()
    {
        try
        {
            var fpkg = await _fpkg.DetectAsync().ConfigureAwait(false);
            var mk = await _mkpfs.DetectAsync().ConfigureAwait(false);
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                return;
            }

            await dispatcher.InvokeAsync(() =>
            {
                FpkgStatus = fpkg.Available ? $"✓ fpkg-cli: {fpkg.Path}" : $"✗ {fpkg.Details}";
                MkPfsStatus = mk.Available ? $"✓ MkPFS {mk.Version}: {mk.Path}" : $"✗ {mk.Details}";
            });
        }
        catch (Exception ex)
        {
            _log.Warning($"Tool detection failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        CurrentPage = page.ToLowerInvariant() switch
        {
            "extract" => AppPage.Extract,
            "pack" => AppPage.Pack,
            "library" => AppPage.Library,
            "info" => AppPage.Info,
            "tools" => AppPage.Tools,
            "settings" => AppPage.Settings,
            "about" => AppPage.About,
            _ => CurrentPage
        };
    }

    [RelayCommand]
    private async Task BrowsePackageAsync()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "PS5 Package (*.fpkg;*.pkg)|*.fpkg;*.pkg|All files (*.*)|*.*",
            Title = "Выберите .fpkg / .pkg"
        };
        if (dlg.ShowDialog() == true)
        {
            await OpenPackageAsync(dlg.FileName);
        }
    }

    [RelayCommand]
    private void BrowseExtractOutput()
    {
        var path = BrowseFolder("Папка вывода");
        if (path != null)
        {
            ExtractOutputFolder = path;
        }
    }

    [RelayCommand]
    private void BrowsePackSource()
    {
        var path = BrowseFolder("Исходная папка игры");
        if (path != null)
        {
            PackSourceFolder = path;
            SuggestPackOutput();
            _ = LoadFolderMetadataAsync(path);
        }
    }

    [RelayCommand]
    private void BrowsePackOutput()
    {
        var dlg = new SaveFileDialog
        {
            Filter = SelectedOutputFormat.Equals("FFPFS", StringComparison.OrdinalIgnoreCase)
                ? "FFPFS (*.ffpfs)|*.ffpfs"
                : "FFPFSC (*.ffpfsc)|*.ffpfsc",
            Title = "Выходной образ",
            FileName = Path.GetFileName(PackOutputPath)
        };
        if (dlg.ShowDialog() == true)
        {
            PackOutputPath = dlg.FileName;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunExtract))]
    private async Task ExtractAsync()
    {
        if (IsBusy)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        IsBusy = true;
        ResultBanner = null;
        WorkflowStep = 1;
        ProgressTitle = "Прогресс распаковки";
        ResetProgress("Распаковка…");
        try
        {
            var settings = new ExtractionSettings
            {
                PackagePath = PackagePath,
                OutputFolder = ExtractOutputFolder,
                VerifyAfterExtraction = VerifyAfterExtract,
                ExtractCoverAndMetadata = ExtractCoverAndMetadata,
                RemovePackageOnlyFiles = RemovePackageOnlyFiles,
                TempFolder = _settings.TempDirectory,
                ProcessPriority = MapPriority(SelectedPriority)
            };

            var result = await _fpkg.ExtractAsync(settings, new Progress<OperationProgress>(ApplyProgress), _cts.Token);
            HandleResult(result);
            if (result.Success)
            {
                PackSourceFolder = result.OutputPath ?? ExtractOutputFolder;
                SuggestPackOutput();
                WorkflowStep = 2;
                CurrentPage = AppPage.Pack;
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex.Message);
            ResultBanner = "Ошибка: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            ExtractCommand.NotifyCanExecuteChanged();
            PackCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanRunExtract() => !IsBusy && File.Exists(PackagePath) && !string.IsNullOrWhiteSpace(ExtractOutputFolder);

    [RelayCommand(CanExecute = nameof(CanRunPack))]
    private async Task PackAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (File.Exists(PackOutputPath))
        {
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = OverwriteConfirmationRequired;
            if (handler is null)
            {
                _log.Error("Output file already exists: " + PackOutputPath);
                ResultBanner = "Файл уже существует. Укажите другой путь или удалите старый образ.";
                return;
            }

            handler.Invoke(this, (PackOutputPath, answer));
            if (!await answer.Task.ConfigureAwait(true))
            {
                StatusMessage = "Упаковка отменена";
                return;
            }

            try
            {
                File.Delete(PackOutputPath);
                _log.Info("Removed existing output before pack: " + PackOutputPath);
            }
            catch (Exception ex)
            {
                _log.Error("Cannot overwrite output: " + ex.Message);
                ResultBanner = "Не удалось удалить существующий файл: " + ex.Message;
                return;
            }
        }

        _cts = new CancellationTokenSource();
        IsBusy = true;
        ResultBanner = null;
        WorkflowStep = 3;
        ProgressTitle = "Прогресс сжатия";
        ResetProgress("Сжатие…");
        try
        {
            var settings = new CompressionSettings
            {
                SourceFolder = PackSourceFolder,
                OutputPath = PackOutputPath,
                Format = SelectedOutputFormat.Equals("FFPFS", StringComparison.OrdinalIgnoreCase) ? OutputFormat.Ffpfs : OutputFormat.Ffpfsc,
                CpuCount = SelectedCpu.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? 0 : int.Parse(SelectedCpu),
                CompressionLevel = int.Parse(SelectedCompressionLevel),
                BlockSize = SelectedBlockSize.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? "auto" : SelectedBlockSize,
                Compress = CompressEnabled,
                SkipExecutableCompression = SkipExecutableCompression,
                Verify = VerifyAfterPack,
                Verbose = Verbose,
                DryRun = DryRun,
                TempFolder = _settings.TempDirectory,
                ProcessPriority = MapPriority(SelectedPriority),
                Version = "PS5"
            };

            var result = await _mkpfs.PackFolderAsync(settings, new Progress<OperationProgress>(ApplyProgress), _cts.Token);
            HandleResult(result);
            if (result.Success)
            {
                await OnPackSucceededAsync(result, settings);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex.Message);
            ResultBanner = "Ошибка: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            PackCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanRunPack() => !IsBusy && Directory.Exists(PackSourceFolder) && !string.IsNullOrWhiteSpace(PackOutputPath);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusMessage = "Отмена…";
    }

    private bool CanCancel() => IsBusy;

    [RelayCommand]
    private void ClearLog() => LogLines.Clear();

    [RelayCommand]
    private async Task SaveLogAsync()
    {
        await _log.SaveAsync();
        StatusMessage = "Лог сохранён";
    }

    [RelayCommand]
    private void OpenLogFolder() => _log.OpenLogFolder();

    [RelayCommand]
    private async Task TestMkPfsAsync()
    {
        PersistToolSettings();
        var info = await _mkpfs.TestAsync();
        MkPfsStatus = info.Available ? $"✓ MkPFS {info.Version}" : $"✗ {info.Details}";
    }

    [RelayCommand]
    private async Task TestFpkgAsync()
    {
        PersistToolSettings();
        var info = await _fpkg.DetectAsync();
        FpkgStatus = info.Available ? $"✓ {info.Path}" : $"✗ {info.Details}";
    }

    [RelayCommand]
    private void SaveSettings()
    {
        PersistToolSettings();
        var s = _settings.Current;
        s.CpuCount = SelectedCpu.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? 0 : int.Parse(SelectedCpu);
        s.CompressionLevel = int.Parse(SelectedCompressionLevel);
        s.BlockSize = SelectedBlockSize.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? "auto" : SelectedBlockSize;
        s.DefaultOutputFormat = SelectedOutputFormat.Equals("FFPFS", StringComparison.OrdinalIgnoreCase) ? OutputFormat.Ffpfs : OutputFormat.Ffpfsc;
        s.ProcessPriority = MapPriority(SelectedPriority);
        s.VerifyAfterCompression = VerifyAfterPack;
        s.VerifyAfterExtraction = VerifyAfterExtract;
        s.TempFolder = string.IsNullOrWhiteSpace(TempFolderDisplay) ? null : TempFolderDisplay;
        PersistUpdateSettings(s);
        PersistExportSettings(s);
        _settings.Save(s);
        TempFolderDisplay = _settings.TempDirectory;
        StatusMessage = "Настройки сохранены";
        _log.Success("Settings saved");
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        var path = !string.IsNullOrWhiteSpace(PackOutputPath) ? Path.GetDirectoryName(PackOutputPath) : ExtractOutputFolder;
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
    }

    [RelayCommand]
    private void BrowseTempFolder()
    {
        var path = BrowseFolder("Временная папка");
        if (path != null)
        {
            TempFolderDisplay = path;
        }
    }

    private async Task LoadPackageMetadataAsync()
    {
        StatusMessage = "Чтение метаданных…";
        _log.Info("Reading package information…");
        try
        {
            Game = await _metadata.ReadFromPackageAsync(PackagePath);
            ApplyGameToUi(Game);
            _log.Info($"Title: {Game.Title}");
            _log.Info($"Game ID: {Game.TitleId}");
            if (!string.IsNullOrWhiteSpace(Game.Error))
            {
                _log.Warning(Game.Error);
            }

            if (!Game.CanExtract && !string.IsNullOrWhiteSpace(Game.ExtractBlockedReason))
            {
                _log.Warning(Game.ExtractBlockedReason);
            }

            StatusMessage = Game.CanExtract ? "Пакет готов к распаковке" : "Пакет нельзя распаковать";
        }
        catch (Exception ex)
        {
            _log.Error(ex.Message);
            Game = GameInfo.Unknown(PackagePath);
            ApplyGameToUi(Game);
        }
        finally
        {
            ExtractCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task LoadFolderMetadataAsync(string folder)
    {
        try
        {
            Game = await _metadata.ReadFromFolderAsync(folder);
            ApplyGameToUi(Game);
        }
        catch (Exception ex)
        {
            _log.Warning(ex.Message);
        }
    }

    private void ApplyGameToUi(GameInfo g)
    {
        RegionDisplay = ExpandRegion(g.Region);
        ConsoleDisplay = string.IsNullOrWhiteSpace(g.Console) || g.Console == "PS5"
            ? "PS5 — PlayStation 5"
            : g.Console!;
        (RegionCode, RegionName) = SplitRegion(g.Region);
        ConsoleCode = string.IsNullOrWhiteSpace(g.Console) ? "PS5" : g.Console!;
        ConsoleName = ConsoleCode.ToUpperInvariant() switch
        {
            "PS5" => "PlayStation 5",
            "PS4" => "PlayStation 4",
            _ => string.Empty
        };
        SizeDisplay = g.InstalledSizeBytes is { } b
            ? string.Create(CultureInfo.InvariantCulture, $"{FormatBytes(b)} ({b:N0} bytes)")
            : g.FileSizeBytes is { } f ? string.Create(CultureInfo.InvariantCulture, $"{FormatBytes(f)} ({f:N0} bytes)") : "Unknown";
        FileSizeDisplay = g.FileSizeBytes is { } fs ? string.Create(CultureInfo.InvariantCulture, $"{FormatBytes(fs)} ({fs:N0} bytes)") : "Unknown";
        SdkDisplay = FormatSystemVersion(g.SdkVersion, plus: false);
        FirmwareDisplay = FormatSystemVersion(g.RequiredFirmware, plus: true);
        ExtractAvailabilityText = g.CanExtract
            ? "Доступна (debug FIH)"
            : g.ExtractBlockedReason ?? "Недоступна";

        var codes = g.Languages.Select(ToCountryCode).Where(c => c.Length > 0).Distinct().ToList();
        LanguageChips.Clear();
        foreach (var code in codes.Take(8))
        {
            LanguageChips.Add(code);
        }

        LanguagesOverflow = codes.Count > 8 ? $"+{codes.Count - 8}" : string.Empty;
        LanguagesText = codes.Count == 0 ? "—" : string.Join(" ", codes);

        CoverImage = null;
        if (g.CoverImageBytes is { Length: > 0 })
        {
            try
            {
                var bmp = new BitmapImage();
                using var ms = new MemoryStream(g.CoverImageBytes);
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                CoverImage = bmp;
            }
            catch
            {
                CoverImage = null;
            }
        }
    }

    private static string ExpandRegion(string? region) => region?.ToUpperInvariant() switch
    {
        "USA" or "US" => "USA — America",
        "EUR" or "EU" => "EUR — Europe",
        "JPN" or "JP" => "JPN — Japan",
        "ASIA" => "ASIA — Asia",
        "KOR" or "KR" => "KOR — Korea",
        null or "" or "UNKNOWN" => "Unknown",
        _ => region!
    };

    private static (string Code, string Name) SplitRegion(string? region) => region?.ToUpperInvariant() switch
    {
        "USA" or "US" => ("USA", "America"),
        "EUR" or "EU" => ("EUR", "Europe"),
        "JPN" or "JP" => ("JPN", "Japan"),
        "ASIA" => ("ASIA", "Asia"),
        "KOR" or "KR" => ("KOR", "Korea"),
        null or "" or "UNKNOWN" => ("—", "Unknown"),
        _ => (region!, string.Empty)
    };

    /// <summary>param.json stores versions as BCD hex, e.g. 0x0900000000000000 → 9.00.</summary>
    public static string FormatSystemVersion(string? raw, bool plus)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "Unknown";
        }

        var hex = raw.Trim();
        if (!hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || hex.Length < 6)
        {
            return raw;
        }

        if (hex[2..].All(c => c == '0'))
        {
            return "Unknown";
        }

        if (!int.TryParse(hex[2..4], out var major) || !int.TryParse(hex[4..6], out var minor))
        {
            return raw;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{major}.{minor:00}{(plus ? "+" : string.Empty)}");
    }

    /// <summary>Maps a param.json language key (en-US, ja-JP, es-419, zh-Hans, fr…) to a country code for the flag chip.</summary>
    public static string ToCountryCode(string lang)
    {
        var parts = lang.Trim().Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        var language = parts[0].ToLowerInvariant();
        var region = parts.Length > 1 ? parts[^1].ToUpperInvariant() : string.Empty;
        return region switch
        {
            "HANS" => "CN",
            "HANT" => "TW",
            "419" => "MX",
            { Length: 2 } => region,
            _ => language switch
            {
                "en" => "GB",
                "ja" => "JP",
                "ko" => "KR",
                "zh" => "CN",
                "sv" => "SE",
                "da" => "DK",
                "nb" or "no" => "NO",
                "uk" => "UA",
                "cs" => "CZ",
                "el" => "GR",
                "ar" => "SA",
                "pt" => "PT",
                _ => language.ToUpperInvariant()
            }
        };
    }

    private void ApplyProgress(OperationProgress p)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (p.Phase == "monitor")
            {
                if (p.CpuUsage is { } cpu)
                {
                    CpuText = $"{cpu:0}%";
                }

                if (p.MemoryUsageBytes is { } ram)
                {
                    RamText = FormatBytes(ram);
                }

                if (p.Elapsed > TimeSpan.Zero)
                {
                    ElapsedText = p.Elapsed.ToString(@"hh\:mm\:ss");
                }

                return;
            }

            // Reports without a percentage (status lines, "running") keep the last real value.
            // Progress<T> callbacks may arrive out of order, so the value never moves backwards within an operation.
            if (p.Percent is { } pctVal)
            {
                _hasRealPercent = true;
                ProgressPercent = Math.Max(ProgressPercent, pctVal);
                ProgressPercentText = string.Create(CultureInfo.InvariantCulture, $"{ProgressPercent:0}%");
            }

            IsProgressIndeterminate = !_hasRealPercent;
            if (!_hasRealPercent)
            {
                ProgressPercentText = "…";
            }
            ProgressPhase = p.Phase ?? ProgressPhase;
            if (!string.IsNullOrWhiteSpace(p.CurrentFile))
            {
                CurrentFile = p.CurrentFile;
            }
            else if (!string.IsNullOrWhiteSpace(p.StatusText) && p.Phase != "monitor")
            {
                CurrentFile = p.StatusText!;
            }

            if (p.TotalBytes is { } total && total > 0)
            {
                SizeText = $"{FormatBytes(p.ProcessedBytes)} / {FormatBytes(total)}";
            }

            if (p.SpeedBytesPerSecond > 0)
            {
                SpeedText = FormatBytes((long)p.SpeedBytesPerSecond) + "/s";
            }

            ElapsedText = p.Elapsed.ToString(@"hh\:mm\:ss");
            EtaText = p.EstimatedRemaining?.ToString(@"hh\:mm\:ss") ?? "—";
            if (p.OutputBytes is { } ob)
            {
                OutputSizeText = FormatBytes(ob);
            }

            if (p.CompressionRatioPercent is { } ratio)
            {
                RatioText = $"{ratio:0.0}%";
            }

            if (p.CpuUsage is { } c)
            {
                CpuText = $"{c:0}%";
            }

            if (p.MemoryUsageBytes is { } m)
            {
                RamText = FormatBytes(m);
            }

            StatusMessage = p.StatusText ?? StatusMessage;
        });
    }

    private void HandleResult(OperationResult result)
    {
        if (result.State == OperationState.Cancelled)
        {
            ResultBanner = "Операция отменена";
            StatusMessage = ResultBanner;
            return;
        }

        if (!result.Success)
        {
            ResultBanner = result.Message + (string.IsNullOrWhiteSpace(result.TechnicalDetails) ? string.Empty : "\n" + result.TechnicalDetails);
            StatusMessage = result.Message ?? "Ошибка";
            _log.Error(result.Message ?? "Failed");
            if (!string.IsNullOrWhiteSpace(result.TechnicalDetails))
            {
                _log.Error(result.TechnicalDetails);
            }

            return;
        }

        var lines = new List<string> { result.Message ?? "Готово" };
        if (result.InputBytes is { } ib)
        {
            lines.Add($"Input: {FormatBytes(ib)}");
        }

        if (result.OutputBytes is { } ob)
        {
            lines.Add($"Output: {FormatBytes(ob)}");
            if (result.InputBytes is { } ib2 and > 0)
            {
                lines.Add($"Compression: {ob * 100.0 / ib2:0.0}%");
            }
        }

        lines.Add($"Time: {result.Elapsed:hh\\:mm\\:ss}");
        if (!string.IsNullOrWhiteSpace(result.OutputPath))
        {
            lines.Add($"Output: {result.OutputPath}");
        }

        ResultBanner = string.Join("\n", lines);
        StatusMessage = result.Message ?? "Готово";
        IsProgressIndeterminate = false;
        ProgressPercent = 100;
        ProgressPercentText = "100%";
    }

    private void ResetProgress(string phase)
    {
        _hasRealPercent = false;
        IsProgressIndeterminate = true;
        ProgressPercent = 0;
        ProgressPercentText = "0%";
        ProgressPhase = phase;
        CurrentFile = "—";
        SizeText = SpeedText = EtaText = ElapsedText = CpuText = RamText = OutputSizeText = RatioText = "—";
        StatusMessage = phase;
        CancelCommand.NotifyCanExecuteChanged();
        ExtractCommand.NotifyCanExecuteChanged();
        PackCommand.NotifyCanExecuteChanged();
    }

    private void SuggestExtractOutput()
    {
        if (string.IsNullOrWhiteSpace(PackagePath))
        {
            return;
        }

        var dir = Path.GetDirectoryName(PackagePath)!;
        var name = Path.GetFileNameWithoutExtension(PackagePath);
        ExtractOutputFolder = Path.Combine(dir, name + "_Extracted");
    }

    private void SuggestPackOutput()
    {
        if (string.IsNullOrWhiteSpace(PackSourceFolder))
        {
            return;
        }

        var parent = Directory.GetParent(PackSourceFolder)?.FullName ?? PackSourceFolder;
        var name = new DirectoryInfo(PackSourceFolder).Name.Replace("_Extracted", "", StringComparison.OrdinalIgnoreCase);
        var ext = SelectedOutputFormat.Equals("FFPFS", StringComparison.OrdinalIgnoreCase) ? ".ffpfs" : ".ffpfsc";
        PackOutputPath = Path.Combine(parent, name + ext);
    }

    private void PersistToolSettings()
    {
        var s = _settings.Current;
        s.MkPfsPath = string.IsNullOrWhiteSpace(MkPfsPathSetting) ? null : MkPfsPathSetting;
        s.PythonPath = string.IsNullOrWhiteSpace(PythonPathSetting) ? null : PythonPathSetting;
        s.FpkgCliPath = string.IsNullOrWhiteSpace(FpkgCliPathSetting) ? null : FpkgCliPathSetting;
        _settings.Save(s);
    }

    private static string? BrowseFolder(string title)
    {
        var dlg = new OpenFolderDialog { Title = title };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    private static ProcessPriorityChoice MapPriority(string selected) => selected switch
    {
        "Normal" => ProcessPriorityChoice.Normal,
        "Low" => ProcessPriorityChoice.Low,
        _ => ProcessPriorityChoice.BelowNormal
    };

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        double v = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        foreach (var u in units)
        {
            v /= 1024.0;
            if (v < 1024 || u == "TB")
            {
                return string.Format(CultureInfo.InvariantCulture, u is "KB" ? "{0:0} {1}" : "{0:0.00} {1}", v, u);
            }
        }

        return bytes.ToString();
    }
}
