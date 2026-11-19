using Inventory.Core;
using Inventory.Server.Mcp;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Localization;

namespace Inventory.Server.Api;

/// <summary>
/// Unerwartete Fehler bei Anfragen von Programmen (REST-API, MCP-Server): ein Statuscode mit kurzer Meldung als Problem
/// Details statt der HTML-Fehlerseite der Oberfläche, mit der ein Skript oder KI-Assistent nichts anfangen kann.
/// </summary>
public static class ApiErrors
{
    /// <summary>Anfragen von Programmen statt aus dem Browser.</summary>
    public static bool IsMachineRequest(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments(McpSetup.Path);

    /// <summary>
    /// Nach der Fehlerseite der Oberfläche einhängen: Dieser Handler liegt innen und fängt die Fehler der API zuerst.
    /// Gilt auch in der Entwicklung, damit Aufrufer dort dieselbe Antwort sehen wie im Betrieb.
    /// </summary>
    public static void UseApiErrors(this WebApplication app) =>
        app.UseWhen(IsMachineRequest, branch => branch.UseExceptionHandler(new ExceptionHandlerOptions { ExceptionHandler = WriteAsync }));

    /// <summary>Wie lange ein Aufrufer bei einem vorübergehenden Fehler warten soll, in Sekunden.</summary>
    public const int RetryAfterSeconds = 10;

    private static Task WriteAsync(HttpContext context)
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var L = context.RequestServices.GetRequiredService<IStringLocalizer<SharedResource>>();
        // Dieselbe Kennung wie "RequestId" in den Protokolleinträgen der Anfrage.
        var requestId = context.TraceIdentifier;
        // Vorübergehend: Der Dienst fährt gerade herunter (Update, Neustart), oder die Datenbank ist kurz belegt.
        var (status, detail) = context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested
            ? (StatusCodes.Status503ServiceUnavailable, L["The server is restarting. Try again in a moment."].Value)
            : IsDatabaseBusy(exception)
                ? (StatusCodes.Status503ServiceUnavailable, L["The database is busy. Try again in a moment."].Value)
                : (StatusCodes.Status500InternalServerError, L["Unexpected error on the server. The server log has the details (request ID {0}).", requestId].Value);
        if (status == StatusCodes.Status503ServiceUnavailable)
            context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Problem(detail, statusCode: status, extensions: new Dictionary<string, object?> { ["requestId"] = requestId }).ExecuteAsync(context);
    }

    /// <summary>SQLITE_BUSY (5) oder SQLITE_LOCKED (6), auch eingepackt, z. B. in eine DbUpdateException.</summary>
    internal static bool IsDatabaseBusy(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is SqliteException { SqliteErrorCode: 5 or 6 })
                return true;
        }
        return false;
    }
}
