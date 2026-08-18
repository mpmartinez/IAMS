namespace AssetDesk.Api.Entities;

/// <summary>
/// One permission granted to one role within one tenant.
///
/// Deliberately NOT an ITenantEntity and deliberately without a global query filter. TokenService
/// resolves permissions during login, before the request is authenticated, so ITenantProvider
/// reports no tenant at that moment. EF evaluates the filter's provider call eagerly into a query
/// parameter, so the null guard used by the other filters does not short-circuit, and a filtered
/// read would silently return zero rows and hand every user an empty permission set.
/// Every read of this table filters TenantId explicitly instead.
/// </summary>
public class RolePermission
{
    public Guid Id { get; set; }

    public string RoleId { get; set; } = string.Empty;

    public Guid TenantId { get; set; }

    public string Permission { get; set; } = string.Empty;

    public ApplicationRole? Role { get; set; }
    public Tenant? Tenant { get; set; }
}
