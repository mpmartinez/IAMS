using AssetDesk.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AssetDesk.Api.Services;

public interface IPermissionResolver
{
    Task<IReadOnlyList<string>> GetPermissionsAsync(
        IEnumerable<string> roleNames, Guid tenantId, CancellationToken ct = default);
}

public class PermissionResolver(AppDbContext db) : IPermissionResolver
{
    public async Task<IReadOnlyList<string>> GetPermissionsAsync(
        IEnumerable<string> roleNames, Guid tenantId, CancellationToken ct = default)
    {
        var names = roleNames as IList<string> ?? roleNames.ToList();
        if (names.Count == 0) return [];

        // TenantId is filtered explicitly here. RolePermission has no global query filter - see
        // the comment on the entity for why a filter would return nothing during login.
        return await db.RolePermissions
            .Where(rp => rp.TenantId == tenantId)
            .Join(db.Roles.Where(r => r.Name != null && names.Contains(r.Name)),
                rp => rp.RoleId,
                r => r.Id,
                (rp, r) => rp.Permission)
            .Distinct()
            .ToListAsync(ct);
    }
}
