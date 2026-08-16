namespace IAMS.Api.Entities;

public static class TicketTypes
{
    public const string Incident = "Incident";
    public const string Request = "Request";
    public const string SecurityEvent = "SecurityEvent";

    public static readonly string[] All = [Incident, Request, SecurityEvent];

    public static bool IsValid(string type) => All.Contains(type);
}

public static class TicketStatus
{
    public const string New = "New";
    public const string Assigned = "Assigned";
    public const string InProgress = "InProgress";
    public const string OnHold = "OnHold";
    public const string Resolved = "Resolved";
    public const string Closed = "Closed";
    public const string Cancelled = "Cancelled";

    public static readonly string[] All =
        [New, Assigned, InProgress, OnHold, Resolved, Closed, Cancelled];

    public static readonly string[] Open =
        [New, Assigned, InProgress, OnHold];

    public static bool IsValid(string status) => All.Contains(status);
}

public static class TicketPriority
{
    public const string Low = "Low";
    public const string Medium = "Medium";
    public const string High = "High";
    public const string Critical = "Critical";

    public static readonly string[] All = [Low, Medium, High, Critical];

    public static bool IsValid(string priority) => All.Contains(priority);
}

/// <summary>
/// The single source of truth for how a ticket may move between statuses.
/// Closed and Cancelled are terminal.
/// </summary>
public static class TicketWorkflow
{
    private static readonly Dictionary<string, string[]> Transitions = new()
    {
        [TicketStatus.New] = [TicketStatus.Assigned, TicketStatus.Cancelled],
        [TicketStatus.Assigned] = [TicketStatus.InProgress, TicketStatus.OnHold, TicketStatus.Cancelled],
        [TicketStatus.InProgress] = [TicketStatus.OnHold, TicketStatus.Resolved, TicketStatus.Cancelled],
        [TicketStatus.OnHold] = [TicketStatus.InProgress, TicketStatus.Cancelled],
        [TicketStatus.Resolved] = [TicketStatus.Closed, TicketStatus.InProgress],
        [TicketStatus.Closed] = [],
        [TicketStatus.Cancelled] = []
    };

    public static bool CanTransition(string from, string to) =>
        Transitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public static bool IsOpen(string status) => TicketStatus.Open.Contains(status);
}
