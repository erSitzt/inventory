using Microsoft.Extensions.Localization;
using Inventory.Server.Services;
using Inventory.Core.Model;
using Inventory.Core;
using Inventory.Contracts;
using Inventory.Data.Services;
using Inventory.Remoting;
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
        MapPublicDownload(app);

        // Nicht in der API-Dokumentation: internes Protokoll zwischen Agent und Server (siehe Inventory.Contracts/AgentProtocol.cs).
        var group = app.MapGroup("/api/agent").AllowAnonymous().ExcludeFromDescription();

        group.MapPost("enroll", async (HttpContext http, AgentService agents, AuditService audit, CancellationToken ct) =>
        {
            var request = await ReadAsync<AgentEnrollRequest>(http, ct);
            var key = request is null ? null : await agents.EnrollAsync(request, ct);
            if (key is null)
                return Results.Unauthorized();
            await audit.LogAsync(new AuditActor("(Agent)", null, AuditSource.Agent), "Agent enrolled", $"{request!.HostName}, version {request.Version}, from {http.Connection.RemoteIpAddress}", ct);
            return Json(new AgentEnrollResponse { AgentKey = key });
        });

        group.MapPost("poll", async (HttpContext http, AgentService agents, AgentHub hub, RunnerPackage runner, CancellationToken ct) =>
        {
            var request = await ReadAsync<AgentPollRequest>(http, ct);
            var version = request is null ? null : AgentService.DescribeVersion(request.Version, request.RunnerHash);
            var computer = await agents.AuthenticateAsync(http.Request.Headers[AgentProtocol.KeyHeader], version, ct);
            if (computer is null)
                return Results.Unauthorized();
            // Veraltete Agenten aktualisieren sich zuerst; Jobs bleiben so lange in der Warteschlange.
            if (request?.RunnerHash != runner.Hash)
                return Json(new AgentJobOffer { Update = new AgentUpdateOffer { Hash = runner.Hash, Files = runner.Files.Select(f => Path.GetFileName(f)!).ToList() } });
            var job = await hub.DequeueAsync(computer.Name, PollWait, ct);
            return job is null
                ? Results.NoContent()
                : Json(new AgentJobOffer { Manifest = job.Manifest, Files = job.Files.Select(f => f.RelativePath).ToList() });
        });

        group.MapGet("update/files/{index:int}", async (int index, HttpContext http, AgentService agents, RunnerPackage runner, CancellationToken ct) =>
            await agents.AuthenticateAsync(http.Request.Headers[AgentProtocol.KeyHeader], null, ct) is not null && index >= 0 && index < runner.Files.Count
                ? Results.File(runner.Files[index], "application/octet-stream")
                : Results.NotFound());

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

        group.MapPost("jobs/{jobId}/result", async (string jobId, HttpContext http, AgentService agents, AgentHub hub, DeploymentService deployments, CancellationToken ct) =>
        {
            if (await agents.AuthenticateAsync(http.Request.Headers[AgentProtocol.KeyHeader], null, ct) is not { } computer)
                return Results.NotFound();
            // Ein Scan-Ergebnis mit mehreren hundert Programmen und Diensten ist größer als das Standardlimit erlaubt.
            http.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = 64 * 1024 * 1024;
            if (hub.Find(jobId, computer.Name) is { } job)
            {
                if (await ReadAsync<JobResult>(http, ct) is not { } result)
                    return Results.BadRequest();
                hub.Complete(job, result);
                return Results.NoContent();
            }
            // Der Server kennt den Job nicht mehr, weil er inzwischen neu gestartet wurde: das Ergebnis gehört dann zu einem
            // unterbrochenen Ziel. Alles andere (unbekannt oder schon übernommen) meldet 404; der Agent verwirft es daraufhin.
            // Das Ergebnis muss selbst zu diesem Job gehören, nicht nur unter seiner Adresse eintreffen.
            if (await ReadAsync<JobResult>(http, ct) is not { } late || late.JobId != jobId)
                return Results.BadRequest();
            return await deployments.AdoptResultAsync(computer.Id, jobId, late, ct) ? Results.NoContent() : Results.NotFound();
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

    /// <summary>
    /// Download-Seite für den Agenten ohne Anmeldung, wenn ein Administrator sie geöffnet hat: gedacht, um an einem neuen
    /// Rechner die Seite aufzurufen und den Agenten fertig eingerichtet zu installieren. Geschlossen antwortet alles mit 404.
    /// </summary>
    private static void MapPublicDownload(IEndpointRouteBuilder app)
    {
        // Nichts davon darf zwischengespeichert werden: das Paket enthält das Anmeldegeheimnis, und ob die Seite offen ist, entscheidet jede Anfrage neu.
        var group = app.MapGroup("/" + AgentService.PublicPagePath).AllowAnonymous().ExcludeFromDescription().AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });

        group.MapGet("", async (AgentService agents, IStringLocalizer<SharedResource> L, CancellationToken ct) =>
        {
            if (await agents.GetPublicDownloadUntilAsync(ct) is null)
                return Results.NotFound();
            var serverUrl = await agents.GetServerUrlAsync(ct);
            return string.IsNullOrWhiteSpace(serverUrl)
                ? Results.Content(Page(L, L["The server address for agents is not set yet. An administrator can enter it under Settings → Agents."], null), "text/html; charset=utf-8")
                : Results.Content(Page(L, null, $"irm {serverUrl}/{AgentService.PublicScriptPath} | iex"), "text/html; charset=utf-8");
        });

        group.MapGet("setup.zip", async (HttpContext http, AgentService agents, RunnerPackage runner, AuditService audit, CancellationToken ct) =>
        {
            if (await agents.GetPublicDownloadUntilAsync(ct) is null)
                return Results.NotFound();
            try
            {
                var setup = await agents.BuildSetupAsync(runner.Files, runner.Hash, ct);
                // Das Paket enthält das Anmeldegeheimnis: wer es geholt hat, soll nachvollziehbar sein.
                await audit.LogAsync(new AuditActor("(anonymous)", null, AuditSource.Agent), "Agent setup downloaded from the public page", $"from {http.Connection.RemoteIpAddress}", ct);
                return Results.File(setup, "application/zip", AgentService.SetupFileName);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        group.MapGet("install.ps1", async (HttpContext http, AgentService agents, AuditService audit, CancellationToken ct) =>
        {
            if (await agents.GetPublicDownloadUntilAsync(ct) is null)
                return Results.NotFound();
            try
            {
                var script = await agents.BuildInstallScriptAsync(ct);
                await audit.LogAsync(new AuditActor("(anonymous)", null, AuditSource.Agent), "Agent install script downloaded from the public page", $"from {http.Connection.RemoteIpAddress}", ct);
                return Results.Text(script, "text/plain; charset=utf-8");
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });
    }

    /// <summary>Eine Seite ohne Skripte und ohne die Oberfläche der Anwendung: sie soll auf einem frischen Rechner in jedem Browser laufen.</summary>
    private static string Page(IStringLocalizer<SharedResource> L, string? problem, string? command)
    {
        static string H(string text) => System.Net.WebUtility.HtmlEncode(text);
        var body = problem is not null
            ? $"<p class=\"warn\">{H(problem)}</p>"
            : $"""
               <ol>
                 <li>{H(L["Download the agent setup."])}<br><a class="button" href="/{AgentService.PublicSetupPath}">{H(L["Download agent setup"])}</a></li>
                 <li>{H(L["Unpack the ZIP file."])}</li>
                 <li>{H(L["Right-click Install-Agent.cmd and choose \"Run as administrator\"."])}</li>
               </ol>
               <h2>{H(L["Or in one step"])}</h2>
               <p>{H(L["Open Windows PowerShell as administrator and run:"])}</p>
               <pre>{H(command!)}</pre>
               <p class="note">{H(L["The computer appears in Inventory under \"Computers\" a few seconds after the installation. The setup is configured for this server; nothing has to be entered."])}</p>
               """;
        return $$"""
            <!DOCTYPE html>
            <html lang="{{H(System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)}}">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta name="robots" content="noindex">
              <title>{{H(L["Install the Inventory agent"])}}</title>
              <style>
                body { font-family: "Segoe UI", system-ui, sans-serif; background: #f4f5fa; color: #1e1b4b; margin: 0; }
                main { max-width: 640px; margin: 8vh auto; background: #fff; border-radius: 14px; padding: 32px 40px; box-shadow: 0 6px 24px rgba(30, 27, 75, .08); }
                h1 { font-size: 1.5rem; margin-top: 0; }
                h2 { font-size: 1.05rem; margin-top: 2rem; }
                li { margin-bottom: 1rem; line-height: 1.5; }
                .button { display: inline-block; margin-top: .5rem; padding: .65rem 1.2rem; border-radius: 8px; background: #4f46e5; color: #fff; text-decoration: none; font-weight: 600; }
                pre { background: #1e1b4b; color: #e0e7ff; padding: 12px 16px; border-radius: 8px; overflow-x: auto; user-select: all; }
                .note { color: #555; font-size: .9rem; }
                .warn { color: #b45309; }
              </style>
            </head>
            <body>
              <main>
                <h1>{{H(L["Install the Inventory agent"])}}</h1>
                {{body}}
              </main>
            </body>
            </html>
            """;
    }
}
