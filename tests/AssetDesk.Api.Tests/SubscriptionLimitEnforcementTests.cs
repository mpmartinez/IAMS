using System.Security.Claims;
using System.Text;
using AssetDesk.Api.Controllers;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Api.Services;
using AssetDesk.Shared;
using AssetDesk.Shared.DTOs;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetDesk.Api.Tests;

/// <summary>
/// The asset and storage limits on every path that adds to them. SubscriptionService defined
/// CanCreateAssetAsync for as long as the tiers have existed, but nothing called it: a single
/// create, a bulk import and a goods receipt all went straight past MaxAssets, and asset
/// attachments went past MaxStorageBytes. Each test here uses the real SubscriptionService, since
/// the limit behaviour is the thing under test, and asserts a refusal wrote nothing at all.
/// </summary>
public class SubscriptionLimitEnforcementTests
{
    // SubscriptionService's Can* methods resolve their own AppDbContext from a fresh DI scope,
    // so - as in SubscriptionServiceMeteringTests - it needs a provider wired to the same
    // in-memory connection. ReserveAssetCapacityAsync uses the context it is handed instead.
    private static SubscriptionService RealSubscriptions(SqliteConnection conn, Guid tenantId)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantProvider>(new FakeTenantProvider(tenantId));
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(conn));

        return new SubscriptionService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SubscriptionService>.Instance);
    }

    private static async Task SeedTenantWithAssetsAsync(AppDbContext db, Guid tenantId, int maxAssets, int existing)
    {
        var tenant = await TestDb.SeedTenantAsync(db, tenantId);
        tenant.MaxAssets = maxAssets;
        await db.SaveChangesAsync();

        for (var i = 0; i < existing; i++)
            await TestDb.SeedAssetAsync(db, tenantId, $"EXISTING-{i:D4}");

        db.ChangeTracker.Clear();
    }

    private static Task<int> AssetCountAsync(AppDbContext db, Guid tenantId) =>
        db.Assets.IgnoreQueryFilters().CountAsync(a => a.TenantId == tenantId);

    // ---------------------------------------------------------------------------------------
    // The rule itself
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Reserving_capacity_outside_a_transaction_is_refused_loudly()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await SeedTenantWithAssetsAsync(db, tenantId, maxAssets: 10, existing: 0);

            // Without a transaction the tenant lock would be released the moment the UPDATE
            // auto-committed, and the check would quietly be check-then-insert again.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RealSubscriptions(conn, tenantId).ReserveAssetCapacityAsync(db, tenantId, 1));
        }
    }

    [Theory]
    [InlineData(2, true)]  // exactly fills the limit
    [InlineData(3, false)] // one past it
    public async Task Reserving_capacity_allows_up_to_the_limit_and_no_further(int adding, bool allowed)
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await SeedTenantWithAssetsAsync(db, tenantId, maxAssets: 5, existing: 3);

            await using var tx = await db.Database.BeginTransactionAsync();
            var refusal = await RealSubscriptions(conn, tenantId).ReserveAssetCapacityAsync(db, tenantId, adding);

            if (allowed)
                Assert.Null(refusal);
            else
                Assert.Equal(
                    "Adding 3 assets would exceed your subscription's limit of 5: 3 are already registered, " +
                    "so only 2 more can be added. Please upgrade.",
                    refusal);
        }
    }

    [Fact]
    public async Task An_expired_or_deactivated_subscription_cannot_add_assets_even_with_room_left()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await SeedTenantWithAssetsAsync(db, tenantId, maxAssets: 50, existing: 0);
            var subscriptions = RealSubscriptions(conn, tenantId);

            var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
            tenant.SubscriptionEndDate = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();

            await using (var tx = await db.Database.BeginTransactionAsync())
                Assert.Contains("inactive or has expired",
                    await subscriptions.ReserveAssetCapacityAsync(db, tenantId, 1));

            tenant.SubscriptionEndDate = null;
            tenant.IsActive = false;
            await db.SaveChangesAsync();

            await using (var tx = await db.Database.BeginTransactionAsync())
                Assert.Contains("inactive or has expired",
                    await subscriptions.ReserveAssetCapacityAsync(db, tenantId, 1));

            Assert.False(await subscriptions.CanCreateAssetAsync(tenantId));
        }
    }

    // ---------------------------------------------------------------------------------------
    // Single create
    // ---------------------------------------------------------------------------------------

    private static AssetsController AssetsControllerFor(AppDbContext db, SqliteConnection conn, Guid tenantId) =>
        new(db, null!, null!, new FakeLookupService(), new AssetTagGenerator(db),
            new FakeTenantProvider(tenantId), RealSubscriptions(conn, tenantId));

    private static CreateAssetDto NewLaptop() => new()
    {
        DeviceType = DeviceTypes.Laptop,
        Status = AssetStatus.Available,
        Currency = Currencies.PHP
    };

    [Fact]
    public async Task Creating_an_asset_at_the_limit_is_refused_and_writes_nothing()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await SeedTenantWithAssetsAsync(db, tenantId, maxAssets: 2, existing: 2);

            var result = await AssetsControllerFor(db, conn, tenantId).CreateAsset(NewLaptop());

            var body = Assert.IsType<ApiResponse<AssetDto>>(
                Assert.IsType<BadRequestObjectResult>(result.Result).Value);
            Assert.False(body.Success);
            Assert.Equal("Asset limit reached for your subscription (2 assets). Please upgrade.", body.Message);

            db.ChangeTracker.Clear();
            Assert.Equal(2, await AssetCountAsync(db, tenantId));
        }
    }

    [Fact]
    public async Task Creating_the_asset_that_fills_the_limit_succeeds()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await SeedTenantWithAssetsAsync(db, tenantId, maxAssets: 2, existing: 1);

            var result = await AssetsControllerFor(db, conn, tenantId).CreateAsset(NewLaptop());

            Assert.IsType<CreatedAtActionResult>(result.Result);
            db.ChangeTracker.Clear();
            Assert.Equal(2, await AssetCountAsync(db, tenantId));
        }
    }

    // ---------------------------------------------------------------------------------------
    // Bulk import
    // ---------------------------------------------------------------------------------------

    private static readonly string[] ImportHeaders =
    [
        "Name", "DeviceType", "Status", "Manufacturer", "Model", "ModelYear",
        "SerialNumber", "PurchasePrice", "Currency", "PurchaseDate",
        "WarrantyProvider", "WarrantyStartDate", "WarrantyEndDate", "Location", "Notes"
    ];

    private static string[] ValidRow(string name) =>
        [name, DeviceTypes.Laptop, AssetStatus.Available, "Dell", "XPS", "2024",
         "", "1200", Currencies.PHP, "", "", "", "", "Manila", ""];

    private static MemoryStream Workbook(params string[][] rows)
    {
        using var wb = new XLWorkbook();
        var sheet = wb.Worksheets.Add("Assets");
        for (var c = 0; c < ImportHeaders.Length; c++)
            sheet.Cell(1, c + 1).Value = ImportHeaders[c];
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                sheet.Cell(r + 2, c + 1).Value = rows[r][c];

        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    private static AssetImportService ImportServiceFor(AppDbContext db, SqliteConnection conn, Guid tenantId) =>
        new(db, NullLogger<AssetImportService>.Instance, new FakeLookupService(), new AssetTagGenerator(db),
            RealSubscriptions(conn, tenantId));

    [Fact]
    public async Task An_import_that_would_exceed_the_limit_is_refused_whole_and_still_reports_bad_rows()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await SeedTenantWithAssetsAsync(db, tenantId, maxAssets: 3, existing: 1);

            // Three valid rows against room for two, plus one row that fails validation. The two
            // rows that would fit must NOT be imported on their own.
            string[] badRow = ["Broken", DeviceTypes.Laptop, "NotAStatus", "", "", "", "", "", Currencies.PHP, "", "", "", "", "", ""];
            using var file = Workbook(ValidRow("A"), ValidRow("B"), badRow, ValidRow("C"));

            var result = await ImportServiceFor(db, conn, tenantId).ImportAsync(tenantId, file);

            Assert.Equal(0, result.CreatedCount);
            Assert.Equal(4, result.TotalRows);
            Assert.Equal(4, result.FailedCount);
            Assert.Empty(result.CreatedAssets);

            // The limit first, since it is why nothing was imported - and it counts only the
            // three rows that would have been created, not the one that failed validation.
            Assert.Equal(0, result.Errors[0].RowNumber);
            Assert.Equal(
                "Nothing was imported. Adding 3 assets would exceed your subscription's limit of 3: " +
                "1 are already registered, so only 2 more can be added. Please upgrade.",
                result.Errors[0].Message);
            Assert.Contains(result.Errors, e => e.RowNumber == 4 && e.Message.Contains("Invalid Status"));

            db.ChangeTracker.Clear();
            Assert.Equal(1, await AssetCountAsync(db, tenantId));
        }
    }

    [Fact]
    public async Task An_import_that_exactly_fills_the_limit_imports_every_row()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await SeedTenantWithAssetsAsync(db, tenantId, maxAssets: 3, existing: 1);
            using var file = Workbook(ValidRow("A"), ValidRow("B"));

            var result = await ImportServiceFor(db, conn, tenantId).ImportAsync(tenantId, file);

            Assert.Empty(result.Errors);
            Assert.Equal(2, result.CreatedCount);
            db.ChangeTracker.Clear();
            Assert.Equal(3, await AssetCountAsync(db, tenantId));
        }
    }

    [Fact]
    public async Task The_import_endpoint_needs_a_current_tenant_to_meter_against()
    {
        var (db, conn) = TestDb.Create(new FakeTenantProvider(null));
        using (db)
        using (conn)
        {
            var controller = new AssetsController(
                db, null!, null!, null!, null!, new FakeTenantProvider(null), null!);
            using var file = Workbook(ValidRow("A"));

            var result = await controller.ImportAssets(
                new FormFile(file, 0, file.Length, "file", "assets.xlsx"), CancellationToken.None);

            var body = Assert.IsType<ApiResponse<ImportAssetsResultDto>>(
                Assert.IsType<BadRequestObjectResult>(result.Result).Value);
            Assert.Equal("Select an organisation first.", body.Message);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Goods receipt
    // ---------------------------------------------------------------------------------------

    private static async Task<(PurchaseOrder Order, PurchaseOrderLine Laptops, PurchaseOrderLine Software)> SeedOrderAsync(
        AppDbContext db, Guid tenantId)
    {
        var supplier = new Supplier { TenantId = tenantId, Name = "Acme Computers" };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var order = new PurchaseOrder
        {
            TenantId = tenantId, PoNumber = 1, SupplierId = supplier.Id,
            Currency = Currencies.PHP, Status = PurchaseOrderStatus.Ordered,
            OrderDate = new DateTime(2026, 9, 1), CreatedByUserId = "user-1"
        };
        order.Lines.Add(new PurchaseOrderLine
        {
            DeviceType = DeviceTypes.Laptop, Description = "Dell Latitude 5540", Quantity = 10, UnitPrice = 50000m
        });
        order.Lines.Add(new PurchaseOrderLine
        {
            DeviceType = DeviceTypes.Software, Description = "Microsoft 365 Business Standard", Quantity = 50, UnitPrice = 700m
        });
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        var laptops = order.Lines.Single(l => l.DeviceType == DeviceTypes.Laptop);
        var software = order.Lines.Single(l => l.DeviceType == DeviceTypes.Software);
        db.ChangeTracker.Clear();
        return (order, laptops, software);
    }

    private static GoodsReceiptService ReceiptServiceFor(
        AppDbContext db, SqliteConnection conn, Guid tenantId) =>
        new(db, new AssetTagGenerator(db), RealSubscriptions(conn, tenantId), NullLogger<GoodsReceiptService>.Instance);

    private static ReceiveGoodsDto Receive(params ReceiveLineDto[] lines) => new()
    {
        ReceiptDate = new DateTime(2026, 9, 12),
        ExchangeRate = 1m,
        Lines = [.. lines]
    };

    [Fact]
    public async Task A_receipt_that_would_exceed_the_limit_is_refused_and_writes_nothing()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await SeedTenantWithAssetsAsync(db, tenantId, maxAssets: 5, existing: 3);
            var (order, laptops, _) = await SeedOrderAsync(db, tenantId);

            var result = await ReceiptServiceFor(db, conn, tenantId).ReceiveAsync(
                tenantId, order.Id,
                Receive(new ReceiveLineDto { PurchaseOrderLineId = laptops.Id, QuantityReceived = 3 }),
                "user-1");

            Assert.False(result.Success);
            Assert.Equal(
                "Adding 3 assets would exceed your subscription's limit of 5: 3 are already registered, " +
                "so only 2 more can be added. Please upgrade.",
                result.Message);

            db.ChangeTracker.Clear();
            Assert.Equal(3, await AssetCountAsync(db, tenantId));
            Assert.Empty(db.GoodsReceipts.IgnoreQueryFilters());
            var reloaded = await db.PurchaseOrders.IgnoreQueryFilters().Include(p => p.Lines).SingleAsync();
            Assert.Equal(PurchaseOrderStatus.Ordered, reloaded.Status);
            Assert.All(reloaded.Lines, l => Assert.Equal(0, l.ReceivedQuantity));
        }
    }

    [Fact]
    public async Task A_receipt_that_fits_the_limit_is_recorded()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await SeedTenantWithAssetsAsync(db, tenantId, maxAssets: 5, existing: 3);
            var (order, laptops, _) = await SeedOrderAsync(db, tenantId);

            var result = await ReceiptServiceFor(db, conn, tenantId).ReceiveAsync(
                tenantId, order.Id,
                Receive(new ReceiveLineDto { PurchaseOrderLineId = laptops.Id, QuantityReceived = 2 }),
                "user-1");

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            Assert.Equal(5, await AssetCountAsync(db, tenantId));
        }
    }

    /// <summary>
    /// A software line becomes seats on a licence, not assets, so it is not metered against
    /// MaxAssets - a tenant at its asset limit can still receive licences it has paid for.
    /// </summary>
    [Fact]
    public async Task A_software_only_receipt_is_not_metered_against_the_asset_limit()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            await SeedTenantWithAssetsAsync(db, tenantId, maxAssets: 1, existing: 1);
            var (order, _, software) = await SeedOrderAsync(db, tenantId);

            var result = await ReceiptServiceFor(db, conn, tenantId).ReceiveAsync(
                tenantId, order.Id,
                Receive(new ReceiveLineDto
                {
                    PurchaseOrderLineId = software.Id,
                    QuantityReceived = 20,
                    NewLicence = new NewLicenceInputDto
                    {
                        Name = "Microsoft 365 Business Standard", LicenceModel = LicenceModels.PerUser
                    }
                }),
                "user-1");

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            Assert.Equal(1, await AssetCountAsync(db, tenantId));
            Assert.Single(db.LicenceEntitlements.IgnoreQueryFilters());
        }
    }

    /// <summary>
    /// The limit only holds under concurrency because the tenant row is locked before the count,
    /// and a receipt must not deadlock against another by taking its locks in a different order.
    /// A lock is not observable from a single-threaded test; the statement that takes it is. So
    /// this asserts the tenant row is written before the order row, and the order row before any
    /// line is claimed - the single lock order that GoodsReceiptService documents.
    /// </summary>
    [Fact]
    public async Task A_receipt_locks_the_tenant_row_before_the_order_row()
    {
        var tenantId = Guid.NewGuid();
        var recorder = new GoodsReceiptTests.SqlRecorder();
        var (db, conn) = TestDb.Create(
            new FakeTenantProvider(tenantId), builder => builder.AddInterceptors(recorder));
        using (db)
        using (conn)
        {
            await SeedTenantWithAssetsAsync(db, tenantId, maxAssets: 50, existing: 0);
            var (order, laptops, _) = await SeedOrderAsync(db, tenantId);
            recorder.Commands.Clear();

            var result = await ReceiptServiceFor(db, conn, tenantId).ReceiveAsync(
                tenantId, order.Id,
                Receive(new ReceiveLineDto { PurchaseOrderLineId = laptops.Id, QuantityReceived = 2 }),
                "user-1");

            Assert.True(result.Success, result.Message);

            var lockedTenant = recorder.Commands.FindIndex(c => c.Contains("UPDATE \"Tenants\"", StringComparison.Ordinal));
            var lockedOrder = recorder.Commands.FindIndex(c => c.Contains("UPDATE \"PurchaseOrders\"", StringComparison.Ordinal));
            var claimedLine = recorder.Commands.FindIndex(c => c.Contains("UPDATE \"PurchaseOrderLines\"", StringComparison.Ordinal));

            Assert.True(lockedTenant >= 0, "The tenant row was never locked, so nothing serialises the asset limit.");
            Assert.True(lockedTenant < lockedOrder,
                $"The tenant row was locked at statement {lockedTenant} but the order at {lockedOrder}.");
            Assert.True(lockedOrder < claimedLine);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Asset attachment storage
    // ---------------------------------------------------------------------------------------

    private sealed class RecordingFileStorage : IFileStorageService
    {
        public List<string> Saved { get; } = [];

        public Task<string> SaveFileAsync(Stream fileStream, string fileName, string contentType)
        {
            Saved.Add(fileName);
            return Task.FromResult($"stored-{fileName}");
        }

        public Task<bool> DeleteFileAsync(string storedFileName) => Task.FromResult(true);
        public Task<(Stream FileStream, string ContentType)?> GetFileAsync(string storedFileName) =>
            throw new NotSupportedException();
        public bool IsValidFileType(string contentType) => true;
        public bool IsValidFileSize(long sizeBytes) => true;
    }

    private static IFormFile PdfOf(int bytes)
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes(new string('x', bytes)));
        return new FormFile(stream, 0, bytes, "file", "invoice.pdf")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf"
        };
    }

    private static async Task<(Asset Asset, RecordingFileStorage Storage, AttachmentsController Controller)> SeedStorageAsync(
        AppDbContext db, SqliteConnection conn, Guid tenantId, long maxStorageBytes, long usedBytes)
    {
        var tenant = await TestDb.SeedTenantAsync(db, tenantId);
        tenant.MaxStorageBytes = maxStorageBytes;
        await TestDb.SeedUserAsync(db, tenantId, "staff-1", "R. Santos");
        var asset = await TestDb.SeedAssetAsync(db, tenantId, "LAP-0001");

        db.Attachments.Add(new Attachment
        {
            TenantId = tenantId,
            AssetId = asset.Id,
            FileName = "existing.pdf",
            StoredFileName = "stored-existing.pdf",
            ContentType = "application/pdf",
            FileSizeBytes = usedBytes,
            Category = AttachmentCategories.Receipt,
            UploadedByUserId = "staff-1"
        });
        await db.SaveChangesAsync();

        var storage = new RecordingFileStorage();
        var controller = new AttachmentsController(
            db, storage, new FakeLookupService(), RealSubscriptions(conn, tenantId), new FakeTenantProvider(tenantId))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, "staff-1")], "test"))
                }
            }
        };

        return (asset, storage, controller);
    }

    [Fact]
    public async Task An_asset_attachment_past_the_storage_limit_is_refused_before_the_file_is_stored()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (asset, storage, controller) = await SeedStorageAsync(db, conn, tenantId, maxStorageBytes: 1000, usedBytes: 900);

            var result = await controller.UploadAttachment(asset.Id, PdfOf(101), AttachmentCategories.Receipt);

            var body = Assert.IsType<ApiResponse<AttachmentDto>>(
                Assert.IsType<BadRequestObjectResult>(result.Result).Value);
            Assert.Equal("Storage limit reached for your subscription. Please upgrade.", body.Message);
            Assert.Empty(storage.Saved);
            Assert.Equal(1, await db.Attachments.IgnoreQueryFilters().CountAsync());
        }
    }

    [Fact]
    public async Task An_asset_attachment_that_exactly_fills_the_storage_limit_is_accepted()
    {
        var tenantId = Guid.NewGuid();
        var (db, conn) = TestDb.Create(new FakeTenantProvider(tenantId));
        using (db)
        using (conn)
        {
            var (asset, storage, controller) = await SeedStorageAsync(db, conn, tenantId, maxStorageBytes: 1000, usedBytes: 900);

            var result = await controller.UploadAttachment(asset.Id, PdfOf(100), AttachmentCategories.Receipt);

            Assert.IsType<CreatedAtActionResult>(result.Result);
            Assert.Single(storage.Saved);
            Assert.Equal(2, await db.Attachments.IgnoreQueryFilters().CountAsync());
        }
    }
}
