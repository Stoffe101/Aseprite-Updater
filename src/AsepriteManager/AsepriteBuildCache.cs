using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AsepriteManager;

/// <summary>Local, user-owned cache of fully built Aseprite binaries. Never
/// redistributes executables. Each hit verifies every byte before staging,
/// then ManagerService verifies the executable's reported version before swap.
/// Does not include anything copied from the previous user's install.</summary>
public static class AsepriteBuildCache
{
    public sealed record FileDigest(long Length, string Sha256);
    public sealed record CacheManifest(int Schema, string Tag, string Version,
        string Architecture, DateTimeOffset CreatedUtc, Dictionary<string, FileDigest> Files);

    public static string Root => Path.Combine(ManagerService.DataRoot, "compiled-versions");
    public const int MaxVersions = 5;
    public const long BudgetBytes = 3L * 1024 * 1024 * 1024;
    private static readonly StringComparison PathCompare = StringComparison.OrdinalIgnoreCase;

    public static string Key(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 150)
            throw new ArgumentException("Invalid release identifier.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tag))).ToLowerInvariant()[..24];
    }
    private static string Entry(string tag) => Path.Combine(Root, Key(tag));

    public static (int Versions, long Bytes) Statistics()
    {
        if (!Directory.Exists(Root)) return (0, 0);
        int count = 0;
        long bytes = 0;
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            if (File.Exists(Path.Combine(dir, "manifest.json"))) count++;
            try { bytes += TotalBytes(dir); }
            catch { /* surface the still-readable cache if an entry disappears */ }
        }
        return (count, bytes);
    }
    public static bool HasBuild(string tag, string version)
    {
        try
        {
            var m = ReadManifest(Entry(tag));
            return m != null && m.Tag == tag && m.Version == version &&
                m.Architecture == System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        }
        catch { return false; }
    }

    public static bool TryStage(string tag, string version, string stage,
        Action<string> log)
    {
        if (Directory.Exists(stage))
            throw new IOException("Cache staging directory already exists.");
        var dir = Entry(tag);
        var manifest = ReadManifest(dir);
        if (manifest is null || manifest.Tag != tag || manifest.Version != version ||
            manifest.Architecture != System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString())
            return false;
        try
        {
            if (!ValidateFiles(Path.Combine(dir, "payload"), manifest.Files))
                throw new InvalidDataException("Compiled cache digest verification failed.");
            CopyTree(Path.Combine(dir, "payload"), stage);
            // Verify destination bytes too, guarding against incomplete copies.
            if (!ValidateFiles(stage, manifest.Files))
                throw new InvalidDataException("Staged compiled-cache copy failed integrity checks.");
            Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow);
            log($"FAST PATH: verified locally compiled Aseprite {version} ({manifest.Files.Count} files). No source clone or C++ compilation needed.");
            return true;
        }
        catch (Exception e)
        {
            log("Compiled version cache skipped: " + e.Message + "; rebuilding from official source.");
            TryDelete(stage);
            return false;
        }
    }

    public static bool HasPreviousInstallBackup(string installDir, string tag, string version)
    {
        return FindBackup(installDir, tag, version) != null;
    }

    /// <summary>
    /// Previously-installed builds already exist in retained full backup
    /// folders. Check their manager release marker, copy one into independent
    /// staging, then caller MUST run aseprite.exe --version before swapping.
    /// The backup is never moved or deleted and cannot be an arbitrary folder.
    /// </summary>
    public static bool TryStagePreviousInstall(string installDir, string tag, string version,
        string stage, Action<string> log)
    {
        if (Directory.Exists(stage))
            throw new IOException("Previous-install staging already exists.");
        var source = FindBackup(installDir, tag, version);
        if (source == null) return false;
        try
        {
            CopyTree(source, stage);
            log("FAST PATH: copied previous complete local installation for " +
                version + ". Executable version must still be verified before activation.");
            return true;
        }
        catch (Exception ex)
        {
            log("Previous-install backup couldn't be staged: " + ex.Message +
                ". Falling back to a source build.");
            TryDelete(stage);
            return false;
        }
    }

    private static string? FindBackup(string installDir, string tag, string version)
    {
        try
        {
            var full = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar);
            var parent = Path.GetDirectoryName(full)!;
            if (!Directory.Exists(parent)) return null;
            var mask = Path.GetFileName(full) + ".backup-*";
            foreach (var dir in Directory.EnumerateDirectories(parent, mask)
                .OrderByDescending(Directory.GetLastWriteTimeUtc).Take(80))
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                var executable = Path.Combine(dir, "aseprite.exe");
                var marker = Path.Combine(dir, "aseprite-manager.json");
                if (!File.Exists(executable) || !File.Exists(marker) ||
                    new FileInfo(marker).Length > 8_000) continue;
                using var metadata = JsonDocument.Parse(File.ReadAllText(marker));
                if (metadata.RootElement.GetProperty("version").GetString() == version &&
                    metadata.RootElement.GetProperty("release").GetString() == tag)
                    return dir;
            }
        }
        catch
        {
            // Corrupt or missing backup metadata should never prevent a
            // verified official source build.
        }
        return null;
    }

    public static void Store(string tag, string version, string buildFolder,
        Action<string> log)
    {
        Directory.CreateDirectory(Root);
        var dest = Entry(tag);
        if (Directory.Exists(dest))
        {
            // Never overwrite a previously trusted cache entry. A corrupt
            // cached item can be removed explicitly from Cache Management.
            if (HasBuild(tag, version))
            {
                log("Compiled version already recorded in local cache.");
                return;
            }
            log("Existing compiled cache entry cannot be overwritten. Clear build cache to replace it.");
            return;
        }
        var staging = Path.Combine(Root, ".pending-" + Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(staging, "payload");
        try
        {
            CopyTree(buildFolder, payload);
            var files = HashFiles(payload);
            var length = files.Values.Sum(f => f.Length);
            if (length > BudgetBytes)
            {
                log("Compiled package exceeds cache budget; skipping local cache.");
                return;
            }
            var m = new CacheManifest(1, tag, version,
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                DateTimeOffset.UtcNow, files);
            File.WriteAllText(Path.Combine(staging, "manifest.json"),
                JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
            Directory.Move(staging, dest);
            log($"Cached Aseprite {version} for future instant switching ({length / 1024 / 1024} MiB).");
            Prune(log);
        }
        catch (Exception e) { log("Could not cache build (installation continues): " + e.Message); }
        finally { TryDelete(staging); }
    }

    public static void Clear(Action<string> log)
    {
        if (!Directory.Exists(Root)) return;
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Refusing to clear a linked cache folder.");
            Directory.Delete(dir, recursive: true);
        }
        log("Cleared compiled Aseprite version cache. Installed versions and recovery backups are untouched.");
    }

    private static void Prune(Action<string> log)
    {
        var directories = Directory.EnumerateDirectories(Root)
            .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
            .OrderByDescending(Directory.GetLastWriteTimeUtc).ToList();
        long bytes = directories.Sum(TotalBytes);
        foreach (var dir in directories.Skip(1).Reverse().ToArray())
        {
            if (directories.Count <= MaxVersions && bytes <= BudgetBytes) break;
            var size = TotalBytes(dir);
            try { Directory.Delete(dir, true); bytes -= size; directories.Remove(dir); }
            catch (Exception ex) { log("Cache pruning skipped " + dir + ": " + ex.Message); }
        }
    }

    private static CacheManifest? ReadManifest(string dir)
    {
        var file = Path.Combine(dir, "manifest.json");
        if (!File.Exists(file) || new FileInfo(file).Length > 1_000_000)
            return null;
        var item = JsonSerializer.Deserialize<CacheManifest>(File.ReadAllText(file));
        return item?.Schema == 1 ? item : null;
    }

    private static bool ValidateFiles(string root, Dictionary<string, FileDigest> expected)
    {
        if (!Directory.Exists(root) || expected.Count == 0 || expected.Count > 10000) return false;
        var actual = HashFiles(root);
        return actual.Count == expected.Count && actual.All(x =>
            expected.TryGetValue(x.Key, out var entry) &&
            entry.Length == x.Value.Length &&
            string.Equals(entry.Sha256, x.Value.Sha256, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, FileDigest> HashFiles(string root)
    {
        var output = new Dictionary<string, FileDigest>(StringComparer.OrdinalIgnoreCase);
        Walk(root, root, output);
        return output;
    }
    private static void Walk(string root, string dir, Dictionary<string, FileDigest> hashes)
    {
        if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Reparse points cannot be cached.");
        foreach (var path in Directory.EnumerateFiles(dir))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked files cannot be cached.");
            using var fs = File.OpenRead(path);
            hashes.Add(Path.GetRelativePath(root, path), new FileDigest(
                fs.Length, Convert.ToHexString(SHA256.HashData(fs))));
        }
        foreach (var next in Directory.EnumerateDirectories(dir)) Walk(root, next, hashes);
    }
    private static void CopyTree(string source, string dest)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        Directory.CreateDirectory(dest);
        void Copy(string folder)
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked directories cannot be copied to version cache.");
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked files cannot be copied to version cache.");
                var relative = Path.GetRelativePath(source, file);
                var destination = Path.Combine(dest, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: false);
            }
            foreach (var d in Directory.EnumerateDirectories(folder)) Copy(d);
        }
        Copy(source);
    }
    private static long TotalBytes(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}
