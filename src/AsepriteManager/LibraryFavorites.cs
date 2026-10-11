using System.IO;
using System.Text.Json;

namespace AsepriteManager;

/// <summary>Per-user favorites are purely visual preferences; they do not
/// execute code, install files or modify Aseprite configuration.</summary>
public static class LibraryFavorites
{
    public static string FilePath => Path.Combine(ManagerService.DataRoot, "library-favorites.json");
    public static IReadOnlyList<string> Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return Array.Empty<string>();
            if (new FileInfo(FilePath).Length > 50000) return Array.Empty<string>();
            var ids = JsonSerializer.Deserialize<string[]>(File.ReadAllText(FilePath));
            return ids?.Where(IsAllowed).Distinct(StringComparer.Ordinal).ToArray()
                ?? Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    public static bool IsAllowed(string id) =>
        WorkshopLibrary.Tools.Any(t => id == "tool:" + t.Id) ||
        PaletteGallery.Presets.Any(p => id == "palette:" + p.Id);

    public static bool Toggle(string id)
    {
        if (!IsAllowed(id)) throw new InvalidDataException("Unknown resource favorite ID.");
        var state = new HashSet<string>(Read(), StringComparer.Ordinal);
        var added = state.Add(id);
        if (!added) state.Remove(id);
        Directory.CreateDirectory(ManagerService.DataRoot);
        var temp = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(state.OrderBy(x => x).ToArray()));
            File.Move(temp, FilePath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return added;
    }
}
