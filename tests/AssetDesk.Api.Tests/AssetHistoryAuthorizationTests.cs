using System.Reflection;
using AssetDesk.Api.Controllers;
using Microsoft.AspNetCore.Authorization;

namespace AssetDesk.Api.Tests;

/// <summary>
/// GET /api/assignments/assets/{assetId}/history carried only the controller's bare
/// [Authorize], so any signed-in principal - an Employee holding nothing but iams:tickets:file -
/// could read who held any asset in their tenant, when, and who handed it over. The catalog puts
/// exactly that under iams:assignments:view ("See who holds which asset, and the assignment
/// history"), the key its offboarding neighbours already use.
///
/// Unlike GetUserAssets there is no self-service case to preserve: the history is about an
/// asset, not the caller, and the only page that shows it is the asset detail page, which an
/// Employee cannot open.
/// </summary>
public class AssetHistoryAuthorizationTests
{
    [Fact]
    public void Asset_history_requires_the_view_assignments_policy()
    {
        var authorize = typeof(AssignmentsController)
            .GetMethod(nameof(AssignmentsController.GetAssetHistory))!
            .GetCustomAttributes<AuthorizeAttribute>()
            .SingleOrDefault(a => a.Policy is not null);

        Assert.NotNull(authorize);
        Assert.Equal("CanViewAssignments", authorize.Policy);
    }
}
