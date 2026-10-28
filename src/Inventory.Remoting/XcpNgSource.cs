using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

/// <summary>
/// Liest Hosts, Pool und VMs aus XCP-ng (oder Citrix Hypervisor) über die XAPI des Pool-Masters (JSON-RPC unter /jsonrpc). Nur
/// lesende Aufrufe: Anmelden, get_all_records für Pool, Hosts, VMs und Gastangaben, Abmelden. Antwortet ein Pool-Mitglied, das nicht
/// Master ist, wird einmal zum Master weitergegangen.
/// </summary>
public sealed class XcpNgSource(Func<HttpMessageHandler, HttpMessageHandler>? wrap = null) : IHypervisorSource
{
    public HypervisorKind Kind => HypervisorKind.XcpNg;

    private const string NullRef = "OpaqueRef:NULL";

    public async Task<HypervisorInventory> ReadAsync(HypervisorConnection connection, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(connection.Url.Trim(), UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
            throw new RemoteException("The XCP-ng address must start with https://.");
        var pinned = new PinnedCertificateHandler(connection.CertificateThumbprint);
        using var client = new HttpClient(wrap?.Invoke(pinned.Handler) ?? pinned.Handler) { Timeout = TimeSpan.FromMinutes(5) };
        try
        {
            var endpoint = new Uri(baseUri, "/jsonrpc");
            string session;
            try
            {
                session = await LoginAsync(client, endpoint, connection, ct);
            }
            catch (XapiException ex) when (ex.Code == "HOST_IS_SLAVE" && ex.Data.FirstOrDefault() is { Length: > 0 } master)
            {
                // Nur der Pool-Master beantwortet Abfragen; er steht in der Fehlermeldung.
                endpoint = new UriBuilder(endpoint) { Host = master }.Uri;
                session = await LoginAsync(client, endpoint, connection, ct);
            }
            try
            {
                var pools = await CallAsync(client, endpoint, "pool.get_all_records", [session], ct);
                var hostRecords = await CallAsync(client, endpoint, "host.get_all_records", [session], ct);
                var vmRecords = await CallAsync(client, endpoint, "VM.get_all_records", [session], ct);
                var metrics = await CallAsync(client, endpoint, "VM_guest_metrics.get_all_records", [session], ct);

                var pool = pools.AsObject().Select(p => p.Value).FirstOrDefault();
                var poolName = Text(pool, "name_label");
                var masterRef = Text(pool, "master");
                bool? poolHa = pool?["ha_enabled"] is JsonValue ha && ha.TryGetValue<bool>(out var haOn) ? haOn : null;
                var hosts = new List<HypervisorHostInfo>();
                var hostIds = new Dictionary<string, string>();
                string? version = null;
                foreach (var (reference, record) in hostRecords.AsObject())
                {
                    var id = Text(record, "uuid") ?? reference;
                    hostIds[reference] = id;
                    var cpu = record?["cpu_info"];
                    var sockets = Int(cpu, "socket_count");
                    var threads = Int(cpu, "cpu_count");
                    // Ältere XAPI nennt nur die logischen CPUs; ohne Kerne je Sockel bzw. Threads je Kern bleiben die Kerne unbekannt.
                    int? cores = Int(cpu, "cores_per_socket") is { } perSocket && sockets is { } s ? perSocket * s
                        : threads is { } t && Int(cpu, "threads_per_core") is { } perCore && perCore > 0 ? t / perCore : null;
                    hosts.Add(new HypervisorHostInfo(id, Text(record, "hostname") ?? Text(record, "name_label") ?? id,
                        string.IsNullOrWhiteSpace(poolName) ? null : poolName, sockets, cores, Text(record?["bios_strings"], "system-product-name"),
                        record?["enabled"]?.GetValue<bool>() ?? true, null, poolHa));
                    if (reference == masterRef)
                        version = string.Join(" ", new[] { Text(record?["software_version"], "product_brand"), Text(record?["software_version"], "product_version") }.Where(v => v is not null));
                }

                var vms = new List<HypervisorVmInfo>();
                foreach (var (reference, record) in vmRecords.AsObject())
                {
                    if (Bool(record, "is_a_template") || Bool(record, "is_a_snapshot") || Bool(record, "is_control_domain"))
                        continue;
                    var guest = Text(record, "guest_metrics") is { } metricsRef && metricsRef != NullRef ? metrics[metricsRef] : null;
                    var os = guest?["os_version"];
                    var resident = Text(record, "resident_on");
                    vms.Add(new HypervisorVmInfo(Text(record, "uuid") ?? reference, Text(record, "name_label") ?? reference, Text(record, "uuid"),
                        Int(record, "VCPUs_max") ?? Int(record, "VCPUs_at_startup"),
                        resident is not null && resident != NullRef && hostIds.TryGetValue(resident, out var hostId) ? hostId : null,
                        Text(record, "power_state") == "Running", Text(os, "hostname"), Text(os, "name")?.Split('|')[0]));
                }
                return new HypervisorInventory(hosts, vms, string.IsNullOrWhiteSpace(version) ? null : version);
            }
            finally
            {
                try
                {
                    await CallAsync(client, endpoint, "session.logout", [session], CancellationToken.None);
                }
                catch (Exception ex) when (ex is XapiException or RemoteException or HttpRequestException or TaskCanceledException)
                {
                }
            }
        }
        catch (XapiException ex)
        {
            throw new RemoteException(ex.Code is "SESSION_AUTHENTICATION_FAILED"
                ? "XCP-ng rejected the user name or password."
                : ex.Code is "RBAC_PERMISSION_DENIED"
                    ? "The XCP-ng account lacks the permission to read the pool (role read-only or higher)."
                    : $"XCP-ng reported an error: {ex.Code}");
        }
        catch (HttpRequestException ex) when (pinned.Rejected is { } rejected)
        {
            throw new RemoteException($"The certificate of the XCP-ng pool master is not trusted. If it is the right one, enter its SHA-256 thumbprint: {rejected}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new RemoteException($"XCP-ng is not reachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new RemoteException("XCP-ng did not answer in time.", ex);
        }
    }

    private static async Task<string> LoginAsync(HttpClient client, Uri endpoint, HypervisorConnection connection, CancellationToken ct) =>
        (await CallAsync(client, endpoint, "session.login_with_password",
            [connection.Credential.UserName, connection.Credential.Password, "1.0", "Inventory"], ct)).GetValue<string>();

    private sealed class XapiException(string code, IReadOnlyList<string> data) : Exception(code)
    {
        public string Code { get; } = code;
        public new IReadOnlyList<string> Data { get; } = data;
    }

    private static async Task<JsonNode> CallAsync(HttpClient client, Uri endpoint, string method, object[] parameters, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters, id = 1 });
        using var response = await client.PostAsync(endpoint, new StringContent(body, Encoding.UTF8, "application/json"), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? answer;
        try
        {
            answer = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            throw new RemoteException($"XCP-ng answered {(int)response.StatusCode} {response.ReasonPhrase} without a JSON-RPC response. Is the address the pool master?");
        }
        if (answer?["error"] is { } error)
        {
            // XAPI: message = Fehlercode, data = weitere Angaben (z. B. die Adresse des Masters).
            var data = error["data"] is JsonArray array ? array.Select(d => d?.ToString() ?? "").ToList() : [];
            throw new XapiException(error["message"]?.ToString() ?? "UNKNOWN", data);
        }
        return answer?["result"] ?? throw new RemoteException("XCP-ng sent an empty response.");
    }

    private static string? Text(JsonNode? node, string property) =>
        node?[property] is JsonValue value && value.ToString().Trim() is { Length: > 0 } text ? text : null;

    private static int? Int(JsonNode? node, string property) => int.TryParse(Text(node, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool Bool(JsonNode? node, string property) => node?[property] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
}
