using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace AsepriteManager;

public sealed record ThemeDefinition(
    string Id, string Name, string Description, string Owner, string Repository,
    string? ReleaseAssetName = null)
{
    public string Website => $"https://github.com/{Owner}/{Repository}";
    public override string ToString() => Name;
}

public sealed record ThemeSource(ThemeDefinition Definition, string Revision,
    Uri DownloadUri, string? ExpectedSha256);

public sealed record ThemeRecord(string Id, string Revision, Dictionary<string, string> Files);

public sealed class ThemeCatalog
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(3) };
    static ThemeCatalog()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Stoffe101-AsepriteManager/0.3.0");
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    // Small, audited list: names are explicitly sourced from verified public repos,
    // not a blanket search of arbitrary scripts advertised as "themes".
    public static readonly IReadOnlyList<ThemeDefinition> Curated = new[]
    {
        new ThemeDefinition("cat-mocha", "Catppuccin Mocha", "Soft dark pastel theme", "catppuccin", "aseprite", "mocha.aseprite-extension"),
        new ThemeDefinition("cat-macchiato", "Catppuccin Macchiato", "Balanced dark pastel theme", "catppuccin", "aseprite", "macchiato.aseprite-extension"),
        new ThemeDefinition("cat-frappe", "Catppuccin Frappé", "Cool muted dark theme", "catppuccin", "aseprite", "frappe.aseprite-extension"),
        new ThemeDefinition("cat-latte", "Catppuccin Latte", "Light pastel theme", "catppuccin", "aseprite", "latte.aseprite-extension"),
        new ThemeDefinition("dracula", "Dracula", "Dark purple-accented classic", "dracula", "aseprite"),
        new ThemeDefinition("monaki", "Monaki", "Dark theme with refined colors", "el-falso", "monaki-theme")
    };

    public async Task<ThemeSource> ResolveAsync(ThemeDefinition theme, CancellationToken ct)
    {
        if (!Curated.Contains(theme)) throw new InvalidOperationException("Unknown curated theme.");
        if (theme.ReleaseAssetName != null)
        {
            using var doc = await FetchJsonAsync(
                $"https://api.github.com/repos/{theme.Owner}/{theme.Repository}/releases/latest", ct);
            var release = doc.RootElement;
            var tag = release.GetProperty("tag_name").GetString() ??
                throw new InvalidDataException("Theme release tag missing.");
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(),
                    theme.ReleaseAssetName, StringComparison.OrdinalIgnoreCase)) continue;
                var url = asset.GetProperty("browser_download_url").GetString();
                if (url == null) break;
                var uri = new Uri(url);
                if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com")
                    throw new InvalidDataException("Theme release asset must be hosted on GitHub.");
                string? digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() : null;
                var sha = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true
                    ? digest["sha256:".Length..] : null;
                return new ThemeSource(theme, $"{tag}:{sha ?? asset.GetProperty("id").ToString()}", uri, sha);
            }
            throw new InvalidDataException($"Could not locate official {theme.ReleaseAssetName} release asset.");
        }

        using var repo = await FetchJsonAsync(
            $"https://api.github.com/repos/{theme.Owner}/{theme.Repository}", ct);
        var defaultBranch = repo.RootElement.GetProperty("default_branch").GetString() ??
            throw new InvalidDataException("Theme default branch missing.");
        if (defaultBranch is not ("main" or "master"))
            throw new InvalidDataException("Unexpected theme default branch.");
        using var commit = await FetchJsonAsync(
            $"https://api.github.com/repos/{theme.Owner}/{theme.Repository}/commits/{defaultBranch}", ct);
        var id = commit.RootElement.GetProperty("sha").GetString() ??
            throw new InvalidDataException("Theme commit SHA missing.");
        if (id.Length != 40 || !id.All(Uri.IsHexDigit))
            throw new InvalidDataException("Theme commit SHA was not valid.");
        return new ThemeSource(theme, id,
            new Uri($"https://codeload.github.com/{theme.Owner}/{theme.Repository}/zip/{id}"), null);
    }

    public async Task<byte[]> DownloadAsync(ThemeSource source, CancellationToken ct)
    {
        using var response = await Client.GetAsync(source.DownloadUri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 30_000_000)
            throw new InvalidDataException("Theme download exceeds maximum allowed size.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var data = new MemoryStream();
        var buffer = new byte[65536];
        while (true)
        {
            var n = await stream.ReadAsync(buffer, ct);
            if (n == 0) break;
            if (data.Length + n > 30_000_000) throw new InvalidDataException("Theme archive too large.");
            data.Write(buffer, 0, n);
        }
        var bytes = data.ToArray();
        if (source.ExpectedSha256 != null &&
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)),
                source.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Theme release SHA-256 verification failed.");
        return bytes;
    }

    private static async Task<JsonDocument> FetchJsonAsync(string url, CancellationToken ct)
    {
        using var response = await Client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
    }
}

public sealed class ThemeManager
{
    public static string ExtensionsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aseprite", "extensions");
    public static string BackupDir => Path.Combine(ManagerService.DataRoot, "theme-backups");

    private readonly ThemeCatalog _catalog = new();
    private readonly Action<string> _log;

    public ThemeManager(Action<string> log) => _log = log;

    private static string Folder(ThemeDefinition theme) =>
        Path.Combine(ExtensionsDir, "managed-" + theme.Id);
    private static string RecordFile(ThemeDefinition theme) =>
        Path.Combine(ManagerService.DataRoot, "theme-records", theme.Id + ".json");

    public bool IsInstalled(ThemeDefinition theme) =>
        File.Exists(RecordFile(theme)) && Directory.Exists(Folder(theme));

    public string InstalledRevision(ThemeDefinition theme)
    {
        try
        {
            var record = JsonSerializer.Deserialize<ThemeRecord>(File.ReadAllText(RecordFile(theme)));
            return record?.Revision ?? "";
        }
        catch { return ""; }
    }

    public async Task<IReadOnlyDictionary<string, bool>> CheckUpdatesAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, bool>();
        foreach (var theme in ThemeCatalog.Curated.Where(IsInstalled))
        {
            var latest = await _catalog.ResolveAsync(theme, ct);
            result[theme.Id] = !string.Equals(latest.Revision,
                InstalledRevision(theme), StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    public async Task InstallOrUpdateAsync(ThemeDefinition theme, CancellationToken ct)
    {
        if (System.Diagnostics.Process.GetProcessesByName("aseprite").Length > 0)
            throw new InvalidOperationException("Close Aseprite before installing or updating themes.");

        var target = Folder(theme);
        var recordPath = RecordFile(theme);
        if (Directory.Exists(target) && !File.Exists(recordPath))
            throw new InvalidOperationException("A theme folder already exists but is not managed by this app; nothing was overwritten.");

        var previous = ReadRecord(theme);
        if (Directory.Exists(target) && previous == null)
            throw new InvalidOperationException(
                "The theme installation has no readable manager manifest; to protect files it cannot be overwritten.");
        if (previous != null && Directory.Exists(target))
        {
            foreach (var (relative, expected) in previous.Files)
            {
                var file = Path.Combine(target, relative);
                if (!File.Exists(file) ||
                    !string.Equals(Sha256File(file), expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"{theme.Name} has local edits. To avoid overwriting your changes, the manager stopped. Back up or rename your customized theme first.");
            }
            var previousFiles = new HashSet<string>(previous.Files.Keys, StringComparer.OrdinalIgnoreCase);
            if (Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(target, f))
                .Any(f => !previousFiles.Contains(f)))
                throw new InvalidOperationException(
                    $"{theme.Name} contains extra custom files, so automatic updating was blocked to protect them.");
        }

        var resolved = await _catalog.ResolveAsync(theme, ct);
        if (previous != null && previous.Revision == resolved.Revision && Directory.Exists(target))
        {
            _log($"{theme.Name} is already current.");
            return;
        }

        _log($"Downloading {theme.Name} from {theme.Website}...");
        var zipBytes = await _catalog.DownloadAsync(resolved, ct);
        var stage = Path.Combine(ManagerService.DataRoot, "theme-staging", Guid.NewGuid().ToString("N"));
        string? backup = null;
        Directory.CreateDirectory(stage);
        try
        {
            UnpackVerifiedTheme(zipBytes, stage);
            var hashes = Directory.GetFiles(stage, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(stage, f), Sha256File,
                    StringComparer.OrdinalIgnoreCase);
            var next = new ThemeRecord(theme.Id, resolved.Revision, hashes);
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(ExtensionsDir);
            if (Directory.Exists(target))
            {
                Directory.CreateDirectory(BackupDir);
                backup = Path.Combine(BackupDir, $"{theme.Id}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
                Directory.Move(target, backup);
            }
            try
            {
                Directory.Move(stage, target);
                Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
                var tmp = recordPath + ".tmp";
                await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(next), CancellationToken.None);
                File.Move(tmp, recordPath, overwrite: true);
            }
            catch
            {
                if (Directory.Exists(target)) Directory.Delete(target, true);
                if (backup != null && Directory.Exists(backup)) Directory.Move(backup, target);
                // A manifest written before another step failed would be stale.
                // The previous manifest remains at recordPath unless the atomic
                // final File.Move has already succeeded; don't overwrite it here.
                throw;
            }
            _log($"Installed {theme.Name}. " +
                (backup == null ? "" : $"Previous theme retained at {backup}. ") +
                "Choose it in Edit > Preferences > Theme.");
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    private static ThemeRecord? ReadRecord(ThemeDefinition theme)
    {
        try { return JsonSerializer.Deserialize<ThemeRecord>(File.ReadAllText(RecordFile(theme))); }
        catch { return null; }
    }

    private static string Sha256File(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static void UnpackVerifiedTheme(byte[] archive, string destination)
    {
        using var mem = new MemoryStream(archive);
        using var zip = new ZipArchive(mem, ZipArchiveMode.Read);
        if (zip.Entries.Count > 350)
            throw new InvalidDataException("Theme archive contains too many files.");
        var packages = zip.Entries.Where(e =>
            string.Equals(Path.GetFileName(e.FullName.Replace('\\', '/')),
                "package.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (packages.Length != 1) throw new InvalidDataException("Theme archive must contain exactly one package.json.");
        var prefix = packages[0].FullName[..^"package.json".Length];
        var validFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            ".png", ".xml", ".json", ".aseprite-data", ".ttf", ".otf",
            ".txt", ".md", ".gpl", ".pal", ".ase", ".aseprite", ".license"
        };
        long total = 0;
        foreach (var item in zip.Entries)
        {
            if (!item.FullName.StartsWith(prefix, StringComparison.Ordinal) ||
                item.FullName.EndsWith('/')) continue;
            var relative = item.FullName[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar)
                .Any(s => s is "" or "." or ".."))
                throw new InvalidDataException("Unsafe path in theme archive.");
            if ((item.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("Theme archives may not include symbolic links.");
            var ext = Path.GetExtension(relative);
            if (!validFileNames.Contains(ext) &&
                !string.Equals(Path.GetFileName(relative), "LICENSE", StringComparison.OrdinalIgnoreCase))
                continue; // Never install scripts or binaries bundled with third-party archives.
            total += item.Length;
            if (total > 40_000_000 || item.Length > 12_000_000)
                throw new InvalidDataException("Theme archive extraction limit exceeded.");
            var path = Path.GetFullPath(Path.Combine(destination, relative));
            if (!path.StartsWith(Path.GetFullPath(destination).TrimEnd('\\') +
                Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Theme archive attempted directory traversal.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            item.ExtractToFile(path, overwrite: false);
        }

        var manifest = Path.Combine(destination, "package.json");
        if (!File.Exists(manifest)) throw new InvalidDataException("Missing theme package.json.");
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        if (!doc.RootElement.TryGetProperty("contributes", out var contributes) ||
            !contributes.TryGetProperty("themes", out var themes) ||
            themes.ValueKind != JsonValueKind.Array || themes.GetArrayLength() == 0)
            throw new InvalidDataException("This Aseprite extension does not advertise a theme.");
        foreach (var t in themes.EnumerateArray())
        {
            var loc = t.GetProperty("path").GetString() ?? ".";
            var themePath = Path.GetFullPath(Path.Combine(destination, loc));
            if (!(themePath + Path.DirectorySeparatorChar).StartsWith(
                Path.GetFullPath(destination).TrimEnd('\\') + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsafe theme manifest path.");
            if (!File.Exists(Path.Combine(themePath, "theme.xml")))
                throw new InvalidDataException("Theme metadata references missing theme.xml.");
        }
    }
}
