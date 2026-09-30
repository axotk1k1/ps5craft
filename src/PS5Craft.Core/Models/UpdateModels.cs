namespace PS5Craft.Core.Models;

public sealed class UpdateInfo
{
    public required Version Version { get; init; }
    public required string TagName { get; init; }
    public string ReleaseName { get; init; } = string.Empty;
    public string ReleaseNotes { get; init; } = string.Empty;
    public DateTimeOffset? PublishedAt { get; init; }
    public required string DownloadUrl { get; init; }
    public long DownloadSize { get; init; }
    public string? ChecksumUrl { get; init; }
    public string? HtmlUrl { get; init; }
    public string AssetName { get; init; } = "PS5Craft-win-x64.zip";
    public bool IsPrerelease { get; init; }
}

public sealed class UpdateCheckResult
{
    public bool IsUpdateAvailable { get; init; }
    public required Version CurrentVersion { get; init; }
    public Version? LatestVersion { get; init; }
    public UpdateInfo? UpdateInfo { get; init; }
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public bool SkippedDueToRateLimit { get; init; }

    public static UpdateCheckResult UpToDate(Version current, Version latest) => new()
    {
        Succeeded = true,
        IsUpdateAvailable = false,
        CurrentVersion = current,
        LatestVersion = latest
    };

    public static UpdateCheckResult Available(Version current, UpdateInfo info) => new()
    {
        Succeeded = true,
        IsUpdateAvailable = true,
        CurrentVersion = current,
        LatestVersion = info.Version,
        UpdateInfo = info
    };

    public static UpdateCheckResult Failed(Version current, string message) => new()
    {
        Succeeded = false,
        IsUpdateAvailable = false,
        CurrentVersion = current,
        ErrorMessage = message
    };

    public static UpdateCheckResult Skipped(Version current) => new()
    {
        Succeeded = true,
        IsUpdateAvailable = false,
        CurrentVersion = current,
        SkippedDueToRateLimit = true
    };
}

public sealed class UpdateDownloadProgress
{
    public long BytesReceived { get; init; }
    public long? TotalBytes { get; init; }
    public double? Percent => TotalBytes is > 0 ? Math.Clamp(100.0 * BytesReceived / TotalBytes.Value, 0, 100) : null;
}
