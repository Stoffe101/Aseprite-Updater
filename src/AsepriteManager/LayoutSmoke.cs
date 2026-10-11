using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AsepriteManager;

public static class LayoutSmoke
{
    public static void Run()
    {
        Directory.CreateDirectory("ui-snapshots");
        foreach (var (width, height) in new[] { (850, 660), (1040, 875), (1440, 950) })
        {
            var window = new MainWindow();
            // Populate realistic release data so screenshots exercise selected-text
            // styling instead of only showing an empty disabled dropdown.
            window.InstalledVersion.Text = "v1.3.18.2-dev";
            window.InstalledSubtitle.Text = "Installed source build";
            window.LatestVersion.Text = "v1.3.18.6";
            window.LatestSubtitle.Text = "Official GitHub stable release";
            window.VersionPicker.ItemsSource = new[]
            {
                new ReleaseInfo("v1.3.18.6", "1.3.18.6", false, true, DateTimeOffset.UtcNow),
                new ReleaseInfo("v1.3.18.2", "1.3.18.2", false, false, DateTimeOffset.UtcNow.AddDays(-30)),
                new ReleaseInfo("v1.3.18-beta1", "1.3.18-beta1", true, false, DateTimeOffset.UtcNow.AddMonths(-2))
            };
            window.VersionPicker.IsEnabled = true;
            window.VersionPicker.SelectedIndex = 0;
            window.DependencySummary.Text = "Git: Ready   •   CMake: Ready   •   Ninja: Ready   •   Visual Studio 2022 C++: Ready";
            window.StatusTitle.Text = "Version change available";
            window.StatusDetail.Text = "A newer version is ready. Your previous installation and custom themes will be backed up.";
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();

            var required = new FrameworkElement[] {
                window.InstalledVersion, window.LatestVersion, window.VersionPicker,
                window.CheckButton, window.InstallPath, window.BrowseButton,
                window.DepsButton, window.InstallButton, window.StatusTitle
            };
            foreach (var control in required)
            {
                if (control.ActualWidth < 20 || control.ActualHeight < 10)
                    throw new InvalidOperationException($"Layout failed at {width}x{height} for {control.Name}.");
                if (double.IsNaN(control.ActualWidth) || double.IsNaN(control.ActualHeight))
                    throw new InvalidOperationException($"Invalid layout dimensions: {control.Name}.");
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine("ui-snapshots", $"manager-{width}x{height}.png"));
            encoder.Save(stream);
            Console.WriteLine($"PASS: WPF layout {width}x{height}");
        }
        foreach (var (width, height) in new[] { (850, 660), (1050, 780) })
        {
            var library = new LibraryWindow(new ManagerService());
            library.ToolList.SelectedIndex = 0;
            var root = (FrameworkElement)library.Content;
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();

            var controls = new FrameworkElement[]
            {
                library.ToolList, library.ToolTitle,
                library.InstallToolButton, library.StarterKitButton,
                library.ToolDescription, library.PaletteStatus, library.LibraryStatus
            };
            foreach (var control in controls)
            {
                if (control.ActualWidth < 20 || control.ActualHeight < 10 ||
                    double.IsNaN(control.ActualWidth) || double.IsNaN(control.ActualHeight))
                    throw new InvalidOperationException(
                        $"Library layout failed at {width}x{height} for {control.Name}.");
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(
                Path.Combine("ui-snapshots", $"library-{width}x{height}.png"));
            encoder.Save(stream);
            Console.WriteLine($"PASS: Creator's Library layout {width}x{height}");
        }
    }
}