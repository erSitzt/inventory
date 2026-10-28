using System.Reflection;
using System.Text.Json;

namespace Inventory.Core.Licensing;

/// <summary>Ein Lizenzprodukt, wie es im Bestand geführt wird.</summary>
/// <param name="Key">Fester Schlüssel, z. B. WindowsServerDatacenterCore; so steht er im Bestand und in der CSV-Datei.</param>
/// <param name="Unit">"core" (Kerne, gekauft in Paketen zu <see cref="MicrosoftRules.PackSize"/>) oder "licence".</param>
/// <param name="ListPricePerPack">Listenpreis je Paket als Vergleichswert, solange keine eigenen Preise eingetragen sind.</param>
public sealed record LicenseProduct(string Key, string Name, string Unit, decimal? ListPricePerPack = null)
{
    public bool PerCore => Unit == "core";
}

/// <summary>Ein Dokument von Microsoft, auf dem die Regeln beruhen (Lizenzleitfaden, Product Terms).</summary>
public sealed record RuleDocument(string Title, string Url);

/// <summary>Regeln für SQL Server aus rules/microsoft/sql-server.json.</summary>
/// <param name="PerVmRequiresSoftwareAssuranceFromMajorVersion">Ab dieser Hauptversion (16 = SQL Server 2022) ist die Lizenzierung je VM nur mit SA oder Abonnement erlaubt.</param>
/// <param name="ListPrices">Listenpreise je 2-Kern-Paket bzw. je Lizenz (Server, CAL) als Vergleichswert.</param>
public sealed record SqlServerRules(string Version, DateOnly ValidFrom, string Source, int MinCoresPerProcessor, int MinCoresPerVm,
    int PerVmRequiresSoftwareAssuranceFromMajorVersion, IReadOnlyList<string> FreeEditions, IReadOnlyList<string> NonProductionEditions,
    IReadOnlyDictionary<string, decimal> ListPrices)
{
    /// <summary>Die Dokumente von Microsoft zu SQL Server, zum Nachlesen.</summary>
    public IReadOnlyList<RuleDocument> Documents { get; init; } = [];
}

public sealed record WindowsServerRules(int MinCoresPerProcessor, int MinCoresPerServer, int StandardVmsPerLicense, int PerVmMinCores,
    bool PerVmRequiresSoftwareAssurance, int LicenceReassignmentDays);

/// <summary>
/// Die Regeln der Microsoft-Lizenzierung aus rules/microsoft (eingebettet). Werte stehen nicht im Code: eine geänderte Regel ist
/// eine geänderte Datei mit neuer Version, und jedes Ergebnis nennt die Version, mit der es gerechnet wurde.
/// </summary>
public sealed record MicrosoftRules(string Version, DateOnly ValidFrom, string Source, int PackSize, WindowsServerRules WindowsServer, IReadOnlyList<LicenseProduct> Products)
{
    public const string WindowsServerStandard = "WindowsServerStandardCore";
    public const string WindowsServerDatacenter = "WindowsServerDatacenterCore";
    public const string WindowsServerPerVm = "WindowsServerPerVmCore";

    public const string SqlEnterprise = "SqlServerEnterpriseCore";
    public const string SqlStandard = "SqlServerStandardCore";
    public const string SqlStandardServer = "SqlServerStandardServer";
    public const string SqlCal = "SqlServerCal";

    private static readonly Lazy<MicrosoftRules> Loaded = new(() => Parse(Read("windows-server.json")) with { SqlServer = ParseSql(Read("sql-server.json")) });

    /// <summary>Die Dokumente von Microsoft zu Windows Server, zum Nachlesen.</summary>
    public IReadOnlyList<RuleDocument> Documents { get; init; } = [];

    /// <summary>Die SQL-Server-Regeln; Teil des Regelwerks, aber eine eigene Datei mit eigener Version.</summary>
    public SqlServerRules SqlServer { get; init; } = null!;

    /// <summary>Preis je Paket bzw. Lizenz: eigener Preis, sonst Listenpreis aus den Regeldateien.</summary>
    public decimal Price(string product, IReadOnlyDictionary<string, decimal> own) =>
        own.TryGetValue(product, out var price) ? price
        : Product(product)?.ListPricePerPack ?? (SqlServer?.ListPrices.TryGetValue(product, out var list) == true ? list : 0m);

    public static MicrosoftRules Current => Loaded.Value;

    public LicenseProduct? Product(string key) => Products.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Auf ganze Pakete aufrunden (Kerne werden in 2er-Paketen gekauft).</summary>
    public int RoundToPacks(int cores) => (cores + PackSize - 1) / PackSize * PackSize;

    private static string Read(string file)
    {
        using var stream = typeof(MicrosoftRules).Assembly.GetManifestResourceStream("Inventory.Core.Rules." + file)
                           ?? throw new InvalidOperationException($"Rules file {file} is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <exception cref="InvalidDataException">Die Datei ist unvollständig oder unplausibel.</exception>
    public static SqlServerRules ParseSql(string json)
    {
        var rules = JsonSerializer.Deserialize<SqlServerRules>(json, Options) ?? throw new InvalidDataException("Empty SQL Server rules file.");
        if (string.IsNullOrWhiteSpace(rules.Version) || rules.MinCoresPerProcessor < 1 || rules.MinCoresPerVm < 1 || rules.FreeEditions is null || rules.ListPrices is null)
            throw new InvalidDataException("The SQL Server rules are incomplete or implausible.");
        return rules;
    }

    /// <exception cref="InvalidDataException">Die Datei ist unvollständig oder unplausibel.</exception>
    public static MicrosoftRules Parse(string json)
    {
        var rules = JsonSerializer.Deserialize<MicrosoftRules>(json, Options) ?? throw new InvalidDataException("Empty rules file.");
        var server = rules.WindowsServer ?? throw new InvalidDataException("windowsServer is missing.");
        if (string.IsNullOrWhiteSpace(rules.Version) || rules.PackSize < 1 || server.MinCoresPerProcessor < 1 || server.MinCoresPerServer < server.MinCoresPerProcessor
            || server.StandardVmsPerLicense < 1 || server.PerVmMinCores < 1)
            throw new InvalidDataException("The Windows Server rules are incomplete or implausible.");
        if (rules.Products is not { Count: > 0 } || rules.Products.GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidDataException("Products are missing or not unique.");
        foreach (var required in new[] { WindowsServerStandard, WindowsServerDatacenter, WindowsServerPerVm })
            if (rules.Product(required) is null)
                throw new InvalidDataException($"Product {required} is missing.");
        return rules;
    }
}
