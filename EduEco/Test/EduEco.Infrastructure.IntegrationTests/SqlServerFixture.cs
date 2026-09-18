using EduEco.Application.Abstractions.Security;
using EduEco.Database.Migrator;
using EduEco.Database.Seeders;
using EduEco.Infrastructure.Persistence;
using EduEco.ServiceRegistry;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Testcontainers.MsSql;

namespace EduEco.Infrastructure.IntegrationTests;

/// <summary>
/// One SQL Server container per test run; schema built exclusively by the DbUp migrator (same path as production).
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string BffSecret = "integration-test-secret-0123456789abcdef";

    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    private ServiceProvider? _services;

    public string ConnectionString { get; private set; } = string.Empty;

    public IServiceProvider Services => _services ?? throw new InvalidOperationException("Fixture not initialised.");

    public bool FirstMigrationSucceeded { get; private set; }

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        ConnectionString = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = "EduEcoTests",
            TrustServerCertificate = true,
        }.ConnectionString;

        _services = BuildServices(ConnectionString);

        FirstMigrationSucceeded = Services.GetRequiredService<DbUpRunner>()
            .Run(new MigratorCommand { EnsureDatabase = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    private static ServiceProvider BuildServices(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{DatabaseOptions.ConnectionStringName}"] = connectionString,
                ["Seed:Clients:it-confidential:ClientType"] = "confidential",
                ["Seed:Clients:it-confidential:ClientSecret"] = BffSecret,
                ["Seed:Clients:it-confidential:GrantTypes:0"] = "client_credentials",
                ["Seed:Clients:it-confidential:Scopes:0"] = "api.read",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddEduEcoPersistence(configuration);
        services.AddEduEcoAuthStores(configuration);
        services.AddEduEcoSystemContext("integration-tests");
        services.AddOptions<SeedOptions>().Bind(configuration.GetSection(SeedOptions.SectionName));
        services.AddSingleton<DbUpRunner>();
        services.AddScoped<OpenIddictClientSeeder>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    /// <summary>Non-Development environment: 99_Dev scripts must not run.</summary>
    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "IntegrationTest";

        public string ApplicationName { get; set; } = "EduEco.Infrastructure.IntegrationTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sql-server";
}

/// <summary>Configurable tenant context for exercising tenant isolation.</summary>
internal sealed class TestTenantContext(long? tenantId, bool isCrossTenant = false) : ITenantContext
{
    public long? TenantId { get; } = tenantId;

    public bool IsCrossTenant { get; } = isCrossTenant;
}

internal sealed class TestCurrentUser : ICurrentUser
{
    public long? UserId => null;

    public string? ClientId => "integration-tests";

    public bool IsAuthenticated => true;

    public bool IsServiceClient => true;

    public string AuditName => "test:integration";
}
