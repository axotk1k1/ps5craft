namespace PS5Craft.Core.Models;

public sealed class GameInfo
{
    public string? Title { get; set; }
    public string? TitleId { get; set; }
    public string? ContentId { get; set; }
    public string? Region { get; set; }
    public string Console { get; set; } = "PS5";
    public string? Version { get; set; }
    public string? PackageType { get; set; }
    public long? InstalledSizeBytes { get; set; }
    public long? FileSizeBytes { get; set; }
    public string? SdkVersion { get; set; }
    public string? RequiredFirmware { get; set; }
    public IReadOnlyList<string> Languages { get; set; } = Array.Empty<string>();
    public byte[]? CoverImageBytes { get; set; }
    public string? SourcePath { get; set; }
    public bool CanExtract { get; set; }
    public string? ExtractBlockedReason { get; set; }
    public string? KindLabel { get; set; }
    public string? ImageMode { get; set; }
    public string? Error { get; set; }

    public static GameInfo Unknown(string? path = null) => new()
    {
        Title = "Unknown",
        TitleId = "Unknown",
        ContentId = "Unknown",
        Region = "Unknown",
        Version = "Unknown",
        PackageType = "Unknown",
        SdkVersion = "Unknown",
        RequiredFirmware = "Unknown",
        SourcePath = path
    };
}

public sealed class OperationProgress
{
    /// <summary>Null means indeterminate.</summary>
    public double? Percent { get; set; }
    public string? Phase { get; set; }
    public string? CurrentFile { get; set; }
    public long ProcessedBytes { get; set; }
    public long? TotalBytes { get; set; }
    public double SpeedBytesPerSecond { get; set; }
    public TimeSpan Elapsed { get; set; }
    public TimeSpan? EstimatedRemaining { get; set; }
    public double? CpuUsage { get; set; }
    public long? MemoryUsageBytes { get; set; }
    public double? DiskReadBytesPerSecond { get; set; }
    public double? DiskWriteBytesPerSecond { get; set; }
    public long? OutputBytes { get; set; }
    public double? CompressionRatioPercent { get; set; }
    public string? StatusText { get; set; }
}

public sealed class OperationResult
{
    public bool Success { get; init; }
    public OperationState State { get; init; }
    public string? Message { get; init; }
    public string? TechnicalDetails { get; init; }
    public int? ExitCode { get; init; }
    public string? OutputPath { get; init; }
    public TimeSpan Elapsed { get; init; }
    public long? InputBytes { get; init; }
    public long? OutputBytes { get; init; }

    public static OperationResult Cancelled(string? message = null) => new()
    {
        Success = false,
        State = OperationState.Cancelled,
        Message = message ?? "Операция отменена."
    };

    public static OperationResult Fail(string message, string? details = null, int? exitCode = null) => new()
    {
        Success = false,
        State = OperationState.Failed,
        Message = message,
        TechnicalDetails = details,
        ExitCode = exitCode
    };

    public static OperationResult Ok(string message, string? outputPath = null, TimeSpan elapsed = default, long? input = null, long? output = null) => new()
    {
        Success = true,
        State = OperationState.Completed,
        Message = message,
        OutputPath = outputPath,
        Elapsed = elapsed,
        InputBytes = input,
        OutputBytes = output
    };
}

public sealed class CompressionSettings
{
    public string SourceFolder { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public OutputFormat Format { get; set; } = OutputFormat.Ffpfsc;
    public string Version { get; set; } = "PS5";
    public int CpuCount { get; set; } // 0 = auto
    public int CompressionLevel { get; set; } = 7;
    public string BlockSize { get; set; } = "auto";
    public bool Compress { get; set; } = true;
    public bool SkipExecutableCompression { get; set; } = true;
    public bool Verify { get; set; } = true;
    public bool Verbose { get; set; }
    public bool DryRun { get; set; }
    public string? TempFolder { get; set; }
    public ProcessPriorityChoice ProcessPriority { get; set; } = ProcessPriorityChoice.BelowNormal;
}

public sealed class ExtractionSettings
{
    public string PackagePath { get; set; } = string.Empty;
    public string OutputFolder { get; set; } = string.Empty;
    public bool VerifyAfterExtraction { get; set; } = true;
    public bool ExtractCoverAndMetadata { get; set; } = true;
    public bool RemovePackageOnlyFiles { get; set; } = true;
    public string Passcode { get; set; } = new string('0', 32);
    public string? TempFolder { get; set; }
    public int Threads { get; set; }
    public ProcessPriorityChoice ProcessPriority { get; set; } = ProcessPriorityChoice.BelowNormal;
}

public sealed class ToolInfo
{
    public bool Available { get; init; }
    public string? Path { get; init; }
    public string? Version { get; init; }
    public string? Details { get; init; }
    public string DisplayName { get; init; } = string.Empty;
}

public sealed class LogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; }
    public string Message { get; init; } = string.Empty;

    public override string ToString()
    {
        var tag = Level switch
        {
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Success => "OK",
            _ => "INFO"
        };
        return $"[{Timestamp:HH:mm:ss}] [{tag}] {Message}";
    }
}

public sealed class AppSettings
{
    public string Language { get; set; } = "ru";
    public string Theme { get; set; } = "Dark";
    public string? MkPfsPath { get; set; }
    public string? PythonPath { get; set; }
    public string? FpkgCliPath { get; set; }
    public int CpuCount { get; set; } // 0 = auto
    public ProcessPriorityChoice ProcessPriority { get; set; } = ProcessPriorityChoice.BelowNormal;
    public int CompressionLevel { get; set; } = 7;
    public string BlockSize { get; set; } = "auto";
    public OutputFormat DefaultOutputFormat { get; set; } = OutputFormat.Ffpfsc;
    public string? DefaultOutputFolder { get; set; }
    public string? TempFolder { get; set; }
    public bool VerifyAfterCompression { get; set; } = true;
    public bool VerifyAfterExtraction { get; set; } = true;
    public bool KeepTempOnFailure { get; set; } = true;
    public bool CheckUpdatesAutomatically { get; set; } = true;
    public bool IncludePrereleases { get; set; }
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    // Automatic export (safe defaults)
    public bool DetectUsbAutomatically { get; set; } = true;
    public bool AutoCopyToUsb { get; set; }
    public bool ConfirmBeforeUsbCopy { get; set; } = true;
    public bool DetectConsoleAutomatically { get; set; } = false;
    public bool AutoSendToConsole { get; set; }
    public bool ConfirmBeforeConsoleTransfer { get; set; } = true;
    public List<ApprovedUsbDevice> ApprovedUsbDevices { get; set; } = [];
    public string? PreferredConsoleEndpoint { get; set; }
    /// <summary>FTP destination on the console (etaHEN homebrew folder by default).</summary>
    public string ConsoleRemoteDirectory { get; set; } = "/data/etaHEN/homebrew";
    /// <summary>Pinned ampr_emu release folder under tools/ampr_emu/&lt;version&gt;/ (e.g. 0.3.6.6).</summary>
    public string AmprEmulatorVersion { get; set; } = "0.3.6.6";
}
