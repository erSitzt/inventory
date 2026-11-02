using Inventory.Core;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Server.Services;

namespace Inventory.Server.Api;

public sealed record SubnetDto(int Id, string Name, string Cidr, int? Vlan, string? Gateway, string? Excluded, string? Notes, int Addresses,
    int InUse, int DnsOnly, int Stale, int Free, int Reserved, int ExcludedAddresses, DateTime? LastScanUtc, string? LastResult);

public sealed record IpEvidenceDto(IpEvidenceKind Kind, string? Text, DateTime? TimeUtc, IpAddressStatus Status);

public sealed record IpReservationDto(int Id, string Address, string? Note, string? ReservedBy, DateTime CreatedUtc, DateTime? ExpiresUtc);

public sealed record IpAddressDto(string Address, IpAddressStatus Status, IReadOnlyList<IpEvidenceDto> Evidence, IpReservationDto? Reservation);

/// <param name="Addresses">Bestimmte Adressen reservieren; ohne sie sucht der Server <paramref name="Count"/> freie.</param>
public sealed record IpReserveRequest(IReadOnlyList<string>? Addresses, int Count = 1, bool Contiguous = false, string? Note = null, DateTime? ExpiresUtc = null);

public static class IpAddressEndpoints
{
    public static IEndpointRouteBuilder MapIpAddressEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/subnets").WithTags("IP addresses").AddEndpointFilter(MapErrors).WritesRequire(AccessControl.OperatorPolicy);

        group.MapGet("", async (IpAddressService service, CancellationToken ct) =>
            (await service.GetViewsAsync(ct)).Select(ToDto))
            .WithSummary("All subnets of the IP address management").WithDescription("With the number of addresses per status.");

        group.MapGet("{id:int}/addresses", async (int id, IpAddressService service, CancellationToken ct) =>
            await service.GetViewAsync(id, ct) is { } view ? Results.Ok(view.Addresses.Select(ToDto)) : Results.NotFound())
            .WithSummary("Every address of a subnet").WithDescription("Status and the evidence it is based on: ping, ARP, inventory, DNS records, reservations.");

        group.MapGet("{id:int}/free", async (int id, int? count, bool? contiguous, bool? includeStale, IpAddressService service, CancellationToken ct) =>
            (await service.FindFreeAsync(id, count ?? 1, contiguous ?? false, includeStale ?? false, fromTop: false, ct)).Addresses)
            .WithSummary("Find free addresses").WithDescription("Free addresses, checked again by ping before they are returned. Empty when not enough are free. Does not reserve them.");

        group.MapPost("{id:int}/reservations", async (int id, IpReserveRequest request, IpAddressService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            var user = http.User.Identity?.Name;
            var reserved = request.Addresses is { Count: > 0 } addresses
                ? await service.ReserveAsync(id, addresses, request.Note, request.ExpiresUtc, user, ct)
                : await service.ReserveFreeAsync(id, request.Count, request.Contiguous, request.Note, request.ExpiresUtc, user, ct);
            await audit.LogAsync(http.Actor(), "IP addresses reserved via API", Details(reserved), ct);
            return Results.Ok(reserved.Select(ToDto));
        }).WithSummary("Reserve addresses").WithDescription("Reserves the given addresses or, without them, finds and reserves count free ones. Operators and administrators.");

        group.MapDelete("reservations/{reservationId:int}", async (int reservationId, IpAddressService service, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            if (await service.ReleaseAsync(reservationId, ct) is not { } released)
                return Results.NotFound();
            await audit.LogAsync(http.Actor(), "IP reservation released via API", Details([released]), ct);
            return Results.NoContent();
        }).WithSummary("Release a reservation");

        return app;
    }

    internal static string Details(IReadOnlyCollection<IpReservation> reservations) =>
        string.Join(", ", reservations.Select(r => r.Address)) + (reservations.FirstOrDefault()?.Note is { } note ? $": {note}" : "");

    private static async ValueTask<object?> MapErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(ex.Message);
        }
    }

    private static SubnetDto ToDto(IpSubnetView v) => new(v.Subnet.Id, v.Subnet.Name, v.Subnet.Cidr, v.Subnet.Vlan, v.Subnet.Gateway, v.Subnet.Excluded, v.Subnet.Notes,
        v.Addresses.Count, v.Count(IpAddressStatus.InUse), v.Count(IpAddressStatus.DnsOnly), v.Count(IpAddressStatus.Stale), v.Count(IpAddressStatus.Free),
        v.Count(IpAddressStatus.Reserved), v.Count(IpAddressStatus.Excluded), v.Subnet.LastScanUtc, v.Subnet.LastResult);

    private static IpAddressDto ToDto(IpAddressEntry e) => new(e.Address, e.Status,
        [.. e.Evidence.Select(x => new IpEvidenceDto(x.Kind, x.Text, x.TimeUtc, x.Status))], e.Reservation is { } r ? ToDto(r) : null);

    private static IpReservationDto ToDto(IpReservation r) => new(r.Id, r.Address, r.Note, r.ReservedBy, r.CreatedUtc, r.ExpiresUtc);
}
