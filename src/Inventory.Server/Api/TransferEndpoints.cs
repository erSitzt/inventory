using Inventory.Data.Services;
using Inventory.Server.Services;

namespace Inventory.Server.Api;

public static class TransferEndpoints
{
    public static IEndpointRouteBuilder MapTransferEndpoints(this IEndpointRouteBuilder app)
    {
        // Die Einstellungsseite legt den Export an; hier wird er einmal abgeholt und danach gelöscht.
        app.MapGet("/download/export/{id:guid}", (Guid id, DataTransferService transfer, TimeProvider time) =>
        {
            if (transfer.ExportFile(id) is not { } path)
                return Results.NotFound();
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete, 1 << 20, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            return Results.File(stream, "application/zip", $"inventory-export-{Environment.MachineName.ToLowerInvariant()}-{time.GetLocalNow():yyyyMMdd-HHmm}.zip");
        }).RequireAuthorization(AccessControl.AdminPolicy).ExcludeFromDescription();
        return app;
    }
}
