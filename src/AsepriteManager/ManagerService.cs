using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AsepriteManager;

public sealed record WorkStatus(int Percent, string Stage, string Detail);
public sealed record DependencyReport(string? Git, string? CMake, string? Ninja, string? VsDevCmd)
{
    public bool Complete => Git != null && CMake != null && Ninja != null && VsDevCmd != null;
    public string Summary =>
        $"Git: {Label(Git)}   •   CMake: {Label(CMake)}   •   Ninja: {Label(Ninja)}   •   VS 2022 C++: {Label(VsDevCmd)}";
    private static string Label(string? p) => p is null ? "MISSING" : "Ready";
}

public static class VersionOrder
{
    private static readonly Regex Pattern = new(@"(?<![0-9])v?(?<number>\d+(?:\.\d+){2,3})(?<suffix>-[A-Za-z][0-9A-Za-z.-]*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? Parse(string value)
    {
        var m = Pattern.Match(value);
        return m.Success ? m.Groups["number"].Value + m.Groups["suffix"].Value : null;
    }

    public static int Compare(string installed, string latest)
    {
        var a = Pattern.Match(installed);
        var b = Pattern.Match(latest);
        if (!a.Success || !b.Success) throw new ArgumentException("Unrecognized Aseprite version.");
        var aa = a.Groups["number"].Value.Split('.').Select(int.Parse).ToArray();
        var bb = b.Groups["number"].Value.Split('.').Select(int.Parse).ToArray();
        for (int i = 0; i < Math.Max(aa.Length, bb.Length); i++)
        {
            var c = (i < aa.Length ? aa[i] : 0).CompareTo(i < bb.Length ? bb[i] : 0);
            if (c != 0) return c;
        }

        // A source-built stable tag can report 1.3.18.2-dev: same numeric release.
        var left = a.Groups["suffix"].Value;
        var right = b.Groups["suffix"].Value;
        if (left.Equals("-dev", StringComparison.OrdinalIgnoreCase)) left = "";
        if (right.Equals("-dev", StringComparison.OrdinalIgnoreCase)) right = "";
        if (left.Length == 0 && right.Length == 0) return 0;
        if (left.Length == 0) return 1;
        if (right.Length == 0) return -1;
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ManagerService
{
    public static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aseprite Manager");
    public static string DefaultInstall => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Aseprite");
    public static string LogPath => Path.Combine(DataRoot, "manager.log");

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly object _fileLock = new();
    static ManagerService() => Client.DefaultRequestHeaders.UserAgent.ParseAdd("AsepriteManager/0.1.0");
    public event Action<string>? Logged;

    public void Log(string text)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {text}";
        try
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(DataRoot);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch { /* logging must never interrupt a rollback */ }
        Logged?.Invoke(line);
    }

    private readonly ReleaseCatalog _catalog = new();

    public async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken ct = default)
    {
        var releases = await _catalog.GetPublishedReleasesAsync(ct);
        Log($"Found {releases.Count} published Aseprite GitHub releases.");
        return releases;
    }

    public DependencyReport ScanDependencies()
    {
        RefreshEnvironmentPath();
        var git = FindExe("git.exe", @"C:\Program Files\Git\cmd\git.exe");
        if (git != null && GetGitBash(git) == null) git = null;
        return new DependencyReport(
            git,
            FindExe("cmake.exe", @"C:\Program Files\CMake\bin\cmake.exe"),
            FindExe("ninja.exe"),
            HasWindowsSdk() ? GetVsDevCmd() : null);
    }

    public async Task<string?> ReadInstalledVersionAsync(string installDir, CancellationToken ct = default)
    {
        var exe = Path.Combine(installDir, "aseprite.exe");
        if (!File.Exists(exe)) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var result = await RunAsync(exe, new[] { "--version" }, installDir, timeout.Token, logOutput: false);
            return VersionOrder.Parse(result);
        }
        catch (Exception ex)
        {
            Log("Version probe failed: " + ex.Message);
            var marker = Path.Combine(installDir, "aseprite-manager.json");
            if (File.Exists(marker))
            {
                try
                {
                    using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(marker, ct));
                    return doc.RootElement.GetProperty("version").GetString();
                }
                catch { }
            }
            return null;
        }
    }

    public async Task InstallMissingToolsAsync(DependencyReport deps, IProgress<WorkStatus> progress, CancellationToken ct)
    {
        var winget = FindExe("winget.exe");
        if (winget is null)
            throw new InvalidOperationException("Windows Package Manager (winget) is missing. Install 'App Installer' from Microsoft, then retry.");
        var missing = new List<(string Name, string Id, string? Override)>();
        if (deps.Git == null) missing.Add(("Git", "Git.Git", null));
        if (deps.CMake == null) missing.Add(("CMake", "Kitware.CMake", null));
        if (deps.Ninja == null) missing.Add(("Ninja", "Ninja-build.Ninja", null));
        if (deps.VsDevCmd == null)
            missing.Add(("Visual Studio 2022 C++ Build Tools", "Microsoft.VisualStudio.2022.BuildTools",
                "--passive --wait --norestart --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended --add Microsoft.VisualStudio.Component.Windows11SDK.26100"));

        for (int i = 0; i < missing.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var tool = missing[i];
            progress.Report(new WorkStatus((i * 100) / missing.Count, "Installing prerequisites", tool.Name + " (UAC may ask for approval)"));
            Log($"Installing {tool.Name} with Windows Package Manager...");
            var args = new List<string> { "install", "--exact", "--id", tool.Id, "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity" };
            if (tool.Override != null)
            {
                args.Add("--override");
                args.Add(tool.Override);
            }
            else args.Add("--silent");
            await RunAsync(winget, args, DataRoot, ct);
            RefreshEnvironmentPath();
        }
        progress.Report(new WorkStatus(100, "Prerequisites", "Finished. Rechecking installed tools..."));
    }

    public async Task InstallOrUpdateAsync(string requestedDir, ReleaseInfo release, IProgress<WorkStatus> progress, CancellationToken ct)
    {
        var installDir = Path.GetFullPath(requestedDir.Trim());
        InstallSafety.ValidateDestination(installDir);
        if (Process.GetProcessesByName("aseprite").Length > 0)
            throw new InvalidOperationException("Close Aseprite before installing or updating.");
        var oldVersion = await ReadInstalledVersionAsync(installDir, ct);
        if (oldVersion != null && VersionOrder.Compare(oldVersion, release.Version) == 0)
            throw new InvalidOperationException($"Selected version is already installed ({oldVersion}).");
        if (File.Exists(Path.Combine(installDir, "aseprite.exe")) && oldVersion == null)
            throw new InvalidOperationException("The existing Aseprite version could not be identified. No files were changed.");

        var deps = ScanDependencies();
        if (!deps.Complete)
            throw new InvalidOperationException("Build prerequisites are missing. Click 'Install missing tools' first.");
        var gitBash = GetGitBash(deps.Git!) ??
            throw new FileNotFoundException("Git Bash is required (install Git for Windows).");

        // Work remains isolated until compilation and version verification both succeed.
        Directory.CreateDirectory(DataRoot);
        var session = Path.Combine(DataRoot, "work", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(session, "source");
        var stage = Path.Combine(Path.GetDirectoryName(installDir)!, ".aseprite-staging-" + Guid.NewGuid().ToString("N"));
        bool swapped = false;
        Directory.CreateDirectory(session);
        try
        {
            progress.Report(new WorkStatus(8, "Downloading", "Cloning the official release and its submodules..."));
            await RunAsync(deps.Git!, new[] { "clone", "--recursive", "--depth", "1", "--branch", release.Tag,
                "https://github.com/aseprite/aseprite.git", source }, session, ct);
            if (!File.Exists(Path.Combine(source, ".gitmodules")))
                throw new InvalidDataException("Git clone did not include the required source metadata.");

            progress.Report(new WorkStatus(30, "Building", "Compiling Aseprite with the official --auto helper. This may take a while."));
            // Do NOT invoke upstream build.cmd: it unconditionally pauses at the end.
            var script = Path.Combine(session, "build-with-msvc.cmd");
            var extra = string.Join(";", new[] {
                Path.GetDirectoryName(deps.Git!), Path.GetDirectoryName(deps.CMake!), Path.GetDirectoryName(deps.Ninja!)
            }.Distinct(StringComparer.OrdinalIgnoreCase));
            var lines = new[] {
                "@echo off", "setlocal",
                $"set \"PATH={extra};%PATH%\"",
                $"call \"{deps.VsDevCmd}\" -arch=x64 >nul",
                "if errorlevel 1 exit /b %errorlevel%",
                $"\"{gitBash}\" ./build.sh --auto --norun",
                "exit /b %errorlevel%"
            };
            await File.WriteAllLinesAsync(script, lines, ct);
            await RunAsync("cmd.exe", new[] { "/d", "/s", "/c", $"\"{script}\"" }, source, ct);

            var built = Path.Combine(source, "build", "bin");
            var newExe = Path.Combine(built, "aseprite.exe");
            if (!File.Exists(newExe))
                throw new FileNotFoundException("The build finished without build/bin/aseprite.exe. Check the log.", newExe);
            progress.Report(new WorkStatus(72, "Verifying", "Checking the newly compiled Aseprite version..."));
            var compiledVersion = await ReadInstalledVersionAsync(built, ct);
            if (compiledVersion is null || VersionOrder.Compare(compiledVersion, release.Version) != 0)
                throw new InvalidDataException($"Compiled version '{compiledVersion ?? "unknown"}' does not match release {release.Version}.");

            ct.ThrowIfCancellationRequested();
            progress.Report(new WorkStatus(80, "Staging", "Preparing the verified build without touching the current installation..."));
            CopyDirectory(built, stage);
            if (Directory.Exists(installDir))
                InstallSafety.PreserveUserContent(installDir, stage, Log);
            var marker = JsonSerializer.Serialize(new { version = release.Version, release = release.Tag,
                installedAt = DateTimeOffset.UtcNow }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(stage, "aseprite-manager.json"), marker, ct);

            // Cancellation is checked BEFORE the swap, never during the rename transaction.
            ct.ThrowIfCancellationRequested();
            if (Process.GetProcessesByName("aseprite").Length > 0)
                throw new InvalidOperationException("Aseprite was started during the build. Close it and retry.");
            progress.Report(new WorkStatus(92, "Installing", "Swapping the verified build while retaining the previous version..."));
            InstallSafety.SwapVerifiedInstallation(stage, installDir, () =>
            {
                // Verify the executable and version again after the final rename.
                var post = ReadInstalledVersionAsync(installDir, CancellationToken.None).GetAwaiter().GetResult();
                return post != null && VersionOrder.Compare(post, release.Version) == 0;
            }, Log);
            swapped = true;
            progress.Report(new WorkStatus(100, "Complete",
                $"Aseprite {release.Version} installed. Previous application folder is retained as a full backup."));

            try { CreateStartMenuShortcut(installDir); }
            catch (Exception ex) { Log("Start Menu shortcut could not be created: " + ex.Message); }
            Log($"SUCCESS: {release.Tag} => {installDir}. Any prior installation is retained as a full backup.");
        }
        catch (Exception ex)
        {
            Log("INSTALL/UPDATE FAILED: " + ex);
            // After a completed swap, never automatically delete a working installation.
            throw;
        }
        finally
        {
            TryDelete(session);
            if (!swapped) TryDelete(stage);
        }
    }

    private static void CreateStartMenuShortcut(string installDir)
    {
        var programs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        Directory.CreateDirectory(programs);
        var target = Path.Combine(installDir, "aseprite.exe");
        var shortcutPath = Path.Combine(programs, "Aseprite (Source Build).lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows shortcut service is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Could not create Windows shortcut service.");
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = target;
        shortcut.WorkingDirectory = installDir;
        shortcut.Description = "Aseprite (compiled locally from official source)";
        shortcut.IconLocation = target + ",0";
        shortcut.Save();
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: false);
        }
    }
    private static void CopyMissing(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(destination, Path.GetRelativePath(source, file));
            if (File.Exists(dst)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst);
        }
    }
    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    private async Task<string> RunAsync(string executable, IEnumerable<string> args, string workingDir, CancellationToken ct, bool logOutput = true)
    {
        Directory.CreateDirectory(workingDir);
        var psi = new ProcessStartInfo
        {
            FileName = executable, WorkingDirectory = workingDir, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = psi };
        if (!process.Start()) throw new InvalidOperationException("Could not start: " + executable);
        var output = new StringBuilder();
        const int maxResultChars = 16000;
        async Task ReadAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                lock (output)
                {
                    output.AppendLine(line);
                    if (output.Length > maxResultChars * 2)
                        output.Remove(0, output.Length - maxResultChars);
                }
                if (logOutput) Log(line);
            }
        }
        var stdout = ReadAsync(process.StandardOutput);
        var stderr = ReadAsync(process.StandardError); // Git progress is normal stderr, NOT failure.
        try
        {
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} exited with code {process.ExitCode}. Recent output: {output.ToString()[Math.Max(0, output.Length - 3000)..]}. See activity log.");
        return output.ToString();
    }

    private static string? FindExe(string name, params string[] extras)
    {
        var possible = extras.Concat((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries).Select(dir => Path.Combine(dir.Trim('"'), name)));
        foreach (var p in possible)
        {
            try { if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    private static string? GetGitBash(string gitExe)
    {
        var gitCmdDir = Path.GetDirectoryName(gitExe)!;
        var guesses = new[]
        {
            Path.GetFullPath(Path.Combine(gitCmdDir, "..", "bin", "bash.exe")),
            Path.Combine(gitCmdDir, "bash.exe"),
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe"
        };
        return guesses.FirstOrDefault(File.Exists);
    }

    private static bool HasWindowsSdk()
    {
        var kitRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Windows Kits\10\Include");
        if (!Directory.Exists(kitRoot)) return false;
        return Directory.EnumerateDirectories(kitRoot).Any(dir =>
            Directory.Exists(Path.Combine(dir, "um")) &&
            Directory.Exists(Path.Combine(dir, "ucrt")));
    }

    private static string? GetVsDevCmd()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var vswhere = Path.Combine(pf, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return null;
        try
        {
            var psi = new ProcessStartInfo(vswhere)
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            foreach (var arg in new[] { "-latest", "-products", "*", "-requires",
                "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-find",
                @"VC\Auxiliary\Build\vcvars64.bat" }) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi);
            if (p == null) return null;
            var line = p.StandardOutput.ReadToEnd().Trim().Split('\n').FirstOrDefault()?.Trim();
            if (!p.WaitForExit(10000)) { p.Kill(); return null; }
            return p.ExitCode == 0 && line != null && File.Exists(line) ? line : null;
        }
        catch { return null; }
    }

    private static void RefreshEnvironmentPath()
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? "";
        var machine = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
        var user = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        var parts = new[] { current, machine, user,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Links"),
            @"C:\Program Files\Git\cmd", @"C:\Program Files\CMake\bin" };
        var unique = parts.SelectMany(s => s.Split(';', StringSplitOptions.RemoveEmptyEntries))
            .Select(s => s.Trim().Trim('"'))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        Environment.SetEnvironmentVariable("PATH", string.Join(";", unique));
    }
}
