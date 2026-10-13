using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AsepriteManager;

public partial class ControlCenterWindow : System.Windows.Controls.UserControl
{
    private readonly MainWindow _main;
    private readonly ManagerService _service;
    private readonly string _installFolder;
    private string? _restoreZip;
    private bool _busy;
    public bool IsBusy => _busy;

    public ControlCenterWindow(MainWindow main, ManagerService service, string installFolder)
    {
        _main = main;
        _service = service;
        _installFolder = installFolder;
        InitializeComponent();

        ReloadProfiles();
    }


    private void SetBusy(bool busy)
    {
        _busy = busy;
        CheckAllButton.IsEnabled = !busy;
        UpdatesTab.IsEnabled = !busy;
        RecoveryTab.IsEnabled = !busy;
        ProfilesTab.IsEnabled = !busy;
        CreateBackupButton.IsEnabled = !busy;
        SelectRestoreButton.IsEnabled = !busy;
        RestoreButton.IsEnabled = !busy && _restoreZip != null;
        SaveProfileButton.IsEnabled = !busy;
    }

    private void ShowStatus(string text)
    {
        HubStatus.Text = text;
        _service.Log("CONTROL CENTER: " + text);
    }

    private void Navigate_Click(object sender, RoutedEventArgs e) =>
        ShowPage((sender as Button)?.Tag as string ?? "updates");

    public void ShowPage(string page)
    {
        PageHeading.Text = page switch
        {
            "updates" => "Updates across your toolkit",
            "recovery" => "Configuration recovery",
            "profiles" => "Project Profiles",
            _ => "Your creative setup"
        };
        PageSummary.Text = page switch
        {
            "updates" => "Check Aseprite, themes, tools and integrations in one place.",
            "recovery" => "Back up and restore safely without disturbing your artwork.",
            "profiles" => "Keep a portable inventory of your preferred tools and palettes.",
            _ => "Manage your Aseprite workspace."
        };
        UpdatesPage.Visibility = page == "updates" ? Visibility.Visible : Visibility.Collapsed;
        RecoveryPage.Visibility = page == "recovery" ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPage.Visibility = page == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        var selected = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(105, 84, 184));
        var normal = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(51, 67, 95));
        UpdatesTab.Background = page == "updates" ? selected : normal;
        RecoveryTab.Background = page == "recovery" ? selected : normal;
        ProfilesTab.Background = page == "profiles" ? selected : normal;
        PageScroller.ScrollToTop();
    }

    private void ReviewAseprite_Click(object sender, RoutedEventArgs e)
    {
        _main.NavigateTo("aseprite");
        _main.FocusAsepriteUpdates();
    }

    private void OpenLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _main.NavigateTo("tools");
    }

    private void OpenManagerReleases_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(
            "https://github.com/Stoffe101/Aseprite-Updater/releases") { UseShellExecute = true });
    }

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(25) };
    static ControlCenterWindow()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("AsepriteManager/0.6.0");
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    private async Task<string> CheckManagerReleaseAsync()
    {
        using var response = await GitHubApi.GetAsync(Client,
            "https://api.github.com/repos/Stoffe101/Aseprite-Updater/releases/latest",
            CancellationToken.None);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return "No GitHub release has been published yet. Use verified CI installers until releases are available.";
        response.EnsureSuccessStatusCode();
        using var data = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var tag = data.RootElement.GetProperty("tag_name").GetString() ?? "";
        var current = typeof(ControlCenterWindow).Assembly.GetName().Version ?? new Version(0, 0);
        var value = tag.TrimStart('v', 'V');
        var dash = value.IndexOf('-');
        if (dash >= 0) value = value[..dash];
        if (!Version.TryParse(value, out var latest))
            return "Published Manager release has an unrecognized version tag: " + tag;
        return latest > current
            ? $"Manager {tag} is published (installed: {current}). Open official releases to review the installer."
            : $"Manager {current} is current relative to latest release {tag}.";
    }

    private async void CheckAll_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        int checkedCount = 0, failed = 0;
        async Task UpdateOne(TextBlock output, string label, Func<Task<string>> getStatus)
        {
            output.Text = "Checking...";
            ShowStatus("Checking " + label + "...");
            try
            {
                output.Text = await getStatus();
                checkedCount++;
            }
            catch (Exception ex)
            {
                output.Text = "Could not check: " + ex.Message;
                failed++;
                _service.Log("UPDATE CHECK " + label + ": " + ex);
            }
        }
        try
        {
            await UpdateOne(AsepriteUpdateStatus, "Aseprite", async () =>
            {
                var releases = await _service.GetReleasesAsync();
                var latest = releases.First(r => r.IsLatest);
                var current = await _service.ReadInstalledVersionAsync(_installFolder);
                if (current == null) return $"Not installed in configured folder. Latest stable: v{latest.Version}.";
                return VersionOrder.Compare(current, latest.Version) < 0
                    ? $"Update available: installed v{current}, latest stable v{latest.Version}. Select it on the main screen to build."
                    : $"Installed v{current}; no newer stable release than v{latest.Version}.";
            });
            await UpdateOne(ManagerUpdateStatus, "Manager releases", CheckManagerReleaseAsync);
            await UpdateOne(ThemeUpdateStatus, "themes", async () =>
            {
                var manager = new ThemeManager(_service.Log);
                var installed = ThemeCatalog.Curated.Count(manager.IsInstalled);
                if (installed == 0) return "No Manager-installed themes.";
                var changes = await manager.CheckUpdatesAsync(CancellationToken.None);
                return $"{installed} installed • {changes.Values.Count(x => x)} newer versions detected. Review in main window.";
            });
            await UpdateOne(ToolUpdateStatus, "Lua tools", async () =>
            {
                var installed = WorkshopLibrary.Tools.Where(WorkshopLibrary.Installed).ToArray();
                if (installed.Length == 0) return "No Manager-installed scripts.";
                var latest = await WorkshopLibrary.LatestToolRevision(CancellationToken.None);
                return $"{installed.Length} installed • {installed.Count(t => WorkshopLibrary.Revision(t) != latest)} upstream repository revisions differ. Review source before updating.";
            });
            await UpdateOne(PaletteUpdateStatus, "palettes", async () =>
            {
                var installed = PaletteGallery.Presets.Where(PaletteGallery.IsInstalled).ToArray();
                if (installed.Length == 0) return "No optional managed palette copies.";
                int updated = 0;
                foreach (var preset in installed)
                {
                    var (version, _) = await PaletteGallery.DownloadAsync(preset);
                    if (version != PaletteGallery.InstalledRevision(preset)) updated++;
                }
                return $"{installed.Length} managed copies • {updated} changes detected. Review in Creator's Library.";
            });
            await UpdateOne(OverlayUpdateStatus, "Tile Index", async () =>
            {
                if (!TileIndexOverlayManager.Installed) return "Not installed.";
                var current = TileIndexOverlayManager.Revision();
                var latest = await TileIndexOverlayManager.LatestSha(CancellationToken.None);
                return current == latest ? "Installed • current upstream revision." :
                    "New upstream revision detected. Review source and update in Creator's Library.";
            });
            await UpdateOne(WizardUpdateStatus, "Godot Wizard", async () =>
            {
                var profile = SavedProfiles.SelectedItem as ProjectProfiles.Profile ??
                    ProjectProfiles.ReadAll().FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.ProjectFolder));
                if (profile?.ProjectFolder == null)
                    return "Save a profile with a Godot project path to enable Wizard checks.";
                var state = GodotWizardManager.Inspect(profile.ProjectFolder);
                if (!state.Installed) return "Not installed for project " + profile.Name;
                if (!state.Managed || state.Modified) return "Plugin is unmanaged or customized. Automatic replacement is blocked.";
                var latest = await GodotWizardManager.LatestCommitAsync(CancellationToken.None);
                return state.Revision == latest ? "Installed and current for " + profile.Name :
                    "Update available for " + profile.Name + ". Review in Creator's Library.";
            });
            UpdateSummary.Text = $"{checkedCount} checks completed; {failed} unavailable. No files were changed.";
            ShowStatus("Update review complete. Choose an individual resource to apply updates safely.");
        }
        finally { SetBusy(false); }
    }

    private async void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        var save = new SaveFileDialog
        {
            Title = "Export Aseprite profile",
            Filter = "ZIP archive (*.zip)|*.zip",
            FileName = "Aseprite-profile-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip",
            DefaultExt = ".zip"
        };
        if (save.ShowDialog(Window.GetWindow(this)) != true) return;
        SetBusy(true);
        try
        {
            if (Process.GetProcessesByName("aseprite").Length > 0)
                throw new InvalidOperationException("Close Aseprite before creating a consistent backup.");
            var count = await Task.Run(() =>
                ProfileBackupService.CreateBackup(ProfileBackupService.AsepriteProfile, save.FileName));
            RecoveryDetails.Text = $"Backup created at {save.FileName} ({count} files).";
            ShowStatus("Full configuration backup ZIP verified. The original profile was not changed.");
        }
        catch (Exception ex)
        {
            RecoveryDetails.Text = "Backup failed: " + ex.Message;
            _service.Log("PROFILE BACKUP: " + ex);
        }
        finally { SetBusy(false); }
    }

    private void ChooseRecovery_Click(object sender, RoutedEventArgs e)
    {
        var open = new OpenFileDialog
        {
            Title = "Choose an Aseprite profile backup ZIP",
            Filter = "ZIP archive (*.zip)|*.zip", CheckFileExists = true
        };
        if (open.ShowDialog(Window.GetWindow(this)) != true) return;
        _restoreZip = null;
        RestoreButton.IsEnabled = false;
        try
        {
            var preview = ProfileRecoveryService.Inspect(open.FileName);
            _restoreZip = open.FileName;
            RecoveryDetails.Text = $"ZIP: {open.FileName}\n" +
                $"{preview.FileCount} files; {preview.ExpandedBytes / 1024.0 / 1024.0:F1} MiB unpacked.\n" +
                $"Settings: {(preview.ContainsSettings ? "yes" : "no")} • " +
                $"Saved brushes: {(preview.ContainsBrushes ? "yes" : "no")} • " +
                $"Extensions: {(preview.ContainsExtensions ? "yes" : "no")}.\n" +
                "Inspect the source of this backup: restored scripts and plugins can execute code.";
            RestoreButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            RecoveryDetails.Text = "Rejected backup: " + ex.Message;
            _service.Log("PROFILE ZIP REJECTED: " + ex);
        }
    }

    private async void RestoreProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_restoreZip == null) return;
        var selectedZip = _restoreZip;
        var answer = MessageBox.Show(Window.GetWindow(this) ?? Application.Current.MainWindow,
            "REPLACE THE ACTIVE ASEPRITE CONFIGURATION?\n\n" +
            "ZIP: " + selectedZip + "\n\n" +
            "The ZIP has been checked, but restore will replace the complete active configuration " +
            "including extensions, saved brushes, scripts and settings.\n\n" +
            "The previous profile will be kept as an independent folder. Your artwork " +
            "outside Aseprite's configuration directory will not be touched.\n\n" +
            "Close Aseprite before continuing. Only restore archives you trust.",
            "Confirm complete profile restore", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            ShowStatus("Validating and restoring the selected profile...");
            var backup = await Task.Run(() =>
                ProfileRecoveryService.Restore(selectedZip, ProfileBackupService.AsepriteProfile, _service.Log));
            RecoveryDetails.Text = "Restore complete. " +
                (backup.Length == 0 ? "No prior configuration existed." : "Entire previous profile retained at " + backup);
            ShowStatus("Profile restored. Restart Aseprite and verify your settings.");
            _restoreZip = null;
        }
        catch (Exception ex)
        {
            RecoveryDetails.Text = "Restore failed: " + ex.Message +
                "\nIf an existing profile was moved, the recovery routine attempted to restore it.";
            _service.Log("PROFILE RESTORE: " + ex);
        }
        finally { SetBusy(false); }
    }

    private void ReloadProfiles()
    {
        SavedProfiles.ItemsSource = ProjectProfiles.ReadAll();
        if (SavedProfiles.Items.Count != 0 && SavedProfiles.SelectedItem == null)
            SavedProfiles.SelectedIndex = 0;
    }

    private void BrowseProfileProject_Click(object sender, RoutedEventArgs e)
    {
        var select = new OpenFolderDialog { Title = "Choose a Godot project folder (optional)" };
        if (select.ShowDialog(Window.GetWindow(this)) == true) ProjectFolderText.Text = select.FolderName;
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var item = ProjectProfiles.Capture(ProfileName.Text,
                string.IsNullOrWhiteSpace(ProjectFolderText.Text) ? null : ProjectFolderText.Text.Trim(),
                ProfileNotes.Text);
            var file = ProjectProfiles.Save(item);
            ReloadProfiles();
            SavedProfiles.SelectedItem = ProjectProfiles.ReadAll()
                .FirstOrDefault(p => p.Name == item.Name);
            ShowStatus($"Saved {item.Name}: {item.ResourceIds.Length} preferred resources • {file}");
        }
        catch (Exception ex) { ShowStatus("Profile could not be saved: " + ex.Message); }
    }

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var open = new OpenFileDialog
        {
            Title = "Import resource profile JSON", Filter = "Profile JSON (*.json)|*.json",
            CheckFileExists = true
        };
        if (open.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var profile = ProjectProfiles.Load(open.FileName);
            ProjectProfiles.Save(profile);
            ReloadProfiles();
            ShowStatus("Imported " + profile.Name +
                ". Resources were NOT automatically installed or changed.");
        }
        catch (Exception ex) { ShowStatus("Profile import rejected: " + ex.Message); }
    }

    private void SavedProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SavedProfiles.SelectedItem is not ProjectProfiles.Profile profile) return;
        ProfileName.Text = profile.Name;
        ProjectFolderText.Text = profile.ProjectFolder ?? "";
        ProfileNotes.Text = profile.Notes;
        ProfileDetails.Text = $"{profile.ResourceIds.Length} saved resource preferences. " +
            "Choose 'Check missing resources' to compare with this computer.";
    }

    private void ReviewProfile_Click(object sender, RoutedEventArgs e)
    {
        if (SavedProfiles.SelectedItem is not ProjectProfiles.Profile profile)
        {
            ProfileDetails.Text = "Choose a saved profile first.";
            return;
        }
        var missing = ProjectProfiles.MissingResources(profile);
        ProfileDetails.Text = missing.Count == 0
            ? "All referenced resources are detected. This is a toolkit manifest, not an Aseprite settings switch."
            : $"{missing.Count} missing resources:\n" + string.Join("\n", missing) +
              "\nUse the Creator's Library to review and install desired items individually.";
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (SavedProfiles.SelectedItem is not ProjectProfiles.Profile profile) return;
        var save = new SaveFileDialog
        {
            Title = "Export portable project profile", Filter = "JSON (*.json)|*.json",
            FileName = profile.Name.Replace('/', '-').Replace('\\', '-') + "-Aseprite.json"
        };
        if (save.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            ProjectProfiles.Export(profile, save.FileName);
            ShowStatus("Exported profile. No theme, script or sprite content was copied.");
        }
        catch (Exception ex) { ShowStatus("Export failed: " + ex.Message); }
    }
}
