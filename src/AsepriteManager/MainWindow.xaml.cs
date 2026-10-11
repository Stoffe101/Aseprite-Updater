using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace AsepriteManager;

public partial class MainWindow : Window
{
    private readonly ManagerService _manager = new();
    private ReleaseInfo? _release;
    private DependencyReport? _dependencies;
    private string? _installedVersion;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _manager.Logged += line => Dispatcher.BeginInvoke(() =>
        {
            if (ActivityLog.Text == "Waiting for first action...") ActivityLog.Clear();
            ActivityLog.AppendText(line + Environment.NewLine);
            // Keep the UI fast even during thousands of compiler messages.
            if (ActivityLog.Text.Length > 24000)
                ActivityLog.Text = ActivityLog.Text[^16000..];
            ActivityLog.ScrollToEnd();
        });
        InstallPath.Text = ManagerService.DefaultInstall;
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
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        CheckButton.IsEnabled = !_busy;
        DepsButton.IsEnabled = !_busy && (_dependencies is null || !_dependencies.Complete);
        LaunchButton.IsEnabled = !_busy && File.Exists(System.IO.Path.Combine(TargetPath, "aseprite.exe"));
        var canInstall = _release != null &&
            (_installedVersion is null || VersionOrder.Compare(_installedVersion, _release.Version) < 0);
        InstallButton.IsEnabled = !_busy && canInstall;
        InstallButton.Content = _installedVersion is null ? "Install Aseprite" : "Update now";
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
            if (_release != null)
            {
                var current = _installedVersion is null ? -1 : VersionOrder.Compare(_installedVersion, _release.Version);
                StatusTitle.Text = current >= 0 ? "Up to date" : current < 0 ? "Ready to install" : "Ready";
                StatusDetail.Text = current >= 0
                    ? "This installation already matches the latest stable release."
                    : $"Aseprite {_release.Version} is available for this device.";
            }
        }
        catch (Exception ex)
        {
            StatusTitle.Text = "Local scan issue";
            StatusDetail.Text = ex.Message;
            _manager.Log("SCAN ERROR: " + ex);
        }
        finally { RefreshButtons(); }
    }

    private void InstallPath_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        _ = RefreshLocalAsync();
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

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        StatusTitle.Text = "Checking GitHub";
        StatusDetail.Text = "Contacting the official Aseprite stable release API...";
        WorkProgress.Value = 10;
        try
        {
            _release = await _manager.GetLatestAsync();
            LatestVersion.Text = $"v{_release.Version}";
            LatestSubtitle.Text = "Official stable release • github.com/aseprite/aseprite";
            WorkProgress.Value = 100;
            await RefreshLocalAsync();
        }
        catch (Exception ex)
        {
            StatusTitle.Text = "Check failed";
            StatusDetail.Text = ex.Message;
            _manager.Log("CHECK ERROR: " + ex);
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
            "A fresh compile can take a long time and requires several GB of free disk space. " +
            "The current installed version (if any) will be kept as a backup. Continue?",
            "Confirm source build", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            await _manager.InstallOrUpdateAsync(target, _release, ProgressReporter(), CancellationToken.None);
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
