using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Certificates;
using EduEco.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace EduEco.ServiceRegistry;

/// <summary>
/// Azure Key Vault settings (section <c>KeyVault</c>). Everything is optional: without <see cref="Uri"/> the hosts keep
/// using files, environment variables and user secrets (local Docker). Authentication uses Microsoft Entra ID only
/// (managed identity in Azure, developer credentials locally); no Key Vault credentials are ever stored in configuration.
/// </summary>
public sealed class KeyVaultOptions
{
    public const string SectionName = "KeyVault";

    /// <summary>Vault URI, e.g. <c>https://eduEco-prod.vault.azure.net/</c>.</summary>
    public Uri? Uri { get; set; }

    /// <summary>User-assigned managed identity client id; empty = system-assigned / default credential chain.</summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>Load vault secrets as configuration (<c>ConnectionStrings--EduEco</c> becomes <c>ConnectionStrings:EduEco</c>).</summary>
    public bool LoadSecrets { get; set; } = true;

    /// <summary>Only secrets starting with <c>{SecretPrefix}--</c> are loaded (prefix stripped), so hosts can share a vault.</summary>
    public string? SecretPrefix { get; set; }

    public TimeSpan? ReloadInterval { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>Data Protection key ring in Azure (section <c>DataProtection</c>), shared by all instances of a host.</summary>
public sealed class AzureDataProtectionOptions
{
    public const string SectionName = "DataProtection";

    /// <summary>Blob that stores the key ring, e.g. <c>https://account.blob.core.windows.net/dataprotection/identity.xml</c>.</summary>
    public Uri? BlobUri { get; set; }

    /// <summary>Key Vault key that wraps the key ring, e.g. <c>https://vault.vault.azure.net/keys/dataprotection</c>.</summary>
    public Uri? KeyVaultKeyId { get; set; }
}

public static class KeyVaultExtensions
{
    /// <summary>Adds Key Vault secrets as the highest-priority configuration source (no-op without <c>KeyVault:Uri</c>).</summary>
    public static IHostApplicationBuilder AddEduEcoKeyVault(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Configuration.GetSection(KeyVaultOptions.SectionName).Get<KeyVaultOptions>() ?? new KeyVaultOptions();
        builder.Services.TryAddSingleton(CertificateLoader.Create(builder.Configuration));

        if (options.Uri is null || !options.LoadSecrets)
        {
            return builder;
        }

        builder.Configuration.AddAzureKeyVault(options.Uri, CreateCredential(options), new AzureKeyVaultConfigurationOptions
        {
            Manager = string.IsNullOrWhiteSpace(options.SecretPrefix)
                ? new KeyVaultSecretManager()
                : new PrefixKeyVaultSecretManager(options.SecretPrefix),
            ReloadInterval = options.ReloadInterval,
        });

        return builder;
    }

    /// <summary>
    /// Persists the key ring to Azure Blob Storage and wraps it with a Key Vault key when <c>DataProtection:BlobUri</c> /
    /// <c>DataProtection:KeyVaultKeyId</c> are set.
    /// </summary>
    /// <returns><c>true</c> when the key ring is protected by Key Vault (callers must not add another key encryptor).</returns>
    public static bool UseAzureKeyRing(this IDataProtectionBuilder dataProtection, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(dataProtection);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection(AzureDataProtectionOptions.SectionName).Get<AzureDataProtectionOptions>() ?? new AzureDataProtectionOptions();
        if (options.BlobUri is null && options.KeyVaultKeyId is null)
        {
            return false;
        }

        var credential = CreateCredential(configuration.GetSection(KeyVaultOptions.SectionName).Get<KeyVaultOptions>() ?? new KeyVaultOptions());
        if (options.BlobUri is not null)
        {
            dataProtection.PersistKeysToAzureBlobStorage(options.BlobUri, credential);
        }

        if (options.KeyVaultKeyId is not null)
        {
            dataProtection.ProtectKeysWithAzureKeyVault(options.KeyVaultKeyId, credential);
            return true;
        }

        return false;
    }

    /// <summary>Whether the host has a shared, durable key ring (file system or Azure).</summary>
    public static bool HasAzureKeyRing(IConfiguration configuration) =>
        configuration.GetSection(AzureDataProtectionOptions.SectionName).Get<AzureDataProtectionOptions>()?.BlobUri is not null;

    internal static TokenCredential CreateCredential(KeyVaultOptions options) =>
        string.IsNullOrWhiteSpace(options.ManagedIdentityClientId)
            ? new DefaultAzureCredential(new DefaultAzureCredentialOptions { ExcludeInteractiveBrowserCredential = true })
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId));

    /// <summary>Loads only <c>{prefix}--*</c> secrets and strips the prefix.</summary>
    private sealed class PrefixKeyVaultSecretManager(string prefix) : KeyVaultSecretManager
    {
        private readonly string _prefix = prefix + "--";

        public override bool Load(Azure.Security.KeyVault.Secrets.SecretProperties secret) =>
            secret.Name.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase);

        public override string GetKey(Azure.Security.KeyVault.Secrets.KeyVaultSecret secret) =>
            base.GetKey(secret)[(_prefix.Length - 1)..].TrimStart(':');
    }
}

/// <summary>
/// Loads a certificate with its private key from Key Vault (by certificate name) or from a PKCS#12 file.
/// Key Vault keeps the private key non-exportable from the portal and supports rotation by version.
/// </summary>
public sealed class CertificateLoader
{
    private readonly CertificateClient? _client;

    private CertificateLoader(CertificateClient? client) => _client = client;

    public static CertificateLoader Create(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(KeyVaultOptions.SectionName).Get<KeyVaultOptions>() ?? new KeyVaultOptions();
        return new CertificateLoader(options.Uri is null ? null : new CertificateClient(options.Uri, KeyVaultExtensions.CreateCredential(options)));
    }

    public bool KeyVaultEnabled => _client is not null;

    /// <param name="path">PKCS#12 file (used when <paramref name="keyVaultCertificateName"/> is empty).</param>
    /// <param name="password">PKCS#12 password.</param>
    /// <param name="keyVaultCertificateName">Key Vault certificate name; latest enabled version is used.</param>
    public X509Certificate2 Load(string? path, string? password, string? keyVaultCertificateName)
    {
        if (!string.IsNullOrWhiteSpace(keyVaultCertificateName))
        {
            if (_client is null)
            {
                throw new InvalidOperationException(
                    $"Certificate '{keyVaultCertificateName}' is configured to come from Key Vault but KeyVault:Uri is not set.");
            }

            return _client.DownloadCertificate(new DownloadCertificateOptions(keyVaultCertificateName)
            {
                KeyStorageFlags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet,
            }).Value;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Either a certificate path or a Key Vault certificate name is required.");
        }

        return KeyMaterial.LoadPkcs12(path, password);
    }
}
