using System.Security.Claims;
using AssetDesk.Api.Authorization;
using AssetDesk.Api.Entities;

namespace AssetDesk.Api.Tests;

/// <summary>
/// Plain unit tests for <see cref="ClaimsPrincipalExtensions.HasPermission"/> - no controller,
/// no database. The controller-level scenarios that exercise this through real endpoints live
/// in PermissionGateTests.cs.
/// </summary>
public class ClaimsPrincipalExtensionsTests
{
    private static ClaimsPrincipal BuildPrincipal(string[]? roles = null, string[]? permissions = null)
    {
        var claims = new List<Claim>();
        foreach (var role in roles ?? [])
            claims.Add(new Claim(ClaimTypes.Role, role));
        foreach (var permission in permissions ?? [])
            claims.Add(new Claim(Permissions.ClaimType, permission));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    [Fact]
    public void SuperAdmin_role_grants_every_permission_even_with_no_claims()
    {
        var user = BuildPrincipal(roles: [Roles.SuperAdmin]);

        Assert.True(user.HasPermission(Permissions.TicketsManage));
        Assert.True(user.HasPermission("some-permission-that-does-not-even-exist"));
    }

    [Fact]
    public void A_permission_claim_grants_exactly_that_key_and_nothing_else()
    {
        var user = BuildPrincipal(permissions: [Permissions.TicketsQueue]);

        Assert.True(user.HasPermission(Permissions.TicketsQueue));
        Assert.False(user.HasPermission(Permissions.TicketsManage));
    }

    [Fact]
    public void An_empty_principal_has_no_permissions()
    {
        var user = BuildPrincipal();

        Assert.False(user.HasPermission(Permissions.TicketsQueue));
    }

    [Fact]
    public void A_principal_less_context_fails_closed_instead_of_throwing()
    {
        // Guards the null check added to HasPermission: a controller context that somehow has
        // no User set must be treated as "no permission", not throw a NullReferenceException
        // that would surface as a 500 instead of a clean 403/401.
        ClaimsPrincipal? user = null;

        var ex = Record.Exception(() => user!.HasPermission(Permissions.TicketsQueue));

        Assert.Null(ex);
        Assert.False(user!.HasPermission(Permissions.TicketsQueue));
    }
}
