using System.Diagnostics;
using System.Security.Cryptography;
using PS5Craft.Core.Abstractions;
using PS5Craft.Core.Models;

namespace PS5Craft.Services.Usb;

public sealed class UsbExportService : IUsbExportService
{
    private readonly ILogService _log;

    public UsbExportService(ILogService log) => _log = log;

    public string BuildDestinationPath(UsbDriveInfo drive, LibraryItem item)
    {
        var safeTitle = SanitizeFolderName(string.IsNullOrWhiteSpace(item.Title) ? item.TitleId ?? "Game" : item.Title);
        var dir = Path.Combine(drive.RootPath, "PS5Craft", "Games", safeTitle);
        return Path.Combine(dir, item.FileName);
    }

    public async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(fs, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<FileCopyResult> CopyToUsbAsync(
        LibraryItem item,
        UsbDriveInfo drive,
        bool overwrite,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(item.FilePath))
        {
            return FileCopyResult.Fail("Исходный файл не найден.");
        }

        var srcInfo = new FileInfo(item.FilePath);
        if (drive.FreeBytes < srcInfo.Length)
        {
            return FileCopyResult.Fail(
                $"Недостаточно места. Файл: {FormatGb(srcInfo.Length)}, свободно: {FormatGb(drive.FreeBytes)}.");
        }

        var dest = BuildDestinationPath(drive, item);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        if (File.Exists(dest) && !overwrite)
        {
            return FileCopyResult.Fail("EXISTS:" + dest);
        }

        var partial = dest + ".partial";
        try
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }

            _log.Info("Starting USB copy...");
            await CopyFileStreamingAsync(item.FilePath, partial, srcInfo.Length, progress, () => DriveExists(drive.RootPath), cancellationToken)
                .ConfigureAwait(false);

            if (File.Exists(dest))
            {
                File.Delete(dest);
            }

            File.Move(partial, dest);

            _log.Info("USB copy completed");
            _log.Info("Verifying destination...");
            var expected = item.Sha256;
            if (string.IsNullOrWhiteSpace(expected))
            {
                expected = await ComputeSha256Async(item.FilePath, cancellationToken).ConfigureAwait(false);
            }

            var actual = await ComputeSha256Async(dest, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                _log.Error("USB verification failed");
                return FileCopyResult.Fail("Ошибка проверки файла.", verify: true);
            }

            _log.Success("USB verification successful");
            _log.Info("USB destination: " + dest);
            return FileCopyResult.Ok(dest);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partial);
            return FileCopyResult.Cancel();
        }
        catch (IOException ex) when (!DriveExists(drive.RootPath))
        {
            TryDelete(partial);
            return FileCopyResult.Fail("USB-накопитель отключён. " + ex.Message);
        }
        catch (Exception ex)
        {
            TryDelete(partial);
            return FileCopyResult.Fail(ex.Message);
        }
    }

    public static async Task CopyFileStreamingAsync(
        string source,
        string destination,
        long total,
        IProgress<TransferProgress>? progress,
        Func<bool>? stillAvailable,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        var buffer = new byte[bufferSize];
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);

        long copied = 0;
        var sw = Stopwatch.StartNew();
        long lastBytes = 0;
        var lastMark = sw.Elapsed;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (stillAvailable is not null && !stillAvailable())
            {
                throw new IOException("Destination drive unavailable.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;

            var elapsed = sw.Elapsed - lastMark;
            double speed = 0;
            if (elapsed.TotalSeconds >= 0.25)
            {
                speed = (copied - lastBytes) / Math.Max(elapsed.TotalSeconds, 0.001);
                lastBytes = copied;
                lastMark = sw.Elapsed;
            }
            else if (sw.Elapsed.TotalSeconds > 0)
            {
                speed = copied / sw.Elapsed.TotalSeconds;
            }

            TimeSpan? eta = null;
            if (speed > 1 && total > copied)
            {
                eta = TimeSpan.FromSeconds((total - copied) / speed);
            }

            progress?.Report(new TransferProgress
            {
                BytesTransferred = copied,
                TotalBytes = total,
                BytesPerSecond = speed,
                EstimatedRemaining = eta,
                Phase = "copy"
            });
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool DriveExists(string root)
    {
        try
        {
            var letter = root.TrimEnd('\\');
            return DriveInfo.GetDrives().Any(d =>
                d.IsReady && d.Name.StartsWith(letter, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static string SanitizeFolderName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        name = name.Trim();
        return string.IsNullOrWhiteSpace(name) ? "Game" : name;
    }

    private static string FormatGb(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";

    private static void TryDelete(string path)
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
            // ignore
        }
    }
}
