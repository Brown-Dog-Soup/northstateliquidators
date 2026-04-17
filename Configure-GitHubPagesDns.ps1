<#
.SYNOPSIS
    Configure GoDaddy DNS for GitHub Pages hosting of northstateliquidators.com.

.DESCRIPTION
    One-shot script that sets all the DNS records required to point
    northstateliquidators.com at GitHub Pages:
      • 4 A records on @ → GitHub Pages IPs
      • CNAME on www → Brown-Dog-Soup.github.io

    Relies on Set-GoDaddyDns.ps1 in the same directory. Requires
    $env:GODADDY_KEY and $env:GODADDY_SECRET to be set.

.PARAMETER WhatIf
    Show what would be done without making changes.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param()

$ErrorActionPreference = 'Stop'

$domain = 'northstateliquidators.com'
$githubIps = @(
    '185.199.108.153'
    '185.199.109.153'
    '185.199.110.153'
    '185.199.111.153'
)
$cnameTarget = 'Brown-Dog-Soup.github.io'

$setScript = Join-Path $PSScriptRoot 'Set-GoDaddyDns.ps1'
if (-not (Test-Path $setScript)) { throw "Set-GoDaddyDns.ps1 not found next to this script." }

Write-Host ""
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "  Configure GitHub Pages DNS - $domain" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# 1. Apex A records → GitHub Pages
& $setScript -Domain $domain -Type 'A' -Name '@' -Values $githubIps -WhatIf:$WhatIfPreference

# 2. www CNAME → Brown-Dog-Soup.github.io
& $setScript -Domain $domain -Type 'CNAME' -Name 'www' -Values $cnameTarget -WhatIf:$WhatIfPreference

Write-Host ""
Write-Host "==================================================" -ForegroundColor Green
Write-Host "  Next steps:" -ForegroundColor Green
Write-Host "    1. Wait 5-30 min for DNS propagation"
Write-Host "    2. Visit https://github.com/Brown-Dog-Soup/northstateliquidators/settings/pages"
Write-Host "    3. Enable 'Enforce HTTPS' once cert is issued"
Write-Host "    4. Test: https://northstateliquidators.com"
Write-Host "==================================================" -ForegroundColor Green
