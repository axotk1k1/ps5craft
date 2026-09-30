using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using PS5Craft.Core;
using PS5Craft.Core.Models;
using PS5Craft.Infrastructure.Settings;
using PS5Craft.Services.Updates;
using Xunit;

namespace PS5Craft.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v2.0.0-beta.1", "2.0.0")]
    public void Normalize_StripsPrefixAndPrerelease(string input, string expected)
        => Assert.Equal(expected, AppVersion.Normalize(input));

    [Theory]
    [InlineData("1.0.1", "1.0.0", 1)]
    [InlineData("1.1.0", "1.0.9", 1)]
    [InlineData("2.0.0", "1.9.9", 1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.0", "v1.0.1", -1)]
    public void Compare_Semantic(string a, string b, int expectedSign)
        => Assert.Equal(expectedSign, Math.Sign(AppVersion.Compare(a, b)));

    [Theory]
    [InlineData("v1.0.0-beta", true)]
    [InlineData("1.0.0-rc.1", true)]
    [InlineData("1.0.0-alpha", true)]
    [InlineData("v1.0.0", false)]
    [InlineData("1.2.3", false)]
    public void IsPrerelease(string tag, bool expected)
        => Assert.Equal(expected, AppVersion.IsPrerelease(tag));
}

public class GitHubReleaseParserTests
{
    private const string SampleRelease = """
        {
          "tag_name": "v1.1.0",
          "name": "PS5Craft 1.1.0",
          "body": "## Changes\n- Improved FPKG extraction\n- Fixed progress\n* Better MkPFS compression",
          "draft": false,
          "prerelease": false,
          "published_at": "2026-09-30T12:00:00Z",
          "html_url": "https://github.com/axotk1k1/ps5craft/releases/tag/v1.1.0",
          "assets": [
            {
              "name": "PS5Craft-win-x64.zip",
              "size": 204800000,
              "browser_download_url": "https://github.com/axotk1k1/ps5craft/releases/download/v1.1.0/PS5Craft-win-x64.zip"
            },
            {
              "name": "PS5Craft-win-x64.zip.sha256",
              "size": 80,
              "browser_download_url": "https://github.com/axotk1k1/ps5craft/releases/download/v1.1.0/PS5Craft-win-x64.zip.sha256"
            }
          ]
        }
        """;

    [Fact]
    public void ParseLatestRelease_SelectsZipAndChecksum()
    {
        var info = GitHubReleaseParser.ParseLatestRelease(SampleRelease, includePrereleases: false);
        Assert.NotNull(info);
        Assert.Equal(new Version(1, 1, 0), info!.Version);
        Assert.Equal("v1.1.0", info.TagName);
        Assert.EndsWith("PS5Craft-win-x64.zip", info.DownloadUrl);
        Assert.NotNull(info.ChecksumUrl);
        Assert.Equal(204800000, info.DownloadSize);
        Assert.False(info.IsPrerelease);
    }

    [Fact]
    public void ParseLatestRelease_IgnoresPrereleaseByDefault()
    {
        var json = SampleRelease.Replace("\"prerelease\": false", "\"prerelease\": true", StringComparison.Ordinal);
        var info = GitHubReleaseParser.ParseLatestRelease(json, includePrereleases: false);
        Assert.Null(info);
    }

    [Fact]
    public void ParseLatestRelease_AllowsPrereleaseWhenEnabled()
    {
        var json = SampleRelease.Replace("\"prerelease\": false", "\"prerelease\": true", StringComparison.Ordinal)
            .Replace("v1.1.0", "v1.1.0-beta.1", StringComparison.Ordinal);
        var info = GitHubReleaseParser.ParseLatestRelease(json, includePrereleases: true);
        Assert.NotNull(info);
        Assert.True(info!.IsPrerelease);
        Assert.Equal(new Version(1, 1, 0), info.Version);
    }

    [Fact]
    public void ParseLatestRelease_MissingZip_ReturnsNull()
    {
        var json = SampleRelease.Replace("PS5Craft-win-x64.zip", "other.zip", StringComparison.Ordinal);
        Assert.Null(GitHubReleaseParser.ParseLatestRelease(json, includePrereleases: false));
    }

    [Fact]
    public void ParseLatestRelease_RejectsUntrustedUrl()
    {
        var json = SampleRelease.Replace(
            "https://github.com/axotk1k1/ps5craft/releases/download/v1.1.0/PS5Craft-win-x64.zip",
            "https://evil.example/PS5Craft-win-x64.zip",
            StringComparison.Ordinal);
        Assert.Null(GitHubReleaseParser.ParseLatestRelease(json, includePrereleases: false));
    }

    [Fact]
    public void ParseLatestRelease_InvalidJson_Throws()
        => Assert.ThrowsAny<Exception>(() => GitHubReleaseParser.ParseLatestRelease("{not-json", false));

    [Theory]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789  PS5Craft-win-x64.zip")]
    [InlineData("SHA256 (PS5Craft-win-x64.zip) = ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    public void ParseSha256File(string content)
    {
        var hash = GitHubReleaseParser.ParseSha256File(content);
        Assert.NotNull(hash);
        Assert.Equal(64, hash!.Length);
    }

    [Fact]
    public void FormatReleaseNotes_BulletLines()
    {
        var lines = GitHubReleaseParser.FormatReleaseNotes("## Changes\n- one\n* two\n\nthree");
        Assert.Contains(lines, l => l.Contains("one", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("two", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseReleasesList_PicksNewest()
    {
        var json = """
            [
              {
                "tag_name": "v1.0.0", "draft": false, "prerelease": false,
                "assets": [{ "name": "PS5Craft-win-x64.zip", "size": 1,
                  "browser_download_url": "https://github.com/axotk1k1/ps5craft/releases/download/v1.0.0/PS5Craft-win-x64.zip" }]
              },
              {
                "tag_name": "v1.2.0", "draft": false, "prerelease": false,
                "assets": [{ "name": "PS5Craft-win-x64.zip", "size": 1,
                  "browser_download_url": "https://github.com/axotk1k1/ps5craft/releases/download/v1.2.0/PS5Craft-win-x64.zip" }]
              }
            ]
            """;
        var info = GitHubReleaseParser.ParseReleasesList(json, includePrereleases: false);
        Assert.Equal(new Version(1, 2, 0), info!.Version);
    }
}

public class GitHubUpdateServiceTests
{
    [Fact]
    public async Task CheckForUpdates_Available_WhenNewer()
    {
        var handler = new QueueHandler();
        handler.EnqueueJson(HttpStatusCode.OK, """
            {
              "tag_name": "v1.1.0", "draft": false, "prerelease": false,
              "html_url": "https://github.com/axotk1k1/ps5craft/releases/tag/v1.1.0",
              "assets": [{
                "name": "PS5Craft-win-x64.zip", "size": 10,
                "browser_download_url": "https://github.com/axotk1k1/ps5craft/releases/download/v1.1.0/PS5Craft-win-x64.zip"
              }]
            }
            """);
        using var svc = CreateService(handler, new Version(1, 0, 0));
        var result = await svc.CheckForUpdatesAsync(force: true);
        Assert.True(result.Succeeded);
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(new Version(1, 1, 0), result.LatestVersion);
    }

    [Fact]
    public async Task CheckForUpdates_SameVersion_NotAvailable()
    {
        var handler = new QueueHandler();
        handler.EnqueueJson(HttpStatusCode.OK, """
            {
              "tag_name": "v1.0.0", "draft": false, "prerelease": false,
              "assets": [{
                "name": "PS5Craft-win-x64.zip", "size": 10,
                "browser_download_url": "https://github.com/axotk1k1/ps5craft/releases/download/v1.0.0/PS5Craft-win-x64.zip"
              }]
            }
            """);
        using var svc = CreateService(handler, new Version(1, 0, 0));
        var result = await svc.CheckForUpdatesAsync(force: true);
        Assert.True(result.Succeeded);
        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckForUpdates_OlderRemote_NotAvailable()
    {
        var handler = new QueueHandler();
        handler.EnqueueJson(HttpStatusCode.OK, """
            {
              "tag_name": "v0.9.0", "draft": false, "prerelease": false,
              "assets": [{
                "name": "PS5Craft-win-x64.zip", "size": 10,
                "browser_download_url": "https://github.com/axotk1k1/ps5craft/releases/download/v0.9.0/PS5Craft-win-x64.zip"
              }]
            }
            """);
        using var svc = CreateService(handler, new Version(1, 0, 0));
        var result = await svc.CheckForUpdatesAsync(force: true);
        Assert.True(result.Succeeded);
        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckForUpdates_NetworkFailure_FailsSoftly()
    {
        var handler = new QueueHandler();
        handler.EnqueueException(new HttpRequestException("offline"));
        using var svc = CreateService(handler, new Version(1, 0, 0));
        var result = await svc.CheckForUpdatesAsync(force: true);
        Assert.False(result.Succeeded);
        Assert.False(result.IsUpdateAvailable);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    [Fact]
    public async Task CheckForUpdates_MissingAsset_Fails()
    {
        var handler = new QueueHandler();
        handler.EnqueueJson(HttpStatusCode.OK, """
            { "tag_name": "v1.2.0", "draft": false, "prerelease": false, "assets": [] }
            """);
        using var svc = CreateService(handler, new Version(1, 0, 0));
        var result = await svc.CheckForUpdatesAsync(force: true);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task CheckForUpdates_RateLimited_Skips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PS5Craft.Tests", Guid.NewGuid().ToString("N"));
        var settings = new SettingsService(dir);
        var s = settings.Current;
        s.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        settings.Save(s);

        var handler = new QueueHandler();
        using var http = new HttpClient(handler);
        using var svc = new GitHubUpdateService(settings, http, dir, () => new Version(1, 0, 0));
        var result = await svc.CheckForUpdatesAsync(force: false);
        Assert.True(result.SkippedDueToRateLimit);
        Assert.Equal(0, handler.RequestCount);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Download_WritesFile_AndReportsProgress()
    {
        var payload = Encoding.UTF8.GetBytes("hello-update-package");
        var handler = new QueueHandler();
        handler.EnqueueBytes(HttpStatusCode.OK, payload, "application/zip");

        var dir = Path.Combine(Path.GetTempPath(), "PS5Craft.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var svc = CreateService(handler, new Version(1, 0, 0), dir);
            var dest = Path.Combine(dir, "PS5Craft-win-x64.zip");
            var reports = new List<UpdateDownloadProgress>();
            var info = DummyInfo(payload.Length);
            await svc.DownloadAsync(info, dest, new Progress<UpdateDownloadProgress>(reports.Add));
            Assert.True(File.Exists(dest));
            Assert.Equal(payload, File.ReadAllBytes(dest));
            Assert.NotEmpty(reports);
            Assert.Equal(payload.Length, reports[^1].BytesReceived);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Download_Cancel_DeletesIncomplete()
    {
        var huge = new byte[1024 * 256];
        RandomNumberGenerator.Fill(huge);
        var handler = new QueueHandler();
        handler.EnqueueBytes(HttpStatusCode.OK, huge, "application/zip", throttleMs: 50);

        var dir = Path.Combine(Path.GetTempPath(), "PS5Craft.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var svc = CreateService(handler, new Version(1, 0, 0), dir);
            var dest = Path.Combine(dir, "PS5Craft-win-x64.zip");
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                svc.DownloadAsync(DummyInfo(huge.Length), dest, cancellationToken: cts.Token));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void VerifySha256_And_ValidateZip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PS5Craft.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var zipPath = Path.Combine(dir, "pkg.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("PS5Craft.exe");
                using var s = entry.Open();
                s.Write(Encoding.UTF8.GetBytes("fake-exe"));
            }

            using var settings = new DisposableSettings(dir);
            using var http = new HttpClient(new QueueHandler());
            using var svc = new GitHubUpdateService(settings.Service, http, dir, () => new Version(1, 0, 0));
            Assert.True(svc.ValidateZipArchive(zipPath));

            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zipPath))).ToLowerInvariant();
            Assert.True(svc.VerifySha256(zipPath, hash));
            Assert.False(svc.VerifySha256(zipPath, new string('0', 64)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task FetchChecksum_ParsesFile()
    {
        var handler = new QueueHandler();
        handler.EnqueueText(HttpStatusCode.OK, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  PS5Craft-win-x64.zip");
        using var svc = CreateService(handler, new Version(1, 0, 0));
        var info = DummyInfo(1);
        info = new UpdateInfo
        {
            Version = info.Version,
            TagName = info.TagName,
            DownloadUrl = info.DownloadUrl,
            DownloadSize = info.DownloadSize,
            AssetName = info.AssetName,
            ChecksumUrl = "https://github.com/axotk1k1/ps5craft/releases/download/v1.1.0/PS5Craft-win-x64.zip.sha256"
        };
        var hash = await svc.FetchChecksumAsync(info);
        Assert.Equal(64, hash!.Length);
    }

    private static UpdateInfo DummyInfo(long size) => new()
    {
        Version = new Version(1, 1, 0),
        TagName = "v1.1.0",
        DownloadUrl = "https://github.com/axotk1k1/ps5craft/releases/download/v1.1.0/PS5Craft-win-x64.zip",
        DownloadSize = size,
        AssetName = "PS5Craft-win-x64.zip"
    };

    private static GitHubUpdateService CreateService(QueueHandler handler, Version current, string? dir = null)
    {
        dir ??= Path.Combine(Path.GetTempPath(), "PS5Craft.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = new SettingsService(dir);
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        return new GitHubUpdateService(settings, http, dir, () => current);
    }

    private sealed class DisposableSettings : IDisposable
    {
        public SettingsService Service { get; }
        private readonly string _dir;
        public DisposableSettings(string dir)
        {
            _dir = dir;
            Service = new SettingsService(dir);
        }
        public void Dispose() { }
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _queue = new();
        public int RequestCount { get; private set; }

        public void EnqueueJson(HttpStatusCode code, string json)
            => _queue.Enqueue(_ => new HttpResponseMessage(code)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });

        public void EnqueueText(HttpStatusCode code, string text)
            => _queue.Enqueue(_ => new HttpResponseMessage(code)
            {
                Content = new StringContent(text, Encoding.UTF8, "text/plain")
            });

        public void EnqueueBytes(HttpStatusCode code, byte[] data, string contentType, int throttleMs = 0)
            => _queue.Enqueue(_ =>
            {
                HttpContent content = throttleMs > 0
                    ? new ThrottledContent(data, contentType, throttleMs)
                    : new ByteArrayContent(data);
                content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                content.Headers.ContentLength = data.Length;
                return new HttpResponseMessage(code) { Content = content };
            });

        public void EnqueueException(Exception ex)
            => _queue.Enqueue(_ => throw ex);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (_queue.Count == 0)
            {
                throw new InvalidOperationException("No queued HTTP response for " + request.RequestUri);
            }

            return Task.FromResult(_queue.Dequeue()(request));
        }
    }

    private sealed class ThrottledContent : HttpContent
    {
        private readonly byte[] _data;
        private readonly int _throttleMs;

        public ThrottledContent(byte[] data, string contentType, int throttleMs)
        {
            _data = data;
            _throttleMs = throttleMs;
            Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (var i = 0; i < _data.Length; i += 4096)
            {
                var len = Math.Min(4096, _data.Length - i);
                await stream.WriteAsync(_data.AsMemory(i, len));
                await Task.Delay(_throttleMs);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _data.Length;
            return true;
        }
    }
}
