using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Dapper;
using EduEco.Api.Controllers;
using EduEco.Application.Abstractions.Persistence;
using EduEco.Application.Tenants;
using EduEco.Bff.Endpoints;
using EduEco.Core.Authorization;
using EduEco.Core.Identity;
using EduEco.Core.Tenants;
using EduEco.Database.Migrator;
using EduEco.Database.Seeders;
using EduEco.Identity.Controllers;
using EduEco.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
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
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenIddict.Abstractions;
using Testcontainers.MsSql;
using Yarp.ReverseProxy.Forwarder;

namespace EduEco.Bff.IntegrationTests;

/// <summary>
/// Full browser-to-API chain in memory: SPA/BFF (https://bff.test) → Identity (https://identity.test) → API (https://api.test),
/// all on one SQL Server container whose schema is built by the production migrator.
/// </summary>
public sealed class BffFixture : IAsyncLifetime
{
    public const string IdentityIssuer = "https://identity.test/";
    public const string BffOrigin = "https://bff.test/";
    public const string ApiOrigin = "https://api.test/";
    public const string BffClientId = "it-bff";
    public const string BackchannelLogoutUri = BffOrigin + "bff/backchannel-logout";
    public const string Password = "Integration!Test9Pw";
    public const string SessionCookieName = "__Host-EduEco.Bff";

    private const string CertificatePassword = "it-cert-password";

    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private readonly string _certificateDirectory = Path.Combine(Path.GetTempPath(), "eduEco-bff-it-" + Guid.NewGuid().ToString("N"));

    private WebApplicationFactory<AuthorizationController>? _identity;
    private WebApplicationFactory<MeController>? _api;
    private WebApplicationFactory<BffUserResponse>? _bff;
    private WebApplicationFactory<BffUserResponse>? _bffB;

    public string ConnectionString { get; private set; } = string.Empty;

    public FakeTimeProvider BffClock { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>Identity server signing certificate (PKCS#12), for minting logout tokens in tests.</summary>
    public string SigningCertificatePath { get; private set; } = string.Empty;

    public static string SigningCertificatePassword => CertificatePassword;

    public TenantSummary TenantAlpha { get; private set; } = null!;

    public TenantSummary TenantBeta { get; private set; } = null!;

    public WebApplicationFactory<AuthorizationController> Identity => _identity ?? throw new InvalidOperationException("Not initialised.");

    public WebApplicationFactory<BffUserResponse> Bff => _bff ?? throw new InvalidOperationException("Not initialised.");

    /// <summary>Second BFF instance: same database, same Data Protection key ring (scale-out).</summary>
    public WebApplicationFactory<BffUserResponse> BffB => _bffB ?? throw new InvalidOperationException("Not initialised.");

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = "EduEcoBffTests",
            TrustServerCertificate = true,
        }.ConnectionString;

        if (!new DbUpRunner(Options.Create(new DatabaseOptions { ConnectionString = ConnectionString }), new TestHostEnvironment(), NullLogger<DbUpRunner>.Instance)
                .Run(new MigratorCommand { EnsureDatabase = true }))
        {
            throw new InvalidOperationException("Database migration failed.");
        }

        Directory.CreateDirectory(_certificateDirectory);
        var signing = WriteCertificate("signing.pfx", "CN=IT Signing", X509KeyUsageFlags.DigitalSignature);
        var encryption = WriteCertificate("encryption.pfx", "CN=IT Encryption", X509KeyUsageFlags.KeyEncipherment);
        var client = WriteCertificate("bff-client.pfx", "CN=IT BFF Client", X509KeyUsageFlags.DigitalSignature);
        var dataProtectionKeys = Directory.CreateDirectory(Path.Combine(_certificateDirectory, "dp-keys")).FullName;
        SigningCertificatePath = signing;

        // Identity delivers back-channel logout tokens to the in-memory BFF (created below).
        _identity = new IdentityFactory(ConnectionString, signing, encryption, new DeferredHandler(() => Bff.Server.CreateHandler()));
        await SeedAsync(client);

        _api = new ApiFactory(ConnectionString, _identity);
        _bff = new BffFactory(ConnectionString, _identity, _api, BffClock, client, dataProtectionKeys);
        _bffB = new BffFactory(ConnectionString, _identity, _api, BffClock, client, dataProtectionKeys);
        _ = _bff.Server; // start hosts
        _ = _bffB.Server;
    }

    public async ValueTask DisposeAsync()
    {
        if (_bff is not null)
        {
            await _bff.DisposeAsync();
        }

        if (_bffB is not null)
        {
            await _bffB.DisposeAsync();
        }

        if (_api is not null)
        {
            await _api.DisposeAsync();
        }

        if (_identity is not null)
        {
            await _identity.DisposeAsync();
        }

        await _container.DisposeAsync();

        try
        {
            Directory.Delete(_certificateDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    public HttpClient CreateBffClient(bool instanceB = false) =>
        (instanceB ? BffB : Bff).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri(BffOrigin) });

    public HttpClient CreateIdentityClient() =>
        Identity.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri(IdentityIssuer) });

    public async Task<ApplicationUser> CreateUserAsync(string label, params (TenantSummary Tenant, string Role)[] memberships)
    {
        await using var scope = Identity.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var email = $"{label}-{Guid.NewGuid():N}@it.local";
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = label };
        var created = await users.CreateAsync(user, Password);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        var repository = scope.ServiceProvider.GetRequiredService<ICommandRepository<UserTenantMembership>>();
        foreach (var (tenant, role) in memberships)
        {
            await repository.InsertAsync(new UserTenantMembership { TenantId = tenant.Id, UserId = user.Id, RoleId = Roles.All.Single(r => r.Name == role).Id });
        }

        return user;
    }

    public async Task<(int Count, DateTimeOffset? AccessTokenExpiresAtUtc)> GetSessionAsync(string sessionId)
    {
        await using var connection = new SqlConnection(ConnectionString);
        var row = await connection.QuerySingleOrDefaultAsync<(int Count, DateTimeOffset? Expires)>(
            "SELECT COUNT(*) AS Count, MAX(AccessTokenExpiresAtUtc) AS Expires FROM bff.Sessions WHERE SessionId = @SessionId;",
            new { SessionId = sessionId });
        return row;
    }

    public async Task<int> CountSessionsAsync(long userId)
    {
        await using var connection = new SqlConnection(ConnectionString);
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM bff.Sessions WHERE Subject = @Subject;",
            new { Subject = userId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    }

    private async Task SeedAsync(string clientCertificatePath)
    {
        await using var scope = Identity.Services.CreateAsyncScope();
        var provider = scope.ServiceProvider;

        var tenants = provider.GetRequiredService<ICommandRepository<Tenant>>();
        var alpha = new Tenant { Code = "it-alpha", Name = "Alpha School" };
        var beta = new Tenant { Code = "it-beta", Name = "Beta School" };
        await tenants.InsertRangeAsync([alpha, beta]);
        TenantAlpha = new TenantSummary(alpha.Id, alpha.Code, alpha.Name);
        TenantBeta = new TenantSummary(beta.Id, beta.Code, beta.Name);

        var seed = new SeedOptions();
        seed.Clients[BffClientId] = new ClientSeed
        {
            ClientType = "confidential",
            ApplicationType = "web",
            PublicKeyCertificatePath = clientCertificatePath,
            PublicKeyCertificatePassword = CertificatePassword,
            RequirePushedAuthorizationRequests = true,
            BackchannelLogoutUri = BackchannelLogoutUri,
            GrantTypes = { "authorization_code", "refresh_token" },
            Scopes = { "openid", "profile", "email", "offline_access", Scopes.ApiRead, Scopes.ApiWrite },
            RedirectUris = { BffOrigin + "signin-oidc" },
            PostLogoutRedirectUris = { BffOrigin + "signout-callback-oidc" },
        };

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

    private sealed class IdentityFactory(string connectionString, string signingPath, string encryptionPath, HttpMessageHandler backchannelLogout)
        : WebApplicationFactory<AuthorizationController>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("IntegrationTest");
            builder.UseSetting("ConnectionStrings:EduEco", connectionString);
            builder.UseSetting("IdentityServer:Issuer", IdentityIssuer);
            builder.UseSetting("IdentityServer:CredentialMode", "Certificate");
            builder.UseSetting("IdentityServer:SigningCertificates:0:Path", signingPath);
            builder.UseSetting("IdentityServer:SigningCertificates:0:Password", CertificatePassword);
            builder.UseSetting("IdentityServer:EncryptionCertificates:0:Path", encryptionPath);
            builder.UseSetting("IdentityServer:EncryptionCertificates:0:Password", CertificatePassword);
            builder.UseSetting("IdentityServer:TokenPruningEnabled", "false");
            builder.UseSetting("IdentityServer:AccessTokenLifetime", "00:05:00");
            builder.UseSetting("IdentityServer:RateLimits:TokenPermitsPerMinute", "100000");
            builder.UseSetting("IdentityServer:RateLimits:LoginPermitsPerMinute", "100000");

            builder.ConfigureTestServices(services =>
                services.AddHttpClient("backchannel-logout")
                    .ConfigurePrimaryHttpMessageHandler(() => backchannelLogout)
                    .SetHandlerLifetime(Timeout.InfiniteTimeSpan));
        }
    }

    private sealed class ApiFactory(string connectionString, WebApplicationFactory<AuthorizationController> identity) : WebApplicationFactory<MeController>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("IntegrationTest");
            builder.UseSetting("ConnectionStrings:EduEco", connectionString);
            builder.UseSetting("Authentication:Authority", IdentityIssuer);
            builder.UseSetting("Authentication:PermitsPerMinute", "100000");
            builder.UseSetting("AuthorizationCache:TenantStatusCacheDuration", "00:00:00");

            builder.ConfigureTestServices(services =>
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.Backchannel = new HttpClient(identity.Server.CreateHandler());
                    options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        new Uri(new Uri(IdentityIssuer), ".well-known/openid-configuration").ToString(),
                        new OpenIdConnectConfigurationRetriever(),
                        new HttpDocumentRetriever(options.Backchannel) { RequireHttps = true });
                }));
        }
    }

    private sealed class BffFactory(
        string connectionString,
        WebApplicationFactory<AuthorizationController> identity,
        WebApplicationFactory<MeController> api,
        FakeTimeProvider clock,
        string clientCertificatePath,
        string dataProtectionKeysPath) : WebApplicationFactory<BffUserResponse>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("IntegrationTest");
            builder.UseSetting("ConnectionStrings:EduEco", connectionString);
            builder.UseSetting("Bff:Authority", IdentityIssuer);
            builder.UseSetting("Bff:ApiBaseAddress", ApiOrigin);
            builder.UseSetting("Bff:ClientId", BffClientId);
            builder.UseSetting("Bff:ClientAssertion:CertificatePath", clientCertificatePath);
            builder.UseSetting("Bff:ClientAssertion:CertificatePassword", CertificatePassword);
            builder.UseSetting("Bff:DataProtectionKeysPath", dataProtectionKeysPath);

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);

                // OIDC discovery/token/revocation over the in-memory Identity server.
                services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
                {
                    options.Backchannel = new HttpClient(identity.Server.CreateHandler());
                    options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        new Uri(new Uri(IdentityIssuer), ".well-known/openid-configuration").ToString(),
                        new OpenIdConnectConfigurationRetriever(),
                        new HttpDocumentRetriever(options.Backchannel) { RequireHttps = true });
                });

                // YARP forwards to the in-memory API.
                services.AddSingleton<IForwarderHttpClientFactory>(new TestForwarderHttpClientFactory(api.Server.CreateHandler()));
            });
        }
    }

    /// <summary>Resolves the target handler on first use (breaks the Identity → BFF construction cycle).</summary>
    private sealed class DeferredHandler(Func<HttpMessageHandler> factory) : HttpMessageHandler
    {
        private HttpMessageInvoker? _invoker;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            (_invoker ??= new HttpMessageInvoker(factory(), disposeHandler: false)).SendAsync(request, cancellationToken);
    }

    private sealed class TestForwarderHttpClientFactory(HttpMessageHandler handler) : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(handler, disposeHandler: false);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "IntegrationTest";

        public string ApplicationName { get; set; } = "EduEco.Bff.IntegrationTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

[CollectionDefinition(Name)]
public sealed class BffCollection : ICollectionFixture<BffFixture>
{
    public const string Name = "bff";
}
