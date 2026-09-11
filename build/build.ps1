#Requires -Version 5.1
<#
.SYNOPSIS
    Restores, builds, and tests the SDLC solution.
#>
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$sln = Join-Path $root "SDLC.sln"

Push-Location $root
try {
    dotnet restore $sln
    if ($LASTEXITCODE -ne 0) { throw "Restore failed." }

    dotnet build $sln --no-restore --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }

    dotnet test $sln --no-build --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}
finally {
    Pop-Location
}
