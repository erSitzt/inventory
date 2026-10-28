using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

/// <summary>
/// Liest Pools, Hosts und VMs aus Xen Orchestra über dessen REST-Schnittstelle (/rest/v0), angemeldet mit einem
/// Authentication Token statt eines Kennworts. Nur GET-Abfragen; Vorlagen, Snapshots und die Steuerdomäne führt Xen Orchestra
/// als eigene Typen und liefert sie hier gar nicht erst.
/// </summary>
public sealed class XenOrchestraSource(Func<HttpMessageHandler, HttpMessageHandler>? wrap = null) : IHypervisorSource
{
    public HypervisorKind Kind => HypervisorKind.XenOrchestra;

    private const string HostFields = "id,name_label,hostname,CPUs,bios_strings,enabled,$pool,version,productBrand";
    private const string VmFields = "id,name_label,CPUs,power_state,$container,os_version";

    /// <remarks>Das Token steht im Kennwort der Zugangsdaten; der Benutzername wird nicht gebraucht.</remarks>
    public async Task<HypervisorInventory> ReadAsync(HypervisorConnection connection, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(connection.Url.Trim(), UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
            throw new RemoteException("The Xen Orchestra address must start with https://.");
        if (string.IsNullOrWhiteSpace(connection.Credential.Password))
            throw new RemoteException("Enter the Xen Orchestra authentication token as the password of the credentials.");
        var pinned = new PinnedCertificateHandler(connection.CertificateThumbprint);
        using var client = new HttpClient(wrap?.Invoke(pinned.Handler) ?? pinned.Handler) { BaseAddress = new Uri(baseUri, "/rest/v0/"), Timeout = TimeSpan.FromMinutes(5) };
        // Xen Orchestra erwartet das Token als Cookie.
        client.DefaultRequestHeaders.Add("Cookie", "authenticationToken=" + connection.Credential.Password.Trim());
        try
        {
            var poolRecords = await GetAsync(client, "pools?fields=id,name_label,HA_enabled", ct);
            var pools = poolRecords.ToDictionary(p => Text(p, "id") ?? "", p => Text(p, "name_label"));
            var poolHa = poolRecords.ToDictionary(p => Text(p, "id") ?? "", p => p?["HA_enabled"] is JsonValue ha && ha.TryGetValue<bool>(out var on) ? on : (bool?)null);
            var hosts = new List<HypervisorHostInfo>();
            string? version = null;
            foreach (var host in await GetAsync(client, "hosts?fields=" + HostFields, ct))
            {
                var id = Text(host, "id") ?? "";
                var cpu = host?["CPUs"];
                var sockets = Int(cpu, "socket_count");
                var threads = Int(cpu, "cpu_count");
                // Wie bei der XAPI: ohne Kerne je Sockel bzw. Threads je Kern bleiben die physischen Kerne unbekannt.
                int? cores = Int(cpu, "cores_per_socket") is { } perSocket && sockets is { } s ? perSocket * s
                    : threads is { } t && Int(cpu, "threads_per_core") is { } perCore && perCore > 0 ? t / perCore : null;
                var pool = Text(host, "$pool") is { } poolId && pools.TryGetValue(poolId, out var poolName) && !string.IsNullOrWhiteSpace(poolName) ? poolName : null;
                hosts.Add(new HypervisorHostInfo(id, Text(host, "hostname") ?? Text(host, "name_label") ?? id, pool, sockets, cores,
                    Text(host?["bios_strings"], "system-product-name"), host?["enabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out var on) ? on : true,
                    null, Text(host, "$pool") is { } haPool ? poolHa.GetValueOrDefault(haPool) : null));
                version ??= string.Join(" ", new[] { Text(host, "productBrand"), Text(host, "version") }.Where(v => v is not null)) is { Length: > 0 } v ? v : null;
            }
            var hostIds = hosts.Select(h => h.Id).ToHashSet();
            var vms = (await GetAsync(client, "vms?fields=" + VmFields, ct)).Select(vm =>
            {
                var id = Text(vm, "id") ?? "";
                // $container ist der Host, solange die VM läuft, sonst der Pool.
                var container = Text(vm, "$container");
                return new HypervisorVmInfo(id, Text(vm, "name_label") ?? id, id, Int(vm?["CPUs"], "max") ?? Int(vm?["CPUs"], "number"),
                    container is not null && hostIds.Contains(container) ? container : null, Text(vm, "power_state") == "Running",
                    Text(vm?["os_version"], "hostname"), Text(vm?["os_version"], "name")?.Split('|')[0]);
            }).ToList();
            return new HypervisorInventory(hosts, vms, version is null ? "Xen Orchestra" : $"Xen Orchestra · {version}");
        }
        catch (HttpRequestException ex) when (pinned.Rejected is { } rejected)
        {
            throw new RemoteException($"The certificate of Xen Orchestra is not trusted. If it is the right one, enter its SHA-256 thumbprint: {rejected}", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new RemoteException("Xen Orchestra rejected the authentication token.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new RemoteException($"Xen Orchestra is not reachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new RemoteException("Xen Orchestra did not answer in time.", ex);
        }
    }

    private static async Task<List<JsonNode?>> GetAsync(HttpClient client, string path, CancellationToken ct)
    {
        using var response = await client.GetAsync(path, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonNode.Parse(text) is JsonArray array ? array.ToList() : throw new RemoteException("Xen Orchestra sent an unexpected response. Is the address right?");
        }
        catch (JsonException)
        {
            throw new RemoteException("Xen Orchestra sent no JSON. Is the address right?");
        }
    }

    private static string? Text(JsonNode? node, string property) =>
        node?[property] is JsonValue value && value.ToString().Trim() is { Length: > 0 } text ? text : null;

    private static int? Int(JsonNode? node, string property) => int.TryParse(Text(node, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}
