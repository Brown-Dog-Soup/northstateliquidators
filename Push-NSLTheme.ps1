<#
.SYNOPSIS
    Push the local NSL Loading Dock theme to the Shopify dev store.

.DESCRIPTION
    Convenience wrapper around `shopify theme push`. The theme lives in
    `shopify-theme/` and gets pushed under the name "NSL-LoadingDock".

    Use -Publish to also make it the active storefront theme.
    Use -PreviewOnly (default) to push to an unpublished theme so you can
    preview before committing.

.EXAMPLE
    .\Push-NSLTheme.ps1                    # push to NSL-LoadingDock unpublished
    .\Push-NSLTheme.ps1 -Publish           # push and publish (replaces active)
#>
[CmdletBinding()]
param(
    [string]$Store = 'north-state-liquidators-dev',
    [string]$ThemeName = 'NSL-LoadingDock',
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$themePath = Join-Path $PSScriptRoot 'shopify-theme'

if (-not (Test-Path $themePath)) {
    throw "Theme directory not found at $themePath"
}

Push-Location $themePath
try {
    Write-Host "Pushing $ThemeName to $Store..." -ForegroundColor Cyan
    shopify theme push --store=$Store --theme=$ThemeName --json
    if ($LASTEXITCODE -ne 0) { throw "shopify theme push failed (exit $LASTEXITCODE)" }

    if ($Publish) {
        Write-Host "Publishing $ThemeName as active storefront theme..." -ForegroundColor Cyan
        shopify theme publish --store=$Store --theme=$ThemeName --force
        if ($LASTEXITCODE -ne 0) { throw "shopify theme publish failed (exit $LASTEXITCODE)" }
    } else {
        Write-Host "Theme uploaded (not published). Preview at:" -ForegroundColor Green
        Write-Host "  https://$Store.myshopify.com/?preview_theme_id=<id>" -ForegroundColor Yellow
        Write-Host "  Use -Publish to make it the active theme." -ForegroundColor DarkGray
    }
} finally {
    Pop-Location
}
