namespace Inventory.Core.Model;

public class Setting
{
    public required string Key { get; set; }
    public string? Value { get; set; }
}

public static class SettingKeys
{
    public const string DefaultTransport = "DefaultTransport";
    public const string TransportFallback = "TransportFallback";
    /// <summary><see cref="AgentlessPolicy"/>; leer = immer erlaubt.</summary>
    public const string AgentlessPolicy = "Transport.AgentlessPolicy";
    public const string SkipTargetNameCheck = "SkipTargetNameCheck";
    /// <summary>So viele Minuten darf ein Rechner nach einem Neustart-Schritt brauchen, bis er wieder erreichbar ist.</summary>
    public const string RebootWaitMinutes = "Deployment.RebootWaitMinutes";
    /// <summary>Wartungsfenster (JSON) für Rechner, die in keiner Sammlung mit eigenem Fenster sind; leer = keine Einschränkung.</summary>
    public const string MaintenanceDefaultWindow = "Maintenance.DefaultWindow";
    /// <summary>So viele Rechner werden über alle Deployments hinweg gleichzeitig bedient.</summary>
    public const string MaxParallelTargets = "Deployment.MaxParallelTargets";
    /// <summary>"True" = automatisch übernommene Bibliotheks-Updates behalten die Freigabe des Pakets.</summary>
    public const string AutoApproveLibraryUpdates = "Packages.AutoApproveLibraryUpdates";
    /// <summary>Protokolleinträge werden so viele Tage aufbewahrt; "0" = unbegrenzt.</summary>
    public const string AuditRetentionDays = "Audit.RetentionDays";
    /// <summary>Mandant und Anwendung (öffentlicher Client) für die Freigabe des Runners in Microsoft Defender.</summary>
    public const string DefenderTenant = "Defender.Tenant";
    public const string DefenderClientId = "Defender.ClientId";
    /// <summary>SHA-256 des Runners, der zuletzt in Defender freigegeben wurde.</summary>
    public const string DefenderAllowedHash = "Defender.AllowedHash";
    public const string AgentServerUrl = "Agent.ServerUrl";
    public const string AgentEnrollmentToken = "Agent.EnrollmentToken";
    /// <summary>Gesetzt, sobald die Beispiel-Zeitpläne einmal angelegt wurden; gelöschte Beispiele kommen nicht wieder.</summary>
    public const string ExampleSchedulesCreated = "Examples.SchedulesCreated";
    /// <summary>Gesetzt, sobald die Beispiel-Berichte einmal angelegt wurden.</summary>
    public const string ExampleReportsCreated = "Examples.ReportsAdded";
    public const string HeartbeatEnabled = "Heartbeat.Enabled";
    public const string HeartbeatIntervalMinutes = "Heartbeat.IntervalMinutes";
    public const string HeartbeatLastRunUtc = "Heartbeat.LastRunUtc";
    public const string HeartbeatLastResult = "Heartbeat.LastResult";
    public const string AdSyncEnabled = "AdSync.Enabled";
    public const string AdSyncServer = "AdSync.Server";
    public const string AdSyncBaseDns = "AdSync.BaseDns";
    public const string AdSyncCredentialId = "AdSync.CredentialId";
    public const string AdSyncIntervalHours = "AdSync.IntervalHours";
    public const string AdSyncExcludeDisabled = "AdSync.ExcludeDisabled";
    public const string AdSyncMissingAction = "AdSync.MissingAction";
    public const string AdSyncMissingDays = "AdSync.MissingDays";
    public const string AdSyncLastRunUtc = "AdSync.LastRunUtc";
    public const string AdSyncLastResult = "AdSync.LastResult";
}
