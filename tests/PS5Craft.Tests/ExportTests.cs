using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using PS5Craft.Core;
using PS5Craft.Core.Abstractions;
using PS5Craft.Core.Models;
using PS5Craft.Infrastructure.Settings;
using PS5Craft.Services.Library;
using PS5Craft.Services.Network;
using PS5Craft.Services.Usb;
using Xunit;

namespace PS5Craft.Tests;

internal static class ExportTestUtil
{
    public static string NewTemp()
    {
        var d = Path.Combine(Path.GetTempPath(), "PS5Craft.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}

internal sealed class NullLog : ILogService
{
    public event EventHandler<LogEntry>? EntryAdded
    {
        add { }
        remove { }
    }
    public IReadOnlyList<LogEntry> Entries => Array.Empty<LogEntry>();
    public void Info(string message) { }
    public void Warning(string message) { }
    public void Error(string message) { }
    public void Success(string message) { }
    public void Clear() { }
    public Task SaveAsync(string? path = null) => Task.CompletedTask;
    public void OpenLogFolder() { }
}

public class LibraryServiceTests
{
    [Fact]
    public async Task Register_PersistsMetadata_WithoutDuplicatingFile()
    {
        var dir = ExportTestUtil.NewTemp();
        try
        {
            var image = Path.Combine(dir, "game.ffpfsc");
            await File.WriteAllBytesAsync(image, Encoding.UTF8.GetBytes("fake-image"));
            var settings = new SettingsService(Path.Combine(dir, "appdata"));
            var lib = new LibraryService(settings);
            await lib.LoadAsync();
            var item = await lib.RegisterPackedImageAsync(image, new GameInfo
            {
                Title = "Test Game",
                TitleId = "PPSA12345",
                Region = "EUR"
            }, OutputFormat.Ffpfsc, "abc");

            Assert.Equal("Test Game", item.Title);
            Assert.True(File.Exists(item.FilePath));

            var lib2 = new LibraryService(settings);
            await lib2.LoadAsync();
            Assert.Single(lib2.Items);
            Assert.Equal("PPSA12345", lib2.Items[0].TitleId);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task PruneMissing_RemovesEntriesWithoutFiles()
    {
        var dir = ExportTestUtil.NewTemp();
        try
        {
            var image = Path.Combine(dir, "keep.ffpfsc");
            await File.WriteAllTextAsync(image, "x");
            var settings = new SettingsService(Path.Combine(dir, "appdata"));
            var lib = new LibraryService(settings);
            await lib.LoadAsync();
            var keep = await lib.RegisterPackedImageAsync(image, new GameInfo { Title = "Keep" }, OutputFormat.Ffpfsc);
            keep.FilePath = Path.Combine(dir, "does-not-exist.ffpfsc");
            await lib.UpdateAsync(keep);

            var removed = await lib.PruneMissingAsync();
            Assert.Equal(1, removed);
            Assert.Empty(lib.Items);

            await lib.RegisterPackedImageAsync(image, new GameInfo { Title = "Keep" }, OutputFormat.Ffpfsc);
            Assert.Single(lib.Items);
            Assert.Equal(0, await lib.PruneMissingAsync());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class UsbExportTests
{
    [Fact]
    public void BuildDestinationPath_UsesDeterministicStructure()
    {
        var export = new UsbExportService(new NullLog());
        var drive = new UsbDriveInfo { DriveLetter = "E:", FreeBytes = 100, TotalBytes = 200 };
        var item = new LibraryItem { Title = "007 First Light", FileName = "game.ffpfsc" };
        Assert.Equal(@"E:\PS5Craft\Games\007 First Light\game.ffpfsc", export.BuildDestinationPath(drive, item));
    }

    [Fact]
    public async Task Copy_ReportsProgress_AndKeepsSource()
    {
        var dir = ExportTestUtil.NewTemp();
        try
        {
            var src = Path.Combine(dir, "src.bin");
            var data = RandomNumberGenerator.GetBytes(256 * 1024);
            await File.WriteAllBytesAsync(src, data);
            var dest = Path.Combine(dir, "out.bin");
            var reports = new List<TransferProgress>();
            await UsbExportService.CopyFileStreamingAsync(src, dest, data.Length, new Progress<TransferProgress>(reports.Add), () => true, CancellationToken.None);
            Assert.Equal(data, await File.ReadAllBytesAsync(dest));
            Assert.True(File.Exists(src));
            Assert.Equal(data.Length, reports[^1].BytesTransferred);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Copy_Cancel_LeavesSourceIntact()
    {
        var dir = ExportTestUtil.NewTemp();
        try
        {
            var src = Path.Combine(dir, "src.bin");
            await File.WriteAllBytesAsync(src, RandomNumberGenerator.GetBytes(32 * 1024 * 1024));
            var dest = Path.Combine(dir, "out.partial");
            using var cts = new CancellationTokenSource();
            var task = UsbExportService.CopyFileStreamingAsync(src, dest, 32 * 1024 * 1024, null, () => true, cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.True(File.Exists(src));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DeviceKey_SameAcrossDriveLetters()
    {
        var a = new UsbDriveInfo { DriveLetter = "E:", VolumeSerial = "AABBCCDD", TotalBytes = 1 };
        var b = new UsbDriveInfo { DriveLetter = "F:", VolumeSerial = "AABBCCDD", TotalBytes = 1 };
        Assert.Equal(a.DeviceKey, b.DeviceKey);
    }

    [Fact]
    public void UsbDriveService_Approve_StoresKey()
    {
        var usb = new UsbDriveService(new NullLog());
        var drive = new UsbDriveInfo { DriveLetter = "E:", VolumeSerial = "11223344", VolumeLabel = "PS5" };
        var approved = usb.Approve(drive);
        Assert.True(usb.IsApproved(drive, [approved]));
    }
}

public class NetworkDiscoveryTests
{
    [Theory]
    [InlineData("220 FTP ready", DiscoveredDeviceKind.CompatibleFtp)]
    [InlineData("PS5CRAFT/1 READY", DiscoveredDeviceKind.Ps5Craft)]
    [InlineData("Hello", DiscoveredDeviceKind.Unknown)]
    public void ClassifyBanner(string banner, DiscoveredDeviceKind expected)
        => Assert.Equal(expected, Ps5NetworkDiscoveryService.ClassifyBanner(banner));

    [Fact]
    public void IsPrivate_Ranges()
    {
        Assert.True(Ps5NetworkDiscoveryService.IsPrivate(IPAddress.Parse("192.168.1.10")));
        Assert.True(Ps5NetworkDiscoveryService.IsPrivate(IPAddress.Parse("10.0.0.5")));
        Assert.True(Ps5NetworkDiscoveryService.IsPrivate(IPAddress.Parse("172.16.0.1")));
        Assert.False(Ps5NetworkDiscoveryService.IsPrivate(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void EnumerateHosts_CapsLargeSubnets()
    {
        var hosts = Ps5NetworkDiscoveryService.EnumerateHosts(IPAddress.Parse("192.168.0.0"), 16).Count();
        Assert.Equal(254, hosts);
    }

    [Fact]
    public async Task ProbeHost_DetectsMockPs5CraftServer()
    {
        await using var server = await MockPs5CraftServer.StartAsync();
        var device = await new Ps5NetworkDiscoveryService(new NullLog()).ProbeHostAsync("127.0.0.1", server.Port, 1000);
        Assert.NotNull(device);
        Assert.Equal(DiscoveredDeviceKind.Ps5Craft, device!.Kind);
    }

    [Fact]
    public async Task ProbeHost_UnknownBanner_NotCompatible()
    {
        await using var server = await MockBannerServer.StartAsync("HELLO WORLD\r\n");
        var device = await new Ps5NetworkDiscoveryService(new NullLog()).ProbeHostAsync("127.0.0.1", server.Port, 1000);
        Assert.NotNull(device);
        Assert.False(device!.IsCompatible);
    }

    [Fact]
    public async Task Transfer_Ps5CraftProtocol_StreamsFile()
    {
        await using var server = await MockPs5CraftServer.StartAsync();
        var dir = ExportTestUtil.NewTemp();
        try
        {
            var file = Path.Combine(dir, "payload.bin");
            var payload = Encoding.UTF8.GetBytes("PS5Craft transfer payload " + new string('x', 4000));
            await File.WriteAllBytesAsync(file, payload);
            var transfer = new Ps5TransferService(new NullLog(), new Ps5NetworkDiscoveryService(new NullLog()));
            var reports = new List<TransferProgress>();
            var result = await transfer.SendFileAsync("127.0.0.1", server.Port, file, "payload.bin",
                progress: new Progress<TransferProgress>(reports.Add));
            Assert.True(result.Success);
            Assert.Equal(payload, server.Received);
            Assert.NotEmpty(reports);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Transfer_UnknownDevice_Refused()
    {
        await using var server = await MockBannerServer.StartAsync("NOPE\r\n");
        var dir = ExportTestUtil.NewTemp();
        try
        {
            var file = Path.Combine(dir, "a.bin");
            await File.WriteAllTextAsync(file, "x");
            var result = await new Ps5TransferService(new NullLog(), new Ps5NetworkDiscoveryService(new NullLog()))
                .SendFileAsync("127.0.0.1", server.Port, file, "a.bin");
            Assert.False(result.Success);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

internal sealed class MockPs5CraftServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    public int Port { get; }
    public byte[] Received { get; private set; } = [];

    private MockPs5CraftServer(TcpListener listener, int port)
    {
        _listener = listener;
        Port = port;
    }

    public static async Task<MockPs5CraftServer> StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = new MockPs5CraftServer(listener, ((IPEndPoint)listener.LocalEndpoint).Port);
        _ = Task.Run(server.AcceptLoop);
        await Task.Delay(30);
        return server;
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                await using var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes("PS5CRAFT/1 READY\r\n"));
                var header = await ReadLine(stream);
                if (header is null || !header.StartsWith("PUT ", StringComparison.Ordinal))
                {
                    continue;
                }

                var size = long.Parse(header.Split(' ')[^1]);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("READY\r\n"));
                var buffer = new byte[size];
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read));
                    if (n == 0) break;
                    read += n;
                }
                Received = buffer.AsSpan(0, read).ToArray();
                await stream.WriteAsync(Encoding.ASCII.GetBytes("OK\r\n"));
            }
        }
        catch { /* shutdown */ }
    }

    private static async Task<string?> ReadLine(NetworkStream stream)
    {
        var ms = new MemoryStream();
        var b = new byte[1];
        while (await stream.ReadAsync(b) > 0)
        {
            if (b[0] == (byte)'\n') break;
            if (b[0] != (byte)'\r') ms.WriteByte(b[0]);
        }
        return ms.Length == 0 ? null : Encoding.ASCII.GetString(ms.ToArray());
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class MockBannerServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _banner;
    public int Port { get; }

    private MockBannerServer(TcpListener listener, int port, string banner)
    {
        _listener = listener;
        Port = port;
        _banner = banner;
    }

    public static async Task<MockBannerServer> StartAsync(string banner)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = new MockBannerServer(listener, ((IPEndPoint)listener.LocalEndpoint).Port, banner);
        _ = Task.Run(server.AcceptLoop);
        await Task.Delay(30);
        return server;
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                await using var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes(_banner));
                await Task.Delay(100, _cts.Token);
            }
        }
        catch { /* shutdown */ }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
