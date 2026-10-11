using System.Diagnostics;
using System.IO;

namespace AsepriteManager;

/// <summary>
/// Aseprite's published build.sh scripts use unquoted shell paths for .build
/// metadata and Skia dependencies. Windows subst maps our private build session
/// (whose physical path may contain spaces) onto a short, space-free drive root.
/// No Aseprite source is modified. A process-local lock prevents one process
/// from reserving the same letter twice; Windows also rejects occupied drives.
/// </summary>
public sealed class WindowsBuildDrive : IDisposable
{
    private static readonly object DriveLock = new();
    private static readonly HashSet<char> Reserved = new();
    private readonly char _letter;
    private bool _disposed;

    private WindowsBuildDrive(char letter, string root)
    {
        _letter = letter;
        Root = root;
    }

    public string Root { get; }

    public static WindowsBuildDrive Mount(string existingSessionFolder)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Build-drive mapping is supported on Windows only.");
        var full = Path.GetFullPath(existingSessionFolder);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException("Cannot mount missing build session: " + full);

        lock (DriveLock)
        {
            var mounted = Environment.GetLogicalDrives().ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (char letter = 'Z'; letter >= 'D'; letter--)
            {
                if (Reserved.Contains(letter) || mounted.Contains($"{letter}:\\"))
                    continue;
                var result = RunSubst($"{letter}:", full);
                if (result.ExitCode != 0) continue;
                if (!Directory.Exists($"{letter}:\\"))
                {
                    RunSubst($"{letter}:", "/D");
                    continue;
                }
                Reserved.Add(letter);
                return new WindowsBuildDrive(letter, $"{letter}:\\");
            }
        }
        throw new IOException("Could not map an unused temporary build drive. " +
            "Free a Windows drive letter and retry. Existing Aseprite files were not changed.");
    }

    private static (int ExitCode, string Output) RunSubst(string drive, string path)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "subst.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        process.StartInfo.ArgumentList.Add(drive);
        process.StartInfo.ArgumentList.Add(path);
        if (!process.Start()) throw new InvalidOperationException("Unable to start Windows subst.exe.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Windows build-drive mapping timed out.");
        }
        return (process.ExitCode, stdout + stderr);
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (DriveLock)
        {
            if (_disposed) return;
            var outcome = RunSubst($"{_letter}:", "/D");
            if (outcome.ExitCode != 0)
                throw new IOException("Could not release temporary build drive " +
                    _letter + ":. Close any open build processes and run " +
                    $"subst {_letter}: /D manually. Details: {outcome.Output}");
            Reserved.Remove(_letter);
            _disposed = true;
        }
    }
}
