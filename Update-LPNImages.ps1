<#
.SYNOPSIS
    Looks up product images for catalog rows that have an ASIN and writes the
    image URL back to dbo.lpn_catalog.

.DESCRIPTION
    Adds (idempotently) three columns to dbo.lpn_catalog:
        product_image_url           nvarchar(1000)
        image_lookup_attempted_at   datetime2
        image_lookup_status         varchar(20)   ('ok' | 'no_image' | 'not_found' | 'blocked' | 'error')

    Selects rows where asin is populated and product_image_url is NULL, calls
    Rainforest API (api.rainforestapi.com) for each ASIN, and writes the
    main product image URL back in batches via a staging table.

    Requires the RAINFOREST_API_KEY environment variable. Rainforest handles
    proxies, headless browsers, and CAPTCHA solving on Amazon's side, so the
    aggressive throttling that affected the direct-scrape version is gone.

.PARAMETER Limit
    Maximum number of rows to process this run. Useful for smoke testing.
    0 (default) means no limit.

.PARAMETER DelayMs
    Milliseconds to wait between requests. Default 1500.

.PARAMETER RetryFailed
    Also re-attempt rows previously marked 'blocked' or 'error'.

.PARAMETER FlushEvery
    Flush accumulated results to SQL every N rows. Default 50.
#>
[CmdletBinding()]
param(
    [int]$Limit = 0,
    [int]$DelayMs = 200,
    [switch]$RetryFailed,
    [int]$FlushEvery = 50
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command Invoke-Sqlcmd -ErrorAction SilentlyContinue)) {
    throw "Invoke-Sqlcmd not available — install the SqlServer PowerShell module."
}

$rainforestKey = $env:RAINFOREST_API_KEY
if (-not $rainforestKey) {
    throw "RAINFOREST_API_KEY env var not set. `$env:RAINFOREST_API_KEY = '<key>'  (current session) or setx RAINFOREST_API_KEY <key>  (persisted)."
}

$server = 'sql-nsl-prod-nc5h2y.database.windows.net'
$database = 'sqldb-nsl-prod'

function Get-Token { az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv }

function Invoke-NSLSql([string]$Query) {
    Invoke-Sqlcmd -ServerInstance $server -Database $database -AccessToken (Get-Token) -Query $Query -QueryTimeout 300
}

# --- Ensure schema -------------------------------------------------------
Write-Host "Ensuring image columns exist on dbo.lpn_catalog ..."
Invoke-NSLSql @"
IF COL_LENGTH('dbo.lpn_catalog', 'product_image_url') IS NULL
    ALTER TABLE dbo.lpn_catalog ADD product_image_url nvarchar(1000) NULL;
IF COL_LENGTH('dbo.lpn_catalog', 'image_lookup_attempted_at') IS NULL
    ALTER TABLE dbo.lpn_catalog ADD image_lookup_attempted_at datetime2 NULL;
IF COL_LENGTH('dbo.lpn_catalog', 'image_lookup_status') IS NULL
    ALTER TABLE dbo.lpn_catalog ADD image_lookup_status varchar(20) NULL;
"@ | Out-Null

# --- Pick rows to process ------------------------------------------------
$retryClause = if ($RetryFailed) {
    "AND (product_image_url IS NULL OR image_lookup_status IN ('error','blocked'))"
} else {
    "AND product_image_url IS NULL AND image_lookup_status IS NULL"
}
$topClause = if ($Limit -gt 0) { "TOP ($Limit)" } else { "" }

$selectSql = @"
SELECT $topClause lpn, asin
FROM dbo.lpn_catalog
WHERE asin IS NOT NULL AND LEN(LTRIM(RTRIM(asin))) > 0
$retryClause
ORDER BY lpn;
"@

$todo = @(Invoke-NSLSql $selectSql)
Write-Host "  $($todo.Count) rows to process."
if ($todo.Count -eq 0) { return }

# --- Lookup loop ---------------------------------------------------------
function Resolve-AmazonImage([string]$Asin) {
    $url = "https://api.rainforestapi.com/request?api_key=$rainforestKey&type=product&amazon_domain=amazon.com&asin=$Asin&output=json"
    try {
        $resp = Invoke-RestMethod -Uri $url -Method Get -TimeoutSec 60 -ErrorAction Stop
    } catch {
        $sc = $_.Exception.Response.StatusCode.value__
        if ($sc -eq 404) { return @{ status = 'not_found'; url = $null } }
        if ($sc -in 429,503) { return @{ status = 'blocked'; url = $null } }
        return @{ status = 'error'; url = $null; err = $_.Exception.Message }
    }

    if ($resp.request_info -and $resp.request_info.success -eq $false) {
        $msg = "$($resp.request_info.message)"
        if ($msg -match 'not[ _-]?found|invalid asin|no product') {
            return @{ status = 'not_found'; url = $null }
        }
        return @{ status = 'error'; url = $null; err = $msg }
    }
    if (-not $resp.product) {
        return @{ status = 'not_found'; url = $null }
    }

    $img = $null
    if ($resp.product.main_image -and $resp.product.main_image.link) {
        $img = $resp.product.main_image.link
    } elseif ($resp.product.images -and $resp.product.images.Count -gt 0 -and $resp.product.images[0].link) {
        $img = $resp.product.images[0].link
    }

    if ($img -and "$img" -match '^https?://') {
        return @{ status = 'ok'; url = "$img" }
    }
    return @{ status = 'no_image'; url = $null }
}

function Q([object]$v) {
    if ($null -eq $v -or "$v" -eq '') { return 'NULL' }
    $s = "$v" -replace "'", "''"
    return "N'$s'"
}

function Flush-Results([array]$results) {
    if ($results.Count -eq 0) { return }
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("SET NOCOUNT ON;")
    [void]$sb.AppendLine("IF OBJECT_ID('tempdb..#img_results') IS NOT NULL DROP TABLE #img_results;")
    [void]$sb.AppendLine("CREATE TABLE #img_results (lpn varchar(40) NOT NULL, url nvarchar(1000) NULL, status varchar(20) NOT NULL);")
    [void]$sb.AppendLine("INSERT INTO #img_results (lpn,url,status) VALUES")
    $valueLines = foreach ($r in $results) {
        "(" + (Q $r.lpn) + "," + (Q $r.url) + "," + (Q $r.status) + ")"
    }
    [void]$sb.AppendLine(($valueLines -join ",`n") + ';')
    [void]$sb.AppendLine(@"
UPDATE t SET
    t.product_image_url = r.url,
    t.image_lookup_status = r.status,
    t.image_lookup_attempted_at = SYSUTCDATETIME()
FROM dbo.lpn_catalog t
JOIN #img_results r ON r.lpn = t.lpn;
"@)
    Invoke-NSLSql $sb.ToString() | Out-Null
}

# --- Main loop -----------------------------------------------------------
$pending = New-Object System.Collections.Generic.List[object]
$counts = @{ ok=0; no_image=0; not_found=0; blocked=0; error=0 }
$start = Get-Date

for ($i = 0; $i -lt $todo.Count; $i++) {
    $row = $todo[$i]
    $res = Resolve-AmazonImage $row.asin
    $counts[$res.status]++
    $pending.Add([pscustomobject]@{ lpn = $row.lpn; url = $res.url; status = $res.status })

    if ((($i + 1) % 25) -eq 0 -or $i -eq $todo.Count - 1) {
        $elapsed = ((Get-Date) - $start).TotalSeconds
        $rate = if ($elapsed -gt 0) { ($i + 1) / $elapsed } else { 0 }
        Write-Host ("  [{0,5}/{1}]  ok={2} no_image={3} not_found={4} blocked={5} error={6}  ({7:N2}/s)" -f `
            ($i + 1), $todo.Count, $counts.ok, $counts.no_image, $counts.not_found, $counts.blocked, $counts.error, $rate)
    }

    if ($pending.Count -ge $FlushEvery) {
        Flush-Results $pending.ToArray()
        $pending.Clear()
    }

    if ($i -lt $todo.Count - 1) { Start-Sleep -Milliseconds $DelayMs }

    if (($counts.blocked + $counts.error) -ge 10 -and $counts.ok -eq 0) {
        Write-Warning "10 failures with zero successes — check API key / Rainforest plan. Stopping early."
        break
    }
}

if ($pending.Count -gt 0) {
    Flush-Results $pending.ToArray()
    $pending.Clear()
}

$elapsed = ((Get-Date) - $start).TotalSeconds
Write-Host ""
Write-Host "Done in $('{0:N1}' -f $elapsed)s."
Write-Host "  ok:        $($counts.ok)"
Write-Host "  no_image:  $($counts.no_image)"
Write-Host "  not_found: $($counts.not_found)"
Write-Host "  blocked:   $($counts.blocked)"
Write-Host "  error:     $($counts.error)"
