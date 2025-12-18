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
                    AssetTag = "LAP-20251218-0001",
                    Manufacturer = "Apple",
                    Model = "MacBook Pro 16\"",
                    ModelYear = 2024,
                    SerialNumber = "C02X1234ABCD",
                    DeviceType = DeviceTypes.Laptop,
                    Status = AssetStatus.InUse,
                    PurchasePrice = 2499.00m,
                    Currency = "USD",
                    WarrantyProvider = "Apple Care+",
                    WarrantyStartDate = DateTime.UtcNow.AddMonths(-6),
                    WarrantyEndDate = DateTime.UtcNow.AddYears(2),
                    AssignedToUserId = staffUser?.Id,
                    Location = "Office - Floor 2",
                    PurchaseDate = DateTime.UtcNow.AddMonths(-6)
                },
                new Asset
                {
                    AssetTag = "MON-20251218-0001",
                    Manufacturer = "Dell",
                    Model = "UltraSharp 27\"",
                    ModelYear = 2023,
                    SerialNumber = "DEL789XYZ",
                    DeviceType = DeviceTypes.Monitor,
                    Status = AssetStatus.InUse,
                    PurchasePrice = 549.00m,
                    Currency = "USD",
                    AssignedToUserId = staffUser?.Id,
                    Location = "Office - Floor 2",
                    PurchaseDate = DateTime.UtcNow.AddMonths(-6)
                },
                new Asset
                {
                    AssetTag = "PHN-20251218-0001",
                    Manufacturer = "Apple",
                    Model = "iPhone 15 Pro",
                    ModelYear = 2024,
                    SerialNumber = "APPL123456",
                    DeviceType = DeviceTypes.Phone,
                    Status = AssetStatus.Available,
                    PurchasePrice = 999.00m,
                    Currency = "USD",
                    WarrantyProvider = "Apple Care+",
                    WarrantyStartDate = DateTime.UtcNow.AddMonths(-1),
                    WarrantyEndDate = DateTime.UtcNow.AddYears(1),
                    Location = "IT Storage",
                    PurchaseDate = DateTime.UtcNow.AddMonths(-1)
                },
                new Asset
                {
                    AssetTag = "PRN-20251218-0001",
                    Manufacturer = "HP",
                    Model = "LaserJet Pro M404dn",
                    ModelYear = 2022,
                    SerialNumber = "HP9876543",
                    DeviceType = DeviceTypes.Printer,
                    Status = AssetStatus.InUse,
                    PurchasePrice = 399.00m,
                    Currency = "USD",
                    Location = "Office - Floor 1",
                    PurchaseDate = DateTime.UtcNow.AddYears(-1)
                },
                new Asset
                {
                    AssetTag = "NET-20251218-0001",
                    Manufacturer = "Cisco",
                    Model = "Catalyst 2960X-24",
                    ModelYear = 2021,
                    SerialNumber = "CSC456789",
                    DeviceType = DeviceTypes.Network,
                    Status = AssetStatus.InUse,
                    PurchasePrice = 1299.00m,
                    Currency = "USD",
                    WarrantyProvider = "Cisco SmartNet",
                    WarrantyStartDate = DateTime.UtcNow.AddYears(-2),
                    WarrantyEndDate = DateTime.UtcNow.AddYears(1),
                    Location = "Server Room",
                    PurchaseDate = DateTime.UtcNow.AddYears(-2)
                },
                new Asset
                {
                    AssetTag = "DSK-20251218-0001",
                    Manufacturer = "Lenovo",
                    Model = "ThinkCentre M90q",
                    ModelYear = 2024,
                    SerialNumber = "LEN456123",
                    DeviceType = DeviceTypes.Desktop,
                    Status = AssetStatus.Available,
                    PurchasePrice = 899.00m,
                    Currency = "USD",
                    WarrantyProvider = "Lenovo Premier Support",
                    WarrantyStartDate = DateTime.UtcNow.AddMonths(-2),
                    WarrantyEndDate = DateTime.UtcNow.AddYears(3),
                    Location = "IT Storage",
                    PurchaseDate = DateTime.UtcNow.AddMonths(-2)
                }
            };

            db.Assets.AddRange(assets);
            await db.SaveChangesAsync();
        }
    }
}
