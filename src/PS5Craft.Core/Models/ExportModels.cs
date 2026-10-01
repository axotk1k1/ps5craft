namespace PS5Craft.Core.Models;

public sealed class LibraryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Unknown";
    public string? TitleId { get; set; }
    public string? ContentId { get; set; }
    public string? Region { get; set; }
    public string Console { get; set; } = "PS5";
    public string? Version { get; set; }
    public OutputFormat Format { get; set; } = OutputFormat.Ffpfsc;
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public byte[]? CoverImageBytes { get; set; }
    public LibraryTransferState UsbState { get; set; } = LibraryTransferState.NotTransferred;
    public LibraryTransferState ConsoleState { get; set; } = LibraryTransferState.NotTransferred;
    public string? LastUsbDestination { get; set; }
    public string? LastConsoleEndpoint { get; set; }
    public string? IncompleteDestinationPath { get; set; }
    public TransferDestinationKind? IncompleteKind { get; set; }
    public string StatusText => BuildStatus();

    private string BuildStatus()
    {
        if (IncompleteDestinationPath is not null)
        {
            return "Незавершённая передача";
        }

        var parts = new List<string>();
        if (UsbState == LibraryTransferState.Completed)
        {
            parts.Add("USB ✓");
        }
        else if (UsbState == LibraryTransferState.Failed)
        {
            parts.Add("USB ✕");
        }

        if (ConsoleState == LibraryTransferState.Completed)
        {
            parts.Add("Консоль ✓");
        }
        else if (ConsoleState == LibraryTransferState.Failed)
        {
            parts.Add("Консоль ✕");
        }

        return parts.Count == 0 ? "Готово" : string.Join(" · ", parts);
    }
}

public sealed class UsbDriveInfo
{
    public required string DriveLetter { get; init; }
    public string RootPath => DriveLetter.EndsWith(":\\", StringComparison.Ordinal) ? DriveLetter : DriveLetter.TrimEnd('\\') + "\\";
    public string? VolumeLabel { get; init; }
    public string? FileSystem { get; init; }
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public string? VolumeSerial { get; init; }
    public string DeviceKey => !string.IsNullOrWhiteSpace(VolumeSerial)
        ? "vol:" + VolumeSerial
        : "label:" + (VolumeLabel ?? "") + "|fs:" + (FileSystem ?? "") + "|size:" + TotalBytes;
    public bool IsRemovable { get; init; } = true;
}

public sealed class ApprovedUsbDevice
{
    public required string DeviceKey { get; init; }
    public string? VolumeLabel { get; init; }
    public string? VolumeSerial { get; init; }
    public string? LastDriveLetter { get; set; }
    public DateTimeOffset ApprovedAt { get; init; } = DateTimeOffset.Now;
}

public sealed class DiscoveredNetworkDevice
{
    public required string Host { get; init; }
    public int Port { get; init; } = 2121;
    public DiscoveredDeviceKind Kind { get; init; }
    public string DisplayName { get; init; } = "Устройство";
    public string Banner { get; init; } = string.Empty;
    public string Endpoint => $"{Host}:{Port}";
    public bool IsCompatible => Kind is DiscoveredDeviceKind.CompatibleFtp or DiscoveredDeviceKind.Ps5Craft;
}

public sealed class TransferProgress
{
    public long BytesTransferred { get; init; }
    public long TotalBytes { get; init; }
    public double? Percent => TotalBytes > 0 ? Math.Clamp(100.0 * BytesTransferred / TotalBytes, 0, 100) : null;
    public double BytesPerSecond { get; init; }
    public TimeSpan? EstimatedRemaining { get; init; }
    public string Phase { get; init; } = "transfer";
}

public sealed class TransferJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string LibraryItemId { get; init; }
    public required string Title { get; init; }
    public required string SourcePath { get; init; }
    public required TransferDestinationKind Kind { get; init; }
    public string? UsbRoot { get; init; }
    public string? ConsoleEndpoint { get; init; }
    public string? ConsoleRemoteDirectory { get; init; }
    public long TotalBytes { get; init; }
    public LibraryTransferState State { get; set; } = LibraryTransferState.Pending;
    public string? DestinationPath { get; set; }
    public string? Error { get; set; }
}

public sealed class FileCopyResult
{
    public bool Success { get; init; }
    public string? DestinationPath { get; init; }
    public string? Error { get; init; }
    public bool Cancelled { get; init; }
    public bool VerificationFailed { get; init; }

    public static FileCopyResult Ok(string dest) => new() { Success = true, DestinationPath = dest };
    public static FileCopyResult Fail(string error, bool verify = false) => new() { Success = false, Error = error, VerificationFailed = verify };
    public static FileCopyResult Cancel() => new() { Success = false, Cancelled = true, Error = "Отменено" };
}
