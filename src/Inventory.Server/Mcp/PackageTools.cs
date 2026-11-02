using System.ComponentModel;
using System.Security.Cryptography;
using Inventory.Contracts;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Server.Services;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Inventory.Server.Mcp;

/// <summary>
/// Pakete und schnelle Skripte von KI-Assistenten. Alles, was hier entsteht, ist als KI-Paket gekennzeichnet und läuft erst,
/// nachdem ein angemeldeter Administrator es in der Oberfläche freigegeben hat (wartende Anfrage unter Deployments).
/// </summary>
[McpServerToolType, Authorize(Policy = AccessControl.OperatorPolicy)]
public sealed class PackageTools(McpCaller caller, PackageService packages, DeploymentRequestService requests, CollectionService collections, IHttpContextAccessor http)
{
    /// <summary>Größe einer Datei, die der Server von einer Adresse lädt.</summary>
    public const long MaxDownloadBytes = 4L * 1024 * 1024 * 1024;
    /// <summary>Größe einer Datei, die direkt im Aufruf (Base64) mitkommt.</summary>
    public const int MaxInlineBytes = 10 * 1024 * 1024;
    private static readonly HttpClient Downloads = new() { Timeout = TimeSpan.FromMinutes(30) };

    private string Actor => caller.User.Identity?.Name ?? "MCP";

    [McpServerTool(Name = "create_package", Title = "Create a package", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Creates a software package from steps. It is marked as created by an AI assistant, named \"MCP: <name>\", and is NOT approved: "
        + "deploying it creates a request that an administrator approves in the web interface. Step types: PowerShell or Command (script), "
        + "Install (file of the package plus arguments, e.g. msiexec /qn is added for .msi), Reboot, Sleep. Add files with add_package_file "
        + "or upload them to uploadUrl (HTTP PUT with the same token).")]
    public async Task<object> CreatePackage(
        [Description("Package name, e.g. \"Contoso Agent 4.2\".")] string name,
        [Description("Steps in order.")] AiStep[] steps,
        [Description("Version shown with the package.")] string? version = null,
        [Description("What the package does and why.")] string? description = null,
        CancellationToken ct = default)
    {
        await caller.RequireOperatorAsync();
        try
        {
            var package = await requests.CreatePackageAsync(name, version, description, steps, Actor, ct);
            await caller.LogAsync("Package created via MCP", $"{package.Name} ({steps.Length} steps)", ct);
            return Describe(package);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "set_package_steps", Title = "Replace the steps of a package", Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Replaces all steps of a package created by an AI assistant (packages created by people cannot be changed). Withdraws an approval.")]
    public async Task<object> SetPackageSteps([Description("Package name or id.")] string package, [Description("Steps in order.")] AiStep[] steps, CancellationToken ct = default)
    {
        await caller.RequireOperatorAsync();
        var target = await AiPackageAsync(package, ct);
        try
        {
            await requests.SetStepsAsync(target.Id, steps, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new McpException(ex.Message);
        }
        await caller.LogAsync("Package changed via MCP", $"{target.Name}: {steps.Length} steps", ct);
        return Describe((await packages.GetAsync(target.Id, ct))!);
    }

    [McpServerTool(Name = "add_package_file", Title = "Add a file to a package", Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Puts a file into a package created by an AI assistant: either the server downloads it from url (http/https, optionally checked "
        + "against sha256), or the content comes as contentBase64 (up to 10 MB). Larger local files: HTTP PUT to the package's uploadUrl. "
        + "Withdraws an approval.")]
    public async Task<object> AddPackageFile(
        [Description("Package name or id.")] string package,
        [Description("File name or relative path in the package, e.g. setup.msi or config/app.json.")] string path,
        [Description("Download address of the file.")] string? url = null,
        [Description("Expected SHA-256 (hex) of the download; the file is rejected if it differs.")] string? sha256 = null,
        [Description("File content, Base64-encoded, instead of url.")] string? contentBase64 = null,
        CancellationToken ct = default)
    {
        await caller.RequireOperatorAsync();
        var target = await AiPackageAsync(package, ct);
        if ((url is null) == (contentBase64 is null))
            throw new McpException("Give either url or contentBase64.");
        // Erst die Freigabe zurückziehen, dann die Datei ändern (wie beim Hochladen über die API).
        await packages.RevokeApprovalAsync(target.Id, ct);
        try
        {
            if (contentBase64 is not null)
            {
                var bytes = Convert.FromBase64String(contentBase64);
                if (bytes.Length > MaxInlineBytes)
                    throw new McpException($"At most {MaxInlineBytes / 1024 / 1024} MB inline; use url or the upload address for larger files.");
                await packages.Repository.SaveAsync(target.Id, path, new MemoryStream(bytes), ct);
            }
            else
            {
                await DownloadAsync(target.Id, path, url!, sha256, ct);
            }
        }
        catch (FormatException)
        {
            throw new McpException("contentBase64 is not valid Base64.");
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message);
        }
        // Die Ablage nennt Pfade mit Backslash, wie sie das Setup auf dem Zielrechner sieht.
        var stored = path.Replace('/', '\\').TrimStart('\\');
        var file = packages.Repository.List(target.Id).FirstOrDefault(f => f.RelativePath.Equals(stored, StringComparison.OrdinalIgnoreCase));
        await caller.LogAsync("Package file added via MCP", $"{target.Name}: {path}{(url is null ? "" : $" from {url}")}", ct);
        return new { package = target.Name, path, size = file?.Size };
    }

    private async Task DownloadAsync(int packageId, string path, string url, string? sha256, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new McpException("url must be an http or https address.");
        var temp = Path.GetTempFileName();
        try
        {
            using (var response = await Downloads.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!response.IsSuccessStatusCode)
                    throw new McpException($"Download failed: HTTP {(int)response.StatusCode}.");
                if (response.Content.Headers.ContentLength > MaxDownloadBytes)
                    throw new McpException("The file is too large (more than 4 GB).");
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(temp);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    if ((total += read) > MaxDownloadBytes)
                        throw new McpException("The file is too large (more than 4 GB).");
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            if (!string.IsNullOrWhiteSpace(sha256))
            {
                await using var check = File.OpenRead(temp);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(check, ct));
                if (!actual.Equals(sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new McpException($"SHA-256 mismatch: expected {sha256.Trim()}, got {actual}. The file was not stored.");
            }
            await using var stored = File.OpenRead(temp);
            await packages.Repository.SaveAsync(packageId, path, stored, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new McpException($"Download failed: {ex.Message}");
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [McpServerTool(Name = "deploy_script", Title = "Run a script on computers (with approval)", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Quick way to run a PowerShell or command script on computers: creates an AI package \"MCP: <name>\" with this one step and a "
        + "deployment request. Nothing runs until an administrator approves the request in the web interface; follow it with list_deployment_requests "
        + "and then get_deployment.")]
    public async Task<object> DeployScript(
        [Description("Short name, e.g. \"Restart print spooler\".")] string name,
        [Description("The script.")] string script,
        [Description("Why, for the person who approves it.")] string reason,
        [Description("Computer names or ids. Either this or collection.")] string[]? computers = null,
        [Description("Collection name or id. Either this or computers.")] string? collection = null,
        [Description("PowerShell (default) or Command.")] StepType type = StepType.PowerShell,
        [Description("System (default) or LoggedOnUser.")] StepRunAs runAs = StepRunAs.System,
        CancellationToken ct = default)
    {
        await caller.RequireOperatorAsync();
        if (type is not (StepType.PowerShell or StepType.Command))
            throw new McpException("type must be PowerShell or Command.");
        var ids = await TargetsAsync(computers, collection, ct);
        try
        {
            // Die Begründung steht an der Anfrage; als Beschreibung des Pakets erschiene sie doppelt.
            var package = await requests.CreatePackageAsync($"{name} {DateTime.UtcNow:yyyyMMdd-HHmmss}", null, null, [new AiStep(type, name, script, RunAs: runAs)], Actor, ct);
            var request = await requests.RequestAsync(package.Id, ids, Actor, reason, ct: ct);
            await caller.LogAsync("Deployment requested via MCP", $"{package.Name} on {ids.Count} computers: {reason}", ct);
            return new { requestId = request.Id, package = package.Name, computers = ids.Count, status = "waiting for approval in the web interface (Deployments)" };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "list_deployment_requests", Title = "Deployment requests", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Requests to deploy packages that are not approved yet: Pending (waiting for an administrator), Started (deploymentId: follow with get_deployment) or Rejected (note = reason).")]
    public async Task<object> ListDeploymentRequests([Description("Only those still waiting.")] bool pendingOnly = false, CancellationToken ct = default) =>
        McpCaller.Page((await requests.ListAsync(pendingOnly, 100, ct)).Select(r => new
        {
            r.Id, package = r.Package?.Name, computers = r.ComputerIds.Count, r.RequestedBy, r.RequestedUtc, r.Reason, r.Status, r.DecidedBy, r.Note, r.DeploymentId,
        }));

    internal async Task<List<int>> TargetsAsync(string[]? computers, string? collection, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(collection) && computers is { Length: > 0 })
            throw new McpException("Give either computers or a collection, not both.");
        List<int> ids;
        if (!string.IsNullOrWhiteSpace(collection))
        {
            var match = McpCaller.Pick(await collections.ListAsync(ct), collection, s => s.Collection.Id, s => s.Collection.Name, "collection");
            ids = [.. await collections.GetMemberIdsAsync(match.Collection.Id, ct)];
        }
        else
        {
            ids = [.. (await caller.ComputersAsync(computers ?? [], ct)).Select(c => c.Id)];
        }
        if (ids.Count == 0)
            throw new McpException("No computers to deploy to.");
        await caller.RequireScopeAsync(ids, ct);
        return ids;
    }

    private async Task<Package> AiPackageAsync(string package, CancellationToken ct)
    {
        var match = McpCaller.Pick(await packages.ListAsync(ct), package, p => p.Id, p => p.Name, "package");
        try
        {
            return await requests.DemandAiPackageAsync(match.Id, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    private object Describe(Package p)
    {
        var request = http.HttpContext?.Request;
        var baseUrl = request is null ? "" : $"{request.Scheme}://{request.Host}{request.PathBase}";
        return new
        {
            p.Id, p.Name, p.Version, approved = p.IsApproved,
            steps = p.Steps.OrderBy(s => s.Order).Select(s => new { s.Definition.Name, type = s.Definition.Type, s.Definition.File }),
            files = packages.Repository.List(p.Id).Select(f => new { path = f.RelativePath, size = f.Size }),
            uploadUrl = $"{baseUrl}/api/packages/{p.Id}/files/<path>",
            next = "Deploy with deploy_package: this creates a request that an administrator approves in the web interface.",
        };
    }
}
