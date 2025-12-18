using IAMS.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Data;

public static class SeedData
{
    public static async Task Initialize(AppDbContext db)
    {
        if (await db.Users.AnyAsync())
            return;

        var admin = new User
        {
            Email = "admin@company.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin123"),
            FullName = "System Administrator",
            Department = "IT",
            Role = "Admin"
        };

        var user = new User
        {
            Email = "user@company.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("user123"),
            FullName = "John Smith",
            Department = "Engineering",
            Role = "User"
        };

        db.Users.AddRange(admin, user);
        await db.SaveChangesAsync();

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
                AssignedToUserId = user.Id,
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
                AssignedToUserId = user.Id,
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
