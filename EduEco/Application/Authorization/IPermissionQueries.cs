namespace EduEco.Application.Authorization;

public interface IPermissionQueries
{
    /// <summary>
    /// Permission names granted to the user in the tenant: tenant membership roles plus global roles
    /// (e.g. PlatformAdmin assigned through Identity user roles).
    /// </summary>
    Task<IReadOnlyList<string>> GetPermissionNamesAsync(long userId, long tenantId, CancellationToken cancellationToken = default);
}
