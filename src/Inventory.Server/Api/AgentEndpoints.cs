using Inventory.Contracts;
using Inventory.Data.Services;
using Inventory.Remoting.Agent;
using Microsoft.AspNetCore.Http.Features;

namespace Inventory.Server.Api;

/// <summary>
/// Schnittstelle für die Agenten. Sie melden sich nicht als Windows-Benutzer an, sondern weisen sich mit ihrem
/// Schlüssel aus; die Endpunkte sind deshalb von der Benutzeranmeldung ausgenommen und prüfen selbst.
/// </summary>
public static class AgentEndpoints
{
    /// <summary>So lange bleibt eine Job-Anfrage offen; kürzer als das Zeitlimit des Agenten.</summary>
    private static readonly TimeSpan PollWait = TimeSpan.FromSeconds(50);

    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/agent").AllowAnonymous();

        group.MapPost("enroll", async (HttpContext http, AgentService agents, AuditService audit, CancellationToken ct) =>
        {
            var request = await ReadAsync<AgentEnrollRequest>(http, ct);
            var key = request is null ? null : await agents.EnrollAsync(request, ct);
            if (key is null)
                return Results.Unauthorized();
            await audit.LogAsync("(Agent)", "Agent angemeldet", $"{request!.HostName}, Version {request.Version}, von {http.Connection.RemoteIpAddress}", ct);
            return Json(new AgentEnrollResponse { AgentKey = key });
        });

        group.MapPost("poll", async (HttpContext http, AgentService agents, AgentHub hub, CancellationToken ct) =>
        {
            var request = await ReadAsync<AgentPollRequest>(http, ct);
            var computer = await agents.AuthenticateAsync(http.Request.Headers[AgentProtocol.KeyHeader], request?.Version, ct);
            if (computer is null)
                return Results.Unauthorized();
            var job = await hub.DequeueAsync(computer.Name, PollWait, ct);
            return job is null
                ? Results.NoContent()
                : Json(new AgentJobOffer { Manifest = job.Manifest, Files = job.Files.Select(f => f.RelativePath).ToList() });
        });

        group.MapGet("jobs/{jobId}/files/{index:int}", async (string jobId, int index, HttpContext http, AgentService agents, AgentHub hub, CancellationToken ct) =>
            await FindJobAsync(http, agents, hub, jobId, ct) is { } job && index >= 0 && index < job.Files.Count
                ? Results.File(job.Files[index].LocalPath, "application/octet-stream")
                : Results.NotFound());

        group.MapPost("jobs/{jobId}/progress", async (string jobId, HttpContext http, AgentService agents, AgentHub hub, CancellationToken ct) =>
        {
            if (await FindJobAsync(http, agents, hub, jobId, ct) is not { } job)
                return Results.NotFound();
            foreach (var e in await ReadAsync<List<ProgressEvent>>(http, ct) ?? [])
                job.Progress?.Report(e);
            return Results.NoContent();
        });

        group.MapPost("jobs/{jobId}/result", async (string jobId, HttpContext http, AgentService agents, AgentHub hub, CancellationToken ct) =>
        {
            if (await FindJobAsync(http, agents, hub, jobId, ct) is not { } job)
                return Results.NotFound();
            // Ein Scan-Ergebnis mit mehreren hundert Programmen und Diensten ist größer als das Standardlimit erlaubt.
            http.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = 64 * 1024 * 1024;
            if (await ReadAsync<JobResult>(http, ct) is not { } result)
                return Results.BadRequest();
            hub.Complete(job, result);
            return Results.NoContent();
        });

        return app;
    }

    private static async Task<AgentJob?> FindJobAsync(HttpContext http, AgentService agents, AgentHub hub, string jobId, CancellationToken ct) =>
        await agents.AuthenticateAsync(http.Request.Headers[AgentProtocol.KeyHeader], null, ct) is { } computer ? hub.Find(jobId, computer.Name) : null;

    // Agent und Server teilen sich das Format aus den Contracts (JobJson), nicht die JSON-Einstellungen der übrigen API.
    private static async Task<T?> ReadAsync<T>(HttpContext http, CancellationToken ct)
    {
        using var reader = new StreamReader(http.Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        try
        {
            return body.Length == 0 ? default : JobJson.Deserialize<T>(body);
        }
        catch (System.Text.Json.JsonException)
        {
            return default;
        }
    }

    private static IResult Json<T>(T value) => Results.Content(JobJson.Serialize(value), "application/json");
}
