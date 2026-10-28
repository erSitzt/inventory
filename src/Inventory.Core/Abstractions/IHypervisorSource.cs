using System.Net;

namespace Inventory.Core.Abstractions;

/// <summary>Zugang zu einer Virtualisierungsverwaltung, z. B. einem vCenter.</summary>
/// <param name="Url">Adresse, z. B. https://vcenter.corp.example; ohne Pfad.</param>
/// <param name="CertificateThumbprint">
/// SHA-256-Fingerabdruck eines Zertifikats, dem vertraut wird, obwohl Windows es nicht kennt (selbst signiert); leer = nur
/// Zertifikate, denen Windows vertraut.
/// </param>
public sealed record HypervisorConnection(string Url, NetworkCredential Credential, string? CertificateThumbprint);

/// <summary>Physischer Host, wie ihn die Verwaltung führt.</summary>
/// <param name="Id">Kennung in der Verwaltung (beim vCenter die Managed-Object-ID, z. B. host-42).</param>
/// <param name="CpuCores">Physische Kerne insgesamt, ohne Hyperthreading.</param>
public sealed record HypervisorHostInfo(string Id, string Name, string? Cluster, int? CpuSockets, int? CpuCores, string? Model, bool Connected);

/// <summary>Virtuelle Maschine, wie sie die Verwaltung führt; Vorlagen zählen nicht.</summary>
/// <param name="BiosUuid">SMBIOS-UUID, wie der Gast sie meldet (beim vCenter config.uuid).</param>
/// <param name="GuestHostName">Rechnername laut Gastwerkzeugen (VMware Tools), falls bekannt.</param>
public sealed record HypervisorVmInfo(string Id, string Name, string? BiosUuid, int? VirtualCpus, string? HostId, bool PoweredOn, string? GuestHostName, string? GuestOs);

public sealed record HypervisorInventory(IReadOnlyList<HypervisorHostInfo> Hosts, IReadOnlyList<HypervisorVmInfo> VirtualMachines, string? Version);

/// <summary>Liest Hosts, Cluster und virtuelle Maschinen aus einer Virtualisierungsverwaltung; nur lesend.</summary>
public interface IHypervisorSource
{
    /// <exception cref="RemoteException">Die Verwaltung ist nicht erreichbar, lehnt die Anmeldung ab oder ihr Zertifikat ist nicht vertrauenswürdig.</exception>
    Task<HypervisorInventory> ReadAsync(HypervisorConnection connection, CancellationToken ct = default);
}
