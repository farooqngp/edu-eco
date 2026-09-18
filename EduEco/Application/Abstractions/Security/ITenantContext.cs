namespace EduEco.Application.Abstractions.Security;

/// <summary>Tenant resolved for the current scope (from the <c>tenant_id</c> claim in HTTP requests).</summary>
public interface ITenantContext
{
    long? TenantId { get; }

    /// <summary>
    /// <c>true</c> only for trusted system processes (migrator, platform jobs) that legitimately work across tenants.
    /// When <c>false</c> and <see cref="TenantId"/> is <c>null</c>, tenant-owned data access is rejected.
    /// </summary>
    bool IsCrossTenant { get; }
}
