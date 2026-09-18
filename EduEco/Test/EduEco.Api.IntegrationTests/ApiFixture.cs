using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EduEco.Api.Controllers;
using EduEco.Application.Abstractions.Persistence;
using EduEco.Application.Tenants;
using EduEco.Core.Authorization;
using EduEco.Core.Identity;
using EduEco.Core.Tenants;
using EduEco.Database.Migrator;
using EduEco.Database.Seeders;
using EduEco.Identity.Controllers;
using EduEco.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenIddict.Abstractions;
using Testcontainers.MsSql;

namespace EduEco.Api.IntegrationTests;

/// <summary>
/// End-to-end environment: SQL Server container (DbUp schema) + in-memory EduEco.Identity (certificate credentials)
/// + in-memory EduEco.Api whose JwtBearer discovers keys from that Identity server. The signing certificate is also
/// available to tests to mint deliberately malformed tokens.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public const string Issuer = "https://identity.test/";
    public const string Password = "Integration!Test9Pw";
    public const string WebClientId = "it-web";
    public const string ReadOnlyWebClientId = "it-web-readonly";
    public const string WebClientSecret = "it-web-secret-0123456789abcdefghij";
    public const string TenantServiceClientId = "it-svc-alpha";
    public const string PlatformServiceClientId = "it-svc-platform";
    public const string ServiceClientSecret = "it-svc-secret-0123456789abcdefghij";
    public const string RedirectUri = "https://client.test/callback";

    private const string CertificatePassword = "it-cert-password";

    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private readonly Testcontainers.Redis.RedisContainer _redis = new Testcontainers.Redis.RedisBuilder("redis:7-alpine").Build();
    private readonly string _certificateDirectory = Path.Combine(Path.GetTempPath(), "eduEco-api-it-" + Guid.NewGuid().ToString("N"));

    private IdentityFactory? _identity;
    private ApiFactory? _api;
    private ApiFactory? _apiB;

    public WebApplicationFactory<AuthorizationController> Identity => _identity ?? throw new InvalidOperationException("Not initialised.");

    public WebApplicationFactory<MeController> Api => _api ?? throw new InvalidOperationException("Not initialised.");

    /// <summary>Second API instance sharing Redis with <see cref="Api"/> (scale-out behaviour).</summary>
    public WebApplicationFactory<MeController> ApiB => _apiB ?? throw new InvalidOperationException("Not initialised.");

    public X509Certificate2 SigningCertificate { get; private set; } = null!;

    public TenantSummary TenantAlpha { get; private set; } = null!;

    public TenantSummary TenantBeta { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_container.StartAsync(), _redis.StartAsync());
        var connectionString = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = "EduEcoApiTests",
            TrustServerCertificate = true,
        }.ConnectionString;

        if (!new DbUpRunner(Options.Create(new DatabaseOptions { ConnectionString = connectionString }), new TestHostEnvironment(), NullLogger<DbUpRunner>.Instance)
                .Run(new MigratorCommand { EnsureDatabase = true }))
        {
            throw new InvalidOperationException("Database migration failed.");
        }

        Directory.CreateDirectory(_certificateDirectory);
        var signingPath = WriteCertificate("signing.pfx", "CN=IT Signing", X509KeyUsageFlags.DigitalSignature);
        var encryptionPath = WriteCertificate("encryption.pfx", "CN=IT Encryption", X509KeyUsageFlags.KeyEncipherment);
        SigningCertificate = X509CertificateLoader.LoadPkcs12FromFile(signingPath, CertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
        var apiClientPath = WriteCertificate("api-client.pfx", "CN=IT API Client", X509KeyUsageFlags.DigitalSignature);

        _identity = new IdentityFactory(connectionString, signingPath, encryptionPath);
        await SeedAsync(_identity.Services, apiClientPath);

        var redis = _redis.GetConnectionString();
        _api = new ApiFactory(connectionString, _identity, redis, apiClientPath);
        _apiB = new ApiFactory(connectionString, _identity, redis, apiClientPath);
    }

    public async ValueTask DisposeAsync()
    {
        if (_api is not null)
        {
            await _api.DisposeAsync();
        }

        if (_apiB is not null)
        {
            await _apiB.DisposeAsync();
        }

        await _redis.DisposeAsync();

        if (_identity is not null)
        {
            await _identity.DisposeAsync();
        }

        SigningCertificate?.Dispose();
        await _container.DisposeAsync();

        try
        {
            Directory.Delete(_certificateDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of temporary test certificates.
        }
    }

    public HttpClient CreateApiClient(string? accessToken = null, bool instanceB = false)
    {
        var client = (instanceB ? ApiB : Api).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://api.test/") });
        if (accessToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        }

        return client;
    }

    public OidcClient CreateOidcClient() =>
        new(Identity.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri(Issuer),
        }));

    public async Task<TenantSummary> CreateTenantAsync(string label)
    {
        await using var scope = Identity.Services.CreateAsyncScope();
        var tenant = new Tenant { Code = $"{label}-{Guid.NewGuid():N}"[..30], Name = $"{label} School" };
        await scope.ServiceProvider.GetRequiredService<ICommandRepository<Tenant>>().InsertAsync(tenant);
        return new TenantSummary(tenant.Id, tenant.Code, tenant.Name);
    }

    public async Task SetTenantActiveAsync(long tenantId, bool active)
    {
        await using var scope = Identity.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ICommandRepository<Tenant>>();
        var tenant = await repository.GetByIdAsync(tenantId) ?? throw new InvalidOperationException("Tenant not found.");
        tenant.IsActive = active;
        await repository.UpdateAsync(tenant);
    }

    public async Task<ApplicationUser> CreateUserAsync(string label, IEnumerable<(TenantSummary Tenant, string Role)>? memberships = null, params string[] globalRoles)
    {
        await using var scope = Identity.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var email = $"{label}-{Guid.NewGuid():N}@it.local";
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = label };
        Ensure(await users.CreateAsync(user, Password));
        foreach (var role in globalRoles)
        {
            Ensure(await users.AddToRoleAsync(user, role));
        }

        var repository = scope.ServiceProvider.GetRequiredService<ICommandRepository<UserTenantMembership>>();
        foreach (var (tenant, role) in memberships ?? [])
        {
            await repository.InsertAsync(new UserTenantMembership { TenantId = tenant.Id, UserId = user.Id, RoleId = Roles.All.Single(r => r.Name == role).Id });
        }

        return user;
    }

    /// <summary>Real user access token via authorization code + PKCE against the in-memory Identity server.</summary>
    public async Task<string> GetUserTokenAsync(ApplicationUser user, string? tenantCode = null, string clientId = WebClientId, string scope = "openid api.read api.write")
    {
        var oidc = CreateOidcClient();
        var tokens = await oidc.SignInAsync(user.Email!, Password, clientId, WebClientSecret, scope, tenantCode);
        return tokens.AccessToken;
    }

    public async Task<string> GetServiceTokenAsync(string clientId, string scope)
    {
        var tokens = await CreateOidcClient().ClientCredentialsAsync(clientId, ServiceClientSecret, scope);
        return tokens.AccessToken;
    }

    private async Task SeedAsync(IServiceProvider services, string apiClientCertificatePath)
    {
        TenantAlpha = await CreateTenantAsync("alpha");
        TenantBeta = await CreateTenantAsync("beta");

        var seed = new SeedOptions();
        foreach (var (clientId, scopes) in new[] { (WebClientId, new[] { "openid", Scopes.ApiRead, Scopes.ApiWrite }), (ReadOnlyWebClientId, new[] { "openid", Scopes.ApiRead }) })
        {
            seed.Clients[clientId] = new ClientSeed
            {
                ClientType = "confidential",
                ClientSecret = WebClientSecret,
                GrantTypes = { "authorization_code" },
                Scopes = { },
                RedirectUris = { RedirectUri },
            };
            seed.Clients[clientId].Scopes.AddRange(scopes);
        }

        seed.Clients[TenantServiceClientId] = new ClientSeed
        {
            ClientType = "confidential",
            ClientSecret = ServiceClientSecret,
            GrantTypes = { "client_credentials" },
            Scopes = { Scopes.ApiRead, Scopes.ApiWrite },
            TenantCode = TenantAlpha.Code,
        };
        seed.Clients[PlatformServiceClientId] = new ClientSeed
        {
            ClientType = "confidential",
            ClientSecret = ServiceClientSecret,
            GrantTypes = { "client_credentials" },
            Scopes = { Scopes.ApiRead },
        };

        // Resource server client: private_key_jwt, introspection only.
        seed.Clients[Resources.Api] = new ClientSeed
        {
            ClientType = "confidential",
            PublicKeyCertificatePath = apiClientCertificatePath,
            PublicKeyCertificatePassword = CertificatePassword,
            AllowIntrospection = true,
        };

        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        await new OpenIddictClientSeeder(
                provider.GetRequiredService<IOpenIddictApplicationManager>(),
                provider.GetRequiredService<IOpenIddictScopeManager>(),
                provider.GetRequiredService<ITenantQueries>(),
                Options.Create(seed),
                NullLogger<OpenIddictClientSeeder>.Instance)
            .SeedAsync(CancellationToken.None);
    }

    private string WriteCertificate(string fileName, string subject, X509KeyUsageFlags usage)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        var path = Path.Combine(_certificateDirectory, fileName);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, CertificatePassword));
        return path;
    }

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
        }
    }

    private sealed class IdentityFactory(string connectionString, string signingPath, string encryptionPath) : WebApplicationFactory<AuthorizationController>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("IntegrationTest");
            builder.UseSetting("ConnectionStrings:EduEco", connectionString);
            builder.UseSetting("IdentityServer:Issuer", Issuer);
            builder.UseSetting("IdentityServer:CredentialMode", "Certificate");
            builder.UseSetting("IdentityServer:SigningCertificates:0:Path", signingPath);
            builder.UseSetting("IdentityServer:SigningCertificates:0:Password", CertificatePassword);
            builder.UseSetting("IdentityServer:EncryptionCertificates:0:Path", encryptionPath);
            builder.UseSetting("IdentityServer:EncryptionCertificates:0:Password", CertificatePassword);
            builder.UseSetting("IdentityServer:TokenPruningEnabled", "false");
            builder.UseSetting("IdentityServer:RateLimits:TokenPermitsPerMinute", "100000");
            builder.UseSetting("IdentityServer:RateLimits:LoginPermitsPerMinute", "100000");
        }
    }

    private sealed class ApiFactory(string connectionString, IdentityFactory identity, string redis, string apiClientCertificatePath)
        : WebApplicationFactory<MeController>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("IntegrationTest");
            builder.UseSetting("ConnectionStrings:EduEco", connectionString);
            builder.UseSetting("Authentication:Authority", Issuer);
            builder.UseSetting("Authentication:PermitsPerMinute", "100000");
            builder.UseSetting("AuthorizationCache:PermissionCacheDuration", "00:05:00");
            builder.UseSetting("AuthorizationCache:TenantStatusCacheDuration", "00:00:00");
            builder.UseSetting("ConnectionStrings:Redis", redis);
            builder.UseSetting("Authentication:Introspection:Enabled", "true");
            builder.UseSetting("Authentication:Introspection:CertificatePath", apiClientCertificatePath);
            builder.UseSetting("Authentication:Introspection:CertificatePassword", CertificatePassword);

            builder.ConfigureTestServices(services =>
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    // Route discovery/JWKS requests to the in-memory Identity server.
                    options.Backchannel = new HttpClient(identity.Server.CreateHandler());
                    options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        new Uri(new Uri(Issuer), ".well-known/openid-configuration").ToString(),
                        new OpenIdConnectConfigurationRetriever(),
                        new HttpDocumentRetriever(options.Backchannel) { RequireHttps = true });
                }));
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "IntegrationTest";

        public string ApplicationName { get; set; } = "EduEco.Api.IntegrationTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}

internal static class InvariantExtensions
{
    public static string Invariant(this long value) => value.ToString(CultureInfo.InvariantCulture);
}
