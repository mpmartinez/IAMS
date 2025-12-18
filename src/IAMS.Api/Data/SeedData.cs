using IAMS.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Data;

public static class SeedData
{
    public static async Task Initialize(
        AppDbContext db,
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager)
    {
        // Create roles
        string[] roles = ["Admin", "Staff", "Auditor"];
        foreach (var role in roles)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole(role));
            }
        }

        // Create admin user
        if (await userManager.FindByEmailAsync("admin@company.com") is null)
        {
            var admin = new ApplicationUser
            {
                UserName = "admin@company.com",
                Email = "admin@company.com",
                FullName = "System Administrator",
                Department = "IT",
                EmailConfirmed = true
            };

            var result = await userManager.CreateAsync(admin, "Admin123!");
            if (result.Succeeded)
            {
                await userManager.AddToRoleAsync(admin, "Admin");
            }
        }

        // Create staff user
        if (await userManager.FindByEmailAsync("staff@company.com") is null)
        {
            var staff = new ApplicationUser
            {
                UserName = "staff@company.com",
                Email = "staff@company.com",
                FullName = "John Smith",
                Department = "Engineering",
                EmailConfirmed = true
            };

            var result = await userManager.CreateAsync(staff, "Staff123!");
            if (result.Succeeded)
            {
                await userManager.AddToRoleAsync(staff, "Staff");
            }
        }

        // Create auditor user
        if (await userManager.FindByEmailAsync("auditor@company.com") is null)
        {
            var auditor = new ApplicationUser
            {
                UserName = "auditor@company.com",
                Email = "auditor@company.com",
                FullName = "Jane Auditor",
                Department = "Compliance",
                EmailConfirmed = true
            };

            var result = await userManager.CreateAsync(auditor, "Auditor123!");
            if (result.Succeeded)
            {
                await userManager.AddToRoleAsync(auditor, "Auditor");
            }
        }

        // Seed assets if none exist
        if (!await db.Assets.AnyAsync())
        {
            var staffUser = await userManager.FindByEmailAsync("staff@company.com");

            var assets = new[]
            {
                new Asset
                {
                    Name = "MacBook Pro 16\"",
                    AssetTag = "LAP-001",
                    SerialNumber = "C02X1234ABCD",
                    Category = AssetCategory.Laptop,
                    Status = AssetStatus.InUse,
                    Location = "Office - Floor 2",
                    AssignedToUserId = staffUser?.Id,
                    PurchasePrice = 2499.00m,
                    PurchaseDate = DateTime.UtcNow.AddMonths(-6),
                    WarrantyExpiry = DateTime.UtcNow.AddYears(2)
                },
                new Asset
                {
                    Name = "Dell UltraSharp 27\"",
                    AssetTag = "MON-001",
                    SerialNumber = "DEL789XYZ",
                    Category = AssetCategory.Monitor,
                    Status = AssetStatus.InUse,
                    Location = "Office - Floor 2",
                    AssignedToUserId = staffUser?.Id,
                    PurchasePrice = 549.00m,
                    PurchaseDate = DateTime.UtcNow.AddMonths(-6)
                },
                new Asset
                {
                    Name = "iPhone 15 Pro",
                    AssetTag = "PHN-001",
                    SerialNumber = "APPL123456",
                    Category = AssetCategory.Phone,
                    Status = AssetStatus.Available,
                    Location = "IT Storage",
                    PurchasePrice = 999.00m,
                    PurchaseDate = DateTime.UtcNow.AddMonths(-1),
                    WarrantyExpiry = DateTime.UtcNow.AddYears(1)
                },
                new Asset
                {
                    Name = "HP LaserJet Pro",
                    AssetTag = "PRT-001",
                    SerialNumber = "HP9876543",
                    Category = AssetCategory.Printer,
                    Status = AssetStatus.InUse,
                    Location = "Office - Floor 1",
                    PurchasePrice = 399.00m,
                    PurchaseDate = DateTime.UtcNow.AddYears(-1)
                },
                new Asset
                {
                    Name = "Cisco Switch 24-Port",
                    AssetTag = "NET-001",
                    SerialNumber = "CSC456789",
                    Category = AssetCategory.Network,
                    Status = AssetStatus.InUse,
                    Location = "Server Room",
                    PurchasePrice = 1299.00m,
                    PurchaseDate = DateTime.UtcNow.AddYears(-2)
                }
            };

            db.Assets.AddRange(assets);
            await db.SaveChangesAsync();
        }
    }
}
