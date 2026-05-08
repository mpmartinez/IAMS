using ClosedXML.Excel;
using IAMS.Api.Data;
using IAMS.Api.Entities;
using IAMS.Shared.DTOs;
using Microsoft.EntityFrameworkCore;

namespace IAMS.Api.Services;

public interface IAssetImportService
{
    Task<ImportAssetsResultDto> ImportAsync(Stream xlsxStream, CancellationToken ct = default);
}

public class AssetImportService(AppDbContext db, ILogger<AssetImportService> logger) : IAssetImportService
{
    private static readonly string[] ExpectedHeaders =
    [
        "Name", "DeviceType", "Status", "Manufacturer", "Model", "ModelYear",
        "SerialNumber", "PurchasePrice", "Currency", "PurchaseDate",
        "WarrantyProvider", "WarrantyStartDate", "WarrantyEndDate", "Location", "Notes"
    ];

    private static readonly string[] ValidStatuses =
        [AssetStatus.Available, AssetStatus.InUse, AssetStatus.Maintenance, AssetStatus.Retired, AssetStatus.Lost];

    public async Task<ImportAssetsResultDto> ImportAsync(Stream xlsxStream, CancellationToken ct = default)
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
                var asset = await BuildAssetAsync(row, headerMap, tagSequenceCache, ct);
                toCreate.Add(asset);
            }
            catch (ImportRowException ex)
            {
                errors.Add(new ImportRowError { RowNumber = rowNum, Message = ex.Message });
            }
        }

        if (toCreate.Count > 0)
        {
            try
            {
                db.Assets.AddRange(toCreate);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
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
        IXLRow row,
        Dictionary<string, int> headerMap,
        Dictionary<string, int> tagSequenceCache,
        CancellationToken ct)
    {
        var deviceType = ReadString(row, headerMap, "DeviceType");
        if (string.IsNullOrWhiteSpace(deviceType))
            throw new ImportRowException("DeviceType is required.");
        if (!DeviceTypes.All.Contains(deviceType))
            throw new ImportRowException($"Invalid DeviceType '{deviceType}'. Allowed: {string.Join(", ", DeviceTypes.All)}.");

        var status = ReadString(row, headerMap, "Status");
        if (string.IsNullOrWhiteSpace(status))
            throw new ImportRowException("Status is required.");
        if (!ValidStatuses.Contains(status))
            throw new ImportRowException($"Invalid Status '{status}'. Allowed: {string.Join(", ", ValidStatuses)}.");

        var currency = ReadString(row, headerMap, "Currency");
        if (string.IsNullOrWhiteSpace(currency))
            currency = "USD";
        if (!Currencies.All.Contains(currency))
            throw new ImportRowException($"Invalid Currency '{currency}'. Allowed: {string.Join(", ", Currencies.All)}.");

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

        var assetTag = await GenerateAssetTagAsync(deviceType, tagSequenceCache, ct);

        return new Asset
        {
            AssetTag = assetTag,
            DeviceType = deviceType,
            Status = status,
            Currency = currency,
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

    private async Task<string> GenerateAssetTagAsync(
        string deviceType,
        Dictionary<string, int> sequenceCache,
        CancellationToken ct)
    {
        var prefix = deviceType switch
        {
            DeviceTypes.Laptop => "LAP",
            DeviceTypes.Desktop => "DSK",
            DeviceTypes.Monitor => "MON",
            DeviceTypes.Phone => "PHN",
            DeviceTypes.Tablet => "TAB",
            DeviceTypes.Printer => "PRN",
            DeviceTypes.Network => "NET",
            DeviceTypes.Server => "SVR",
            DeviceTypes.Peripheral => "PER",
            DeviceTypes.Software => "SFT",
            _ => "OTH"
        };

        var datePart = DateTime.UtcNow.ToString("yyyyMMdd");
        var baseTag = $"{prefix}-{datePart}-";

        if (!sequenceCache.TryGetValue(baseTag, out var current))
        {
            var todayTags = await db.Assets
                .Where(a => a.AssetTag.StartsWith(baseTag))
                .Select(a => a.AssetTag)
                .ToListAsync(ct);

            current = 0;
            foreach (var tag in todayTags)
            {
                if (int.TryParse(tag.Replace(baseTag, ""), out var seq) && seq > current)
                    current = seq;
            }
        }

        current++;
        sequenceCache[baseTag] = current;
        return $"{baseTag}{current:D4}";
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
