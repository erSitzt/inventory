namespace Inventory.Core;

public enum ChassisKind
{
    Unknown,
    Desktop,
    Laptop,
    Tablet,
    Server,
    VirtualMachine,
}

/// <summary>Ordnet die SMBIOS-Gehäusetypen den Geräteklassen zu, nach denen im Alltag gefragt wird.</summary>
public static class Chassis
{
    private static readonly string[] VirtualModels = ["Virtual Machine", "VMware", "VirtualBox", "KVM", "HVM domU", "Standard PC (", "Parallels"];

    /// <param name="type">Win32_SystemEnclosure.ChassisTypes, erster Eintrag.</param>
    /// <param name="model">Modellbezeichnung; virtuelle Maschinen melden beliebige Gehäusetypen und sind nur daran zu erkennen.</param>
    public static ChassisKind Kind(int? type, string? model = null)
    {
        if (model is not null && VirtualModels.Any(v => model.Contains(v, StringComparison.OrdinalIgnoreCase)))
            return ChassisKind.VirtualMachine;
        return type switch
        {
            3 or 4 or 5 or 6 or 7 or 13 or 15 or 16 or 24 or 34 or 35 or 36 => ChassisKind.Desktop,
            8 or 9 or 10 or 14 or 31 or 32 => ChassisKind.Laptop,
            11 or 30 => ChassisKind.Tablet,
            17 or 23 or 25 or 28 or 29 => ChassisKind.Server,
            _ => ChassisKind.Unknown,
        };
    }

    /// <summary>Englische Bezeichnung; zugleich der Schlüssel für die Übersetzung.</summary>
    public static string Label(ChassisKind kind) => kind switch
    {
        ChassisKind.Desktop => "Desktop",
        ChassisKind.Laptop => "Laptop",
        ChassisKind.Tablet => "Tablet",
        ChassisKind.Server => "Server",
        ChassisKind.VirtualMachine => "Virtual machine",
        _ => "unknown",
    };
}
