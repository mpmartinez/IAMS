using AssetDesk.Api.Authorization;
using AssetDesk.Api.Entities;

namespace AssetDesk.Api.Tests;

public class PermissionCatalogTests
{
    [Fact]
    public void ClaimType_MatchesWhatPermissionViewReads()
    {
        // PermissionView.razor calls user.HasClaim("permission", key).
        Assert.Equal("permission", Permissions.ClaimType);
    }

    [Fact]
    // The prefix is still "iams", not "assetdesk". These keys are persisted in
    // RolePermissions, so the AssetDesk rename deliberately left them alone - renaming
    // the prefix without migrating that table strips every role of every permission.
    public void EveryKey_UsesTheIamsPrefix()
    {
        foreach (var key in Permissions.Keys)
        {
            var parts = key.Split(':');
            Assert.Equal(3, parts.Length);
            Assert.Equal("iams", parts[0]);
            Assert.All(parts, p => Assert.False(string.IsNullOrWhiteSpace(p)));
        }
    }

    [Fact]
    public void Keys_AreUnique()
    {
        Assert.Equal(Permissions.Keys.Length, Permissions.Keys.Distinct().Count());
    }

    [Fact]
    public void Admin_HoldsEveryPermission()
    {
        Assert.Equal(
            Permissions.Keys.OrderBy(k => k),
            Permissions.DefaultsFor(Roles.Admin).OrderBy(k => k));
    }

    [Theory]
    [InlineData(Roles.Staff, 13)]
    [InlineData(Roles.Auditor, 3)]
    [InlineData(Roles.Management, 1)]
    [InlineData(Roles.Employee, 1)]
    public void BuiltInRoles_HaveTheExpectedGrantCount(string role, int expected)
    {
        Assert.Equal(expected, Permissions.DefaultsFor(role).Count);
    }

    [Fact]
    public void EveryRole_CanFileTickets()
    {
        // CanFileTickets today lists every authenticated role including Employee.
        foreach (var role in Roles.All)
            Assert.Contains(Permissions.TicketsFile, Permissions.DefaultsFor(role));
    }

    [Fact]
    public void Auditor_KeepsReportsAndAssignmentReads_ButNoAssetWrites()
    {
        var auditor = Permissions.DefaultsFor(Roles.Auditor);
        Assert.Contains(Permissions.ReportsView, auditor);
        Assert.Contains(Permissions.AssignmentsView, auditor);
        Assert.DoesNotContain(Permissions.AssetsCreate, auditor);
        Assert.DoesNotContain(Permissions.AssetsDelete, auditor);
    }

    [Fact]
    public void Staff_CanImportButNotDelete()
    {
        var staff = Permissions.DefaultsFor(Roles.Staff);
        Assert.Contains(Permissions.AssetsImport, staff);
        Assert.Contains(Permissions.AssetsCreate, staff);
        Assert.DoesNotContain(Permissions.AssetsDelete, staff);
    }

    [Fact]
    public void UnknownRole_GetsNothing()
    {
        Assert.Empty(Permissions.DefaultsFor("NoSuchRole"));
    }

    [Fact]
    public void EveryDescriptor_HasAGroupAndLabel()
    {
        Assert.All(Permissions.All, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Group));
            Assert.False(string.IsNullOrWhiteSpace(d.Label));
            Assert.False(string.IsNullOrWhiteSpace(d.Description));
        });
    }
}
