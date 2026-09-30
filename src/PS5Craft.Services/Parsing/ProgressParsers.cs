using System.Text.RegularExpressions;
using PS5Craft.Core.Models;

namespace PS5Craft.Services.Parsing;

/// <summary>
/// Parses MkPFS stderr progress lines of the form:
/// [################------------]  75% compress @ 1.2 GB/s ETA 21s
/// </summary>
public static partial class MkPfsProgressParser
{
    [GeneratedRegex(
        @"\[(?<bar>[#\-]+)\]\s*(?<pct>\d{1,3})%\s*(?<phase>\S+)(?:\s*@\s*(?<speed>[\d.,]+\s*[KMGT]?i?B?)/s)?(?:\s*ETA\s*(?<eta>\S+))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ProgressRegex();

    public static bool TryParse(string line, out OperationProgress progress)
    {
        progress = new OperationProgress();
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        // Carriage-return progress may arrive as a single segment.
        var trimmed = line.Trim().Trim('\r');
        var match = ProgressRegex().Match(trimmed);
        if (!match.Success)
        {
            if (trimmed.Contains("Scanning", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("скан", StringComparison.OrdinalIgnoreCase))
            {
                progress.StatusText = trimmed;
                progress.Phase = "scan";
                return true;
            }

            return false;
        }

        if (int.TryParse(match.Groups["pct"].Value, out var pct))
        {
            progress.Percent = Math.Clamp(pct, 0, 100);
        }

        progress.Phase = match.Groups["phase"].Value;
        progress.StatusText = trimmed;

        if (match.Groups["speed"].Success)
        {
            progress.SpeedBytesPerSecond = ParseSizeToBytes(match.Groups["speed"].Value);
        }

        if (match.Groups["eta"].Success)
        {
            progress.EstimatedRemaining = ParseEta(match.Groups["eta"].Value);
        }

        return true;
    }

    public static double ParseSizeToBytes(string text)
    {
        text = text.Trim();
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var numberPart = parts.Length > 0 ? parts[0] : text;
        var unitPart = parts.Length > 1 ? parts[1] : ExtractUnit(text, out numberPart);

        if (!double.TryParse(numberPart, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return 0;
        }

        return unitPart.ToUpperInvariant() switch
        {
            "KB" or "KIB" or "K" => value * 1024,
            "MB" or "MIB" or "M" => value * 1024 * 1024,
            "GB" or "GIB" or "G" => value * 1024 * 1024 * 1024,
            "TB" or "TIB" or "T" => value * 1024L * 1024 * 1024 * 1024,
            "B" => value,
            _ => value
        };
    }

    private static string ExtractUnit(string text, out string number)
    {
        var i = 0;
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] is '.' or ','))
        {
            i++;
        }

        number = text[..i].Replace(',', '.');
        return text[i..].Trim();
    }

    private static TimeSpan? ParseEta(string eta)
    {
        if (eta.EndsWith("s", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(eta.TrimEnd('s', 'S'), out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        if (eta.EndsWith("m", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(eta.TrimEnd('m', 'M'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var minutes))
        {
            return TimeSpan.FromMinutes(minutes);
        }

        if (TimeSpan.TryParse(eta, out var ts))
        {
            return ts;
        }

        return null;
    }
}

/// <summary>
/// Parses redirected fpkg-cli pkg-extract progress. When stdout is redirected fpkg-cli prints one line per 10 %:
/// [██████░░░░]  50,0% · 120/300 files · 3.5 GB / 7.1 GB · 00:01:12 ·
/// The percentage is formatted with the process culture, so both "50.0" and "50,0" occur.
/// </summary>
public static partial class FpkgProgressParser
{
    [GeneratedRegex(@"(?<![\d.,])(?<pct>\d{1,3}(?:[.,]\d+)?)\s*%", RegexOptions.Compiled)]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"(?<done>\d[\d\s.,]*)/(?<total>\d[\d\s.,]*)\s*(?:files|tệp)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex FilesRegex();

    public static bool TryParse(string line, out OperationProgress progress)
    {
        progress = new OperationProgress { StatusText = line, Phase = "extract" };
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = PercentRegex().Match(line);
        if (!match.Success)
        {
            // Still useful as status.
            return line.Contains("Extract", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("Giải", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("pkg", StringComparison.OrdinalIgnoreCase);
        }

        var raw = match.Groups["pct"].Value.Replace(',', '.');
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var pct))
        {
            progress.Percent = Math.Clamp(pct, 0, 100);
        }

        var files = FilesRegex().Match(line);
        if (files.Success)
        {
            progress.StatusText = $"{files.Groups["done"].Value.Trim()} / {files.Groups["total"].Value.Trim()} файлов";
        }

        return true;
    }
}
