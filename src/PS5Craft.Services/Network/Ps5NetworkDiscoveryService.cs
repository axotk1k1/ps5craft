using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using PS5Craft.Core;
using PS5Craft.Core.Abstractions;
using PS5Craft.Core.Models;

namespace PS5Craft.Services.Network;

public sealed class Ps5NetworkDiscoveryService : IPs5NetworkDiscoveryService
{
    private readonly ILogService _log;

    public Ps5NetworkDiscoveryService(ILogService log) => _log = log;

    public async Task<IReadOnlyList<DiscoveredNetworkDevice>> DiscoverAsync(
        int port = 2121,
        int maxConcurrency = 48,
        int timeoutMs = 700,
        CancellationToken cancellationToken = default)
    {
        var subnets = GetLocalSubnets().ToList();
        if (subnets.Count == 0)
        {
            _log.Warning("Network discovery: no private IPv4 interfaces found.");
            return [];
        }

        _log.Info("Network discovery started");
        var targets = new List<IPAddress>();
        foreach (var (network, prefix) in subnets)
        {
            _log.Info($"Scanning local subnet: {network}/{prefix}");
            targets.AddRange(EnumerateHosts(network, prefix));
        }

        targets = targets.Distinct().ToList();
        var found = new ConcurrentBag<DiscoveredNetworkDevice>();
        using var gate = new SemaphoreSlim(Math.Clamp(maxConcurrency, 8, 128));
        var tasks = targets.Select(async ip =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var device = await ProbeHostAsync(ip.ToString(), port, timeoutMs, cancellationToken).ConfigureAwait(false);
                if (device is not null)
                {
                    found.Add(device);
                    _log.Info($"Port {port} detected: {device.Host}");
                    if (device.IsCompatible)
                    {
                        _log.Info("Service verification successful");
                        _log.Info($"PS5-compatible device discovered: {device.Endpoint}");
                    }
                    else
                    {
                        _log.Warning($"Unknown device on {device.Endpoint}");
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return found.OrderBy(d => d.Host, StringComparer.Ordinal).ToList();
    }

    public async Task<DiscoveredNetworkDevice?> ProbeHostAsync(
        string host,
        int port = 2121,
        int timeoutMs = 700,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
            client.ReceiveTimeout = timeoutMs;
            client.SendTimeout = timeoutMs;
            await using var stream = client.GetStream();
            stream.ReadTimeout = timeoutMs;

            var banner = await ReadBannerAsync(stream, timeoutCts.Token).ConfigureAwait(false);
            var kind = ClassifyBanner(banner);
            return new DiscoveredNetworkDevice
            {
                Host = host,
                Port = port,
                Kind = kind,
                Banner = banner,
                DisplayName = kind switch
                {
                    DiscoveredDeviceKind.Ps5Craft => "PS5Craft",
                    DiscoveredDeviceKind.CompatibleFtp => "PS5 (FTP)",
                    _ => "Неизвестное устройство"
                }
            };
        }
        catch
        {
            return null;
        }
    }

    public static DiscoveredDeviceKind ClassifyBanner(string banner)
    {
        if (string.IsNullOrWhiteSpace(banner))
        {
            return DiscoveredDeviceKind.Unknown;
        }

        if (banner.Contains("PS5CRAFT", StringComparison.OrdinalIgnoreCase))
        {
            return DiscoveredDeviceKind.Ps5Craft;
        }

        // FTP greeting commonly used by PS5 homebrew services on 2121
        if (banner.StartsWith("220", StringComparison.Ordinal))
        {
            return DiscoveredDeviceKind.CompatibleFtp;
        }

        return DiscoveredDeviceKind.Unknown;
    }

    public static IEnumerable<(IPAddress Network, int Prefix)> GetLocalSubnets()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                if (!IsPrivate(ua.Address))
                {
                    continue;
                }

                var prefix = ua.PrefixLength;
                if (prefix is < 16 or > 30)
                {
                    prefix = 24;
                }

                var network = ToNetwork(ua.Address, prefix);
                yield return (network, prefix);
            }
        }
    }

    public static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b[0] == 10)
        {
            return true;
        }

        if (b[0] == 192 && b[1] == 168)
        {
            return true;
        }

        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
        {
            return true;
        }

        return false;
    }

    public static IPAddress ToNetwork(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var ip = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var net = ip & mask;
        return new IPAddress(new[]
        {
            (byte)(net >> 24),
            (byte)(net >> 16),
            (byte)(net >> 8),
            (byte)net
        });
    }

    public static IEnumerable<IPAddress> EnumerateHosts(IPAddress network, int prefix)
    {
        var bytes = network.GetAddressBytes();
        var net = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var hostBits = 32 - prefix;
        if (hostBits > 12)
        {
            // Cap scan size for very large subnets (/16 etc.) to first /24 of the network block.
            hostBits = 8;
        }

        var count = 1u << hostBits;
        for (uint i = 1; i < count - 1; i++)
        {
            var ip = net + i;
            yield return new IPAddress(new[]
            {
                (byte)(ip >> 24),
                (byte)(ip >> 16),
                (byte)(ip >> 8),
                (byte)ip
            });
        }
    }

    private static async Task<string> ReadBannerAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[512];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(800));
        try
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token).ConfigureAwait(false);
            return Encoding.ASCII.GetString(buffer, 0, Math.Max(0, read)).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }
}
