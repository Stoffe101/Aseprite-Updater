using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AsepriteManager;

/// <summary>
/// Pixel-perfect screenshots of the actual unified MainWindow, not isolated
/// child windows. Every route is photographed at normal and compact sizes,
/// including scrolled content for sections taller than their viewport.
/// </summary>
public static class LayoutSmoke
{
    private static readonly string[] Routes =
    {
        "overview", "aseprite", "themes", "tools", "palettes",
        "palette-import", "extensions", "godot", "brushes", "backup",
        "updates", "recovery", "profiles", "performance", "activity"
    };

    public static void Run()
    {
        Directory.CreateDirectory("ui-snapshots");
        foreach (var (width, height) in new[]
            { (1024, 720), (1366, 900), (1920, 1080) })
        {
            var window = new MainWindow();
            // Realistic state makes clipping, badge truncation, and missing
            // controls visible instead of rendering only placeholder text.
            window.InstalledVersion.Text = "v1.3.18.2";
            window.InstalledSubtitle.Text = "Installed source build";
            window.LatestVersion.Text = "v1.3.18.6";
            window.LatestSubtitle.Text = "Latest stable on GitHub";
            window.VersionPicker.ItemsSource = new[]
            {
                new ReleaseInfo("v1.3.18.6", "1.3.18.6", false, true, DateTimeOffset.UtcNow),
                new ReleaseInfo("v1.3.18.2", "1.3.18.2", false, false, DateTimeOffset.UtcNow.AddDays(-30)),
                new ReleaseInfo("v1.3.18-beta1", "1.3.18-beta1", true, false, DateTimeOffset.UtcNow.AddMonths(-2))
            };
            window.VersionPicker.IsEnabled = true;
            window.VersionPicker.SelectedIndex = 0;
            window.DependencySummary.Text =
                "Git, Git Bash, CMake, Ninja, Visual Studio 2022 C++ and Windows SDK are ready.";
            window.StatusTitle.Text = "Ready to switch";
            window.StatusDetail.Text =
                "Previously compiled version found. Changes are verified and the previous installation is retained.";
            window.ActivityLog.Text =
                "[12:03] [259/1575] Compiling Aseprite source...\n" +
                "[12:04] Cached Skia ready. Completed source validation.\n" +
                "[12:05] Previous installation backup preserved.";

            foreach (var route in Routes)
            {
                window.NavigateTo(route);
                var root = (FrameworkElement)window.Content;
                var bounds = new Size(width, height);
                root.Measure(bounds);
                root.Arrange(new Rect(new Point(0, 0), bounds));
                root.UpdateLayout();

                if (window.ShellStage.ActualWidth < 50 ||
                    ((route is "overview" or "aseprite" or "themes" or "activity") &&
                        window.MainScroller.ActualWidth < 300) ||
                    window.NavOverview.ActualWidth < 100)
                    throw new InvalidOperationException(
                        $"Unified shell unusable at {width}x{height}, route {route}.");

                if (route == "aseprite" && (window.VersionPicker.ActualWidth < 130 ||
                    window.InstallButton.ActualWidth < 120))
                    throw new InvalidOperationException("Installer controls clipped.");
                if (route == "tools" &&
                    window.LibraryHost.Content is LibraryWindow library &&
                    library.ToolList.ActualWidth < 150)
                    throw new InvalidOperationException("Resource selection panel clipped.");
                if (route == "updates" &&
                    window.ControlHost.Content is ControlCenterWindow center &&
                    center.CheckAllButton.ActualWidth < 120)
                    throw new InvalidOperationException("Update review controls clipped.");

                var scroll = route switch
                {
                    "overview" or "aseprite" or "themes" or "activity" => window.MainScroller,
                    "tools" or "palettes" or "palette-import" or "extensions" or "godot"
                        or "brushes" or "backup" =>
                        (window.LibraryHost.Content as LibraryWindow)!.LibraryScroller,
                    "updates" or "recovery" or "profiles" =>
                        (window.ControlHost.Content as ControlCenterWindow)!.PageScroller,
                    _ => FindScrollViewer(window.PerformanceHost)
                };
                if (scroll == null)
                    throw new InvalidOperationException("Missing scroll container for " + route);
                scroll.ScrollToVerticalOffset(0);
                root.UpdateLayout();
                SaveSnapshot(root, width, height, route, "top");

                // Capture midpoint and bottom to review ALL content, not just
                // the portion initially visible above the fold.
                var length = scroll.ScrollableHeight;
                if (length > 30)
                {
                    foreach (var (label, factor) in new[]
                    { ("middle", 0.5), ("bottom", 1.0) })
                    {
                        scroll.ScrollToVerticalOffset(length * factor);
                        root.UpdateLayout();
                        SaveSnapshot(root, width, height, route, label);
                    }
                }
                scroll.ScrollToVerticalOffset(0);
                Console.WriteLine($"PASS: unified {route} panel at {width}x{height}, " +
                    $"scroll height {length:F0}");
            }
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject obj)
    {
        if (obj is ScrollViewer scroller) return scroller;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var match = FindScrollViewer(VisualTreeHelper.GetChild(obj, i));
            if (match != null) return match;
        }
        return null;
    }

    private static void SaveSnapshot(FrameworkElement root, int width, int height,
        string route, string position)
    {
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var stream = File.Create(Path.Combine("ui-snapshots",
            $"unified-{route}-{width}x{height}-{position}.png"));
        encoder.Save(stream);
    }
}
