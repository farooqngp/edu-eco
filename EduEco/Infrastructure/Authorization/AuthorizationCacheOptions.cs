namespace EduEco.Infrastructure.Authorization;

public sealed class AuthorizationCacheOptions
{
    public const string SectionName = "AuthorizationCache";

    /// <summary>
    /// Upper bound for permission staleness across instances (in-process invalidation is immediate).
    /// <see cref="TimeSpan.Zero"/> disables caching.
    /// </summary>
    public TimeSpan PermissionCacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Upper bound for a deactivated tenant to lose API access. <see cref="TimeSpan.Zero"/> disables caching.</summary>
    public TimeSpan TenantStatusCacheDuration { get; set; } = TimeSpan.FromMinutes(1);
}
