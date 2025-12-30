using System.Security.Claims;

namespace IAMS.Api.Services;

public interface ITenantProvider
{
    Guid? GetCurrentTenantId();
    Guid GetRequiredTenantId();
    bool IsSuperAdmin();
    void SetTenantId(Guid tenantId);
    void ClearTenantOverride();
}

public class TenantProvider : ITenantProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private Guid? _overrideTenantId;

    public TenantProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? GetCurrentTenantId()
    {
        // Check for manual override (used in background services)
        if (_overrideTenantId.HasValue)
            return _overrideTenantId.Value;

        // Extract from JWT claims
        var tenantClaim = _httpContextAccessor.HttpContext?.User
            .FindFirst("tenant_id")?.Value;

        return Guid.TryParse(tenantClaim, out var tenantId) ? tenantId : null;
    }

    public Guid GetRequiredTenantId()
    {
        return GetCurrentTenantId()
            ?? throw new UnauthorizedAccessException("Tenant context is required for this operation");
    }

    public bool IsSuperAdmin()
    {
        return _httpContextAccessor.HttpContext?.User
            .IsInRole("SuperAdmin") ?? false;
    }

    public void SetTenantId(Guid tenantId)
    {
        _overrideTenantId = tenantId;
    }

    public void ClearTenantOverride()
    {
        _overrideTenantId = null;
    }
}
