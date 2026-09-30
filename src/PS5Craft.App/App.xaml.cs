using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using PS5Craft.Core.Abstractions;
using PS5Craft.Core.Process;
using PS5Craft.Infrastructure.Logging;
using PS5Craft.Infrastructure.Monitoring;
using PS5Craft.Infrastructure.Process;
using PS5Craft.Infrastructure.Settings;
using PS5Craft.Services;
using PS5Craft.Services.Updates;
using PS5Craft.ViewModels;

namespace PS5Craft.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService, SettingsService>();
        sc.AddSingleton<ILogService, LogService>();
        sc.AddSingleton<IExternalProcessRunner, ExternalProcessRunner>();
        sc.AddSingleton<IResourceMonitor, ResourceMonitor>();
        sc.AddSingleton<IMkPfsService, MkPfsService>();
        sc.AddSingleton<IFpkgService, FpkgService>();
        sc.AddSingleton<IGameMetadataService, GameMetadataService>();
        sc.AddSingleton<IUpdateService, GitHubUpdateService>();
        sc.AddSingleton<MainViewModel>();
        sc.AddSingleton<MainWindow>();

        Services = sc.BuildServiceProvider();
        var window = Services.GetRequiredService<MainWindow>();
        var vm = Services.GetRequiredService<MainViewModel>();
        var args = StartupArgs.Parse(e.Args);

        vm.UpdateAvailable += (_, info) =>
        {
            window.Dispatcher.Invoke(() =>
            {
                foreach (Window w in Current.Windows)
                {
                    if (w is UpdateWindow)
                    {
                        w.Activate();
                        return;
                    }
                }

                var dlg = new UpdateWindow(vm, info) { Owner = window };
                dlg.Show();
            });
        };

        vm.RequestCloseForUpdate += (_, _) =>
        {
            window.Dispatcher.Invoke(() => Shutdown());
        };

        if (args.Maximized)
        {
            window.WindowState = WindowState.Maximized;
        }
        else if (args.Size is { } size)
        {
            window.Width = size.Width;
            window.Height = size.Height;
        }
        else
        {
            // Full HD and smaller work areas: use the whole screen; on larger screens open at the default size.
            var area = SystemParameters.WorkArea;
            if (area.Width <= 1920 || area.Height <= 1080)
            {
                window.WindowState = WindowState.Maximized;
            }
        }

        window.Loaded += async (_, _) =>
        {
            if (args.Page is { } page)
            {
                vm.NavigateCommand.Execute(page);
            }

            if (args.PackagePath is { } pkg && File.Exists(pkg))
            {
                await vm.OpenPackageAsync(pkg);
            }

            if (args.ScreenshotPath is { } shot)
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Delay(400);
                SaveScreenshot(window, shot);
                Shutdown();
                return;
            }

            // Non-blocking background update check (skipped for screenshot mode).
            _ = vm.StartBackgroundUpdateCheckAsync();
        };

        window.Show();
    }

    /// <summary>Developer aid: renders the window client area to a PNG (used to compare against the design mockup).</summary>
    private static void SaveScreenshot(Window window, string path)
    {
        if (window.Content is not FrameworkElement root)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        var bmp = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }

        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private sealed record StartupArgs(string? PackagePath, string? ScreenshotPath, Size? Size, string? Page, bool Maximized)
    {
        public static StartupArgs Parse(string[] args)
        {
            string? pkg = null, shot = null, page = null;
            Size? size = null;
            var maximized = false;
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                var next = i + 1 < args.Length ? args[i + 1] : null;
                switch (a.ToLowerInvariant())
                {
                    case "--screenshot" when next is not null:
                        shot = next;
                        i++;
                        break;
                    case "--maximized":
                        maximized = true;
                        break;
                    case "--page" when next is not null:
                        page = next;
                        i++;
                        break;
                    case "--size" when next is not null:
                        var parts = next.Split('x', 'X');
                        if (parts.Length == 2 && double.TryParse(parts[0], out var w) && double.TryParse(parts[1], out var h))
                        {
                            size = new Size(w, h);
                        }

                        i++;
                        break;
                    default:
                        if (!a.StartsWith("--", StringComparison.Ordinal))
                        {
                            pkg = a;
                        }

                        break;
                }
            }

            return new StartupArgs(pkg, shot, size, page, maximized);
        }
    }
}
