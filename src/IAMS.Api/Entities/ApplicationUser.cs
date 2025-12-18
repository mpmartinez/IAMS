using Microsoft.AspNetCore.Identity;

namespace IAMS.Api.Entities;

public class ApplicationUser : IdentityUser
{
    public required string FullName { get; set; }
    public string? Department { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<Asset> AssignedAssets { get; set; } = [];
}
