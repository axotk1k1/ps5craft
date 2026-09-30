using System.Reflection;

namespace PS5Craft.Core;

/// <summary>Application version from assembly metadata (Directory.Build.props → Version).</summary>
public static class AppVersion
{
    static AppVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // Strip any build metadata (+hash) from InformationalVersion.
            var plus = info.IndexOf('+');
            if (plus >= 0)
            {
                info = info[..plus];
            }

            if (Version.TryParse(Normalize(info), out var fromInfo))
            {
                Current = fromInfo;
                return;
            }
        }

        Current = asm.GetName().Version ?? new Version(1, 0, 0);
    }

    public static Version Current { get; }

    /// <summary>Semantic form without leading v, e.g. 1.0.0.</summary>
    public static string Semantic => ToSemantic(Current);

    /// <summary>UI form with leading v, e.g. v1.0.0.</summary>
    public static string Display => "v" + Semantic;

    /// <summary>Short UI form, e.g. v1.0 (major.minor).</summary>
    public static string DisplayShort => $"v{Current.Major}.{Current.Minor}";

    public static string ToSemantic(Version v)
    {
        var build = v.Build < 0 ? 0 : v.Build;
        return $"{v.Major}.{v.Minor}.{build}";
    }

    public static string Normalize(string? tagOrVersion)
    {
        if (string.IsNullOrWhiteSpace(tagOrVersion))
        {
            return "0.0.0";
        }

        var s = tagOrVersion.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            s = s[1..];
        }

        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            s = s[..dash];
        }

        return s;
    }

    public static bool TryParse(string? tagOrVersion, out Version version)
        => Version.TryParse(Normalize(tagOrVersion), out version!);

    public static int Compare(string? a, string? b)
    {
        TryParse(a, out var va);
        TryParse(b, out var vb);
        va ??= new Version(0, 0, 0);
        vb ??= new Version(0, 0, 0);
        return va.CompareTo(vb);
    }

    public static bool IsPrerelease(string? tagOrVersion)
    {
        if (string.IsNullOrWhiteSpace(tagOrVersion))
        {
            return false;
        }

        var s = tagOrVersion.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            s = s[1..];
        }

        return s.Contains('-', StringComparison.Ordinal);
    }
}
