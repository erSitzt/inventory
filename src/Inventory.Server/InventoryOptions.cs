namespace Inventory.Server;

public sealed class InventoryOptions
{
    public const string SectionName = "Inventory";

    /// <summary>Ablage für Datenbank und Schlüssel. Leer = %ProgramData%\Inventory bzw. unter Linux ~/.local/share/Inventory.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>Keine echten Zugriffe auf Zielrechner. Leer = unter Windows aus, sonst an.</summary>
    public bool? Simulate { get; set; }

    /// <summary>"Windows" (Negotiate/Kerberos) oder "None". Leer = Windows, außer im Simulationsmodus.</summary>
    public string? Authentication { get; set; }

    /// <summary>Gruppen, deren Mitglieder die Oberfläche und die API nutzen dürfen, z. B. "FIRMA\IT-Admins".</summary>
    public string[] AllowedGroups { get; set; } = [@"BUILTIN\Administrators"];

    public bool ResolveWindowsAuthentication() =>
        Authentication is null ? OperatingSystem.IsWindows() && !ResolveSimulate() : Authentication.Equals("Windows", StringComparison.OrdinalIgnoreCase);

    public string ResolveDataDirectory()
    {
        if (!string.IsNullOrWhiteSpace(DataDirectory))
            return Path.GetFullPath(DataDirectory);
        var root = OperatingSystem.IsWindows() ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.LocalApplicationData;
        return Path.Combine(Environment.GetFolderPath(root), "Inventory");
    }

    public bool ResolveSimulate() => Simulate ?? !OperatingSystem.IsWindows();
}
