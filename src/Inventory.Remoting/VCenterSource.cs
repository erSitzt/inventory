using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using Inventory.Core.Abstractions;

namespace Inventory.Remoting;

/// <summary>
/// Liest Hosts, Cluster und VMs aus einem vCenter (oder einem einzelnen ESXi) über die SOAP-Schnittstelle vim25. Nur sie
/// liefert Sockel und Kerne der Hosts; die REST-Schnittstelle kennt die Hardware der Hosts nicht. Nur lesende Aufrufe:
/// Anmelden, Sichten anlegen, Eigenschaften abfragen, Abmelden.
/// </summary>
public sealed class VCenterSource(Func<HttpMessageHandler, HttpMessageHandler>? wrap = null) : IHypervisorSource
{
    public HypervisorKind Kind => HypervisorKind.VCenter;

    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Vim = "urn:vim25";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    /// <summary>Älteste unterstützte Version der Schnittstelle (vSphere 6.5); neuere vCenter beantworten sie ebenso.</summary>
    private const string SoapAction = "urn:vim25/6.5";
    private const int PageSize = 500;

    public async Task<HypervisorInventory> ReadAsync(HypervisorConnection connection, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(connection.Url.Trim(), UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
            throw new RemoteException("The vCenter address must start with https://.");
        var pinned = new PinnedCertificateHandler(connection.CertificateThumbprint, cookies: true);
        var handler = pinned.Handler;
        using var client = new HttpClient(wrap?.Invoke(handler) ?? handler) { BaseAddress = new Uri(baseUri, "/sdk"), Timeout = TimeSpan.FromMinutes(5) };
        var session = new Session(client);
        try
        {
            var content = await session.CallAsync("RetrieveServiceContent", new XElement(Vim + "_this", new XAttribute("type", "ServiceInstance"), "ServiceInstance"), ct);
            string Ref(string name) => content.Element(Vim + name)?.Value ?? throw new RemoteException($"The vCenter did not report its {name}.");
            var sessionManager = Ref("sessionManager");
            var propertyCollector = Ref("propertyCollector");
            var viewManager = Ref("viewManager");
            var rootFolder = Ref("rootFolder");
            var version = content.Element(Vim + "about")?.Element(Vim + "fullName")?.Value;

            await session.CallAsync("Login",
                [This("SessionManager", sessionManager), new XElement(Vim + "userName", connection.Credential.UserName), new XElement(Vim + "password", connection.Credential.Password)], ct);
            try
            {
                var clusters = (await RetrieveAsync(session, viewManager, rootFolder, propertyCollector, "ClusterComputeResource", ["name"], ct))
                    .ToDictionary(o => o.Id, o => o.Text("name"));
                var automation = await ClusterAutomationAsync(session, viewManager, rootFolder, propertyCollector, ct);
                var hosts = (await RetrieveAsync(session, viewManager, rootFolder, propertyCollector, "HostSystem",
                        ["name", "parent", "summary.hardware.numCpuPkgs", "summary.hardware.numCpuCores", "summary.hardware.model", "runtime.connectionState"], ct))
                    .Select(o => new HypervisorHostInfo(o.Id, o.Text("name") ?? o.Id,
                        o.Ref("parent") is { Type: "ClusterComputeResource" } parent && clusters.TryGetValue(parent.Id, out var cluster) ? cluster : null,
                        o.Int("summary.hardware.numCpuPkgs"), o.Int("summary.hardware.numCpuCores"), o.Text("summary.hardware.model"),
                        o.Text("runtime.connectionState") == "connected",
                        o.Ref("parent") is { Type: "ClusterComputeResource" } drsParent && automation.TryGetValue(drsParent.Id, out var drs) ? drs.Drs : null,
                        o.Ref("parent") is { Type: "ClusterComputeResource" } haParent && automation.TryGetValue(haParent.Id, out var ha) ? ha.Ha : null))
                    .ToList();
                var vms = (await RetrieveAsync(session, viewManager, rootFolder, propertyCollector, "VirtualMachine",
                        ["name", "config.uuid", "config.hardware.numCPU", "config.template", "config.guestFullName", "runtime.host", "runtime.powerState", "guest.hostName"], ct))
                    .Where(o => o.Text("config.template") != "true")
                    .Select(o => new HypervisorVmInfo(o.Id, o.Text("name") ?? o.Id, o.Text("config.uuid"), o.Int("config.hardware.numCPU"), o.Ref("runtime.host")?.Id,
                        o.Text("runtime.powerState") == "poweredOn", o.Text("guest.hostName"), o.Text("config.guestFullName")))
                    .ToList();
                return new HypervisorInventory(hosts, vms, version);
            }
            finally
            {
                // Abmelden gibt auch die angelegten Sichten frei; ein Fehler dabei ändert nichts am Ergebnis.
                try
                {
                    await session.CallAsync("Logout", This("SessionManager", sessionManager), CancellationToken.None);
                }
                catch (Exception ex) when (ex is RemoteException or HttpRequestException or TaskCanceledException)
                {
                }
            }
        }
        catch (HttpRequestException ex) when (pinned.Rejected is { } rejectedThumbprint)
        {
            throw new RemoteException($"The certificate of the vCenter is not trusted. If it is the right one, enter its SHA-256 thumbprint: {rejectedThumbprint}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new RemoteException($"The vCenter is not reachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new RemoteException("The vCenter did not answer in time.", ex);
        }
    }

    private static XElement This(string type, string id) => new(Vim + "_this", new XAttribute("type", type), id);

    /// <summary>
    /// DRS und HA je Cluster (für die Lizenzierung: wandern VMs selbsttätig?). Eigene Abfrage, damit ein vCenter, das die Eigenschaften
    /// nicht kennt oder nicht zeigen darf, den Abgleich nicht scheitern lässt; dann bleibt es "unbekannt".
    /// </summary>
    private static async Task<Dictionary<string, (bool? Drs, bool? Ha)>> ClusterAutomationAsync(Session session, string viewManager, string rootFolder,
        string propertyCollector, CancellationToken ct)
    {
        try
        {
            return (await RetrieveAsync(session, viewManager, rootFolder, propertyCollector, "ClusterComputeResource",
                    ["configuration.drsConfig.enabled", "configuration.dasConfig.enabled"], ct))
                .ToDictionary(o => o.Id, o => (Bool(o.Text("configuration.drsConfig.enabled")), Bool(o.Text("configuration.dasConfig.enabled"))));
        }
        catch (RemoteException)
        {
            return [];
        }

        static bool? Bool(string? text) => bool.TryParse(text, out var value) ? value : null;
    }

    /// <summary>Alle Objekte eines Typs mit den genannten Eigenschaften, über eine Container-Sicht ab dem Wurzelordner, seitenweise.</summary>
    private static async Task<List<ManagedObject>> RetrieveAsync(Session session, string viewManager, string rootFolder, string propertyCollector, string type,
        string[] properties, CancellationToken ct)
    {
        var view = (await session.CallAsync("CreateContainerView",
        [
            This("ViewManager", viewManager), new XElement(Vim + "container", new XAttribute("type", "Folder"), rootFolder),
            new XElement(Vim + "type", type), new XElement(Vim + "recursive", "true"),
        ], ct)).Value;
        var spec = new XElement(Vim + "specSet",
            new XElement(Vim + "propSet", new XElement(Vim + "type", type), properties.Select(p => new XElement(Vim + "pathSet", p))),
            new XElement(Vim + "objectSet",
                new XElement(Vim + "obj", new XAttribute("type", "ContainerView"), view),
                new XElement(Vim + "skip", "true"),
                new XElement(Vim + "selectSet", new XAttribute(Xsi + "type", "TraversalSpec"),
                    new XElement(Vim + "name", "traverseEntities"), new XElement(Vim + "type", "ContainerView"),
                    new XElement(Vim + "path", "view"), new XElement(Vim + "skip", "false"))));
        var result = new List<ManagedObject>();
        var page = await session.CallAsync("RetrievePropertiesEx",
            [This("PropertyCollector", propertyCollector), spec, new XElement(Vim + "options", new XElement(Vim + "maxObjects", PageSize))], ct);
        while (true)
        {
            result.AddRange(page.Elements(Vim + "objects").Select(ManagedObject.Parse));
            if (page.Element(Vim + "token")?.Value is not { Length: > 0 } token)
                break;
            page = await session.CallAsync("ContinueRetrievePropertiesEx", [This("PropertyCollector", propertyCollector), new XElement(Vim + "token", token)], ct);
        }
        return result;
    }

    private sealed class Session(HttpClient client)
    {
        public Task<XElement> CallAsync(string method, XElement argument, CancellationToken ct) => CallAsync(method, [argument], ct);

        /// <returns>Das Element returnval der Antwort; ohne Rückgabewert ein leeres.</returns>
        public async Task<XElement> CallAsync(string method, XElement[] arguments, CancellationToken ct)
        {
            var envelope = new XDocument(new XElement(Soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soapenv", Soap), new XAttribute(XNamespace.Xmlns + "urn", Vim), new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
                new XElement(Soap + "Body", new XElement(Vim + method, arguments))));
            using var request = new HttpRequestMessage(HttpMethod.Post, "") { Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml") };
            request.Headers.Add("SOAPAction", SoapAction);
            using var response = await client.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            XDocument document;
            try
            {
                document = XDocument.Parse(text);
            }
            catch (System.Xml.XmlException)
            {
                throw new RemoteException($"The vCenter answered {(int)response.StatusCode} {response.ReasonPhrase} without a SOAP response. Is the address right?");
            }
            var body = document.Root?.Element(Soap + "Body");
            if (body?.Element(Soap + "Fault") is { } fault)
            {
                var detail = fault.Element("detail")?.Elements().FirstOrDefault()?.Name.LocalName;
                var message = fault.Element("faultstring")?.Value ?? detail ?? "unknown error";
                throw new RemoteException(detail is "InvalidLoginFault" or "InvalidLogin"
                    ? "The vCenter rejected the user name or password."
                    : $"The vCenter reported an error: {message}");
            }
            return body?.Element(Vim + method + "Response")?.Element(Vim + "returnval") ?? new XElement(Vim + "returnval");
        }
    }

    /// <summary>Ein Objekt der Antwort mit seinen Eigenschaften (Pfad → Wert).</summary>
    private sealed record ManagedObject(string Id, Dictionary<string, XElement> Properties)
    {
        public static ManagedObject Parse(XElement objectContent) => new(
            objectContent.Element(Vim + "obj")?.Value ?? "",
            objectContent.Elements(Vim + "propSet")
                .Where(p => p.Element(Vim + "name") is not null && p.Element(Vim + "val") is not null)
                .GroupBy(p => p.Element(Vim + "name")!.Value)
                .ToDictionary(g => g.Key, g => g.First().Element(Vim + "val")!));

        public string? Text(string property) => Properties.TryGetValue(property, out var value) && value.Value.Trim() is { Length: > 0 } text ? text : null;

        public int? Int(string property) => int.TryParse(Text(property), out var value) ? value : null;

        /// <summary>Verweis auf ein anderes Objekt (Typ und ID).</summary>
        public (string Type, string Id)? Ref(string property) =>
            Properties.TryGetValue(property, out var value) && value.Value.Trim() is { Length: > 0 } id ? ((string)value.Attribute("type")! ?? "", id) : null;
    }
}
