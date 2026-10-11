using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AsepriteManager;

public sealed record PalettePreset(string Id, string Name, string SourcePath,
    string Credit, string Usage, string Kind = "Pixel Art")
{
    public override string ToString() => Name;
    public string Url => "https://github.com/aseprite/aseprite/blob/main/data/extensions/" + SourcePath;
}

/// <summary>Curated discovery of public palette data in the official upstream
/// Aseprite repository. Some are already bundled with Aseprite: installing a
/// managed copy is optional. Individual creators retain all their rights.</summary>
public static class PaletteGallery
{
    public static IReadOnlyList<PalettePreset> Presets { get; } = new[]
    {
        new PalettePreset("db16","DawnBringer 16","dawnbringer-palettes/db16.gpl","DawnBringer","Compact village sprites, clean readability"),
        new PalettePreset("db32","DawnBringer 32","dawnbringer-palettes/db32.gpl","DawnBringer","General-purpose fantasy and landscape ramps"),
        new PalettePreset("endesga16","ENDESGA 16","endesga-palettes/edg16.gpl","ENDESGA","Simple character palettes and high contrast"),
        new PalettePreset("endesga32","ENDESGA 32","endesga-palettes/edg32.gpl","ENDESGA","Storybook outdoor and dungeon color ramps"),
        new PalettePreset("aap64","AAP-64","adigunpolack-palettes/aap-64.gpl","Adigun A. Polack","Detailed environments and clothing"),
        new PalettePreset("aap12","AAP Micro 12","adigunpolack-palettes/aap-micro12.gpl","Adigun A. Polack","Small-scale readability exercises"),
        new PalettePreset("aap128","AAP Splendor 128","adigunpolack-palettes/aap-splendor128.gpl","Adigun A. Polack","Wider material and lighting variation"),
        new PalettePreset("zughy32","Zughy 32","zughy-palettes/zughy-32.gpl","Zughy","Colorful world building"),
        new PalettePreset("rosy42","Rosy 42","pinetreepizza-palettes/rosy-42.gpl","PineTreePizza","Warm villages and character skin/clothing"),
        new PalettePreset("bubblegum16","Bubblegum 16","pinetreepizza-palettes/bubblegum-16.gpl","PineTreePizza","Playful mood studies"),
        new PalettePreset("arne16","Arne 16","arne-palettes/arne16.gpl","Arne Niklas Jansson","Tiny character silhouettes"),
        new PalettePreset("arne32","Arne 32","arne-palettes/arne32.gpl","Arne Niklas Jansson","Modest outdoor palette"),
        new PalettePreset("pico8","PICO-8","pico8-palette/pico-8.gpl","PICO-8","Restrained retro color exercises")
    };
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(40) };
    static PaletteGallery() => Client.DefaultRequestHeaders.UserAgent.ParseAdd("AsepriteManager/0.5.0");

    public static async Task<(string Revision, string Gpl)> DownloadAsync(PalettePreset preset,
        CancellationToken ct = default)
    {
        if (!Presets.Contains(preset)) throw new InvalidOperationException("Unlisted palette.");
        var path = "https://raw.githubusercontent.com/aseprite/aseprite/main/data/extensions/" +
            preset.SourcePath;
        using var response = await Client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 100_000)
            throw new InvalidDataException("Unexpected palette size.");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length > 100_000) throw new InvalidDataException("Unexpected palette size.");
        var gpl = new UTF8Encoding(false, true).GetString(bytes);
        WorkshopLibrary.ValidateGpl(gpl);
        return (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(gpl))), gpl);
    }
    public static IReadOnlyList<(byte R, byte G, byte B)> Colors(string gpl)
    {
        WorkshopLibrary.ValidateGpl(gpl);
        return Regex.Matches(gpl, @"(?m)^\s*(\d{1,3})\s+(\d{1,3})\s+(\d{1,3})(?:\s|$)")
            .Select(m => (
                byte.Parse(m.Groups[1].Value),
                byte.Parse(m.Groups[2].Value),
                byte.Parse(m.Groups[3].Value)))
            .ToArray();
    }
    public static bool IsInstalled(PalettePreset preset) =>
        Directory.Exists(Path.Combine(WorkshopLibrary.ExtensionsFolder,
            "managed-gallery-" + preset.Id)) &&
        File.Exists(WorkshopLibrary.RecordPath("gallery-" + preset.Id));
    public static string? InstalledRevision(PalettePreset preset)
    {
        var file = WorkshopLibrary.RecordPath("gallery-" + preset.Id);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.GetProperty("Revision").GetString();
        }
        catch { return null; }
    }
    public static async Task InstallAsync(PalettePreset preset, Action<string> log, CancellationToken ct)
    {
        var (_, gpl) = await DownloadAsync(preset, ct);
        ct.ThrowIfCancellationRequested();
        WorkshopLibrary.InstallPalette("gallery-" + preset.Id,
            preset.Name + " • " + preset.Credit, gpl, log);
    }
}

public static class BrushVault
{
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Aseprite Manager", "brush-sources");

    /// <summary>Organizes user-provided PNG stamps, never rewrites
    /// Aseprite's internal user.aseprite-brushes XML preferences.</summary>
    public static string ImportPng(string path)
    {
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".png",
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose an existing .png brush image.");
        var info = new FileInfo(path);
        if (info.Length is < 30 or > 12_000_000)
            throw new InvalidDataException("Brush image must be a small PNG (12 MB maximum).");
        using (var input = File.OpenRead(path))
        {
            var pngSignature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            var header = new byte[8];
            if (input.Read(header) != 8 || !header.SequenceEqual(pngSignature))
                throw new InvalidDataException("The selected image is not a PNG file.");
        }
        Directory.CreateDirectory(Folder);
        var filename = Path.GetFileNameWithoutExtension(path);
        var unique = filename + "-" + Guid.NewGuid().ToString("N")[..8] + ".png";
        var destination = Path.Combine(Folder, unique);
        File.Copy(path, destination, overwrite: false);
        return destination;
    }
}
