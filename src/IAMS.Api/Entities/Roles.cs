namespace IAMS.Api.Entities;

public static class Roles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Admin = "Admin";
    public const string Management = "Management";
    public const string Staff = "Staff";
    public const string Auditor = "Auditor";

    // Office users who can file and follow their own tickets. Excluded from seat
    // metering (see SubscriptionService) and from asset/queue management.
    public const string Employee = "Employee";

    public static readonly string[] All = [SuperAdmin, Admin, Management, Staff, Auditor, Employee];

    // SuperAdmin is deliberately absent: it satisfies ITenantProvider.IsSuperAdmin(), which
    // short-circuits every global query filter. Only an existing SuperAdmin may grant it.
    public static readonly string[] TenantAssignable = [Admin, Management, Staff, Auditor, Employee];

    public static bool CanAssign(string role, bool actorIsSuperAdmin) =>
        actorIsSuperAdmin ? All.Contains(role) : TenantAssignable.Contains(role);

    public static string AssignableList(bool actorIsSuperAdmin) =>
        string.Join(", ", actorIsSuperAdmin ? All : TenantAssignable);

    public static string DescriptionFor(string role) => role switch
    {
        SuperAdmin => "Platform operator. Bypasses tenant isolation and every permission check.",
        Admin => "Full control of this organisation, including users and roles.",
        Management => "Files and follows tickets. No asset or queue management.",
        Staff => "Runs the asset estate and works the ticket queue.",
        Auditor => "Read-only oversight: reports and assignment history.",
        Employee => "Files and follows their own tickets.",
        _ => ""
    };
}
