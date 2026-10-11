using System.IO;

namespace AsepriteManager;

/// <summary>
/// cmd.exe does not parse its /c command string like an ordinary argv array.
/// Execute a fixed, relative batch filename with the source folder as cwd.
/// This avoids brittle nested-quote handling of absolute paths containing spaces.
/// </summary>
public static class WindowsBatchLauncher
{
    public const string ScriptName = "build-with-msvc.cmd";

    public static string[] Arguments(string scriptName = ScriptName)
    {
        if (Path.GetFileName(scriptName) != scriptName ||
            scriptName.Contains('"') || scriptName.Contains('%') ||
            scriptName.Contains('\r') || scriptName.Contains('\n') ||
            !scriptName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected a simple .cmd filename.", nameof(scriptName));

        return new[] { "/d", "/c", @".\" + scriptName };
    }

    public static void WriteAsepriteWrapper(
        string sourceDirectory, string vcvarsBatch, string gitBash, IEnumerable<string> toolFolders)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException(sourceDirectory);
        foreach (var value in toolFolders.Append(vcvarsBatch).Append(gitBash))
            if (value.Contains('"') || value.Contains('%') || value.Contains('\r') || value.Contains('\n'))
                throw new InvalidOperationException("Build-tool paths containing unsupported command-script characters are not supported.");
        if (!File.Exists(vcvarsBatch)) throw new FileNotFoundException("MSVC environment batch file missing.", vcvarsBatch);
        if (!File.Exists(gitBash)) throw new FileNotFoundException("Git Bash missing.", gitBash);
        var extra = string.Join(";", toolFolders.Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        var lines = new[]
        {
            "@echo off",
            "setlocal DisableDelayedExpansion",
            $"set \"PATH={extra};%PATH%\"",
            $"call \"{vcvarsBatch}\"",
            "if errorlevel 1 exit /b %errorlevel%",
            $"\"{gitBash}\" ./build.sh --auto --norun",
            "exit /b %errorlevel%"
        };
        File.WriteAllLines(Path.Combine(sourceDirectory, ScriptName), lines);
    }
}
