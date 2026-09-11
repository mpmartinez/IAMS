using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetDesk.Api.Tests;

/// <summary>
/// The importer's contract with existing customers: a workbook built against the current
/// template, which has no ExchangeRate column at all, must keep importing untouched.
/// </summary>
public class AssetImportCurrencyTests
{
    private static readonly string[] LegacyHeaders =
    [
        "Name", "DeviceType", "Status", "Manufacturer", "Model", "ModelYear",
        "SerialNumber", "PurchasePrice", "Currency", "PurchaseDate",
        "WarrantyProvider", "WarrantyStartDate", "WarrantyEndDate", "Location", "Notes"
    ];

    private static MemoryStream Workbook(string[] headers, params string[][] rows)
    {
        using var wb = new XLWorkbook();
        var sheet = wb.Worksheets.Add("Assets");

        for (var c = 0; c < headers.Length; c++)
            sheet.Cell(1, c + 1).Value = headers[c];

        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                sheet.Cell(r + 2, c + 1).Value = rows[r][c];

        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    private static AssetImportService ServiceFor(AppDbContext db) =>
        new(db, NullLogger<AssetImportService>.Instance, new LookupService(db));

    private static string[] LegacyRow(string currency, string price) =>
        ["Test Laptop", DeviceTypes.Laptop, AssetStatus.Available, "Dell", "XPS", "2024",
         "SN-1", price, currency, "", "", "", "", "Manila", ""];

    [Fact]
    public async Task A_workbook_with_no_ExchangeRate_column_still_imports()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            using var stream = Workbook(LegacyHeaders, LegacyRow(Currencies.PHP, "50000"));

            var result = await ServiceFor(db).ImportAsync(stream);

            Assert.Empty(result.Errors);
            Assert.Equal(1, result.CreatedCount);
            Assert.Equal(1m, db.Assets.Single().ExchangeRate);
        }
    }

    [Fact]
    public async Task A_blank_rate_on_a_USD_row_is_a_row_error()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            using var stream = Workbook(LegacyHeaders, LegacyRow(Currencies.USD, "1200"));

            var result = await ServiceFor(db).ImportAsync(stream);

            Assert.Single(result.Errors);
            Assert.Contains("exchange rate is required", result.Errors[0].Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, result.CreatedCount);
        }
    }

    [Fact]
    public async Task A_USD_row_with_a_rate_imports_and_keeps_both_numbers()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            string[] headers = [.. LegacyHeaders, "ExchangeRate"];
            string[] row = [.. LegacyRow(Currencies.USD, "1200"), "58.20"];
            using var stream = Workbook(headers, row);

            var result = await ServiceFor(db).ImportAsync(stream);

            Assert.Empty(result.Errors);
            var asset = db.Assets.Single();
            Assert.Equal(1200m, asset.PurchasePrice);
            Assert.Equal(Currencies.USD, asset.Currency);
            Assert.Equal(58.20m, asset.ExchangeRate);
        }
    }

    [Fact]
    public async Task PHP_with_a_rate_other_than_one_is_a_row_error()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            string[] headers = [.. LegacyHeaders, "ExchangeRate"];
            string[] row = [.. LegacyRow(Currencies.PHP, "50000"), "58.20"];
            using var stream = Workbook(headers, row);

            var result = await ServiceFor(db).ImportAsync(stream);

            Assert.Single(result.Errors);
            Assert.Contains("must be exactly 1", result.Errors[0].Message);
        }
    }
}
