using System.IO;
using System.IO.Compression;

namespace AsepriteManager;

/// <summary>Read-only export of the entire Aseprite user profile, including
/// custom brushes and scripts. No restoration or profile mutation is performed.
/// The archive is user-owned and may contain private settings/project sessions.</summary>
public static class ProfileBackupService
{
    public static string AsepriteProfile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aseprite");

    public static int CreateBackup(string source, string destination)
    {
        var full = Path.GetFullPath(source);
        var file = Path.GetFullPath(destination);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        if (File.Exists(file)) throw new IOException("Backup file exists; choose a new file name.");
        if (file.StartsWith(full.TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Save the backup outside the Aseprite profile.");

        var found = new List<(string Absolute, string Relative, long Length)>();
        long size = 0;
        void Walk(string dir)
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Aseprite profile contains a linked folder. Backup stopped rather than following links.");
            foreach (var child in Directory.EnumerateFileSystemEntries(dir))
            {
                var attr = File.GetAttributes(child);
                if ((attr & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked file or folder detected; backup stopped.");
                if ((attr & FileAttributes.Directory) != 0) { Walk(child); continue; }
                var length = new FileInfo(child).Length;
                if (length > 300_000_000 || (size += length) > 1_000_000_000 ||
                    found.Count > 20_000)
                    throw new InvalidDataException("Aseprite profile is too large for one backup. Review caches and files first.");
                found.Add((child, Path.GetRelativePath(full, child), length));
            }
        }
        Walk(full);
        if (found.Count == 0) throw new InvalidDataException("Aseprite profile is empty; nothing to back up.");
        var parent = Path.GetDirectoryName(file)!;
        Directory.CreateDirectory(parent);
        try
        {
            using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                foreach (var item in found)
                {
                    var entry = archive.CreateEntry(item.Relative.Replace('\\', '/'),
                        CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    using var input = File.OpenRead(item.Absolute);
                    input.CopyTo(entryStream);
                }
            // Verify ZIP central directory and entry count after writing.
            using var check = ZipFile.OpenRead(file);
            if (check.Entries.Count != found.Count)
                throw new InvalidDataException("Backup ZIP verification failed.");
            return found.Count;
        }
        catch
        {
            if (File.Exists(file)) File.Delete(file);
            throw;
        }
    }
}
