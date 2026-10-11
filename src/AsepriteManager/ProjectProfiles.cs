using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AsepriteManager;

/// <summary>
/// Project toolkits are descriptions of preferred resources, not changes to
/// Aseprite's global configuration or to Godot projects. Applying never installs
/// or executes code automatically.
/// </summary>
public static class ProjectProfiles
{
    public sealed record Profile(string Name, string? ProjectFolder,
        string[] ResourceIds, string Notes, int SchemaVersion = 1);

    public static string Folder => Path.Combine(ManagerService.DataRoot, "project-profiles");
    public static IReadOnlyList<string> SupportedResources =>
        WorkshopLibrary.Tools.Select(t => "tool:" + t.Id)
        .Concat(ThemeCatalog.Curated.Select(t => "theme:" + t.Id))
        .Concat(PaletteGallery.Presets.Select(p => "palette:" + p.Id))
        .Concat(new[] { "extension:tile-index-overlay", "godot:aseprite-wizard" })
        .ToArray();

    public static Profile Capture(string name, string? projectFolder, string notes)
    {
        var ids = WorkshopLibrary.Tools.Where(WorkshopLibrary.Installed)
            .Select(t => "tool:" + t.Id)
            .Concat(ThemeCatalog.Curated.Where(t =>
                new ThemeManager(_ => { }).IsInstalled(t)).Select(t => "theme:" + t.Id))
            .Concat(PaletteGallery.Presets.Where(PaletteGallery.IsInstalled)
                .Select(p => "palette:" + p.Id))
            .ToList();
        if (TileIndexOverlayManager.Installed) ids.Add("extension:tile-index-overlay");
        if (!string.IsNullOrWhiteSpace(projectFolder))
        {
            try
            {
                if (GodotWizardManager.Inspect(projectFolder).Installed)
                    ids.Add("godot:aseprite-wizard");
            }
            catch { /* Profiles may refer to projects not available on this machine. */ }
        }
        return Validate(new Profile(name, projectFolder, ids.ToArray(), notes));
    }

    public static Profile Validate(Profile profile)
    {
        if (profile.SchemaVersion != 1) throw new InvalidDataException("Unsupported profile format version.");
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 80 ||
            Regex.IsMatch(profile.Name, @"[\x00-\x1f]"))
            throw new InvalidDataException("Profile name must be 1–80 printable characters.");
        if (profile.Notes.Length > 1500) throw new InvalidDataException("Profile notes are too long.");
        if (profile.ProjectFolder?.Length > 1500)
            throw new InvalidDataException("Project path is too long.");
        if (profile.ResourceIds.Length > 200 ||
            profile.ResourceIds.Any(id => !SupportedResources.Contains(id, StringComparer.Ordinal)))
            throw new InvalidDataException("Profile references unknown or unsupported resources.");
        return profile with { ResourceIds = profile.ResourceIds.Distinct(StringComparer.Ordinal).ToArray() };
    }

    public static string Save(Profile profile)
    {
        profile = Validate(profile);
        Directory.CreateDirectory(Folder);
        var destination = Path.Combine(Folder, Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(profile.Name.ToUpperInvariant()))) + ".json");
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(profile, new JsonSerializerOptions
            { WriteIndented = true }));
            File.Move(temp, destination, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return destination;
    }

    public static IReadOnlyList<Profile> ReadAll()
    {
        if (!Directory.Exists(Folder)) return Array.Empty<Profile>();
        var profiles = new List<Profile>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try { profiles.Add(Load(file)); } catch { /* invalid files are never acted upon */ }
        }
        return profiles.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static Profile Load(string file)
    {
        if (new FileInfo(file).Length > 100_000)
            throw new InvalidDataException("Profile file is unexpectedly large.");
        var item = JsonSerializer.Deserialize<Profile>(File.ReadAllText(file)) ??
            throw new InvalidDataException("Empty profile document.");
        return Validate(item);
    }

    public static void Export(Profile profile, string destination)
    {
        if (File.Exists(destination))
            throw new IOException("Export destination already exists.");
        var value = JsonSerializer.Serialize(Validate(profile), new JsonSerializerOptions
        { WriteIndented = true });
        File.WriteAllText(destination, value);
    }

    public static IReadOnlyList<string> MissingResources(Profile profile)
    {
        var result = new List<string>();
        foreach (var id in Validate(profile).ResourceIds)
        {
            bool installed = id.Split(':', 2) switch
            {
                ["tool", var name] => WorkshopLibrary.Tools.Any(t =>
                    t.Id == name && WorkshopLibrary.Installed(t)),
                ["theme", var name] => ThemeCatalog.Curated.Any(t =>
                    t.Id == name && new ThemeManager(_ => { }).IsInstalled(t)),
                ["palette", var name] => PaletteGallery.Presets.Any(p =>
                    p.Id == name && PaletteGallery.IsInstalled(p)),
                ["extension", "tile-index-overlay"] => TileIndexOverlayManager.Installed,
                ["godot", "aseprite-wizard"] => !string.IsNullOrWhiteSpace(profile.ProjectFolder) &&
                    TryWizardInstalled(profile.ProjectFolder),
                _ => false
            };
            if (!installed) result.Add(id);
        }
        return result;
    }

    private static bool TryWizardInstalled(string project)
    {
        try { return GodotWizardManager.Inspect(project).Installed; }
        catch { return false; }
    }
}
