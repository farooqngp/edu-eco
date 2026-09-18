namespace EduEco.Database.Seeders;

public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    /// <summary>OAuth clients keyed by client_id. Secrets come from environment variables / user-secrets, never appsettings.</summary>
    public Dictionary<string, ClientSeed> Clients { get; init; } = new(StringComparer.Ordinal);

    public DevUsersSeed DevUsers { get; init; } = new();
}

public sealed class ClientSeed
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary><c>confidential</c> or <c>public</c>.</summary>
    public string ClientType { get; set; } = "confidential";

    /// <summary><c>web</c> or <c>native</c>.</summary>
    public string ApplicationType { get; set; } = "web";

    /// <summary>Shared secret. Rejected outside Development/testing: production confidential clients use <see cref="PublicKeyCertificatePath"/>.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Public certificate (.cer/.crt, or .pfx whose public part is used) for <c>private_key_jwt</c> client authentication.
    /// Only the public key is registered (JWKS); the client keeps the private key.
    /// </summary>
    public string? PublicKeyCertificatePath { get; set; }

    public string? PublicKeyCertificatePassword { get; set; }

    /// <summary><c>authorization_code</c>, <c>refresh_token</c>, <c>client_credentials</c>, token exchange URN.</summary>
    public List<string> GrantTypes { get; init; } = [];

    public List<string> Scopes { get; init; } = [];

    /// <summary>Resources (audiences) the client may request explicitly, e.g. as token exchange targets.</summary>
    public List<string> Resources { get; init; } = [];

    /// <summary>
    /// client_credentials clients only: tenant the service acts for (stored as application property <c>tenant_id</c>
    /// and emitted as the <c>tenant_id</c> claim). Omit for platform-level services.
    /// </summary>
    public string? TenantCode { get; set; }

    public List<string> RedirectUris { get; init; } = [];

    public List<string> PostLogoutRedirectUris { get; init; } = [];

    /// <summary>RFC 9126: authorization requests must be pushed (PAR) first.</summary>
    public bool RequirePushedAuthorizationRequests { get; set; }

    /// <summary>RFC 9449: access and refresh tokens must be DPoP-bound (mobile/public clients).</summary>
    public bool RequireDPoP { get; set; }

    /// <summary>OIDC Back-Channel Logout endpoint of the client.</summary>
    public string? BackchannelLogoutUri { get; set; }

    /// <summary>Resource server allowed to call the introspection endpoint (RFC 7662).</summary>
    public bool AllowIntrospection { get; set; }
}

public sealed class DevUsersSeed
{
    /// <summary>Shared password for demo users (Development only; supply via .env / user-secrets).</summary>
    public string? Password { get; set; }
}
