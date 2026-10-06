using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace Modexa.Core.Remote;

public sealed record UpdateInfo(bool Available, string? LatestVersion, string? Url, string? Notes);

/// <summary>
/// Checks GitHub Releases for a newer version of the public (Free) build. The repo name is a
/// placeholder until the client confirms it. Any failure (offline, rate-limited) returns
/// "no update" silently.
/// </summary>
public static class UpdateChecker
{
    private const string Owner = "farshad-zamani";
    private const string Repo = "modexa"; // TODO: confirm final public repo name with the client.
    private const string UserAgent = "Modexa-Updater";

    public static async Task<UpdateInfo> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            string url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            string json = await http.GetStringAsync(url);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            string? htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            string? body = root.TryGetProperty("body", out var b) ? b.GetString() : null;

            var latest = ParseVersion(tag);
            var current = Assembly.GetEntryAssembly()?.GetName().Version
                          ?? Assembly.GetExecutingAssembly().GetName().Version;

            bool available = latest != null && current != null && latest > current;
            return new UpdateInfo(available, tag, htmlUrl, body);
        }
        catch
        {
            return new UpdateInfo(false, null, null, null);
        }
    }

    private static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var cleaned = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(cleaned, out var v) ? v : null;
    }
}
