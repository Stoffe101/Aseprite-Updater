using System.IO;
using System.Collections.Concurrent;
using System.Windows.Threading;
using System.Text.Json;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace AsepriteManager;

public partial class MainWindow : Window
{
    private readonly ManagerService _manager = new();
    private ReleaseInfo? _release;
    private IReadOnlyList<ReleaseInfo> _releases = Array.Empty<ReleaseInfo>();
    private DependencyReport? _dependencies;
    private string? _installedVersion;
    private bool _busy;
    private readonly ConcurrentQueue<string> _pendingLog = new();
    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private CancellationTokenSource? _pathDebounce;
    private static string SettingsPath => System.IO.Path.Combine(ManagerService.DataRoot, "settings.json");

    public MainWindow()
    {
        InitializeComponent();
        _manager.Logged += line => _pendingLog.Enqueue(line);
        _logTimer.Tick += (_, _) =>
        {
            if (_pendingLog.IsEmpty) return;
            if (ActivityLog.Text == "Waiting for first action...") ActivityLog.Clear();
            var batch = new System.Text.StringBuilder();
            var processed = 0;
            while (processed++ < 100 && _pendingLog.TryDequeue(out var line))
                batch.AppendLine(line);
            ActivityLog.AppendText(batch.ToString());
            if (ActivityLog.Text.Length > 24000)
                ActivityLog.Text = ActivityLog.Text[^16000..];
            ActivityLog.ScrollToEnd();
        };
        _logTimer.Start();
        InstallPath.Text = LoadSavedInstallPath();
        Loaded += async (_, _) => await RefreshLocalAsync();
        Closing += OnClosing;
        RefreshButtons();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true;
        MessageBox.Show(this, "An operation is running. Wait for it to finish before closing Aseprite Manager.",
            "Installation in progress", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private string TargetPath => InstallPath.Text.Trim().Trim('"');

    private void SetBusy(bool busy)
    {
        _busy = busy;
        InstallPath.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        VersionPicker.IsEnabled = !busy && _releases.Count > 0;
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        CheckButton.IsEnabled = !_busy;
        DepsButton.IsEnabled = !_busy && (_dependencies is null || !_dependencies.Complete);
        LaunchButton.IsEnabled = !_busy && File.Exists(System.IO.Path.Combine(TargetPath, "aseprite.exe"));
        var canInstall = _release != null &&
            (_installedVersion is null || VersionOrder.Compare(_installedVersion, _release.Version) != 0);
        InstallButton.IsEnabled = !_busy && canInstall;
        InstallButton.Content = _installedVersion is null ? "Install selected" :
            (_release != null && _installedVersion != null &&
             VersionOrder.Compare(_installedVersion, _release.Version) > 0 ?
              "Switch to older" : "Switch to selected");
    }

    private async Task RefreshLocalAsync()
    {
        try
        {
            var target = TargetPath;
            _dependencies = await Task.Run(_manager.ScanDependencies);
            DependencySummary.Text = _dependencies.Summary;
            _installedVersion = await _manager.ReadInstalledVersionAsync(target);
            InstalledVersion.Text = _installedVersion is null ? "Not installed" : $"v{_installedVersion}";
            InstalledSubtitle.Text = _installedVersion is null
                ? "Fresh installation available. Choose an installation folder."
                : "Aseprite detected in the selected folder.";
            UpdateSelectedStatus();
        }
        catch (Exception ex)
        {
            StatusTitle.Text = "Local scan issue";
            StatusDetail.Text = ex.Message;
            _manager.Log("SCAN ERROR: " + ex);
        }
        finally { RefreshButtons(); }
    }

    private async void InstallPath_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        _pathDebounce?.Cancel();
        var tokenSource = new CancellationTokenSource();
        _pathDebounce = tokenSource;
        try
        {
            await Task.Delay(350, tokenSource.Token);
            if (!tokenSource.IsCancellationRequested)
            {
                SaveInstallPath(TargetPath);
                await RefreshLocalAsync();
            }
        }
        catch (OperationCanceledException) { }
    }

    private static string LoadSavedInstallPath()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                var stored = json.RootElement.GetProperty("installDir").GetString();
                if (!string.IsNullOrWhiteSpace(stored)) return stored;
            }
        }
        catch { }
        return ManagerService.DefaultInstall;
    }

    private static void SaveInstallPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            Directory.CreateDirectory(ManagerService.DataRoot);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { installDir = path }));
        }
        catch { /* path persistence errors should not block the GUI */ }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select Aseprite installation folder" };
        if (Directory.Exists(TargetPath)) dialog.InitialDirectory = TargetPath;
        if (dialog.ShowDialog(this) == true) InstallPath.Text = dialog.FolderName;
    }

    private IProgress<WorkStatus> ProgressReporter() => new Progress<WorkStatus>(s =>
    {
        StatusTitle.Text = s.Stage;
        StatusDetail.Text = s.Detail;
        WorkProgress.Value = s.Percent;
    });

    private void VersionPicker_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _release = VersionPicker.SelectedItem as ReleaseInfo;
        if (_release is null)
        {
            VersionDetail.Text = "Select a published version.";
            RefreshButtons();
            return;
        }
        var kind = _release.IsLatest ? "Latest stable release" :
            _release.IsPreRelease ? "Preview release" : "Published stable release";
        VersionDetail.Text = $"{kind}  •  {_release.Tag}" +
            (_release.PublishedAt is { } when ? $"  •  Released {when:yyyy-MM-dd}" : "");
        UpdateSelectedStatus();
        RefreshButtons();
    }

    private void UpdateSelectedStatus()
    {
        if (_release == null) return;
        var current = _installedVersion is null ? -1 :
            VersionOrder.Compare(_installedVersion, _release.Version);
        if (current == 0)
        {
            StatusTitle.Text = "Selected version is installed";
            StatusDetail.Text = $"Aseprite {_release.Version} is already installed.";
        }
        else
        {
            StatusTitle.Text = current > 0 ? "Older version selected" :
                _installedVersion is null ? "Ready for first installation" : "Version change available";
            StatusDetail.Text = current > 0
                ? "Switching backward can make sprites saved by newer versions incompatible. Your existing application folder will be backed up."
                : $"Aseprite {_release.Version} is ready to build from official source. Your existing installation will be backed up.";
        }
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        StatusTitle.Text = "Loading official releases";
        StatusDetail.Text = "Retrieving published versions and the latest stable release from GitHub...";
        WorkProgress.Value = 10;
        try
        {
            var previousTag = _release?.Tag;
            _releases = await _manager.GetReleasesAsync();
            VersionPicker.ItemsSource = _releases;
            var latest = _releases.First(r => r.IsLatest);
            LatestVersion.Text = $"v{latest.Version}";
            LatestSubtitle.Text = "Official GitHub stable release";
            VersionPicker.SelectedItem =
                _releases.FirstOrDefault(r => string.Equals(r.Tag, previousTag,
                    StringComparison.OrdinalIgnoreCase)) ?? latest;
            WorkProgress.Value = 100;
            await RefreshLocalAsync();
        }
        catch (Exception ex)
        {
            StatusTitle.Text = "Couldn't load releases";
            StatusDetail.Text = ex.Message;
            _manager.Log("GITHUB RELEASE ERROR: " + ex);
        }
        finally { SetBusy(false); }
    }

    private async void Dependencies_Click(object sender, RoutedEventArgs e)
    {
        var response = MessageBox.Show(this,
            "Aseprite source builds require Git, CMake, Ninja and Visual Studio 2022 C++ Build Tools with the Windows SDK.\n\n" +
            "This may download SEVERAL GIGABYTES and Windows may request administrator approval. " +
            "Only missing tools will be requested. Continue?",
            "Install build prerequisites", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (response != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            _dependencies ??= await Task.Run(_manager.ScanDependencies);
            await _manager.InstallMissingToolsAsync(_dependencies, ProgressReporter(), CancellationToken.None);
            await RefreshLocalAsync();
            if (_dependencies?.Complete != true)
                throw new InvalidOperationException("Some build tools are still missing. " +
                    "Open the activity log, or restart Windows if a tool installer requested it.");
            StatusTitle.Text = "Prerequisites ready";
            StatusDetail.Text = "Everything needed for a Windows source build has been detected.";
        }
        catch (Exception ex)
        {
            StatusTitle.Text = "Prerequisite installation issue";
            StatusDetail.Text = ex.Message;
            _manager.Log("PREREQUISITES ERROR: " + ex);
            MessageBox.Show(this, ex.Message, "Prerequisites", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_release == null) return;
        var target = TargetPath;
        if (MessageBox.Show(this,
            $"Build and install Aseprite {_release.Version} into:\n\n{target}\n\n" +
            "The previous application folder will be retained IN FULL as a dated backup. " +
            "Existing sprite projects in your Documents, Pictures and other personal folders will not be touched. " +
            "Local custom themes/extensions and nearby sprite files are carried forward when possible.\n\n" +
            "OLDER VERSIONS MAY NOT OPEN FILES SAVED WITH NEWER ASEPRITE VERSIONS.\n\n" +
            "Compiling can take a long time and several GB of disk space. Continue?",
            "Confirm source build", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            var selected = _release;
            var progress = ProgressReporter();
            await Task.Run(() => _manager.InstallOrUpdateAsync(target, selected, progress, CancellationToken.None));
            await RefreshLocalAsync();
            MessageBox.Show(this, $"Aseprite {_release.Version} was installed successfully.\n\nFolder: {target}",
                "Installation complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusTitle.Text = "Install / update failed";
            StatusDetail.Text = ex.Message + " Your previous installation is retained if the new build did not pass.";
            _manager.Log("WORKER ERROR: " + ex);
            MessageBox.Show(this, ex.Message + "\n\nSee Open log for diagnostic output.",
                "Installation failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        var target = TargetPath;
        var exe = System.IO.Path.Combine(target, "aseprite.exe");
        try
        {
            if (!File.Exists(exe)) throw new FileNotFoundException("aseprite.exe not found", exe);
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = target, UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Launch failed"); }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ManagerService.DataRoot);
            if (!File.Exists(ManagerService.LogPath)) File.WriteAllText(ManagerService.LogPath, "");
            var psi = new ProcessStartInfo("notepad.exe") { UseShellExecute = true };
            psi.ArgumentList.Add(ManagerService.LogPath);
            Process.Start(psi);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't open log"); }
    }
}
