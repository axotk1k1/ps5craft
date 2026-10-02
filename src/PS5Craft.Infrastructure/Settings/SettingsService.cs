using System.Text.Json;
using PS5Craft.Core.Abstractions;
using PS5Craft.Core.Models;

namespace PS5Craft.Infrastructure.Settings;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _gate = new();
    private AppSettings _current = new();

    public SettingsService()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PS5Craft"))
    {
    }

    public SettingsService(string appDataDirectory)
    {
        AppDataDirectory = appDataDirectory;
        SettingsPath = Path.Combine(AppDataDirectory, "settings.json");
        LogsDirectory = Path.Combine(AppDataDirectory, "Logs");
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TempDirectory);
        Load();
    }

    public AppSettings Current
    {
        get { lock (_gate) return Clone(_current); }
    }

    public string SettingsPath { get; }
    public string AppDataDirectory { get; }
    public string LogsDirectory { get; }

    public string TempDirectory
    {
        get
        {
            lock (_gate)
            {
                var configured = _current.TempFolder;
                if (!string.IsNullOrWhiteSpace(configured))
                {
                    Directory.CreateDirectory(configured);
                    return configured;
                }
            }

            var path = Path.Combine(AppDataDirectory, "Temp");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public event EventHandler? SettingsChanged;

    public AppSettings Load()
    {
        lock (_gate)
        {
            if (File.Exists(SettingsPath))
            {
                try
                {
                    var json = File.ReadAllText(SettingsPath);
                    _current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                }
                catch
                {
                    _current = new AppSettings();
                }
            }
            else
            {
                _current = new AppSettings();
                SaveUnlocked(_current);
            }

            return Clone(_current);
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _current = Clone(settings);
            SaveUnlocked(_current);
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Reload() => Load();

    private void SaveUnlocked(AppSettings settings)
    {
        Directory.CreateDirectory(AppDataDirectory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
        if (!string.IsNullOrWhiteSpace(settings.TempFolder))
        {
            Directory.CreateDirectory(settings.TempFolder);
        }
    }

    private static AppSettings Clone(AppSettings s) => new()
    {
        Language = s.Language,
        Theme = s.Theme,
        MkPfsPath = s.MkPfsPath,
        PythonPath = s.PythonPath,
        FpkgCliPath = s.FpkgCliPath,
        CpuCount = s.CpuCount,
        ProcessPriority = s.ProcessPriority,
        CompressionLevel = s.CompressionLevel,
        BlockSize = s.BlockSize,
        DefaultOutputFormat = s.DefaultOutputFormat,
        DefaultOutputFolder = s.DefaultOutputFolder,
        TempFolder = s.TempFolder,
        VerifyAfterCompression = s.VerifyAfterCompression,
        VerifyAfterExtraction = s.VerifyAfterExtraction,
        KeepTempOnFailure = s.KeepTempOnFailure,
        CheckUpdatesAutomatically = s.CheckUpdatesAutomatically,
        IncludePrereleases = s.IncludePrereleases,
        LastUpdateCheckUtc = s.LastUpdateCheckUtc,
        DetectUsbAutomatically = s.DetectUsbAutomatically,
        AutoCopyToUsb = s.AutoCopyToUsb,
        ConfirmBeforeUsbCopy = s.ConfirmBeforeUsbCopy,
        DetectConsoleAutomatically = s.DetectConsoleAutomatically,
        AutoSendToConsole = s.AutoSendToConsole,
        ConfirmBeforeConsoleTransfer = s.ConfirmBeforeConsoleTransfer,
        ApprovedUsbDevices = s.ApprovedUsbDevices.Select(d => new ApprovedUsbDevice
        {
            DeviceKey = d.DeviceKey,
            VolumeLabel = d.VolumeLabel,
            VolumeSerial = d.VolumeSerial,
            LastDriveLetter = d.LastDriveLetter,
            ApprovedAt = d.ApprovedAt
        }).ToList(),
        PreferredConsoleEndpoint = s.PreferredConsoleEndpoint,
        ConsoleRemoteDirectory = string.IsNullOrWhiteSpace(s.ConsoleRemoteDirectory)
            ? "/data/etaHEN/homebrew"
            : s.ConsoleRemoteDirectory,
        AmprEmulatorVersion = string.IsNullOrWhiteSpace(s.AmprEmulatorVersion)
            ? AmprEmulatorDefaultVersion
            : s.AmprEmulatorVersion
    };

    private const string AmprEmulatorDefaultVersion = "0.3.6.6";
}
