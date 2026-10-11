using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AsepriteManager;

public sealed record ReleaseInfo(string Tag, string Version, bool IsPreRelease, bool IsLatest, DateTimeOffset? PublishedAt)
{
    public string DisplayName => $"v{Version}" +
        (IsLatest ? "   •   LATEST" : IsPreRelease ? "   •   Preview" : "");
}

public sealed class ReleaseCatalog
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
    // Only version-shaped release tags from the official upstream repository can reach git.
    private static readonly Regex ValidTag = new(
        @"^v?(?:\d+\.){1,3}\d+(?:[-.](?:alpha|beta|rc|dev|pre)[a-zA-Z0-9.-]*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static ReleaseCatalog()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Stoffe101-AsepriteManager/0.2.0");
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<IReadOnlyList<ReleaseInfo>> GetPublishedReleasesAsync(CancellationToken ct = default)
    {
        // The latest endpoint is canonical for identifying the latest stable release.
        // The generic /releases list may start with a prerelease.
        var latestTag = await GetLatestStableTagAsync(ct);
        var releases = new Dictionary<string, ReleaseInfo>(StringComparer.OrdinalIgnoreCase);
        for (int page = 1; page <= 30; page++)
        {
            using var response = await Client.GetAsync(
                $"https://api.github.com/repos/aseprite/aseprite/releases?per_page=100&page={page}", ct);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var items = document.RootElement;
            if (items.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("GitHub's release list was not a JSON array.");

            foreach (var item in items.EnumerateArray())
            {
                if (item.GetProperty("draft").GetBoolean()) continue;
                var tag = item.GetProperty("tag_name").GetString() ?? "";
                if (!ValidTag.IsMatch(tag)) continue;
                var version = tag.TrimStart('v', 'V');
                var preview = item.GetProperty("prerelease").GetBoolean();
                DateTimeOffset? published = null;
                if (item.TryGetProperty("published_at", out var when) &&
                    when.ValueKind == JsonValueKind.String &&
                    when.TryGetDateTimeOffset(out var time)) published = time;
                releases[tag] = new ReleaseInfo(tag, version, preview,
                    string.Equals(tag, latestTag, StringComparison.OrdinalIgnoreCase), published);
            }
            if (items.GetArrayLength() < 100) break;
            if (page == 30)
                throw new InvalidDataException("The GitHub release list exceeded 3,000 entries; unable to guarantee completeness.");
        }
        if (!releases.ContainsKey(latestTag))
            throw new InvalidDataException("GitHub's latest stable release was missing from the published release pages.");
        return releases.Values.OrderByDescending(r => r.IsLatest)
            .ThenByDescending(r => r.PublishedAt)
            .ThenByDescending(r => r.Version, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<string> GetLatestStableTagAsync(CancellationToken ct)
    {
        using var response = await Client.GetAsync(
            "https://api.github.com/repos/aseprite/aseprite/releases/latest", ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("Expected the latest stable release.");
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!ValidTag.IsMatch(tag)) throw new InvalidDataException("Unexpected GitHub stable release tag.");
        return tag;
    }
}
