<#
.SYNOPSIS
    Provisions North State Liquidators mailboxes on the TenantIQ Pro M365 tenant.

.DESCRIPTION
    Creates norm@ and rob@northstateliquidators.com as licensed users (Microsoft 365
    Business Basic) and hello@ / wholesale@ / sales@ as shared mailboxes with both
    Norm and Rob granted FullAccess + SendAs.

    SAFE BY DESIGN:
      - Hard-coded tenant ID guard. Aborts if connected to anything other than
        TenantIQ Pro (d9b645c3-3587-4cd4-be9b-1a8d405c92ad). Cannot accidentally
        run against Surya, customer tenants, or a dev tenant.
      - Idempotent. Re-running after partial completion is safe.
      - Prereq gates (domain verification, license purchase) stop the script
        cleanly with next-step guidance if not yet met.

.PREREQUISITES (manual, via https://admin.microsoft.com)
    1. Purchase 2 x Microsoft 365 Business Basic licenses
       (Billing > Purchase services > search "Business Basic", $6/user/mo annual).
    2. Add northstateliquidators.com as a custom domain and verify it
       (Settings > Domains > Add domain). Verification TXT record goes at GoDaddy;
       Set-GoDaddyDns.ps1 in this repo can place it.

.PARAMETER WhatIf
    Preview changes without executing.

.EXAMPLE
    .\Provision-NSLMailboxes.ps1 -WhatIf
    .\Provision-NSLMailboxes.ps1
#>

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param()

$ErrorActionPreference = 'Stop'

# ============================================================================
# CONFIGURATION
# ============================================================================
$EXPECTED_TENANT_ID = 'd9b645c3-3587-4cd4-be9b-1a8d405c92ad'   # TenantIQ Pro
$EXPECTED_TENANT_NAME = 'tenantiqpro.com'
$NSL_DOMAIN = 'northstateliquidators.com'
$BASIC_SKU_PART_NUMBER = 'O365_BUSINESS_ESSENTIALS'   # = M365 Business Basic

$Users = @(
    @{ UPN = "norm@$NSL_DOMAIN"; First = 'Norman'; Last = 'Turner'; Display = 'Norman Turner'; Nick = 'norm' }
    @{ UPN = "rob@$NSL_DOMAIN";  First = 'Rob';    Last = 'TeCarr'; Display = 'Rob TeCarr';    Nick = 'rob'  }
)

$SharedAliases = @('hello', 'wholesale', 'sales')

# ============================================================================
# HELPERS
# ============================================================================
function New-TempPassword {
    param([int]$Length = 18)
    $chars = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ0123456789!@#%&*'
    -join ((1..$Length) | ForEach-Object { $chars[(Get-Random -Maximum $chars.Length)] })
}

function Assert-Tenant {
    $ctx = Get-MgContext
    if (-not $ctx) { throw 'No Graph context. Connect-MgGraph first.' }
    Write-Host "Signed in as: $($ctx.Account)" -ForegroundColor Cyan
    Write-Host "Tenant ID:    $($ctx.TenantId)" -ForegroundColor Cyan
    if ($ctx.TenantId -ne $EXPECTED_TENANT_ID) {
        throw "TENANT GUARD: expected $EXPECTED_TENANT_ID ($EXPECTED_TENANT_NAME), got $($ctx.TenantId). Refusing to proceed — sign in with a tenantiqpro.com global-admin account and rerun."
    }
    Write-Host "Tenant confirmed: $EXPECTED_TENANT_NAME" -ForegroundColor Green
}

# ============================================================================
# CONNECT
# ============================================================================
Import-Module Microsoft.Graph.Authentication -ErrorAction Stop
Import-Module Microsoft.Graph.Users -ErrorAction Stop

Write-Host "`n=== Connecting to Microsoft Graph ===" -ForegroundColor Cyan
Connect-MgGraph -Scopes 'User.ReadWrite.All','Organization.Read.All','Directory.ReadWrite.All','Domain.Read.All' -NoWelcome

Assert-Tenant

# ============================================================================
# PREREQ 1: Domain verified
# ============================================================================
Write-Host "`n=== Checking domain: $NSL_DOMAIN ===" -ForegroundColor Cyan
$domain = $null
try {
    $domain = Invoke-MgGraphRequest -Method GET -Uri "https://graph.microsoft.com/v1.0/domains/$NSL_DOMAIN" -ErrorAction Stop
} catch {
    # 404 means domain not added yet
}

if (-not $domain -or -not $domain.isVerified) {
    Write-Host "PREREQ MISSING: $NSL_DOMAIN is not yet a verified custom domain." -ForegroundColor Red
    Write-Host @"
Next steps:
  1. Open https://admin.microsoft.com/Adminportal/Home#/Domains
  2. Click "Add domain" -> enter $NSL_DOMAIN
  3. Copy the MS=msXXXXXXXX TXT verification value shown on screen
  4. Use Set-GoDaddyDns.ps1 (or GoDaddy UI) to add that TXT record at the apex of $NSL_DOMAIN
  5. Click "Verify" in the M365 admin center
  6. Re-run this script
"@ -ForegroundColor Yellow
    Disconnect-MgGraph -ErrorAction SilentlyContinue
    exit 1
}
Write-Host "Domain verified." -ForegroundColor Green

# ============================================================================
# PREREQ 2: Business Basic licenses purchased
# ============================================================================
Write-Host "`n=== Checking Business Basic licenses ===" -ForegroundColor Cyan
$skus = (Invoke-MgGraphRequest -Method GET -Uri 'https://graph.microsoft.com/v1.0/subscribedSkus').value
$basic = $skus | Where-Object { $_.skuPartNumber -eq $BASIC_SKU_PART_NUMBER }

if (-not $basic) {
    Write-Host "PREREQ MISSING: Microsoft 365 Business Basic not on this tenant." -ForegroundColor Red
    Write-Host @"
Next steps:
  1. Open https://admin.microsoft.com/#/catalog
  2. Search "Microsoft 365 Business Basic" (`$6/user/mo, annual commitment)
  3. Purchase 2 seats (enter payment info -- this is billed to TenantIQ Pro LLC)
  4. Re-run this script
"@ -ForegroundColor Yellow
    Disconnect-MgGraph -ErrorAction SilentlyContinue
    exit 1
}

$available = $basic.prepaidUnits.enabled - $basic.consumedUnits
Write-Host "Business Basic: $($basic.consumedUnits)/$($basic.prepaidUnits.enabled) assigned; $available available" -ForegroundColor Green
if ($available -lt $Users.Count) {
    throw "Only $available Business Basic seat(s) available; need $($Users.Count). Purchase additional seats and rerun."
}

# ============================================================================
# STEP 1: Create users + assign licenses
# ============================================================================
Write-Host "`n=== Creating user accounts ===" -ForegroundColor Cyan
$createdCreds = @()

foreach ($u in $Users) {
    $existing = Get-MgUser -Filter "userPrincipalName eq '$($u.UPN)'" -ErrorAction SilentlyContinue

    if ($existing) {
        Write-Host "User $($u.UPN) exists (Id: $($existing.Id)) - skipping creation" -ForegroundColor Yellow
        $userId = $existing.Id
    } else {
        $tempPass = New-TempPassword
        if ($PSCmdlet.ShouldProcess($u.UPN, 'Create user')) {
            $newUser = New-MgUser `
                -UserPrincipalName $u.UPN `
                -MailNickname $u.Nick `
                -DisplayName $u.Display `
                -GivenName $u.First `
                -Surname $u.Last `
                -AccountEnabled `
                -UsageLocation 'US' `
                -PasswordProfile @{ ForceChangePasswordNextSignIn = $true; Password = $tempPass } `
                -ErrorAction Stop
            $userId = $newUser.Id
            $createdCreds += [PSCustomObject]@{ UPN = $u.UPN; TempPassword = $tempPass }
            Write-Host "Created $($u.UPN) (Id: $userId)" -ForegroundColor Green
        } else {
            continue
        }
    }

    # Assign Business Basic license (idempotent - no-op if already assigned)
    if ($PSCmdlet.ShouldProcess($u.UPN, 'Assign Business Basic license')) {
        try {
            Set-MgUserLicense -UserId $userId `
                -AddLicenses @(@{ SkuId = $basic.skuId }) `
                -RemoveLicenses @() -ErrorAction Stop | Out-Null
            Write-Host "Licensed $($u.UPN) (Business Basic)" -ForegroundColor Green
        } catch {
            if ($_.Exception.Message -match 'conflict|already') {
                Write-Host "License already present on $($u.UPN)" -ForegroundColor Yellow
            } else { throw }
        }
    }
}

# ============================================================================
# STEP 2: Shared mailboxes for hello@, wholesale@, sales@
# ============================================================================
Write-Host "`n=== Creating shared mailboxes via Exchange Online ===" -ForegroundColor Cyan
Write-Host "Note: Exchange may take 5-15 minutes to provision new user mailboxes after licensing." -ForegroundColor Yellow

Import-Module ExchangeOnlineManagement -ErrorAction Stop
Connect-ExchangeOnline -ShowBanner:$false -ErrorAction Stop

try {
    foreach ($alias in $SharedAliases) {
        $smtp = "$alias@$NSL_DOMAIN"
        $mb = Get-Mailbox -Identity $smtp -ErrorAction SilentlyContinue

        if ($mb) {
            Write-Host "Shared mailbox $smtp exists (type: $($mb.RecipientTypeDetails))" -ForegroundColor Yellow
            if ($mb.RecipientTypeDetails -ne 'SharedMailbox') {
                if ($PSCmdlet.ShouldProcess($smtp, 'Convert to SharedMailbox')) {
                    Set-Mailbox -Identity $smtp -Type Shared
                    Write-Host "Converted $smtp to SharedMailbox" -ForegroundColor Green
                }
            }
        } else {
            if ($PSCmdlet.ShouldProcess($smtp, 'Create shared mailbox')) {
                New-Mailbox -Shared -Name $alias -DisplayName $alias -PrimarySmtpAddress $smtp | Out-Null
                Write-Host "Created shared mailbox $smtp" -ForegroundColor Green
            }
        }

        # Grant FullAccess + SendAs to Norm and Rob
        foreach ($u in $Users) {
            if ($PSCmdlet.ShouldProcess("$smtp -> $($u.UPN)", 'Grant FullAccess + SendAs')) {
                Add-MailboxPermission -Identity $smtp -User $u.UPN `
                    -AccessRights FullAccess -InheritanceType All `
                    -AutoMapping $true -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
                Add-RecipientPermission -Identity $smtp -Trustee $u.UPN `
                    -AccessRights SendAs -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
            }
        }
        Write-Host "Granted FullAccess + SendAs on $smtp to Norm and Rob" -ForegroundColor Green
    }
} finally {
    Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue
}

# ============================================================================
# OUTPUT SUMMARY
# ============================================================================
Write-Host "`n=== Provisioning complete ===" -ForegroundColor Green
if ($createdCreds.Count -gt 0) {
    Write-Host "`n*** TEMPORARY PASSWORDS -- CAPTURE NOW, THEY WILL NOT BE SHOWN AGAIN ***" -ForegroundColor Magenta
    $createdCreds | Format-Table -AutoSize
    Write-Host "Users will be forced to change password at first sign-in." -ForegroundColor Cyan
}

Write-Host @"

Next steps:
  - Add MX, SPF, DKIM, DMARC, Autodiscover DNS records at GoDaddy
    (values from M365 admin > Domains > $NSL_DOMAIN > DNS records).
    Set-GoDaddyDns.ps1 in this repo can automate that.
  - Set up Outlook on Norm and Rob's phones + laptops using their
    @$NSL_DOMAIN UPN and temp password.
  - Test a send from norm@ and receipt at hello@ -> Norm.
"@ -ForegroundColor Cyan

Disconnect-MgGraph -ErrorAction SilentlyContinue
