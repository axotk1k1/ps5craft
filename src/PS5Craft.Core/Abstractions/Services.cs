using PS5Craft.Core.Models;

namespace PS5Craft.Core.Abstractions;

public interface ISettingsService
{
    AppSettings Current { get; }
    string SettingsPath { get; }
    string TempDirectory { get; }
    string LogsDirectory { get; }
    string AppDataDirectory { get; }
    event EventHandler? SettingsChanged;
    AppSettings Load();
    void Save(AppSettings settings);
    void Reload();
}

public interface ILogService
{
    event EventHandler<LogEntry>? EntryAdded;
    IReadOnlyList<LogEntry> Entries { get; }
    void Info(string message);
    void Warning(string message);
    void Error(string message);
    void Success(string message);
    void Clear();
    Task SaveAsync(string? path = null);
    void OpenLogFolder();
}

public interface IMkPfsService
{
    Task<ToolInfo> DetectAsync(CancellationToken cancellationToken = default);
    Task<ToolInfo> TestAsync(CancellationToken cancellationToken = default);
    Task<OperationResult> PackFolderAsync(
        CompressionSettings settings,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<OperationResult> VerifyAsync(string imagePath, CancellationToken cancellationToken = default);
    Task<OperationResult> UnpackAsync(string imagePath, string outputDir, bool deep = true, CancellationToken cancellationToken = default);
}

public interface IFpkgService
{
    Task<ToolInfo> DetectAsync(CancellationToken cancellationToken = default);
    Task<GameInfo> InspectAsync(string packagePath, string? passcode = null, CancellationToken cancellationToken = default);
    Task<OperationResult> ExtractAsync(
        ExtractionSettings settings,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<OperationResult> VerifyAsync(string packagePath, bool full = false, CancellationToken cancellationToken = default);
}

public interface IGameMetadataService
{
    Task<GameInfo> ReadFromPackageAsync(string path, CancellationToken cancellationToken = default);
    Task<GameInfo> ReadFromFolderAsync(string folder, CancellationToken cancellationToken = default);
}

public interface IResourceMonitor : IAsyncDisposable
{
    event EventHandler<OperationProgress>? Updated;
    void Attach(int processId);
    void Detach();
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
}

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckForUpdatesAsync(bool force = false, CancellationToken cancellationToken = default);
    Task DownloadAsync(
        UpdateInfo info,
        string destinationPath,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<string?> FetchChecksumAsync(UpdateInfo info, CancellationToken cancellationToken = default);
    bool VerifySha256(string filePath, string expectedHex);
    bool ValidateZipArchive(string filePath);
    bool IsInstallDirectoryWritable(string? directory = null);
    Task ApplyUpdateAsync(UpdateInfo info, string zipPath, CancellationToken cancellationToken = default);
}

public interface ILibraryService
{
    event EventHandler? Changed;
    IReadOnlyList<LibraryItem> Items { get; }
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CancellationToken cancellationToken = default);
    Task<LibraryItem> RegisterPackedImageAsync(
        string imagePath,
        GameInfo? game,
        OutputFormat format,
        string? sha256 = null,
        CancellationToken cancellationToken = default);
    Task UpdateAsync(LibraryItem item, CancellationToken cancellationToken = default);
    Task RemoveAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>Removes library entries whose packed image file no longer exists. Returns how many were removed.</summary>
    Task<int> PruneMissingAsync(CancellationToken cancellationToken = default);
    LibraryItem? FindById(string id);
    IReadOnlyList<LibraryItem> GetIncompleteTransfers();
}

public interface IUsbDriveService
{
    event EventHandler<UsbDriveInfo>? DriveArrived;
    event EventHandler<UsbDriveInfo>? DriveRemoved;
    IReadOnlyList<UsbDriveInfo> GetRemovableDrives();
    UsbDriveInfo? FindApprovedConnected(IEnumerable<ApprovedUsbDevice> approved);
    bool IsApproved(UsbDriveInfo drive, IEnumerable<ApprovedUsbDevice> approved);
    ApprovedUsbDevice Approve(UsbDriveInfo drive);
    void StartWatching();
    void StopWatching();
}

public interface IUsbExportService
{
    string BuildDestinationPath(UsbDriveInfo drive, LibraryItem item);
    Task<FileCopyResult> CopyToUsbAsync(
        LibraryItem item,
        UsbDriveInfo drive,
        bool overwrite,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default);
}

public interface IPs5NetworkDiscoveryService
{
    Task<IReadOnlyList<DiscoveredNetworkDevice>> DiscoverAsync(
        int port = 2121,
        int maxConcurrency = 48,
        int timeoutMs = 700,
        CancellationToken cancellationToken = default);
    Task<DiscoveredNetworkDevice?> ProbeHostAsync(string host, int port = 2121, int timeoutMs = 700, CancellationToken cancellationToken = default);
}

public interface IPs5TransferService
{
    Task<bool> IsAvailableAsync(string host, int port = 2121, CancellationToken cancellationToken = default);
    Task<FileCopyResult> SendFileAsync(
        string host,
        int port,
        string localPath,
        string remoteFileName,
        string? remoteDirectory = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface ITransferQueueService
{
    event EventHandler? Changed;
    event EventHandler<TransferProgress>? ProgressChanged;
    event EventHandler<TransferJob>? JobFinished;
    IReadOnlyList<TransferJob> Jobs { get; }
    TransferJob? ActiveJob { get; }
    void EnqueueUsb(LibraryItem item, UsbDriveInfo drive);
    void EnqueueConsole(LibraryItem item, DiscoveredNetworkDevice device);
    Task StartAsync(CancellationToken cancellationToken = default);
    void CancelActive();
}
