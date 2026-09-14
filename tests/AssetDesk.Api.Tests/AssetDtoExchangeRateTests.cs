using System.Security.Claims;
using AssetDesk.Api.Authorization;
using AssetDesk.Api.Controllers;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared.DTOs;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetDesk.Api.Tests;

/// <summary>
/// AssetDto.ExchangeRate defaults to 1m, so a hand-built mapping that forgets it does not fail
/// to compile - it quietly reports every USD asset as booked at parity. Two places built the DTO
/// by hand rather than through AssetsController.MapToDto, and both are covered here.
/// </summary>
public class AssetDtoExchangeRateTests
{
    private static AssignmentsController BuildController(AppDbContext db, Guid tenantId, string callerId) =>
        new(db, new FakeTenantProvider(tenantId))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [
                            new Claim(ClaimTypes.NameIdentifier, callerId),
                            new Claim(Permissions.ClaimType, Permissions.AssignmentsView)
                        ],
                        "TestAuth"))
                }
            }
        };

    [Fact]
    public async Task GetUserAssets_reports_the_rate_each_asset_was_booked_at()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            await TestDb.SeedUserAsync(db, tenantId, "emp-1", "Holder");
            db.Assets.Add(new Asset
            {
                TenantId = tenantId,
                AssetTag = "LAP-USD",
                DeviceType = DeviceTypes.Laptop,
                Status = AssetStatus.InUse,
                AssignedToUserId = "emp-1",
                PurchasePrice = 1200m,
                Currency = Currencies.USD,
                ExchangeRate = 58.20m
            });
            await db.SaveChangesAsync();

            var result = await BuildController(db, tenantId, "emp-1").GetUserAssets("emp-1");

            var body = Assert.IsType<UserAssetsDto>(
                Assert.IsType<OkObjectResult>(result.Result).Value);

            Assert.Equal(58.20m, Assert.Single(body.CurrentAssets).ExchangeRate);
        }
    }

    [Fact]
    public async Task The_importer_echoes_the_rate_back_on_the_assets_it_created()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);

            using var ms = new MemoryStream();
            using (var wb = new XLWorkbook())
            {
                var sheet = wb.Worksheets.Add("Assets");
                string[] headers =
                [
                    "Name", "DeviceType", "Status", "Manufacturer", "Model", "ModelYear",
                    "SerialNumber", "PurchasePrice", "Currency", "ExchangeRate", "PurchaseDate",
                    "WarrantyProvider", "WarrantyStartDate", "WarrantyEndDate", "Location", "Notes"
                ];
                string[] row =
                [
                    "Imported Laptop", DeviceTypes.Laptop, AssetStatus.Available, "Dell", "XPS",
                    "2024", "SN-9", "1200", Currencies.USD, "58.20", "", "", "", "", "Manila", ""
                ];
                for (var c = 0; c < headers.Length; c++)
                    sheet.Cell(1, c + 1).Value = headers[c];
                for (var c = 0; c < row.Length; c++)
                    sheet.Cell(2, c + 1).Value = row[c];
                wb.SaveAs(ms);
            }
            ms.Position = 0;

            var service = new AssetImportService(
                db, NullLogger<AssetImportService>.Instance, new LookupService(db), new AssetTagGenerator(db),
                new FakeSubscriptionService());

            var result = await service.ImportAsync(tenantId, ms);

            Assert.Empty(result.Errors);
            var created = Assert.Single(result.CreatedAssets);
            Assert.Equal(Currencies.USD, created.Currency);
            Assert.Equal(58.20m, created.ExchangeRate);
        }
    }
}
