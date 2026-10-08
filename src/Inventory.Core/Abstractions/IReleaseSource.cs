namespace Inventory.Core.Abstractions;

/// <param name="SetupUrl">Download des Setups; null, wenn das Release keines enthält.</param>
/// <param name="ChecksumsUrl">Datei mit SHA-256-Prüfsummen (Format: Hash, Leerzeichen, Dateiname).</param>
public sealed record ReleaseInfo(string Version, string? Notes, string PageUrl, DateTime? PublishedUtc, string? SetupName, string? SetupUrl, string? ChecksumsUrl);

/// <summary>Woher der Server erfährt, dass es eine neuere Version gibt, und woher er sie lädt.</summary>
public interface IReleaseSource
{
    /// <summary>Neuestes fertiges Release (keine Vorabversionen); null, wenn es keines gibt.</summary>
    /// <exception cref="RemoteException">Die Quelle ist nicht erreichbar.</exception>
    Task<ReleaseInfo?> GetLatestAsync(string repository, CancellationToken ct = default);

    Task DownloadAsync(string url, string path, CancellationToken ct = default);
}
