using System.IO;
using System.Collections.Concurrent;
using System.Windows.Threading;
using System.Text.Json;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace AsepriteManager;

public partial class MainWindow : Window
{
    private readonly ManagerService _manager = new();
    private readonly ThemeManager _themeManager;
    private IReadOnlyDictionary<string, bool> _availableThemeUpdates = new Dictionary<string, bool>();
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
        _themeManager = new ThemeManager(_manager.Log);
        ThemePicker.ItemsSource = ThemeCatalog.Curated;
        ThemePicker.SelectedIndex = 0;
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
        ThemePicker.IsEnabled = !busy;
        InstallThemeButton.IsEnabled = !busy;
        RemoveThemeButton.IsEnabled = !busy && ThemePicker.SelectedItem is ThemeDefinition selected && _themeManager.IsInstalled(selected);
        InstallRecommendedButton.IsEnabled = !busy;
        CheckThemesButton.IsEnabled = !busy;
        UpdateThemesButton.IsEnabled = !busy && ThemeCatalog.Curated.Any(_themeManager.IsInstalled);
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        CheckButton.IsEnabled = !_busy;
        var stable = _releases.FirstOrDefault(r => r.IsLatest);
        var updateAvailable = stable != null && _installedVersion != null &&
            VersionOrder.Compare(_installedVersion, stable.Version) < 0;
        UpdateAsepriteButton.IsEnabled = !_busy && updateAvailable;
        AsepriteUpdateHint.Text = stable == null ? "Check GitHub to compare versions." :
            _installedVersion == null ? "Aseprite isn't installed yet; choose a release to install." :
            updateAvailable ? $"v{stable.Version} is available. Your current build will be backed up." :
            "Your Aseprite is current, or you have a newer preview build.";
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
            ShowInstalledIcon(target);
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
            var cached = AsepriteBuildCache.HasBuild(_release.Tag, _release.Version) ||
                AsepriteBuildCache.HasPreviousInstallBackup(TargetPath, _release.Tag, _release.Version);
            StatusDetail.Text = cached
                ? $"A local Aseprite {_release.Version} build is cached. Manager will verify its files before switching; the current installation will be backed up."
                : current > 0
                    ? "Older versions may not open sprites saved by newer versions. The first build may take time; successful builds will be cached for future switches."
                    : $"Aseprite {_release.Version} can be built from official source. Repeated switches can reuse verified local builds.";
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

    private void ShowInstalledIcon(string installDir)
    {
        var candidates = new[]
        {
            Path.Combine(installDir, "aseprite.ico"),
            Path.Combine(installDir, "data", "icons", "ase.ico")
        };
        var iconPath = candidates.FirstOrDefault(File.Exists);
        if (iconPath == null) return;
        try
        {
            var bitmap = BitmapFrame.Create(new Uri(iconPath, UriKind.Absolute),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            OfficialLogo.Source = bitmap;
            FallbackLogo.Visibility = Visibility.Collapsed;
            Icon = bitmap;
        }
        catch (Exception ex) { _manager.Log("Could not load Aseprite's original icon: " + ex.Message); }
    }

    private void UpdateAseprite_Click(object sender, RoutedEventArgs e)
    {
        var stable = _releases.FirstOrDefault(r => r.IsLatest);
        if (stable == null) return;
        VersionPicker.SelectedItem = stable;
        Install_Click(sender, e);
    }

    private void ThemePicker_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateThemeDetail();
    }

    private void UpdateThemeDetail()
    {
        if (ThemePicker.SelectedItem is not ThemeDefinition theme) return;
        var installed = _themeManager.IsInstalled(theme);
        var update = _availableThemeUpdates.TryGetValue(theme.Id, out var available) && available;
        ThemeDetail.Text = $"{theme.Description}  •  {theme.Website}" +
            (update ? "  •  UPDATE AVAILABLE" : installed ? "  •  Installed" : "  •  Not installed");
        InstallThemeButton.Content = installed ? (update ? "Update selected theme" : "Reinstall selected theme") :
            "Install selected theme";
        RemoveThemeButton.IsEnabled = !_busy && installed;
    }

    private void RemoveTheme_Click(object sender, RoutedEventArgs e)
    {
        if (ThemePicker.SelectedItem is not ThemeDefinition theme) return;
        if (MessageBox.Show(this,
            $"Uninstall your managed {theme.Name} theme?\n\n" +
            "Custom changes are protected and the original folder is backed up. " +
            "Aseprite's built-in themes and your sprites are untouched. Close Aseprite first.",
            "Uninstall selected theme", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            _themeManager.RemoveTheme(theme);
            StatusTitle.Text = "Theme uninstalled";
            StatusDetail.Text = theme.Name + " removed. Backup retained.";
        }
        catch (Exception ex)
        {
            StatusTitle.Text = "Theme uninstall blocked";
            StatusDetail.Text = ex.Message;
            _manager.Log("THEME UNINSTALL ERROR: " + ex);
        }
        UpdateThemeDetail();
    }

    private async void InstallTheme_Click(object sender, RoutedEventArgs e)
    {
        if (ThemePicker.SelectedItem is not ThemeDefinition theme) return;
        if (MessageBox.Show(this,
            $"Download {theme.Name} from:\n{theme.Website}\n\n" +
            "The extension will be installed in your Aseprite user configuration folder. " +
            "Your sprites and projects will not be touched. Aseprite must be closed. Continue?",
            "Install community theme", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            StatusTitle.Text = "Installing theme";
            StatusDetail.Text = $"Downloading and verifying {theme.Name}...";
            WorkProgress.Value = 15;
            await Task.Run(() => _themeManager.InstallOrUpdateAsync(theme, CancellationToken.None));
            _availableThemeUpdates = new Dictionary<string, bool>();
            UpdateThemeDetail();
            StatusTitle.Text = "Theme installed";
            StatusDetail.Text = $"Installed {theme.Name}. Select it inside Aseprite → Edit → Preferences → Theme.";
            WorkProgress.Value = 100;
        }
        catch (Exception ex)
        {
            StatusTitle.Text = "Theme installation failed";
            StatusDetail.Text = ex.Message;
            _manager.Log("THEME ERROR: " + ex);
            MessageBox.Show(this, ex.Message, "Theme installation failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); UpdateThemeDetail(); }
    }

    private async void InstallRecommended_Click(object sender, RoutedEventArgs e)
    {
        var recommended = ThemeCatalog.Curated.Where(x =>
            x.Id is "cat-mocha" or "dracula" or "monaki").ToArray();
        if (MessageBox.Show(this,
            "Install these community themes?\n\n" +
            "• Catppuccin Mocha\n• Dracula\n• Monaki\n\n" +
            "All downloads come from their maintainers' GitHub repositories. " +
            "No sprites or user projects are modified. Close Aseprite first.",
            "Install recommended themes", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        SetBusy(true);
        var failed = new List<string>();
        try
        {
            for (int i = 0; i < recommended.Length; i++)
            {
                var theme = recommended[i];
                StatusTitle.Text = "Installing recommended themes";
                StatusDetail.Text = $"{i + 1}/{recommended.Length}: {theme.Name}";
                WorkProgress.Value = i * 100.0 / recommended.Length;
                try { await Task.Run(() => _themeManager.InstallOrUpdateAsync(theme, CancellationToken.None)); }
                catch (Exception ex)
                {
                    failed.Add(theme.Name + ": " + ex.Message);
                    _manager.Log("RECOMMENDED THEME ERROR: " + theme.Name + " " + ex);
                }
            }
            _availableThemeUpdates = new Dictionary<string, bool>();
            StatusTitle.Text = failed.Count == 0 ? "Recommended themes installed" : "Some themes were skipped";
            StatusDetail.Text = failed.Count == 0
                ? "Open Aseprite → Edit → Preferences → Theme to choose your new look."
                : string.Join(" | ", failed);
            WorkProgress.Value = 100;
        }
        finally { SetBusy(false); UpdateThemeDetail(); }
    }

    private async void CheckThemeUpdates_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            StatusTitle.Text = "Checking theme repositories";
            StatusDetail.Text = "Comparing installed managed themes with their official GitHub sources...";
            WorkProgress.Value = 20;
            _availableThemeUpdates = await _themeManager.CheckUpdatesAsync(CancellationToken.None);
            var count = _availableThemeUpdates.Values.Count(x => x);
            StatusTitle.Text = count == 0 ? "Themes are up to date" : $"{count} theme update(s) available";
            StatusDetail.Text = count == 0 ? "Your installed managed themes are current." :
                "Use Update installed themes to apply available updates; local edits will be protected.";
            WorkProgress.Value = 100;
        }
        catch (Exception ex)
        {
            StatusTitle.Text = "Theme update check failed";
            StatusDetail.Text = ex.Message;
            _manager.Log("THEME UPDATE ERROR: " + ex);
        }
        finally { SetBusy(false); UpdateThemeDetail(); }
    }

    private async void UpdateThemes_Click(object sender, RoutedEventArgs e)
    {
        var installed = ThemeCatalog.Curated.Where(_themeManager.IsInstalled).ToArray();
        if (installed.Length == 0) return;
        if (MessageBox.Show(this, $"Check and update {installed.Length} installed managed themes?\n\n" +
            "Updated themes are backed up. Locally edited themes will not be replaced. " +
            "Close Aseprite before continuing.", "Update installed themes", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SetBusy(true);
        var failures = new List<string>();
        try
        {
            for (int i = 0; i < installed.Length; i++)
            {
                var theme = installed[i];
                StatusTitle.Text = "Updating theme library";
                StatusDetail.Text = $"Checking {theme.Name} ({i + 1}/{installed.Length})...";
                WorkProgress.Value = (i * 100.0) / installed.Length;
                try { await Task.Run(() => _themeManager.InstallOrUpdateAsync(theme, CancellationToken.None)); }
                catch (Exception ex)
                {
                    failures.Add(theme.Name + ": " + ex.Message);
                    _manager.Log($"Theme update skipped for {theme.Name}: {ex}");
                }
            }
            _availableThemeUpdates = new Dictionary<string, bool>();
            StatusTitle.Text = failures.Count == 0 ? "Theme updates finished" : "Some themes need attention";
            StatusDetail.Text = failures.Count == 0 ? "All managed themes were checked." :
                string.Join(" | ", failures);
            WorkProgress.Value = 100;
        }
        finally { SetBusy(false); UpdateThemeDetail(); }
    }

    private void OpenThemesFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ThemeManager.ExtensionsDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ThemeManager.ExtensionsDir}\"") {
            UseShellExecute = true
        });
    }

    private async void RepairIcon_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            await _manager.RepairAsepriteShortcutAsync(TargetPath);
            ShowInstalledIcon(TargetPath);
            MessageBox.Show(this, "Aseprite's Start Menu shortcut has been updated.", "Icon repaired",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _manager.Log("ICON REPAIR ERROR: " + ex);
            MessageBox.Show(this, ex.Message, "Icon repair failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private void OpenBuildPerformance_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        new BuildCacheWindow(_manager) { Owner = this }.ShowDialog();
        UpdateSelectedStatus();
    }

    private void OpenControlCenter_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        new ControlCenterWindow(this, _manager, TargetPath) { Owner = this }.ShowDialog();
    }

    public void FocusAsepriteUpdates()
    {
        VersionPicker.BringIntoView();
        if (!_busy) Check_Click(this, new RoutedEventArgs());
    }

    private void OpenLibrary_Click(object sender, RoutedEventArgs e)
    {
        var window = new LibraryWindow(_manager) { Owner = this };
        window.ShowDialog();
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
