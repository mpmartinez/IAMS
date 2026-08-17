using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace IAMS.Api.Tests;

// SubscriptionService resolves its own AppDbContext from a fresh DI scope on every call,
// so (as in SubscriptionServiceStorageTests) it needs a real service provider wired to the
// same in-memory Sqlite connection rather than the TestDb.Create() context directly.
public class SubscriptionServiceMeteringTests
{
    private static async Task<(SubscriptionService Service, AppDbContext Db, SqliteConnection Conn)> BuildAsync(Guid tenantId)
    {
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));

        var services = new ServiceCollection();
        services.AddSingleton<ITenantProvider>(new FakeTenantProvider(tenantId));
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(conn));
        var provider = services.BuildServiceProvider();

        var service = new SubscriptionService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SubscriptionService>.Instance);

        return (service, db, conn);
    }

    private static async Task<string> EnsureEmployeeRoleAsync(AppDbContext db)
    {
        var role = new ApplicationRole(Roles.Employee) { NormalizedName = "EMPLOYEE" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role.Id;
    }

    private static async Task AssignRoleAsync(AppDbContext db, string userId, string roleId)
    {
        db.UserRoles.Add(new IdentityUserRole<string> { UserId = userId, RoleId = roleId });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CanCreateUserAsync_ignores_employee_only_users_against_the_seat_cap()
    {
        var tenantId = Guid.NewGuid();
        var (service, db, conn) = await BuildAsync(tenantId);
        using (db)
        using (conn)
        {
            var tenant = await TestDb.SeedTenantAsync(db, tenantId); // Pro: MaxUsers = 25
            tenant.MaxUsers = 2;
            await db.SaveChangesAsync();

            var employeeRoleId = await EnsureEmployeeRoleAsync(db);

            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "Staff One");
            await TestDb.SeedUserAsync(db, tenantId, "staff-2", "Staff Two");

            // Seat cap is already exhausted by two billable users.
            Assert.False(await service.CanCreateUserAsync(tenantId));

            // A flood of Employee-only accounts must not push the cap any further.
            for (var i = 0; i < 50; i++)
            {
                var emp = await TestDb.SeedUserAsync(db, tenantId, $"emp-{i}", $"Employee {i}");
                await AssignRoleAsync(db, emp.Id, employeeRoleId);
            }

            Assert.False(await service.CanCreateUserAsync(tenantId));

            // Freeing one billable seat (not an Employee one) makes room again.
            var staff2 = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == "staff-2");
            db.Users.Remove(staff2);
            await db.SaveChangesAsync();

            Assert.True(await service.CanCreateUserAsync(tenantId));
        }
    }

    [Fact]
    public async Task A_user_with_employee_plus_another_role_still_counts_as_billable()
    {
        var tenantId = Guid.NewGuid();
        var (service, db, conn) = await BuildAsync(tenantId);
        using (db)
        using (conn)
        {
            var tenant = await TestDb.SeedTenantAsync(db, tenantId);
            tenant.MaxUsers = 1;
            await db.SaveChangesAsync();

            var employeeRoleId = await EnsureEmployeeRoleAsync(db);
            var staffRole = new ApplicationRole(Roles.Staff) { NormalizedName = "STAFF" };
            db.Roles.Add(staffRole);
            await db.SaveChangesAsync();

            var user = await TestDb.SeedUserAsync(db, tenantId, "dual-1", "Dual Role");
            await AssignRoleAsync(db, user.Id, employeeRoleId);
            await AssignRoleAsync(db, user.Id, staffRole.Id);

            // Employee + Staff is not "only Employee" - it still occupies the single seat.
            Assert.False(await service.CanCreateUserAsync(tenantId));
        }
    }

    [Fact]
    public async Task GetUsageAsync_reports_a_user_count_that_excludes_employee_only_users()
    {
        var tenantId = Guid.NewGuid();
        var (service, db, conn) = await BuildAsync(tenantId);
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var employeeRoleId = await EnsureEmployeeRoleAsync(db);

            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "Staff One");
            var emp = await TestDb.SeedUserAsync(db, tenantId, "emp-1", "Employee One");
            await AssignRoleAsync(db, emp.Id, employeeRoleId);

            var usage = await service.GetUsageAsync(tenantId);

            Assert.Equal(1, usage.CurrentUserCount);
        }
    }

    [Fact]
    public async Task UpdateUserCountAsync_persists_a_count_that_excludes_employee_only_users()
    {
        var tenantId = Guid.NewGuid();
        var (service, db, conn) = await BuildAsync(tenantId);
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            var employeeRoleId = await EnsureEmployeeRoleAsync(db);

            await TestDb.SeedUserAsync(db, tenantId, "staff-1", "Staff One");
            var emp = await TestDb.SeedUserAsync(db, tenantId, "emp-1", "Employee One");
            await AssignRoleAsync(db, emp.Id, employeeRoleId);

            await service.UpdateUserCountAsync(tenantId);

            // AsNoTracking: UpdateUserCountAsync wrote through a different DbContext
            // instance (its own DI scope). Without it, EF's identity resolution would
            // hand back this context's already-tracked (and now stale) Tenant instance
            // instead of the freshly persisted row.
            var tenant = await db.Tenants.IgnoreQueryFilters().AsNoTracking().FirstAsync(t => t.Id == tenantId);
            Assert.Equal(1, tenant.CurrentUserCount);
        }
    }

    [Fact]
    public async Task CanCreateTicketAsync_enforces_the_monthly_cap_and_ignores_older_tickets()
    {
        var tenantId = Guid.NewGuid();
        var (service, db, conn) = await BuildAsync(tenantId);
        using (db)
        using (conn)
        {
            var tenant = await TestDb.SeedTenantAsync(db, tenantId);
            tenant.MaxTicketsPerMonth = 2;
            await db.SaveChangesAsync();
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "J. Dela Cruz");

            // A ticket from a previous month must not count against this month's cap.
            db.Tickets.Add(new Ticket
            {
                TenantId = tenantId,
                TicketNumber = 1,
                Title = "Old ticket",
                RequesterUserId = "emp-1",
                CreatedAt = DateTime.UtcNow.AddMonths(-2)
            });
            await db.SaveChangesAsync();

            Assert.True(await service.CanCreateTicketAsync(tenantId));

            db.Tickets.Add(new Ticket
            {
                TenantId = tenantId,
                TicketNumber = 2,
                Title = "This month #1",
                RequesterUserId = "emp-1",
                CreatedAt = DateTime.UtcNow
            });
            db.Tickets.Add(new Ticket
            {
                TenantId = tenantId,
                TicketNumber = 3,
                Title = "This month #2",
                RequesterUserId = "emp-1",
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            Assert.False(await service.CanCreateTicketAsync(tenantId));
        }
    }
}
