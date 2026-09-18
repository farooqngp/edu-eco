using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EduEco.ServiceRegistry;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace EduEco.Infrastructure.IntegrationTests;

/// <summary>Key Vault wiring stays inert until configured, and fails fast on half-configured settings.</summary>
public sealed class KeyVaultConfigurationTests
{
    [Fact]
    public void Without_a_vault_uri_no_azure_configuration_source_is_added()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        var sources = ((IConfigurationBuilder)builder.Configuration).Sources.Count;

        builder.AddEduEcoKeyVault();

        ((IConfigurationBuilder)builder.Configuration).Sources.Count.ShouldBe(sources);
        using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<CertificateLoader>().KeyVaultEnabled.ShouldBeFalse();
    }

    [Fact]
    public void Vault_uri_enables_certificate_loading_even_when_secrets_are_not_loaded()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KeyVault:Uri"] = "https://eduEco-test.vault.azure.net/",
            ["KeyVault:LoadSecrets"] = "false",
        });
        var sources = ((IConfigurationBuilder)builder.Configuration).Sources.Count;

        builder.AddEduEcoKeyVault();

        ((IConfigurationBuilder)builder.Configuration).Sources.Count.ShouldBe(sources);
        using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<CertificateLoader>().KeyVaultEnabled.ShouldBeTrue();
    }

    [Fact]
    public void Key_vault_certificate_without_vault_uri_fails_fast()
    {
        var loader = CertificateLoader.Create(new ConfigurationBuilder().Build());

        Should.Throw<InvalidOperationException>(() => loader.Load(null, null, "identity-signing"))
            .Message.ShouldContain("KeyVault:Uri");
        Should.Throw<InvalidOperationException>(() => loader.Load(null, null, null));
    }

    [Fact]
    public void File_certificates_are_loaded_when_no_vault_name_is_given()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eduEco-kv-{Guid.NewGuid():N}.pfx");
        using (var rsa = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=KV Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, "pw"));
        }

        try
        {
            using var loaded = CertificateLoader.Create(new ConfigurationBuilder().Build()).Load(path, "pw", keyVaultCertificateName: null);
            loaded.HasPrivateKey.ShouldBeTrue();
            loaded.Subject.ShouldBe("CN=KV Test");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Azure_key_ring_is_not_used_without_settings()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddDataProtection().UseAzureKeyRing(configuration).ShouldBeFalse();
        KeyVaultExtensions.HasAzureKeyRing(configuration).ShouldBeFalse();
    }
}
