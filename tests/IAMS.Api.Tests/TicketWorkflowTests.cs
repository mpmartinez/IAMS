using IAMS.Api.Entities;

namespace IAMS.Api.Tests;

public class TicketWorkflowTests
{
    [Theory]
    [InlineData(TicketStatus.New, TicketStatus.Assigned)]
    [InlineData(TicketStatus.New, TicketStatus.Cancelled)]
    [InlineData(TicketStatus.Assigned, TicketStatus.InProgress)]
    [InlineData(TicketStatus.Assigned, TicketStatus.OnHold)]
    [InlineData(TicketStatus.InProgress, TicketStatus.OnHold)]
    [InlineData(TicketStatus.InProgress, TicketStatus.Resolved)]
    [InlineData(TicketStatus.OnHold, TicketStatus.InProgress)]
    [InlineData(TicketStatus.Resolved, TicketStatus.Closed)]
    [InlineData(TicketStatus.Resolved, TicketStatus.InProgress)]
    public void Allows_valid_transitions(string from, string to)
    {
        Assert.True(TicketWorkflow.CanTransition(from, to));
    }

    [Theory]
    [InlineData(TicketStatus.New, TicketStatus.Resolved)]
    [InlineData(TicketStatus.New, TicketStatus.Closed)]
    [InlineData(TicketStatus.Closed, TicketStatus.InProgress)]
    [InlineData(TicketStatus.Closed, TicketStatus.New)]
    [InlineData(TicketStatus.Cancelled, TicketStatus.InProgress)]
    [InlineData(TicketStatus.OnHold, TicketStatus.Resolved)]
    public void Rejects_invalid_transitions(string from, string to)
    {
        Assert.False(TicketWorkflow.CanTransition(from, to));
    }

    [Fact]
    public void Rejects_unknown_status_values()
    {
        Assert.False(TicketWorkflow.CanTransition("Banana", TicketStatus.Closed));
        Assert.False(TicketWorkflow.CanTransition(TicketStatus.New, "Banana"));
    }

    [Fact]
    public void Open_statuses_are_the_four_working_states()
    {
        Assert.True(TicketWorkflow.IsOpen(TicketStatus.New));
        Assert.True(TicketWorkflow.IsOpen(TicketStatus.Assigned));
        Assert.True(TicketWorkflow.IsOpen(TicketStatus.InProgress));
        Assert.True(TicketWorkflow.IsOpen(TicketStatus.OnHold));
        Assert.False(TicketWorkflow.IsOpen(TicketStatus.Resolved));
        Assert.False(TicketWorkflow.IsOpen(TicketStatus.Closed));
        Assert.False(TicketWorkflow.IsOpen(TicketStatus.Cancelled));
    }

    [Fact]
    public void Validators_accept_known_values_and_reject_others()
    {
        Assert.True(TicketTypes.IsValid(TicketTypes.SecurityEvent));
        Assert.False(TicketTypes.IsValid("Escalation"));
        Assert.True(TicketStatus.IsValid(TicketStatus.OnHold));
        Assert.False(TicketStatus.IsValid("Paused"));
        Assert.True(TicketPriority.IsValid(TicketPriority.Critical));
        Assert.False(TicketPriority.IsValid("Urgent"));
        Assert.True(TicketCategory.IsValid(TicketCategory.Software));
        Assert.True(TicketCategory.IsValid(TicketCategory.Access));
        Assert.False(TicketCategory.IsValid("Furniture"));
    }
}
