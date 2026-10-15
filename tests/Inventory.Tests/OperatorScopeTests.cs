using System.Security.Claims;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Remoting;
using Inventory.Server;
using Inventory.Server.Services;

namespace Inventory.Tests;

/// <summary>Operatoren, die nur auf Rechnern bestimmter Sammlungen handeln dürfen.</summary>
public class OperatorScopeTests
{
    private const string Helpdesk = "S-1-5-21-1-2-3-1101";
    private const string SiteB = "S-1-5-21-1-2-3-1201";

    private static ClaimsPrincipal Windows(params string[] groupSids) =>
        new(new ClaimsIdentity(groupSids.Select(sid => new Claim(ClaimTypes.GroupSid, sid)), "Negotiate"));

    /// <summary>So trägt eine Anmeldung Rolle und Bereich (wie nach der Anmeldung bzw. der Rollenergänzung).</summary>
    private static ClaimsPrincipal Signed(AccessRole role, params string[]? scope)
    {
        List<Claim> claims = [new(ClaimTypes.Name, "user"), new(AccessControl.RoleClaim, role.ToString())];
        if (scope is not null)
            claims.AddRange([new Claim(AccessControl.ScopedClaim, "1"), .. scope.Select(name => new Claim(AccessControl.ScopeClaim, name))]);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public void Matching_groups_give_the_union_of_their_collections_and_others_stay_unrestricted()
    {
        var options = new InventoryOptions
        {
            Roles = new RoleGroups { Operator = [Helpdesk] },
            OperatorScopes =
            [
                new OperatorScope { Groups = [SiteB], Collections = ["Site B"] },
                new OperatorScope { Groups = [SiteB], Collections = ["Laptops", "site b"] },
            ],
        };
        Assert.Equal(["Site B", "Laptops"], options.ScopeOf(Windows(Helpdesk, SiteB), oidc: false));
        Assert.Null(options.ScopeOf(Windows(Helpdesk), oidc: false));

        var oidc = new InventoryOptions { OperatorScopes = [new OperatorScope { Groups = ["g-siteb"], Collections = ["Site B"] }] };
        Assert.Equal(["Site B"], oidc.ScopeOf(new ClaimsPrincipal(new ClaimsIdentity([new Claim("groups", "G-SITEB")], "oidc")), oidc: true));
    }

    [Fact]
    public void Only_operators_are_restricted()
    {
        Assert.Equal(["Site B"], AccessControl.ScopeOf(Signed(AccessRole.Operator, "Site B")));
        Assert.Empty(AccessControl.ScopeOf(Signed(AccessRole.Operator, []))!);
        Assert.Null(AccessControl.ScopeOf(Signed(AccessRole.Operator, null)));
        // Administratoren gelten Bereiche nie, auch wenn die Anmeldung welche trägt.
        Assert.Null(AccessControl.ScopeOf(Signed(AccessRole.Admin, "Site B")));
    }

    [Fact]
    public async Task Allowed_computers_are_the_current_members_of_the_scoped_collections()
    {
        using var db = new TestDb();
        var computers = new ComputerService(db, new SimulatedHostProbe());
        var collections = new CollectionService(db);
        var all = (await computers.ImportAsync("pc-a1 pc-a2 pc-b1")).OrderBy(c => c.Name).ToList();
        var siteB = await collections.SaveAsync(null, "Site B", null, true, true,
            [new CollectionRule { Field = RuleField.ComputerName, Operator = RuleOperator.StartsWith, Value = "pc-b" }]);
        var scope = new ComputerScope(collections);

        var allowed = await scope.AllowedAsync(Signed(AccessRole.Operator, "site b"));
        Assert.Equal([all[2].Id], allowed!);
        Assert.Equal([all[0].Id, all[1].Id], await scope.OutsideAsync(Signed(AccessRole.Operator, "Site B"), all.Select(c => c.Id)));
        Assert.Empty(await scope.OutsideAsync(Signed(AccessRole.Admin, "Site B"), all.Select(c => c.Id)));
        // Eine Sammlung, die es nicht gibt, gibt nichts frei.
        Assert.Empty((await new ComputerScope(collections).AllowedAsync(Signed(AccessRole.Operator, "Gone")))!);
        Assert.Null(await scope.AllowedAsync(Signed(AccessRole.Operator, null)));
        _ = siteB;
    }
}
