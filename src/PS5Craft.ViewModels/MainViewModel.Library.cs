using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PS5Craft.Core;
using PS5Craft.Core.Abstractions;
using PS5Craft.Core.Models;

namespace PS5Craft.ViewModels;

public sealed partial class ConsoleDeviceViewModel : ObservableObject
{
    public ConsoleDeviceViewModel(DiscoveredNetworkDevice device)
    {
        Device = device;
        DisplayName = device.DisplayName;
        Endpoint = device.Endpoint;
        RefreshSelection(null, connected: false);
    }

    public DiscoveredNetworkDevice Device { get; }

    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _endpoint = "";
    [ObservableProperty] private string _statusLabel = "";
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _actionButtonText = "Выбрать";

    public void RefreshSelection(DiscoveredNetworkDevice? selected, bool connected)
    {
        IsSelected = selected is not null
            && string.Equals(selected.Endpoint, Device.Endpoint, StringComparison.OrdinalIgnoreCase);
        IsConnected = IsSelected && connected;
        if (IsConnected)
        {
            StatusLabel = "Соединение установлено";
            ActionButtonText = "Выбрана";
        }
        else if (IsSelected)
        {
            StatusLabel = "Выбрана · нет связи";
            ActionButtonText = "Повторить";
        }
        else if (!Device.IsCompatible)
        {
            StatusLabel = "Неизвестное устройство";
            ActionButtonText = "—";
        }
        else
        {
            StatusLabel = "Готово к выбору";
            ActionButtonText = "Выбрать";
        }
    }
}

public sealed partial class LibraryItemViewModel : ObservableObject
{
    public LibraryItem Model { get; }

    public LibraryItemViewModel(LibraryItem model)
    {
        Model = model;
        RefreshFromModel();
        CoverImage = TryLoadCover(model.CoverImageBytes);
    }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _titleId = "";
    [ObservableProperty] private string _metaLine = "";
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _formatText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _createdText = "";
    [ObservableProperty] private BitmapImage? _coverImage;
    [ObservableProperty] private bool _isTransferring;
    [ObservableProperty] private double _transferPercent;
    [ObservableProperty] private string _transferPercentText = "0%";
    [ObservableProperty] private string _transferDetailText = "";

    public void RefreshFromModel()
    {
        Title = Model.Title;
        TitleId = Model.TitleId ?? "—";
        MetaLine = $"{Model.Console} • {Model.Region ?? "—"}";
        SizeText = FormatSize(Model.FileSizeBytes);
        FormatText = Model.Format switch
        {
            OutputFormat.Ffpfs => "FFPFS",
            OutputFormat.Exfat => "EXFAT",
            _ => "FFPFSC"
        };
        StatusText = Model.StatusText;
        FilePath = Model.FilePath;
        CreatedText = Model.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        if (!IsTransferring)
        {
            TransferPercent = 0;
            TransferPercentText = "0%";
            TransferDetailText = string.Empty;
        }
    }

    public void ApplyProgress(TransferProgress p)
    {
        IsTransferring = true;
        if (p.Percent is { } pct)
        {
            TransferPercent = pct;
            TransferPercentText = string.Create(CultureInfo.InvariantCulture, $"{pct:0}%");
        }

        var speed = p.BytesPerSecond > 0 ? FormatSize((long)p.BytesPerSecond) + "/s" : "—";
        var eta = p.EstimatedRemaining is { } e ? e.ToString(@"mm\:ss") : "—";
        TransferDetailText = $"{FormatSize(p.BytesTransferred)} / {FormatSize(p.TotalBytes)} · {speed} · ETA {eta}";
        StatusText = p.Phase == "network" ? "Отправка…" : "Копирование…";
    }

    public void ClearProgress()
    {
        IsTransferring = false;
        TransferPercent = 0;
        TransferPercentText = "0%";
        TransferDetailText = string.Empty;
        StatusText = Model.StatusText;
    }

    private static string FormatSize(long bytes)
    {
        double gb = bytes / (1024.0 * 1024 * 1024);
        if (gb >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{gb:0.##} GB");
        }

        double mb = bytes / (1024.0 * 1024);
        return string.Create(CultureInfo.InvariantCulture, $"{mb:0.##} MB");
    }

    private static BitmapImage? TryLoadCover(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            var img = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }
}

public partial class MainViewModel
{
    private readonly ILibraryService _library;
    private readonly IUsbDriveService _usb;
    private readonly IUsbExportService _usbExport;
    private readonly IPs5NetworkDiscoveryService _discovery;
    private readonly IPs5TransferService _consoleTransfer;
    private readonly ITransferQueueService _transferQueue;

    public ObservableCollection<LibraryItemViewModel> LibraryItems { get; } = new();
    public ObservableCollection<ConsoleDeviceViewModel> DiscoveredDevices { get; } = new();

    [ObservableProperty] private LibraryItemViewModel? _selectedLibraryItem;
    [ObservableProperty] private string _ps5StatusText = "● Не найдена";
    [ObservableProperty] private string _ps5EndpointText = "—";
    [ObservableProperty] private string _ps5ConnectionHint = "Выберите устройство из списка";
    [ObservableProperty] private bool _ps5Found;
    [ObservableProperty] private bool _isConsoleConnected;
    [ObservableProperty] private DiscoveredNetworkDevice? _selectedConsole;
    [ObservableProperty] private string _preferredUsbText = "не выбран";
    [ObservableProperty] private string _transferStatusText = string.Empty;
    [ObservableProperty] private double _transferPercent;
    [ObservableProperty] private string _transferPercentText = "";
    [ObservableProperty] private string _transferDetailText = "";
    [ObservableProperty] private bool _isTransferring;
    [ObservableProperty] private bool _isDiscovering;

    [ObservableProperty] private bool _detectUsbAutomatically = true;
    [ObservableProperty] private bool _autoCopyToUsb;
    [ObservableProperty] private bool _confirmBeforeUsbCopy = true;
    [ObservableProperty] private bool _detectConsoleAutomatically = true;
    [ObservableProperty] private bool _autoSendToConsole;
    [ObservableProperty] private bool _confirmBeforeConsoleTransfer = true;
    [ObservableProperty] private string _consoleRemoteDirectory = "/data/etaHEN/homebrew";

    public event EventHandler<UsbDriveInfo>? UsbConfirmationRequired;
    public event EventHandler<(LibraryItem Item, UsbDriveInfo Drive)>? UsbExportOfferRequired;
    public event EventHandler<(LibraryItem Item, DiscoveredNetworkDevice Device)>? ConsoleTransferOfferRequired;
    public event EventHandler<(string Title, string Message)>? UserNoticeRequired;
    public event EventHandler<(string Path, TaskCompletionSource<bool> Answer)>? OverwriteConfirmationRequired;
    public event EventHandler<(string Title, string Message, string ConfirmText, TaskCompletionSource<bool> Answer)>? ConfirmRequired;

    private void InitializeExportSubsystem()
    {
        var s = _settings.Current;
        DetectUsbAutomatically = s.DetectUsbAutomatically;
        AutoCopyToUsb = s.AutoCopyToUsb;
        ConfirmBeforeUsbCopy = s.ConfirmBeforeUsbCopy;
        DetectConsoleAutomatically = s.DetectConsoleAutomatically;
        AutoSendToConsole = s.AutoSendToConsole;
        ConfirmBeforeConsoleTransfer = s.ConfirmBeforeConsoleTransfer;
        ConsoleRemoteDirectory = string.IsNullOrWhiteSpace(s.ConsoleRemoteDirectory)
            ? "/data/etaHEN/homebrew"
            : s.ConsoleRemoteDirectory;
        UpdatePreferredUsbText();

        _library.Changed += (_, _) =>
        {
            var d = System.Windows.Application.Current?.Dispatcher;
            if (d is not null)
            {
                _ = d.BeginInvoke(ReloadLibraryUi);
            }
        };
        _usb.DriveArrived += OnUsbArrived;
        _usb.DriveRemoved += (_, drive) => _log.Info($"USB removed: {drive.DriveLetter}");
        _transferQueue.Changed += (_, _) =>
        {
            var d = System.Windows.Application.Current?.Dispatcher;
            if (d is not null)
            {
                _ = d.BeginInvoke(UpdateTransferUi);
            }
        };
        _transferQueue.ProgressChanged += (_, p) =>
        {
            var d = System.Windows.Application.Current?.Dispatcher;
            if (d is not null)
            {
                _ = d.BeginInvoke(() => ApplyTransferProgress(p));
            }
        };
        _transferQueue.JobFinished += (_, job) =>
        {
            var d = System.Windows.Application.Current?.Dispatcher;
            if (d is not null)
            {
                _ = d.BeginInvoke(() => OnTransferJobFinished(job));
            }
        };

        _ = InitLibraryAsync();
    }

    private async Task InitLibraryAsync()
    {
        try
        {
            await _library.LoadAsync().ConfigureAwait(false);
            var pruned = await _library.PruneMissingAsync().ConfigureAwait(false);

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null)
            {
                await dispatcher.InvokeAsync(() =>
                {
                    ReloadLibraryUi();
                    if (pruned > 0)
                    {
                        _log.Info($"Removed {pruned} library item(s) with missing image files");
                    }

                    foreach (var incomplete in _library.GetIncompleteTransfers())
                    {
                        UserNoticeRequired?.Invoke(this, (
                            "Незавершённая передача",
                            $"{incomplete.Title}\n{incomplete.IncompleteDestinationPath}\nОткройте Библиотеку, чтобы продолжить, начать заново или удалить неполный файл."));
                    }
                });
            }

            if (DetectUsbAutomatically)
            {
                // DriveInfo / GetVolumeInformation can stall; never run the first poll on the UI thread.
                await Task.Run(() => _usb.StartWatching()).ConfigureAwait(false);
            }

            await _transferQueue.StartAsync().ConfigureAwait(false);

            // Auto network scan is heavy (full /24 TCP probes). Delay so the window can paint first.
            if (DetectConsoleAutomatically)
            {
                _ = DelayedAutoDiscoverAsync();
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Library init failed: {ex.Message}");
        }
    }

    private async Task DelayedAutoDiscoverAsync()
    {
        try
        {
            await Task.Delay(2500).ConfigureAwait(false);
            await DiscoverConsolesCoreAsync(silent: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warning($"Auto console discovery failed: {ex.Message}");
        }
    }

    private void ReloadLibraryUi()
    {
        var progressById = LibraryItems
            .Where(i => i.IsTransferring)
            .ToDictionary(i => i.Model.Id, i => i);

        LibraryItems.Clear();
        foreach (var item in _library.Items)
        {
            var vm = new LibraryItemViewModel(item);
            if (progressById.TryGetValue(item.Id, out var prev))
            {
                vm.IsTransferring = prev.IsTransferring;
                vm.TransferPercent = prev.TransferPercent;
                vm.TransferPercentText = prev.TransferPercentText;
                vm.TransferDetailText = prev.TransferDetailText;
                if (prev.IsTransferring)
                {
                    vm.StatusText = prev.StatusText;
                }
            }

            LibraryItems.Add(vm);
        }
    }

    private void UpdatePreferredUsbText()
    {
        var approved = _settings.Current.ApprovedUsbDevices;
        if (approved.Count == 0)
        {
            PreferredUsbText = "не выбран";
            return;
        }

        var a = approved[0];
        PreferredUsbText = $"{a.LastDriveLetter ?? "?"} — {a.VolumeLabel ?? a.DeviceKey}";
    }

    private void OnUsbArrived(object? sender, UsbDriveInfo drive)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(() =>
        {
            var s = _settings.Current;
            if (_usb.IsApproved(drive, s.ApprovedUsbDevices))
            {
                var match = s.ApprovedUsbDevices.First(a => a.DeviceKey == drive.DeviceKey);
                match.LastDriveLetter = drive.DriveLetter;
                _settings.Save(s);
                UpdatePreferredUsbText();
                _log.Info("Approved USB recognized: " + drive.DriveLetter);
                return;
            }

            UsbConfirmationRequired?.Invoke(this, drive);
        });
    }

    public void ConfirmUsbDrive(UsbDriveInfo drive)
    {
        var s = _settings.Current;
        var approved = _usb.Approve(drive);
        s.ApprovedUsbDevices.RemoveAll(a => a.DeviceKey == approved.DeviceKey);
        s.ApprovedUsbDevices.Insert(0, approved);
        _settings.Save(s);
        UpdatePreferredUsbText();
        _log.Info("USB device confirmed by user");
        UserNoticeRequired?.Invoke(this, ("USB", $"Накопитель {drive.DriveLetter} сохранён для PS5Craft.\nТеперь можно нажать «На USB»."));

        // If the user started export before approving, continue with the selected library item.
        if (SelectedLibraryItem is not null &&
            drive.FreeBytes >= SelectedLibraryItem.Model.FileSizeBytes)
        {
            UsbExportOfferRequired?.Invoke(this, (SelectedLibraryItem.Model, drive));
        }
    }

    public async Task OnPackSucceededAsync(OperationResult result, CompressionSettings settings)
    {
        if (!result.Success || string.IsNullOrWhiteSpace(result.OutputPath) || !File.Exists(result.OutputPath))
        {
            return;
        }

        string? sha = null;
        try
        {
            sha = await _usbExport.ComputeSha256Async(result.OutputPath);
        }
        catch
        {
            // optional
        }

        GameInfo? meta = Game.Title is not null && Game.Title != "Unknown" ? Game : null;
        if (meta is null && Directory.Exists(settings.SourceFolder))
        {
            try
            {
                meta = await _metadata.ReadFromFolderAsync(settings.SourceFolder);
            }
            catch
            {
                // ignore
            }
        }

        var item = await _library.RegisterPackedImageAsync(result.OutputPath, meta, settings.Format, sha);
        ReloadLibraryUi();
        _log.Success($"Добавлено в библиотеку: {item.Title}");

        var s = _settings.Current;
        var usb = _usb.FindApprovedConnected(s.ApprovedUsbDevices);
        if (usb is not null)
        {
            if (s.AutoCopyToUsb && !s.ConfirmBeforeUsbCopy)
            {
                _transferQueue.EnqueueUsb(item, usb);
            }
            else
            {
                UsbExportOfferRequired?.Invoke(this, (item, usb));
            }
        }

        if (s.AutoSendToConsole && SelectedConsole is { IsCompatible: true } console)
        {
            if (!s.ConfirmBeforeConsoleTransfer)
            {
                if (item.ConsoleState != LibraryTransferState.Completed)
                {
                    _transferQueue.EnqueueConsole(item, console);
                }
            }
            else
            {
                ConsoleTransferOfferRequired?.Invoke(this, (item, console));
            }
        }
    }

    [RelayCommand]
    private Task DiscoverConsolesAsync() => DiscoverConsolesCoreAsync(silent: false);

    private async Task DiscoverConsolesCoreAsync(bool silent)
    {
        if (IsDiscovering)
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null)
        {
            await dispatcher.InvokeAsync(() =>
            {
                IsDiscovering = true;
                if (!silent)
                {
                    Ps5StatusText = "● Поиск…";
                }
            });
        }
        else
        {
            IsDiscovering = true;
        }

        try
        {
            var devices = await _discovery.DiscoverAsync().ConfigureAwait(false);
            var preferred = _settings.Current.PreferredConsoleEndpoint;
            var selected = devices.FirstOrDefault(d => d.IsCompatible && d.Endpoint == preferred)
                ?? devices.FirstOrDefault(d => d.IsCompatible);

            if (dispatcher is null)
            {
                return;
            }

            await dispatcher.InvokeAsync(() =>
            {
                DiscoveredDevices.Clear();
                foreach (var d in devices)
                {
                    DiscoveredDevices.Add(new ConsoleDeviceViewModel(d));
                }

                SelectedConsole = null;
                IsConsoleConnected = false;

                if (selected is not null)
                {
                    Ps5Found = true;
                    Ps5StatusText = "● Найдена";
                    Ps5EndpointText = $"{selected.DisplayName} · {selected.Endpoint}";
                    Ps5ConnectionHint = "Нажмите «Выбрать», чтобы установить соединение";
                }
                else if (devices.Count > 0)
                {
                    Ps5Found = false;
                    Ps5StatusText = "● Неизвестные устройства";
                    Ps5EndpointText = $"Найдено: {devices.Count}";
                    Ps5ConnectionHint = "Совместимая консоль не найдена";
                }
                else
                {
                    Ps5Found = false;
                    Ps5StatusText = "● Не найдена";
                    Ps5EndpointText = "—";
                    Ps5ConnectionHint = "Нажмите «Найти устройства»";
                }

                // Preferred device first in the list for convenience.
                if (selected is not null)
                {
                    var preferredRow = DiscoveredDevices.FirstOrDefault(d =>
                        string.Equals(d.Endpoint, selected.Endpoint, StringComparison.OrdinalIgnoreCase));
                    if (preferredRow is not null)
                    {
                        DiscoveredDevices.Remove(preferredRow);
                        DiscoveredDevices.Insert(0, preferredRow);
                    }
                }

                RefreshConsoleDeviceRows();
            });
        }
        finally
        {
            if (dispatcher is not null)
            {
                await dispatcher.InvokeAsync(() => IsDiscovering = false);
            }
            else
            {
                IsDiscovering = false;
            }
        }
    }

    [RelayCommand]
    private async Task SelectConsole(ConsoleDeviceViewModel? row)
    {
        var device = row?.Device;
        if (device is null || !device.IsCompatible)
        {
            UserNoticeRequired?.Invoke(this, ("Консоль", "Нельзя выбрать неизвестное устройство."));
            return;
        }

        SelectedConsole = device;
        Ps5Found = true;
        IsConsoleConnected = false;
        Ps5StatusText = "● Проверка соединения…";
        Ps5EndpointText = $"{device.DisplayName} · {device.Endpoint}";
        Ps5ConnectionHint = "Проверяем ответ FTP/сервиса на консоли…";
        RefreshConsoleDeviceRows();

        var ok = await _consoleTransfer.IsAvailableAsync(device.Host, device.Port).ConfigureAwait(true);
        if (!ok)
        {
            IsConsoleConnected = false;
            Ps5StatusText = "● Нет связи";
            Ps5ConnectionHint = "Соединение не установлено — включите FTP на PS5 и нажмите «Повторить»";
            RefreshConsoleDeviceRows();
            _log.Warning($"Console selected but not reachable: {device.Endpoint}");
            UserNoticeRequired?.Invoke(this, (
                "Консоль",
                $"Устройство найдено, но сейчас не отвечает:\n{device.Endpoint}\n\nПроверьте, что FTP/сервис на PS5 запущен, затем нажмите «Выбрать» ещё раз."));
            return;
        }

        IsConsoleConnected = true;
        Ps5StatusText = "● Соединение установлено";
        Ps5EndpointText = $"{device.DisplayName} · {device.Endpoint}";
        Ps5ConnectionHint = "Готово — можно нажать «На консоль» для передачи";
        var s = _settings.Current;
        s.PreferredConsoleEndpoint = device.Endpoint;
        _settings.Save(s);
        RefreshConsoleDeviceRows();
        _log.Success($"Console selected and connection verified: {device.Endpoint} ({device.DisplayName})");
        UserNoticeRequired?.Invoke(this, (
            "Консоль",
            $"Соединение установлено.\n\n{device.DisplayName}\n{device.Endpoint}\n\nМожно нажать «На консоль» для передачи."));
    }

    private void RefreshConsoleDeviceRows()
    {
        foreach (var row in DiscoveredDevices)
        {
            row.RefreshSelection(SelectedConsole, IsConsoleConnected);
        }
    }

    [RelayCommand]
    private void ExportItemToUsb(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        SelectedLibraryItem = item;
        ExportSelectedToUsb();
    }

    [RelayCommand]
    private void ExportItemToConsole(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        SelectedLibraryItem = item;
        ExportSelectedToConsole();
    }

    [RelayCommand]
    private void ExportSelectedToUsb()
    {
        if (SelectedLibraryItem is null)
        {
            return;
        }

        var s = _settings.Current;
        var usb = _usb.FindApprovedConnected(s.ApprovedUsbDevices);
        if (usb is null)
        {
            var connected = _usb.GetRemovableDrives();
            if (connected.Count == 0)
            {
                UserNoticeRequired?.Invoke(this, (
                    "USB",
                    "Внешний накопитель не найден.\n\nПодключите флешку или внешний HDD/SSD по USB.\nВнутренние диски (C:, D:, E:) для экспорта не используются.\nЕсли диск уже подключён — проверьте, что Windows назначила ему букву."));
                _log.Warning("USB export: no removable/USB drives detected");
                return;
            }

            // Offer the first unapproved connected drive (same dialog as hot-plug).
            var pending = connected.FirstOrDefault(d => !_usb.IsApproved(d, s.ApprovedUsbDevices)) ?? connected[0];
            _log.Info($"USB export: requesting confirmation for {pending.DriveLetter}");
            UsbConfirmationRequired?.Invoke(this, pending);
            return;
        }

        if (usb.FreeBytes < SelectedLibraryItem.Model.FileSizeBytes)
        {
            UserNoticeRequired?.Invoke(this, ("Недостаточно места",
                $"Файл: {SelectedLibraryItem.SizeText}\nСвободно: {usb.FreeBytes / (1024.0 * 1024 * 1024):0.##} GB"));
            return;
        }

        UsbExportOfferRequired?.Invoke(this, (SelectedLibraryItem.Model, usb));
    }

    public void StartUsbExport(LibraryItem item, UsbDriveInfo drive)
    {
        _transferQueue.EnqueueUsb(item, drive);
        StatusMessage = "Копирование на USB в очереди";
        _log.Info($"USB export started by user: {item.Title} → {drive.DriveLetter}");
        var row = LibraryItems.FirstOrDefault(i => i.Model.Id == item.Id);
        if (row is not null)
        {
            row.IsTransferring = true;
            row.StatusText = "В очереди…";
            row.TransferPercent = 0;
            row.TransferPercentText = "0%";
            row.TransferDetailText = "Ожидание…";
        }
    }

    [RelayCommand]
    private void ExportSelectedToConsole()
    {
        if (SelectedLibraryItem is null)
        {
            return;
        }

        if (SelectedConsole is not { IsCompatible: true })
        {
            UserNoticeRequired?.Invoke(this, ("Консоль", "Совместимая консоль не выбрана."));
            return;
        }

        ConsoleTransferOfferRequired?.Invoke(this, (SelectedLibraryItem.Model, SelectedConsole));
    }

    public void StartConsoleTransfer(LibraryItem item, DiscoveredNetworkDevice device)
    {
        _transferQueue.EnqueueConsole(item, device);
        StatusMessage = "Отправка на консоль в очереди";
        _log.Info($"Console export started by user: {item.Title} → {device.Endpoint}");
        _log.Info("Remote directory: " + (string.IsNullOrWhiteSpace(ConsoleRemoteDirectory)
            ? "/data/etaHEN/homebrew"
            : ConsoleRemoteDirectory.Trim()));
        var row = LibraryItems.FirstOrDefault(i => i.Model.Id == item.Id);
        if (row is not null)
        {
            row.IsTransferring = true;
            row.StatusText = "В очереди…";
            row.TransferPercent = 0;
            row.TransferPercentText = "0%";
            row.TransferDetailText = "Ожидание…";
        }
    }

    public void SetConsoleRemoteDirectory(string path)
    {
        path = path.Replace('\\', '/').Trim();
        while (path.StartsWith("//", StringComparison.Ordinal))
        {
            path = path[1..];
        }

        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        path = path.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(path))
        {
            path = "/data/etaHEN/homebrew";
        }

        ConsoleRemoteDirectory = path;
        var s = _settings.Current;
        s.ConsoleRemoteDirectory = path;
        _settings.Save(s);
        _log.Info("Console remote directory saved: " + path);
    }

    [RelayCommand]
    private void OpenLibraryFolder(LibraryItemViewModel? item)
    {
        item ??= SelectedLibraryItem;
        if (item is null || !File.Exists(item.FilePath))
        {
            UserNoticeRequired?.Invoke(this, ("Библиотека", "Файл образа не найден. Запись будет удалена из списка."));
            if (item is not null)
            {
                _ = RemoveLibraryItemCoreAsync(item);
            }

            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{item.FilePath}\"",
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private async Task RemoveLibraryItemAsync(LibraryItemViewModel? item)
    {
        item ??= SelectedLibraryItem;
        if (item is null)
        {
            return;
        }

        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = ConfirmRequired;
        if (handler is null)
        {
            await RemoveLibraryItemCoreAsync(item).ConfigureAwait(true);
            return;
        }

        handler.Invoke(this, (
            "Удалить из библиотеки",
            $"Удалить «{item.Title}» из списка?\n\nФайл образа на диске не удаляется.",
            "Удалить",
            answer));
        if (!await answer.Task.ConfigureAwait(true))
        {
            return;
        }

        await RemoveLibraryItemCoreAsync(item).ConfigureAwait(true);
    }

    private async Task RemoveLibraryItemCoreAsync(LibraryItemViewModel item)
    {
        await _library.RemoveAsync(item.Model.Id).ConfigureAwait(true);
        _log.Info("Removed from library: " + item.Title);
        ReloadLibraryUi();
    }

    [RelayCommand]
    private void CancelTransfer() => _transferQueue.CancelActive();

    [RelayCommand]
    private async Task ResolveIncompleteAsync(string action)
    {
        var incomplete = _library.GetIncompleteTransfers().FirstOrDefault();
        if (incomplete?.IncompleteDestinationPath is null)
        {
            return;
        }

        if (action == "delete")
        {
            try
            {
                if (File.Exists(incomplete.IncompleteDestinationPath))
                {
                    File.Delete(incomplete.IncompleteDestinationPath);
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex.Message);
            }

            incomplete.IncompleteDestinationPath = null;
            incomplete.IncompleteKind = null;
            await _library.UpdateAsync(incomplete);
            ReloadLibraryUi();
            return;
        }

        if (action == "restart" || action == "continue")
        {
            var kind = incomplete.IncompleteKind;
            try
            {
                if (File.Exists(incomplete.IncompleteDestinationPath))
                {
                    File.Delete(incomplete.IncompleteDestinationPath);
                }
            }
            catch
            {
                // ignore
            }

            incomplete.IncompleteDestinationPath = null;
            incomplete.IncompleteKind = null;
            await _library.UpdateAsync(incomplete);

            if (kind == TransferDestinationKind.Usb || action == "restart")
            {
                var usb = _usb.FindApprovedConnected(_settings.Current.ApprovedUsbDevices);
                if (usb is not null)
                {
                    StartUsbExport(incomplete, usb);
                }
            }
        }
    }

    private void ApplyTransferProgress(TransferProgress p)
    {
        IsTransferring = true;
        if (p.Percent is { } pct)
        {
            TransferPercent = pct;
            TransferPercentText = string.Create(CultureInfo.InvariantCulture, $"{pct:0}%");
        }

        var speed = p.BytesPerSecond > 0 ? FormatBytes((long)p.BytesPerSecond) + "/s" : "—";
        var eta = p.EstimatedRemaining is { } e ? e.ToString(@"mm\:ss") : "—";
        TransferDetailText = $"{FormatBytes(p.BytesTransferred)} / {FormatBytes(p.TotalBytes)} · {speed} · ETA {eta}";
        TransferStatusText = _transferQueue.ActiveJob is { } job
            ? $"{job.Title} → {(job.Kind == TransferDestinationKind.Usb ? "USB" : "Консоль")}"
            : "Передача…";

        var activeId = _transferQueue.ActiveJob?.LibraryItemId;
        if (activeId is not null)
        {
            var row = LibraryItems.FirstOrDefault(i => i.Model.Id == activeId);
            row?.ApplyProgress(p);
        }
    }

    private void OnTransferJobFinished(TransferJob job)
    {
        var row = LibraryItems.FirstOrDefault(i => i.Model.Id == job.LibraryItemId);
        var latest = _library.FindById(job.LibraryItemId);
        if (row is not null && latest is not null)
        {
            row.Model.UsbState = latest.UsbState;
            row.Model.ConsoleState = latest.ConsoleState;
            row.Model.LastUsbDestination = latest.LastUsbDestination;
            row.Model.LastConsoleEndpoint = latest.LastConsoleEndpoint;
            row.Model.IncompleteDestinationPath = latest.IncompleteDestinationPath;
            row.ClearProgress();
        }

        ReloadLibraryUi();

        if (job.State == LibraryTransferState.Completed)
        {
            StatusMessage = job.Kind == TransferDestinationKind.Usb ? "Копирование на USB завершено" : "Отправка на консоль завершена";
            UserNoticeRequired?.Invoke(this, (
                job.Kind == TransferDestinationKind.Usb ? "USB" : "Консоль",
                $"{job.Title}\n\nГотово.\n{job.DestinationPath ?? job.ConsoleEndpoint ?? ""}"));
        }
        else if (job.State == LibraryTransferState.Failed)
        {
            StatusMessage = "Ошибка передачи";
            UserNoticeRequired?.Invoke(this, (
                "Ошибка передачи",
                $"{job.Title}\n\n{job.Error ?? "Неизвестная ошибка"}"));
        }
    }

    private void UpdateTransferUi()
    {
        var active = _transferQueue.ActiveJob;
        IsTransferring = active is { State: LibraryTransferState.InProgress };
        if (!IsTransferring && active is null)
        {
            TransferStatusText = _transferQueue.Jobs.Count > 0 ? $"В очереди: {_transferQueue.Jobs.Count}" : string.Empty;
        }

        ReloadLibraryUi();
        _ = PruneMissingQuietAsync();
    }

    private async Task PruneMissingQuietAsync()
    {
        try
        {
            var removed = await _library.PruneMissingAsync().ConfigureAwait(true);
            if (removed > 0)
            {
                _log.Info($"Auto-removed {removed} missing library item(s)");
                ReloadLibraryUi();
            }
        }
        catch (Exception ex)
        {
            _log.Warning("Library prune failed: " + ex.Message);
        }
    }

    private void PersistExportSettings(AppSettings s)
    {
        s.DetectUsbAutomatically = DetectUsbAutomatically;
        s.AutoCopyToUsb = AutoCopyToUsb;
        s.ConfirmBeforeUsbCopy = ConfirmBeforeUsbCopy;
        s.DetectConsoleAutomatically = DetectConsoleAutomatically;
        s.AutoSendToConsole = AutoSendToConsole;
        s.ConfirmBeforeConsoleTransfer = ConfirmBeforeConsoleTransfer;
        s.ConsoleRemoteDirectory = string.IsNullOrWhiteSpace(ConsoleRemoteDirectory)
            ? "/data/etaHEN/homebrew"
            : ConsoleRemoteDirectory.Trim();
    }
}
