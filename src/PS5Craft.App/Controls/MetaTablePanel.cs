using System.Windows;
using System.Windows.Controls;

namespace PS5Craft.App.Controls;

/// <summary>
/// Lays out metadata rows in one column; when the available height cannot hold every row at
/// <see cref="MinSingleColumnRowHeight"/>, rows flow column-first into two columns instead of becoming unreadably small.
/// </summary>
public sealed class MetaTablePanel : Panel
{
    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(
        nameof(RowHeight), typeof(double), typeof(MetaTablePanel),
        new FrameworkPropertyMetadata(32.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MinSingleColumnRowHeightProperty = DependencyProperty.Register(
        nameof(MinSingleColumnRowHeight), typeof(double), typeof(MetaTablePanel),
        new FrameworkPropertyMetadata(27.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MinRowHeightProperty = DependencyProperty.Register(
        nameof(MinRowHeight), typeof(double), typeof(MetaTablePanel),
        new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ColumnGapProperty = DependencyProperty.Register(
        nameof(ColumnGap), typeof(double), typeof(MetaTablePanel),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private int _columns = 1;
    private double _rowHeight = 32;

    public double RowHeight
    {
        get => (double)GetValue(RowHeightProperty);
        set => SetValue(RowHeightProperty, value);
    }

    public double MinSingleColumnRowHeight
    {
        get => (double)GetValue(MinSingleColumnRowHeightProperty);
        set => SetValue(MinSingleColumnRowHeightProperty, value);
    }

    public double MinRowHeight
    {
        get => (double)GetValue(MinRowHeightProperty);
        set => SetValue(MinRowHeightProperty, value);
    }

    public double ColumnGap
    {
        get => (double)GetValue(ColumnGapProperty);
        set => SetValue(ColumnGapProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = VisibleCount();
        if (count == 0)
        {
            return default;
        }

        var height = availableSize.Height;
        if (double.IsInfinity(height) || height >= count * MinSingleColumnRowHeight)
        {
            _columns = 1;
            _rowHeight = double.IsInfinity(height) ? RowHeight : Math.Min(RowHeight, height / count);
        }
        else
        {
            _columns = 2;
            var rows = (count + 1) / 2;
            _rowHeight = Math.Max(MinRowHeight, Math.Min(RowHeight, height / rows));
        }

        var rowsPerColumn = (count + _columns - 1) / _columns;
        var width = availableSize.Width;
        var columnWidth = double.IsInfinity(width) ? double.PositiveInfinity : (width - ColumnGap * (_columns - 1)) / _columns;

        var widest = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            child.Measure(new Size(columnWidth, _rowHeight));
            widest = Math.Max(widest, child.DesiredSize.Width);
        }

        var desiredWidth = double.IsInfinity(width) ? widest * _columns + ColumnGap * (_columns - 1) : width;
        return new Size(desiredWidth, rowsPerColumn * _rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = VisibleCount();
        if (count == 0)
        {
            return finalSize;
        }

        var rowsPerColumn = (count + _columns - 1) / _columns;
        var columnWidth = (finalSize.Width - ColumnGap * (_columns - 1)) / _columns;
        var index = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            var column = index / rowsPerColumn;
            var row = index % rowsPerColumn;
            child.Arrange(new Rect(column * (columnWidth + ColumnGap), row * _rowHeight, columnWidth, _rowHeight));
            index++;
        }

        return finalSize;
    }

    private int VisibleCount()
    {
        var n = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility != Visibility.Collapsed)
            {
                n++;
            }
        }

        return n;
    }
}
