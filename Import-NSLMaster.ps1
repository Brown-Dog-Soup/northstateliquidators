<#
.SYNOPSIS
    Imports the NSL "INVENTORY MASTER.xlsx" file into dbo.lpn_catalog.

.DESCRIPTION
    Built specifically for the master-inventory format (Brand | Item Description |
    Item # | UPC | Seller Category | Condition | NSL Lot # | Qty | Unit Retail |
    Ext. Retail | Unit Cost | Wholesale Price | Sold).

    Skips rows whose Item # is not LPN- or LPTG-prefixed (apparel rows + numeric
    SKUs without barcodes are not catalog-able). Within-file duplicates collapse
    on the lpn primary key (last-row-wins).

    Drives the import via Invoke-Sqlcmd: builds a single SQL batch with a temp
    staging table, multi-row INSERTs, and a MERGE — all in one connection so the
    temp table is in scope for the whole flow. No SqlBulkCopy needed.

.PARAMETER File
    Path to the INVENTORY MASTER xlsx. Defaults to ./Amazon/INVENTORY MASTER.xlsx.

.PARAMETER WhatIf
    Show counts but do not write to SQL.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$File = "$PSScriptRoot\Amazon\INVENTORY MASTER.xlsx"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $File)) { throw "File not found: $File" }
if (-not (Get-Command Invoke-Sqlcmd -ErrorAction SilentlyContinue)) {
    throw "Invoke-Sqlcmd not available — install the SqlServer PowerShell module."
}

# --- Read ----------------------------------------------------------------
$tmp = "$env:TEMP\nsl-master-$([guid]::NewGuid().Guid).xlsx"
Copy-Item $File $tmp -Force
try {
    Write-Host "Reading $File ..."
    $rows = @(Import-Excel -Path $tmp -WorksheetName 'Manifest') +
            @(Import-Excel -Path $tmp -WorksheetName 'Headphones')
    Write-Host "  $($rows.Count) total rows across Manifest + Headphones"
} finally {
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
}

# --- Filter + dedupe -----------------------------------------------------
$catalogable = $rows | Where-Object { $_.'Item #' -match '^LP[A-Z0-9]' }
Write-Host "  $($catalogable.Count) rows have an LP-prefixed Item # (Amazon LPN, Target LPTG/LPHZ/LPJW — the rest are apparel/numeric SKU)"

$byLpn = @{}
foreach ($r in $catalogable) { $byLpn[$r.'Item #'.Trim()] = $r }
$dedup = @($byLpn.Values)
Write-Host "  $($dedup.Count) unique LPNs after collapsing within-file duplicates"

if ($WhatIfPreference) {
    Write-Host ""
    Write-Host "[WhatIf] Skipping SQL operation."
    return
}

# --- Build the batch SQL -------------------------------------------------
function Q([object]$v) {
    if ($null -eq $v -or "$v" -eq '') { return 'NULL' }
    $s = "$v" -replace "'", "''"
    return "N'$s'"
}
function Num([object]$v) {
    if ($null -eq $v -or "$v" -eq '') { return 'NULL' }
    $d = 0.0
    if ([decimal]::TryParse("$v", [ref]$d)) { return $d.ToString([System.Globalization.CultureInfo]::InvariantCulture) }
    return 'NULL'
}
function Int([object]$v) {
    if ($null -eq $v -or "$v" -eq '') { return 'NULL' }
    $i = 0
    if ([int]::TryParse(("$v" -replace '\..*$',''), [ref]$i)) { return "$i" }
    return 'NULL'
}

$source = (Split-Path $File -Leaf)
$sourceSql = $source -replace "'", "''"

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine(@"
SET NOCOUNT ON;
IF OBJECT_ID('tempdb..#lpn_staging') IS NOT NULL DROP TABLE #lpn_staging;
CREATE TABLE #lpn_staging (
    lpn varchar(40) NOT NULL,
    asin varchar(20) NULL, upc varchar(20) NULL, ean varchar(20) NULL,
    title nvarchar(500) NULL, description nvarchar(max) NULL,
    brand nvarchar(200) NULL, category nvarchar(200) NULL, subcategory nvarchar(200) NULL,
    msrp decimal(12,2) NULL, unit_cost decimal(12,4) NULL,
    condition varchar(40) NULL, qty_in_manifest int NULL,
    seller_category nvarchar(200) NULL, product_class nvarchar(200) NULL,
    order_number nvarchar(100) NULL, pallet_id nvarchar(200) NULL, lot_id nvarchar(200) NULL,
    source_manifest nvarchar(500) NOT NULL, source_pallet_ref nvarchar(200) NULL
);
"@)

# SQL Server caps multi-row INSERT VALUES at 1000 rows.
$batchSize = 900
for ($offset = 0; $offset -lt $dedup.Count; $offset += $batchSize) {
    $batch = $dedup[$offset..[Math]::Min($offset + $batchSize - 1, $dedup.Count - 1)]
    [void]$sb.AppendLine("INSERT INTO #lpn_staging (lpn,asin,upc,ean,title,description,brand,category,subcategory,msrp,unit_cost,condition,qty_in_manifest,seller_category,product_class,order_number,pallet_id,lot_id,source_manifest,source_pallet_ref) VALUES")
    $valueLines = foreach ($r in $batch) {
        $vals = @(
            (Q $r.'Item #'.Trim()),                            # lpn
            'NULL',                                             # asin
            (Q $r.UPC),                                         # upc
            'NULL',                                             # ean
            (Q $r.'Item Description'),                          # title
            'NULL',                                             # description
            (Q $r.Brand),                                       # brand
            (Q $r.'Seller Category'),                           # category
            'NULL',                                             # subcategory
            (Num $r.'Unit Retail'),                             # msrp
            (Num $r.'Unit Cost'),                               # unit_cost
            (Q $r.Condition),                                   # condition
            (Int $r.Qty),                                       # qty_in_manifest
            (Q $r.'Seller Category'),                           # seller_category
            'NULL',                                             # product_class
            'NULL',                                             # order_number
            'NULL',                                             # pallet_id
            (Q $r.'NSL Lot #'),                                 # lot_id
            "N'$sourceSql'",                                    # source_manifest
            (Q $r.'NSL Lot #')                                  # source_pallet_ref
        )
        "(" + ($vals -join ',') + ")"
    }
    [void]$sb.AppendLine(($valueLines -join ",`n") + ';')
}

[void]$sb.AppendLine(@"
DECLARE @actions TABLE (action varchar(10));
MERGE dbo.lpn_catalog AS t
USING #lpn_staging AS s ON t.lpn = s.lpn
WHEN MATCHED THEN UPDATE SET
    asin = s.asin, upc = s.upc, ean = s.ean, title = s.title,
    description = s.description, brand = s.brand, category = s.category,
    subcategory = s.subcategory, msrp = s.msrp, unit_cost = s.unit_cost,
    condition = s.condition, qty_in_manifest = s.qty_in_manifest,
    seller_category = s.seller_category, product_class = s.product_class,
    order_number = s.order_number, pallet_id = s.pallet_id, lot_id = s.lot_id,
    source_manifest = s.source_manifest, source_pallet_ref = s.source_pallet_ref,
    last_seen_at = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
    (lpn, asin, upc, ean, title, description, brand, category, subcategory,
     msrp, unit_cost, condition, qty_in_manifest, seller_category, product_class,
     order_number, pallet_id, lot_id, source_manifest, source_pallet_ref)
VALUES
    (s.lpn, s.asin, s.upc, s.ean, s.title, s.description, s.brand, s.category, s.subcategory,
     s.msrp, s.unit_cost, s.condition, s.qty_in_manifest, s.seller_category, s.product_class,
     s.order_number, s.pallet_id, s.lot_id, s.source_manifest, s.source_pallet_ref)
OUTPUT `$action INTO @actions;

SELECT
    SUM(CASE WHEN action = 'INSERT' THEN 1 ELSE 0 END) AS inserted,
    SUM(CASE WHEN action = 'UPDATE' THEN 1 ELSE 0 END) AS updated
FROM @actions;
"@)

$batchSql = $sb.ToString()
Write-Host ""
Write-Host ("SQL batch size: {0:N0} characters" -f $batchSql.Length)

# --- Run --------------------------------------------------------------------
Write-Host "Connecting to sql-nsl-prod-nc5h2y / sqldb-nsl-prod via Entra ..."
$token = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
if (-not $token) { throw "Failed to acquire Entra token via 'az account get-access-token'" }

Write-Host "Running batch (CREATE staging + $([Math]::Ceiling($dedup.Count / $batchSize)) INSERT batches + MERGE) ..."
$start = Get-Date
$result = Invoke-Sqlcmd `
    -ServerInstance 'sql-nsl-prod-nc5h2y.database.windows.net' `
    -Database 'sqldb-nsl-prod' `
    -AccessToken $token `
    -Query $batchSql `
    -QueryTimeout 300
$elapsed = ((Get-Date) - $start).TotalSeconds

Write-Host ""
Write-Host "Done in $('{0:N1}' -f $elapsed) seconds."
Write-Host "  inserted: $($result.inserted)"
Write-Host "  updated:  $($result.updated)"
