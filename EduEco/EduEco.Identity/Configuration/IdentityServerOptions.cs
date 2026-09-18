namespace EduEco.Identity.Configuration;

public enum CredentialMode
{
    /// <summary>X.509 certificates from PFX files (Docker volume) or Azure Key Vault. Required in Production.</summary>
    Certificate,

    /// <summary>OpenIddict development certificates stored in the current user's certificate store.</summary>
    Development,

    /// <summary>In-memory keys regenerated on every start (automated tests only).</summary>
    Ephemeral,
}

public sealed class CertificateOptions
{
    /// <summary>PKCS#12 file (local Docker: mounted volume).</summary>
    public string Path { get; set; } = string.Empty;

    public string? Password { get; set; }

    /// <summary>Key Vault certificate name (requires <c>KeyVault:Uri</c>); takes precedence over <see cref="Path"/>.</summary>
    public string? KeyVaultName { get; set; }
}

public sealed class RateLimitOptions
{
    public int TokenPermitsPerMinute { get; set; } = 60;

    public int LoginPermitsPerMinute { get; set; } = 10;
}

public sealed class IdentityServerOptions
{
    public const string SectionName = "IdentityServer";

    /// <summary>Public issuer URI (must match what clients and resource servers see, e.g. behind a reverse proxy).</summary>
    public Uri? Issuer { get; set; }

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan IdentityTokenLifetime { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan AuthorizationCodeLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Sliding refresh token lifetime (renewed on each rotation).</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(14);

    /// <summary>Hard cap from the original sign-in; refresh is refused afterwards regardless of sliding renewals.</summary>
    public TimeSpan RefreshTokenAbsoluteLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Grace window for concurrent refresh retries. Zero = strict one-time use (RFC 9700 reuse detection).</summary>
    public TimeSpan RefreshTokenReuseLeeway { get; set; } = TimeSpan.Zero;

    /// <summary>Interactive session cookie lifetime (absolute, non-sliding so max_age/auth_time stay accurate).</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    public CredentialMode CredentialMode { get; set; } = CredentialMode.Certificate;

    /// <summary>First = active signing key; additional certificates stay published in JWKS during rotation.</summary>
    public List<CertificateOptions> SigningCertificates { get; init; } = [];

    public List<CertificateOptions> EncryptionCertificates { get; init; } = [];

    /// <summary>Persistent Data Protection key ring (cookies, anti-forgery, 2FA tokens). Required outside Development.</summary>
    public string? DataProtectionKeysPath { get; set; }

    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// Enables the self-service <c>/Account/Register</c> page. New accounts need email confirmation and a tenant membership
    /// granted by an administrator before they can obtain tokens. Off by default (invitation/admin provisioning).
    /// </summary>
    public bool AllowSelfRegistration { get; set; }

    /// <summary>
    /// Relying-party origins allowed in the CSP <c>form-action</c> directive, in addition to <c>'self'</c>. Browsers apply
    /// <c>form-action</c> to the redirect chain after a form post (login/consent/logout confirmation), so every client
    /// redirect/post-logout origin must be listed, e.g. <c>https://app.example.org</c>.
    /// </summary>
    public List<string> FormActionOrigins { get; init; } = [];

    /// <summary>
    /// Development only: accept self-signed TLS certificates of relying parties when delivering back-channel logout
    /// tokens (local Docker, where containers do not trust the host's ASP.NET dev certificate). Refused elsewhere.
    /// </summary>
    public bool AllowUntrustedBackchannelLogoutCertificates { get; set; }

    /// <summary>
    /// Security-sensitive account changes (2FA, passkeys) require a sign-in no older than this ("sudo mode").
    /// </summary>
    public TimeSpan ReauthenticationWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Reverse proxies allowed to set X-Forwarded-* headers.</summary>
    public List<string> KnownProxies { get; init; } = [];

    public bool TokenPruningEnabled { get; set; } = true;

    public TimeSpan TokenPruningInterval { get; set; } = TimeSpan.FromHours(1);

    public RateLimitOptions RateLimits { get; init; } = new();
}
