using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AsepriteManager;

public sealed record LibraryTool(string Id, string Name, string Kind, string Description,
    string PixelTownUse, string UpstreamFile)
{
    public override string ToString() => Name;
}
public sealed record LibraryReceipt(string Id, string Revision, Dictionary<string, string> Files);

public static class WorkshopLibrary
{
    public const string OfficialScriptsRepo = "https://github.com/aseprite/Aseprite-Script-Examples";
    public static readonly IReadOnlyList<LibraryTool> Tools = new[]
    {
        new LibraryTool("split-tiles","Tiles to Layers","Tilesets",
            "Split a grid-based tilesheet into named layers.","16x16 terrain and modular structures; choose tile dimensions in Aseprite.","Tiles to Layers.lua"),
        new LibraryTool("pack-tiles","Pack Similar Tiles","Tilesets",
            "Find duplicate tiles and make a compact tileset/map.","Tile repetition analysis. Operate on a COPY of the sprite.","Pack Similar Tiles.lua"),
        new LibraryTool("move-cels","Move Cels","Animation",
            "Move selected animation cels by an X/Y offset.","Align character poses and equipment overlays.","Move Cels.lua"),
        new LibraryTool("stroke-outside","Stroke Outside Selection","Editing",
            "Add an outside border to a selected region.","Readable silhouettes for tiny characters and gear.","Stroke Outside Selection.lua"),
        new LibraryTool("export-tags","Export Tags to Sheets","Godot Workflow",
            "Export each named animation tag as PNG and JSON. Prompts before overwriting existing exports.",
            "Prepare directional walk, work and combat sprite animations.","Export Tags To Different Sprite Sheets.lua"),
        new LibraryTool("merge-top-name","Merge with Top Layer Name","Editing",
            "Merge down while keeping the active top layer name. Modifies the open sprite.",
            "Organize complex character and equipment layers; test on an asset copy.","Merge Down with Name Of Top Layer.lua"),
        new LibraryTool("stack-sprites","Stack All Open Sprites","Workflow",
            "Combine currently open sprites into one new layered sprite.",
            "Compare villager, guard and object sprite variations without changing source files.","Stack All Sprites in One.lua"),
        new LibraryTool("glass-floor","Glass Floor Reflection","Environment",
            "Create a mirrored semi-transparent floor layer from selected layers.",
            "Prototype lake reflections and crystal-cave water. This modifies the open sprite: use a copy.",
            "Glass Floor.lua"),
        new LibraryTool("touch-toolbar","Touch Toolbar","Accessibility",
            "Open a compact Undo/Redo and timeline navigation toolbar.",
            "Useful for pen/tablet workflows and small laptop screens.",
            "Touch Toolbar.lua")
    };
    // Safe default pack remains focused; optional tools are installable individually.
    public static IReadOnlyList<LibraryTool> StarterTools => Tools.Take(4).ToArray();

    public static string ScriptsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Aseprite", "scripts", "AsepriteManager");
    public static string ExtensionsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aseprite", "extensions");
    public static string RecordsFolder => Path.Combine(ManagerService.DataRoot, "library-records");
    public static string BackupsFolder => Path.Combine(ManagerService.DataRoot, "library-backups");
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(75) };
    static WorkshopLibrary()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Stoffe101-AsepriteManager/0.4.0");
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static bool Installed(LibraryTool tool) =>
        Directory.Exists(ToolDir(tool)) && File.Exists(RecordPath(tool.Id));
    public static string? Revision(LibraryTool tool) => ReadReceipt(RecordPath(tool.Id))?.Revision;
    public static string ToolDir(LibraryTool tool) => Path.Combine(ScriptsFolder, "managed-" + tool.Id);
    public static string RecordPath(string id) => Path.Combine(RecordsFolder, id + ".json");

    public static async Task<string> LatestToolRevision(CancellationToken ct)
    {
        using var response = await GitHubApi.GetAsync(Client,
            "https://api.github.com/repos/aseprite/Aseprite-Script-Examples/commits/main", ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var sha = json.RootElement.GetProperty("sha").GetString() ?? "";
        if (!Regex.IsMatch(sha, "^[0-9a-f]{40}$", RegexOptions.IgnoreCase))
            throw new InvalidDataException("Unexpected GitHub commit SHA.");
        return sha;
    }

    public static async Task InstallTool(LibraryTool tool, string sha,
        Action<string> log, CancellationToken ct)
    {
        if (!Tools.Contains(tool) || !Regex.IsMatch(sha, "^[0-9a-f]{40}$", RegexOptions.IgnoreCase))
            throw new InvalidDataException("Tool or source revision isn't approved.");
        if (System.Diagnostics.Process.GetProcessesByName("aseprite").Length != 0)
            throw new InvalidOperationException("Close Aseprite before installing scripts.");

        var url = "https://raw.githubusercontent.com/aseprite/Aseprite-Script-Examples/" + sha + "/";
        var script = await ReadText(new Uri(url + Uri.EscapeDataString(tool.UpstreamFile)), 150_000, ct);
        var license = await ReadText(new Uri(url + "LICENSE.txt"), 15_000, ct);
        if (!script.Contains("app.", StringComparison.Ordinal) ||
            !license.Contains("Permission is hereby granted", StringComparison.Ordinal))
            throw new InvalidDataException("The downloaded files did not match the expected script/license format.");

        Directory.CreateDirectory(ScriptsFolder);
        var stage = Path.Combine(ScriptsFolder, ".stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(stage, tool.UpstreamFile), script, ct);
            await File.WriteAllTextAsync(Path.Combine(stage, "LICENSE.txt"), license, ct);
            ReplaceManaged(ToolDir(tool), stage, RecordPath(tool.Id),
                new LibraryReceipt(tool.Id, sha, HashFiles(stage)), log);
            log($"Installed {tool.Name} from official MIT-licensed Aseprite scripts.");
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    public static void UninstallTool(LibraryTool tool, Action<string> log)
    {
        var target = ToolDir(tool);
        var receipt = RecordPath(tool.Id);
        ValidateOwned(target, receipt);
        Directory.CreateDirectory(BackupsFolder);
        var backup = BackupPath(tool.Id);
        Directory.Move(target, backup);
        File.Delete(receipt);
        log($"Uninstalled {tool.Name}. Full backup: {backup}");
    }

    public static string ExtractPixelTownPalette(string root, bool fullDiagnostic = false)
    {
        if (!File.Exists(Path.Combine(root, "project.godot")))
            throw new FileNotFoundException("Select the PixelTown Godot project directory.");
        var source = Path.Combine(root, "art", "world_canvas.gd");
        if (!File.Exists(source))
            throw new FileNotFoundException("PixelTown's current art/world_canvas.gd wasn't found.", source);

        // The hand-authored scene currently contains many decorative, one-off
        // colors. The twelve explicitly named constants are a more useful
        // starting palette than the entire diagnostic color inventory.
        // Everything is read locally: the proprietary game source is neither
        // uploaded to GitHub nor bundled in this public MIT Manager repository.
        var text = File.ReadAllText(source);
        var pattern = fullDiagnostic
            ? """Color\s*\(\s*["']#(?<rgb>[0-9a-fA-F]{6})(?:[0-9a-fA-F]{2})?["']"""
            : """(?m)^\s*const\s+(?<name>[A-Z][A-Z0-9_]*)\s*:?=\s*Color\s*\(\s*["']#(?<rgb>[0-9a-fA-F]{6})(?:[0-9a-fA-F]{2})?["']""";
        var colors = Regex.Matches(text, pattern).Select(m => new
        {
            Hex = m.Groups["rgb"].Value.ToUpperInvariant(),
            Name = m.Groups["name"].Success ? m.Groups["name"].Value : "Draft"
        }).DistinctBy(x => x.Hex, StringComparer.OrdinalIgnoreCase).ToArray();
        if (colors.Length is < 2 or > 256)
            throw new InvalidDataException($"Found {colors.Length} unique colors; expected 2–256.");
        var name = fullDiagnostic ? "PixelTown Full Draft Diagnostic" : "PixelTown Core Named Colors";
        var palette = new StringBuilder($"GIMP Palette\nName: {name}\nColumns: 8\n");
        palette.AppendLine("# Local prototype colors; NOT an approved final palette. Art remains proprietary to PixelTown.");
        foreach (var item in colors)
        {
            var hex = item.Hex;
            palette.AppendLine($"{Convert.ToInt32(hex[..2],16),3} " +
                $"{Convert.ToInt32(hex.Substring(2,2),16),3} " +
                $"{Convert.ToInt32(hex.Substring(4,2),16),3} {item.Name}");
        }
        return palette.ToString();
    }

    public static void InstallPalette(string id, string display, string palette,
        Action<string> log)
    {
        if (!Regex.IsMatch(id, "^[a-z0-9-]{3,60}$"))
            throw new ArgumentException("Invalid palette ID.");
        ValidateGpl(palette);
        if (System.Diagnostics.Process.GetProcessesByName("aseprite").Length != 0)
            throw new InvalidOperationException("Close Aseprite before installing palettes.");
        Directory.CreateDirectory(ExtensionsFolder);
        var stage = Path.Combine(ExtensionsFolder, ".stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            File.WriteAllText(Path.Combine(stage, "palette.gpl"), palette, new UTF8Encoding(false));
            var package = new
            {
                name = "aseprite-manager-" + id,
                displayName = display,
                version = "1.0.0",
                contributes = new { palettes = new[] { new { id = display, path = "./palette.gpl" } } }
            };
            File.WriteAllText(Path.Combine(stage, "package.json"),
                JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = true }));
            var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(palette)));
            ReplaceManaged(Path.Combine(ExtensionsFolder, "managed-" + id), stage,
                RecordPath(id), new LibraryReceipt(id, revision, HashFiles(stage)), log);
            log($"Installed {display}. Restart Aseprite and open the palette presets.");
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    public static void ValidateGpl(string palette)
    {
        if (palette.Length > 500_000 || !palette.StartsWith("GIMP Palette", StringComparison.Ordinal))
            throw new InvalidDataException("Expected a small .gpl palette file.");
        var entries = Regex.Matches(palette, @"(?m)^\s*(\d{1,3})\s+(\d{1,3})\s+(\d{1,3})(?:\s|$)");
        if (entries.Count is < 2 or > 256)
            throw new InvalidDataException("Palettes must have 2–256 RGB colors.");
        foreach (Match entry in entries)
            for (int i = 1; i <= 3; i++)
                if (int.Parse(entry.Groups[i].Value) > 255)
                    throw new InvalidDataException("Palette RGB values must be 0–255.");
    }

    public static void ReplaceManaged(string target, string stage, string recordPath,
        LibraryReceipt next, Action<string> log)
    {
        if (Directory.Exists(target)) ValidateOwned(target, recordPath);
        Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
        string? backup = null;
        if (Directory.Exists(target))
        {
            Directory.CreateDirectory(BackupsFolder);
            backup = BackupPath(next.Id);
            Directory.Move(target, backup);
        }
        bool moved = false;
        try
        {
            Directory.Move(stage, target);
            moved = true;
            var temp = recordPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(next));
                File.Move(temp, recordPath, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        catch
        {
            if (moved && Directory.Exists(target))
                Directory.Move(target, BackupPath(next.Id) + ".failed");
            if (backup != null && Directory.Exists(backup) && !Directory.Exists(target))
                Directory.Move(backup, target);
            throw;
        }
        if (backup != null) log("Previous resource retained as backup: " + backup);
    }

    private static string BackupPath(string id) =>
        Path.Combine(BackupsFolder, "managed-" + id + "-" + Guid.NewGuid().ToString("N"));

    public static void ValidateOwned(string folder, string receiptPath)
    {
        var receipt = ReadReceipt(receiptPath) ??
            throw new InvalidOperationException("This folder wasn't installed by Manager. No files were changed.");
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        var hashes = HashFiles(folder);
        if (hashes.Count != receipt.Files.Count ||
            hashes.Any(f => !receipt.Files.TryGetValue(f.Key, out var expected) ||
                            !string.Equals(f.Value, expected, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "Managed files were customized. Manager will not replace or remove your changes.");
    }

    private static LibraryReceipt? ReadReceipt(string path)
    {
        try { return JsonSerializer.Deserialize<LibraryReceipt>(File.ReadAllText(path)); }
        catch { return null; }
    }
    private static Dictionary<string,string> HashFiles(string folder)
    {
        var hashes = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            using var stream = File.OpenRead(path);
            hashes.Add(Path.GetRelativePath(folder, path),
                Convert.ToHexString(SHA256.HashData(stream)));
        }
        return hashes;
    }
    private static async Task<string> ReadText(Uri uri, int maxBytes, CancellationToken ct)
    {
        if (uri.Scheme != "https" || uri.Host != "raw.githubusercontent.com")
            throw new InvalidOperationException("Unapproved script download host.");
        using var res = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        if (res.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("Script download exceeded allowed size.");
        await using var network = await res.Content.ReadAsStreamAsync(ct);
        using var data = new MemoryStream();
        var bytes = new byte[8192];
        int read;
        while ((read = await network.ReadAsync(bytes, ct)) != 0)
        {
            if (data.Length + read > maxBytes)
                throw new InvalidDataException("Script response exceeded allowed size.");
            data.Write(bytes, 0, read);
        }
        return new UTF8Encoding(false, true).GetString(data.ToArray());
    }
}
