namespace Inventory.Core.Model;

public class ComputerSoftware
{
    public int Id { get; set; }
    public int ComputerId { get; set; }
    public required string Name { get; set; }
    public string? Version { get; set; }
    public string? Publisher { get; set; }
    public string? InstallDate { get; set; }
    public bool Is64Bit { get; set; }
    public bool PerUser { get; set; }
    public string? UninstallString { get; set; }
}

public class ComputerHotfix
{
    public int Id { get; set; }
    public int ComputerId { get; set; }
    public required string HotfixId { get; set; }
    public string? Description { get; set; }
    public string? InstalledOn { get; set; }
}

public class ComputerDisk
{
    public int Id { get; set; }
    public int ComputerId { get; set; }
    public required string Drive { get; set; }
    public string? Label { get; set; }
    public string? FileSystem { get; set; }
    public long SizeBytes { get; set; }
    public long FreeBytes { get; set; }
}

public class ComputerNetworkAdapter
{
    public int Id { get; set; }
    public int ComputerId { get; set; }
    public required string Name { get; set; }
    public string? MacAddress { get; set; }
    /// <summary>Mehrere Werte durch Komma getrennt.</summary>
    public string? IpAddresses { get; set; }
    public string? Gateways { get; set; }
    public string? DnsServers { get; set; }
    public bool DhcpEnabled { get; set; }
}

public class ComputerWindowsService
{
    public int Id { get; set; }
    public int ComputerId { get; set; }
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? State { get; set; }
    public string? StartMode { get; set; }
    public string? Account { get; set; }
}
