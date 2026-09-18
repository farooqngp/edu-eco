using EduEco.Application.Abstractions.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EduEco.ServiceRegistry;

public static class SecurityExtensions
{
    /// <summary>
    /// Trusted system identity + cross-tenant context for non-HTTP processes (migrator, background jobs).
    /// Never register in request-serving hosts; they use claim-based implementations.
    /// </summary>
    public static IServiceCollection AddEduEcoSystemContext(this IServiceCollection services, string auditName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditName);

        services.TryAddSingleton<ICurrentUser>(new SystemCurrentUser(auditName));
        services.TryAddSingleton<ITenantContext, SystemTenantContext>();
        return services;
    }

    private sealed class SystemCurrentUser(string auditName) : ICurrentUser
    {
        public long? UserId => null;

        public string? ClientId => null;

        public bool IsAuthenticated => true;

        public bool IsServiceClient => false;

        public string AuditName { get; } = $"system:{auditName}";
    }

    private sealed class SystemTenantContext : ITenantContext
    {
        public long? TenantId => null;

        public bool IsCrossTenant => true;
    }
}
