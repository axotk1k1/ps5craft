using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using PS5Craft.Core;
using PS5Craft.Core.Abstractions;
using PS5Craft.Core.Models;

namespace PS5Craft.Services.Network;

/// <summary>
/// Production transfer client for PS5-compatible services on TCP 2121.
/// Supports PS5Craft custom protocol and FTP (common homebrew FTP on 2121).
/// </summary>
public sealed partial class Ps5TransferService : IPs5TransferService
{
    private readonly ILogService _log;
    private readonly IPs5NetworkDiscoveryService _discovery;

    public Ps5TransferService(ILogService log, IPs5NetworkDiscoveryService discovery)
    {
        _log = log;
        _discovery = discovery;
    }

    public async Task<bool> IsAvailableAsync(string host, int port = 2121, CancellationToken cancellationToken = default)
    {
        var device = await _discovery.ProbeHostAsync(host, port, 1000, cancellationToken).ConfigureAwait(false);
        return device?.IsCompatible == true;
    }

    public async Task<FileCopyResult> SendFileAsync(
        string host,
        int port,
        string localPath,
        string remoteFileName,
        string? remoteDirectory = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(localPath))
        {
            return FileCopyResult.Fail("Исходный файл не найден.");
        }

        var device = await _discovery.ProbeHostAsync(host, port, 1500, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return FileCopyResult.Fail("Устройство недоступно.");
        }

        if (!device.IsCompatible)
        {
            return FileCopyResult.Fail("Неизвестное устройство — передача запрещена.");
        }

        var remoteDir = NormalizeRemoteDirectory(remoteDirectory);
        _log.Info($"Connected device: {host}:{port} ({device.Kind})");
        _log.Info($"Remote directory: {remoteDir}");
        try
        {
            return device.Kind switch
            {
                DiscoveredDeviceKind.Ps5Craft => await SendPs5CraftAsync(host, port, localPath, remoteFileName, remoteDir, progress, cancellationToken).ConfigureAwait(false),
                DiscoveredDeviceKind.CompatibleFtp => await SendFtpAsync(host, port, localPath, remoteFileName, remoteDir, progress, cancellationToken).ConfigureAwait(false),
                _ => FileCopyResult.Fail("Неизвестный протокол.")
            };
        }
        catch (OperationCanceledException)
        {
            return FileCopyResult.Cancel();
        }
        catch (Exception ex)
        {
            return FileCopyResult.Fail(ex.Message);
        }
    }

    private static async Task<FileCopyResult> SendPs5CraftAsync(
        string host,
        int port,
        string localPath,
        string remoteFileName,
        string remoteDirectory,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var size = new FileInfo(localPath).Length;
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();
        _ = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false); // READY banner

        var remoteName = CombineRemotePath(remoteDirectory, Sanitize(remoteFileName));
        var header = $"PUT {remoteName} {size}\r\n";
        await WriteAsciiAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var ready = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
        if (ready is null || !ready.StartsWith("READY", StringComparison.OrdinalIgnoreCase))
        {
            return FileCopyResult.Fail("Протокол PS5Craft: нет READY.");
        }

        await StreamFileAsync(localPath, stream, size, progress, cancellationToken).ConfigureAwait(false);
        var ok = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
        if (ok is null || !ok.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
        {
            return FileCopyResult.Fail("Протокол PS5Craft: нет подтверждения OK.");
        }

        return FileCopyResult.Ok($"{host}:{port}{remoteName}");
    }

    private async Task<FileCopyResult> SendFtpAsync(
        string host,
        int port,
        string localPath,
        string remoteFileName,
        string remoteDirectory,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var size = new FileInfo(localPath).Length;
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        await using var control = client.GetStream();
        var banner = await ReadFtpResponseAsync(control, cancellationToken).ConfigureAwait(false);
        if (!IsFtpCode(banner, 220))
        {
            return FileCopyResult.Fail("FTP: нет приветствия 220. " + Truncate(banner));
        }

        var login = await TryFtpLoginAsync(control, cancellationToken).ConfigureAwait(false);
        if (login is not null)
        {
            return FileCopyResult.Fail(login);
        }

        var type = await FtpCommandAsync(control, "TYPE I", cancellationToken).ConfigureAwait(false);
        if (!IsFtpCode(type, 200))
        {
            return FileCopyResult.Fail("FTP TYPE I failed: " + Truncate(type));
        }

        var cwdError = await EnsureFtpDirectoryAsync(control, remoteDirectory, cancellationToken).ConfigureAwait(false);
        if (cwdError is not null)
        {
            return FileCopyResult.Fail(cwdError);
        }

        var pwd = await FtpCommandAsync(control, "PWD", cancellationToken).ConfigureAwait(false);
        _log.Info("FTP PWD: " + Truncate(pwd));

        var pasv = await FtpCommandAsync(control, "PASV", cancellationToken).ConfigureAwait(false);
        var parsed = ParsePasv(pasv);
        if (parsed is null)
        {
            return FileCopyResult.Fail("FTP PASV failed: " + Truncate(pasv));
        }

        // Homebrew FTP often advertises 127.0.0.0/8 or a wrong LAN IP. Always open the data
        // channel to the same host we already verified on the control connection.
        var dataPort = parsed.Value.Port;
        var remoteName = Sanitize(remoteFileName);

        await WriteAsciiAsync(control, $"STOR {remoteName}\r\n", cancellationToken).ConfigureAwait(false);

        using var dataClient = new TcpClient();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(15));
            await dataClient.ConnectAsync(host, dataPort, connectCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return FileCopyResult.Fail(
                $"FTP: не удалось открыть канал данных {host}:{dataPort}. {ex.Message}");
        }

        await using var data = dataClient.GetStream();
        var storReply = await ReadFtpResponseAsync(control, cancellationToken).ConfigureAwait(false);
        if (!IsFtpCode(storReply, 150) && !IsFtpCode(storReply, 125))
        {
            return FileCopyResult.Fail(
                "FTP STOR отклонён: " + Truncate(storReply) +
                $"\nКаталог: {remoteDirectory}\nНужен доступ на запись (обычно /data/etaHEN/homebrew).");
        }

        _log.Info($"FTP transfer started: {remoteDirectory}/{remoteName} ({size} bytes) → {host}:{port}");
        await StreamFileAsync(localPath, data, size, progress, cancellationToken).ConfigureAwait(false);
        dataClient.Close();
        var done = await ReadFtpResponseAsync(control, cancellationToken).ConfigureAwait(false);
        if (!IsFtpCode(done, 226) && !IsFtpCode(done, 250))
        {
            return FileCopyResult.Fail("FTP: передача не подтверждена. " + Truncate(done));
        }

        await WriteAsciiAsync(control, "QUIT\r\n", cancellationToken).ConfigureAwait(false);
        return FileCopyResult.Ok($"ftp://{host}:{port}{remoteDirectory}/{remoteName}");
    }

    /// <summary>Creates missing folders if needed, then CWD into the target directory.</summary>
    private async Task<string?> EnsureFtpDirectoryAsync(
        NetworkStream control,
        string remoteDirectory,
        CancellationToken cancellationToken)
    {
        remoteDirectory = NormalizeRemoteDirectory(remoteDirectory);
        var absolute = await FtpCommandAsync(control, "CWD " + remoteDirectory, cancellationToken).ConfigureAwait(false);
        if (IsFtpCode(absolute, 250))
        {
            return null;
        }

        var root = await FtpCommandAsync(control, "CWD /", cancellationToken).ConfigureAwait(false);
        if (!IsFtpCode(root, 250))
        {
            return "FTP: не удалось перейти в корень (/). " + Truncate(root);
        }

        foreach (var segment in remoteDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
            {
                continue;
            }

            var cwd = await FtpCommandAsync(control, "CWD " + segment, cancellationToken).ConfigureAwait(false);
            if (IsFtpCode(cwd, 250))
            {
                continue;
            }

            var mkd = await FtpCommandAsync(control, "MKD " + segment, cancellationToken).ConfigureAwait(false);
            if (!IsFtpCode(mkd, 257) && !IsFtpCode(mkd, 550) && !mkd.Contains("exists", StringComparison.OrdinalIgnoreCase))
            {
                return $"FTP: нельзя создать каталог «{segment}» ({remoteDirectory}). " + Truncate(mkd);
            }

            cwd = await FtpCommandAsync(control, "CWD " + segment, cancellationToken).ConfigureAwait(false);
            if (!IsFtpCode(cwd, 250))
            {
                return $"FTP: нельзя открыть каталог «{segment}» ({remoteDirectory}). " + Truncate(cwd) +
                       "\nЧастая причина: раздел только для чтения — нужен /data/etaHEN/homebrew.";
            }
        }

        return null;
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

    private static string CombineRemotePath(string directory, string fileName)
    {
        directory = NormalizeRemoteDirectory(directory);
        fileName = fileName.TrimStart('/');
        return directory + "/" + fileName;
    }

    private static async Task<string?> TryFtpLoginAsync(NetworkStream control, CancellationToken cancellationToken)
    {
        (string User, string Pass)[] attempts =
        [
            ("anonymous", "anonymous"),
            ("anonymous", ""),
            ("ftp", "ftp"),
            ("ps5", "ps5")
        ];

        foreach (var (user, pass) in attempts)
        {
            var userReply = await FtpCommandAsync(control, "USER " + user, cancellationToken).ConfigureAwait(false);
            if (IsFtpCode(userReply, 230))
            {
                return null;
            }

            if (!IsFtpCode(userReply, 331) && !IsFtpCode(userReply, 230))
            {
                continue;
            }

            var passReply = await FtpCommandAsync(control, "PASS " + pass, cancellationToken).ConfigureAwait(false);
            if (IsFtpCode(passReply, 230) || IsFtpCode(passReply, 202))
            {
                return null;
            }
        }

        return "FTP: вход не принят (anonymous/ftp). Проверьте сервис на PS5.";
    }

    private static async Task<string> FtpCommandAsync(NetworkStream control, string command, CancellationToken cancellationToken)
    {
        await WriteAsciiAsync(control, command + "\r\n", cancellationToken).ConfigureAwait(false);
        return await ReadFtpResponseAsync(control, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsFtpCode(string? response, int code)
        => response is not null && response.StartsWith(code.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static string Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "—";
        }

        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 160 ? text : text[..160] + "…";
    }

    private static async Task StreamFileAsync(
        string localPath,
        Stream destination,
        long total,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        var buffer = new byte[bufferSize];
        await using var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        long sent = 0;
        var sw = Stopwatch.StartNew();
        long lastBytes = 0;
        var lastMark = sw.Elapsed;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            sent += read;
            var elapsed = sw.Elapsed - lastMark;
            double speed = sw.Elapsed.TotalSeconds > 0 ? sent / sw.Elapsed.TotalSeconds : 0;
            if (elapsed.TotalSeconds >= 0.25)
            {
                speed = (sent - lastBytes) / Math.Max(elapsed.TotalSeconds, 0.001);
                lastBytes = sent;
                lastMark = sw.Elapsed;
            }

            TimeSpan? eta = speed > 1 && total > sent ? TimeSpan.FromSeconds((total - sent) / speed) : null;
            progress?.Report(new TransferProgress
            {
                BytesTransferred = sent,
                TotalBytes = total,
                BytesPerSecond = speed,
                EstimatedRemaining = eta,
                Phase = "network"
            });
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static (string Host, int Port)? ParsePasv(string response)
    {
        var m = PasvRegex().Match(response);
        if (!m.Success)
        {
            return null;
        }

        var n = m.Groups.Values.Skip(1).Select(g => int.Parse(g.Value, CultureInfo.InvariantCulture)).ToArray();
        var host = $"{n[0]}.{n[1]}.{n[2]}.{n[3]}";
        var port = n[4] * 256 + n[5];
        return (host, port);
    }

    private static async Task WriteAsciiAsync(Stream stream, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var ms = new MemoryStream();
        var buf = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buf.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buf[0] == (byte)'\n')
            {
                break;
            }

            if (buf[0] != (byte)'\r')
            {
                ms.WriteByte(buf[0]);
            }
        }

        return ms.Length == 0 ? null : Encoding.ASCII.GetString(ms.ToArray());
    }

    private static async Task<string> ReadFtpResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var line = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            sb.AppendLine(line);
            if (line.Length >= 4 && char.IsDigit(line[0]) && line[3] == ' ')
            {
                break;
            }
        }

        return sb.ToString().Trim();
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name;
    }

    [GeneratedRegex(@"\((\d+),(\d+),(\d+),(\d+),(\d+),(\d+)\)")]
    private static partial Regex PasvRegex();
}
