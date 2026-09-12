using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetDesk.Api.Tests;

/// <summary>
/// Guards the workbook we actually hand people. The documented flow is: download the template,
/// fill it in, upload it. Before the ExchangeRate column existed there, a user entering
/// USD/1200 got "An exchange rate is required for USD" on every row with no column to satisfy
/// it - the feature was unreachable through its own documented path.
///
/// These tests read the shipped .xlsx rather than a hand-built one on purpose: a workbook
/// constructed in the test can never catch the template drifting away from the importer.
/// </summary>
public class AssetImportTemplateTests
{
    private static string TemplatePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AssetDesk.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        var path = Path.Combine(
            dir.FullName, "src", "AssetDesk.Web", "wwwroot", "templates", "Asset_Upload_Template.xlsx");
        Assert.True(File.Exists(path), $"Template not found at {path}");
        return path;
    }

    private static AssetImportService ServiceFor(AppDbContext db) =>
        new(db, NullLogger<AssetImportService>.Instance, new LookupService(db), new AssetTagGenerator(db));

    [Fact]
    public void The_template_offers_an_ExchangeRate_column_next_to_Currency()
    {
        using var wb = new XLWorkbook(TemplatePath());
        var sheet = wb.Worksheet("Assets");
        var headers = sheet.Row(1).CellsUsed().Select(c => c.GetString().Trim()).ToList();

        Assert.Contains("ExchangeRate", headers);

        // Beside the code it qualifies, not bolted on at the far right where nobody scrolls.
        Assert.Equal(headers.IndexOf("Currency") + 1, headers.IndexOf("ExchangeRate"));
    }

    [Fact]
    public async Task The_template_imports_as_downloaded_sample_row_and_all()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await TestDb.SeedTenantAsync(db, tenantId);
            using var stream = File.OpenRead(TemplatePath());

            var result = await ServiceFor(db).ImportAsync(stream);

            // The sample row is booked in USD, so a template whose sample carries no rate would
            // fail the moment anyone uploaded it untouched.
            Assert.Empty(result.Errors);
            Assert.Equal(1, result.CreatedCount);

            var asset = db.Assets.Single();
            Assert.Equal(Currencies.USD, asset.Currency);
            Assert.Equal(1299.99m, asset.PurchasePrice);
            Assert.Equal(58.20m, asset.ExchangeRate);
        }
    }

    [Fact]
    public void The_template_only_offers_currencies_the_importer_accepts()
    {
        using var wb = new XLWorkbook(TemplatePath());
        var offered = wb.Worksheet("Reference")
            .Column("C")
            .CellsUsed()
            .Skip(1)   // the header
            .Select(c => c.GetString().Trim())
            .ToList();

        // The dropdown used to list EUR, GBP, JPY, CAD and AUD - every one of which the
        // importer rejects, because Currencies.All holds only these two.
        Assert.Equal(Currencies.All.Order(), offered.Order());
    }

    [Fact]
    public void The_currency_validation_covers_only_the_Currency_column()
    {
        using var wb = new XLWorkbook(TemplatePath());
        var sheet = wb.Worksheet("Assets");
        var headers = sheet.Row(1).CellsUsed().Select(c => c.GetString().Trim()).ToList();
        var currencyColumn = headers.IndexOf("Currency") + 1; // ClosedXML columns are 1-based

        // Inserting ExchangeRate next to Currency widened this validation's range from
        // I2:I1000 to I2:J1000, so any value typed into ExchangeRate had to come from the
        // Currency list on the Reference sheet - errorStyle="stop" made every rate rejected.
        var currencyValidation = sheet.DataValidations.Single(dv => dv.InputMessage == "Choose a currency");

        foreach (var range in currencyValidation.Ranges)
        {
            Assert.Equal(currencyColumn, range.RangeAddress.FirstAddress.ColumnNumber);
            Assert.Equal(currencyColumn, range.RangeAddress.LastAddress.ColumnNumber);
        }
    }
}
