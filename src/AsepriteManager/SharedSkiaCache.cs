using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace AsepriteManager;

/// <summary>
/// The official Aseprite build.sh puts downloaded Skia libraries under
/// .deps/skia-mNNN and deletes them along with the throwaway source tree.
/// An NTFS junction into a user-owned persistent dependency cache retains
/// those assets without changing upstream source or symlinking user projects.
/// Cache identity is the ENTIRE upstream skia-tag, not only the mNNN prefix.
/// </summary>
public sealed class SharedSkiaCache : IDisposable
{
    public static string Root => Path.Combine(ManagerService.DataRoot, "shared-skia");
    public string JunctionPath { get; }
    public string TargetPath { get; }
    private bool _disposed;

    private SharedSkiaCache(string junction, string target)
    {
        JunctionPath = junction; TargetPath = target;
    }

    public static SharedSkiaCache? TryAttach(string source, Action<string> log)
    {
        try { return Attach(source, log); }
        catch (Exception ex)
        {
            log("Shared Skia optimization unavailable; using official per-build dependency download: " + ex.Message);
            return null;
        }
    }
    public static SharedSkiaCache Attach(string source, Action<string> log)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var file = Path.Combine(source, "laf", "misc", "skia-tag.txt");
        if (!File.Exists(file)) throw new FileNotFoundException("Aseprite's Skia revision marker is missing.", file);
        var tag = File.ReadAllText(file).Trim();
        if (!Regex.IsMatch(tag, "^[a-zA-Z0-9._-]{3,140}$"))
            throw new InvalidDataException("Skia tag contains unsupported characters.");
        var shortName = "skia-" + tag.Split('-')[0];
        var suffix = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(tag))).ToLowerInvariant()[..12];
        var target = Path.Combine(Root, shortName + "-" + suffix);
        var junction = Path.Combine(source, ".deps", shortName);
        if (Directory.Exists(junction) || File.Exists(junction))
            throw new InvalidOperationException("Skia build directory already exists; refusing to replace it.");
        Directory.CreateDirectory(Path.GetDirectoryName(junction)!);
        Directory.CreateDirectory(target);
        var cmdPath = Path.Combine(source, "manager-link-skia.cmd");
        try
        {
            if (junction.Contains('"') || target.Contains('"') ||
                junction.Contains('%') || target.Contains('%'))
                throw new InvalidOperationException("Unsupported characters in Skia cache paths.");
            File.WriteAllText(cmdPath, "@echo off\r\nmklink /J \"" + junction +
                "\" \"" + target + "\"\r\nexit /b %errorlevel%\r\n");
            var psi = new ProcessStartInfo("cmd.exe")
            {
                WorkingDirectory = source, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            psi.ArgumentList.Add("/d"); psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(@".\manager-link-skia.cmd");
            using var p = Process.Start(psi) ??
                throw new IOException("Could not start junction setup.");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000))
            {
                p.Kill(entireProcessTree: true);
                throw new TimeoutException("Skia junction setup timed out.");
            }
            if (p.ExitCode != 0 || !Directory.Exists(junction))
                throw new IOException("Cannot establish persistent Skia cache: " + stdout + stderr);
            log("Shared Skia dependency directory: " + target + " (tag " + tag + ").");
            return new SharedSkiaCache(junction, target);
        }
        finally { try { File.Delete(cmdPath); } catch { } }
    }

    public void Dispose()
    {
        if (_disposed) return;
        // NEVER recurse into a junction. Remove the link only, preserving
        // downloaded Skia dependencies in the shared target directory.
        if (Directory.Exists(JunctionPath))
        {
            if ((File.GetAttributes(JunctionPath) & FileAttributes.ReparsePoint) == 0)
                throw new InvalidOperationException("Expected the cached Skia directory to be a junction; refuse recursive removal.");
            Directory.Delete(JunctionPath, recursive: false);
        }
        _disposed = true;
    }

    public static (int Folders, long Bytes) Statistics()
    {
        if (!Directory.Exists(Root)) return (0, 0);
        long bytes = 0; int count = 0;
        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                continue;
            count++;
            bytes += Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        return (count, bytes);
    }
    public static void Clear(Action<string> log)
    {
        if (!Directory.Exists(Root)) return;
        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing to clear linked dependency cache.");
            Directory.Delete(directory, recursive: true);
        }
        log("Cleared shared Skia dependency downloads (installed Aseprite unaffected).");
    }
}

public static class BuildCacheLease
{
    public static IDisposable Acquire()
    {
        Directory.CreateDirectory(ManagerService.DataRoot);
        try
        {
            return new FileStream(Path.Combine(ManagerService.DataRoot, "build-cache.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException e)
        {
            throw new IOException(
                "Another Aseprite Manager operation is building or cleaning caches. Wait for it to finish.", e);
        }
    }
}
