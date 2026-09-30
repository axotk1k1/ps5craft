using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace PS5Craft.Updater;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var opts = UpdateOptions.Parse(args);
            if (opts is null)
            {
                Console.Error.WriteLine("Usage: PS5Craft.Updater --pid <pid> --app-dir <dir> --package <zip> --backup-dir <dir> --restart <exe>");
                return 2;
            }

            return Run(opts);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            TryWriteLog($"FATAL: {ex}");
            return 1;
        }
    }

    private static int Run(UpdateOptions opts)
    {
        TryWriteLog($"Updater start pid={opts.ProcessId} app={opts.AppDirectory}");

        if (!WaitForExit(opts.ProcessId, TimeSpan.FromMinutes(2)))
        {
            TryWriteLog("Timed out waiting for PS5Craft to exit.");
            return 3;
        }

        // Extra settle time for file handles.
        Thread.Sleep(750);

        if (!File.Exists(opts.PackagePath))
        {
            TryWriteLog("Package missing: " + opts.PackagePath);
            return 4;
        }

        Directory.CreateDirectory(opts.AppDirectory);
        Directory.CreateDirectory(opts.BackupDirectory);

        var staging = Path.Combine(Path.GetTempPath(), "PS5Craft", "staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        try
        {
            ZipFile.ExtractToDirectory(opts.PackagePath, staging, overwriteFiles: true);
            FlattenIfSingleRoot(staging);

            var newExe = Path.Combine(staging, "PS5Craft.exe");
            if (!File.Exists(newExe))
            {
                TryWriteLog("Extracted package does not contain PS5Craft.exe");
                return 5;
            }

            BackupCurrent(opts.AppDirectory, opts.BackupDirectory);
            try
            {
                CopyDirectory(staging, opts.AppDirectory);
            }
            catch (Exception ex)
            {
                TryWriteLog("Copy failed, restoring backup: " + ex);
                RestoreBackup(opts.BackupDirectory, opts.AppDirectory);
                return 6;
            }

            if (!File.Exists(Path.Combine(opts.AppDirectory, "PS5Craft.exe")))
            {
                TryWriteLog("Verification failed: PS5Craft.exe missing after update. Restoring.");
                RestoreBackup(opts.BackupDirectory, opts.AppDirectory);
                return 7;
            }

            TryDeleteDirectory(opts.BackupDirectory);
            TryDelete(opts.PackagePath);

            var restart = opts.RestartPath;
            if (File.Exists(restart))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = restart,
                        WorkingDirectory = opts.AppDirectory,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    TryWriteLog("Update files installed, but restart failed: " + ex.Message);
                    // Files are already updated; treat as success.
                }
            }

            TryWriteLog("Update completed successfully.");
            return 0;
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private static bool WaitForExit(int pid, TimeSpan timeout)
    {
        if (pid <= 0)
        {
            return true;
        }

        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            // Already exited.
            return true;
        }
    }

    private static void FlattenIfSingleRoot(string staging)
    {
        var entries = Directory.GetFileSystemEntries(staging);
        if (entries.Length != 1 || !Directory.Exists(entries[0]))
        {
            return;
        }

        var root = entries[0];
        if (File.Exists(Path.Combine(root, "PS5Craft.exe")))
        {
            foreach (var child in Directory.GetFileSystemEntries(root))
            {
                var name = Path.GetFileName(child);
                var dest = Path.Combine(staging, name);
                if (Directory.Exists(child))
                {
                    CopyDirectory(child, dest);
                    TryDeleteDirectory(child);
                }
                else
                {
                    File.Copy(child, dest, overwrite: true);
                    File.Delete(child);
                }
            }

            TryDeleteDirectory(root);
        }
    }

    private static void BackupCurrent(string appDir, string backupDir)
    {
        Directory.CreateDirectory(backupDir);
        foreach (var path in Directory.EnumerateFileSystemEntries(appDir))
        {
            var name = Path.GetFileName(path);
            if (name.Equals("Temp", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("update-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var dest = Path.Combine(backupDir, name);
            if (Directory.Exists(path))
            {
                CopyDirectory(path, dest);
            }
            else if (File.Exists(path))
            {
                File.Copy(path, dest, overwrite: true);
            }
        }
    }

    private static void RestoreBackup(string backupDir, string appDir)
    {
        if (!Directory.Exists(backupDir))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(backupDir))
        {
            var name = Path.GetFileName(path);
            var dest = Path.Combine(appDir, name);
            if (Directory.Exists(path))
            {
                if (Directory.Exists(dest))
                {
                    TryDeleteDirectory(dest);
                }

                CopyDirectory(path, dest);
            }
            else
            {
                File.Copy(path, dest, overwrite: true);
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, destination, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = file.Replace(source, destination, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            // Retry a few times for transient locks.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    File.Copy(file, dest, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 9)
                {
                    Thread.Sleep(200);
                }
            }
        }
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
            // ignore
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void TryWriteLog(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PS5Craft", "Logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "updater.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // ignore
        }
    }
}

internal sealed class UpdateOptions
{
    public required int ProcessId { get; init; }
    public required string AppDirectory { get; init; }
    public required string PackagePath { get; init; }
    public required string BackupDirectory { get; init; }
    public required string RestartPath { get; init; }

    public static UpdateOptions? Parse(string[] args)
    {
        string? appDir = null, package = null, backup = null, restart = null;
        var pid = 0;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            var next = i + 1 < args.Length ? args[i + 1] : null;
            switch (a)
            {
                case "--pid" when next is not null && int.TryParse(next, out var p):
                    pid = p;
                    i++;
                    break;
                case "--app-dir" when next is not null:
                    appDir = next;
                    i++;
                    break;
                case "--package" when next is not null:
                    package = next;
                    i++;
                    break;
                case "--backup-dir" when next is not null:
                    backup = next;
                    i++;
                    break;
                case "--restart" when next is not null:
                    restart = next;
                    i++;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(appDir)
            || string.IsNullOrWhiteSpace(package)
            || string.IsNullOrWhiteSpace(backup)
            || string.IsNullOrWhiteSpace(restart))
        {
            return null;
        }

        return new UpdateOptions
        {
            ProcessId = pid,
            AppDirectory = Path.GetFullPath(appDir),
            PackagePath = Path.GetFullPath(package),
            BackupDirectory = Path.GetFullPath(backup),
            RestartPath = Path.GetFullPath(restart)
        };
    }
}
