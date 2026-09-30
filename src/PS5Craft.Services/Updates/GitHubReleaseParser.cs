using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PS5Craft.Core;
using PS5Craft.Core.Models;

namespace PS5Craft.Services.Updates;

/// <summary>Parses GitHub Releases API JSON into <see cref="UpdateInfo"/> (no network).</summary>
public static partial class GitHubReleaseParser
{
    public const string Owner = "axotk1k1";
    public const string Repository = "ps5craft";
    public const string PreferredAssetName = "PS5Craft-win-x64.zip";
    public const string ChecksumAssetName = "PS5Craft-win-x64.zip.sha256";

    public static UpdateInfo? ParseLatestRelease(string json, bool includePrereleases)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseReleaseElement(doc.RootElement, includePrereleases);
    }

    public static UpdateInfo? ParseReleasesList(string json, bool includePrereleases)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        UpdateInfo? best = null;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var info = ParseReleaseElement(el, includePrereleases);
            if (info is null)
            {
                continue;
            }

            if (best is null || info.Version > best.Version)
            {
                best = info;
            }
        }

        return best;
    }

    public static UpdateInfo? ParseReleaseElement(JsonElement root, bool includePrereleases)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var draft = root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True;
        if (draft)
        {
            return null;
        }

        var prerelease = root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        if (!includePrereleases && (prerelease || AppVersion.IsPrerelease(tag)))
        {
            return null;
        }

        if (!AppVersion.TryParse(tag, out var version) || version is null)
        {
            return null;
        }

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? zipUrl = null;
        string? zipName = null;
        long zipSize = 0;
        string? checksumUrl = null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (!IsTrustedDownloadUrl(url))
            {
                continue;
            }

            if (name.Equals(PreferredAssetName, StringComparison.OrdinalIgnoreCase))
            {
                zipUrl = url;
                zipName = name;
                zipSize = asset.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var size) ? size : 0;
            }
            else if (name.Equals(ChecksumAssetName, StringComparison.OrdinalIgnoreCase))
            {
                checksumUrl = url;
            }
        }

        if (zipUrl is null)
        {
            return null;
        }

        DateTimeOffset? published = null;
        if (root.TryGetProperty("published_at", out var pub) && pub.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(pub.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
        {
            published = dt;
        }

        return new UpdateInfo
        {
            Version = version,
            TagName = tag,
            ReleaseName = root.TryGetProperty("name", out var rn) ? rn.GetString() ?? tag : tag,
            ReleaseNotes = root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
            PublishedAt = published,
            DownloadUrl = zipUrl,
            DownloadSize = zipSize,
            ChecksumUrl = checksumUrl,
            HtmlUrl = root.TryGetProperty("html_url", out var html) ? html.GetString() : null,
            AssetName = zipName ?? PreferredAssetName,
            IsPrerelease = prerelease || AppVersion.IsPrerelease(tag)
        };
    }

    public static bool IsTrustedDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.Host;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ParseSha256File(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var m = Sha256Regex().Match(content);
        return m.Success ? m.Groups["hash"].Value.ToLowerInvariant() : null;
    }

    public static IReadOnlyList<string> FormatReleaseNotes(string? body, int maxLines = 8)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>();
        foreach (var raw in body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            line = BulletRegex().Replace(line, "• ");
            lines.Add(line);
            if (lines.Count >= maxLines)
            {
                break;
            }
        }

        return lines;
    }

    [GeneratedRegex(@"^\s*[-*•]\s+", RegexOptions.Compiled)]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"\b(?<hash>[A-Fa-f0-9]{64})\b", RegexOptions.Compiled)]
    private static partial Regex Sha256Regex();
}
