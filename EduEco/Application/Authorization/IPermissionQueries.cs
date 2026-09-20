namespace EduEco.Application.Authorization;

public interface IPermissionQueries
{
    /// <summary>
    /// Permission names granted to the user in the tenant: tenant membership roles plus global roles
    /// (e.g. PlatformAdmin assigned through Identity user roles).
    /// </summary>
    Task<IReadOnlyList<string>> GetPermissionNamesAsync(long userId, long tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Permission names granted to the user by global roles alone (no tenant membership). Used for permissions
    /// declared with <c>TenantScoped: false</c>, which a caller can exercise without acting inside a tenant.
    /// </summary>
    Task<IReadOnlyList<string>> GetGlobalPermissionNamesAsync(long userId, CancellationToken cancellationToken = default);
}
