using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EduEco.Infrastructure.Security;

public sealed class DPoPOptions
{
    public const string SectionName = "DPoP";

    /// <summary>Maximum age of a proof (<c>iat</c>) accepted by servers.</summary>
    public TimeSpan ProofLifetime { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Asymmetric algorithms accepted for proofs (never "none" or HMAC).</summary>
    public IReadOnlyList<string> SupportedAlgorithms { get; set; } = ["ES256", "ES384", "PS256", "RS256"];
}

public static class DPoPConstants
{
    public const string HeaderName = "DPoP";
    public const string AuthorizationScheme = "DPoP";
    public const string ProofType = "dpop+jwt";
    public const string ConfirmationClaim = "cnf";
    public const string ThumbprintMember = "jkt";
    public const string InvalidProof = "invalid_dpop_proof";
}

public sealed record DPoPValidationResult(bool IsValid, string? Thumbprint, string? Error)
{
    public static DPoPValidationResult Success(string thumbprint) => new(true, thumbprint, null);

    public static DPoPValidationResult Failure(string error) => new(false, null, error);
}

/// <summary>
/// RFC 9449 DPoP proof validation shared by the authorization server (token endpoint) and resource servers.
/// Checks: explicit type, asymmetric alg, public-only embedded JWK, signature, <c>htm</c>, <c>htu</c>, <c>iat</c> window,
/// <c>ath</c> (resource requests), expected key thumbprint, and one-time <c>jti</c>.
/// </summary>
public sealed class DPoPProofValidator(IReplayCache replayCache, TimeProvider timeProvider, IOptions<DPoPOptions> options)
{
    private static readonly JsonWebTokenHandler Handler = new();

    public async Task<DPoPValidationResult> ValidateAsync(
        string? proof,
        string httpMethod,
        Uri httpUri,
        string? accessToken = null,
        string? expectedThumbprint = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(proof) || proof.Contains(',', StringComparison.Ordinal))
        {
            return DPoPValidationResult.Failure("A single DPoP proof is required.");
        }

        JsonWebToken token;
        try
        {
            token = Handler.ReadJsonWebToken(proof);
        }
        catch (ArgumentException)
        {
            return DPoPValidationResult.Failure("The DPoP proof is malformed.");
        }

        var settings = options.Value;
        if (!string.Equals(token.Typ, DPoPConstants.ProofType, StringComparison.Ordinal))
        {
            return DPoPValidationResult.Failure("The DPoP proof type must be dpop+jwt.");
        }

        if (!settings.SupportedAlgorithms.Contains(token.Alg))
        {
            return DPoPValidationResult.Failure("The DPoP proof algorithm is not supported.");
        }

        if (!token.TryGetHeaderValue<JsonElement>("jwk", out var jwkElement) || jwkElement.ValueKind != JsonValueKind.Object)
        {
            return DPoPValidationResult.Failure("The DPoP proof must embed the public key (jwk).");
        }

        var jwk = new JsonWebKey(jwkElement.GetRawText());
        if (jwk.HasPrivateKey || jwkElement.TryGetProperty("d", out _))
        {
            return DPoPValidationResult.Failure("The DPoP proof key must not contain private parameters.");
        }

        var validation = await Handler.ValidateTokenAsync(proof, new TokenValidationParameters
        {
            IssuerSigningKey = jwk,
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = false,
            RequireExpirationTime = false,
            RequireSignedTokens = true,
            ValidTypes = [DPoPConstants.ProofType],
            ValidAlgorithms = settings.SupportedAlgorithms,
            TryAllIssuerSigningKeys = true, // proofs carry no kid; the only candidate is the embedded jwk
        }).ConfigureAwait(false);

        if (!validation.IsValid)
        {
            return DPoPValidationResult.Failure("The DPoP proof signature is invalid.");
        }

        if (!token.TryGetPayloadValue<string>("jti", out var jti) || string.IsNullOrWhiteSpace(jti) || jti.Length > 256)
        {
            return DPoPValidationResult.Failure("The DPoP proof must contain a jti.");
        }

        if (!token.TryGetPayloadValue<string>("htm", out var htm) || !string.Equals(htm, httpMethod, StringComparison.Ordinal))
        {
            return DPoPValidationResult.Failure("The DPoP proof htm does not match the request method.");
        }

        if (!token.TryGetPayloadValue<string>("htu", out var htu) || !Uri.TryCreate(htu, UriKind.Absolute, out var proofUri)
            || !SameTarget(proofUri, httpUri))
        {
            return DPoPValidationResult.Failure("The DPoP proof htu does not match the request URI.");
        }

        if (!token.TryGetPayloadValue<long>("iat", out var iatSeconds))
        {
            return DPoPValidationResult.Failure("The DPoP proof must contain iat.");
        }

        var now = timeProvider.GetUtcNow();
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(iatSeconds);
        if (issuedAt > now + settings.ClockSkew || issuedAt < now - settings.ProofLifetime - settings.ClockSkew)
        {
            return DPoPValidationResult.Failure("The DPoP proof is expired or not yet valid.");
        }

        if (accessToken is not null)
        {
            if (!token.TryGetPayloadValue<string>("ath", out var ath)
                || !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(ath),
                    System.Text.Encoding.ASCII.GetBytes(KeyMaterial.Sha256Base64Url(accessToken))))
            {
                return DPoPValidationResult.Failure("The DPoP proof ath does not match the access token.");
            }
        }

        var thumbprint = KeyMaterial.ComputeJwkThumbprint(jwk);
        if (expectedThumbprint is not null && !string.Equals(thumbprint, expectedThumbprint, StringComparison.Ordinal))
        {
            return DPoPValidationResult.Failure("The DPoP proof key does not match the token binding.");
        }

        if (!await replayCache.TryAddAsync($"dpop:{thumbprint}:{jti}", settings.ProofLifetime + (settings.ClockSkew * 2), cancellationToken).ConfigureAwait(false))
        {
            return DPoPValidationResult.Failure("The DPoP proof has already been used.");
        }

        return DPoPValidationResult.Success(thumbprint);
    }

    /// <summary>RFC 9449 §4.3: compare without query and fragment; scheme/host case-insensitive.</summary>
    private static bool SameTarget(Uri proof, Uri request) =>
        string.Equals(proof.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(proof.Host, request.Host, StringComparison.OrdinalIgnoreCase)
        && proof.Port == request.Port
        && string.Equals(proof.AbsolutePath, request.AbsolutePath, StringComparison.Ordinal);

    /// <summary><c>cnf</c> claim value binding a token to a DPoP key.</summary>
    public static string ConfirmationClaimValue(string thumbprint) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { [DPoPConstants.ThumbprintMember] = thumbprint });

    /// <summary>Extracts <c>cnf.jkt</c> from a JSON claim value.</summary>
    public static string? ReadThumbprint(string? confirmationClaim)
    {
        if (string.IsNullOrWhiteSpace(confirmationClaim))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(confirmationClaim);
            return json.RootElement.TryGetProperty(DPoPConstants.ThumbprintMember, out var jkt) ? jkt.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Creates DPoP proofs (clients, tests and tooling).</summary>
public static class DPoPProofFactory
{
    public static string Create(
        ECDsa key,
        string httpMethod,
        Uri httpUri,
        DateTimeOffset now,
        string? accessToken = null,
        string? jti = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        var securityKey = new ECDsaSecurityKey(key);
        var publicJwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(ECDsa.Create(key.ExportParameters(false))));

        var claims = new Dictionary<string, object>
        {
            ["jti"] = jti ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            ["htm"] = httpMethod,
            ["htu"] = httpUri.GetLeftPart(UriPartial.Path),
            ["iat"] = now.ToUnixTimeSeconds(),
        };

        if (accessToken is not null)
        {
            claims["ath"] = KeyMaterial.Sha256Base64Url(accessToken);
        }

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            TokenType = DPoPConstants.ProofType,
            Claims = claims,
            AdditionalHeaderClaims = new Dictionary<string, object>
            {
                ["jwk"] = new Dictionary<string, object> { ["kty"] = publicJwk.Kty, ["crv"] = publicJwk.Crv, ["x"] = publicJwk.X, ["y"] = publicJwk.Y },
            },
            SigningCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.EcdsaSha256),
        });
    }

    public static string Thumbprint(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(ECDsa.Create(key.ExportParameters(false))));
        return KeyMaterial.ComputeJwkThumbprint(jwk);
    }
}
