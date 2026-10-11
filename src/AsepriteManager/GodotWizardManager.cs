using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AsepriteManager;

/// <summary>Opt-in, project-scoped installation of the MIT Godot 4 Aseprite Wizard.
/// Never edits project.godot or enables the plugin automatically. No Godot
/// importer runs until the user enables it in the editor.</summary>
public sealed class GodotWizardManager
{
    public const string RepositoryUrl = "https://github.com/viniciusgerevini/godot-aseprite-wizard";
    public const string Branch = "godot_4";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(3) };
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".gd", ".uid", ".tscn", ".tres", ".cfg", ".png", ".svg", ".json", ".md", ".txt", ".import", ".gdshader" };

    static GodotWizardManager() => Client.DefaultRequestHeaders.UserAgent.ParseAdd("AsepriteManager/0.5.0");

    public sealed record Receipt(string Revision, Dictionary<string,string> Files);
    public sealed record Status(bool Installed, bool Managed, bool Modified, string? Revision, string PluginPath);

    public static string VerifyProject(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Choose a Godot project folder.");
        var project = Path.Combine(Path.GetFullPath(root), "project.godot");
        if (!File.Exists(project)) throw new FileNotFoundException("The selected folder does not contain project.godot.");
        if (!File.ReadAllText(project).Contains("config_version=5", StringComparison.Ordinal))
            throw new InvalidOperationException("Only a Godot 4 project is supported. No files changed.");
        var dir = Path.GetDirectoryName(project)!;
        var addon = Path.Combine(dir, "addons");
        if (Directory.Exists(addon) && (File.GetAttributes(addon) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("A symlinked addons directory is not supported.");
        return dir;
    }
    public static string PluginFolder(string project) =>
        Path.Combine(VerifyProject(project), "addons", "AsepriteWizard");
    public static string ReceiptPath(string project)
    {
        var canonical = VerifyProject(project).TrimEnd('\\', '/').ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
        return Path.Combine(ManagerService.DataRoot, "godot-wizard-records", key + ".json");
    }
    public static Status Inspect(string project)
    {
        var folder = PluginFolder(project);
        var receipt = TryRead(ReceiptPath(project));
        if (!Directory.Exists(folder))
            return new Status(false, false, false, null, folder);
        if (receipt == null) return new Status(true, false, true, null, folder);
        var hashes = Fingerprints(folder);
        var dirty = hashes.Count != receipt.Files.Count ||
            hashes.Any(f => !receipt.Files.TryGetValue(f.Key, out var recorded) ||
                !string.Equals(recorded, f.Value, StringComparison.OrdinalIgnoreCase));
        return new Status(true, true, dirty, receipt.Revision, folder);
    }

    public static async Task<string> LatestCommitAsync(CancellationToken ct)
    {
        using var response = await Client.GetAsync(
            "https://api.github.com/repos/viniciusgerevini/godot-aseprite-wizard/commits/godot_4", ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var sha = doc.RootElement.GetProperty("sha").GetString() ?? "";
        if (!Regex.IsMatch(sha, "^[0-9a-f]{40}$", RegexOptions.IgnoreCase))
            throw new InvalidDataException("Invalid upstream revision.");
        return sha;
    }

    public static async Task InstallOrUpdateAsync(string project, Action<string> log, CancellationToken ct)
    {
        var root = VerifyProject(project);
        RejectRunningGodot();
        var status = Inspect(root);
        if (status.Installed && (!status.Managed || status.Modified))
            throw new InvalidOperationException("The existing AsepriteWizard plugin is untracked or modified. Back it up and remove it manually before using Manager.");
        var sha = await LatestCommitAsync(ct);
        if (status.Installed && status.Revision == sha)
        {
            log("Godot Aseprite Wizard is already current.");
            return;
        }
        log("Downloading Aseprite Wizard Godot 4 source at commit " + sha);
        var url = new Uri("https://codeload.github.com/viniciusgerevini/godot-aseprite-wizard/zip/" + sha);
        byte[] archive = await DownloadLimitedAsync(url, 22_000_000, ct);
        var addons = Path.Combine(root, "addons");
        Directory.CreateDirectory(addons);
        var stage = Path.Combine(addons, ".asepritewizard-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var target = Path.Combine(addons, "AsepriteWizard");
        var receiptPath = ReceiptPath(root);
        try
        {
            ExtractWizardArchive(archive, stage);
            var newReceipt = new Receipt(sha, Fingerprints(stage));
            ct.ThrowIfCancellationRequested();
            // An inactive editor plugin cannot mutate project.godot or game
            // source until explicitly enabled by the user in Godot.
            if (Directory.Exists(target))
            {
                var existing = Inspect(root);
                if (!existing.Managed || existing.Modified)
                    throw new InvalidOperationException("Plugin files changed while downloading. Installation stopped.");
            }
            string? backup = null;
            if (Directory.Exists(target))
            {
                backup = Path.Combine(ManagerService.DataRoot, "godot-wizard-backups",
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".zip");
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                ZipFile.CreateFromDirectory(target, backup);
            }
            var old = Path.Combine(addons, ".asepritewizard-old-" + Guid.NewGuid().ToString("N"));
            var hadOld = Directory.Exists(target);
            if (hadOld) Directory.Move(target, old);
            try
            {
                Directory.Move(stage, target);
                Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
                var tmp = receiptPath + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllText(tmp, JsonSerializer.Serialize(newReceipt));
                    File.Move(tmp, receiptPath, overwrite: true);
                }
                finally { if (File.Exists(tmp)) File.Delete(tmp); }
                if (hadOld) Directory.Delete(old, recursive: true);
            }
            catch
            {
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                if (hadOld && Directory.Exists(old)) Directory.Move(old, target);
                throw;
            }
            log("Installed Godot 4 Aseprite Wizard at " + target +
                (backup == null ? "" : ". Previous version backed up to " + backup) +
                ". Enable manually in Godot Project > Project Settings > Plugins.");
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    public static void Uninstall(string project, Action<string> log)
    {
        var root = VerifyProject(project);
        RejectRunningGodot();
        var status = Inspect(root);
        if (!status.Installed || !status.Managed || status.Modified)
            throw new InvalidOperationException("Only intact Manager-installed plugins can be removed. Project contents were not changed.");
        var backup = Path.Combine(ManagerService.DataRoot, "godot-wizard-backups",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        ZipFile.CreateFromDirectory(status.PluginPath, backup);
        Directory.Delete(status.PluginPath, recursive: true);
        File.Delete(ReceiptPath(root));
        log("Removed Aseprite Wizard from " + root + ". Recoverable plugin backup: " + backup +
            ". Imported Godot resources were not touched.");
    }

    private static void RejectRunningGodot()
    {
        if (System.Diagnostics.Process.GetProcesses().Any(p =>
        {
            try { return p.ProcessName.StartsWith("Godot", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }))
            throw new InvalidOperationException("Close Godot before changing its editor plugin files.");
    }
    public static void ExtractWizardArchive(byte[] zipBytes, string stage)
    {
        using var data = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(data);
        if (zip.Entries.Count > 1400) throw new InvalidDataException("Too many archive entries.");
        const string marker = "/addons/AsepriteWizard/";
        int files = 0;
        long total = 0;
        var stageFull = Path.GetFullPath(stage).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            var split = path.IndexOf(marker, StringComparison.Ordinal);
            if (split < 0 || path.EndsWith('/')) continue;
            var relative = path[(split + marker.Length)..];
            if (string.IsNullOrWhiteSpace(relative)) continue;
            var pieces = relative.Split('/');
            if (pieces.Any(s => s is "" or "." or "..") || Path.IsPathRooted(relative))
                throw new InvalidDataException("Archive path is not safe.");
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("Symbolic links not supported.");
            var ext = Path.GetExtension(relative);
            if (!AllowedExtensions.Contains(ext) && Path.GetFileName(relative) != "LICENSE")
                throw new InvalidDataException("Unexpected plugin file type: " + relative);
            if (entry.Length > 3_000_000 || (total += entry.Length) > 48_000_000)
                throw new InvalidDataException("Plugin payload exceeds safe limits.");
            var destination = Path.GetFullPath(Path.Combine(stage,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(stageFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Archive attempted directory traversal.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
            files++;
        }
        if (files < 10 || !File.Exists(Path.Combine(stage, "plugin.cfg")) ||
            !File.Exists(Path.Combine(stage, "plugin.gd")) ||
            !File.Exists(Path.Combine(stage, "LICENSE")))
            throw new InvalidDataException("Upstream Godot addon was missing required files.");
        var cfg = File.ReadAllText(Path.Combine(stage, "plugin.cfg"));
        if (!cfg.Contains("Aseprite Wizard", StringComparison.Ordinal))
            throw new InvalidDataException("Plugin identity did not match Aseprite Wizard.");
    }
    private static async Task<byte[]> DownloadLimitedAsync(Uri url, int max, CancellationToken ct)
    {
        if (url.Scheme != "https" || url.Host != "codeload.github.com")
            throw new InvalidOperationException("Unexpected download host.");
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > max)
            throw new InvalidDataException("Source download exceeds allowed size.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + n > max) throw new InvalidDataException("Source download exceeded allowed size.");
            output.Write(buffer, 0, n);
        }
        return output.ToArray();
    }
    private static Receipt? TryRead(string file)
    {
        try { return JsonSerializer.Deserialize<Receipt>(File.ReadAllText(file)); }
        catch { return null; }
    }
    private static Dictionary<string, string> Fingerprints(string dir)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Symbolic links are not supported in managed plugins.");
            using var stream = File.OpenRead(file);
            hashes.Add(Path.GetRelativePath(dir, file),
                Convert.ToHexString(SHA256.HashData(stream)));
        }
        return hashes;
    }
}
