using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace AsepriteManager;

/// <summary>Use a scoped Actions token only for API calls during trusted CI.
/// Release assets, raw.githubusercontent and codeload requests are *never*
/// sent an authorization header. Public desktop use needs no credentials.</summary>
public static class GitHubApi
{
    public static async Task<HttpResponseMessage> GetAsync(HttpClient client,
        string url, CancellationToken ct)
    {
        var uri = new Uri(url);
        if (uri.Scheme != "https" || uri.Host != "api.github.com")
            throw new InvalidOperationException("Authenticated calls must target GitHub's HTTPS API.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        var token = Environment.GetEnvironmentVariable("ASEPRITE_MANAGER_GITHUB_API_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, ct);
    }
}
