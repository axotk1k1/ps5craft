namespace PS5Craft.Infrastructure;

public static class PathValidator
{
    public static void EnsureExistingFile(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException($"{label} not found: {path}", path);
        }
    }

    public static void EnsureExistingDirectory(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"{label} not found: {path}");
        }
    }

    public static string Normalize(string path) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));

    public static bool IsSafeToolPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // Reject shell metacharacters — we never pass through cmd.exe.
        ReadOnlySpan<char> bad = ['&', '|', '>', '<', '^'];
        return path.AsSpan().IndexOfAny(bad) < 0;
    }
}
