<#
.SYNOPSIS
    Pulls the Featured collection from the NSL Shopify dev store and rewrites
    the hunt-grid block in index.html with real product cards.

.DESCRIPTION
    Talks to Shopify Admin GraphQL via the local CLI proxy that `shopify app dev`
    starts. Requires `shopify app dev` to be running in the shopify-cli/ folder
    so the proxy at localhost:3457 is up.

    Replaces everything between `<!-- featured-start -->` and `<!-- featured-end -->`
    in index.html with hunt-card HTML built from the live Shopify data. Re-run
    any time Norm or Rob add/remove products tagged "featured" in the Shopify
    mobile app, or change prices.

.PARAMETER Endpoint
    Shopify CLI GraphiQL proxy URL. Default reads from .shopify-graphiql-url.txt
    in the working directory if it exists; otherwise must be passed.

.EXAMPLE
    .\Sync-NSLFeatured.ps1 -Endpoint 'http://localhost:3457/graphiql/graphql.json?key=...'
#>
[CmdletBinding()]
param(
    [string]$Endpoint,
    [string]$IndexHtml = (Join-Path $PSScriptRoot 'index.html'),
    [switch]$CommitAndPush
)

$ErrorActionPreference = 'Stop'

if (-not $Endpoint) {
    $cached = Join-Path $PSScriptRoot '.shopify-graphiql-url.txt'
    if (Test-Path $cached) { $Endpoint = (Get-Content $cached -Raw).Trim() }
    else { throw "No -Endpoint passed and no $cached cache. Pass the localhost:3457 graphiql URL printed by 'shopify app dev'." }
}

function Invoke-ShopifyGraphQL {
    param([string]$Query, $Variables)
    $body = @{ query = $Query; variables = $Variables } | ConvertTo-Json -Depth 10 -Compress
    $r = Invoke-RestMethod -Method Post -Uri $Endpoint -Headers @{ 'Content-Type' = 'application/json' } -Body $body
    if ($r.errors) { throw "GraphQL error: $($r.errors | ConvertTo-Json -Compress)" }
    return $r.data
}

Write-Host "Fetching Featured collection from Shopify..." -ForegroundColor Cyan
$query = @'
{
  collectionByHandle(handle: "featured") {
    id
    title
    products(first: 24) {
      edges {
        node {
          id
          title
          handle
          vendor
          tags
          variants(first: 1) {
            edges {
              node {
                price
                compareAtPrice
                inventoryQuantity
              }
            }
          }
          featuredMedia {
            preview {
              image {
                url(transform: { maxWidth: 800 })
                altText
              }
            }
          }
        }
      }
    }
  }
}
'@

$data = Invoke-ShopifyGraphQL -Query $query
$products = $data.collectionByHandle.products.edges.node
Write-Host "Pulled $($products.Count) product(s) from Featured." -ForegroundColor Green

# Build the hunt-card HTML
$storeDomain = 'north-state-liquidators-dev.myshopify.com'
$cards = New-Object System.Collections.Generic.List[string]

foreach ($p in $products) {
    $variant = $p.variants.edges[0].node
    $price = $variant.price
    $was = $variant.compareAtPrice
    $img = $p.featuredMedia.preview.image.url
    $alt = if ($p.featuredMedia.preview.image.altText) { $p.featuredMedia.preview.image.altText } else { $p.title }
    $url = "https://$storeDomain/products/$($p.handle)"

    $photoStyle = if ($img) {
        "style=`"background-image: url('$img'); background-size: cover; background-position: center;`""
    } else { '' }

    $priceFmt = '$' + ([decimal]$price).ToString('0.##')
    $wasFmt = if ($was) { '$' + ([decimal]$was).ToString('0.##') } else { $null }

    $note = ''
    if ($p.tags -contains 'open-box') { $note = 'Open box. Inspected and tested.' }
    elseif ($p.tags -contains 'refurbished') { $note = 'Manufacturer refurbished. Tested.' }
    elseif ($p.tags -contains 'sample') { $note = 'Sample product (POC).' }
    else { $note = 'Inspected at our Raleigh warehouse.' }

    $card = @"
    <a class="hunt-card" href="$url" target="_blank" rel="noopener" style="text-decoration:none; color:inherit;">
      <div class="photo" $photoStyle></div>
      <div class="price-tag">$priceFmt</div>
      <div class="meta">
        <h3>$([System.Web.HttpUtility]::HtmlEncode($p.title))</h3>
"@
    if ($wasFmt) {
        $card += "        <div class=`"was`">Was $wasFmt MSRP</div>`n"
    }
    $card += @"
        <div class="note">$note</div>
      </div>
    </a>
"@
    $cards.Add($card)
}

Add-Type -AssemblyName System.Web

$replacement = "<!-- featured-start -->`n" + ($cards -join "`n") + "`n    <!-- featured-end -->"

# Read index.html, splice between markers
$html = Get-Content $IndexHtml -Raw
$pattern = '(?s)<!-- featured-start -->.*?<!-- featured-end -->'
if ($html -notmatch $pattern) {
    throw "Markers <!-- featured-start --> ... <!-- featured-end --> not found in $IndexHtml. Add them first inside the hunt-grid div."
}

$newHtml = [regex]::Replace($html, $pattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $replacement })
Set-Content -Path $IndexHtml -Value $newHtml -NoNewline -Encoding UTF8

Write-Host "Wrote $($cards.Count) cards into $IndexHtml" -ForegroundColor Green

if ($CommitAndPush) {
    Push-Location $PSScriptRoot
    try {
        git add index.html
        git commit -m "Sync Featured products from Shopify ($($cards.Count) items)"
        git push
        Write-Host "Pushed to origin." -ForegroundColor Green
    } finally {
        Pop-Location
    }
}
