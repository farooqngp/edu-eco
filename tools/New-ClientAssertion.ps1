<#
.SYNOPSIS
    Creates a private_key_jwt client assertion (RFC 7523) for LOCAL DEVELOPMENT calls to the EduEco token endpoint.

.DESCRIPTION
    Signs a short-lived JWT (typ client-authentication+jwt, iss = sub = client id, aud = issuer, random jti) with the
    client's development key from ./certs. Paste the output into the client_assertion field, for example in
    EduEco.Api.http, together with:
        client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer

    Each assertion is single-use (jti replay protection) and expires after 60 seconds.

.EXAMPLE
    ./tools/New-ClientAssertion.ps1 -ClientId eduEco-svc-dev
#>
[CmdletBinding()]
param(
    [string] $ClientId = 'eduEco-svc-dev',
    [string] $Issuer = 'https://localhost:7013/',
    [string] $CertificatePath,
    [string] $Password,
    [int] $LifetimeSeconds = 60
)

$ErrorActionPreference = 'Stop'
if (-not $CertificatePath) {
    $name = switch ($ClientId) { 'eduEco-bff' { 'bff-client' } 'eduEco-api' { 'api-client' } default { 'svc-dev-client' } }
    $CertificatePath = Join-Path $PSScriptRoot "..\certs\$name.pfx"
}

if (-not $Password) {
    $envFile = Join-Path $PSScriptRoot '..\.env'
    if (Test-Path $envFile) {
        $line = Get-Content $envFile | Where-Object { $_ -like 'IDENTITY_CERT_PASSWORD=*' } | Select-Object -First 1
        if ($line) { $Password = $line.Substring('IDENTITY_CERT_PASSWORD='.Length) }
    }
}
if (-not $Password) { throw 'Provide -Password or set IDENTITY_CERT_PASSWORD in .env.' }

function ConvertTo-Base64Url([byte[]] $Bytes) {
    [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function ConvertTo-JsonSegment($Value) {
    ConvertTo-Base64Url ([System.Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Compress)))
}

$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    (Resolve-Path $CertificatePath).Path, $Password,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
try {
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

    $header = [ordered]@{ alg = 'RS256'; typ = 'client-authentication+jwt'; kid = $certificate.Thumbprint }
    $payload = [ordered]@{
        iss = $ClientId
        sub = $ClientId
        aud = $Issuer
        jti = [Guid]::NewGuid().ToString('N')
        iat = $now
        nbf = $now
        exp = $now + $LifetimeSeconds
    }

    $signingInput = "$(ConvertTo-JsonSegment $header).$(ConvertTo-JsonSegment $payload)"
    $signature = $rsa.SignData(
        [System.Text.Encoding]::ASCII.GetBytes($signingInput),
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

    "$signingInput.$(ConvertTo-Base64Url $signature)"
}
finally {
    $certificate.Dispose()
}
