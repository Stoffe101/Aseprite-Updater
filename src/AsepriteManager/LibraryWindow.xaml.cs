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
            foreach (var tool in WorkshopLibrary.Tools)
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
                WorkshopLibrary.InstallPalette("pixeltown-prototype",
                    "PixelTown Prototype Colors (unapproved)", palette, _service.Log);
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
            WorkshopLibrary.InstallPalette("pixeltown-prototype",
                "PixelTown Prototype Colors (unapproved)", gpl, _service.Log);
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
}
