using EduEco.Application.Abstractions.Security;
using EduEco.Application.Memberships;
using EduEco.Infrastructure.Authorization;
using EduEco.Infrastructure.Queries;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EduEco.ServiceRegistry;

public static class AuthorizationExtensions
{
    /// <summary>Cached permission/tenant-status resolution and tenant administration use cases for resource servers.</summary>
    public static IServiceCollection AddEduEcoAuthorizationServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AuthorizationCacheOptions>().Bind(configuration.GetSection(AuthorizationCacheOptions.SectionName));
        services.AddHybridCache();
        services.AddEduEcoDistributedCache(configuration); // in-process or Redis (replay cache, invalidation bus)

        services.TryAddScoped<PermissionService>();
        services.TryAddScoped<IPermissionService>(sp => sp.GetRequiredService<PermissionService>());
        services.TryAddScoped<IPermissionCacheInvalidator>(sp => sp.GetRequiredService<PermissionService>());
        services.TryAddScoped<ITenantStatusService>(sp => sp.GetRequiredService<PermissionService>());

        services.TryAddScoped<IMembershipQueries, MembershipQueries>();
        services.TryAddScoped<MembershipService>();

        return services;
    }
}
