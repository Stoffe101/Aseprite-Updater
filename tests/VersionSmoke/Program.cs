using AsepriteManager;

var cases = new (string installed, string latest, int expected)[]
{
    ("v1.3.18.2-dev", "v1.3.18.2", 0),
    ("1.3.18.2-dev", "v1.3.18.6", -1),
    ("1.3.18.6", "1.3.18.6", 0),
    ("1.3.19", "1.3.18.6", 1),
    ("1.3.18.6-beta1", "1.3.18.6", -1),
    ("1.3.18.6", "1.3.18.6-beta1", 1),
    ("1.3.18.6", "1.3.18.7", -1),
    ("1.3.18.6", "1.3.19", -1),
    ("1.2", "1.3", -1),
    ("1.3.18.6", "1.3.18.2", 1)
};
foreach (var (installed, latest, expected) in cases)
{
    var actual = Math.Sign(VersionOrder.Compare(installed, latest));
    if (actual != expected) throw new Exception($"FAIL: {installed} vs {latest}: expected {expected}, got {actual}");
    Console.WriteLine($"PASS: {installed} vs {latest}");
}
Console.WriteLine("All version comparison checks passed.");


// Run safety tests in a disposable temp directory only.
var scratch = Path.Combine(Path.GetTempPath(), "AsepriteManager-Test-" + Guid.NewGuid().ToString("N"));
var installFolder = Path.Combine(scratch, "Aseprite");
var staged = Path.Combine(scratch, "Staged");
var customTheme = Path.Combine(installFolder, "data", "themes", "my-theme.xml");
var project = Path.Combine(installFolder, "Sprites", "my-art.aseprite");
try
{
    Directory.CreateDirectory(Path.GetDirectoryName(customTheme)!);
    Directory.CreateDirectory(Path.GetDirectoryName(project)!);
    Directory.CreateDirectory(staged);
    File.WriteAllText(Path.Combine(installFolder, "aseprite.exe"), "OLD");
    File.WriteAllText(customTheme, "MY THEME");
    File.WriteAllText(project, "MY SPRITE");
    File.WriteAllText(Path.Combine(staged, "aseprite.exe"), "NEW");
    InstallSafety.PreserveUserContent(installFolder, staged);
    if (File.ReadAllText(Path.Combine(staged, "data", "themes", "my-theme.xml")) != "MY THEME")
        throw new Exception("Theme was not preserved.");
    if (File.ReadAllText(Path.Combine(staged, "Sprites", "my-art.aseprite")) != "MY SPRITE")
        throw new Exception("Artwork in install folder was not preserved.");
    InstallSafety.SwapVerifiedInstallation(staged, installFolder,
        () => File.ReadAllText(Path.Combine(installFolder, "aseprite.exe")) == "NEW", _ => { });
    if (File.ReadAllText(Path.Combine(installFolder, "aseprite.exe")) != "NEW")
        throw new Exception("New installation was not activated.");
    var backups = Directory.GetDirectories(scratch, "Aseprite.backup-*");
    if (backups.Length != 1 ||
        File.ReadAllText(Path.Combine(backups[0], "Sprites", "my-art.aseprite")) != "MY SPRITE")
        throw new Exception("Complete old installation backup missing.");

    // Replacing an installation which fails post-swap validation MUST restore the old exe.
    var badStage = Path.Combine(scratch, "BadStage");
    Directory.CreateDirectory(badStage);
    File.WriteAllText(Path.Combine(badStage, "aseprite.exe"), "BAD");
    try
    {
        InstallSafety.SwapVerifiedInstallation(badStage, installFolder, () => false, _ => { });
        throw new Exception("Expected validation failure.");
    }
    catch (InvalidDataException) { }
    if (File.ReadAllText(Path.Combine(installFolder, "aseprite.exe")) != "NEW")
        throw new Exception("Rollback failed to restore the working installation.");
    try
    {
        InstallSafety.ValidateDestination(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        throw new Exception("Profile root should have been rejected.");
    }
    catch (InvalidOperationException) { }
    Console.WriteLine("PASS: user content preserved, full backup retained and rollback restored old install.");
}
finally
{
    try { Directory.Delete(scratch, recursive: true); } catch { }
}

// Regression test: a Windows batch file under a folder with spaces must
// execute correctly, and its exit code must propagate to the manager.
if (OperatingSystem.IsWindows())
{
    var rootWithSpaces = Path.Combine(Path.GetTempPath(),
        "Aseprite Manager command with spaces " + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(rootWithSpaces);
    try
    {
        var batch = Path.Combine(rootWithSpaces, WindowsBatchLauncher.ScriptName);
        foreach (var expectedCode in new[] { 0, 27 })
        {
            File.WriteAllLines(batch, new[] {
                "@echo off",
                "echo BUILD_HELPER_STARTED",
                $"exit /b {expectedCode}"
            });
            using var p = new System.Diagnostics.Process();
            p.StartInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                WorkingDirectory = rootWithSpaces,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in WindowsBatchLauncher.Arguments())
                p.StartInfo.ArgumentList.Add(arg);
            if (!p.Start()) throw new Exception("Could not start cmd.exe.");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(20000)) { p.Kill(entireProcessTree: true); throw new Exception("cmd.exe hung."); }
            if (p.ExitCode != expectedCode || !stdout.Contains("BUILD_HELPER_STARTED"))
                throw new Exception($"Windows batch launch failed (expected {expectedCode}, got {p.ExitCode}): {stdout} {stderr}");
            Console.WriteLine($"PASS: relative batch in spaced path, exit code {expectedCode}");
        }

        // Exercise the exact production wrapper as well, including an MSVC
        // environment batch and a real Git Bash executable under Program Files.
        var bashExe = @"C:\Program Files\Git\bin\bash.exe";
        if (!File.Exists(bashExe))
            throw new Exception("Git for Windows is not installed on the Windows CI runner.");
        var sourceFolder = Path.Combine(rootWithSpaces, "source folder");
        var fakeVsFolder = Path.Combine(rootWithSpaces, "Visual Studio Tools");
        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(fakeVsFolder);
        var vcvars = Path.Combine(fakeVsFolder, "vcvars64.bat");
        File.WriteAllLines(vcvars, new[] { "@echo off", "echo FAKE_MSVC_SETUP_OK", "exit /b 0" });
        File.WriteAllText(Path.Combine(sourceFolder, "build.sh"),
            "#!/usr/bin/env bash\n" +
            "echo FAKE_ASEPRITE_BUILD_OK\n" +
            "test \"$1\" = \"--auto\" && test \"$2\" = \"--norun\"\n");
        WindowsBatchLauncher.WriteAsepriteWrapper(
            sourceFolder, vcvars, bashExe, new[] { fakeVsFolder });
        using (var productionLikeProcess = new System.Diagnostics.Process())
        {
            productionLikeProcess.StartInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                WorkingDirectory = sourceFolder, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in WindowsBatchLauncher.Arguments())
                productionLikeProcess.StartInfo.ArgumentList.Add(arg);
            productionLikeProcess.Start();
            var output = productionLikeProcess.StandardOutput.ReadToEnd();
            var err = productionLikeProcess.StandardError.ReadToEnd();
            if (!productionLikeProcess.WaitForExit(20000))
            {
                productionLikeProcess.Kill(entireProcessTree: true);
                throw new Exception("Fake build invocation hung.");
            }
            if (productionLikeProcess.ExitCode != 0 ||
                !output.Contains("FAKE_MSVC_SETUP_OK") ||
                !output.Contains("FAKE_ASEPRITE_BUILD_OK"))
                throw new Exception($"Full spaced-path MSVC/Git Bash launch failed: code {productionLikeProcess.ExitCode}. {output} {err}");
            Console.WriteLine("PASS: full MSVC + Git Bash wrapper under a spaced source path.");
        }
    }
    finally { try { Directory.Delete(rootWithSpaces, true); } catch { } }
}


// Regression: upstream Aseprite build.sh uses unquoted $pwd for .build/
// metadata and Skia directories. Even if the real AppData path has spaces,
// Git Bash must see a short path through the temporary build drive.
if (OperatingSystem.IsWindows())
{
    var physicalRoot = Path.Combine(Path.GetTempPath(),
        "Aseprite Manager root containing spaces " + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(physicalRoot);
    try
    {
        using (var mapping = WindowsBuildDrive.Mount(physicalRoot))
        {
            var mappedSource = Path.Combine(mapping.Root, "source");
            Directory.CreateDirectory(mappedSource);
            if (mappedSource.Contains(' '))
                throw new Exception("Build drive path unexpectedly contains spaces.");
            File.WriteAllText(Path.Combine(mappedSource, "build.sh"),
                "#!/usr/bin/env bash\n" +
                "set -e\n" +
                "pwd=$(pwd)\n" +
                "echo \"EFFECTIVE_SOURCE=$pwd\"\n" +
                "mkdir -p \"$pwd/.build\"\n" +
                "echo user > $pwd/.build/userkind\n" +
                "userkind=$(cat $pwd/.build/userkind)\n" +
                "echo \"$pwd\" > \"$pwd/.build/builds_dir\"\n" +
                "builds_dir=\"$(cat $pwd/.build/builds_dir)\"\n" +
                "skia_dir=\"$pwd/.deps/skia-m124\"\n" +
                "mkdir -p $skia_dir\n" +
                "echo $skia_dir > \"$pwd/.build/HEAD_skia_dir\"\n" +
                "skia_dir=$(cat $pwd/.build/HEAD_skia_dir)\n" +
                "test \"$userkind\" = user\n" +
                "test \"$builds_dir\" = \"$pwd\"\n" +
                "test -d \"$skia_dir\"\n" +
                "test \"$1\" = --auto && test \"$2\" = --norun\n" +
                "echo OFFICIAL_UNQUOTED_PATH_CASE_PASSED\n");
            var fakeVs = Path.Combine(physicalRoot, "Visual Studio Tools", "vcvars64.bat");
            Directory.CreateDirectory(Path.GetDirectoryName(fakeVs)!);
            File.WriteAllLines(fakeVs, new [] { "@echo off", "echo MSVC_OK", "exit /b 0" });
            var bash = @"C:\Program Files\Git\bin\bash.exe";
            if (!File.Exists(bash))
                throw new Exception("Git Bash expected on Windows CI runner.");
            WindowsBatchLauncher.WriteAsepriteWrapper(
                mappedSource, fakeVs, bash, new [] { Path.GetDirectoryName(bash)! });
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                WorkingDirectory = mappedSource, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in WindowsBatchLauncher.Arguments())
                process.StartInfo.ArgumentList.Add(arg);
            if (!process.Start()) throw new Exception("Could not launch mapped-drive build.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30000))
            {
                process.Kill(entireProcessTree: true);
                throw new Exception("Regression build helper hung.");
            }
            if (process.ExitCode != 0 ||
                !stdout.Contains("OFFICIAL_UNQUOTED_PATH_CASE_PASSED"))
                throw new Exception("Unquoted upstream Aseprite paths still fail: " + stdout + "\n" + stderr);
            if (File.ReadAllText(Path.Combine(physicalRoot, "source", ".build", "userkind")).Trim() != "user" ||
                !Directory.Exists(Path.Combine(physicalRoot, "source", ".deps", "skia-m124")))
                throw new Exception("Mapped build path did not materialize files in the private physical session.");
            Console.WriteLine("PASS: actual Git Bash unquoted Aseprite path syntax with spaced AppData folder.");
        }
        // The mapping must be removed after the run.
        if (Directory.Exists(Path.Combine(physicalRoot, "source", ".build")) == false)
            throw new Exception("Disposing the build drive unexpectedly deleted physical build files.");
    }
    finally { try { Directory.Delete(physicalRoot, true); } catch { } }
}

// Curated themes can contain graphics, fonts and metadata, but never executable
// scripts and never paths that escape the designated extension folder.
byte[] BuildThemeZip(bool includeEvil = false)
{
    using var ms = new MemoryStream();
    using (var zip = new System.IO.Compression.ZipArchive(ms,
        System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
    {
        void Entry(string name, string content)
        {
            var file = zip.CreateEntry(name);
            using var w = new StreamWriter(file.Open());
            w.Write(content);
        }
        Entry("theme/package.json",
            "{\"name\":\"smoke-theme\",\"version\":\"1.0\",\"contributes\":{\"themes\":[{\"id\":\"smoke\",\"path\":\".\"}]}}");
        Entry("theme/theme.xml", "<theme/>");
        Entry("theme/sheet.png", "png_placeholder");
        Entry("theme/malicious.lua", "os.execute('ignored')");
        if (includeEvil)
            Entry("theme/../../evil.txt", "bad");
    }
    return ms.ToArray();
}
var tempTheme = Path.Combine(Path.GetTempPath(), "AsepriteManager-ThemeSafe-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(tempTheme);
    ThemeManager.UnpackVerifiedTheme(BuildThemeZip(), tempTheme);
    if (!File.Exists(Path.Combine(tempTheme, "theme.xml")))
        throw new Exception("Valid theme was not extracted.");
    if (File.Exists(Path.Combine(tempTheme, "malicious.lua")))
        throw new Exception("Unsafe extension script was installed.");
    try
    {
        ThemeManager.UnpackVerifiedTheme(BuildThemeZip(true),
            Path.Combine(tempTheme, "unsafe"));
        throw new Exception("Unsafe theme archive should have been rejected.");
    }
    catch (InvalidDataException) { }
    Console.WriteLine("PASS: valid themes extracted; unexpected scripts excluded; traversal rejected.");
}
finally
{
    try { Directory.Delete(tempTheme, true); } catch { }
}


// Optional GitHub-backed check of the actual community theme archives.
// Only executes when explicitly requested by CI; no settings or installs.
if (args.Contains("--theme-integration"))
{
    var catalog = new ThemeCatalog();
    foreach (var theme in ThemeCatalog.Curated)
    {
        var resolved = await catalog.ResolveAsync(theme, CancellationToken.None);
        var archive = await catalog.DownloadAsync(resolved, CancellationToken.None);
        var dir = Path.Combine(Path.GetTempPath(),
            "Aseprite-Community-Theme-Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            ThemeManager.UnpackVerifiedTheme(archive, dir);
            Console.WriteLine($"PASS: real upstream {theme.Name}: {resolved.Revision}");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
    // The Manager must uninstall only its own unmodified theme and keep a backup.
    var themeToInstall = ThemeCatalog.Curated.First(t => t.Id == "cat-mocha");
    var themeService = new ThemeManager(Console.WriteLine);
    await themeService.InstallOrUpdateAsync(themeToInstall, CancellationToken.None);
    if (!themeService.IsInstalled(themeToInstall))
        throw new Exception("Real managed theme installation did not appear.");
    themeService.RemoveTheme(themeToInstall);
    if (themeService.IsInstalled(themeToInstall))
        throw new Exception("Managed theme uninstall failed.");
    Console.WriteLine("PASS: real Catppuccin theme installation and protected uninstall.");
}


// The PixelTown palette extractor only reads local source and keeps draft
// colors outside the public Aseprite Manager repository.
var pixelTownScratch = Path.Combine(Path.GetTempPath(),
    "PixelTown art example " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(pixelTownScratch, "art"));
try
{
    File.WriteAllText(Path.Combine(pixelTownScratch, "project.godot"), "[application]");
    File.WriteAllText(Path.Combine(pixelTownScratch, "art", "world_canvas.gd"),
        "const DIRT = Color(\"#a7865d\")\n" +
        "const ROOF = Color(\"#b66549\")\n" +
        "_box(0,0,16,16,Color(\"#a7865d\"))\n");
    var generated = WorkshopLibrary.ExtractPixelTownPalette(pixelTownScratch);
    WorkshopLibrary.ValidateGpl(generated);
    if (!generated.Contains("167 134  93") ||
        !generated.Contains("182 101  73"))
        throw new Exception("Color extraction failed or was not RGB accurate.");
    if (generated.Split('\n').Count(x => x.TrimEnd().EndsWith("DIRT")) != 1)
        throw new Exception("Duplicate colors were not eliminated.");
    try
    {
        WorkshopLibrary.ValidateGpl("GIMP Palette\n 300 0 0 Bad\n 0 0 0 Black\n");
        throw new Exception("Out-of-range GIMP channel should be rejected.");
    }
    catch (InvalidDataException) { }

    // Existing managed resources with local edits must not be silently
    // replaced. Run only in disposable test directories, not Aseprite's.
    var root = Path.Combine(pixelTownScratch, "resources");
    var target = Path.Combine(root, "managed-unit");
    var stage = Path.Combine(root, "stage");
    var receiptPath = Path.Combine(root, "unit.json");
    Directory.CreateDirectory(stage);
    File.WriteAllText(Path.Combine(stage, "example.lua"), "original");
    var originalHash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("original")));
    WorkshopLibrary.ReplaceManaged(target, stage, receiptPath,
        new LibraryReceipt("unit", "a", new Dictionary<string, string> {
            ["example.lua"] = originalHash
        }), _ => { });
    File.WriteAllText(Path.Combine(target, "example.lua"), "customized");
    var anotherStage = Path.Combine(root, "new-stage");
    Directory.CreateDirectory(anotherStage);
    File.WriteAllText(Path.Combine(anotherStage, "example.lua"), "replacement");
    try
    {
        WorkshopLibrary.ReplaceManaged(target, anotherStage, receiptPath,
            new LibraryReceipt("unit", "b", new Dictionary<string,string> {
                ["example.lua"] = "different-hash"
            }), _ => { });
        throw new Exception("Customized resource was overwritten.");
    }
    catch (InvalidOperationException) { }
    if (File.ReadAllText(Path.Combine(target, "example.lua")) != "customized")
        throw new Exception("Custom resource wasn't preserved.");
    Console.WriteLine("PASS: local PixelTown palette extraction and safe managed script overwrite protection.");
}
finally { try { Directory.Delete(pixelTownScratch, true); } catch { } }

// The CI integration mode downloads and installs only the explicitly-reviewed
// MIT-licensed official Aseprite examples into the ephemeral GitHub runner.
if (args.Contains("--library-integration"))
{
    var revision = await WorkshopLibrary.LatestToolRevision(CancellationToken.None);
    foreach (var item in WorkshopLibrary.Tools)
    {
        await WorkshopLibrary.InstallTool(item, revision, Console.WriteLine,
            CancellationToken.None);
        if (!WorkshopLibrary.Installed(item) ||
            WorkshopLibrary.Revision(item) != revision)
            throw new Exception("Managed script wasn't installed: " + item.Name);
        WorkshopLibrary.UninstallTool(item, Console.WriteLine);
        Console.WriteLine("PASS: upstream MIT script direct install/uninstall: " + item.Name);
    }
}


// Godot integration safety: reject unexpected files and traversals. The
// installed plugin must leave the user's project.godot unchanged.
byte[] FakeWizardArchive(bool unsafePath = false)
{
    using var stream = new MemoryStream();
    using (var zip = new System.IO.Compression.ZipArchive(stream,
        System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
    {
        void Put(string file, string text)
        {
            var e = zip.CreateEntry("godot-aseprite-wizard-fixture/addons/AsepriteWizard/" + file);
            using var writer = new StreamWriter(e.Open());
            writer.Write(text);
        }
        Put("plugin.cfg", "[plugin]\nname=\"Aseprite Wizard\"\n");
        Put("plugin.gd", "@tool\nextends EditorPlugin\n");
        Put("LICENSE", "MIT");
        for (int i = 0; i < 9; ++i)
            Put("test/file" + i + ".gd", "extends RefCounted\n");
        if (unsafePath) Put("../evil.txt", "evil");
    }
    return stream.ToArray();
}
var godotSafeRoot = Path.Combine(Path.GetTempPath(), "WizardSafety-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(godotSafeRoot);
try
{
    GodotWizardManager.ExtractWizardArchive(FakeWizardArchive(), godotSafeRoot);
    if (!File.Exists(Path.Combine(godotSafeRoot, "plugin.gd")))
        throw new Exception("Safe Godot plugin fixture was not extracted.");
    var badRoot = Path.Combine(godotSafeRoot, "malicious");
    Directory.CreateDirectory(badRoot);
    try
    {
        GodotWizardManager.ExtractWizardArchive(FakeWizardArchive(true), badRoot);
        throw new Exception("Traversal archive was accepted.");
    }
    catch (InvalidDataException) { }
    Console.WriteLine("PASS: Godot addon archive identity and path validation.");
}
finally { try { Directory.Delete(godotSafeRoot, true); } catch { } }

if (args.Contains("--godot-integration"))
{
    var godotFixtureProject = Path.Combine(Path.GetTempPath(), "Godot 4 Project " + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(godotFixtureProject);
    var config = Path.Combine(godotFixtureProject, "project.godot");
    var original = "config_version=5\n[application]\nconfig/name=\"CI Wizard Test\"\n";
    File.WriteAllText(config, original);
    try
    {
        await GodotWizardManager.InstallOrUpdateAsync(godotFixtureProject, Console.WriteLine, CancellationToken.None);
        var state = GodotWizardManager.Inspect(godotFixtureProject);
        if (!state.Installed || !state.Managed || state.Modified ||
            !File.Exists(Path.Combine(state.PluginPath, "plugin.gd")) ||
            File.ReadAllText(config) != original)
            throw new Exception("Godot Wizard install changed project config or failed integrity.");
        var pluginFile = Path.Combine(state.PluginPath, "plugin.gd");
        var originalPluginText = File.ReadAllText(pluginFile);
        File.AppendAllText(pluginFile, "\n# temporary CI customization\n");
        if (!GodotWizardManager.Inspect(godotFixtureProject).Modified)
            throw new Exception("Modified Godot addon was not detected.");
        try
        {
            GodotWizardManager.Uninstall(godotFixtureProject, Console.WriteLine);
            throw new Exception("The manager removed locally modified addon code.");
        }
        catch (InvalidOperationException) { }
        File.WriteAllText(pluginFile, originalPluginText);
        GodotWizardManager.Uninstall(godotFixtureProject, Console.WriteLine);
        if (Directory.Exists(state.PluginPath) || File.ReadAllText(config) != original)
            throw new Exception("Godot addon uninstall touched the project or retained plugin files.");
        Console.WriteLine("PASS: real Godot 4 Wizard addon downloaded, installed and removed without changing project.godot.");
    }
    finally { try { Directory.Delete(godotFixtureProject, true); } catch { } }
}
if (args.Contains("--palette-integration"))
{
    foreach (var p in PaletteGallery.Presets)
    {
        var (_, gpl) = await PaletteGallery.DownloadAsync(p);
        var colors = PaletteGallery.Colors(gpl);
        if (colors.Count < 2 || colors.Count > 256)
            throw new Exception("Invalid gallery palette: " + p.Name);
        Console.WriteLine($"PASS: upstream palette {p.Name}, {colors.Count} RGB swatches.");
    }
}
if (args.Contains("--overlay-integration"))
{
    await TileIndexOverlayManager.InstallAsync(Console.WriteLine, CancellationToken.None);
    if (!TileIndexOverlayManager.Installed ||
        !File.Exists(Path.Combine(TileIndexOverlayManager.Folder, "tile-index-overlay.lua")))
        throw new Exception("Tile Index Overlay was not installed.");
    TileIndexOverlayManager.Remove(Console.WriteLine);
    if (TileIndexOverlayManager.Installed) throw new Exception("Tile Index Overlay removal failed.");
    Console.WriteLine("PASS: curated Tile Index Overlay installed and removed.");
}


// Opt-in CI fixture: verify the *real Godot editor* can load the addon on the
// pinned Godot 4.7.2 version. This does NOT modify PixelTown or user projects.
if (args.Contains("--godot-editor-fixture"))
{
    var fixturePath = Path.Combine(Path.GetTempPath(), "AsepriteWizardGodotEditorCI");
    if (Directory.Exists(fixturePath)) Directory.Delete(fixturePath, recursive: true);
    Directory.CreateDirectory(fixturePath);
    var config = "config_version=5\n" +
        "[application]\nconfig/name=\"Aseprite Wizard CI Fixture\"\n" +
        "[editor_plugins]\n" +
        "enabled=PackedStringArray(\"res://addons/AsepriteWizard/plugin.cfg\")\n";
    File.WriteAllText(Path.Combine(fixturePath, "project.godot"), config);
    await GodotWizardManager.InstallOrUpdateAsync(fixturePath, Console.WriteLine,
        CancellationToken.None);
    if (File.ReadAllText(Path.Combine(fixturePath, "project.godot")) != config)
        throw new Exception("Wizard altered the CI project config.");
    Console.WriteLine("GODOT_CI_FIXTURE=" + fixturePath);
}


// Profile backups are exports only, never edits to the source folder.
var profileFixture = Path.Combine(Path.GetTempPath(),
    "Aseprite Profile Fixture-" + Guid.NewGuid().ToString("N"));
var srcProfile = Path.Combine(profileFixture, "Aseprite");
var archiveFile = Path.Combine(profileFixture, "profile.zip");
try
{
    Directory.CreateDirectory(Path.Combine(srcProfile, "extensions", "test"));
    File.WriteAllText(Path.Combine(srcProfile, "aseprite.ini"), "saved-preferences");
    File.WriteAllText(Path.Combine(srcProfile, "user.aseprite-brushes"), "custom-brushes");
    File.WriteAllText(Path.Combine(srcProfile, "extensions", "test", "theme.xml"), "custom-theme");
    var count = ProfileBackupService.CreateBackup(srcProfile, archiveFile);
    if (count != 3) throw new Exception("Profile backup missed files.");
    using (var zip = System.IO.Compression.ZipFile.OpenRead(archiveFile))
    {
        if (!zip.Entries.Any(e => e.FullName == "user.aseprite-brushes") ||
            !zip.Entries.Any(e => e.FullName == "extensions/test/theme.xml"))
            throw new Exception("Backup did not preserve user brush settings or extensions.");
    }
    if (File.ReadAllText(Path.Combine(srcProfile, "aseprite.ini")) != "saved-preferences")
        throw new Exception("Profile backup modified user preferences.");
    try
    {
        ProfileBackupService.CreateBackup(srcProfile,
            Path.Combine(srcProfile, "accidental-inside.zip"));
        throw new Exception("Profile backup should reject output nested inside the source.");
    }
    catch (InvalidOperationException) { }
    Console.WriteLine("PASS: complete read-only Aseprite profile backup and inside-folder rejection.");
}
finally { try { Directory.Delete(profileFixture, true); } catch { } }


// Recovery Center: valid archive restores atomically and retains the COMPLETE
// previous profile, including user.aseprite-brushes and customized scripts.
var recoveryScratch = Path.Combine(Path.GetTempPath(),
    "Aseprite Recovery Test " + Guid.NewGuid().ToString("N"));
var originalProfile = Path.Combine(recoveryScratch, "Aseprite");
var backupInput = Path.Combine(recoveryScratch, "chosen-backup.zip");
try
{
    Directory.CreateDirectory(Path.Combine(originalProfile, "extensions"));
    File.WriteAllText(Path.Combine(originalProfile, "aseprite.ini"), "OLD SETTINGS");
    File.WriteAllText(Path.Combine(originalProfile, "user.aseprite-brushes"), "MY BRUSHES");
    var stored = Path.Combine(recoveryScratch, "stored-profile");
    Directory.CreateDirectory(Path.Combine(stored, "scripts"));
    File.WriteAllText(Path.Combine(stored, "aseprite.ini"), "RESTORED SETTINGS");
    File.WriteAllText(Path.Combine(stored, "scripts", "custom.lua"), "custom Lua");
    System.IO.Compression.ZipFile.CreateFromDirectory(stored, backupInput);

    var summary = ProfileRecoveryService.Inspect(backupInput);
    if (summary.FileCount != 2 || !summary.ContainsSettings)
        throw new Exception("Backup preview did not identify expected contents.");
    var oldCopy = ProfileRecoveryService.Restore(backupInput, originalProfile, _ => { });
    if (!Directory.Exists(oldCopy) ||
        File.ReadAllText(Path.Combine(oldCopy, "user.aseprite-brushes")) != "MY BRUSHES" ||
        File.ReadAllText(Path.Combine(originalProfile, "aseprite.ini")) != "RESTORED SETTINGS" ||
        !File.Exists(Path.Combine(originalProfile, "scripts", "custom.lua")))
        throw new Exception("Restore did not preserve previous profile and restore new files.");

    var malicious = Path.Combine(recoveryScratch, "malicious.zip");
    using (var stream = File.Create(malicious))
    using (var zip = new System.IO.Compression.ZipArchive(stream,
        System.IO.Compression.ZipArchiveMode.Create))
    {
        using var writer = new StreamWriter(zip.CreateEntry("../outside.txt").Open());
        writer.Write("bad");
    }
    try
    {
        ProfileRecoveryService.Inspect(malicious);
        throw new Exception("Traversal archive should not validate.");
    }
    catch (InvalidDataException) { }
    if (File.ReadAllText(Path.Combine(originalProfile, "aseprite.ini")) != "RESTORED SETTINGS")
        throw new Exception("Rejected archive modified existing settings.");
    Console.WriteLine("PASS: transactional profile recovery, retained brushes, ZIP traversal rejection.");
}
finally { try { Directory.Delete(recoveryScratch, true); } catch { } }

// Portable project profiles contain references, not copied copyrighted assets
// and do not automatically execute or install third-party scripts.
var profileScratch = Path.Combine(Path.GetTempPath(),
    "Profile Export Test " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(profileScratch);
try
{
    var expected = ProjectProfiles.Validate(new ProjectProfiles.Profile("PixelTown",
        @"C:\Projects\PixelTown", new[]
        { "tool:split-tiles", "tool:split-tiles", "palette:db32", "godot:aseprite-wizard" },
        "Warm village assets; provisional 16×16 grid"));
    if (expected.ResourceIds.Length != 3)
        throw new Exception("Profile resource references were not deduplicated.");
    var filename = Path.Combine(profileScratch, "profile.json");
    ProjectProfiles.Export(expected, filename);
    var loaded = ProjectProfiles.Load(filename);
    if (loaded.Name != expected.Name ||
        !loaded.ResourceIds.SequenceEqual(expected.ResourceIds))
        throw new Exception("Portable JSON profile round trip failed.");
    try
    {
        ProjectProfiles.Validate(expected with { ResourceIds = new[] { "script:untrusted-code" } });
        throw new Exception("Unknown tool references were accepted.");
    }
    catch (InvalidDataException) { }
    Console.WriteLine("PASS: safe portable project profiles, no unknown resource identifiers.");
}
finally { try { Directory.Delete(profileScratch, true); } catch { } }


// Persistent local favorites never install executable code or allow unlisted IDs.
if (!LibraryFavorites.IsAllowed("tool:glass-floor") ||
    !LibraryFavorites.IsAllowed("palette:db32") ||
    LibraryFavorites.IsAllowed("tool:unknown-resource"))
    throw new Exception("Library favorites allowlist is incorrect.");
var initiallyLiked = LibraryFavorites.Read().Contains("tool:glass-floor");
var newLiked = LibraryFavorites.Toggle("tool:glass-floor");
if (newLiked == initiallyLiked)
    throw new Exception("Favorite toggle did not flip its value.");
LibraryFavorites.Toggle("tool:glass-floor");
if (LibraryFavorites.Read().Contains("tool:glass-floor") != initiallyLiked)
    throw new Exception("Favorite preference could not be restored.");
try
{
    LibraryFavorites.Toggle("tool:unreviewed-code");
    throw new Exception("Unknown Lua favorites must be rejected.");
}
catch (InvalidDataException) { }
Console.WriteLine("PASS: safe favorites persistence and resource allowlist.");


// v0.6.2: Pristine compiled binaries must be reusable without a compiler;
// SHA-256 checking must reject changes before a swap touches any installed app.
using (BuildCacheLease.Acquire())
{
    AsepriteBuildCache.Clear(Console.WriteLine);
    var fake = Path.Combine(Path.GetTempPath(),
        "Aseprite Speed Test " + Guid.NewGuid().ToString("N"));
    var sourceBin = Path.Combine(fake, "build", "bin");
    Directory.CreateDirectory(Path.Combine(sourceBin, "data"));
    var tag = "speed-smoke-v99.0.0";
    try
    {
        File.WriteAllText(Path.Combine(sourceBin, "aseprite.exe"), "test-only-not-a-real-executable");
        File.WriteAllText(Path.Combine(sourceBin, "data", "theme.png"), "upstream packaged asset");
        AsepriteBuildCache.Store(tag, "99.0.0", sourceBin, Console.WriteLine);
        if (!AsepriteBuildCache.HasBuild(tag, "99.0.0"))
            throw new Exception("Compiled cache did not record local build.");
        var stage1 = Path.Combine(fake, "staged1");
        if (!AsepriteBuildCache.TryStage(tag, "99.0.0", stage1, Console.WriteLine) ||
            File.ReadAllText(Path.Combine(stage1, "data", "theme.png")) != "upstream packaged asset")
            throw new Exception("Verified compiled build wasn't restored.");
        var cachePayload = Path.Combine(AsepriteBuildCache.Root,
            AsepriteBuildCache.Key(tag), "payload", "aseprite.exe");
        File.AppendAllText(cachePayload, "CACHE CORRUPTION");
        var stage2 = Path.Combine(fake, "staged2");
        if (AsepriteBuildCache.TryStage(tag, "99.0.0", stage2, Console.WriteLine))
            throw new Exception("Corrupted compiled cache was trusted.");
        if (Directory.Exists(stage2))
            throw new Exception("Rejected cache left a partially staged executable.");
        if (AsepriteBuildCache.Statistics().Versions < 1)
            throw new Exception("Cache storage usage wasn't reported.");
        Console.WriteLine("PASS: instant build restoration, SHA-256 verification, tamper refusal.");
    }
    finally
    {
        AsepriteBuildCache.Clear(Console.WriteLine);
        try { Directory.Delete(fake, recursive: true); } catch { }
    }
}
var speedSettings = BuildPerformance.Validate(new BuildPerformance.Options(true, 4));
if (BuildPerformance.EffectiveJobs(speedSettings) != 4 ||
    BuildPerformance.EffectiveJobs(new BuildPerformance.Options(false, 0)) is < 2 or > 12)
    throw new Exception("Ninja job calculation isn't bounded.");
try
{
    BuildPerformance.Validate(new BuildPerformance.Options(true, 100));
    throw new Exception("Unbounded Ninja job count was accepted.");
}
catch (InvalidDataException) { }
Console.WriteLine("PASS: bounded Ninja parallelism and cache settings.");

if (OperatingSystem.IsWindows())
{
    using var lease1 = BuildCacheLease.Acquire();
    try
    {
        using var duplicate = BuildCacheLease.Acquire();
        throw new Exception("Second concurrent build got an exclusive cache lock.");
    }
    catch (IOException) { }
}
Console.WriteLine("PASS: concurrent build/cache cleanup lock.");

if (OperatingSystem.IsWindows())
{
    var physical = Path.Combine(Path.GetTempPath(),
        "Source path with spaces " + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(physical);
    string? sharedFolder = null;
    try
    {
        using (var mounted = WindowsBuildDrive.Mount(physical))
        {
            var source = Path.Combine(mounted.Root, "source");
            Directory.CreateDirectory(Path.Combine(source, "laf", "misc"));
            var tag = "m124-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(source, "laf", "misc", "skia-tag.txt"), tag + "\n");
            using (var skia = SharedSkiaCache.Attach(source, Console.WriteLine))
            {
                sharedFolder = skia.TargetPath;
                File.WriteAllText(Path.Combine(skia.TargetPath, "dependency-test.txt"), "shared");
                if (File.ReadAllText(Path.Combine(source, ".deps", "skia-m124",
                    "dependency-test.txt")) != "shared")
                    throw new Exception("Skia directory junction does not route to persistent dependency cache.");
            }
            if (Directory.Exists(Path.Combine(source, ".deps", "skia-m124")))
                throw new Exception("Skia cache cleanup left a dangling junction.");
        }
        if (sharedFolder == null || !File.Exists(Path.Combine(sharedFolder, "dependency-test.txt")))
            throw new Exception("Skia cache dependency vanished after source cleanup.");
        Console.WriteLine("PASS: version-keyed shared Skia survives disposable build session.");
    }
    finally
    {
        if (sharedFolder != null && Directory.Exists(sharedFolder))
            Directory.Delete(sharedFolder, recursive: true);
        try { Directory.Delete(physical, true); } catch { }
    }
}


// A previously installed source build retained by the rollback subsystem is
// eligible for an instant switch even if new compiled caches aren't populated.
var restoreFixtures = Path.Combine(Path.GetTempPath(),
    "Aseprite backup fastpath " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(restoreFixtures);
try
{
    var requestedInstall = Path.Combine(restoreFixtures, "Aseprite");
    var backup = requestedInstall + ".backup-20261012-010203-test";
    Directory.CreateDirectory(backup);
    File.WriteAllText(Path.Combine(backup, "aseprite.exe"), "existing installation");
    File.WriteAllText(Path.Combine(backup, "aseprite-manager.json"),
        "{\"version\":\"1.3.18.2\",\"release\":\"v1.3.18.2\"}");
    File.WriteAllText(Path.Combine(backup, "custom-project.aseprite"), "user art preserved");
    if (!AsepriteBuildCache.HasPreviousInstallBackup(requestedInstall,
        "v1.3.18.2", "1.3.18.2"))
        throw new Exception("Existing valid prior-install version not discoverable.");
    var stage = Path.Combine(restoreFixtures, "stage");
    if (!AsepriteBuildCache.TryStagePreviousInstall(requestedInstall, "v1.3.18.2",
        "1.3.18.2", stage, Console.WriteLine))
        throw new Exception("Manager failed to stage retained previous installation.");
    if (File.ReadAllText(Path.Combine(stage, "custom-project.aseprite")) != "user art preserved" ||
        !File.Exists(Path.Combine(backup, "aseprite.exe")))
        throw new Exception("Fastpath must copy backup, never move it or discard user art.");
    if (AsepriteBuildCache.HasPreviousInstallBackup(requestedInstall,
        "v1.3.18.1", "1.3.18.1"))
        throw new Exception("Backup from a different tag was selected.");
    Console.WriteLine("PASS: instant previously-installed version staging, original backup unchanged.");
}
finally { try { Directory.Delete(restoreFixtures, true); } catch { } }
