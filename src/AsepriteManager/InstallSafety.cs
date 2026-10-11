using System.IO;

namespace AsepriteManager;

/// <summary>
/// Safety boundary for an Aseprite source-built installation. It does not touch
/// Documents, Pictures, Roaming Aseprite settings or any user profile projects.
/// The COMPLETE previous install directory is retained after every successful swap.
/// </summary>
public static class InstallSafety
{
    private static readonly HashSet<string> SpriteExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ase", ".aseprite", ".aseprite-backup", ".png", ".gif", ".jpg",
        ".jpeg", ".webp", ".bmp", ".tga", ".psd", ".pal", ".gpl"
    };

    public static void ValidateDestination(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
            throw new InvalidOperationException("Choose an installation directory.");
        var full = Path.GetFullPath(installDir);
        var root = Path.GetPathRoot(full)!;
        if (full.TrimEnd('\\') == root.TrimEnd('\\'))
            throw new InvalidOperationException("Installing over a drive root is not allowed.");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var protectedDirs = new[] { home,
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ManagerService.DataRoot
        };
        if (protectedDirs.Where(x => !string.IsNullOrWhiteSpace(x)).Any(x =>
            string.Equals(Path.GetFullPath(x).TrimEnd('\\'), full.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Choose a dedicated Aseprite application folder, not a personal or system folder.");
        if (full.Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A Steam-managed installation cannot be replaced by this source-build manager.");
        if (Directory.Exists(full) &&
            Directory.EnumerateFileSystemEntries(full).Any() &&
            !File.Exists(Path.Combine(full, "aseprite.exe")))
            throw new InvalidOperationException("The target folder has files but no aseprite.exe. Pick an empty folder or an existing source-built installation.");
    }

    public static void PreserveUserContent(string oldInstall, string stage, Action<string>? log = null)
    {
        if (!Directory.Exists(oldInstall)) return;
        var oldData = Path.Combine(oldInstall, "data");
        foreach (var folder in new[] { "extensions", "themes", "palettes" })
            CopyOnlyMissing(Path.Combine(oldData, folder), Path.Combine(stage, "data", folder), log);

        // Many people keep work beside an executable. Never overwrite staged files,
        // but make project/image files available in the new installation.
        // All other legacy files are still available in the retained full backup.
        foreach (var file in Directory.EnumerateFiles(oldInstall, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(oldInstall, file);
            if (relative.StartsWith("data" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) continue;
            if (relative.StartsWith(".git" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) continue;
            var ext = Path.GetExtension(file);
            if (!SpriteExtensions.Contains(ext) &&
                !string.Equals(Path.GetFileName(file), "aseprite.ini",
                    StringComparison.OrdinalIgnoreCase))
                continue;
            var dst = Path.Combine(stage, relative);
            CopyOneMissing(file, dst, log);
        }
    }

    private static void CopyOnlyMissing(string source, string dest, Action<string>? log)
    {
        if (!Directory.Exists(source)) return;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            CopyOneMissing(file, Path.Combine(dest, Path.GetRelativePath(source, file)), log);
    }

    private static void CopyOneMissing(string file, string dest, Action<string>? log)
    {
        if (File.Exists(dest))
        {
            // The previous complete installation backup still contains the custom
            // version of a conflicting file, so no data is discarded.
            log?.Invoke("Keeping newer packaged file; original available in backup: " + dest);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(file, dest, overwrite: false);
    }

    public static string NewBackupPath(string installDir)
    {
        var parent = Path.GetDirectoryName(installDir)!;
        var baseName = Path.GetFileName(installDir);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var name = $"{baseName}.backup-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            var backup = Path.Combine(parent, name);
            if (!Directory.Exists(backup) && !File.Exists(backup)) return backup;
        }
        throw new IOException("Could not allocate a unique backup directory.");
    }

    public static void SwapVerifiedInstallation(string stage, string installDir,
        Func<bool> verifyInstalled, Action<string> log)
    {
        ValidateDestination(installDir);
        if (!Directory.Exists(stage)) throw new DirectoryNotFoundException(stage);
        string? backup = null;
        var renamedStage = false;
        if (Directory.Exists(installDir))
        {
            backup = NewBackupPath(installDir);
            Directory.Move(installDir, backup);
        }
        try
        {
            Directory.Move(stage, installDir);
            renamedStage = true;
            if (!verifyInstalled()) throw new InvalidDataException("The installed executable failed post-swap verification.");
            log(backup is null ? "Fresh installation verified." :
                $"Installation verified; FULL previous installation retained at {backup}");
        }
        catch
        {
            // A failed post-swap verification is restored, not silently accepted.
            // Keep the failed install for diagnostics only if it was activated.
            if (renamedStage && Directory.Exists(installDir))
            {
                var failed = NewBackupPath(installDir) + ".failed";
                Directory.Move(installDir, failed);
                log("Invalid new installation preserved for diagnosis at " + failed);
            }
            if (backup != null && Directory.Exists(backup) && !Directory.Exists(installDir))
                Directory.Move(backup, installDir);
            throw;
        }
    }
}
