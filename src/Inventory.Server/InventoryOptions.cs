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
    /// <remarks>
    /// Bewusst ohne Vorbelegung: die Konfiguration hängt ihre Einträge an vorhandene an, eine vorbelegte Gruppe
    /// bliebe also zusätzlich zu den konfigurierten erlaubt. Der Standard greift erst in <see cref="EffectiveGroups"/>.
    /// </remarks>
    public string[] AllowedGroups { get; set; } = [];

    /// <summary>Die konfigurierten Gruppen; ist keine konfiguriert, die lokalen Administratoren des Servers.</summary>
    public string[] EffectiveGroups => AllowedGroups.Length > 0 ? AllowedGroups : [LocalAdministratorsSid];

    private static readonly string[] GroupClaimTypes =
    [
        System.Security.Claims.ClaimTypes.GroupSid, System.Security.Claims.ClaimTypes.PrimaryGroupSid,
        // Greift jemand vom Server selbst aus ohne erhöhte Rechte zu, markiert die Benutzerkontensteuerung seine
        // Administratorgruppen als "nur zum Ablehnen". Mitglied ist er trotzdem, und nur darum geht es hier.
        System.Security.Claims.ClaimTypes.DenyOnlySid, System.Security.Claims.ClaimTypes.DenyOnlyPrimaryGroupSid,
    ];

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _groupSids = new(StringComparer.OrdinalIgnoreCase);

    public bool IsAllowed(System.Security.Claims.ClaimsPrincipal user) => EffectiveGroups.Any(group =>
        ResolveSid(group) is { } sid
            ? user.HasClaim(c => GroupClaimTypes.Contains(c.Type) && c.Value.Equals(sid, StringComparison.OrdinalIgnoreCase))
            : user.IsInRole(group));

    /// <summary>SID einer Gruppe; null, wenn sich der Name (noch) nicht auflösen lässt, etwa weil kein Domänencontroller erreichbar ist.</summary>
    private string? ResolveSid(string group)
    {
        if (group.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
            return group;
        if (_groupSids.TryGetValue(group, out var known) && known is not null)
            return known;
        string? sid = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                sid = new System.Security.Principal.NTAccount(group).Translate(typeof(System.Security.Principal.SecurityIdentifier)).Value;
            }
            catch (Exception ex) when (ex is System.Security.Principal.IdentityNotMappedException or SystemException)
            {
                // Nicht merken: beim nächsten Zugriff erneut versuchen.
                return null;
            }
        }
        _groupSids[group] = sid;
        return sid;
    }

    /// <summary>Gruppen für die Anzeige; SIDs werden unter Windows in Namen übersetzt.</summary>
    public IEnumerable<string> DescribeAllowedGroups() => EffectiveGroups.Select(group =>
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
