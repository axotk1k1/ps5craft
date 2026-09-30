using PS5Craft.Core.Abstractions;
using PS5Craft.Core.Models;
using PS5Craft.Infrastructure;

namespace PS5Craft.Services.Tools;

public static class ToolLocator
{
    public static IEnumerable<string> CandidateFpkgCliPaths(ISettingsService settings, string? appBase = null)
    {
        appBase ??= AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(settings.Current.FpkgCliPath))
        {
            yield return PathValidator.Normalize(settings.Current.FpkgCliPath);
        }

        yield return Path.Combine(appBase, "tools", "fpkg", "fpkg-cli.exe");
        yield return Path.Combine(appBase, "tools", "fpkg", "fpkg-cli");
        yield return Path.Combine(appBase, "fpkg-cli.exe");

        // Dev layout: repo tools folder relative to bin
        yield return Path.GetFullPath(Path.Combine(appBase, "..", "..", "..", "..", "..", "tools", "fpkg", "fpkg-cli.exe"));
        yield return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "tools", "fpkg", "fpkg-cli.exe"));
    }

    public static IEnumerable<(string Executable, List<string> PrefixArgs)> CandidateMkPfsInvocations(ISettingsService settings, string? appBase = null)
    {
        appBase ??= AppContext.BaseDirectory;
        var settingsPath = settings.Current.MkPfsPath;
        if (!string.IsNullOrWhiteSpace(settingsPath))
        {
            var p = PathValidator.Normalize(settingsPath);
            if (p.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            {
                var python = ResolvePython(settings) ?? "python";
                yield return (python, ["-m", "mkpfs"]);
            }
            else
            {
                yield return (p, []);
            }
        }

        var local = Path.Combine(appBase, "tools", "mkpfs", "mkpfs.exe");
        if (File.Exists(local))
        {
            yield return (local, []);
        }

        local = Path.Combine(appBase, "tools", "mkpfs", "mkpfs");
        if (File.Exists(local))
        {
            yield return (local, []);
        }

        local = Path.GetFullPath(Path.Combine(appBase, "..", "..", "..", "..", "..", "tools", "mkpfs", "mkpfs.exe"));
        if (File.Exists(local))
        {
            yield return (local, []);
        }

        yield return ("mkpfs", []);

        var pythonExe = ResolvePython(settings) ?? "python";
        yield return (pythonExe, ["-m", "mkpfs"]);
    }

    public static string? ResolvePython(ISettingsService settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Current.PythonPath) && File.Exists(settings.Current.PythonPath))
        {
            return settings.Current.PythonPath;
        }

        foreach (var name in new[] { "python", "python3", "py" })
        {
            if (FindOnPath(name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    public static string? FindOnPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? name : name + ext);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            var bare = Path.Combine(dir, name);
            if (File.Exists(bare))
            {
                return bare;
            }
        }

        return null;
    }

    public static string? FirstExisting(IEnumerable<string> paths) =>
        paths.FirstOrDefault(File.Exists);
}
