using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PS5Craft.Core;

namespace PS5Craft.App.Converters;

/// <summary>ConverterParameter is one page name or a comma-separated list ("Extract,Pack,Info").</summary>
public sealed class PageVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not AppPage page || parameter is not string names)
        {
            return Visibility.Collapsed;
        }

        foreach (var name in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<AppPage>(name, true, out var expected) && page == expected)
            {
                return Visibility.Visible;
            }
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when CurrentPage equals the page named in ConverterParameter (used by nav RadioButtons).</summary>
public sealed class PageIsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is AppPage page && parameter is string name &&
        Enum.TryParse<AppPage>(name, true, out var expected) && page == expected;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>WorkflowStep + step number → "Active" / "Done" / "Idle" for the stepper template.</summary>
public sealed class StepStateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int current || !int.TryParse(parameter as string, out var step))
        {
            return "Idle";
        }

        return current == step ? "Active" : current > step ? "Done" : "Idle";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null)
        {
            return Visibility.Collapsed;
        }

        if (value is string s && string.IsNullOrWhiteSpace(s))
        {
            return Visibility.Collapsed;
        }

        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BytesToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long l ? ViewModels.MainViewModel.FormatBytes(l) : "Unknown";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Colours a log line by its level tag.</summary>
public sealed class LogLineBrushConverter : IValueConverter
{
    private static readonly Brush Error = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)));
    private static readonly Brush Warn = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xB4, 0x29)));
    private static readonly Brush Ok = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0xE3, 0x7A)));
    private static readonly Brush Normal = Freeze(new SolidColorBrush(Color.FromRgb(0xC3, 0xD0, 0xDE)));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var line = value as string ?? string.Empty;
        if (line.Contains("[ERROR]", StringComparison.Ordinal))
        {
            return Error;
        }

        if (line.Contains("[WARN]", StringComparison.Ordinal))
        {
            return Warn;
        }

        return line.Contains("[OK]", StringComparison.Ordinal) ? Ok : Normal;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}

/// <summary>
/// Turns a two-letter country code into a small flag brush (30×20 design units).
/// With ConverterParameter "vis" returns Visible when no flag is known, so a text chip can be shown instead.
/// </summary>
public sealed class FlagConverter : IValueConverter
{
    private static readonly Dictionary<string, Brush?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var code = value as string ?? string.Empty;
        var brush = Get(code);
        if (parameter as string == "vis")
        {
            return brush is null ? Visibility.Visible : Visibility.Collapsed;
        }

        return brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush? Get(string code)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(code, out var brush))
            {
                brush = Build(code.ToUpperInvariant());
                brush?.Freeze();
                Cache[code] = brush;
            }

            return brush;
        }
    }

    private static Brush? Build(string code)
    {
        var g = new DrawingGroup();
        switch (code)
        {
            case "FR": Vertical(g, "#0055A4", "#FFFFFF", "#EF4135"); break;
            case "IT": Vertical(g, "#009246", "#FFFFFF", "#CE2B37"); break;
            case "MX": Vertical(g, "#006847", "#FFFFFF", "#CE1126"); break;
            case "BE": Vertical(g, "#000000", "#FDDA24", "#EF3340"); break;
            case "DE": Horizontal(g, "#000000", "#DD0000", "#FFCE00"); break;
            case "RU": Horizontal(g, "#FFFFFF", "#0039A6", "#D52B1E"); break;
            case "NL": Horizontal(g, "#AE1C28", "#FFFFFF", "#21468B"); break;
            case "PL": Horizontal(g, "#FFFFFF", "#DC143C"); break;
            case "UA": Horizontal(g, "#0057B7", "#FFD700"); break;
            case "ES":
                Rect(g, "#AA151B", 0, 0, 30, 20);
                Rect(g, "#F1BF00", 0, 5, 30, 10);
                break;
            case "PT":
                Rect(g, "#FF0000", 0, 0, 30, 20);
                Rect(g, "#006600", 0, 0, 12, 20);
                break;
            case "JP":
                Rect(g, "#FFFFFF", 0, 0, 30, 20);
                Circle(g, "#BC002D", 15, 10, 6);
                break;
            case "KR":
                Rect(g, "#FFFFFF", 0, 0, 30, 20);
                Circle(g, "#0047A0", 15, 10, 5.5);
                g.Children.Add(new GeometryDrawing(Hex("#CD2E3A"), null,
                    Geometry.Parse("M9.5,10 A5.5,5.5 0 0 1 20.5,10 A2.75,2.75 0 0 1 15,10 A2.75,2.75 0 0 0 9.5,10 Z")));
                break;
            case "CN":
                Rect(g, "#DE2910", 0, 0, 30, 20);
                g.Children.Add(new GeometryDrawing(Hex("#FFDE00"), null,
                    Geometry.Parse("M6,2.5 L7.2,5.6 L10.5,5.6 L7.8,7.5 L8.8,10.6 L6,8.7 L3.2,10.6 L4.2,7.5 L1.5,5.6 L4.8,5.6 Z")));
                break;
            case "BR":
                Rect(g, "#009C3B", 0, 0, 30, 20);
                g.Children.Add(new GeometryDrawing(Hex("#FFDF00"), null, Geometry.Parse("M15,2 L28,10 L15,18 L2,10 Z")));
                Circle(g, "#002776", 15, 10, 4.2);
                break;
            case "GB":
                Rect(g, "#012169", 0, 0, 30, 20);
                Line(g, "#FFFFFF", 4, 0, 0, 30, 20);
                Line(g, "#FFFFFF", 4, 30, 0, 0, 20);
                Line(g, "#C8102E", 1.5, 0, 0, 30, 20);
                Line(g, "#C8102E", 1.5, 30, 0, 0, 20);
                Rect(g, "#FFFFFF", 12, 0, 6, 20);
                Rect(g, "#FFFFFF", 0, 7, 30, 6);
                Rect(g, "#C8102E", 13.2, 0, 3.6, 20);
                Rect(g, "#C8102E", 0, 8.2, 30, 3.6);
                break;
            case "US":
                Rect(g, "#FFFFFF", 0, 0, 30, 20);
                for (var i = 0; i < 7; i++)
                {
                    Rect(g, "#B22234", 0, i * 20.0 / 6.5, 30, 20.0 / 13);
                }

                Rect(g, "#3C3B6E", 0, 0, 12, 10.8);
                break;
            case "SE":
                Rect(g, "#006AA7", 0, 0, 30, 20);
                Rect(g, "#FECC02", 9, 0, 4, 20);
                Rect(g, "#FECC02", 0, 8, 30, 4);
                break;
            case "FI":
                Rect(g, "#FFFFFF", 0, 0, 30, 20);
                Rect(g, "#002F6C", 8, 0, 5, 20);
                Rect(g, "#002F6C", 0, 7.5, 30, 5);
                break;
            case "DK":
                Rect(g, "#C8102E", 0, 0, 30, 20);
                Rect(g, "#FFFFFF", 9, 0, 3.5, 20);
                Rect(g, "#FFFFFF", 0, 8.25, 30, 3.5);
                break;
            case "NO":
                Rect(g, "#BA0C2F", 0, 0, 30, 20);
                Rect(g, "#FFFFFF", 8, 0, 5, 20);
                Rect(g, "#FFFFFF", 0, 7.5, 30, 5);
                Rect(g, "#00205B", 9.25, 0, 2.5, 20);
                Rect(g, "#00205B", 0, 8.75, 30, 2.5);
                break;
            case "TR":
                Rect(g, "#E30A17", 0, 0, 30, 20);
                Circle(g, "#FFFFFF", 11, 10, 5);
                Circle(g, "#E30A17", 12.3, 10, 4);
                break;
            default:
                return null;
        }

        return new DrawingBrush(g) { Stretch = Stretch.Fill };
    }

    private static void Vertical(DrawingGroup g, params string[] colors)
    {
        var w = 30.0 / colors.Length;
        for (var i = 0; i < colors.Length; i++)
        {
            Rect(g, colors[i], i * w, 0, w + 0.05, 20);
        }
    }

    private static void Horizontal(DrawingGroup g, params string[] colors)
    {
        var h = 20.0 / colors.Length;
        for (var i = 0; i < colors.Length; i++)
        {
            Rect(g, colors[i], 0, i * h, 30, h + 0.05);
        }
    }

    private static void Rect(DrawingGroup g, string color, double x, double y, double w, double h) =>
        g.Children.Add(new GeometryDrawing(Hex(color), null, new RectangleGeometry(new Rect(x, y, w, h))));

    private static void Circle(DrawingGroup g, string color, double cx, double cy, double r) =>
        g.Children.Add(new GeometryDrawing(Hex(color), null, new EllipseGeometry(new Point(cx, cy), r, r)));

    private static void Line(DrawingGroup g, string color, double thickness, double x1, double y1, double x2, double y2) =>
        g.Children.Add(new GeometryDrawing(null, new Pen(Hex(color), thickness), new LineGeometry(new Point(x1, y1), new Point(x2, y2))));

    private static SolidColorBrush Hex(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
