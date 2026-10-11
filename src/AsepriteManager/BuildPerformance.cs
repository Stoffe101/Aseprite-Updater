using System.IO;
using System.Text.Json;

namespace AsepriteManager;

/// <summary>Performance choices are opt-in where they involve executable
/// compiler wrappers; cache management is independent of user projects.</summary>
public static class BuildPerformance
{
    public sealed record Options(bool UseCompilerCache = true, int ParallelJobs = 0);

    public static string SettingsPath => Path.Combine(ManagerService.DataRoot, "build-performance.json");
    public static string CompilerCacheFolder => Path.Combine(ManagerService.DataRoot, "compiler-cache");
    public static Options Read()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new Options();
            var item = JsonSerializer.Deserialize<Options>(File.ReadAllText(SettingsPath));
            return item is null ? new Options() : Validate(item);
        }
        catch { return new Options(); }
    }
    public static Options Validate(Options options)
    {
        if (options.ParallelJobs is < 0 or > 32)
            throw new InvalidDataException("Parallel jobs must be between 0 (auto) and 32.");
        return options;
    }
    public static void Save(Options options)
    {
        Validate(options);
        Directory.CreateDirectory(ManagerService.DataRoot);
        var temp = SettingsPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(options,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, SettingsPath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static int EffectiveJobs(Options options)
    {
        Validate(options);
        // Auto caps very wide CPUs to reduce memory pressure in Ninja/MSVC
        // and leaves enough responsiveness for a Windows laptop.
        return options.ParallelJobs == 0 ? Math.Clamp(Environment.ProcessorCount, 2, 12)
                                         : options.ParallelJobs;
    }

    public static string? FindCompilerCache()
    {
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.Combine(path.Trim().Trim('"'), "sccache.exe"))
            .Concat(new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Microsoft\WinGet\Links\sccache.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    @".cargo\bin\sccache.exe")
            });
        return candidates.FirstOrDefault(File.Exists);
    }
}
