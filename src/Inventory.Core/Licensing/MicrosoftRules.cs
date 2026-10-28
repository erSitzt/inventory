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

    private static readonly Lazy<MicrosoftRules> Loaded = new(() => Parse(Read("windows-server.json")));

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
