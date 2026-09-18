using EduEco.Core.Authorization;

namespace EduEco.Api.Configuration;

public sealed class ApiSecurityOptions
{
    public const string SectionName = "Authentication";

    /// <summary>Token issuer (EduEco.Identity). Signing keys are discovered from its JWKS and refreshed on rotation.</summary>
    public Uri? Authority { get; set; }

    /// <summary>Optional internal discovery URL when the issuer's public URL is not reachable from this host.</summary>
    public Uri? MetadataAddress { get; set; }

    public string Audience { get; set; } = Resources.Api;

    public bool RequireHttpsMetadata { get; set; } = true;

    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Development-only: accept the self-signed certificate of a local Identity container.</summary>
    public bool AllowUntrustedBackchannelCertificate { get; set; }

    /// <summary>OAuth client used by the interactive API reference (Development only).</summary>
    public string ApiDocsClientId { get; set; } = "eduEco-api-docs";

    /// <summary>Per client (client_id, else IP) request budget.</summary>
    public int PermitsPerMinute { get; set; } = 600;

    public IntrospectionOptions Introspection { get; init; } = new();
}

/// <summary>
/// RFC 7662 introspection for high-risk operations (<c>[RequireActiveToken]</c>): catches tokens revoked before expiry
/// (logout, password reset, admin revocation). The API authenticates as a confidential client with private_key_jwt.
/// </summary>
public sealed class IntrospectionOptions
{
    /// <summary>When disabled, <c>[RequireActiveToken]</c> relies on signature/expiry only (JWT validation).</summary>
    public bool Enabled { get; set; }

    public string ClientId { get; set; } = Resources.Api;

    /// <summary>PKCS#12 with the private key registered (public part) for the API client.</summary>
    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }

    /// <summary>Key Vault certificate name (requires <c>KeyVault:Uri</c>); takes precedence over <see cref="CertificatePath"/>.</summary>
    public string? CertificateKeyVaultName { get; set; }

    /// <summary>Short cache bounds the revocation delay and the load on the authorization server.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(30);
}
