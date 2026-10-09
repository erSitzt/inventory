namespace Inventory.Core.Abstractions;

/// <summary>Ein fertiges Paket aus der Bibliothek: woher das Setup kommt und wie es still installiert wird.</summary>
public sealed class CatalogEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Publisher { get; set; }
    public string Version { get; set; } = "";
    public string? Description { get; set; }
    public string? Homepage { get; set; }
    public string? License { get; set; }
    public string? LicenseUrl { get; set; }
    /// <summary>Ausführliche Beschreibung; wie alle folgenden Angaben nur gefüllt, wenn das Manifest sie nennt.</summary>
    public string? Details { get; set; }
    public List<string> Tags { get; set; } = [];
    public string? Moniker { get; set; }
    public string? SupportUrl { get; set; }
    public string? ReleaseNotesUrl { get; set; }
    public string? ReleaseDate { get; set; }
    /// <summary>Technik des Setups laut winget: wix, msi, burn, inno, nullsoft oder exe.</summary>
    public string InstallerType { get; set; } = "";
    public string? Architecture { get; set; }
    /// <summary>machine oder user; null, wenn das Manifest nichts festlegt.</summary>
    public string? Scope { get; set; }
    public string? MinimumOsVersion { get; set; }
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Arguments { get; set; } = "";
    public List<int> SuccessCodes { get; set; } = [];
    /// <summary>Woran das installierte Programm im Inventar zu erkennen ist.</summary>
    public string Match { get; set; } = "";
}

public sealed class Catalog
{
    public DateTime? Generated { get; set; }
    public string? Source { get; set; }
    public List<CatalogEntry> Packages { get; set; } = [];
}

public interface ICatalogSource
{
    /// <exception cref="RemoteException">Der Katalog ist nicht erreichbar oder nicht lesbar.</exception>
    Task<Catalog> LoadAsync(string location, CancellationToken ct = default);

    /// <exception cref="RemoteException">Der Download ist fehlgeschlagen.</exception>
    Task DownloadAsync(string url, string path, CancellationToken ct = default);
}
