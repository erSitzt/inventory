namespace Inventory.Server;

public sealed class InventoryOptions
{
    public const string SectionName = "Inventory";

    /// <summary>Ablage für Datenbank und Schlüssel. Leer = %ProgramData%\Inventory bzw. unter Linux ~/.local/share/Inventory.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>Keine echten Zugriffe auf Zielrechner. Leer = unter Windows aus, sonst an.</summary>
    public bool? Simulate { get; set; }

    /// <summary>
    /// "Windows" (Kerberos/NTLM), "Oidc" (z. B. Entra ID), beides mit Komma getrennt ("Windows,Oidc") oder "None".
    /// Leer = Windows, außer im Simulationsmodus.
    /// </summary>
    public string? Authentication { get; set; }

    /// <summary>Sprache der Oberfläche, wenn der Browser keine der verfügbaren Sprachen nennt, und der Hintergrundläufe (Protokoll). Leer = en.</summary>
    public string? Language { get; set; }

    public OidcOptions Oidc { get; set; } = new();

    public UpdateSettingsOptions Update { get; set; } = new();

    public CatalogOptions Catalog { get; set; } = new();

    private string[] Modes => (Authentication ?? "").Split([',', ';', ' ', '+'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool OidcEnabled => Modes.Contains("Oidc", StringComparer.OrdinalIgnoreCase);

    public bool AuthenticationEnabled => ResolveWindowsAuthentication() || OidcEnabled;

    /// <summary>SID der lokalen Administratoren; der Name wäre je nach Windows-Sprache verschieden (Administrators/Administratoren).</summary>
    public const string LocalAdministratorsSid = "S-1-5-32-544";

    /// <summary>Gruppen, deren Mitglieder die Oberfläche und die API nutzen dürfen: Name ("FIRMA\IT-Admins") oder SID.</summary>
    /// <remarks>
    /// Bewusst ohne Vorbelegung: die Konfiguration hängt ihre Einträge an vorhandene an, eine vorbelegte Gruppe
    /// bliebe also zusätzlich zu den konfigurierten erlaubt. Der Standard greift erst in <see cref="EffectiveGroups"/>.
    /// </remarks>
    public string[] AllowedGroups { get; set; } = [];

    /// <summary>
    /// Gruppen je Rolle (Name oder SID). <see cref="AllowedGroups"/> zählt zu den Administratoren; ohne jede Administratorgruppe
    /// sind es die lokalen Administratoren des Servers.
    /// </summary>
    public RoleGroups Roles { get; set; } = new();

    /// <summary>Die Administratorgruppen; ist keine konfiguriert, die lokalen Administratoren des Servers.</summary>
    public string[] EffectiveGroups => AllowedGroups.Concat(Roles.Admin).ToArray() is { Length: > 0 } groups ? groups : [LocalAdministratorsSid];

    /// <summary>Höchste Rolle, die sich aus den Gruppen eines Windows-Benutzers ergibt.</summary>
    public Inventory.Core.Model.AccessRole RoleOf(System.Security.Claims.ClaimsPrincipal user) =>
        EffectiveGroups.Any(group => InGroup(user, group)) ? Inventory.Core.Model.AccessRole.Admin
        : Roles.Operator.Any(group => InGroup(user, group)) ? Inventory.Core.Model.AccessRole.Operator
        : Roles.Viewer.Any(group => InGroup(user, group)) ? Inventory.Core.Model.AccessRole.Viewer
        : Inventory.Core.Model.AccessRole.None;

    private static readonly string[] GroupClaimTypes =
    [
        System.Security.Claims.ClaimTypes.GroupSid, System.Security.Claims.ClaimTypes.PrimaryGroupSid,
        // Greift jemand vom Server selbst aus ohne erhöhte Rechte zu, markiert die Benutzerkontensteuerung seine
        // Administratorgruppen als "nur zum Ablehnen". Mitglied ist er trotzdem, und nur darum geht es hier.
        System.Security.Claims.ClaimTypes.DenyOnlySid, System.Security.Claims.ClaimTypes.DenyOnlyPrimaryGroupSid,
    ];

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _groupSids = new(StringComparer.OrdinalIgnoreCase);

    private bool InGroup(System.Security.Claims.ClaimsPrincipal user, string group) =>
        ResolveSid(group) is { } sid
            ? user.HasClaim(c => GroupClaimTypes.Contains(c.Type) && c.Value.Equals(sid, StringComparison.OrdinalIgnoreCase))
            : user.IsInRole(group);

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

    /// <summary>Administratorgruppen für die Anzeige; SIDs werden unter Windows in Namen übersetzt.</summary>
    public IEnumerable<string> DescribeAllowedGroups() => Describe(EffectiveGroups);

    /// <summary>Gruppen für die Anzeige; SIDs werden unter Windows in Namen übersetzt.</summary>
    public static IEnumerable<string> Describe(IEnumerable<string> groups) => groups.Select(group =>
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
        Authentication is null ? OperatingSystem.IsWindows() && !ResolveSimulate() : Modes.Contains("Windows", StringComparer.OrdinalIgnoreCase);

    public string ResolveDataDirectory()
    {
        if (!string.IsNullOrWhiteSpace(DataDirectory))
            return Path.GetFullPath(DataDirectory);
        var root = OperatingSystem.IsWindows() ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.LocalApplicationData;
        return Path.Combine(Environment.GetFolderPath(root), "Inventory");
    }

    public bool ResolveSimulate() => Simulate ?? !OperatingSystem.IsWindows();
}

/// <summary>Gruppen je Rolle; die höchste zutreffende Rolle gilt.</summary>
public sealed class RoleGroups
{
    public string[] Admin { get; set; } = [];
    public string[] Operator { get; set; } = [];
    public string[] Viewer { get; set; } = [];
}

/// <summary>Anmeldung über OpenID Connect, z. B. Microsoft Entra ID.</summary>
public sealed class OidcOptions
{
    /// <summary>Entra-Mandant (GUID oder Domäne). Daraus ergibt sich die Authority, sofern sie nicht ausdrücklich gesetzt ist.</summary>
    public string? TenantId { get; set; }

    /// <summary>Aussteller-Adresse für andere Anbieter, z. B. https://idp.firma.de/realms/intern.</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Administratoren: Werte aus dem Gruppen-Claim (bei Entra ID die Objekt-IDs der Gruppen) oder App-Rollen.
    /// Ohne Eintrag hier und in <see cref="Roles"/> wird niemand zugelassen.
    /// </summary>
    public string[] AllowedGroups { get; set; } = [];

    /// <summary>Gruppen oder App-Rollen je Rolle; <see cref="AllowedGroups"/> zählt zu den Administratoren.</summary>
    public RoleGroups Roles { get; set; } = new();

    /// <summary>Claim mit den Gruppen; App-Rollen ("roles") werden zusätzlich immer geprüft.</summary>
    public string GroupClaim { get; set; } = "groups";

    /// <summary>Beschriftung der Schaltfläche auf der Anmeldeseite.</summary>
    public string DisplayName { get; set; } = "Microsoft Entra ID";

    public string ResolveAuthority() =>
        !string.IsNullOrWhiteSpace(Authority) ? Authority.TrimEnd('/')
        : !string.IsNullOrWhiteSpace(TenantId) ? $"https://login.microsoftonline.com/{TenantId}/v2.0"
        : throw new InvalidOperationException("OIDC sign-in requires Inventory:Oidc:TenantId or Inventory:Oidc:Authority.");

    /// <summary>Höchste Rolle, die sich aus Gruppen und App-Rollen des Benutzers ergibt.</summary>
    public Inventory.Core.Model.AccessRole RoleOf(System.Security.Claims.ClaimsPrincipal user)
    {
        bool In(IEnumerable<string> groups) => user.Claims.Any(c =>
            (c.Type.Equals(GroupClaim, StringComparison.OrdinalIgnoreCase) || c.Type == "roles")
            && groups.Contains(c.Value, StringComparer.OrdinalIgnoreCase));
        return In(AllowedGroups.Concat(Roles.Admin)) ? Inventory.Core.Model.AccessRole.Admin
            : In(Roles.Operator) ? Inventory.Core.Model.AccessRole.Operator
            : In(Roles.Viewer) ? Inventory.Core.Model.AccessRole.Viewer
            : Inventory.Core.Model.AccessRole.None;
    }

    public IEnumerable<string> AllGroups => AllowedGroups.Concat(Roles.Admin).Concat(Roles.Operator).Concat(Roles.Viewer);

    /// <exception cref="InvalidOperationException">Die Konfiguration ist unvollständig.</exception>
    public void Validate()
    {
        _ = ResolveAuthority();
        if (string.IsNullOrWhiteSpace(ClientId))
            throw new InvalidOperationException("OIDC sign-in requires Inventory:Oidc:ClientId.");
        if (!AllGroups.Any())
            throw new InvalidOperationException("OIDC sign-in requires at least one group or app role in Inventory:Oidc:AllowedGroups or Inventory:Oidc:Roles; otherwise every account of the tenant could sign in.");
    }
}

/// <summary>Suche nach neuen Versionen und Selbstaktualisierung.</summary>
public sealed class UpdateSettingsOptions
{
    /// <summary>false = der Server fragt nie bei GitHub nach.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>GitHub-Repository mit den Releases, in der Form besitzer/name.</summary>
    public string Repository { get; set; } = "erSitzt/inventory";

    /// <summary>
    /// Gesetzt = das Setup wird nur installiert, wenn es eine gültige Signatur dieses Herausgebers trägt.
    /// Leer (solange Releases unsigniert sind) sichert nur die Prüfsumme die Übertragung.
    /// </summary>
    public string? RequiredPublisher { get; set; }
}

/// <summary>Quelle der Paketbibliothek.</summary>
public sealed class CatalogOptions
{
    /// <summary>Web-Adresse oder lokaler Pfad der catalog.json; z. B. ein eigener, intern gepflegter Katalog.</summary>
    public string Url { get; set; } = "https://raw.githubusercontent.com/erSitzt/inventory/catalog/catalog.json";

    /// <summary>Suchindex über das gesamte winget-Repository (ungeprüfte Einträge); leer = Suche abschalten.</summary>
    public string? IndexUrl { get; set; } = "https://raw.githubusercontent.com/erSitzt/inventory/catalog/winget.json";

    /// <summary>Ordner mit je einer Datei "&lt;winget-ID&gt;.json" für die älteren Versionen eines Programms; leer = nur die aktuelle Version anbieten.</summary>
    public string? VersionsUrl { get; set; } = "https://raw.githubusercontent.com/erSitzt/inventory/catalog/versions";
}
