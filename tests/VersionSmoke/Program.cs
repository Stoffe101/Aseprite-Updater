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
var installed = Path.Combine(scratch, "Aseprite");
var staged = Path.Combine(scratch, "Staged");
var customTheme = Path.Combine(installed, "data", "themes", "my-theme.xml");
var project = Path.Combine(installed, "Sprites", "my-art.aseprite");
try
{
    Directory.CreateDirectory(Path.GetDirectoryName(customTheme)!);
    Directory.CreateDirectory(Path.GetDirectoryName(project)!);
    Directory.CreateDirectory(staged);
    File.WriteAllText(Path.Combine(installed, "aseprite.exe"), "OLD");
    File.WriteAllText(customTheme, "MY THEME");
    File.WriteAllText(project, "MY SPRITE");
    File.WriteAllText(Path.Combine(staged, "aseprite.exe"), "NEW");
    InstallSafety.PreserveUserContent(installed, staged);
    if (File.ReadAllText(Path.Combine(staged, "data", "themes", "my-theme.xml")) != "MY THEME")
        throw new Exception("Theme was not preserved.");
    if (File.ReadAllText(Path.Combine(staged, "Sprites", "my-art.aseprite")) != "MY SPRITE")
        throw new Exception("Artwork in install folder was not preserved.");
    InstallSafety.SwapVerifiedInstallation(staged, installed,
        () => File.ReadAllText(Path.Combine(installed, "aseprite.exe")) == "NEW", _ => { });
    if (File.ReadAllText(Path.Combine(installed, "aseprite.exe")) != "NEW")
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
        InstallSafety.SwapVerifiedInstallation(badStage, installed, () => false, _ => { });
        throw new Exception("Expected validation failure.");
    }
    catch (InvalidDataException) { }
    if (File.ReadAllText(Path.Combine(installed, "aseprite.exe")) != "NEW")
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
