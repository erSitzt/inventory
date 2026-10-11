using System.Security.Claims;
using Inventory.Core.Model;
using Inventory.Data.Services;
using Inventory.Server;
using Inventory.Server.Services;

namespace Inventory.Tests;

public class RoleTests
{
    private const string Admins = "S-1-5-21-1-2-3-1100";
    private const string Helpdesk = "S-1-5-21-1-2-3-1101";
    private const string Auditors = "S-1-5-21-1-2-3-1102";

    private static ClaimsPrincipal Windows(params string[] groupSids) =>
        new(new ClaimsIdentity(groupSids.Select(sid => new Claim(ClaimTypes.GroupSid, sid)), "Negotiate"));

    private static ClaimsPrincipal Oidc(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "oidc"));

    [Fact]
    public void A_windows_user_gets_the_highest_role_of_their_groups()
    {
        var options = new InventoryOptions { AllowedGroups = [Admins], Roles = new RoleGroups { Operator = [Helpdesk], Viewer = [Auditors] } };
        Assert.Equal(AccessRole.Admin, options.RoleOf(Windows(Admins, Helpdesk)));
        Assert.Equal(AccessRole.Operator, options.RoleOf(Windows(Helpdesk, Auditors)));
        Assert.Equal(AccessRole.Viewer, options.RoleOf(Windows(Auditors)));
        Assert.Equal(AccessRole.None, options.RoleOf(Windows("S-1-5-21-1-2-3-9999")));
    }

    [Fact]
    public void Without_configured_administrators_the_local_administrators_of_the_server_are_administrators()
    {
        var options = new InventoryOptions { Roles = new RoleGroups { Operator = [Helpdesk] } };
        Assert.Equal(AccessRole.Admin, options.RoleOf(Windows(InventoryOptions.LocalAdministratorsSid)));
        Assert.Equal(AccessRole.Operator, options.RoleOf(Windows(Helpdesk)));

        // Roles:Admin ergänzt AllowedGroups; dann gilt der Standard nicht mehr.
        var explicitAdmins = new InventoryOptions { Roles = new RoleGroups { Admin = [Admins] } };
        Assert.Equal(AccessRole.Admin, explicitAdmins.RoleOf(Windows(Admins)));
        Assert.Equal(AccessRole.None, explicitAdmins.RoleOf(Windows(InventoryOptions.LocalAdministratorsSid)));
    }

    [Fact]
    public void Oidc_roles_come_from_the_group_claim_or_app_roles()
    {
        var oidc = new OidcOptions { AllowedGroups = ["g-admin"], Roles = new RoleGroups { Operator = ["Inventory.Operator"], Viewer = ["g-view"] } };
        Assert.Equal(AccessRole.Admin, oidc.RoleOf(Oidc(("groups", "g-admin"), ("groups", "g-view"))));
        Assert.Equal(AccessRole.Operator, oidc.RoleOf(Oidc(("roles", "Inventory.Operator"))));
        Assert.Equal(AccessRole.Viewer, oidc.RoleOf(Oidc(("groups", "G-VIEW"))));
        Assert.Equal(AccessRole.None, oidc.RoleOf(Oidc(("groups", "other"), ("email", "g-admin"))));
    }

    [Fact]
    public void Oidc_needs_at_least_one_group_in_any_role()
    {
        var oidc = new OidcOptions { TenantId = "t", ClientId = "c" };
        Assert.Throws<InvalidOperationException>(oidc.Validate);
        oidc.Roles.Viewer = ["g-view"];
        oidc.Validate();
    }

    [Fact]
    public void The_role_claim_decides_and_a_sign_in_without_it_has_no_role()
    {
        ClaimsPrincipal With(string? role) => new(new ClaimsIdentity(role is null ? [] : [new Claim(AccessControl.RoleClaim, role)], "test"));
        Assert.Equal(AccessRole.Operator, AccessControl.RoleOf(With("Operator")));
        Assert.Equal(AccessRole.None, AccessControl.RoleOf(With(null)));
        Assert.Equal(AccessRole.None, AccessControl.RoleOf(With("Superuser")));
    }

    [Fact]
    public async Task A_token_keeps_the_role_it_was_created_with_and_defaults_to_administrator()
    {
        using var db = new TestDb();
        var tokens = new ApiTokenService(db, TimeProvider.System);
        var viewer = await tokens.CreateAsync("Monitoring", "anna", null, role: AccessRole.Viewer);
        var legacy = await tokens.CreateAsync("Alt", "anna", null);
        Assert.Equal(AccessRole.Viewer, (await tokens.ValidateAsync(viewer))!.Role);
        Assert.Equal(AccessRole.Admin, (await tokens.ValidateAsync(legacy))!.Role);
    }
}
