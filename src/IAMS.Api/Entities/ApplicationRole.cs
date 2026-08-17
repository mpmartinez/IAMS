using Microsoft.AspNetCore.Identity;

namespace IAMS.Api.Entities;

public class ApplicationRole : IdentityRole
{
    /// Null for the built-in roles, which are shared by every tenant. Set for custom roles,
    /// which only their own tenant may see or edit.
    public Guid? TenantId { get; set; }

    public bool IsBuiltIn { get; set; }

    public string? Description { get; set; }

    public ApplicationRole() { }

    public ApplicationRole(string roleName) : base(roleName) { }
}
