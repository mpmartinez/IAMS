using IAMS.Api.Authorization;
using IAMS.Api.Data;
using IAMS.Api.Entities;

namespace IAMS.Api.Tests;

public class RolesApiTests
{
    [Fact]
    public void GrantableKeys_AreLimitedToWhatTheActorHolds()
    {
        var actor = TestPrincipals.With(Permissions.RolesManage, Permissions.AssetsView);

        var grantable = ClaimsPrincipalExtensions.GrantableKeys(actor, isSuperAdmin: false);

        Assert.Contains(Permissions.AssetsView, grantable);
        Assert.DoesNotContain(Permissions.AssetsDelete, grantable);
    }

    [Fact]
    public void GrantableKeys_AreUnlimitedForSuperAdmin()
    {
        var actor = TestPrincipals.With();

        var grantable = ClaimsPrincipalExtensions.GrantableKeys(actor, isSuperAdmin: true);

        Assert.Equal(Permissions.Keys.OrderBy(k => k), grantable.OrderBy(k => k));
    }

    [Fact]
    public void GrantableKeys_IgnoreUnknownClaims()
    {
        var actor = TestPrincipals.With("iams:not:real", Permissions.AssetsView);

        var grantable = ClaimsPrincipalExtensions.GrantableKeys(actor, isSuperAdmin: false);

        Assert.Equal([Permissions.AssetsView], grantable);
    }
}

internal static class TestPrincipals
{
    public static System.Security.Claims.ClaimsPrincipal With(params string[] permissions)
    {
        var claims = permissions
            .Select(p => new System.Security.Claims.Claim(Permissions.ClaimType, p))
            .ToList();
        return new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(claims, "test"));
    }
}
