using IAMS.Api.Entities;
using IAMS.Api.Mapping;

namespace IAMS.Api.Tests;

public class TicketMappingTests
{
    [Fact]
    public void Reference_is_zero_padded_to_four_digits()
    {
        var ticket = new Ticket { Id = 1, TicketNumber = 183, Title = "Printer jams" };
        Assert.Equal("TKT-0183", ticket.ToListItem().Reference);
    }

    [Fact]
    public void Detail_mapping_carries_asset_context()
    {
        var ticket = new Ticket
        {
            Id = 5,
            TicketNumber = 183,
            Title = "Printer jams",
            AssetId = 41,
            Asset = new Asset
            {
                Id = 41,
                AssetTag = "IAMS-0241",
                DeviceType = DeviceTypes.Printer,
                Status = AssetStatus.Maintenance,
                Manufacturer = "HP",
                Model = "LaserJet M404dn",
                WarrantyEndDate = new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc)
            }
        };

        var dto = ticket.ToDto(includeInternalComments: false);

        Assert.Equal("IAMS-0241", dto.AssetTag);
        Assert.Equal("HP LaserJet M404dn", dto.AssetName);
        Assert.Equal(AssetStatus.Maintenance, dto.AssetStatus);
        Assert.Equal(2026, dto.WarrantyEndDate!.Value.Year);
    }

    [Fact]
    public void Internal_comments_are_dropped_when_not_permitted()
    {
        var ticket = new Ticket
        {
            Id = 5, TicketNumber = 1, Title = "Printer jams",
            Comments =
            [
                new TicketComment { Id = 1, Body = "Looking at it", IsInternal = false },
                new TicketComment { Id = 2, Body = "Third jam this quarter", IsInternal = true }
            ]
        };

        var hidden = ticket.ToDto(includeInternalComments: false);
        var shown = ticket.ToDto(includeInternalComments: true);

        Assert.Single(hidden.Comments);
        Assert.Equal("Looking at it", hidden.Comments[0].Body);
        Assert.Equal(2, shown.Comments.Count);
    }
}
