using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EduEco.Application.Abstractions.Persistence;
using EduEco.Application.Tenants;
using EduEco.Core.Authorization;
using EduEco.Core.Identity;
using EduEco.Core.Tenants;
using EduEco.Database.Migrator;
using EduEco.Database.Seeders;
using EduEco.Identity.Controllers;
using EduEco.Identity.Logout;
using EduEco.Infrastructure.Persistence;
using EduEco.Infrastructure.Security;
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
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using Testcontainers.MsSql;

namespace EduEco.Identity.IntegrationTests;

/// <summary>
/// Real authorization server (in-memory TestServer) over a real SQL Server container whose schema is built by the
/// production DbUp migrator. Seeds two tenants and clients covering secrets, private_key_jwt, PAR, DPoP, token exchange,
/// introspection and back-channel logout. Outgoing emails and back-channel logout calls are captured.
/// </summary>
public sealed class IdentityServerFixture : IAsyncLifetime
{
    public const string Issuer = "https://localhost/";
    public const string Password = "Integration!Test9Pw";
    public const string WebClientId = "it-web";
    public const string WebClientSecret = "it-web-secret-0123456789abcdefghij";
    public const string TenantServiceClientId = "it-svc";
    public const string PlatformServiceClientId = "it-svc-platform";
    public const string ServiceClientSecret = "it-svc-secret-0123456789abcdefghij";
    public const string KeyServiceClientId = "it-svc-jwt";
    public const string ParClientId = "it-web-par";
    public const string MobileClientId = "it-mobile";
    public const string ResourceClientId = Resources.Api; // resource server doubling as token-exchange/introspection client
    public const string RedirectUri = "https://client.test/callback";
    public const string PostLogoutRedirectUri = "https://client.test/signed-out";
    public const string BackchannelLogoutUri = "https://client.test/backchannel-logout";
    public const string MobileRedirectUri = "com.eduEco.test:/callback";

    private const string CertificatePassword = "it-cert-password";

    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private readonly string _certificateDirectory = Path.Combine(Path.GetTempPath(), "eduEco-identity-it-" + Guid.NewGuid().ToString("N"));

    private IdentityServerFactory? _factory;

    public WebApplicationFactory<AuthorizationController> Factory => _factory ?? throw new InvalidOperationException("Fixture not initialised.");

    public TenantSummary TenantAlpha { get; private set; } = null!;

    public TenantSummary TenantBeta { get; private set; } = null!;

    /// <summary>Private key of <see cref="KeyServiceClientId"/> and <see cref="ResourceClientId"/> (private_key_jwt).</summary>
    public SigningCredentials ClientSigningCredentials { get; private set; } = null!;

    public CapturingEmailSender Emails { get; } = new();

    public RecordingHandler BackchannelLogouts { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var connectionString = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = "EduEcoIdentityTests",
            TrustServerCertificate = true,
        }.ConnectionString;

        var migrated = new DbUpRunner(
                Options.Create(new DatabaseOptions { ConnectionString = connectionString }),
                new TestHostEnvironment(),
                NullLogger<DbUpRunner>.Instance)
            .Run(new MigratorCommand { EnsureDatabase = true });
        if (!migrated)
        {
            throw new InvalidOperationException("Database migration failed.");
        }

        Directory.CreateDirectory(_certificateDirectory);
        var clientCertificatePath = WriteClientCertificate();
        ClientSigningCredentials = KeyMaterial.ToSigningCredentials(KeyMaterial.LoadPkcs12(clientCertificatePath, CertificatePassword));

        _factory = new IdentityServerFactory(connectionString, Emails, BackchannelLogouts);
        await SeedAsync(_factory.Services, clientCertificatePath);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
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

    public HttpClient CreateClient() =>
        Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri(Issuer),
        });

    /// <summary>Signed client assertion for <paramref name="clientId"/> (private_key_jwt).</summary>
    public string CreateClientAssertion(string clientId, SigningCredentials? credentials = null, string? audience = null) =>
        ClientAssertion.Create(clientId, audience ?? Issuer, credentials ?? ClientSigningCredentials, DateTimeOffset.UtcNow);

    /// <summary>Creates an active, confirmed user with optional tenant memberships and global roles.</summary>
    public async Task<ApplicationUser> CreateUserAsync(
        string label,
        IEnumerable<(TenantSummary Tenant, string Role)>? memberships = null,
        params string[] globalRoles)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
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
            await repository.InsertAsync(new UserTenantMembership
            {
                TenantId = tenant.Id,
                UserId = user.Id,
                RoleId = Roles.All.Single(r => r.Name == role).Id,
            });
        }

        return user;
    }

    public async Task<ApplicationUser?> FindUserByEmailAsync(string email)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email);
    }

    public async Task AddMembershipAsync(long userId, TenantSummary tenant, string role)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICommandRepository<UserTenantMembership>>().InsertAsync(new UserTenantMembership
        {
            TenantId = tenant.Id,
            UserId = userId,
            RoleId = Roles.All.Single(r => r.Name == role).Id,
        });
    }

    public async Task UpdateUserAsync(long userId, Action<ApplicationUser> update)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ?? throw new InvalidOperationException("User not found.");
        update(user);
        Ensure(await users.UpdateAsync(user));
    }

    private async Task SeedAsync(IServiceProvider services, string clientCertificatePath)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;

        var tenants = provider.GetRequiredService<ICommandRepository<Tenant>>();
        var alpha = new Tenant { Code = "it-alpha", Name = "Alpha School" };
        var beta = new Tenant { Code = "it-beta", Name = "Beta School" };
        await tenants.InsertRangeAsync([alpha, beta]);
        TenantAlpha = new TenantSummary(alpha.Id, alpha.Code, alpha.Name);
        TenantBeta = new TenantSummary(beta.Id, beta.Code, beta.Name);

        var seedOptions = new SeedOptions();
        seedOptions.Clients[WebClientId] = new ClientSeed
        {
            ClientType = "confidential",
            ApplicationType = "web",
            ClientSecret = WebClientSecret,
            GrantTypes = { "authorization_code", "refresh_token" },
            Scopes = { "openid", "profile", "email", "offline_access", Scopes.ApiRead, Scopes.ApiWrite },
            RedirectUris = { RedirectUri },
            PostLogoutRedirectUris = { PostLogoutRedirectUri },
            BackchannelLogoutUri = BackchannelLogoutUri,
        };
        seedOptions.Clients[ParClientId] = new ClientSeed
        {
            ClientType = "confidential",
            ApplicationType = "web",
            ClientSecret = WebClientSecret,
            GrantTypes = { "authorization_code" },
            Scopes = { "openid", Scopes.ApiRead },
            RedirectUris = { RedirectUri },
            RequirePushedAuthorizationRequests = true,
        };
        seedOptions.Clients[MobileClientId] = new ClientSeed
        {
            ClientType = "public",
            ApplicationType = "native",
            GrantTypes = { "authorization_code", "refresh_token" },
            Scopes = { "openid", "offline_access", Scopes.ApiRead },
            RedirectUris = { MobileRedirectUri },
            RequireDPoP = true,
        };
        seedOptions.Clients[TenantServiceClientId] = new ClientSeed
        {
            ClientType = "confidential",
            ClientSecret = ServiceClientSecret,
            GrantTypes = { "client_credentials" },
            Scopes = { Scopes.ApiRead, Scopes.ApiSync },
            TenantCode = alpha.Code,
        };
        seedOptions.Clients[PlatformServiceClientId] = new ClientSeed
        {
            ClientType = "confidential",
            ClientSecret = ServiceClientSecret,
            GrantTypes = { "client_credentials" },
            Scopes = { Scopes.ApiSync },
        };
        seedOptions.Clients[KeyServiceClientId] = new ClientSeed
        {
            ClientType = "confidential",
            PublicKeyCertificatePath = clientCertificatePath,
            PublicKeyCertificatePassword = CertificatePassword,
            GrantTypes = { "client_credentials" },
            Scopes = { Scopes.ApiRead },
        };
        seedOptions.Clients[ResourceClientId] = new ClientSeed
        {
            ClientType = "confidential",
            PublicKeyCertificatePath = clientCertificatePath,
            PublicKeyCertificatePassword = CertificatePassword,
            GrantTypes = { OpenIddictConstants.GrantTypes.TokenExchange },
            Scopes = { Scopes.ReportingRead },
            Resources = { Resources.Reporting },
            AllowIntrospection = true,
        };

        await new OpenIddictClientSeeder(
                provider.GetRequiredService<IOpenIddictApplicationManager>(),
                provider.GetRequiredService<IOpenIddictScopeManager>(),
                provider.GetRequiredService<ITenantQueries>(),
                Options.Create(seedOptions),
                NullLogger<OpenIddictClientSeeder>.Instance)
            .SeedAsync(CancellationToken.None);
    }

    private string WriteClientCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=IT Client", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        var path = Path.Combine(_certificateDirectory, "client.pfx");
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

    private sealed class IdentityServerFactory(string connectionString, CapturingEmailSender emails, RecordingHandler backchannel)
        : WebApplicationFactory<AuthorizationController>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("IntegrationTest");
            builder.UseSetting("ConnectionStrings:EduEco", connectionString);
            builder.UseSetting("IdentityServer:Issuer", Issuer);
            builder.UseSetting("IdentityServer:CredentialMode", "Ephemeral");
            builder.UseSetting("IdentityServer:TokenPruningEnabled", "false");
            builder.UseSetting("IdentityServer:ReauthenticationWindow", "00:00:03");
            builder.UseSetting("IdentityServer:AllowSelfRegistration", "true");
            builder.UseSetting("IdentityServer:RateLimits:TokenPermitsPerMinute", "100000");
            builder.UseSetting("IdentityServer:RateLimits:LoginPermitsPerMinute", "100000");

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IEmailSender<ApplicationUser>>(emails);
                services.AddHttpClient(BackchannelLogoutWorker.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => backchannel)
                    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
            });
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "IntegrationTest";

        public string ApplicationName { get; set; } = "EduEco.Identity.IntegrationTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

/// <summary>In-memory outbox replacing SMTP.</summary>
public sealed class CapturingEmailSender : IEmailSender<ApplicationUser>
{
    public ConcurrentQueue<(string To, string Kind, string Link)> Sent { get; } = new();

    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink)
    {
        Sent.Enqueue((email, "confirm", confirmationLink));
        return Task.CompletedTask;
    }

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink)
    {
        Sent.Enqueue((email, "reset", resetLink));
        return Task.CompletedTask;
    }

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) => Task.CompletedTask;

    public string? LastLinkFor(string email, string kind) =>
        Sent.Where(m => m.To == email && m.Kind == kind).Select(m => m.Link).LastOrDefault();
}

/// <summary>Records outgoing back-channel logout requests and answers 200.</summary>
public sealed class RecordingHandler : HttpMessageHandler
{
    public ConcurrentQueue<(Uri Uri, string Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue((request.RequestUri!, body));
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
    }
}

[CollectionDefinition(Name)]
public sealed class IdentityServerCollection : ICollectionFixture<IdentityServerFixture>
{
    public const string Name = "identity-server";
}
