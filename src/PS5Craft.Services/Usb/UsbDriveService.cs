using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PS5Craft.Core.Abstractions;
using PS5Craft.Core.Models;

namespace PS5Craft.Services.Usb;

public sealed class UsbDriveService : IUsbDriveService, IDisposable
{
    private readonly ILogService _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, UsbDriveInfo> _known = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _timer;
    private bool _watching;

    public UsbDriveService(ILogService log) => _log = log;

    public event EventHandler<UsbDriveInfo>? DriveArrived;
    public event EventHandler<UsbDriveInfo>? DriveRemoved;

    public void StartWatching()
    {
        if (_watching)
        {
            return;
        }

        _watching = true;
        // First poll: seed known drives AND notify about already-connected ones,
        // otherwise a disk plugged in before launch is never offered for approval.
        Refresh(fireEvents: true);
        _timer = new Timer(_ =>
        {
            try { Refresh(fireEvents: true); }
            catch { /* ignore poll errors */ }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public void StopWatching()
    {
        _watching = false;
        _timer?.Dispose();
        _timer = null;
    }

    public IReadOnlyList<UsbDriveInfo> GetRemovableDrives()
    {
        Refresh(fireEvents: false);
        lock (_gate)
        {
            return _known.Values.ToList();
        }
    }

    public UsbDriveInfo? FindApprovedConnected(IEnumerable<ApprovedUsbDevice> approved)
    {
        var list = approved.ToList();
        foreach (var drive in GetRemovableDrives())
        {
            if (IsApproved(drive, list))
            {
                return drive;
            }
        }

        return null;
    }

    public bool IsApproved(UsbDriveInfo drive, IEnumerable<ApprovedUsbDevice> approved)
        => approved.Any(a => string.Equals(a.DeviceKey, drive.DeviceKey, StringComparison.OrdinalIgnoreCase));

    public ApprovedUsbDevice Approve(UsbDriveInfo drive) => new()
    {
        DeviceKey = drive.DeviceKey,
        VolumeLabel = drive.VolumeLabel,
        VolumeSerial = drive.VolumeSerial,
        LastDriveLetter = drive.DriveLetter,
        ApprovedAt = DateTimeOffset.Now
    };

    private void Refresh(bool fireEvents)
    {
        var current = new Dictionary<string, UsbDriveInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || !IsExportCandidate(d))
                {
                    continue;
                }

                var letter = d.Name.TrimEnd('\\');
                var serial = TryGetVolumeSerial(letter);
                var info = new UsbDriveInfo
                {
                    DriveLetter = letter,
                    VolumeLabel = string.IsNullOrWhiteSpace(d.VolumeLabel) ? null : d.VolumeLabel,
                    FileSystem = d.DriveFormat,
                    TotalBytes = d.TotalSize,
                    FreeBytes = d.AvailableFreeSpace,
                    VolumeSerial = serial,
                    IsRemovable = d.DriveType == DriveType.Removable || IsUsbStorage(letter)
                };
                current[info.DeviceKey] = info;
            }
            catch
            {
                // skip inaccessible drives
            }
        }

        List<UsbDriveInfo> arrived = [];
        List<UsbDriveInfo> removed = [];
        lock (_gate)
        {
            foreach (var kv in current)
            {
                if (!_known.ContainsKey(kv.Key))
                {
                    arrived.Add(kv.Value);
                }
            }

            foreach (var kv in _known)
            {
                if (!current.ContainsKey(kv.Key))
                {
                    removed.Add(kv.Value);
                }
            }

            _known.Clear();
            foreach (var kv in current)
            {
                _known[kv.Key] = kv.Value;
            }
        }

        if (!fireEvents)
        {
            return;
        }

        foreach (var r in removed)
        {
            _log.Info($"USB device removed: {r.DriveLetter}");
            DriveRemoved?.Invoke(this, r);
        }

        foreach (var a in arrived)
        {
            _log.Info($"USB device detected: {a.DriveLetter}");
            if (!string.IsNullOrWhiteSpace(a.VolumeLabel))
            {
                _log.Info($"USB volume: {a.VolumeLabel}");
            }

            _log.Info($"USB free space: {a.FreeBytes / (1024.0 * 1024 * 1024):0.##} GB");
            DriveArrived?.Invoke(this, a);
        }
    }

    /// <summary>
    /// Removable sticks, plus Fixed volumes on a USB bus (external HDD/SSD).
    /// Never treats the Windows system drive as an export target.
    /// </summary>
    internal static bool IsExportCandidate(DriveInfo drive)
    {
        if (drive.DriveType == DriveType.Removable)
        {
            return true;
        }

        if (drive.DriveType != DriveType.Fixed)
        {
            return false;
        }

        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        if (!string.IsNullOrEmpty(systemRoot) &&
            string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsUsbStorage(drive.Name.TrimEnd('\\'));
    }

    private static string? TryGetVolumeSerial(string driveLetter)
    {
        try
        {
            var root = driveLetter.EndsWith(":\\", StringComparison.Ordinal) ? driveLetter : driveLetter + "\\";
            var volName = new StringBuilder(261);
            var fsName = new StringBuilder(261);
            if (GetVolumeInformation(root, volName, volName.Capacity, out var serial, out _, out _, fsName, fsName.Capacity))
            {
                return serial.ToString("X8");
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static bool IsUsbStorage(string driveLetter)
    {
        SafeFileHandle? handle = null;
        try
        {
            var path = @"\\.\" + driveLetter.TrimEnd('\\');
            handle = CreateFile(
                path,
                0,
                FileShare.ReadWrite,
                IntPtr.Zero,
                FileMode.Open,
                0,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return false;
            }

            var query = new STORAGE_PROPERTY_QUERY
            {
                PropertyId = StorageDeviceProperty,
                QueryType = PropertyStandardQuery
            };
            var headerSize = Marshal.SizeOf<STORAGE_DEVICE_DESCRIPTOR_HEADER>();
            var buffer = new byte[Math.Max(headerSize, 1024)];
            if (!DeviceIoControl(
                    handle,
                    IOCTL_STORAGE_QUERY_PROPERTY,
                    ref query,
                    Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(),
                    buffer,
                    buffer.Length,
                    out _,
                    IntPtr.Zero))
            {
                return false;
            }

            var busType = buffer[28]; // STORAGE_DEVICE_DESCRIPTOR.BusType offset
            // BusTypeUsb = 7, BusTypeUsbAttachedSCSI (UASP) = 16
            return busType is 7 or 16;
        }
        catch
        {
            return false;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint StorageDeviceProperty = 0;
    private const uint PropertyStandardQuery = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public uint PropertyId;
        public uint QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_DEVICE_DESCRIPTOR_HEADER
    {
        public uint Version;
        public uint Size;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(
        string rootPathName,
        StringBuilder volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer,
        int nFileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref STORAGE_PROPERTY_QUERY lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    public void Dispose() => StopWatching();
}
