using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AsepriteManager;

public sealed record WorkStatus(int Percent, string Stage, string Detail);
public sealed record DependencyReport(string? Git, string? CMake, string? Ninja, string? VsDevCmd, bool VcRuntime)
{
    public bool Complete => Git != null && CMake != null && Ninja != null && VsDevCmd != null && VcRuntime;
    public string Summary =>
        $"Git/Bash: {Label(Git)}   •   CMake: {Label(CMake)}   •   Ninja: {Label(Ninja)}   •   VS 2022 C++ and SDK: {Label(VsDevCmd)}   •   VC++ Runtime: {(VcRuntime ? "Ready" : "MISSING")}";
    private static string Label(string? p) => p is null ? "MISSING" : "Ready";
}

public static class VersionOrder
{
    private static readonly Regex Pattern = new(@"(?<![0-9])v?(?<number>\d+(?:\.\d+){1,3})(?<suffix>-[A-Za-z][0-9A-Za-z.-]*)?",
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
            HasWindowsSdk() ? GetVsDevCmd() : null,
            HasVcRuntime());
    }

    public async Task<string?> ReadInstalledVersionAsync(string installDir, CancellationToken ct = default, bool allowMarkerFallback = true)
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
            if (allowMarkerFallback && File.Exists(marker))
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
            throw new InvalidOperationException("Windows Package Manager is not available. Install Microsoft's 'App Installer' first, then retry.");

        var basic = new List<(string Name, string Id)>();
        if (deps.Git == null) basic.Add(("Git for Windows (includes Git Bash)", "Git.Git"));
        if (deps.CMake == null) basic.Add(("CMake", "Kitware.CMake"));
        if (deps.Ninja == null) basic.Add(("Ninja", "Ninja-build.Ninja"));
        if (!deps.VcRuntime) basic.Add(("Microsoft Visual C++ x64 runtime", "Microsoft.VCRedist.2015+.x64"));
        var count = basic.Count + (deps.VsDevCmd == null ? 1 : 0);
        var index = 0;

        foreach (var (name, id) in basic)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new WorkStatus(100 * index++ / Math.Max(1, count),
                "Installing prerequisites", name + " (Windows may request approval)"));
            Log($"Installing {name} ({id}) from the official winget catalog...");
            await RunAsync(winget, new[] { "install", "--exact", "--id", id,
                "--source", "winget", "--accept-package-agreements",
                "--accept-source-agreements", "--disable-interactivity", "--silent" }, DataRoot, ct);
            RefreshEnvironmentPath();
        }

        if (deps.VsDevCmd == null)
        {
            progress.Report(new WorkStatus(100 * index / Math.Max(1, count),
                "Installing Microsoft C++ tools",
                "Installing or modifying Visual Studio 2022 C++ Build Tools and the Windows SDK..."));
            // Existing Community/Professional/BuildTools instances must be MODIFIED.
            // 'winget install' on an already installed instance cannot add the missing workload.
            var existingInstall = GetExistingVisualStudioInstallPath();
            var vsSetup = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFilesX86),
                @"Microsoft Visual Studio\Installer\setup.exe");
            var components = new[] {
                "--add", "Microsoft.VisualStudio.Workload.VCTools",
                "--add", "Microsoft.VisualStudio.Component.Windows11SDK.26100",
                "--includeRecommended", "--norestart"
            };
            if (existingInstall != null && File.Exists(vsSetup))
            {
                Log($"Modifying existing Visual Studio 2022 installation at: {existingInstall}");
                await RunAsync(vsSetup, new[] { "modify", "--installPath", existingInstall,
                    "--passive" }.Concat(components), DataRoot, ct);
            }
            else
            {
                const string options = "--passive --wait --norestart --add Microsoft.VisualStudio.Workload.VCTools --add Microsoft.VisualStudio.Component.Windows11SDK.26100 --includeRecommended";
                Log("Installing Visual Studio 2022 Build Tools and the required Windows SDK.");
                await RunAsync(winget, new[] { "install", "--exact", "--id",
                    "Microsoft.VisualStudio.2022.BuildTools", "--source", "winget",
                    "--accept-package-agreements", "--accept-source-agreements",
                    "--override", options }, DataRoot, ct);
            }
        }
        RefreshEnvironmentPath();
        progress.Report(new WorkStatus(100, "Prerequisites",
            "Finished. Checking that every required compiler tool and SDK is present..."));
        var check = ScanDependencies();
        if (!check.Complete)
            throw new InvalidOperationException($"Some required tools are still missing: {check.Summary}. " +
                "A reboot or completing the Visual Studio Installer may be required.");
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

        // One desktop Manager operation may touch the cache at a time, even
        // across separate Windows processes. Never lock user projects.
        using var cacheLease = BuildCacheLease.Acquire();
        var stage = Path.Combine(Path.GetDirectoryName(installDir)!,
            ".aseprite-staging-" + Guid.NewGuid().ToString("N"));
        string? session = null;
        WindowsBuildDrive? drive = null;
        SharedSkiaCache? skiaLink = null;
        bool swapped = false;
        try
        {
            progress.Report(new WorkStatus(7, "Checking build cache",
                "Looking for a previously verified local build of Aseprite " + release.Version + "..."));
            var cacheHit = AsepriteBuildCache.TryStage(
                release.Tag, release.Version, stage, Log);
            if (cacheHit)
            {
                // An intact digest manifest isn't enough: the cached
                // executable itself must still report the requested version.
                var cachedVersion = await ReadInstalledVersionAsync(stage, ct, allowMarkerFallback: false);
                if (cachedVersion == null ||
                    VersionOrder.Compare(cachedVersion, release.Version) != 0)
                {
                    Log("Cached executable version did not match release; performing full rebuild.");
                    TryDelete(stage);
                    cacheHit = false;
                }
            }
            var backupHit = false;
            if (!cacheHit)
            {
                backupHit = AsepriteBuildCache.TryStagePreviousInstall(
                    installDir, release.Tag, release.Version, stage, Log);
                if (backupHit)
                {
                    var restoredVersion = await ReadInstalledVersionAsync(stage, ct,
                        allowMarkerFallback: false);
                    if (restoredVersion == null ||
                        VersionOrder.Compare(restoredVersion, release.Version) != 0)
                    {
                        Log("Backup executable did not match requested release. Ignoring candidate.");
                        TryDelete(stage);
                        backupHit = false;
                    }
                }
            }
            if (!cacheHit && !backupHit)
            {
                var deps = ScanDependencies();
                if (!deps.Complete)
                    throw new InvalidOperationException("Build prerequisites are missing. Click 'Install missing tools' first.");
                var gitBash = GetGitBash(deps.Git!) ??
                    throw new FileNotFoundException("Git Bash is required (install Git for Windows).");

                session = Path.Combine(DataRoot, "work", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(session);
                // Old Aseprite release tags contain unquoted $pwd in build.sh.
                // Keep the command's effective source path short and space free.
                drive = WindowsBuildDrive.Mount(session);
                var source = Path.Combine(drive.Root, "source");
                Log($"Temporary build drive: {drive.Root}.");
                progress.Report(new WorkStatus(14, "Downloading",
                    "Cloning the official Aseprite source tag and submodules..."));
                await RunAsync(deps.Git!, new[] { "clone", "--recursive", "--depth", "1",
                    "--branch", release.Tag, "https://github.com/aseprite/aseprite.git",
                    source }, session, ct);
                if (!File.Exists(Path.Combine(source, ".gitmodules")))
                    throw new InvalidDataException("Git clone did not include source metadata.");

                skiaLink = SharedSkiaCache.TryAttach(source, Log);
                var performance = BuildPerformance.Read();
                var sccache = performance.UseCompilerCache ? BuildPerformance.FindCompilerCache() : null;
                Log($"Ninja parallel jobs: {BuildPerformance.EffectiveJobs(performance)}. " +
                    (sccache == null ? "sccache unavailable/disabled; normal MSVC compilation." :
                        "sccache enabled: " + sccache));
                progress.Report(new WorkStatus(30, "Building",
                    "Running Aseprite's official source build. Future versions may use cached dependencies and compiler objects."));
                var extraDirs = new[] {
                    Path.GetDirectoryName(deps.Git!)!,
                    Path.GetDirectoryName(deps.CMake!)!,
                    Path.GetDirectoryName(deps.Ninja!)!
                };
                WindowsBatchLauncher.WriteAsepriteWrapper(
                    source, deps.VsDevCmd!, gitBash, extraDirs, performance, sccache);
                await RunAsync("cmd.exe", WindowsBatchLauncher.Arguments(), source, ct);

                var built = Path.Combine(source, "build", "bin");
                var newExe = Path.Combine(built, "aseprite.exe");
                if (!File.Exists(newExe))
                    throw new FileNotFoundException(
                        "Build finished without build/bin/aseprite.exe; check the activity log.", newExe);
                progress.Report(new WorkStatus(72, "Verifying",
                    "Checking the newly compiled version before it is eligible for caching..."));
                var compiledVersion = await ReadInstalledVersionAsync(built, ct,
                    allowMarkerFallback: false);
                if (compiledVersion is null ||
                    VersionOrder.Compare(compiledVersion, release.Version) != 0)
                    throw new InvalidDataException(
                        $"Compiled version '{compiledVersion ?? "unknown"}' does not match release {release.Version}.");

                ct.ThrowIfCancellationRequested();
                CopyDirectory(built, stage);
                var upstreamIcon = Path.Combine(source, "data", "icons", "ase.ico");
                if (File.Exists(upstreamIcon))
                    File.Copy(upstreamIcon, Path.Combine(stage, "aseprite.ico"), overwrite: true);

                // Cache ONLY pristine upstream build output, before importing
                // any themes, scripts or user-created files from old installs.
                AsepriteBuildCache.Store(release.Tag, release.Version, stage, Log);
            }
            progress.Report(new WorkStatus(80, "Staging",
                cacheHit ? "Restoring a verified local build; protecting your existing installation..." :
                backupHit ? "Restoring a previously installed version without recompilation..." :
                    "Preparing the new verified source build; preserving your artwork and settings..."));
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
                var post = ReadInstalledVersionAsync(installDir, CancellationToken.None, allowMarkerFallback: false).GetAwaiter().GetResult();
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
            // Remove NTFS junction without following it, then release SUBST.
            // Downloaded shared Skia contents must survive session cleanup.
            var sourceSafeToRemove = true;
            if (skiaLink != null)
            {
                try { skiaLink.Dispose(); }
                catch (Exception ex)
                {
                    Log("SKIA CACHE LINK CLEANUP WARNING: " + ex.Message);
                    // Never recurse into a workspace while an NTFS junction
                    // may still reference the persistent Skia cache.
                    sourceSafeToRemove = false;
                }
            }
            if (drive != null)
            {
                try { drive.Dispose(); }
                catch (Exception ex) { Log("BUILD DRIVE CLEANUP WARNING: " + ex.Message); }
            }
            if (session != null && sourceSafeToRemove) TryDelete(session);
            else if (session != null)
                Log("Retained temporary build workspace for safe manual cleanup: " + session);
            if (!swapped) TryDelete(stage);
        }
    }

    public async Task RepairAsepriteShortcutAsync(string installDir, CancellationToken ct = default)
    {
        var exe = Path.Combine(installDir, "aseprite.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException("Select an installed Aseprite directory first.", exe);
        var icon = Path.Combine(installDir, "aseprite.ico");
        if (!File.Exists(icon))
        {
            var inData = Path.Combine(installDir, "data", "icons", "ase.ico");
            if (File.Exists(inData)) File.Copy(inData, icon);
            else
            {
                // Older source installs may lack this resource. Retrieve it directly
                // from upstream only when the user asks to repair the shortcut.
                using var response = await Client.GetAsync(
                    "https://raw.githubusercontent.com/aseprite/aseprite/main/data/icons/ase.ico",
                    HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > 1_000_000)
                    throw new InvalidDataException("Official Aseprite icon was unexpectedly large.");
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length is < 32 or > 1_000_000 ||
                    bytes[0] != 0 || bytes[1] != 0 || bytes[2] != 1 || bytes[3] != 0)
                    throw new InvalidDataException("Downloaded icon was not a valid ICO header.");
                await File.WriteAllBytesAsync(icon, bytes, ct);
            }
        }
        CreateStartMenuShortcut(installDir);
        Log("Updated Aseprite's Start Menu shortcut with its upstream icon.");
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
        var officialIcon = Path.Combine(installDir, "aseprite.ico");
        if (!File.Exists(officialIcon))
            officialIcon = Path.Combine(installDir, "data", "icons", "ase.ico");
        shortcut.IconLocation = File.Exists(officialIcon) ? officialIcon + ",0" : target + ",0";
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

    private static bool HasVcRuntime()
    {
        try
        {
            const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64";
            var installed = Microsoft.Win32.Registry.GetValue(key, "Installed", 0);
            return installed is int status && status == 1;
        }
        catch { return false; }
    }

    private static bool HasWindowsSdk()
    {
        var kitRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Windows Kits\10\Include");
        if (!Directory.Exists(kitRoot)) return false;
        return Directory.EnumerateDirectories(kitRoot).Any(dir =>
            Path.GetFileName(dir).StartsWith("10.0.26100.", StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(Path.Combine(dir, "um")) &&
            Directory.Exists(Path.Combine(dir, "ucrt")));
    }

    private static string? GetExistingVisualStudioInstallPath()
    {
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Microsoft Visual Studio\Installer\vswhere.exe");
        if (!File.Exists(vswhere)) return null;
        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo(vswhere)
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            foreach (var arg in new[] { "-latest", "-version", "[17.0,18.0)",
                "-products", "*", "-property", "installationPath" })
                p.StartInfo.ArgumentList.Add(arg);
            p.Start();
            var output = p.StandardOutput.ReadToEnd().Trim();
            if (!p.WaitForExit(10000)) { p.Kill(); return null; }
            return p.ExitCode == 0 && Directory.Exists(output) ? output : null;
        }
        catch { return null; }
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
            foreach (var arg in new[] { "-latest", "-version", "[17.0,18.0)", "-products", "*", "-requires",
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
