using IAMS.Api.Entities;

namespace IAMS.Api.Authorization;

/// <param name="Key">Stable identifier stored in RolePermission and emitted as a claim.</param>
/// <param name="Group">Heading the permission sits under in the /admin/roles matrix.</param>
public sealed record PermissionDescriptor(string Key, string Group, string Label, string Description);

/// <summary>
/// The set of permissions that exist. Deliberately code-owned: a permission is only real if some
/// policy checks it, so a database-backed catalog would drift from the policies the way the Users
/// role dropdown drifted from Roles.TenantAssignable. The database owns the grants, not the catalog.
/// </summary>
public static class Permissions
{
    /// Matches the claim type PermissionView.razor already reads. Do not rename.
    public const string ClaimType = "permission";

    public const string AssetsView = "iams:assets:view";
    public const string AssetsCreate = "iams:assets:create";
    public const string AssetsEdit = "iams:assets:edit";
    public const string AssetsDelete = "iams:assets:delete";
    public const string AssetsImport = "iams:assets:import";
    public const string AssetsDebug = "iams:assets:debug";

    public const string AssignmentsView = "iams:assignments:view";
    public const string AssignmentsAssign = "iams:assignments:assign";
    public const string AssignmentsReturn = "iams:assignments:return";

    public const string TicketsFile = "iams:tickets:file";
    public const string TicketsQueue = "iams:tickets:queue";
    public const string TicketsManage = "iams:tickets:manage";

    public const string ReportsView = "iams:reports:view";

    public const string UsersView = "iams:users:view";
    public const string UsersManage = "iams:users:manage";
    public const string UsersRead = "iams:users:read";

    public const string RolesView = "iams:roles:view";
    public const string RolesManage = "iams:roles:manage";

    public const string AttachmentsManage = "iams:attachments:manage";

    public const string WarrantyManage = "iams:warranty:manage";
    public const string WarrantyDelete = "iams:warranty:delete";

    public const string NotificationsTest = "iams:notifications:test";

    public static readonly PermissionDescriptor[] All =
    [
        new(AssetsView, "Assets", "View assets", "See the asset list and individual asset records."),
        new(AssetsCreate, "Assets", "Create assets", "Add a new asset."),
        new(AssetsEdit, "Assets", "Edit assets", "Change details on an existing asset."),
        new(AssetsDelete, "Assets", "Delete assets", "Permanently remove an asset."),
        new(AssetsImport, "Assets", "Bulk import", "Upload a spreadsheet to create many assets at once."),
        new(AssetsDebug, "Assets", "List all asset tags", "Enumerate every asset tag in the tenant."),

        new(AssignmentsView, "Assignments", "View assignments", "See who holds which asset, and the assignment history."),
        new(AssignmentsAssign, "Assignments", "Assign assets", "Hand an asset to a user."),
        new(AssignmentsReturn, "Assignments", "Return assets", "Record an asset coming back."),

        new(TicketsFile, "Tickets", "File tickets", "Raise a ticket and follow your own."),
        new(TicketsQueue, "Tickets", "View the queue", "See every ticket in the tenant, not just your own."),
        new(TicketsManage, "Tickets", "Work the queue", "Assign, progress, and resolve other people's tickets."),

        new(ReportsView, "Reports", "View reports", "Open the inventory, warranty, and value reports."),

        new(UsersView, "Users", "View users", "Browse the full user list with roles and status."),
        new(UsersManage, "Users", "Manage users", "Create, edit, deactivate users and set their role."),
        new(UsersRead, "Users", "Look up users", "Read the names-only list used to pick an assignee."),

        new(RolesView, "Roles", "View roles", "See the roles in this organisation and what they grant."),
        new(RolesManage, "Roles", "Manage roles", "Create custom roles and change what any role grants."),

        new(AttachmentsManage, "Attachments", "Manage attachments", "Upload and delete files on assets."),

        new(WarrantyManage, "Warranty", "Manage alerts", "Acknowledge and update warranty alerts."),
        new(WarrantyDelete, "Warranty", "Delete alerts", "Remove a warranty alert."),

        new(NotificationsTest, "Notifications", "Send test notification", "Push a test notification, for diagnosing delivery."),
    ];

    public static readonly string[] Keys = All.Select(p => p.Key).ToArray();

    public static bool IsValid(string key) => Keys.Contains(key);

    /// <summary>
    /// The grants a built-in role starts with in every tenant. These reproduce the access the
    /// hard-coded policies gave before this feature existed; see the plan for the derivation.
    /// </summary>
    public static IReadOnlyList<string> DefaultsFor(string roleName) => roleName switch
    {
        // SuperAdmin bypasses authorisation entirely. It holds everything so the matrix shows
        // the truth rather than an empty grid.
        Roles.SuperAdmin => Keys,

        Roles.Admin => Keys,

        Roles.Staff =>
        [
            AssetsView, AssetsCreate, AssetsEdit, AssetsImport,
            AssignmentsView, AssignmentsAssign, AssignmentsReturn,
            TicketsFile, TicketsQueue, TicketsManage,
            UsersRead, AttachmentsManage, WarrantyManage,
        ],

        Roles.Auditor => [AssignmentsView, TicketsFile, ReportsView],

        Roles.Management => [TicketsFile],

        Roles.Employee => [TicketsFile],

        _ => [],
    };
}
