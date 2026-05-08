using System.Globalization;
using IAMS.Shared.DTOs;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace IAMS.Api.Services;

public interface IPdfReportService
{
    byte[] BuildInventoryPdf(List<AssetInventoryReportRow> data, string? deviceType, string? status);
    byte[] BuildAssignedByUserPdf(List<AssignedAssetsByUserReportRow> data, string? userName);
    byte[] BuildWarrantyExpiryPdf(List<WarrantyExpiryReportRow> data, string? warrantyStatus, int? daysThreshold);
    byte[] BuildAssetValuePdf(AssetValueSummaryDto summary);
}

public class PdfReportService : IPdfReportService
{
    private const float HeaderFontSize = 16;
    private const float TableFontSize = 8.5f;

    public byte[] BuildInventoryPdf(List<AssetInventoryReportRow> data, string? deviceType, string? status)
    {
        var filters = BuildFilterLine(("Device Type", deviceType), ("Status", status));

        return BuildDocument("Asset Inventory Report", filters, data.Count, content =>
        {
            content.Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(2);  // Asset Tag
                    c.RelativeColumn(2);  // Device Type
                    c.RelativeColumn(2);  // Manufacturer
                    c.RelativeColumn(2);  // Model
                    c.RelativeColumn(2);  // Serial
                    c.RelativeColumn(1.5f); // Status
                    c.RelativeColumn(2);  // Assigned To
                    c.RelativeColumn(2);  // Value
                });

                AddHeaderRow(table, "Asset Tag", "Type", "Manufacturer", "Model", "Serial", "Status", "Assigned To", "Value");

                foreach (var row in data)
                {
                    AddBodyCell(table, row.AssetTag);
                    AddBodyCell(table, row.DeviceType);
                    AddBodyCell(table, row.Manufacturer ?? "—");
                    AddBodyCell(table, row.Model ?? "—");
                    AddBodyCell(table, row.SerialNumber ?? "—");
                    AddBodyCell(table, row.Status);
                    AddBodyCell(table, row.AssignedTo ?? "—");
                    AddBodyCell(table, FormatCurrency(row.PurchasePrice, row.Currency));
                }
            });
        });
    }

    public byte[] BuildAssignedByUserPdf(List<AssignedAssetsByUserReportRow> data, string? userName)
    {
        var filters = BuildFilterLine(("User", userName));

        return BuildDocument("Assigned Assets by User", filters, data.Count, content =>
        {
            content.Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(2.5f); // User
                    c.RelativeColumn(2);    // Department
                    c.RelativeColumn(2);    // Asset Tag
                    c.RelativeColumn(1.5f); // Type
                    c.RelativeColumn(3);    // Device
                    c.RelativeColumn(2);    // Assigned Date
                    c.RelativeColumn(2);    // Value
                });

                AddHeaderRow(table, "User", "Department", "Asset Tag", "Type", "Device", "Assigned", "Value");

                foreach (var row in data)
                {
                    AddBodyCell(table, row.UserName);
                    AddBodyCell(table, row.Department ?? "—");
                    AddBodyCell(table, row.AssetTag);
                    AddBodyCell(table, row.DeviceType);
                    AddBodyCell(table, $"{row.Manufacturer ?? ""} {row.Model ?? ""}".Trim());
                    AddBodyCell(table, row.AssignedDate?.ToString("yyyy-MM-dd") ?? "—");
                    AddBodyCell(table, FormatCurrency(row.PurchasePrice, row.Currency));
                }
            });
        });
    }

    public byte[] BuildWarrantyExpiryPdf(List<WarrantyExpiryReportRow> data, string? warrantyStatus, int? daysThreshold)
    {
        var filters = BuildFilterLine(
            ("Status", warrantyStatus),
            ("Within Days", daysThreshold?.ToString()));

        return BuildDocument("Warranty Expiry Report", filters, data.Count, content =>
        {
            content.Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(2);    // Asset Tag
                    c.RelativeColumn(1.5f); // Type
                    c.RelativeColumn(3);    // Device
                    c.RelativeColumn(2);    // Provider
                    c.RelativeColumn(2);    // Expires
                    c.RelativeColumn(1);    // Days
                    c.RelativeColumn(1.5f); // Status
                    c.RelativeColumn(2);    // Assigned
                });

                AddHeaderRow(table, "Asset Tag", "Type", "Device", "Provider", "Expires", "Days", "Status", "Assigned To");

                foreach (var row in data)
                {
                    AddBodyCell(table, row.AssetTag);
                    AddBodyCell(table, row.DeviceType);
                    AddBodyCell(table, $"{row.Manufacturer ?? ""} {row.Model ?? ""}".Trim());
                    AddBodyCell(table, row.WarrantyProvider ?? "—");
                    AddBodyCell(table, row.WarrantyEndDate.ToString("yyyy-MM-dd"));
                    AddBodyCell(table, row.DaysRemaining.ToString(CultureInfo.InvariantCulture));
                    AddBodyCell(table, row.WarrantyStatus);
                    AddBodyCell(table, row.AssignedTo ?? "—");
                }
            });
        });
    }

    public byte[] BuildAssetValuePdf(AssetValueSummaryDto summary)
    {
        return BuildDocument("Asset Value Report", null, summary.TotalAssetCount, content =>
        {
            content.Column(col =>
            {
                col.Spacing(16);

                col.Item().Row(row =>
                {
                    row.Spacing(12);
                    row.RelativeItem().Element(c => SummaryCard(c, "Grand Total", FormatCurrency(summary.GrandTotalValue, summary.PrimaryCurrency)));
                    row.RelativeItem().Element(c => SummaryCard(c, "Total Assets", summary.TotalAssetCount.ToString(CultureInfo.InvariantCulture)));
                    row.RelativeItem().Element(c => SummaryCard(c, "Average", FormatCurrency(summary.AverageAssetValue, summary.PrimaryCurrency)));
                });

                col.Item().Text("By Device Type").FontSize(12).SemiBold();
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(3);
                        c.RelativeColumn(1.5f);
                        c.RelativeColumn(2);
                        c.RelativeColumn(2);
                    });

                    AddHeaderRow(table, "Device Type", "Count", "Total Value", "Average");

                    foreach (var row in summary.ByDeviceType)
                    {
                        AddBodyCell(table, row.DeviceType);
                        AddBodyCell(table, row.AssetCount.ToString(CultureInfo.InvariantCulture));
                        AddBodyCell(table, FormatCurrency(row.TotalValue, row.Currency));
                        AddBodyCell(table, FormatCurrency(row.AverageValue, row.Currency));
                    }
                });

                if (summary.ByStatus.Count > 0)
                {
                    col.Item().Text("By Status").FontSize(12).SemiBold();
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.RelativeColumn(3);
                            c.RelativeColumn(1.5f);
                            c.RelativeColumn(2);
                        });

                        AddHeaderRow(table, "Status", "Count", "Total Value");

                        foreach (var row in summary.ByStatus)
                        {
                            AddBodyCell(table, row.Status);
                            AddBodyCell(table, row.AssetCount.ToString(CultureInfo.InvariantCulture));
                            AddBodyCell(table, FormatCurrency(row.TotalValue, summary.PrimaryCurrency));
                        }
                    });
                }
            });
        });
    }

    private static byte[] BuildDocument(string title, string? filters, int rowCount, Action<IContainer> contentBuilder)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(1.5f, Unit.Centimetre);
                page.DefaultTextStyle(t => t.FontSize(9).FontFamily(Fonts.Calibri));

                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Text(title).FontSize(HeaderFontSize).Bold();
                        row.ConstantItem(180).AlignRight().Text(t =>
                        {
                            t.AlignRight();
                            t.Span("IAMS — IT Asset Management").FontSize(8).FontColor(Colors.Grey.Darken1);
                            t.EmptyLine();
                            t.Span($"Generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC").FontSize(8).FontColor(Colors.Grey.Darken1);
                        });
                    });

                    if (!string.IsNullOrWhiteSpace(filters))
                        col.Item().PaddingTop(2).Text(filters).FontSize(8.5f).FontColor(Colors.Grey.Darken2);

                    col.Item().PaddingTop(2).Text($"{rowCount} record(s)").FontSize(8.5f).FontColor(Colors.Grey.Darken2);
                    col.Item().PaddingTop(6).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
                });

                page.Content().PaddingVertical(8).Element(contentBuilder);

                page.Footer().AlignRight().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(8).FontColor(Colors.Grey.Darken1));
                    t.Span("Page ");
                    t.CurrentPageNumber();
                    t.Span(" of ");
                    t.TotalPages();
                });
            });
        });

        return doc.GeneratePdf();
    }

    private static void AddHeaderRow(TableDescriptor table, params string[] headers)
    {
        table.Header(header =>
        {
            foreach (var h in headers)
            {
                header.Cell()
                    .Background(Colors.Grey.Lighten3)
                    .BorderBottom(0.75f).BorderColor(Colors.Grey.Darken1)
                    .Padding(4)
                    .Text(h).FontSize(TableFontSize).SemiBold();
            }
        });
    }

    private static void AddBodyCell(TableDescriptor table, string value)
    {
        table.Cell()
            .BorderBottom(0.25f).BorderColor(Colors.Grey.Lighten2)
            .Padding(4)
            .Text(value).FontSize(TableFontSize);
    }

    private static void SummaryCard(IContainer container, string label, string value)
    {
        container
            .Background(Colors.Grey.Lighten4)
            .Border(0.5f).BorderColor(Colors.Grey.Lighten2)
            .Padding(10)
            .Column(col =>
            {
                col.Item().Text(label).FontSize(8.5f).FontColor(Colors.Grey.Darken2);
                col.Item().PaddingTop(2).Text(value).FontSize(14).Bold();
            });
    }

    private static string BuildFilterLine(params (string Label, string? Value)[] filters)
    {
        var parts = filters
            .Where(f => !string.IsNullOrWhiteSpace(f.Value))
            .Select(f => $"{f.Label}: {f.Value}")
            .ToList();

        return parts.Count == 0 ? string.Empty : "Filters — " + string.Join("  •  ", parts);
    }

    private static string FormatCurrency(decimal? value, string? currency)
    {
        if (!value.HasValue) return "—";
        var symbol = currency switch
        {
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            "PHP" => "₱",
            null or "" => "",
            _ => currency + " "
        };
        return $"{symbol}{value.Value:N2}";
    }
}
