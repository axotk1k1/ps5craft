using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PS5Craft.Core;
using PS5Craft.Core.Abstractions;
using PS5Craft.Core.Models;
using PS5Craft.Services.Updates;

namespace PS5Craft.ViewModels;

public partial class MainViewModel
{
    private readonly IUpdateService _updates;
    private CancellationTokenSource? _updateCts;
    private UpdateInfo? _pendingUpdate;

    [ObservableProperty] private string _appVersionDisplay = AppVersion.Display;
    [ObservableProperty] private string _appVersionShortDisplay = AppVersion.DisplayShort;
    [ObservableProperty] private bool _checkUpdatesAutomatically = true;
    [ObservableProperty] private bool _includePrereleases;
    [ObservableProperty] private string _updateStatusText = "Проверка обновлений не выполнялась";
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private bool _isCheckingUpdates;
    [ObservableProperty] private bool _isDownloadingUpdate;
    [ObservableProperty] private double _updateDownloadPercent;
    [ObservableProperty] private string _updateDownloadPercentText = "0%";
    [ObservableProperty] private string _updateDownloadSizeText = string.Empty;
    [ObservableProperty] private string _updateChangelog = string.Empty;
    [ObservableProperty] private string _availableUpdateVersion = string.Empty;

    public event EventHandler<UpdateInfo>? UpdateAvailable;
    public event EventHandler? RequestCloseForUpdate;

    private void InitializeUpdateSettings()
    {
        var s = _settings.Current;
        CheckUpdatesAutomatically = s.CheckUpdatesAutomatically;
        IncludePrereleases = s.IncludePrereleases;
        AppVersionDisplay = AppVersion.Display;
        AppVersionShortDisplay = AppVersion.DisplayShort;
    }

    public async Task StartBackgroundUpdateCheckAsync()
    {
        if (!CheckUpdatesAutomatically)
        {
            return;
        }

        try
        {
            var result = await _updates.CheckForUpdatesAsync(force: false).ConfigureAwait(true);
            ApplyUpdateCheckResult(result, silentOnFailure: true, showDialogIfAvailable: true);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch
        {
            // Automatic checks must not alarm the user.
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheckUpdates))]
    private async Task CheckUpdatesAsync()
    {
        IsCheckingUpdates = true;
        UpdateStatusText = "Проверка обновлений...";
        CheckUpdatesCommand.NotifyCanExecuteChanged();
        try
        {
            var result = await _updates.CheckForUpdatesAsync(force: true).ConfigureAwait(true);
            ApplyUpdateCheckResult(result, silentOnFailure: false, showDialogIfAvailable: true);
        }
        finally
        {
            IsCheckingUpdates = false;
            CheckUpdatesCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanCheckUpdates() => !IsCheckingUpdates && !IsDownloadingUpdate;

    [RelayCommand]
    private void OpenUpdateDialog()
    {
        if (_pendingUpdate is { } info)
        {
            UpdateAvailable?.Invoke(this, info);
        }
    }

    [RelayCommand]
    private void OpenReleaseNotes()
    {
        var url = _pendingUpdate?.HtmlUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            url = $"https://github.com/{GitHubReleaseParser.Owner}/{GitHubReleaseParser.Repository}/releases";
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error("Не удалось открыть страницу релиза: " + ex.Message);
        }
    }

    public async Task<bool> DownloadAndApplyUpdateAsync(
        UpdateInfo info,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        IsDownloadingUpdate = true;
        CheckUpdatesCommand.NotifyCanExecuteChanged();
        CancelUpdateDownloadCommand.NotifyCanExecuteChanged();
        try
        {
            if (!_updates.IsInstallDirectoryWritable())
            {
                UpdateStatusText = "Каталог установки недоступен для записи. Нужны права администратора.";
                _log.Error(UpdateStatusText);
                return false;
            }

            var dir = Path.Combine(_settings.AppDataDirectory, "Updates");
            Directory.CreateDirectory(dir);
            var zipPath = Path.Combine(dir, GitHubReleaseParser.PreferredAssetName);
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            await _updates.DownloadAsync(info, zipPath, progress, cancellationToken).ConfigureAwait(true);

            if (cancellationToken.IsCancellationRequested)
            {
                TryDeleteFile(zipPath);
                return false;
            }

            if (!_updates.ValidateZipArchive(zipPath))
            {
                TryDeleteFile(zipPath);
                UpdateStatusText = "Ошибка проверки обновления.";
                _log.Error(UpdateStatusText);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(info.ChecksumUrl))
            {
                var expected = await _updates.FetchChecksumAsync(info, cancellationToken).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(expected) || !_updates.VerifySha256(zipPath, expected))
                {
                    TryDeleteFile(zipPath);
                    UpdateStatusText = "Ошибка проверки обновления.";
                    _log.Error("SHA-256 checksum mismatch.");
                    return false;
                }
            }

            await _updates.ApplyUpdateAsync(info, zipPath, cancellationToken).ConfigureAwait(true);
            UpdateStatusText = "Установка обновления… Перезапуск.";
            _log.Info($"Launching updater for {info.TagName}");
            RequestCloseForUpdate?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText = "Загрузка обновления отменена.";
            return false;
        }
        catch (Exception ex)
        {
            UpdateStatusText = "Не удалось установить обновление.";
            _log.Error(UpdateStatusText + " " + ex.Message);
            return false;
        }
        finally
        {
            IsDownloadingUpdate = false;
            CheckUpdatesCommand.NotifyCanExecuteChanged();
            CancelUpdateDownloadCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelUpdateDownload))]
    private void CancelUpdateDownload()
    {
        _updateCts?.Cancel();
    }

    private bool CanCancelUpdateDownload() => IsDownloadingUpdate;

    public CancellationTokenSource BeginUpdateDownloadCts()
    {
        _updateCts?.Cancel();
        _updateCts?.Dispose();
        _updateCts = new CancellationTokenSource();
        return _updateCts;
    }

    public void ReportDownloadProgress(UpdateDownloadProgress p)
    {
        if (p.Percent is { } pct)
        {
            UpdateDownloadPercent = pct;
            UpdateDownloadPercentText = string.Create(CultureInfo.InvariantCulture, $"{pct:0}%");
        }
        else
        {
            UpdateDownloadPercentText = "…";
        }

        if (p.TotalBytes is { } total)
        {
            UpdateDownloadSizeText = $"{FormatBytes(p.BytesReceived)} / {FormatBytes(total)}";
        }
        else
        {
            UpdateDownloadSizeText = FormatBytes(p.BytesReceived);
        }
    }

    private void ApplyUpdateCheckResult(UpdateCheckResult result, bool silentOnFailure, bool showDialogIfAvailable)
    {
        if (result.SkippedDueToRateLimit)
        {
            return;
        }

        if (!result.Succeeded)
        {
            if (!silentOnFailure)
            {
                UpdateStatusText = "Не удалось проверить обновления.\nВозможные причины: нет подключения к интернету, GitHub недоступен, ошибка сервера.";
            }

            return;
        }

        if (result.IsUpdateAvailable && result.UpdateInfo is { } info)
        {
            _pendingUpdate = info;
            IsUpdateAvailable = true;
            AvailableUpdateVersion = "v" + AppVersion.ToSemantic(info.Version);
            UpdateStatusText = $"Доступна новая версия {AvailableUpdateVersion}";
            UpdateChangelog = string.Join(Environment.NewLine, GitHubReleaseParser.FormatReleaseNotes(info.ReleaseNotes));
            _log.Info($"Update available: {info.TagName}");
            if (showDialogIfAvailable)
            {
                UpdateAvailable?.Invoke(this, info);
            }
        }
        else
        {
            _pendingUpdate = null;
            IsUpdateAvailable = false;
            AvailableUpdateVersion = string.Empty;
            UpdateChangelog = string.Empty;
            UpdateStatusText = silentOnFailure && result.LatestVersion is null
                ? UpdateStatusText
                : "Последняя версия установлена";
            if (!silentOnFailure)
            {
                UpdateStatusText = "Вы используете последнюю версию PS5Craft.";
            }
        }
    }

    private void PersistUpdateSettings(AppSettings s)
    {
        s.CheckUpdatesAutomatically = CheckUpdatesAutomatically;
        s.IncludePrereleases = IncludePrereleases;
    }

    private static void TryDeleteFile(string path)
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
