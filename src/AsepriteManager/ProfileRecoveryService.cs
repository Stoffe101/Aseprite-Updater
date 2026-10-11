using System.IO;
using System.IO.Compression;

namespace AsepriteManager;

/// <summary>
/// Opt-in, whole-profile recovery. Validate the full archive before touching
/// Aseprite settings; extract into a sibling directory and atomically rename.
/// No live user profile is deleted: the old one is kept alongside it.
/// </summary>
public static class ProfileRecoveryService
{
    public sealed record Preview(int FileCount, long ExpandedBytes, bool ContainsBrushes,
        bool ContainsSettings, bool ContainsExtensions);

    private const int MaxEntries = 20000;
    private const long MaxFileSize = 300_000_000;
    private const long MaxTotalBytes = 1_000_000_000;

    public static Preview Inspect(string archivePath)
    {
        if (!File.Exists(archivePath)) throw new FileNotFoundException("Choose a profile ZIP.", archivePath);
        using var zip = ZipFile.OpenRead(archivePath);
        return Validate(zip);
    }

    private static Preview Validate(ZipArchive zip)
    {
        if (zip.Entries.Count is 0 or > MaxEntries)
            throw new InvalidDataException("Backup contains no files or too many archive entries.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        var count = 0;
        foreach (var entry in zip.Entries)
        {
            var original = entry.FullName;
            if (original.EndsWith('/')) continue;
            var path = original.Replace('\\', '/');
            var parts = path.Split('/');
            if (path.Length > 500 || path.StartsWith('/') || path.Contains(':') ||
                parts.Any(p => p is "" or "." or ".."))
                throw new InvalidDataException("Invalid path in profile ZIP: " + path);
            // Prevent NTFS reparse points, UNIX symlinks and non-file entries.
            var unixKind = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixKind != 0 && unixKind != 0x8000)
                throw new InvalidDataException("Profile archive contains links or unsupported file types.");
            if (!names.Add(path))
                throw new InvalidDataException("Profile archive contains duplicate file names.");
            if (entry.Length > MaxFileSize || (size += entry.Length) > MaxTotalBytes)
                throw new InvalidDataException("Profile backup exceeds recovery size limits.");
            count++;
        }
        if (count == 0) throw new InvalidDataException("The archive contained no files.");
        // Require a recognizable Aseprite user profile, not arbitrary ZIP content.
        if (!names.Contains("aseprite.ini") &&
            !names.Contains("user.aseprite-brushes") &&
            !names.Any(n => n.StartsWith("extensions/", StringComparison.OrdinalIgnoreCase)) &&
            !names.Any(n => n.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The ZIP does not appear to contain Aseprite settings.");
        return new Preview(count, size,
            names.Contains("user.aseprite-brushes"),
            names.Contains("aseprite.ini"),
            names.Any(n => n.StartsWith("extensions/", StringComparison.OrdinalIgnoreCase)));
    }

    public static string Restore(string archivePath, string profileFolder, Action<string> log)
    {
        if (System.Diagnostics.Process.GetProcessesByName("aseprite").Length != 0)
            throw new InvalidOperationException("Close Aseprite before restoring a profile.");
        var profile = Path.GetFullPath(profileFolder).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetPathRoot(profile)!.TrimEnd(Path.DirectorySeparatorChar);
        if (profile.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cannot restore a drive root.");
        var parent = Path.GetDirectoryName(profile)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
        if (File.Exists(archivePath) && Path.GetFullPath(archivePath).StartsWith(
            profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Recovery ZIP must be outside the profile folder.");
        if (Directory.Exists(profile) &&
            (File.GetAttributes(profile) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Cannot replace a linked user profile.");

        var stage = Path.Combine(parent, ".aseprite-restore-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(parent, "Aseprite.before-restore-" +
            DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(stage);
        bool oldMoved = false;
        bool newMoved = false;
        try
        {
            using (var zip = ZipFile.OpenRead(archivePath))
            {
                var inspected = Validate(zip);
                log($"Validated profile archive: {inspected.FileCount} files, {inspected.ExpandedBytes} bytes.");
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith('/')) continue;
                    var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    var destination = Path.GetFullPath(Path.Combine(stage, relative));
                    if (!destination.StartsWith(stage + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Profile archive attempted directory traversal.");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: false);
                }
            }
            if (Directory.Exists(profile))
            {
                Directory.Move(profile, backup);
                oldMoved = true;
            }
            try
            {
                Directory.Move(stage, profile);
                newMoved = true;
                if (!Directory.EnumerateFiles(profile, "*", SearchOption.AllDirectories).Any())
                    throw new InvalidDataException("The restored profile was empty.");
            }
            catch
            {
                if (newMoved && Directory.Exists(profile))
                {
                    var failed = Path.Combine(parent,
                        ".aseprite-restore-failed-" + Guid.NewGuid().ToString("N"));
                    Directory.Move(profile, failed);
                    log("Failed restored files preserved for inspection at " + failed);
                }
                if (oldMoved && Directory.Exists(backup) && !Directory.Exists(profile))
                    Directory.Move(backup, profile);
                throw;
            }
            log(oldMoved
                ? "Profile restored; your entire previous profile is retained at " + backup
                : "New Aseprite profile restored (there was no previous profile).");
            return oldMoved ? backup : "";
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }
}
