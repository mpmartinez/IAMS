using Microsoft.AspNetCore.Identity;

namespace AssetDesk.Api.Entities;

public class ApplicationUser : IdentityUser
{
    public required string FullName { get; set; }
    public string? Department { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    // Multi-tenancy
    public Guid TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    // Super admin can access all tenants
    public bool IsSuperAdmin { get; set; }

    // Tenant-level admin (can manage users within their tenant)
    public bool IsTenantAdmin { get; set; }

    // Set when an administrator sends a password reset link, cleared when the user completes
    // the reset. While set, password login is refused outright rather than merely nagged about:
    // an admin resetting someone's password is usually responding to a lost or compromised
    // credential, so the old one has to stop working immediately.
    public bool MustChangePassword { get; set; }

    public ICollection<Asset> AssignedAssets { get; set; } = [];
}
