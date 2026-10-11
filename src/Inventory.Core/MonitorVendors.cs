namespace Inventory.Core;

/// <summary>Monitore nennen ihren Hersteller nur als dreistellige PnP-Kennung; die häufigen werden ausgeschrieben.</summary>
public static class MonitorVendors
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ACI"] = "ASUS", ["ACR"] = "Acer", ["AOC"] = "AOC", ["APP"] = "Apple", ["AUO"] = "AU Optronics", ["AUS"] = "ASUS",
        ["BNQ"] = "BenQ", ["BOE"] = "BOE", ["CMN"] = "Innolux", ["DEL"] = "Dell", ["EIZ"] = "EIZO", ["ENC"] = "EIZO",
        ["FUS"] = "Fujitsu", ["GBT"] = "Gigabyte", ["GSM"] = "LG", ["HPN"] = "HP", ["HWP"] = "HP", ["IVM"] = "iiyama",
        ["LEN"] = "Lenovo", ["LGD"] = "LG Display", ["MED"] = "Medion", ["MSI"] = "MSI", ["NEC"] = "NEC", ["PHL"] = "Philips",
        ["SAM"] = "Samsung", ["SDC"] = "Samsung Display", ["SEC"] = "Samsung", ["SHP"] = "Sharp", ["SNY"] = "Sony", ["VSC"] = "ViewSonic",
    };

    public static string? Name(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : Names.GetValueOrDefault(code.Trim(), code.Trim());
}
