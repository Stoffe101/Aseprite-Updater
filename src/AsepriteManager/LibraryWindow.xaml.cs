using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;

namespace AsepriteManager;

public partial class LibraryWindow : Window
{
    private readonly ManagerService _service;
    private bool _busy;
    private string? _scriptsRevision;

    public LibraryWindow(ManagerService service)
    {
        _service = service;
        InitializeComponent();
        ToolSearch.Text = "";
        ToolList.ItemsSource = WorkshopLibrary.Tools;
        ToolList.SelectedIndex = 0;
        GalleryList.ItemsSource = PaletteGallery.Presets;
        GalleryList.SelectedIndex = 0;
        RefreshExtensionDetails();
        Closing += ClosingDuringInstall;
        RefreshToolDetails();
    }

    private void ClosingDuringInstall(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true;
        MessageBox.Show(this, "Wait for the current library operation to finish.",
            "Installation in progress", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        ToolSearch.IsEnabled = !value;
        StarterKitButton.IsEnabled = !value;
        GalleryList.IsEnabled = !value;
        PaletteSearch.IsEnabled = !value;
        InstallPaletteButton.IsEnabled = !value && GalleryList.SelectedItem != null;
        PreviewPaletteButton.IsEnabled = !value && GalleryList.SelectedItem != null;
        InstallOverlayButton.IsEnabled = !value;
        RemoveOverlayButton.IsEnabled = !value && TileIndexOverlayManager.Installed;
        BrowseGodotButton.IsEnabled = !value;
        GodotProjectPath.IsEnabled = !value;
        InstallWizardButton.IsEnabled = !value && !string.IsNullOrWhiteSpace(GodotProjectPath.Text);
        RemoveWizardButton.IsEnabled = !value && !string.IsNullOrWhiteSpace(GodotProjectPath.Text);
        CheckWizardButton.IsEnabled = !value && !string.IsNullOrWhiteSpace(GodotProjectPath.Text);
        ToolList.IsEnabled = !value;
        InstallToolButton.IsEnabled = !value && ToolList.SelectedItem != null;
        RemoveToolButton.IsEnabled = !value && ToolList.SelectedItem is LibraryTool tool &&
            WorkshopLibrary.Installed(tool);
    }

    private void SetStatus(string message)
    {
        LibraryStatus.Text = message;
        _service.Log("LIBRARY: " + message);
    }

    private void Search_Changed(object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ToolList == null || !IsInitialized) return;
        var phrase = ToolSearch.Text.Trim();
        ToolList.ItemsSource = WorkshopLibrary.Tools.Where(item =>
            phrase.Length == 0 || string.Join(" ", item.Name, item.Kind,
                item.Description, item.PixelTownUse).Contains(phrase,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
        ToolList.SelectedIndex = ToolList.Items.Count > 0 ? 0 : -1;
    }

    private void ToolList_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) => RefreshToolDetails();

    private void RefreshToolDetails()
    {
        if (ToolList?.SelectedItem is not LibraryTool tool)
        {
            if (ToolTitle != null) ToolTitle.Text = "No matching tools";
            return;
        }
        ToolTitle.Text = tool.Name;
        ToolCategory.Text = tool.Kind.ToUpperInvariant();
        ToolDescription.Text = tool.Description;
        ToolPixelTown.Text = "PIXELTOWN: " + tool.PixelTownUse;
        var installed = WorkshopLibrary.Installed(tool);
        var revision = WorkshopLibrary.Revision(tool);
        ToolStatus.Text = installed
            ? $"Installed • revision {(revision == null ? "unknown" : revision[..Math.Min(10, revision.Length)])}"
            : "Not installed";
        InstallToolButton.Content = installed ? "Reinstall / update" : "Install selected";
        InstallToolButton.IsEnabled = !_busy;
        RemoveToolButton.IsEnabled = !_busy && installed;
    }

    private async void InstallTool_Click(object sender, RoutedEventArgs e)
    {
        if (ToolList.SelectedItem is not LibraryTool tool) return;
        if (MessageBox.Show(this,
            $"Install {tool.Name}?\n\n{tool.Description}\n\n" +
            $"From {WorkshopLibrary.OfficialScriptsRepo} (MIT license).\n\n" +
            "Aseprite Lua scripts run code when invoked. Only install resources you trust. " +
            "Aseprite should be closed before continuing.",
            "Install curated tool", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        SetBusy(true);
        try
        {
            SetStatus($"Checking official source for {tool.Name}...");
            _scriptsRevision = await WorkshopLibrary.LatestToolRevision(CancellationToken.None);
            var rev = _scriptsRevision;
            await Task.Run(() => WorkshopLibrary.InstallTool(tool, rev,
                _service.Log, CancellationToken.None));
            SetStatus($"Installed {tool.Name}. Restart Aseprite and use File → Scripts.");
        }
        catch (Exception ex)
        {
            SetStatus("Tool installation failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Tool installation failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); RefreshToolDetails(); }
    }

    private void RemoveTool_Click(object sender, RoutedEventArgs e)
    {
        if (ToolList.SelectedItem is not LibraryTool tool) return;
        if (MessageBox.Show(this, $"Uninstall {tool.Name}?\n\n" +
            "Manager retains its previous files in a backup; modified resources are protected.",
            "Uninstall managed tool", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            WorkshopLibrary.UninstallTool(tool, _service.Log);
            SetStatus($"{tool.Name} was moved to the backup folder.");
        }
        catch (Exception ex)
        {
            SetStatus("Uninstall blocked: " + ex.Message);
        }
        RefreshToolDetails();
    }

    private void OpenToolSource_Click(object sender, RoutedEventArgs e)
    {
        if (ToolList.SelectedItem is not LibraryTool tool) return;
        var url = WorkshopLibrary.OfficialScriptsRepo + "/blob/main/" +
            Uri.EscapeDataString(tool.UpstreamFile);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async void CheckTools_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            SetStatus("Comparing installed tools with the upstream MIT repository...");
            _scriptsRevision = await WorkshopLibrary.LatestToolRevision(CancellationToken.None);
            var managed = WorkshopLibrary.Tools.Where(WorkshopLibrary.Installed).ToArray();
            var newer = managed.Count(t => WorkshopLibrary.Revision(t) != _scriptsRevision);
            SetStatus($"{managed.Length} tool(s) installed; {newer} have newer upstream repository revisions.");
        }
        catch (Exception ex) { SetStatus("Couldn't check updates: " + ex.Message); }
        finally { SetBusy(false); RefreshToolDetails(); }
    }

    private async void UpdateTools_Click(object sender, RoutedEventArgs e)
    {
        var managed = WorkshopLibrary.Tools.Where(WorkshopLibrary.Installed).ToArray();
        if (managed.Length == 0)
        {
            SetStatus("No managed tools installed yet.");
            return;
        }
        if (MessageBox.Show(this, $"Update {managed.Length} installed tools?\n\n" +
            "Original MIT sources are fetched again. Locally changed files won't be overwritten. " +
            "Close Aseprite first.",
            "Update managed tools", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SetBusy(true);
        var failed = new List<string>();
        try
        {
            _scriptsRevision = await WorkshopLibrary.LatestToolRevision(CancellationToken.None);
            foreach (var tool in managed)
            {
                SetStatus("Checking " + tool.Name + "...");
                try
                {
                    if (WorkshopLibrary.Revision(tool) == _scriptsRevision) continue;
                    var current = _scriptsRevision;
                    await Task.Run(() => WorkshopLibrary.InstallTool(tool, current,
                        _service.Log, CancellationToken.None));
                }
                catch (Exception ex) { failed.Add($"{tool.Name}: {ex.Message}"); }
            }
            SetStatus(failed.Count == 0 ? "Managed tools are up to date." :
                "Some tools were protected or failed: " + string.Join(" | ", failed));
        }
        catch (Exception ex) { SetStatus("Update failed: " + ex.Message); }
        finally { SetBusy(false); RefreshToolDetails(); }
    }

    private async void StarterKit_Click(object sender, RoutedEventArgs e)
    {
        var pick = new OpenFolderDialog
        {
            Title = "Choose the local PixelTown Godot project folder"
        };
        if (pick.ShowDialog(this) != true) return;
        string palette;
        try { palette = WorkshopLibrary.ExtractPixelTownPalette(pick.FolderName); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "PixelTown folder not recognized",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (MessageBox.Show(this,
            "Set up PixelTown's *prototype* art toolkit?\n\n" +
            "This installs four audited MIT scripts: Tiles to Layers, Pack Similar Tiles, " +
            "Move Cels, and Stroke Outside Selection, plus a locally generated color palette.\n\n" +
            "Nothing will be written to the PixelTown project itself. Existing edited managed resources " +
            "will be protected. Scripts run code when invoked in Aseprite, so review their source and " +
            "test on copies of sprites. Close Aseprite before starting.",
            "Install PixelTown starter kit", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        SetBusy(true);
        var failures = new List<string>();
        try
        {
            SetStatus("Fetching vetted official Aseprite script revision...");
            var sha = await WorkshopLibrary.LatestToolRevision(CancellationToken.None);
            foreach (var tool in WorkshopLibrary.StarterTools)
            {
                SetStatus("Installing " + tool.Name + "...");
                try
                {
                    if (WorkshopLibrary.Revision(tool) == sha && WorkshopLibrary.Installed(tool))
                        continue;
                    await Task.Run(() => WorkshopLibrary.InstallTool(
                        tool, sha, _service.Log, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    failures.Add(tool.Name + ": " + ex.Message);
                    _service.Log("KIT ITEM FAILED: " + tool.Name + ": " + ex);
                }
            }
            try
            {
                WorkshopLibrary.InstallPalette("pixeltown-core",
                    "PixelTown Core Colors (draft)", palette, _service.Log);
                PaletteStatus.Text = "PixelTown draft colors installed from the local project.";
            }
            catch (Exception ex)
            {
                failures.Add("PixelTown palette: " + ex.Message);
                _service.Log("KIT PALETTE FAILED: " + ex);
            }
            SetStatus(failures.Count == 0
                ? "PixelTown starter kit installed. Restart Aseprite to load scripts and palettes."
                : "Kit partially installed, with protected items skipped: " + string.Join(" | ", failures));
        }
        catch (Exception ex) { SetStatus("Starter kit setup failed: " + ex.Message); }
        finally { SetBusy(false); RefreshToolDetails(); }
    }

    private void ExtractPalette_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select PixelTown's Godot project folder" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var gpl = WorkshopLibrary.ExtractPixelTownPalette(dialog.FolderName);
            WorkshopLibrary.InstallPalette("pixeltown-core",
                "PixelTown Core Colors (draft)", gpl, _service.Log);
            PaletteStatus.Text = "PixelTown prototype palette installed from the local source. " +
                "Restart Aseprite and select it from the palette presets.";
            SetStatus("PixelTown palette created without modifying the Godot project.");
        }
        catch (Exception ex)
        {
            PaletteStatus.Text = "Palette extraction failed: " + ex.Message;
            _service.Log("LIBRARY PALETTE ERROR: " + ex);
        }
    }

    private void ExtractFullPalette_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select local PixelTown Godot project" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var gpl = WorkshopLibrary.ExtractPixelTownPalette(dialog.FolderName, fullDiagnostic: true);
            WorkshopLibrary.InstallPalette("pixeltown-full-draft",
                "PixelTown Full Draft Diagnostics", gpl, _service.Log);
            PaletteStatus.Text = "Full source color inventory installed for diagnostics. " +
                "Prefer the named core palette for new artwork; neither is final.";
            SetStatus("Full prototype color reference imported. Restart Aseprite to use it.");
        }
        catch (Exception ex)
        {
            PaletteStatus.Text = "Full palette import failed: " + ex.Message;
            _service.Log("LIBRARY FULL PALETTE ERROR: " + ex);
        }
    }

    private void ImportPalette_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Import a GIMP Palette into Aseprite",
            Filter = "GIMP Palette (*.gpl)|*.gpl",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var name = Path.GetFileNameWithoutExtension(picker.FileName);
            var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9-]+", "-").Trim('-');
            slug = slug.Length > 40 ? slug[..40] : slug;
            if (slug.Length < 3) slug = "custom-" + slug;
            var text = File.ReadAllText(picker.FileName);
            WorkshopLibrary.ValidateGpl(text);
            WorkshopLibrary.InstallPalette("palette-" + slug,
                "Imported: " + name, text, _service.Log);
            PaletteStatus.Text = $"Imported {name}. Restart Aseprite and select it in palette presets.";
        }
        catch (Exception ex)
        {
            PaletteStatus.Text = "Palette import failed: " + ex.Message;
            _service.Log("LIBRARY GPL ERROR: " + ex);
        }
    }

    private void OpenExtensions_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(WorkshopLibrary.ExtensionsFolder);

    private void OpenScripts_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(WorkshopLibrary.ScriptsFolder);

    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = true,
            ArgumentList = { folder }
        });
    }
    private void PaletteSearch_Changed(object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        if (GalleryList == null || !IsInitialized) return;
        var term = PaletteSearch.Text.Trim();
        GalleryList.ItemsSource = PaletteGallery.Presets.Where(p =>
            term.Length == 0 || string.Join(" ", p.Name, p.Credit, p.Usage)
                .Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
        GalleryList.SelectedIndex = GalleryList.Items.Count > 0 ? 0 : -1;
    }
    private void GalleryList_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) => RefreshGalleryDetails();

    private void RefreshGalleryDetails()
    {
        if (GalleryList?.SelectedItem is not PalettePreset preset)
        {
            if (GalleryTitle != null) GalleryTitle.Text = "No matching palettes";
            if (PaletteSwatches != null) PaletteSwatches.Children.Clear();
            return;
        }
        GalleryTitle.Text = preset.Name;
        GalleryDescription.Text = $"{preset.Usage}\nCreator: {preset.Credit}";
        GalleryStatus.Text = PaletteGallery.IsInstalled(preset) ?
            "Managed copy installed. Existing Aseprite-bundled presets are unaffected." :
            "Available to preview or install as an optional managed copy.";
        PaletteSwatches.Children.Clear();
    }
    private async void PreviewPalette_Click(object sender, RoutedEventArgs e)
    {
        if (GalleryList.SelectedItem is not PalettePreset preset) return;
        SetBusy(true);
        try
        {
            GalleryStatus.Text = "Reading original colors from upstream Aseprite...";
            var result = await PaletteGallery.DownloadAsync(preset);
            var colors = PaletteGallery.Colors(result.Gpl);
            PaletteSwatches.Children.Clear();
            foreach (var (r, g, b) in colors)
            {
                var border = new System.Windows.Controls.Border
                {
                    Width = 22, Height = 22,
                    Margin = new Thickness(0, 0, 4, 4),
                    Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(r, g, b)),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(20, 31, 49)),
                    ToolTip = $"#{r:X2}{g:X2}{b:X2}"
                };
                PaletteSwatches.Children.Add(border);
            }
            GalleryStatus.Text = $"{colors.Count} colors. {preset.Credit}. " +
                (PaletteGallery.IsInstalled(preset) ? "Managed copy installed." : "Not installed by Manager.");
        }
        catch (Exception ex) { GalleryStatus.Text = "Preview unavailable: " + ex.Message; }
        finally { SetBusy(false); }
    }
    private async void InstallGalleryPalette_Click(object sender, RoutedEventArgs e)
    {
        if (GalleryList.SelectedItem is not PalettePreset preset) return;
        if (MessageBox.Show(this,
            $"Install a managed copy of {preset.Name}?\n\nCreator: {preset.Credit}\n" +
            "Source: aseprite/aseprite on GitHub.\n\n" +
            "Aseprite may already include this palette. This creates a separate preset " +
            "and never modifies existing sprites. Existing customized managed files will be protected.",
            "Install curated palette", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            SetStatus("Installing palette " + preset.Name + "...");
            await Task.Run(() => PaletteGallery.InstallAsync(preset, _service.Log,
                CancellationToken.None));
            GalleryStatus.Text = "Managed palette installed. Restart Aseprite and select it in palette presets.";
            SetStatus(preset.Name + " installed.");
        }
        catch (Exception ex)
        {
            GalleryStatus.Text = "Palette install blocked: " + ex.Message;
            _service.Log("PALETTE GALLERY ERROR: " + ex);
        }
        finally { SetBusy(false); }
    }
    private void OpenPaletteSource_Click(object sender, RoutedEventArgs e)
    {
        if (GalleryList.SelectedItem is PalettePreset preset)
            OpenWebsite(preset.Url);
    }
    private void RefreshExtensionDetails()
    {
        if (ExtensionStatus == null) return;
        ExtensionStatus.Text = TileIndexOverlayManager.Installed ?
            "Installed • commit " + (TileIndexOverlayManager.Revision() ?? "unknown")[..Math.Min(10,
                (TileIndexOverlayManager.Revision() ?? "unknown").Length)] :
            "Not installed";
        if (RemoveOverlayButton != null)
            RemoveOverlayButton.IsEnabled = !_busy && TileIndexOverlayManager.Installed;
    }
    private async void InstallOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
            "Install Pixeltica's MIT-licensed Tile Index Overlay?\n\n" +
            "This Lua extension creates tile-number layers and registers a keyboard shortcut. " +
            "Review its source before use, and close Aseprite before installation.",
            "Install Lua extension", MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            SetStatus("Downloading Tile Index Overlay from Pixeltica's GitHub...");
            await Task.Run(() => TileIndexOverlayManager.InstallAsync(
                _service.Log, CancellationToken.None));
            SetStatus("Tile Index Overlay installed. Restart Aseprite.");
        }
        catch (Exception ex) { SetStatus("Tile Index Overlay failed: " + ex.Message); }
        finally { SetBusy(false); RefreshExtensionDetails(); }
    }
    private void RemoveOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Remove the managed Tile Index Overlay? Its backup will be retained.",
            "Uninstall extension", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            if (Process.GetProcessesByName("aseprite").Length > 0)
                throw new InvalidOperationException("Close Aseprite first.");
            TileIndexOverlayManager.Remove(_service.Log);
            SetStatus("Tile Index Overlay removed with backup.");
        }
        catch (Exception ex) { SetStatus("Uninstall blocked: " + ex.Message); }
        RefreshExtensionDetails();
    }
    private void ViewOverlay_Click(object sender, RoutedEventArgs e) =>
        OpenWebsite(TileIndexOverlayManager.Source);

    private void BrowseGodot_Click(object sender, RoutedEventArgs e)
    {
        var choose = new OpenFolderDialog { Title = "Select a Godot 4 project (PixelTown or other)" };
        if (choose.ShowDialog(this) == true) GodotProjectPath.Text = choose.FolderName;
    }
    private void GodotProjectPath_Changed(object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        if (GodotStatus == null || !IsInitialized) return;
        RefreshGodotDetails();
    }
    private void RefreshGodotDetails()
    {
        if (string.IsNullOrWhiteSpace(GodotProjectPath.Text))
        {
            GodotStatus.Text = "Choose a folder containing project.godot.";
            return;
        }
        try
        {
            var info = GodotWizardManager.Inspect(GodotProjectPath.Text);
            GodotStatus.Text = !info.Installed ? "Godot 4 project detected • Wizard not installed." :
                !info.Managed ? "Wizard already exists, but is not Manager-owned. Installation will be blocked." :
                info.Modified ? "Wizard was manually modified. Update/remove blocked to protect changes." :
                "Installed • managed version " + info.Revision?[..Math.Min(12, info.Revision.Length)];
        }
        catch (Exception ex) { GodotStatus.Text = ex.Message; }
    }
    private async void InstallWizard_Click(object sender, RoutedEventArgs e)
    {
        var root = GodotProjectPath.Text.Trim();
        try { GodotWizardManager.VerifyProject(root); }
        catch (Exception ex) { GodotStatus.Text = ex.Message; return; }
        if (MessageBox.Show(this,
            "Install or update the MIT-licensed Aseprite Wizard (Godot 4 branch)?\n\n" +
            "Project: " + root + "\n\n" +
            "Manager writes ONLY under addons/AsepriteWizard, retains a ZIP backup when updating, " +
            "and does not edit project.godot, enable the plugin, or change your sprites. " +
            "Close Godot first. This plugin contains editor code that executes after you enable it.\n\n" +
            "Compatibility with PixelTown's provisional Godot 4.7.2 still needs live testing.",
            "Install Godot editor addon", MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            SetStatus("Installing Godot Aseprite Wizard into selected project...");
            await Task.Run(() => GodotWizardManager.InstallOrUpdateAsync(
                root, _service.Log, CancellationToken.None));
            SetStatus("Wizard installed. In Godot, enable it under Project Settings > Plugins.");
        }
        catch (Exception ex)
        {
            SetStatus("Wizard installation blocked: " + ex.Message);
            _service.Log("GODOT WIZARD ERROR: " + ex);
        }
        finally { SetBusy(false); RefreshGodotDetails(); }
    }
    private void RemoveWizard_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
            "Uninstall the Manager-owned Wizard from this project?\n\n" +
            "Disable the plugin in Godot first, then close Godot. " +
            "Manager preserves a ZIP backup and does not delete imported sprites, " +
            "scenes, or project settings.",
            "Uninstall Godot Wizard", MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            GodotWizardManager.Uninstall(GodotProjectPath.Text, _service.Log);
            SetStatus("Wizard removed. A backup ZIP was saved.");
        }
        catch (Exception ex) { SetStatus("Wizard removal blocked: " + ex.Message); }
        RefreshGodotDetails();
    }
    private async void CheckWizard_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var version = await GodotWizardManager.LatestCommitAsync(CancellationToken.None);
            var state = GodotWizardManager.Inspect(GodotProjectPath.Text);
            GodotStatus.Text = !state.Installed ? "Wizard not installed • latest upstream " + version[..10] :
                !state.Managed ? "Existing Wizard not managed by Aseprite Manager." :
                state.Modified ? "Local modifications found. Updates are blocked." :
                state.Revision == version ? "Wizard is up to date." :
                "A newer Wizard source revision is available.";
        }
        catch (Exception ex) { GodotStatus.Text = "Could not check update: " + ex.Message; }
        finally { SetBusy(false); }
    }
    private void ViewWizard_Click(object sender, RoutedEventArgs e) =>
        OpenWebsite(GodotWizardManager.RepositoryUrl);
    private void ImportBrush_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select PNG stamp image for reusable brush vault",
            Filter = "PNG files (*.png)|*.png",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var file = BrushVault.ImportPng(dialog.FileName);
            BrushStatus.Text = "Saved brush source: " + file +
                ". Open it in Aseprite, select pixels, press Ctrl+B to save a custom pattern brush.";
            SetStatus("PNG brush source added without changing Aseprite's preferences.");
        }
        catch (Exception ex) { BrushStatus.Text = "Brush import failed: " + ex.Message; }
    }
    private void OpenBrushFolder_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(BrushVault.Folder);
    private static void OpenWebsite(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });


}
