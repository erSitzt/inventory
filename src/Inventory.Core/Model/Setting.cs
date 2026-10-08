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
}
