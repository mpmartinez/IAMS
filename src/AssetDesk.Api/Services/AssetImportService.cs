using ClosedXML.Excel;
using AssetDesk.Api.Data;
using AssetDesk.Api.Entities;
using AssetDesk.Shared.DTOs;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Services;

public interface IAssetImportService
{
    /// <summary>
    /// The tenant is the one whose asset limit the rows are metered against. It is a parameter
    /// rather than read from ITenantProvider here for the reason IGoodsReceiptService gives.
    /// </summary>
    Task<ImportAssetsResultDto> ImportAsync(Guid tenantId, Stream xlsxStream, CancellationToken ct = default);
}

public class AssetImportService(
    AppDbContext db, ILogger<AssetImportService> logger, ILookupService lookups, IAssetTagGenerator tags,
    ISubscriptionService subscriptions) : IAssetImportService
{
    private static readonly string[] ExpectedHeaders =
    [
        "Name", "DeviceType", "Status", "Manufacturer", "Model", "ModelYear",
        "SerialNumber", "PurchasePrice", "Currency", "PurchaseDate",
        "WarrantyProvider", "WarrantyStartDate", "WarrantyEndDate", "Location", "Notes"
    ];

    private static readonly string[] ValidStatuses =
        [AssetStatus.Available, AssetStatus.InUse, AssetStatus.Maintenance, AssetStatus.Retired, AssetStatus.Lost];

    public async Task<ImportAssetsResultDto> ImportAsync(Guid tenantId, Stream xlsxStream, CancellationToken ct = default)
    {
        using var workbook = new XLWorkbook(xlsxStream);

        var sheet = workbook.Worksheets.FirstOrDefault(w =>
            string.Equals(w.Name, "Assets", StringComparison.OrdinalIgnoreCase))
            ?? workbook.Worksheets.First();

        var headerRow = sheet.FirstRowUsed();
        if (headerRow is null)
            return new ImportAssetsResultDto { Errors = [new() { RowNumber = 0, Message = "Sheet is empty." }] };

        var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in headerRow.CellsUsed())
            headerMap[cell.GetString().Trim()] = cell.Address.ColumnNumber;

        var missing = ExpectedHeaders.Where(h => !headerMap.ContainsKey(h)).ToList();
        if (missing.Count > 0)
            return new ImportAssetsResultDto
            {
                Errors = [new() { RowNumber = headerRow.RowNumber(), Message = $"Missing columns: {string.Join(", ", missing)}" }]
            };

        var errors = new List<ImportRowError>();
        var toCreate = new List<Asset>();
        var tagSequenceCache = new Dictionary<string, int>();

        var dataRows = sheet.RowsUsed().Skip(1);
        foreach (var row in dataRows)
        {
            var rowNum = row.RowNumber();
            if (row.IsEmpty()) continue;

            try
            {
                var asset = await BuildAssetAsync(tenantId, row, headerMap, tagSequenceCache, ct);
                toCreate.Add(asset);
            }
            catch (ImportRowException ex)
            {
                errors.Add(new ImportRowError { RowNumber = rowNum, Message = ex.Message });
            }
        }

        if (toCreate.Count > 0)
        {
            string? limitRefusal;
            try
            {
                // The limit check and the insert share one transaction - see
                // ReserveAssetCapacityAsync for why a check made before it would not hold. Run
                // through the execution strategy because production retries transient failures
                // and EF refuses a user-initiated transaction outside one; each attempt starts
                // from a cleared tracker so a replay adds the rows once, not on top of the
                // failed attempt's.
                var strategy = db.Database.CreateExecutionStrategy();
                limitRefusal = await strategy.ExecuteAsync(async () =>
                {
                    db.ChangeTracker.Clear();
                    await using var tx = await db.Database.BeginTransactionAsync(ct);

                    if (await subscriptions.ReserveAssetCapacityAsync(db, tenantId, toCreate.Count, ct) is { } refusal)
                        return refusal;

                    db.Assets.AddRange(toCreate);
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    return null;
                });
            }
            catch (Exception ex)
            {
                // Rolled back, but the tracker still holds every row as Added; a later save on
                // this scoped context would insert them after all.
                db.ChangeTracker.Clear();

                logger.LogError(ex, "Failed to persist imported assets");
                return new ImportAssetsResultDto
                {
                    TotalRows = toCreate.Count + errors.Count,
                    CreatedCount = 0,
                    FailedCount = toCreate.Count + errors.Count,
                    Errors =
                    [
                        ..errors,
                        new ImportRowError { RowNumber = 0, Message = $"Database save failed: {ex.Message}" }
                    ]
                };
            }

            // The whole file is refused, not the rows past the limit. Importing the first N rows
            // in sheet order would leave an arbitrary slice of the register behind, and with no
            // de-duplication on import, re-uploading the rest means hand-editing the file to
            // remove exactly the rows that made it. Nothing written is the state a user can act
            // on: trim the file or upgrade, then upload it again. Rows that failed validation are
            // still reported, so both can be fixed in one pass - and they are not counted against
            // the limit, since they would never have been created.
            if (limitRefusal is not null)
            {
                return new ImportAssetsResultDto
                {
                    TotalRows = toCreate.Count + errors.Count,
                    CreatedCount = 0,
                    FailedCount = toCreate.Count + errors.Count,
                    Errors =
                    [
                        new ImportRowError { RowNumber = 0, Message = $"Nothing was imported. {limitRefusal}" },
                        ..errors
                    ]
                };
            }
        }

        return new ImportAssetsResultDto
        {
            TotalRows = toCreate.Count + errors.Count,
            CreatedCount = toCreate.Count,
            FailedCount = errors.Count,
            Errors = errors,
            CreatedAssets = toCreate.Select(MapToDto).ToList()
        };
    }

    private async Task<Asset> BuildAssetAsync(
        Guid tenantId,
        IXLRow row,
        Dictionary<string, int> headerMap,
        Dictionary<string, int> tagSequenceCache,
        CancellationToken ct)
    {
        var deviceType = ReadString(row, headerMap, "DeviceType");
        if (string.IsNullOrWhiteSpace(deviceType))
            throw new ImportRowException("DeviceType is required.");
        // Editable lookup data, not the DeviceTypes constant.
        if (!await lookups.IsActiveValueAsync(LookupTypes.DeviceType, deviceType, ct))
            throw new ImportRowException($"Invalid DeviceType '{deviceType}'.");

        var status = ReadString(row, headerMap, "Status");
        if (string.IsNullOrWhiteSpace(status))
            throw new ImportRowException("Status is required.");
        // Locked - AssetStatus is branched on elsewhere, so this keeps validating against the
        // constant rather than admin-editable data.
        if (!ValidStatuses.Contains(status))
            throw new ImportRowException($"Invalid Status '{status}'. Allowed: {string.Join(", ", ValidStatuses)}.");

        var currency = ReadString(row, headerMap, "Currency");
        if (string.IsNullOrWhiteSpace(currency))
            currency = Currencies.PHP;
        if (!await lookups.IsActiveValueAsync(LookupTypes.Currency, currency, ct))
            throw new ImportRowException(
                $"Invalid Currency '{currency}'. Supported: {string.Join(", ", Currencies.All)}.");

        // Deliberately NOT in ExpectedHeaders: a header listed there is required, and adding
        // this one would reject every workbook built against the template customers already
        // have. ReadDecimal returns null when the column is absent.
        var exchangeRate = ReadDecimal(row, headerMap, "ExchangeRate") ?? 1m;

        // Same three rules the API enforces, from the same method - see CurrencyRules.
        var rateError = CurrencyRules.Validate(currency, exchangeRate);
        if (rateError is not null)
            throw new ImportRowException(rateError);

        var modelYear = ReadInt(row, headerMap, "ModelYear");
        if (modelYear is < 1900 or > 2100)
            throw new ImportRowException("ModelYear must be between 1900 and 2100.");

        var purchasePrice = ReadDecimal(row, headerMap, "PurchasePrice");
        if (purchasePrice is < 0)
            throw new ImportRowException("PurchasePrice must be a positive value.");

        var warrantyStart = ReadDate(row, headerMap, "WarrantyStartDate");
        var warrantyEnd = ReadDate(row, headerMap, "WarrantyEndDate");
        if (warrantyStart.HasValue && warrantyEnd.HasValue && warrantyStart > warrantyEnd)
            throw new ImportRowException("WarrantyStartDate cannot be after WarrantyEndDate.");

        var assetTag = await tags.NextAsync(deviceType, tagSequenceCache, ct);

        return new Asset
        {
            // Explicit rather than left to SaveChanges stamping, so the rows belong to exactly
            // the tenant whose limit they were metered against.
            TenantId = tenantId,
            AssetTag = assetTag,
            DeviceType = deviceType,
            Status = status,
            Currency = currency,
            ExchangeRate = exchangeRate,
            Name = ReadString(row, headerMap, "Name"),
            Manufacturer = ReadString(row, headerMap, "Manufacturer"),
            Model = ReadString(row, headerMap, "Model"),
            ModelYear = modelYear,
            SerialNumber = ReadString(row, headerMap, "SerialNumber"),
            PurchasePrice = purchasePrice,
            PurchaseDate = ReadDate(row, headerMap, "PurchaseDate"),
            WarrantyProvider = ReadString(row, headerMap, "WarrantyProvider"),
            WarrantyStartDate = warrantyStart,
            WarrantyEndDate = warrantyEnd,
            Location = ReadString(row, headerMap, "Location"),
            Notes = ReadString(row, headerMap, "Notes")
        };
    }

    private static string? ReadString(IXLRow row, Dictionary<string, int> headerMap, string column)
    {
        if (!headerMap.TryGetValue(column, out var col)) return null;
        var value = row.Cell(col).GetString().Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int? ReadInt(IXLRow row, Dictionary<string, int> headerMap, string column)
    {
        if (!headerMap.TryGetValue(column, out var col)) return null;
        var cell = row.Cell(col);
        if (cell.IsEmpty()) return null;

        if (cell.TryGetValue<double>(out var d)) return (int)d;
        var raw = cell.GetString().Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        if (int.TryParse(raw, out var i)) return i;
        throw new ImportRowException($"{column} must be an integer (got '{raw}').");
    }

    private static decimal? ReadDecimal(IXLRow row, Dictionary<string, int> headerMap, string column)
    {
        if (!headerMap.TryGetValue(column, out var col)) return null;
        var cell = row.Cell(col);
        if (cell.IsEmpty()) return null;

        if (cell.TryGetValue<double>(out var d)) return (decimal)d;
        var raw = cell.GetString().Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        if (decimal.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dec))
            return dec;
        throw new ImportRowException($"{column} must be a number (got '{raw}').");
    }

    private static DateTime? ReadDate(IXLRow row, Dictionary<string, int> headerMap, string column)
    {
        if (!headerMap.TryGetValue(column, out var col)) return null;
        var cell = row.Cell(col);
        if (cell.IsEmpty()) return null;

        if (cell.TryGetValue<DateTime>(out var dt))
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);

        var raw = cell.GetString().Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
            return parsed;
        throw new ImportRowException($"{column} must be a date (got '{raw}'). Use YYYY-MM-DD.");
    }

    private static AssetDto MapToDto(Asset asset) => new()
    {
        Id = asset.Id,
        AssetTag = asset.AssetTag,
        Manufacturer = asset.Manufacturer,
        Model = asset.Model,
        ModelYear = asset.ModelYear,
        SerialNumber = asset.SerialNumber,
        DeviceType = asset.DeviceType,
        PurchasePrice = asset.PurchasePrice,
        Currency = asset.Currency,
        ExchangeRate = asset.ExchangeRate,
        WarrantyProvider = asset.WarrantyProvider,
        WarrantyStartDate = asset.WarrantyStartDate,
        WarrantyEndDate = asset.WarrantyEndDate,
        Status = asset.Status,
        Name = asset.Name,
        Location = asset.Location,
        PurchaseDate = asset.PurchaseDate,
        Notes = asset.Notes,
        CreatedAt = asset.CreatedAt,
        UpdatedAt = asset.UpdatedAt
    };

    private sealed class ImportRowException(string message) : Exception(message);
}
