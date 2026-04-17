<#
.SYNOPSIS
    Update GoDaddy DNS records via the Domain API.

.DESCRIPTION
    Uses GoDaddy's PUT /v1/domains/{domain}/records/{type}/{name} endpoint to
    replace all records of a given type/name with the values provided. This is
    the cleanest way to remove GoDaddy-parked defaults and set up new records.

    Requires API key + secret from https://developer.godaddy.com (Production tier).
    Pass via -Key/-Secret parameters, or set $env:GODADDY_KEY / $env:GODADDY_SECRET.

.PARAMETER Domain
    The domain to update (e.g. northstateliquidators.com).

.PARAMETER Type
    DNS record type: A, AAAA, CNAME, MX, TXT, NS, SRV, CAA.

.PARAMETER Name
    Record name. Use '@' for apex. Use 'www' or any subdomain for others.

.PARAMETER Values
    One or more string values. Use an array for A records with multiple IPs.

.PARAMETER Ttl
    TTL in seconds. Default 600 (10 min).

.PARAMETER Key
    GoDaddy API key. Defaults to $env:GODADDY_KEY.

.PARAMETER Secret
    GoDaddy API secret. Defaults to $env:GODADDY_SECRET.

.PARAMETER WhatIf
    Show what would be sent without actually making the call.

.EXAMPLE
    # Set up GitHub Pages apex A records for northstateliquidators.com
    .\Set-GoDaddyDns.ps1 -Domain 'northstateliquidators.com' -Type A -Name '@' `
        -Values '185.199.108.153','185.199.109.153','185.199.110.153','185.199.111.153'

.EXAMPLE
    # Set www CNAME to GitHub Pages
    .\Set-GoDaddyDns.ps1 -Domain 'northstateliquidators.com' -Type CNAME -Name 'www' `
        -Values 'Brown-Dog-Soup.github.io'

.EXAMPLE
    # Dry run
    .\Set-GoDaddyDns.ps1 -Domain 'example.com' -Type A -Name '@' -Values '1.2.3.4' -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory)]
    [string]$Domain,

    [Parameter(Mandatory)]
    [ValidateSet('A', 'AAAA', 'CNAME', 'MX', 'TXT', 'NS', 'SRV', 'CAA')]
    [string]$Type,

    [Parameter(Mandatory)]
    [string]$Name,

    [Parameter(Mandatory)]
    [string[]]$Values,

    [int]$Ttl = 600,

    [string]$Key = $env:GODADDY_KEY,

    [string]$Secret = $env:GODADDY_SECRET
)

$ErrorActionPreference = 'Stop'

if (-not $Key -or -not $Secret) {
    throw "Missing credentials. Set `$env:GODADDY_KEY and `$env:GODADDY_SECRET, or pass -Key/-Secret."
}

$uri = "https://api.godaddy.com/v1/domains/$Domain/records/$Type/$Name"

$body = @($Values | ForEach-Object {
    @{ data = $_; ttl = $Ttl }
}) | ConvertTo-Json -Depth 3

# ConvertTo-Json wraps single-item arrays oddly — ensure it's always a JSON array
if ($Values.Count -eq 1) { $body = "[$body]" }

$headers = @{
    'Authorization' = "sso-key $Key`:$Secret"
    'Content-Type'  = 'application/json'
    'Accept'        = 'application/json'
}

Write-Host ""
Write-Host "  GoDaddy DNS Update" -ForegroundColor Cyan
Write-Host "  ------------------"
Write-Host "  Domain  : $Domain"
Write-Host "  Record  : $Type $Name"
Write-Host "  Values  :"
$Values | ForEach-Object { Write-Host "              $_" }
Write-Host "  TTL     : $Ttl sec"
Write-Host "  Endpoint: PUT $uri"
Write-Host ""

if ($PSCmdlet.ShouldProcess("$Type $Name @ $Domain", 'Replace DNS records')) {
    try {
        $response = Invoke-RestMethod -Method Put -Uri $uri -Headers $headers -Body $body
        Write-Host "  [OK] Success - records replaced" -ForegroundColor Green
        return $response
    }
    catch {
        $status = $_.Exception.Response.StatusCode.value__
        $reason = $_.Exception.Response.StatusDescription
        Write-Host "  [FAIL] HTTP $status $reason" -ForegroundColor Red

        switch ($status) {
            401 { Write-Host "  → Invalid API key/secret" -ForegroundColor Yellow }
            403 { Write-Host "  → API tier forbidden (GoDaddy restricts Developer API to accounts meeting spend/domain thresholds)" -ForegroundColor Yellow }
            404 { Write-Host "  → Domain '$Domain' not found in this account" -ForegroundColor Yellow }
            422 { Write-Host "  → Invalid record data — check values for type $Type" -ForegroundColor Yellow }
        }

        # Try to surface the body
        try {
            $errStream = $_.Exception.Response.GetResponseStream()
            $reader = New-Object System.IO.StreamReader($errStream)
            $errBody = $reader.ReadToEnd()
            if ($errBody) { Write-Host "  Body: $errBody" -ForegroundColor DarkYellow }
        } catch {}

        throw
    }
}
