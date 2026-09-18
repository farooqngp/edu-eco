using EduEco.Application.Abstractions.Persistence;
using EduEco.Application.Authorization;
using EduEco.Application.Tenants;
using EduEco.Infrastructure.Persistence;
using EduEco.Infrastructure.Persistence.Dapper;
using EduEco.Infrastructure.Queries;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EduEco.ServiceRegistry;

public static class PersistenceExtensions
{
    /// <summary>Dapper data access: connection factory, unit of work, generic command repository, query executor, query services.</summary>
    public static IServiceCollection AddEduEcoPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection("Database"))
            .Configure(o =>
            {
                o.ConnectionString = configuration.GetConnectionString(DatabaseOptions.ConnectionStringName) ?? o.ConnectionString;
                o.ReadConnectionString = configuration.GetConnectionString(DatabaseOptions.ReadConnectionStringName) ?? o.ReadConnectionString;
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        DapperConfiguration.Configure();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDbConnectionFactory, SqlConnectionFactory>();

        services.TryAddScoped<DapperSession>();
        services.TryAddScoped<IUnitOfWork>(sp => sp.GetRequiredService<DapperSession>());
        services.TryAddScoped<IDbSession>(sp => sp.GetRequiredService<DapperSession>());

        services.TryAddScoped(typeof(ICommandRepository<>), typeof(DapperCommandRepository<>));
        services.TryAddSingleton<IQueryExecutor, DapperQueryExecutor>();

        services.TryAddScoped<IPermissionQueries, PermissionQueries>();
        services.TryAddScoped<ITenantQueries, TenantQueries>();

        return services;
    }
}
