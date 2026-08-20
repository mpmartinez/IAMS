using System.Reflection;
using System.Security.Claims;
using AssetDesk.Api.Controllers;
using AssetDesk.Api.Entities;
using AssetDesk.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AssetDesk.Api.Tests;

/// <summary>
/// Covers the split introduced when the dashboard became role-aware: GET /api/dashboard carries
/// estate-wide counts and TotalAssetValue and is gated on iams:assets:view, while GET
/// /api/dashboard/me serves the caller's own kit and tickets to anyone signed in.
///
/// The controller is constructed directly, as elsewhere in this suite, so the [Authorize] gate is
/// asserted by reflection rather than exercised - there is no WebApplicationFactory here to run
/// the authorization filter.
/// </summary>
public class MyDashboardTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static DashboardController BuildController(Data.AppDbContext db, string userId)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        var controller = new DashboardController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
                }
            }
        };
        return controller;
    }

    private static MyDashboardDto Unwrap(ActionResult<ApiResponse<MyDashboardDto>> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<MyDashboardDto>>(ok.Value);
        Assert.NotNull(response.Data);
        return response.Data;
    }

    private static Ticket NewTicket(string requesterId, string title, string status) => new()
    {
        TenantId = TenantId,
        TicketNumber = Random.Shared.Next(1, 100_000),
        Title = title,
        Status = status,
        RequesterUserId = requesterId
    };

    [Fact]
    public async Task GetMyDashboard_returns_only_assets_assigned_to_the_caller()
    {
        var (db, connection) = TestDb.Create();
        using var _ = connection;
        await using var __ = db;

        await TestDb.SeedTenantAsync(db, TenantId);
        await TestDb.SeedUserAsync(db, TenantId, "me", "Me");
        await TestDb.SeedUserAsync(db, TenantId, "someone-else", "Someone Else");

        var mine = await TestDb.SeedAssetAsync(db, TenantId, "AST-MINE", AssetStatus.InUse);
        var theirs = await TestDb.SeedAssetAsync(db, TenantId, "AST-THEIRS", AssetStatus.InUse);
        var unassigned = await TestDb.SeedAssetAsync(db, TenantId, "AST-FREE");

        mine.AssignedToUserId = "me";
        theirs.AssignedToUserId = "someone-else";
        await db.SaveChangesAsync();

        var result = await BuildController(db, "me").GetMyDashboard(CancellationToken.None);

        var dashboard = Unwrap(result);
        var tag = Assert.Single(dashboard.MyAssets).AssetTag;
        Assert.Equal("AST-MINE", tag);
        Assert.DoesNotContain(dashboard.MyAssets, a => a.Id == theirs.Id || a.Id == unassigned.Id);
    }

    [Fact]
    public async Task GetMyDashboard_reports_held_since_from_the_open_assignment()
    {
        var (db, connection) = TestDb.Create();
        using var _ = connection;
        await using var __ = db;

        await TestDb.SeedTenantAsync(db, TenantId);
        await TestDb.SeedUserAsync(db, TenantId, "me", "Me");
        var asset = await TestDb.SeedAssetAsync(db, TenantId, "AST-1", AssetStatus.InUse);
        asset.AssignedToUserId = "me";

        var heldSince = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        db.AssetAssignments.Add(new AssetAssignment
        {
            TenantId = TenantId,
            AssetId = asset.Id,
            UserId = "me",
            AssignedByUserId = "me",
            AssignedAt = heldSince
        });
        // A returned assignment for the same asset must not win over the open one.
        db.AssetAssignments.Add(new AssetAssignment
        {
            TenantId = TenantId,
            AssetId = asset.Id,
            UserId = "me",
            AssignedByUserId = "me",
            AssignedAt = new DateTime(2025, 1, 1, 9, 0, 0, DateTimeKind.Utc),
            ReturnedAt = new DateTime(2025, 6, 1, 9, 0, 0, DateTimeKind.Utc)
        });
        await db.SaveChangesAsync();

        var result = await BuildController(db, "me").GetMyDashboard(CancellationToken.None);

        Assert.Equal(heldSince, Assert.Single(Unwrap(result).MyAssets).AssignedAt);
    }

    [Fact]
    public async Task GetMyDashboard_counts_only_the_callers_own_tickets()
    {
        var (db, connection) = TestDb.Create();
        using var _ = connection;
        await using var __ = db;

        await TestDb.SeedTenantAsync(db, TenantId);
        await TestDb.SeedUserAsync(db, TenantId, "me", "Me");
        await TestDb.SeedUserAsync(db, TenantId, "someone-else", "Someone Else");

        db.Tickets.AddRange(
            NewTicket("me", "Laptop won't charge", TicketStatus.New),
            NewTicket("me", "Needs a dock", TicketStatus.InProgress),
            NewTicket("me", "Old request", TicketStatus.Closed),
            NewTicket("someone-else", "Not mine", TicketStatus.New));
        await db.SaveChangesAsync();

        var result = await BuildController(db, "me").GetMyDashboard(CancellationToken.None);

        var dashboard = Unwrap(result);
        Assert.Equal(2, dashboard.OpenTicketCount);
        Assert.Equal(1, dashboard.ResolvedTicketCount);
        Assert.Equal(3, dashboard.RecentTickets.Count);
        Assert.DoesNotContain(dashboard.RecentTickets, t => t.Title == "Not mine");
    }

    [Fact]
    public async Task GetMyDashboard_caps_recent_tickets_at_five_newest_first()
    {
        var (db, connection) = TestDb.Create();
        using var _ = connection;
        await using var __ = db;

        await TestDb.SeedTenantAsync(db, TenantId);
        await TestDb.SeedUserAsync(db, TenantId, "me", "Me");

        for (var i = 0; i < 7; i++)
        {
            var ticket = NewTicket("me", $"Report {i}", TicketStatus.New);
            ticket.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i);
            db.Tickets.Add(ticket);
        }
        await db.SaveChangesAsync();

        var result = await BuildController(db, "me").GetMyDashboard(CancellationToken.None);

        var dashboard = Unwrap(result);
        Assert.Equal(7, dashboard.OpenTicketCount);
        Assert.Equal(5, dashboard.RecentTickets.Count);
        Assert.Equal("Report 6", dashboard.RecentTickets[0].Title);
        Assert.Equal("Report 2", dashboard.RecentTickets[4].Title);
    }

    [Fact]
    public async Task GetMyDashboard_rejects_a_principal_with_no_user_id()
    {
        var (db, connection) = TestDb.Create();
        using var _ = connection;
        await using var __ = db;

        await TestDb.SeedTenantAsync(db, TenantId);

        var controller = new DashboardController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([], "TestAuth"))
                }
            }
        };

        var result = await controller.GetMyDashboard(CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    [Fact]
    public void Estate_dashboard_is_gated_on_the_assets_view_permission()
    {
        // The estate payload carries TotalAssetValue and tenant-wide counts. Without this policy
        // every signed-in user - Employee included - could read it, which is what the role-aware
        // dashboard exists to stop. The Web project is Blazor WebAssembly, so hiding it in markup
        // would not be enough.
        var authorize = typeof(DashboardController)
            .GetMethod(nameof(DashboardController.GetDashboard))!
            .GetCustomAttributes<AuthorizeAttribute>()
            .SingleOrDefault(a => a.Policy is not null);

        Assert.NotNull(authorize);
        Assert.Equal("CanViewAssets", authorize.Policy);
    }

    [Fact]
    public void Self_service_dashboard_carries_no_policy_of_its_own()
    {
        // Every field on MyDashboardDto is the caller's own data, so the controller-level
        // [Authorize] is the whole gate. A policy here would lock out the roles it is meant for.
        var authorize = typeof(DashboardController)
            .GetMethod(nameof(DashboardController.GetMyDashboard))!
            .GetCustomAttributes<AuthorizeAttribute>();

        Assert.Empty(authorize);
    }
}
