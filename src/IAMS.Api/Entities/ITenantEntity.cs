namespace IAMS.Api.Entities;

/// <summary>
/// Interface for entities that belong to a tenant
/// </summary>
public interface ITenantEntity
{
    Guid TenantId { get; set; }
    Tenant? Tenant { get; set; }
}
