using AssetDesk.Api.Services;

namespace AssetDesk.Api.Tests;

public class FakeTenantProvider : ITenantProvider
{
    private Guid? _tenantId;
    private readonly bool _isSuperAdmin;

    public FakeTenantProvider(Guid? tenantId, bool isSuperAdmin = false)
    {
        _tenantId = tenantId;
        _isSuperAdmin = isSuperAdmin;
    }

    public Guid? GetCurrentTenantId() => _tenantId;

    public Guid GetRequiredTenantId() =>
        _tenantId ?? throw new UnauthorizedAccessException("Tenant context is required for this operation");

    public bool IsSuperAdmin() => _isSuperAdmin;

    public void SetTenantId(Guid tenantId) => _tenantId = tenantId;

    public void ClearTenantOverride() => _tenantId = null;
}
