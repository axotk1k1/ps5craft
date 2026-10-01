using PS5Craft.Core;
using PS5Craft.Core.Abstractions;
using PS5Craft.Core.Models;

namespace PS5Craft.Services.Transfer;

public sealed class TransferQueueService : ITransferQueueService
{
    private readonly IUsbExportService _usbExport;
    private readonly IPs5TransferService _consoleTransfer;
    private readonly ILibraryService _library;
    private readonly ISettingsService _settings;
    private readonly ILogService _log;
    private readonly object _gate = new();
    private readonly Queue<TransferJob> _queue = new();
    private TransferJob? _active;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private bool _running;

    public TransferQueueService(
        IUsbExportService usbExport,
        IPs5TransferService consoleTransfer,
        ILibraryService library,
        ISettingsService settings,
        ILogService log)
    {
        _usbExport = usbExport;
        _consoleTransfer = consoleTransfer;
        _library = library;
        _settings = settings;
        _log = log;
    }

    public event EventHandler? Changed;
    public event EventHandler<TransferProgress>? ProgressChanged;
    public event EventHandler<TransferJob>? JobFinished;

    public IReadOnlyList<TransferJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                var list = _queue.ToList();
                if (_active is not null)
                {
                    list.Insert(0, _active);
                }

                return list;
            }
        }
    }

    public TransferJob? ActiveJob
    {
        get { lock (_gate) return _active; }
    }

    public void EnqueueUsb(LibraryItem item, UsbDriveInfo drive)
    {
        lock (_gate)
        {
            if (_active is { Kind: TransferDestinationKind.Usb, LibraryItemId: var id } && id == item.Id
                || _queue.Any(j => j.Kind == TransferDestinationKind.Usb && j.LibraryItemId == item.Id))
            {
                _log.Info("USB transfer already queued for: " + item.Title);
                return;
            }
        }

        var job = new TransferJob
        {
            LibraryItemId = item.Id,
            Title = item.Title,
            SourcePath = item.FilePath,
            Kind = TransferDestinationKind.Usb,
            UsbRoot = drive.RootPath,
            TotalBytes = item.FileSizeBytes
        };
        lock (_gate)
        {
            _queue.Enqueue(job);
        }

        _log.Info($"USB copy queued: {item.Title} → {drive.RootPath}");
        Changed?.Invoke(this, EventArgs.Empty);
        EnsureWorker();
    }

    public void EnqueueConsole(LibraryItem item, DiscoveredNetworkDevice device)
    {
        if (!device.IsCompatible)
        {
            _log.Warning("Console transfer refused — unknown device.");
            return;
        }

        lock (_gate)
        {
            if (_active is { Kind: TransferDestinationKind.Console, LibraryItemId: var id } && id == item.Id
                || _queue.Any(j => j.Kind == TransferDestinationKind.Console && j.LibraryItemId == item.Id))
            {
                _log.Info("Console transfer already queued for: " + item.Title);
                return;
            }
        }

        var job = new TransferJob
        {
            LibraryItemId = item.Id,
            Title = item.Title,
            SourcePath = item.FilePath,
            Kind = TransferDestinationKind.Console,
            ConsoleEndpoint = device.Endpoint,
            ConsoleRemoteDirectory = NormalizeRemoteDirectory(_settings.Current.ConsoleRemoteDirectory),
            TotalBytes = item.FileSizeBytes
        };
        lock (_gate)
        {
            _queue.Enqueue(job);
        }

        _log.Info($"Console transfer queued: {item.Title} → {device.Endpoint}");
        Changed?.Invoke(this, EventArgs.Empty);
        EnsureWorker();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        EnsureWorker();
        return Task.CompletedTask;
    }

    public void CancelActive() => _cts?.Cancel();

    private void EnsureWorker()
    {
        lock (_gate)
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _worker = Task.Run(ProcessLoopAsync);
        }
    }

    private async Task ProcessLoopAsync()
    {
        try
        {
            while (true)
            {
                TransferJob? job;
                lock (_gate)
                {
                    if (!_queue.TryDequeue(out job))
                    {
                        _active = null;
                        _running = false;
                        Changed?.Invoke(this, EventArgs.Empty);
                        return;
                    }

                    _active = job;
                    job.State = LibraryTransferState.InProgress;
                }

                Changed?.Invoke(this, EventArgs.Empty);
                _cts = new CancellationTokenSource();
                try
                {
                    await RunJobAsync(job, _cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    job.State = LibraryTransferState.Failed;
                    job.Error = ex.Message;
                    _log.Error("Transfer failed: " + ex.Message);
                }
                finally
                {
                    lock (_gate)
                    {
                        _active = null;
                    }

                    try
                    {
                        JobFinished?.Invoke(this, job);
                    }
                    catch
                    {
                        // UI handlers must not crash the worker
                    }

                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
            }
        }
    }

    private async Task RunJobAsync(TransferJob job, CancellationToken cancellationToken)
    {
        var item = _library.FindById(job.LibraryItemId);
        if (item is null)
        {
            job.State = LibraryTransferState.Failed;
            job.Error = "Элемент библиотеки не найден.";
            return;
        }

        var progress = new Progress<TransferProgress>(p => ProgressChanged?.Invoke(this, p));
        FileCopyResult result;
        if (job.Kind == TransferDestinationKind.Usb)
        {
            var drive = ResolveConnectedDrive(job.UsbRoot);
            if (drive is null)
            {
                job.State = LibraryTransferState.Failed;
                job.Error = "USB-накопитель отключён или буква диска недоступна: " + (job.UsbRoot ?? "—");
                item.UsbState = LibraryTransferState.Failed;
                _log.Error(job.Error);
                await _library.UpdateAsync(item, cancellationToken).ConfigureAwait(false);
                return;
            }

            item.UsbState = LibraryTransferState.InProgress;
            item.IncompleteDestinationPath = _usbExport.BuildDestinationPath(drive, item) + ".partial";
            item.IncompleteKind = TransferDestinationKind.Usb;
            await _library.UpdateAsync(item, cancellationToken).ConfigureAwait(false);

            result = await _usbExport.CopyToUsbAsync(item, drive, overwrite: false, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Success && result.Error?.StartsWith("EXISTS:", StringComparison.Ordinal) == true)
            {
                // User already confirmed export; overwrite an existing destination copy.
                result = await _usbExport.CopyToUsbAsync(item, drive, overwrite: true, progress, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (result.Success)
            {
                item.UsbState = LibraryTransferState.Completed;
                item.LastUsbDestination = drive.RootPath;
                item.IncompleteDestinationPath = null;
                item.IncompleteKind = null;
                job.State = LibraryTransferState.Completed;
                job.DestinationPath = result.DestinationPath;
                _log.Success("USB copy finished: " + result.DestinationPath);
            }
            else if (result.Cancelled)
            {
                job.State = LibraryTransferState.Failed;
                job.Error = "Отменено";
                item.UsbState = LibraryTransferState.Failed;
                _log.Warning("USB copy cancelled");
            }
            else
            {
                job.State = LibraryTransferState.Failed;
                job.Error = result.Error;
                item.UsbState = LibraryTransferState.Failed;
                _log.Error("USB copy failed: " + (result.Error ?? "unknown"));
            }

            await _library.UpdateAsync(item, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Console
        var parts = (job.ConsoleEndpoint ?? "").Split(':');
        var host = parts[0];
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 2121;
        item.ConsoleState = LibraryTransferState.InProgress;
        item.IncompleteKind = TransferDestinationKind.Console;
        await _library.UpdateAsync(item, cancellationToken).ConfigureAwait(false);

        result = await _consoleTransfer.SendFileAsync(
                host,
                port,
                item.FilePath,
                item.FileName,
                job.ConsoleRemoteDirectory,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Success)
        {
            item.ConsoleState = LibraryTransferState.Completed;
            item.LastConsoleEndpoint = job.ConsoleEndpoint;
            item.IncompleteDestinationPath = null;
            item.IncompleteKind = null;
            job.State = LibraryTransferState.Completed;
            job.DestinationPath = result.DestinationPath;
        }
        else
        {
            item.ConsoleState = LibraryTransferState.Failed;
            job.State = LibraryTransferState.Failed;
            job.Error = result.Error;
        }

        await _library.UpdateAsync(item, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the destination volume by drive letter. External HDD/SSD often report as Fixed, not Removable.
    /// </summary>
    private static UsbDriveInfo? ResolveConnectedDrive(string? usbRoot)
    {
        if (string.IsNullOrWhiteSpace(usbRoot))
        {
            return null;
        }

        var letter = usbRoot.TrimEnd('\\');
        try
        {
            var drive = DriveInfo.GetDrives().FirstOrDefault(d =>
                d.IsReady && d.Name.StartsWith(letter, StringComparison.OrdinalIgnoreCase));
            if (drive is null)
            {
                return null;
            }

            return new UsbDriveInfo
            {
                DriveLetter = drive.Name.TrimEnd('\\'),
                FreeBytes = drive.AvailableFreeSpace,
                TotalBytes = drive.TotalSize,
                VolumeLabel = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel,
                FileSystem = drive.DriveFormat,
                IsRemovable = drive.DriveType == DriveType.Removable
            };
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeRemoteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/data/etaHEN/homebrew";
        }

        path = path.Replace('\\', '/').Trim();
        while (path.StartsWith("//", StringComparison.Ordinal))
        {
            path = path[1..];
        }

        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        return path.TrimEnd('/');
    }
}
