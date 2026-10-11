using Inventory.Server.Services;
using Microsoft.AspNetCore.Authorization;

namespace Inventory.Server.Api;

internal static class RoleConventions
{
    /// <summary>
    /// Lesende Aufrufe (GET) einer Gruppe bleiben für jede Rolle offen; alle anderen verlangen die genannte Richtlinie.
    /// So kann kein neuer schreibender Endpunkt versehentlich ohne Rollenprüfung entstehen.
    /// </summary>
    public static RouteGroupBuilder WritesRequire(this RouteGroupBuilder group, string policy)
    {
        ((IEndpointConventionBuilder)group).Add(endpoint =>
        {
            var methods = endpoint.Metadata.OfType<IHttpMethodMetadata>().SelectMany(m => m.HttpMethods);
            if (methods.Any(method => !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method)))
                endpoint.Metadata.Add(new AuthorizeAttribute(policy));
        });
        return group;
    }

    public static TBuilder RequireAdmin<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(AccessControl.AdminPolicy);

    public static TBuilder RequireOperator<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(AccessControl.OperatorPolicy);
}
