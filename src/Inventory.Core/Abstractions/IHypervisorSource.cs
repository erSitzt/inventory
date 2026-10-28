using System.Net;

namespace Inventory.Core.Abstractions;

/// <summary>Art der Virtualisierungsverwaltung; der Wert steht in der Datenbank, neue Arten nur anhängen.</summary>
public enum HypervisorKind
{
    VCenter = 0,
    /// <summary>XCP-ng bzw. Citrix Hypervisor, über die XAPI des Pool-Masters.</summary>
    XcpNg = 1,
    /// <summary>XCP-ng-Pools über die REST-Schnittstelle von Xen Orchestra, mit Token statt Kennwort.</summary>
    XenOrchestra = 2,
}

/// <summary>Zugang zu einer Virtualisierungsverwaltung, z. B. einem vCenter.</summary>
/// <param name="Url">Adresse, z. B. https://vcenter.corp.example oder der Pool-Master; ohne Pfad.</param>
/// <param name="CertificateThumbprint">
/// SHA-256-Fingerabdruck eines Zertifikats, dem vertraut wird, obwohl Windows es nicht kennt (selbst signiert); leer = nur
/// Zertifikate, denen Windows vertraut.
/// </param>
public sealed record HypervisorConnection(string Url, NetworkCredential Credential, string? CertificateThumbprint);

/// <summary>Physischer Host, wie ihn die Verwaltung führt.</summary>
/// <param name="Id">Kennung in der Verwaltung (beim vCenter die Managed-Object-ID, z. B. host-42, bei XCP-ng die UUID).</param>
/// <param name="Cluster">vCenter-Cluster bzw. XCP-ng-Pool.</param>
/// <param name="CpuCores">Physische Kerne insgesamt, ohne Hyperthreading.</param>
/// <param name="ClusterDrs">Der Cluster verschiebt VMs selbst (vSphere DRS eingeschaltet); null = unbekannt bzw. gibt es bei der Quelle nicht.</param>
/// <param name="ClusterHa">Der Cluster bzw. Pool startet VMs nach einem Hostausfall auf einem anderen Host (HA); null = unbekannt.</param>
public sealed record HypervisorHostInfo(string Id, string Name, string? Cluster, int? CpuSockets, int? CpuCores, string? Model, bool Connected,
    bool? ClusterDrs = null, bool? ClusterHa = null);

/// <summary>Virtuelle Maschine, wie sie die Verwaltung führt; Vorlagen zählen nicht.</summary>
/// <param name="BiosUuid">SMBIOS-UUID, wie der Gast sie meldet (beim vCenter config.uuid, bei XCP-ng die UUID der VM).</param>
/// <param name="GuestHostName">Rechnername laut Gastwerkzeugen (VMware Tools, Xen-Gastagent), falls bekannt.</param>
public sealed record HypervisorVmInfo(string Id, string Name, string? BiosUuid, int? VirtualCpus, string? HostId, bool PoweredOn, string? GuestHostName, string? GuestOs);

public sealed record HypervisorInventory(IReadOnlyList<HypervisorHostInfo> Hosts, IReadOnlyList<HypervisorVmInfo> VirtualMachines, string? Version);

/// <summary>Liest Hosts, Cluster und virtuelle Maschinen aus einer Virtualisierungsverwaltung; nur lesend.</summary>
public interface IHypervisorSource
{
    HypervisorKind Kind { get; }

    /// <exception cref="RemoteException">Die Verwaltung ist nicht erreichbar, lehnt die Anmeldung ab oder ihr Zertifikat ist nicht vertrauenswürdig.</exception>
    Task<HypervisorInventory> ReadAsync(HypervisorConnection connection, CancellationToken ct = default);
}
