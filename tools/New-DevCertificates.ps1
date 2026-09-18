<#
.SYNOPSIS
    Generates LOCAL DEVELOPMENT certificates for the EduEco Identity container.

.DESCRIPTION
    Creates in ./certs (git-ignored):
      identity-signing.pfx     RSA 3072, DigitalSignature  -> OpenIddict token signing (published in JWKS)
      identity-encryption.pfx  RSA 3072, KeyEncipherment   -> OpenIddict token encryption (codes, refresh tokens)
      aspnetcore-https.pfx     ASP.NET Core HTTPS development certificate (trust with: dotnet dev-certs https --trust)
      bff-client.pfx/.cer      private_key_jwt key of eduEco-bff (BFF signs client assertions; migrator registers the .cer)
      api-client.pfx/.cer      private_key_jwt key of eduEco-api (introspection + token exchange)
      svc-dev-client.pfx/.cer  private_key_jwt key of eduEco-svc-dev (see tools/New-ClientAssertion.ps1)

    Password defaults to IDENTITY_CERT_PASSWORD from ./.env.
    Production certificates must come from the organisation's PKI / Key Vault, never from this script.
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [string] $Password,
    [int] $ValidityYears = 2,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
# $PSScriptRoot is not available in param defaults on Windows PowerShell 5.1.
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot '..\certs' }

if (-not $Password) {
    $envFile = Join-Path $PSScriptRoot '..\.env'
    if (Test-Path $envFile) {
        $line = Get-Content $envFile | Where-Object { $_ -like 'IDENTITY_CERT_PASSWORD=*' } | Select-Object -First 1
        if ($line) { $Password = $line.Substring('IDENTITY_CERT_PASSWORD='.Length) }
    }
}
if (-not $Password) { throw 'Provide -Password or set IDENTITY_CERT_PASSWORD in .env.' }

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path

function New-RsaCertificate([string] $FileName, [string] $Subject, [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags] $Usage, [switch] $ExportPublic) {
    $path = Join-Path $OutputDirectory $FileName
    if ((Test-Path $path) -and -not $Force) {
        Write-Host "exists  $FileName (use -Force to regenerate)"
        return
    }

    $rsa = [System.Security.Cryptography.RSA]::Create(3072)
    try {
        $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
            $Subject, $rsa,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $request.CertificateExtensions.Add(
            [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new($Usage, $true))

        $notBefore = [DateTimeOffset]::UtcNow.AddMinutes(-5)
        $certificate = $request.CreateSelfSigned($notBefore, $notBefore.AddYears($ValidityYears))
        try {
            $bytes = $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $Password)
            [System.IO.File]::WriteAllBytes($path, $bytes)
            if ($ExportPublic) {
                # Public part only: what the authorization server needs to verify client assertions.
                $cerPath = [System.IO.Path]::ChangeExtension($path, '.cer')
                [System.IO.File]::WriteAllBytes($cerPath, $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
            }
            Write-Host "created $FileName (thumbprint $($certificate.Thumbprint), expires $($certificate.NotAfter.ToString('yyyy-MM-dd')))"
        }
        finally { $certificate.Dispose() }
    }
    finally { $rsa.Dispose() }
}

New-RsaCertificate 'identity-signing.pfx' 'CN=EduEco Identity Signing (Development)' ([System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature)
New-RsaCertificate 'identity-encryption.pfx' 'CN=EduEco Identity Encryption (Development)' ([System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment)
New-RsaCertificate 'bff-client.pfx' 'CN=eduEco-bff (Development)' ([System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) -ExportPublic
New-RsaCertificate 'api-client.pfx' 'CN=eduEco-api (Development)' ([System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) -ExportPublic
New-RsaCertificate 'svc-dev-client.pfx' 'CN=eduEco-svc-dev (Development)' ([System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature) -ExportPublic

$httpsPath = Join-Path $OutputDirectory 'aspnetcore-https.pfx'
if ((Test-Path $httpsPath) -and -not $Force) {
    Write-Host 'exists  aspnetcore-https.pfx'
}
else {
    & dotnet dev-certs https --export-path $httpsPath --password $Password --format Pfx | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet dev-certs export failed.' }
    Write-Host 'created aspnetcore-https.pfx (run "dotnet dev-certs https --trust" once to trust it in the browser)'
}
