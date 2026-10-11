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
    }
}
