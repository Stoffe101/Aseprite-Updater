using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace AsepriteManager;

public partial class BuildCacheWindow : System.Windows.Controls.UserControl
{
    private readonly ManagerService _service;
    private bool _busy;
    public bool IsBusy => _busy;
    public BuildCacheWindow(ManagerService service)
    {
        _service = service;
        InitializeComponent();
        var options = BuildPerformance.Read();
        UseSccache.IsChecked = options.UseCompilerCache;
        foreach (ComboBoxItem item in ParallelJobs.Items)
            if (int.TryParse(item.Tag?.ToString(), out var jobs) && jobs == options.ParallelJobs)
            { ParallelJobs.SelectedItem = item; break; }
        if (ParallelJobs.SelectedItem == null) ParallelJobs.SelectedIndex = 0;
        ShowCompilerStatus();
        Loaded += async (_, _) => await RefreshAsync();

    }
    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var button in FindVisualButtons(this))
            button.IsEnabled = !busy;
        ParallelJobs.IsEnabled = !busy;
        UseSccache.IsEnabled = !busy;
    }
    private static IEnumerable<Button> FindVisualButtons(System.Windows.DependencyObject root)
    {
        for (int n = 0; n < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); n++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, n);
            if (child is Button button) yield return button;
            foreach (var descendant in FindVisualButtons(child)) yield return descendant;
        }
    }
    private void ShowCompilerStatus()
    {
        var executable = BuildPerformance.FindCompilerCache();
        SccacheStatus.Text = executable == null
            ? "sccache is not installed. Normal Ninja/MSVC compilation will still work."
            : "Detected compiler cache executable: " + executable;
    }
    private async Task RefreshAsync()
    {
        SetBusy(true);
        try
        {
            var result = await Task.Run(() =>
            {
                var builds = AsepriteBuildCache.Statistics();
                var skia = SharedSkiaCache.Statistics();
                var compiler = BuildPerformance.CompilerCacheFolder;
                long bytes = 0;
                if (Directory.Exists(compiler))
                    bytes = Directory.EnumerateFiles(compiler, "*", SearchOption.AllDirectories)
                        .Sum(path => new FileInfo(path).Length);
                return (builds, skia, bytes);
            });
            CacheSummary.Text =
                $"Compiled versions: {result.builds.Versions} • {Format(result.builds.Bytes)}\n" +
                $"Shared Skia dependencies: {result.skia.Folders} • {Format(result.skia.Bytes)}\n" +
                $"Compiler object cache: {Format(result.bytes)}\n" +
                $"Ninja auto setting: {BuildPerformance.EffectiveJobs(new BuildPerformance.Options())} parallel jobs.";
            ShowCompilerStatus();
        }
        catch (Exception ex) { CacheSummary.Text = "Cache scan failed: " + ex.Message; }
        finally { SetBusy(false); }
    }
    private static string Format(long bytes) => bytes < 1024 * 1024
        ? $"{bytes / 1024.0:F1} KiB"
        : bytes < 1024L * 1024 * 1024
            ? $"{bytes / 1024.0 / 1024:F1} MiB"
            : $"{bytes / 1024.0 / 1024 / 1024:F2} GiB";

    private async void RefreshCache_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync();

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var combo = (ComboBoxItem)ParallelJobs.SelectedItem;
            var jobs = int.Parse(combo.Tag?.ToString() ?? "0");
            var settings = new BuildPerformance.Options(UseSccache.IsChecked == true, jobs);
            BuildPerformance.Save(settings);
            OperationStatus.Text = "Saved. New settings apply to the next Aseprite source build.";
        }
        catch (Exception ex) { OperationStatus.Text = "Save failed: " + ex.Message; }
    }

    private async Task CleanAsync(string title, Action work)
    {
        if (MessageBox.Show(Window.GetWindow(this) ?? Application.Current.MainWindow,
            $"Clear {title}?\n\nThis cannot be undone. Your installed Aseprite and previous-install backups are NOT deleted. " +
            "A future build may require downloading or recompiling again.",
            "Confirm cache cleanup", MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            OperationStatus.Text = "Cleaning " + title + "...";
            await Task.Run(() =>
            {
                using var lease = BuildCacheLease.Acquire();
                work();
            });
            OperationStatus.Text = title + " cleared.";
        }
        catch (Exception ex) { OperationStatus.Text = "Cleanup was blocked: " + ex.Message; }
        finally { SetBusy(false); }
        await RefreshAsync();
    }
    private async void ClearBuilds_Click(object sender, RoutedEventArgs e) =>
        await CleanAsync("cached compiled Aseprite versions",
            () => AsepriteBuildCache.Clear(_service.Log));
    private async void ClearSkia_Click(object sender, RoutedEventArgs e) =>
        await CleanAsync("shared Skia dependencies",
            () => SharedSkiaCache.Clear(_service.Log));
    private async void ClearCompiler_Click(object sender, RoutedEventArgs e) =>
        await CleanAsync("sccache compiler objects", () =>
        {
            var sccache = BuildPerformance.FindCompilerCache();
            if (sccache != null)
            {
                using var process = Process.Start(new ProcessStartInfo(sccache)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { "--stop-server" }
                });
                process?.WaitForExit(20000);
            }
            var dir = BuildPerformance.CompilerCacheFolder;
            if (Directory.Exists(dir))
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Refusing to delete linked compiler cache.");
                Directory.Delete(dir, recursive: true);
            }
            _service.Log("Cleared Manager-owned compiler cache.");
        });

    private async void InstallSccache_Click(object sender, RoutedEventArgs e)
    {
        if (BuildPerformance.FindCompilerCache() != null)
        {
            OperationStatus.Text = "sccache is already installed.";
            return;
        }
        if (MessageBox.Show(Window.GetWindow(this) ?? Application.Current.MainWindow,
            "Install Mozilla sccache using the official Windows Package Manager (winget)?\n\n" +
            "This is a third-party compiler cache, not an Aseprite plugin. It can help speed up repeated builds, " +
            "but depends on the version of MSVC and CMake used. Windows may request approval.",
            "Install optional compiler cache", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            OperationStatus.Text = "Installing Mozilla.sccache from the winget catalog...";
            var outcome = await Task.Run(async () =>
            {
                var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                var winget = path.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => Path.Combine(p.Trim('"'), "winget.exe"))
                    .Concat(new[] { Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                        @"Microsoft\WindowsApps\winget.exe") })
                    .FirstOrDefault(File.Exists)
                    ?? throw new FileNotFoundException("Windows Package Manager (winget) is not installed.");
                var psi = new ProcessStartInfo(winget)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                foreach (var arg in new[] { "install", "--exact", "--id", "Mozilla.sccache",
                    "--source", "winget", "--accept-package-agreements", "--accept-source-agreements",
                    "--disable-interactivity" })
                    psi.ArgumentList.Add(arg);
                using var proc = Process.Start(psi) ??
                    throw new IOException("Could not start winget.");
                var stdout = proc.StandardOutput.ReadToEndAsync();
                var stderr = proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();
                return (proc.ExitCode, (await stdout) + "\n" + (await stderr));
            });
            if (outcome.ExitCode != 0)
                throw new InvalidOperationException("winget returned " + outcome.ExitCode +
                    ". Output: " + outcome.Item2[^Math.Min(700, outcome.Item2.Length)..]);
            OperationStatus.Text = "sccache installation completed. You may need to reopen Manager for PATH changes to be visible.";
            _service.Log("Installed optional Mozilla.sccache via winget.");
            ShowCompilerStatus();
        }
        catch (Exception ex)
        {
            OperationStatus.Text = "sccache installation failed: " + ex.Message;
            _service.Log("SCCACHE INSTALL ERROR: " + ex);
        }
        finally { SetBusy(false); }
    }
}
