using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PS5Craft.Core;
using PS5Craft.ViewModels;

namespace PS5Craft.App;

public partial class MainWindow : Window
{
    // Below this content height spacing in the workflow/progress panels is tightened.
    private const double CompactContentHeight = 820;
    private const double LogMinHeight = 140;
    private const double CompactLogMinHeight = 124;
    private const double AbsoluteLogMinHeight = 80;
    private const double MinGameInfoHeight = 150;
    // Title (28) + 6 rows × 22 + panel padding/border.
    private const double ReadableGameInfoHeight = 184;

    private static readonly Thickness ControlsMargin = new(16, 12, 16, 12);
    private static readonly Thickness CompactControlsMargin = new(16, 8, 16, 8);
    private static readonly Thickness ProgressPadding = new(16, 8, 16, 10);
    private static readonly Thickness CompactProgressPadding = new(16, 4, 16, 6);

    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Title = $"PS5Craft {AppVersion.DisplayShort} — PS5 Game Package Tool";

        ContentGrid.SizeChanged += (_, _) => UpdateHeightBudget();
        WorkflowPanel.SizeChanged += (_, _) => UpdateHeightBudget();
        ProgressPanel.SizeChanged += (_, _) => UpdateHeightBudget();
        StateChanged += (_, _) => UpdateWindowState();
        SizeChanged += (_, _) =>
        {
            if (WindowState == WindowState.Maximized)
            {
                UpdateWindowState();
            }
        };
        vm.LogLines.CollectionChanged += OnLogChanged;
        Drop += OnDrop;
        DragOver += OnDragOver;
    }

    /// <summary>
    /// Splits the real content height: workflow and progress keep their natural size, the log keeps a minimum,
    /// and the game-info panel gets at most what remains (its table/cover adapt to that). The log takes any surplus.
    /// </summary>
    private void UpdateHeightBudget()
    {
        var contentHeight = ContentGrid.ActualHeight;
        if (contentHeight <= 0)
        {
            return;
        }

        var compact = contentHeight < CompactContentHeight;
        StepperRow.Height = new GridLength(compact ? 58 : 66);
        GameInfoTitleRow.Height = new GridLength(compact ? 28 : 34);
        var controlsMargin = compact ? CompactControlsMargin : ControlsMargin;
        ExtractControls.Margin = controlsMargin;
        PackControls.Margin = controlsMargin;
        InfoControls.Margin = controlsMargin;
        ProgressPanel.Padding = compact ? CompactProgressPadding : ProgressPadding;
        ProgressTitleRow.Height = new GridLength(compact ? 24 : 28);
        ProgressInfoRow.Height = new GridLength(compact ? 22 : 24);
        var optionsMargin = new Thickness(0, compact ? 8 : 12, 0, 0);
        PackCombos.Margin = optionsMargin;
        PackOptions.Margin = optionsMargin;

        var fixedPanels = Outer(WorkflowPanel) + Outer(ProgressPanel) + LogPanel.Margin.Top;
        var free = contentHeight - fixedPanels;
        var gameInfo = free - (compact ? CompactLogMinHeight : LogMinHeight);
        if (gameInfo < ReadableGameInfoHeight)
        {
            // Keep the metadata readable (two columns of 22 px rows) before protecting more log lines.
            gameInfo = Math.Min(ReadableGameInfoHeight, free - AbsoluteLogMinHeight);
        }

        GameInfoPanel.MaxHeight = Math.Max(MinGameInfoHeight, gameInfo);
        var logMin = compact ? CompactLogMinHeight : LogMinHeight;

        var overlayLog = Math.Max(logMin, contentHeight * 0.25);
        OverlayPanel.Height = Math.Max(240, contentHeight - LogPanel.Margin.Top - overlayLog);
    }

    private static double Outer(FrameworkElement e) =>
        e.Visibility == Visibility.Collapsed ? 0 : e.ActualHeight + e.Margin.Top + e.Margin.Bottom;

    private void UpdateWindowState()
    {
        if (WindowState == WindowState.Maximized)
        {
            RootBorder.Margin = MaximizedOverhang();
            MaxButton.Content = "\uE923";
            MaxButton.ToolTip = "Восстановить";
        }
        else
        {
            RootBorder.Margin = new Thickness(0);
            MaxButton.Content = "\uE922";
            MaxButton.ToolTip = "Развернуть";
        }
    }

    /// <summary>A chromeless maximized window extends past the monitor work area by its resize frame; returns that overhang in DIPs.</summary>
    private Thickness MaximizedOverhang()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var win))
        {
            return new Thickness(0);
        }

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, MonitorDefaultToNearest), ref info))
        {
            return new Thickness(0);
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var work = info.Work;
        return new Thickness(
            Math.Max(0, work.Left - win.Left) / dpi.DpiScaleX,
            Math.Max(0, work.Top - win.Top) / dpi.DpiScaleY,
            Math.Max(0, win.Right - work.Right) / dpi.DpiScaleX,
            Math.Max(0, win.Bottom - work.Bottom) / dpi.DpiScaleY);
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || LogList.Items.Count == 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (LogList.Items.Count > 0)
            {
                LogList.ScrollIntoView(LogList.Items[^1]);
            }
        });
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedPackage(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (TryGetDroppedPackage(e) is { } path && !_vm.IsBusy)
        {
            await _vm.OpenPackageAsync(path);
        }
    }

    private static string? TryGetDroppedPackage(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files)
        {
            return null;
        }

        var ext = Path.GetExtension(files[0]);
        return File.Exists(files[0]) &&
               (ext.Equals(".pkg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".fpkg", StringComparison.OrdinalIgnoreCase))
            ? files[0]
            : null;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
