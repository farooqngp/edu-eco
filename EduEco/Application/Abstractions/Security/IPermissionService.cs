namespace EduEco.Application.Abstractions.Security;

/// <summary>Effective permissions of a user within a tenant (cached; see <see cref="IPermissionCacheInvalidator"/>).</summary>
public interface IPermissionService
{
    Task<bool> HasPermissionAsync(long userId, long tenantId, string permission, CancellationToken cancellationToken = default);

    Task<IReadOnlySet<string>> GetPermissionsAsync(long userId, long tenantId, CancellationToken cancellationToken = default);

    /// <summary>Resolves a <c>TenantScoped: false</c> permission from global roles alone, for callers with no tenant.</summary>
    Task<bool> HasGlobalPermissionAsync(long userId, string permission, CancellationToken cancellationToken = default);

    /// <summary>Global-role permissions only; the tenant-less counterpart of <see cref="GetPermissionsAsync"/>.</summary>
    Task<IReadOnlySet<string>> GetGlobalPermissionsAsync(long userId, CancellationToken cancellationToken = default);
}

/// <summary>Evicts cached authorization data after role/membership/tenant changes.</summary>
public interface IPermissionCacheInvalidator
{
    Task InvalidateUserAsync(long userId, CancellationToken cancellationToken = default);

    Task InvalidateTenantAsync(long tenantId, CancellationToken cancellationToken = default);
}

/// <summary>Cached tenant activity check used on every tenant-scoped request.</summary>
public interface ITenantStatusService
{
    Task<bool> IsActiveAsync(long tenantId, CancellationToken cancellationToken = default);
}
