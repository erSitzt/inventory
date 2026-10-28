namespace Inventory.Core.Model;

/// <summary>Installierte Serverrolle oder -funktion laut Win32_ServerFeature.</summary>
public class ComputerServerFeature
{
    public int Id { get; set; }
    public int ComputerId { get; set; }
    /// <summary>ID laut Win32_ServerFeature, siehe <see cref="ServerFeatures"/>.</summary>
    public int FeatureId { get; set; }
    public required string Name { get; set; }
}

/// <summary>SQL-Server-Instanz laut Registry.</summary>
public class ComputerSqlInstance
{
    public int Id { get; set; }
    public int ComputerId { get; set; }
    public required string Name { get; set; }
    public string? InstanceId { get; set; }
    /// <summary>Edition laut Setup, z. B. "Standard Edition (64-bit)".</summary>
    public string? Edition { get; set; }
    public string? Version { get; set; }
    public string? PatchLevel { get; set; }
    public bool Clustered { get; set; }
}

/// <summary>Virtuelle Maschine, die ein Hyper-V-Host meldet.</summary>
public class HostedVirtualMachine
{
    public int Id { get; set; }
    /// <summary>Der Host.</summary>
    public int ComputerId { get; set; }
    public required string Name { get; set; }
    /// <summary>Normalisiert wie <see cref="Computer.BiosUuid"/>.</summary>
    public string? BiosUuid { get; set; }
    public int? VirtualCpus { get; set; }
    public bool Running { get; set; }
    /// <summary>Der inventarisierte Rechner, der diese VM ist; null = nicht inventarisiert oder nicht zugeordnet.</summary>
    public int? GuestComputerId { get; set; }
}

/// <summary>Physischer Host aus einer Virtualisierungsverwaltung (vCenter, XCP-ng); kein inventarisierter Rechner.</summary>
public class HypervisorHost
{
    public int Id { get; set; }
    public Abstractions.HypervisorKind Kind { get; set; }
    /// <summary>Kennung in der Verwaltung, z. B. host-42.</summary>
    public required string ExternalId { get; set; }
    public required string Name { get; set; }
    public string? Cluster { get; set; }
    public int? CpuSockets { get; set; }
    /// <summary>Physische Kerne insgesamt.</summary>
    public int? CpuCores { get; set; }
    public string? Model { get; set; }
    public bool Connected { get; set; }
    public DateTime SeenUtc { get; set; }
    public List<HypervisorVm> VirtualMachines { get; set; } = [];
}

/// <summary>Virtuelle Maschine aus einer Virtualisierungsverwaltung (vCenter, XCP-ng).</summary>
public class HypervisorVm
{
    public int Id { get; set; }
    public Abstractions.HypervisorKind Kind { get; set; }
    public required string ExternalId { get; set; }
    public required string Name { get; set; }
    /// <summary>Normalisiert wie <see cref="Computer.BiosUuid"/>.</summary>
    public string? BiosUuid { get; set; }
    public int? VirtualCpus { get; set; }
    /// <summary>Der Host, auf dem die VM gerade läuft bzw. registriert ist.</summary>
    public int? HostId { get; set; }
    public bool PoweredOn { get; set; }
    public string? GuestHostName { get; set; }
    public string? GuestOs { get; set; }
    /// <summary>Der inventarisierte Rechner, der diese VM ist; null = nicht inventarisiert oder nicht zugeordnet.</summary>
    public int? GuestComputerId { get; set; }
    public DateTime SeenUtc { get; set; }
}

/// <summary>Woran der Host einer VM erkannt wurde.</summary>
public static class HostSources
{
    /// <summary>Der Host führt eine VM mit derselben BIOS-UUID.</summary>
    public const string BiosUuid = "uuid";
    /// <summary>Die Gastdienste nennen den Host, und ein Rechner dieses Namens ist inventarisiert.</summary>
    public const string GuestReport = "guest";
    /// <summary>Eine Virtualisierungsverwaltung (vCenter, XCP-ng) führt eine VM mit derselben BIOS-UUID; der Wert stammt aus der Zeit nur mit vCenter.</summary>
    public const string Hypervisor = "vcenter";
    /// <summary>Eine Virtualisierungsverwaltung führt eine VM, deren Gastname zum Rechner passt (ohne UUID-Treffer).</summary>
    public const string HypervisorName = "vcenter-name";
}

/// <summary>Die Serverrollen, auf die es bei der Lizenzierung ankommt (IDs aus Win32_ServerFeature).</summary>
public static class ServerFeatures
{
    public const int HyperV = 20;
    public const int FailoverClustering = 33;
    public const int RemoteDesktopSessionHost = 130;
}

/// <summary>
/// SMBIOS-UUIDs in einer vergleichbaren Form: ohne Klammern, groß geschrieben. Platzhalter wie lauter Nullen oder F gelten als
/// keine UUID. Manche Werkzeuge vertauschen die Bytes der ersten drei Felder; <see cref="Same"/> erkennt beide Schreibweisen.
/// </summary>
public static class BiosUuids
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Guid.TryParse(value.Trim().Trim('{', '}'), out var guid))
            return null;
        var text = guid.ToString("D").ToUpperInvariant();
        return text.Replace("-", "").All(c => c == '0') || text.Replace("-", "").All(c => c == 'F') ? null : text;
    }

    public static bool Same(string? a, string? b)
    {
        if (Normalize(a) is not { } left || Normalize(b) is not { } right)
            return false;
        return left == right || left == Swapped(right);
    }

    /// <summary>Dieselbe UUID mit vertauschter Bytefolge in den ersten drei Feldern.</summary>
    public static string Swapped(string normalized)
    {
        var bytes = Guid.Parse(normalized).ToByteArray();
        return new Guid(bytes.Take(4).Reverse().Concat(bytes.Skip(4).Take(2).Reverse()).Concat(bytes.Skip(6).Take(2).Reverse()).Concat(bytes.Skip(8)).ToArray())
            .ToString("D").ToUpperInvariant();
    }
}
