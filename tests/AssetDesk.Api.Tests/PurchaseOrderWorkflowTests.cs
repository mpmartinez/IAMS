using AssetDesk.Api.Entities;

namespace AssetDesk.Api.Tests;

/// <summary>
/// The single source of truth for how an order may move. Shaped after TicketWorkflow, which is
/// the house pattern for this - a table plus a predicate, tested exhaustively rather than by
/// example, because an illegal transition nobody pinned is how a Received order gets reopened.
/// </summary>
public class PurchaseOrderWorkflowTests
{
    [Theory]
    [InlineData(PurchaseOrderStatus.Draft, PurchaseOrderStatus.Ordered)]
    [InlineData(PurchaseOrderStatus.Draft, PurchaseOrderStatus.Cancelled)]
    [InlineData(PurchaseOrderStatus.Ordered, PurchaseOrderStatus.PartiallyReceived)]
    [InlineData(PurchaseOrderStatus.Ordered, PurchaseOrderStatus.Received)]
    [InlineData(PurchaseOrderStatus.Ordered, PurchaseOrderStatus.Cancelled)]
    [InlineData(PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.Received)]
    [InlineData(PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.Cancelled)]
    public void Legal_transitions_are_allowed(string from, string to)
    {
        Assert.True(PurchaseOrderWorkflow.CanTransition(from, to));
    }

    [Theory]
    [InlineData(PurchaseOrderStatus.Draft, PurchaseOrderStatus.Received)]
    [InlineData(PurchaseOrderStatus.Draft, PurchaseOrderStatus.PartiallyReceived)]
    [InlineData(PurchaseOrderStatus.Received, PurchaseOrderStatus.Ordered)]
    [InlineData(PurchaseOrderStatus.Received, PurchaseOrderStatus.Cancelled)]
    [InlineData(PurchaseOrderStatus.Cancelled, PurchaseOrderStatus.Ordered)]
    [InlineData(PurchaseOrderStatus.Cancelled, PurchaseOrderStatus.Draft)]
    [InlineData(PurchaseOrderStatus.Ordered, PurchaseOrderStatus.Draft)]
    [InlineData(PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.Draft)]
    [InlineData(PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.Ordered)]
    public void Illegal_transitions_are_refused(string from, string to)
    {
        Assert.False(PurchaseOrderWorkflow.CanTransition(from, to));
    }

    [Fact]
    public void Received_and_Cancelled_are_terminal()
    {
        foreach (var to in PurchaseOrderStatus.All)
        {
            Assert.False(PurchaseOrderWorkflow.CanTransition(PurchaseOrderStatus.Received, to));
            Assert.False(PurchaseOrderWorkflow.CanTransition(PurchaseOrderStatus.Cancelled, to));
        }
    }

    [Theory]
    [InlineData(10, 0, PurchaseOrderStatus.Ordered, PurchaseOrderStatus.Ordered)]
    [InlineData(10, 4, PurchaseOrderStatus.Ordered, PurchaseOrderStatus.PartiallyReceived)]
    [InlineData(10, 10, PurchaseOrderStatus.Ordered, PurchaseOrderStatus.Received)]
    [InlineData(10, 10, PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.Received)]
    [InlineData(10, 4, PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.PartiallyReceived)]
    public void The_status_follows_the_quantities(
        int ordered, int received, string current, string expected)
    {
        Assert.Equal(expected, PurchaseOrderWorkflow.StatusFor(ordered, received, current));
    }

    [Fact]
    public void A_cancelled_order_is_not_dragged_forward_by_its_quantities()
    {
        // Receiving is refused against a cancelled order, so StatusFor should never be asked -
        // but if it is, it must not resurrect it.
        Assert.Equal(
            PurchaseOrderStatus.Cancelled,
            PurchaseOrderWorkflow.StatusFor(10, 10, PurchaseOrderStatus.Cancelled));
    }

    [Fact]
    public void A_received_order_is_not_dragged_backward_by_its_quantities()
    {
        // Receiving is refused against a received order, so StatusFor should never be asked -
        // but if it is, it must not reopen it.
        Assert.Equal(
            PurchaseOrderStatus.Received,
            PurchaseOrderWorkflow.StatusFor(10, 0, PurchaseOrderStatus.Received));
    }

    [Fact]
    public void Only_Ordered_and_PartiallyReceived_are_open_for_receiving()
    {
        Assert.True(PurchaseOrderWorkflow.IsOpen(PurchaseOrderStatus.Ordered));
        Assert.True(PurchaseOrderWorkflow.IsOpen(PurchaseOrderStatus.PartiallyReceived));
        Assert.False(PurchaseOrderWorkflow.IsOpen(PurchaseOrderStatus.Draft));
        Assert.False(PurchaseOrderWorkflow.IsOpen(PurchaseOrderStatus.Received));
        Assert.False(PurchaseOrderWorkflow.IsOpen(PurchaseOrderStatus.Cancelled));
    }
}
