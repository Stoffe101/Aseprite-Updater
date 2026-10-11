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
    if (generated.Split('\n').Count(x => x.Contains("A7865D")) != 1)
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
