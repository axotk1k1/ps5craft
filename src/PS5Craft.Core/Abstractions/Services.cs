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
