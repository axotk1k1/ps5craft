using PS5Craft.Core.Process;
using PS5Craft.Infrastructure.Process;
using PS5Craft.Services.Parsing;
using Xunit;

namespace PS5Craft.Tests;

public class ExternalProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_EchoSuccess_WithSpacesInArgs()
    {
        var runner = new ExternalProcessRunner();
        var result = await runner.RunAsync(new ProcessStartRequest
        {
            Executable = "cmd.exe",
            Arguments = { "/c", "echo", "My Game Path" },
            Timeout = TimeSpan.FromSeconds(10)
        });
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("My Game Path", result.StdOut);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task RunAsync_MissingExecutable_Throws()
    {
        var runner = new ExternalProcessRunner();
        await Assert.ThrowsAsync<FileNotFoundException>(() => runner.RunAsync(new ProcessStartRequest
        {
            Executable = @"C:\definitely\missing\tool.exe",
            Arguments = { "a" }
        }));
    }

    [Fact]
    public async Task RunAsync_Cancel_Terminates()
    {
        var runner = new ExternalProcessRunner();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var result = await runner.RunAsync(new ProcessStartRequest
        {
            Executable = "cmd.exe",
            Arguments = { "/c", "ping", "127.0.0.1", "-n", "30" }
        }, cancellationToken: cts.Token);
        Assert.True(result.Cancelled || result.ExitCode != 0);
    }

    [Fact]
    public async Task RunAsync_NonZeroExit()
    {
        var runner = new ExternalProcessRunner();
        var result = await runner.RunAsync(new ProcessStartRequest
        {
            Executable = "cmd.exe",
            Arguments = { "/c", "exit", "7" }
        });
        Assert.Equal(7, result.ExitCode);
        Assert.False(result.Succeeded);
    }
}

public class ProgressParserTests
{
    [Fact]
    public void MkPfs_ParsesPercentPhaseSpeed()
    {
        var ok = MkPfsProgressParser.TryParse("[################------------]  75% compress @ 1.2 GB/s ETA 21s", out var p);
        Assert.True(ok);
        Assert.Equal(75, p.Percent);
        Assert.Equal("compress", p.Phase);
        Assert.True(p.SpeedBytesPerSecond > 1_000_000_000 * 0.5);
    }

    [Fact]
    public void MkPfs_NonProgress_ReturnsFalseOrStatus()
    {
        var ok = MkPfsProgressParser.TryParse("hello world", out _);
        Assert.False(ok);
    }

    [Theory]
    [InlineData("[██░░░░░░░░░░░░░░░░░░░░░░]  10.0% · 12/300 files · 715 MB / 7.15 GB · 00:00:12 · ", 10.0)]
    [InlineData("[████████████░░░░░░░░░░░░]  50,0% · 150/300 files · 3,6 GB / 7,15 GB · 00:01:02 · ", 50.0)]
    [InlineData("[████████████████████████] 100,0% · 300/300 files · 7,15 GB / 7,15 GB · 00:02:03 · ", 100.0)]
    [InlineData("[██████████████████░░░░░░]  75.3% · 1 234/2 000 tệp · 5 GB / 7 GB · 00:01:40 · ", 75.3)]
    public void FpkgParser_ReadsPercentWithEitherDecimalSeparator(string line, double expected)
    {
        Assert.True(FpkgProgressParser.TryParse(line, out var p));
        Assert.NotNull(p.Percent);
        Assert.Equal(expected, p.Percent!.Value, 1);
    }

    [Theory]
    [InlineData("1.5 GB", 1.5 * 1024 * 1024 * 1024)]
    [InlineData("380 MB", 380.0 * 1024 * 1024)]
    public void ParseSize(string text, double expected)
    {
        var bytes = MkPfsProgressParser.ParseSizeToBytes(text);
        Assert.InRange(bytes, expected * 0.99, expected * 1.01);
    }
}

public class SettingsServiceTests
{
    [Fact]
    public void SaveLoad_RoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PS5Craft.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var svc = new PS5Craft.Infrastructure.Settings.SettingsService(dir);
            var s = svc.Current;
            s.CompressionLevel = 5;
            s.CpuCount = 4;
            s.MkPfsPath = @"D:\Tools\My MkPFS\mkpfs.exe";
            svc.Save(s);
            svc.Reload();
            var loaded = svc.Current;
            Assert.Equal(5, loaded.CompressionLevel);
            Assert.Equal(4, loaded.CpuCount);
            Assert.Equal(@"D:\Tools\My MkPFS\mkpfs.exe", loaded.MkPfsPath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class PackageOnlyFilesTests
{
    [Fact]
    public void Remove_DeletesCntArtifacts_KeepsGameMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "ps5craft-pof-" + Guid.NewGuid().ToString("N"));
        var sceSys = Path.Combine(root, "sce_sys");
        Directory.CreateDirectory(Path.Combine(sceSys, "trophy2"));
        string[] keep = ["param.json", "icon0.png", "keystone", "pfs-version.dat", "nptitle.dat", "trophy2/trophy00.ucp"];
        string[] drop = ["license.dat", "license.info", "playgo-chunk.dat", "playgo-hash-table.dat", "playgo-ficm.dat", "playgo-scenario.json"];
        foreach (var f in keep.Concat(drop))
        {
            File.WriteAllText(Path.Combine(sceSys, f), "x");
        }

        try
        {
            var removed = PS5Craft.Services.PackageOnlyFiles.Remove(root);
            Assert.Equal(drop.Length, removed.Count);
            Assert.All(drop, f => Assert.False(File.Exists(Path.Combine(sceSys, f))));
            Assert.All(keep, f => Assert.True(File.Exists(Path.Combine(sceSys, f))));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

public class AmprEmulatorTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "ps5craft-ampr-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(0)]
    [InlineData((1 << 20) - 4)]
    [InlineData(3 * (1 << 20) + 7)]
    public void ContainsImport_FindsMarkerAnywhere_IncludingChunkBoundary(int offset)
    {
        var data = new byte[offset + 4096];
        System.Text.Encoding.ASCII.GetBytes("libSceAmpr").CopyTo(data, offset);
        Assert.True(PS5Craft.Services.AmprEmulator.ContainsImport(new MemoryStream(data)));
        Assert.False(PS5Craft.Services.AmprEmulator.ContainsImport(new MemoryStream(new byte[offset + 4096])));
    }

    [Fact]
    public void Inspect_ReportsMissingModule_ThenRestoreCopiesBundledOnce()
    {
        var build = PS5Craft.Services.AmprEmulator.FindBuild(PS5Craft.Services.AmprEmulator.DefaultVersion);
        Assert.NotNull(build);
        var bundled = PS5Craft.Services.AmprEmulator.FindBundledModule(build!.Version);
        Assert.NotNull(bundled);
        var root = NewRoot();
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "eboot.bin"), System.Text.Encoding.ASCII.GetBytes("\0\0libSceAmpr.sprx\0"));
        try
        {
            var status = PS5Craft.Services.AmprEmulator.Inspect(root);
            Assert.True(status.NeedsModule);

            var target = PS5Craft.Services.AmprEmulator.Restore(root, bundled!, build);
            Assert.Equal(Path.Combine(root, "fakelib", "libSceAmpr.sprx"), target);
            Assert.Equal(build.Sha256, PS5Craft.Services.AmprEmulator.ComputeSha256(target));
            Assert.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(target) < TimeSpan.FromMinutes(1));
            Assert.False(PS5Craft.Services.AmprEmulator.Inspect(root).NeedsModule);
            Assert.Equal(build.Version, PS5Craft.Services.AmprEmulator.Inspect(root).PresentVersion);

            Assert.Throws<IOException>(() => PS5Craft.Services.AmprEmulator.Restore(root, bundled!, build));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Restore_CanOverwriteWithAnotherKnownBuild()
    {
        var first = PS5Craft.Services.AmprEmulator.KnownBuilds[0];
        var second = PS5Craft.Services.AmprEmulator.KnownBuilds[1];
        var firstPath = PS5Craft.Services.AmprEmulator.FindBundledModule(first.Version);
        var secondPath = PS5Craft.Services.AmprEmulator.FindBundledModule(second.Version);
        Assert.NotNull(firstPath);
        Assert.NotNull(secondPath);

        var root = NewRoot();
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ampr_emu.index"), "stale");
        try
        {
            PS5Craft.Services.AmprEmulator.Restore(root, firstPath!, first);
            var target = PS5Craft.Services.AmprEmulator.Restore(root, secondPath!, second, overwrite: true);
            Assert.Equal(second.Sha256, PS5Craft.Services.AmprEmulator.ComputeSha256(target));
            Assert.False(File.Exists(Path.Combine(root, "ampr_emu.index")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Restore_RejectsModuleWithUnexpectedHash()
    {
        var build = PS5Craft.Services.AmprEmulator.FindBuild(PS5Craft.Services.AmprEmulator.DefaultVersion)!;
        var root = NewRoot();
        Directory.CreateDirectory(root);
        var fake = Path.Combine(root, "fake.sprx");
        File.WriteAllBytes(fake, new byte[128]);
        try
        {
            Assert.Throws<InvalidDataException>(() => PS5Craft.Services.AmprEmulator.Restore(root, fake, build));
            Assert.False(File.Exists(Path.Combine(root, "fakelib", "libSceAmpr.sprx")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Inspect_GameWithoutAmprImport_NeedsNothing()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "eboot.bin"), new byte[2048]);
        try
        {
            Assert.False(PS5Craft.Services.AmprEmulator.Inspect(root).NeedsModule);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
