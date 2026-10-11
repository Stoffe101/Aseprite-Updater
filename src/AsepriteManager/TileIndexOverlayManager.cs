using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AsepriteManager;

/// <summary>One curated MIT extension, separately opt-in because it executes Lua
/// and may register an Aseprite keyboard shortcut.</summary>
public static class TileIndexOverlayManager
{
    public const string Source = "https://github.com/Pixeltica/AsepriteExtensions/tree/main/TileIndexOverlay";
    public const string Id = "tile-index-overlay";
    public static string Folder => Path.Combine(WorkshopLibrary.ExtensionsFolder, "managed-" + Id);
    public static string ReceiptPath => WorkshopLibrary.RecordPath("extension-" + Id);

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(40) };
    static TileIndexOverlayManager() => Client.DefaultRequestHeaders.UserAgent.ParseAdd("AsepriteManager/0.5.0");

    public static bool Installed => Directory.Exists(Folder) && File.Exists(ReceiptPath);
    public static string? Revision()
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(ReceiptPath));
            return json.RootElement.GetProperty("Revision").GetString();
        }
        catch { return null; }
    }
    public static async Task<string> LatestSha(CancellationToken ct)
    {
        using var response = await GitHubApi.GetAsync(Client,
            "https://api.github.com/repos/Pixeltica/AsepriteExtensions/commits/main", ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var sha = doc.RootElement.GetProperty("sha").GetString() ?? "";
        if (!Regex.IsMatch(sha, "^[0-9a-f]{40}$", RegexOptions.IgnoreCase))
            throw new InvalidDataException("Unknown Tile Index repository revision.");
        return sha;
    }
    public static async Task InstallAsync(Action<string> log, CancellationToken ct)
    {
        if (System.Diagnostics.Process.GetProcessesByName("aseprite").Length != 0)
            throw new InvalidOperationException("Close Aseprite before installing extensions.");
        var sha = await LatestSha(ct);
        if (Installed && Revision() == sha)
        {
            log("Tile Index Overlay is current.");
            return;
        }
        var files = new[] {
            "TileIndexOverlay/package.json",
            "TileIndexOverlay/tile-index-overlay.lua",
            "TileIndexOverlay/tile-index-overlay.aseprite-keys",
            "LICENSE"
        };
        var gathered = new Dictionary<string, string>();
        foreach (var name in files)
        {
            var url = "https://raw.githubusercontent.com/Pixeltica/AsepriteExtensions/" +
                sha + "/" + name;
            using var response = await Client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 150_000)
                throw new InvalidDataException("Unexpected Tile Index source size.");
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 150_000) throw new InvalidDataException("Unexpected Tile Index source size.");
            gathered[Path.GetFileName(name)] = new UTF8Encoding(false, true).GetString(bytes);
        }
        if (!gathered["package.json"].Contains("tile-index-overlay", StringComparison.Ordinal) ||
            !gathered["LICENSE"].Contains("Permission is hereby granted", StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected extension metadata/license.");
        Directory.CreateDirectory(WorkshopLibrary.ExtensionsFolder);
        var stage = Path.Combine(WorkshopLibrary.ExtensionsFolder, ".tileindex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, content) in gathered)
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                await File.WriteAllBytesAsync(Path.Combine(stage, name), bytes, ct);
                hashes[name] = Convert.ToHexString(SHA256.HashData(bytes));
            }
            ct.ThrowIfCancellationRequested();
            WorkshopLibrary.ReplaceManaged(Folder, stage, ReceiptPath,
                new LibraryReceipt("extension-" + Id, sha, hashes), log);
            log("Tile Index Overlay installed. Restart Aseprite. It adds a Layer > New overlay command and may introduce a keyboard shortcut.");
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    public static void Remove(Action<string> log)
    {
        WorkshopLibrary.ValidateOwned(Folder, ReceiptPath);
        Directory.CreateDirectory(WorkshopLibrary.BackupsFolder);
        var backup = Path.Combine(WorkshopLibrary.BackupsFolder,
            "tile-index-overlay-" + Guid.NewGuid().ToString("N"));
        Directory.Move(Folder, backup);
        File.Delete(ReceiptPath);
        log("Tile Index Overlay removed; preserved at " + backup);
    }
}
