using System.Net.Http;
using System.Text.Json;
using Clicky.Diagnostics;

namespace Clicky.Services;

/// <summary>Details of a newer release, if one is available.</summary>
public readonly record struct UpdateInfo(string LatestVersion, string ReleaseUrl);

/// <summary>
/// Best-effort update check against the GitHub releases API. This is the only network
/// call the otherwise-offline app makes, it's non-blocking, and it fails silently — it
/// never auto-downloads or installs anything, it just surfaces "a newer version exists"
/// so the user can grab it themselves. If the repo has no releases yet (404), it's a no-op.
/// </summary>
public static class UpdateChecker
{
    private const string LatestReleaseApi = "https://api.github.com/repos/jobelshaji95/clicky-hack/releases/latest";
    private const string ReleasesPage = "https://github.com/jobelshaji95/clicky-hack/releases";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.UserAgent.ParseAdd("Clicky-Windows-UpdateCheck");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 404 == no releases published yet; anything else == transient. Either way, no-op.
                return null;
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(contentStream).ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("tag_name", out var tagElement))
            {
                return null;
            }

            var tag = tagElement.GetString();
            if (string.IsNullOrWhiteSpace(tag))
            {
                return null;
            }

            var releaseUrl = document.RootElement.TryGetProperty("html_url", out var urlElement)
                ? urlElement.GetString() ?? ReleasesPage
                : ReleasesPage;

            var latestVersion = ParseVersion(tag);
            var currentVersion = typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

            if (latestVersion is null || latestVersion <= currentVersion)
            {
                return null;
            }

            ClickyLog.Info("Update", $"Newer version available: {tag} (current {currentVersion}).");
            return new UpdateInfo(tag, releaseUrl);
        }
        catch (Exception exception)
        {
            ClickyLog.Warn("Update", exception.Message);
            return null;
        }
    }

    /// <summary>Parses a release tag like "v1.2.0" or "1.2" into a comparable Version.</summary>
    private static Version? ParseVersion(string tag)
    {
        var cleaned = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(cleaned, out var version) ? version : null;
    }
}
