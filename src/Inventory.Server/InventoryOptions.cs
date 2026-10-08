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

    /// <summary>SID der lokalen Administratoren; der Name wäre je nach Windows-Sprache verschieden (Administrators/Administratoren).</summary>
    public const string LocalAdministratorsSid = "S-1-5-32-544";

    /// <summary>Gruppen, deren Mitglieder die Oberfläche und die API nutzen dürfen: Name ("FIRMA\IT-Admins") oder SID.</summary>
    public string[] AllowedGroups { get; set; } = [LocalAdministratorsSid];

    public bool IsAllowed(System.Security.Claims.ClaimsPrincipal user) => AllowedGroups.Any(group =>
        group.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
            ? user.HasClaim(c => c.Type is System.Security.Claims.ClaimTypes.GroupSid or System.Security.Claims.ClaimTypes.PrimaryGroupSid
                                 && c.Value.Equals(group, StringComparison.OrdinalIgnoreCase))
            : user.IsInRole(group));

    /// <summary>Gruppen für die Anzeige; SIDs werden unter Windows in Namen übersetzt.</summary>
    public IEnumerable<string> DescribeAllowedGroups() => AllowedGroups.Select(group =>
    {
        if (!OperatingSystem.IsWindows() || !group.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
            return group;
        try
        {
            return new System.Security.Principal.SecurityIdentifier(group).Translate(typeof(System.Security.Principal.NTAccount)).Value;
        }
        catch (Exception ex) when (ex is System.Security.Principal.IdentityNotMappedException or ArgumentException)
        {
            return group;
        }
    });

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
