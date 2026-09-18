using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.IdentityModel.Tokens;

namespace EduEco.Infrastructure.Security;

/// <summary>Loads certificates and converts them to JOSE keys (RSA or EC).</summary>
public static class KeyMaterial
{
    /// <summary>Loads a PKCS#12 file. Keys stay in memory only (no machine/user key store side effects).</summary>
    public static X509Certificate2 LoadPkcs12(string path, string? password)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Certificate file '{path}' not found.", path);
        }

        var flags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
        return X509CertificateLoader.LoadPkcs12FromFile(path, password, flags);
    }

    /// <summary>Loads a public certificate (DER/PEM .cer/.crt) or a PKCS#12 file (public part used).</summary>
    public static X509Certificate2 LoadPublicCertificate(string path, string? password = null) =>
        Path.GetExtension(path).Equals(".pfx", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".p12", StringComparison.OrdinalIgnoreCase)
            ? LoadPkcs12(path, password)
            : X509CertificateLoader.LoadCertificateFromFile(path);

    /// <summary>Public-only JWK (no private parameters) with <c>kid</c> = certificate thumbprint.</summary>
    public static JsonWebKey ToPublicJsonWebKey(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        JsonWebKey jwk;
        if (certificate.GetRSAPublicKey() is { } rsa)
        {
            jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(rsa.ExportParameters(includePrivateParameters: false)));
            jwk.Alg = SecurityAlgorithms.RsaSha256;
        }
        else if (certificate.GetECDsaPublicKey() is { } ecdsa)
        {
            jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(ecdsa));
            jwk.Alg = SecurityAlgorithms.EcdsaSha256;
        }
        else
        {
            throw new NotSupportedException("Only RSA and ECDSA certificates are supported.");
        }

        jwk.Kid = certificate.Thumbprint;
        jwk.Use = JsonWebKeyUseNames.Sig;
        return jwk;
    }

    /// <summary>Signing credentials for a certificate with a private key (RS256 or ES256).</summary>
    public static SigningCredentials ToSigningCredentials(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey)
        {
            throw new InvalidOperationException("The certificate has no private key.");
        }

        var algorithm = certificate.GetRSAPrivateKey() is not null ? SecurityAlgorithms.RsaSha256 : SecurityAlgorithms.EcdsaSha256;
        return new SigningCredentials(new X509SecurityKey(certificate, certificate.Thumbprint), algorithm);
    }

    /// <summary>RFC 7638 JWK SHA-256 thumbprint (base64url), used for DPoP <c>jkt</c>.</summary>
    public static string ComputeJwkThumbprint(JsonWebKey jwk)
    {
        ArgumentNullException.ThrowIfNull(jwk);
        return Base64UrlEncoder.Encode(jwk.ComputeJwkThumbprint());
    }

    public static string Sha256Base64Url(string value) =>
        Base64UrlEncoder.Encode(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(value)));
}
