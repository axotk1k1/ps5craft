using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using PS5Craft.Core;
using PS5Craft.Core.Abstractions;
using PS5Craft.Core.Models;

namespace PS5Craft.Services.Updates;

public sealed class GitHubUpdateService : IUpdateService, IDisposable
{
    public const string UserAgent = "PS5Craft-Updater";
    public static readonly TimeSpan MinAutoCheckInterval = TimeSpan.FromHours(6);

    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly bool _ownsHttp;
    private readonly string _appDirectory;
    private readonly Func<Version> _currentVersionFactory;

    public GitHubUpdateService(ISettingsService settings, HttpClient? httpClient = null, string? appDirectory = null, Func<Version>? currentVersionFactory = null)
    {
        _settings = settings;
        _appDirectory = appDirectory ?? AppContext.BaseDirectory;
        _currentVersionFactory = currentVersionFactory ?? (() => AppVersion.Current);
        if (httpClient is null)
        {
            _http = CreateHttpClient();
            _ownsHttp = true;
        }
        else
        {
            _http = httpClient;
            _ownsHttp = false;
        }
    }

    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var current = _currentVersionFactory();
        var settings = _settings.Current;

        if (!force
            && settings.LastUpdateCheckUtc is { } last
            && DateTimeOffset.UtcNow - last < MinAutoCheckInterval)
        {
            return UpdateCheckResult.Skipped(current);
        }

        try
        {
            UpdateInfo? info;
            if (settings.IncludePrereleases)
            {
                var listJson = await GetStringAsync(
                    $"https://api.github.com/repos/{GitHubReleaseParser.Owner}/{GitHubReleaseParser.Repository}/releases?per_page=20",
                    cancellationToken).ConfigureAwait(false);
                info = GitHubReleaseParser.ParseReleasesList(listJson, includePrereleases: true);
            }
            else
            {
                var latestJson = await GetStringAsync(
                    $"https://api.github.com/repos/{GitHubReleaseParser.Owner}/{GitHubReleaseParser.Repository}/releases/latest",
                    cancellationToken).ConfigureAwait(false);
                info = GitHubReleaseParser.ParseLatestRelease(latestJson, includePrereleases: false);
            }

            PersistLastCheck();

            if (info is null)
            {
                return UpdateCheckResult.Failed(current, "В релизе не найден пакет PS5Craft-win-x64.zip.");
            }

            if (info.Version > current)
            {
                return UpdateCheckResult.Available(current, info);
            }

            return UpdateCheckResult.UpToDate(current, info.Version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Failed(current, ex.Message);
        }
    }

    public async Task DownloadAsync(
        UpdateInfo info,
        string destinationPath,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!GitHubReleaseParser.IsTrustedDownloadUrl(info.DownloadUrl))
        {
            throw new InvalidOperationException("URL загрузки не принадлежит github.com/axotk1k1/ps5craft.");
        }

        if (!info.AssetName.Equals(GitHubReleaseParser.PreferredAssetName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Недопустимый asset: {info.AssetName}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        try
        {
            using var response = await _http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {(int)response.StatusCode} при загрузке обновления.");
            }

            var total = response.Content.Headers.ContentLength ?? (info.DownloadSize > 0 ? info.DownloadSize : null);
            await using var remote = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var local = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await remote.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await local.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                progress?.Report(new UpdateDownloadProgress { BytesReceived = received, TotalBytes = total });
            }

            await local.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (total is > 0 && received != total)
            {
                throw new InvalidOperationException($"Размер файла не совпал: получено {received}, ожидалось {total}.");
            }

            if (info.DownloadSize > 0 && received != info.DownloadSize && total is null)
            {
                if (Math.Abs(received - info.DownloadSize) > 1024)
                {
                    throw new InvalidOperationException($"Размер файла не совпал с релизом: {received} ≠ {info.DownloadSize}.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(destinationPath);
            throw;
        }
        catch
        {
            TryDelete(destinationPath);
            throw;
        }
    }

    public async Task<string?> FetchChecksumAsync(UpdateInfo info, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(info.ChecksumUrl) || !GitHubReleaseParser.IsTrustedDownloadUrl(info.ChecksumUrl))
        {
            return null;
        }

        var text = await GetStringAsync(info.ChecksumUrl, cancellationToken).ConfigureAwait(false);
        return GitHubReleaseParser.ParseSha256File(text);
    }

    public bool VerifySha256(string filePath, string expectedHex)
    {
        var expected = GitHubReleaseParser.ParseSha256File(expectedHex) ?? expectedHex.Trim().ToLowerInvariant();
        if (expected.Length != 64)
        {
            return false;
        }

        using var stream = File.OpenRead(filePath);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return hash.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    public bool ValidateZipArchive(string filePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(filePath);
            if (zip.Entries.Count == 0)
            {
                return false;
            }

            // Force CRC validation by opening each entry stream briefly.
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                using var s = entry.Open();
                _ = s.ReadByte();
            }

            return zip.Entries.Any(e =>
                e.Name.Equals("PS5Craft.exe", StringComparison.OrdinalIgnoreCase)
                || e.FullName.EndsWith("/PS5Craft.exe", StringComparison.OrdinalIgnoreCase)
                || e.FullName.EndsWith("\\PS5Craft.exe", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public bool IsInstallDirectoryWritable(string? directory = null)
    {
        var dir = directory ?? _appDirectory;
        try
        {
            var probe = Path.Combine(dir, $".ps5craft-write-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok", Encoding.ASCII);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task ApplyUpdateAsync(UpdateInfo info, string zipPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException("Пакет обновления не найден.", zipPath);
        }

        if (!ValidateZipArchive(zipPath))
        {
            throw new InvalidOperationException("Ошибка проверки обновления: архив повреждён или не содержит PS5Craft.exe.");
        }

        if (!string.IsNullOrWhiteSpace(info.ChecksumUrl))
        {
            var expected = await FetchChecksumAsync(info, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(expected) || !VerifySha256(zipPath, expected))
            {
                throw new InvalidOperationException("Ошибка проверки обновления: SHA-256 не совпал.");
            }
        }

        if (!IsInstallDirectoryWritable(_appDirectory))
        {
            throw new UnauthorizedAccessException(
                "Каталог установки недоступен для записи. Запустите PS5Craft от имени администратора или переместите программу в доступную папку.");
        }

        var updaterSource = Path.Combine(_appDirectory, "PS5Craft.Updater.exe");
        if (!File.Exists(updaterSource))
        {
            throw new FileNotFoundException("PS5Craft.Updater.exe не найден рядом с приложением.", updaterSource);
        }

        var work = Path.Combine(Path.GetTempPath(), "PS5Craft", "update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var updaterCopy = Path.Combine(work, "PS5Craft.Updater.exe");
        File.Copy(updaterSource, updaterCopy, overwrite: true);

        // Include companion files if framework-dependent updater was published with them.
        foreach (var companion in Directory.EnumerateFiles(_appDirectory, "PS5Craft.Updater.*"))
        {
            var name = Path.GetFileName(companion);
            if (name.Equals("PS5Craft.Updater.exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(companion, Path.Combine(work, name), overwrite: true);
        }

        var backupDir = Path.Combine(Path.GetTempPath(), "PS5Craft", "backup-" + Guid.NewGuid().ToString("N"));
        var exeName = "PS5Craft.exe";
        var psi = new ProcessStartInfo
        {
            FileName = updaterCopy,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = work
        };
        psi.ArgumentList.Add("--pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--app-dir");
        psi.ArgumentList.Add(_appDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        psi.ArgumentList.Add("--package");
        psi.ArgumentList.Add(zipPath);
        psi.ArgumentList.Add("--backup-dir");
        psi.ArgumentList.Add(backupDir);
        psi.ArgumentList.Add("--restart");
        psi.ArgumentList.Add(Path.Combine(_appDirectory, exeName));

        if (Process.Start(psi) is null)
        {
            throw new InvalidOperationException("Не удалось запустить PS5Craft.Updater.exe.");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} от GitHub API.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private void PersistLastCheck()
    {
        var s = _settings.Current;
        s.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        _settings.Save(s);
    }

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
            // ignore cleanup failures
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
