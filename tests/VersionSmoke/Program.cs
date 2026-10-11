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
    }
    finally { try { Directory.Delete(rootWithSpaces, true); } catch { } }
}
