using System.Text.Json;
using PS5Craft.Core.Abstractions;
using PS5Craft.Core.Models;
using PS5Craft.Infrastructure;

namespace PS5Craft.Services;

public sealed class GameMetadataService : IGameMetadataService
{
    private readonly IFpkgService _fpkg;

    public GameMetadataService(IFpkgService fpkg)
    {
        _fpkg = fpkg;
    }

    public Task<GameInfo> ReadFromPackageAsync(string path, CancellationToken cancellationToken = default)
        => _fpkg.InspectAsync(path, cancellationToken: cancellationToken);

    public async Task<GameInfo> ReadFromFolderAsync(string folder, CancellationToken cancellationToken = default)
    {
        folder = PathValidator.Normalize(folder);
        if (!Directory.Exists(folder))
        {
            var missing = GameInfo.Unknown(folder);
            missing.Error = "Папка не найдена";
            return missing;
        }

        return await Task.Run(() =>
        {
            var info = GameInfo.Unknown(folder);
            info.PackageType = "Folder";
            info.CanExtract = false;

            var param = Path.Combine(folder, "sce_sys", "param.json");
            if (!File.Exists(param))
            {
                info.Error = "sce_sys/param.json не найден";
                return info;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(param));
                var root = doc.RootElement;
                info.ContentId = GetString(root, "contentId") ?? "Unknown";
                info.TitleId = GetString(root, "titleId") ?? ExtractTitleId(info.ContentId) ?? "Unknown";
                info.Title = ReadTitle(root) ?? "Unknown";
                info.Version = GetString(root, "contentVersion") ?? GetString(root, "masterVersion") ?? "Unknown";
                info.SdkVersion = GetString(root, "sdkVersion") ?? "Unknown";
                info.RequiredFirmware = GetString(root, "requiredSystemSoftwareVersion") ?? "Unknown";
                info.Region = DetectRegion(info.ContentId);
                info.Languages = ReadLanguages(root);
                info.Error = null;

                var icon = Path.Combine(folder, "sce_sys", "icon0.png");
                if (File.Exists(icon))
                {
                    info.CoverImageBytes = File.ReadAllBytes(icon);
                }

                info.InstalledSizeBytes = EstimateSize(folder);
                info.FileSizeBytes = info.InstalledSizeBytes;
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }

            return info;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string? ReadTitle(JsonElement root)
    {
        if (root.TryGetProperty("titleName", out var titles))
        {
            if (titles.ValueKind == JsonValueKind.String)
            {
                return titles.GetString();
            }

            if (titles.ValueKind == JsonValueKind.Object)
            {
                foreach (var preferred in new[] { "en-US", "en", "ru-RU", "ja-JP" })
                {
                    if (titles.TryGetProperty(preferred, out var t) && t.ValueKind == JsonValueKind.String)
                    {
                        return t.GetString();
                    }
                }

                foreach (var prop in titles.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        return prop.Value.GetString();
                    }
                }
            }
        }

        if (root.TryGetProperty("localizedParameters", out var lp) && lp.ValueKind == JsonValueKind.Object)
        {
            var lang = GetString(lp, "defaultLanguage") ?? "en-US";
            foreach (var key in new[] { lang, "en-US", "en-GB" })
            {
                if (lp.TryGetProperty(key, out var entry) && entry.ValueKind == JsonValueKind.Object &&
                    GetString(entry, "titleName") is { Length: > 0 } name)
                {
                    return name;
                }
            }
        }

        return GetString(root, "title");
    }

    private static List<string> ReadLanguages(JsonElement root)
    {
        var list = new List<string>();
        if (root.TryGetProperty("localizedLanguages", out var langs) && langs.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in langs.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.String)
                {
                    list.Add(e.GetString()!);
                }
            }
        }
        else if (root.TryGetProperty("localizedParameters", out var lp) && lp.ValueKind == JsonValueKind.Object)
        {
            list.AddRange(lp.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.Object && p.Name != "defaultLanguage")
                .Select(p => p.Name));
        }
        else if (root.TryGetProperty("titleName", out var titles) && titles.ValueKind == JsonValueKind.Object)
        {
            list.AddRange(titles.EnumerateObject().Select(p => p.Name));
        }

        return list;
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static string DetectRegion(string? contentId)
    {
        if (string.IsNullOrWhiteSpace(contentId) || contentId.Length < 2)
        {
            return "Unknown";
        }

        return contentId[..2].ToUpperInvariant() switch
        {
            "UP" => "USA",
            "EP" => "EUR",
            "JP" => "JPN",
            "HP" or "AP" => "ASIA",
            "KP" => "KOR",
            _ => "Unknown"
        };
    }

    private static string? ExtractTitleId(string? contentId)
    {
        if (string.IsNullOrWhiteSpace(contentId))
        {
            return null;
        }

        var dash = contentId.IndexOf('-');
        if (dash < 0)
        {
            return null;
        }

        var rest = contentId[(dash + 1)..];
        var under = rest.IndexOf('_');
        return under > 0 ? rest[..under] : null;
    }

    private static long EstimateSize(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => { try { return f.Length; } catch { return 0L; } });
        }
        catch
        {
            return 0;
        }
    }
}
