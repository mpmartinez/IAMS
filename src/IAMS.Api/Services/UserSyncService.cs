using System.Security.Claims;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Services;

/// <summary>
/// Service to synchronize m2ID users to the local IAMS database.
/// When users authenticate via m2ID, their JWT claims are used to create
/// local user records so foreign key constraints are satisfied.
/// </summary>
public class UserSyncService(
    AppDbContext db,
    UserManager<ApplicationUser> userManager,
    ILogger<UserSyncService> logger)
{
    public async Task SyncUserFromClaimsAsync(ClaimsPrincipal principal)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            logger.LogWarning("No user ID (sub claim) found in JWT token");
            return;
        }

        // Check if user already exists
        var existingUser = await userManager.FindByIdAsync(userId);
        if (existingUser is not null)
        {
            return;
        }

        // Extract user info from claims
        var email = principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.FindFirstValue("email");
        var name = principal.FindFirstValue(ClaimTypes.Name)
            ?? principal.FindFirstValue("name")
            ?? email
            ?? "Unknown User";
        var role = principal.FindFirstValue(ClaimTypes.Role)
            ?? principal.FindFirstValue("role")
            ?? "Staff";

        logger.LogInformation("Syncing m2ID user to local database: {UserId}, {Name}, {Email}, Role={Role}",
            userId, name, email, role);

        // Create local user record with the m2ID user ID
        var user = new ApplicationUser
        {
            Id = userId,
            UserName = email ?? userId,
            Email = email,
            FullName = name,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            EmailConfirmed = true,
            NormalizedEmail = email?.ToUpperInvariant(),
            NormalizedUserName = (email ?? userId).ToUpperInvariant()
        };

        // Add directly to database to avoid password requirements
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Add role if it exists in the system
        var roleExists = await db.Roles.AnyAsync(r => r.Name == role);
        if (roleExists)
        {
            await userManager.AddToRoleAsync(user, role);
            logger.LogInformation("Added user {UserId} to role {Role}", userId, role);
        }

        logger.LogInformation("Successfully synced m2ID user {UserId} to local database", userId);
    }
}
