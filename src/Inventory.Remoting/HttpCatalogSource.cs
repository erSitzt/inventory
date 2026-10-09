using System.Text.Json;
using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

/// <summary>Lädt den Katalog von einer Web-Adresse oder aus einer lokalen Datei und die Setups von den Herstellern.</summary>
public sealed class HttpCatalogSource(HttpClient http) : ICatalogSource
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public async Task<Catalog> LoadAsync(string location, CancellationToken ct = default)
    {
        try
        {
            var json = location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || location.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? await http.GetStringAsync(location, ct)
                : await File.ReadAllTextAsync(location, ct);
            return JsonSerializer.Deserialize<Catalog>(json, Options) ?? new Catalog();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or UnauthorizedAccessException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new RemoteException($"The package library could not be loaded ({location}): {ex.Message}", ex);
        }
    }

    public async Task DownloadAsync(string url, string path, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Manche Hersteller liefern ohne Browser-Kennung eine Fehlerseite statt des Setups.
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Inventory");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(file, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new RemoteException($"Download from {url} failed: {ex.Message}", ex);
        }
    }
}
