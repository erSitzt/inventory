using System.Net;
using System.Text.Json;
using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

/// <summary>Liest Releases über die öffentliche GitHub-API (ohne Anmeldung; funktioniert nur für öffentliche Repositories).</summary>
public sealed class GitHubReleaseSource(HttpClient http) : IReleaseSource
{
    public const string SetupPrefix = "Inventory-Setup-";
    public const string ChecksumsName = "SHA256SUMS.txt";

    public async Task<ReleaseInfo?> GetLatestAsync(string repository, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
            // GitHub lehnt Anfragen ohne User-Agent ab.
            request.Headers.UserAgent.ParseAdd("Inventory-Update-Check");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
            return Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            throw new RemoteException($"Update check at GitHub failed: {ex.Message}", ex);
        }
    }

    public async Task DownloadAsync(string url, string path, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Inventory-Update-Check");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(file, ct);
    }

    internal static ReleaseInfo Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string? setupName = null, setupUrl = null, checksumsUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                var url = asset.GetProperty("browser_download_url").GetString();
                if (name.StartsWith(SetupPrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    (setupName, setupUrl) = (name, url);
                else if (name.Equals(ChecksumsName, StringComparison.OrdinalIgnoreCase))
                    checksumsUrl = url;
            }
        }
        return new ReleaseInfo(
            (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V'),
            root.TryGetProperty("body", out var body) ? body.GetString() : null,
            root.GetProperty("html_url").GetString() ?? "",
            root.TryGetProperty("published_at", out var published) && published.TryGetDateTime(out var date) ? date.ToUniversalTime() : null,
            setupName, setupUrl, checksumsUrl);
    }
}
