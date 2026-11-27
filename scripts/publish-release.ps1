<#
.SYNOPSIS
    Builds release artifacts for Lekha Books (API, Web, Windows app, and Android APK/AAB)
    pointing to your production API domain.

.EXAMPLE
    ./scripts/publish-release.ps1 -ApiBaseUrl "https://api.lekhabooks.com/" -Version "1.0.0" -BuildNumber 1
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ApiBaseUrl,

    [string]$Version = "1.0.0",
    [int]$BuildNumber = 1,
    [switch]$SkipAndroid,
    [switch]$SkipWindows
)

$ErrorActionPreference = 'Stop'
if (-not $ApiBaseUrl.EndsWith('/')) { $ApiBaseUrl += '/' }

$outDir = Join-Path $PSScriptRoot "..\artifacts\release-$Version"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Write-Host "Publishing LekhaBook.Api -> $outDir\api" -ForegroundColor Cyan
dotnet publish src/LekhaBook.Api/LekhaBook.Api.csproj -c Release -o "$outDir\api"

Write-Host "Publishing LekhaBook.Web -> $outDir\web" -ForegroundColor Cyan
dotnet publish src/LekhaBook.Web/LekhaBook.Web.csproj -c Release -o "$outDir\web"

if (-not $SkipWindows) {
    Write-Host "Building LekhaBook.App (Windows) -> $ApiBaseUrl" -ForegroundColor Cyan
    dotnet publish src/LekhaBook.App/LekhaBook.App.csproj -f net10.0-windows10.0.19041.0 -c Release `
        -p:ApiBaseUrl="$ApiBaseUrl" `
        -p:ApplicationDisplayVersion="$Version" `
        -p:ApplicationVersion="$BuildNumber" `
        -o "$outDir\windows"
}

if (-not $SkipAndroid) {
    Write-Host "Building LekhaBook.App (Android AAB) -> $ApiBaseUrl" -ForegroundColor Cyan
    dotnet publish src/LekhaBook.App/LekhaBook.App.csproj -f net10.0-android -c Release `
        -p:ApiBaseUrl="$ApiBaseUrl" `
        -p:ApplicationDisplayVersion="$Version" `
        -p:ApplicationVersion="$BuildNumber" `
        -p:AndroidPackageFormat=aab `
        -o "$outDir\android"
}

Write-Host "`nRelease build finished! Artifacts are in: $outDir" -ForegroundColor Green
