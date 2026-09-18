namespace EduEco.Bff.Configuration;

public sealed class BffOptions
{
    public const string SectionName = "Bff";

    /// <summary>EduEco.Identity issuer.</summary>
    public Uri? Authority { get; set; }

    /// <summary>Confidential OAuth client registered for this BFF.</summary>
    public string ClientId { get; set; } = "eduEco-bff";

    /// <summary>Client secret (development only; production uses <see cref="ClientAssertion"/>).</summary>
    public string? ClientSecret { get; set; }

    /// <summary><c>private_key_jwt</c>: PKCS#12 whose public key is registered for the client at the Identity server.</summary>
    public ClientAssertionOptions ClientAssertion { get; init; } = new();

    /// <summary>RFC 9126: push authorization parameters over the back channel (never exposed in the browser URL).</summary>
    public bool UsePushedAuthorization { get; set; } = true;

    public List<string> Scopes { get; init; } = ["openid", "profile", "email", "offline_access", "api.read", "api.write"];

    /// <summary>EduEco.Api base address; <c>/api/**</c> is forwarded here with the user's access token.</summary>
    public Uri? ApiBaseAddress { get; set; }

    /// <summary>Absolute browser session lifetime (non-sliding).</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Access tokens expiring within this window are refreshed before forwarding.</summary>
    public TimeSpan AccessTokenRefreshThreshold { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan SessionCleanupInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Shared Data Protection key ring (session encryption, OIDC state). Production: this or <c>DataProtection:BlobUri</c>.</summary>
    public string? DataProtectionKeysPath { get; set; }

    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Development-only: trust self-signed certificates of local Identity/API containers.</summary>
    public bool AllowUntrustedCertificates { get; set; }
}

public sealed class ClientAssertionOptions
{
    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }

    /// <summary>Key Vault certificate name (requires <c>KeyVault:Uri</c>); takes precedence over <see cref="CertificatePath"/>.</summary>
    public string? KeyVaultName { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(CertificatePath) || !string.IsNullOrWhiteSpace(KeyVaultName);
}
