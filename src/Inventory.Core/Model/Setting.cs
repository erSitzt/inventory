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
    public const string SkipTargetNameCheck = "SkipTargetNameCheck";
    public const string AgentServerUrl = "Agent.ServerUrl";
    public const string AgentEnrollmentToken = "Agent.EnrollmentToken";
    public const string AdSyncEnabled = "AdSync.Enabled";
    public const string AdSyncServer = "AdSync.Server";
    public const string AdSyncBaseDns = "AdSync.BaseDns";
    public const string AdSyncCredentialId = "AdSync.CredentialId";
    public const string AdSyncIntervalHours = "AdSync.IntervalHours";
    public const string AdSyncLastRunUtc = "AdSync.LastRunUtc";
    public const string AdSyncLastResult = "AdSync.LastResult";
}
