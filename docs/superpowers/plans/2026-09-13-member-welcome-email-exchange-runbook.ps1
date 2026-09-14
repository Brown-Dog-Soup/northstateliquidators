# ============================================================================
# NSL member welcome email — Exchange Online scoping (RBAC for Applications)
# Run INTERACTIVELY in PowerShell 7 as Jeff (Global Admin on TenantIQ Pro).
# From the Claude Code prompt:  ! pwsh -NoProfile -File docs/superpowers/plans/2026-09-13-member-welcome-email-exchange-runbook.ps1
#
# What is already done (2026-09-13, via az):
#   - App registration NSL-Website-Mail  appId  5098ef2d-ace8-4503-a8c8-0daeaed197e5
#   - Enterprise app (service principal)  objId 5d415412-a5d2-4cdd-a3dc-88a6eb4ef9a7
#   - 24-month client secret -> SWA setting MAIL_CLIENT_SECRET (never printed)
#   - SWA settings MAIL_TENANT_ID / MAIL_CLIENT_ID / MAIL_FROM / MAIL_ENABLED=false
#   - NO Entra Mail.Send consent (deliberate: RBAC scope + Entra consent = UNION = no scoping)
#
# What this script does (idempotent where the cmdlets allow):
#   1. Mail-enabled security group NSL-Mail-Senders with exactly one member: hello@
#   2. Exchange service-principal pointer for the app
#   3. Management scope "members of NSL-Mail-Senders" + role assignment 'Application Mail.Send'
#   4. Proof: allowed for hello@, NOT allowed for Jeff's own mailbox
# Then wait ~30 minutes for the permission cache, run the smoke test at the bottom.
# ============================================================================
$ErrorActionPreference = 'Stop'
$APPID  = '5098ef2d-ace8-4503-a8c8-0daeaed197e5'
$SPOID  = '5d415412-a5d2-4cdd-a3dc-88a6eb4ef9a7'
$TENANT = 'd9b645c3-3587-4cd4-be9b-1a8d405c92ad'
$HELLO  = 'hello@northstateliquidators.com'
$JEFF   = 'jeffrey.blanchard@tenantiqpro.com'

Import-Module ExchangeOnlineManagement
# -Device: device-code sign-in (prints a code + URL). The default WAM broker
# needs a console window handle and fails under Claude Code's `!` prompt.
Connect-ExchangeOnline -Device -ShowBanner:$false

# --- 0. preflight: hello@ must be a shared mailbox ---------------------------
$mbx = Get-Mailbox -Identity $HELLO
"hello@ = $($mbx.RecipientTypeDetails)"
if ($mbx.RecipientTypeDetails -ne 'SharedMailbox') { throw "hello@ is not a SharedMailbox — stop." }

# --- 1. group ------------------------------------------------------------------
$grp = Get-DistributionGroup -Identity 'NSL-Mail-Senders' -ErrorAction SilentlyContinue
if (-not $grp) {
    $grp = New-DistributionGroup -Name 'NSL-Mail-Senders' -DisplayName 'NSL Mail Senders (app scope)' `
        -Alias 'nsl-mail-senders' -PrimarySmtpAddress 'nsl-mail-senders@northstateliquidators.com' `
        -Type Security -Members $HELLO -MemberJoinRestriction Closed -MemberDepartRestriction Closed
    Set-DistributionGroup -Identity 'NSL-Mail-Senders' -HiddenFromAddressListsEnabled $true
}
$members = Get-DistributionGroupMember -Identity 'NSL-Mail-Senders'
"NSL-Mail-Senders members:"; $members | Format-Table Name, PrimarySmtpAddress, RecipientType
if (@($members).Count -ne 1 -or $members[0].PrimarySmtpAddress -ne $HELLO) { throw "Group must contain exactly hello@ — fix membership before continuing." }

# --- 2. service principal pointer ---------------------------------------------
if (-not (Get-ServicePrincipal -Identity $APPID -ErrorAction SilentlyContinue)) {
    New-ServicePrincipal -AppId $APPID -ObjectId $SPOID -DisplayName 'NSL-Website-Mail' | Out-Null
}
"Exchange service principal: ok"

# --- 3. scope + role assignment -----------------------------------------------
# One-time tenant prerequisite: custom management scopes/role assignments need
# the org "hydrated". Irreversible, benign, standard. Can take a few minutes to
# take effect — if New-ManagementScope still errors right after, re-run this script.
if ((Get-OrganizationConfig).IsDehydrated) {
    "Running Enable-OrganizationCustomization (one-time)…"
    Enable-OrganizationCustomization
    Start-Sleep -Seconds 60
}
if (-not (Get-ManagementScope -Identity 'NSL Mail Senders Scope' -ErrorAction SilentlyContinue)) {
    New-ManagementScope -Name 'NSL Mail Senders Scope' `
        -RecipientRestrictionFilter "MemberOfGroup -eq '$($grp.DistinguishedName)'" | Out-Null
}
# Exchange only lets you assign a role to an app if your own role group holds a
# DELEGATING assignment for that role. Organization Management gets those for
# the classic roles but not always for the newer "Application …" roles.
$who = Get-RoleGroupMember 'Organization Management' | Select-Object -ExpandProperty PrimarySmtpAddress
"Organization Management members: $($who -join ', ')"
$deleg = Get-ManagementRoleAssignment -Role 'Application Mail.Send' -Delegating:$true -ErrorAction SilentlyContinue |
         Where-Object { $_.RoleAssigneeName -eq 'Organization Management' }
if (-not $deleg) {
    "Adding delegating assignment of 'Application Mail.Send' to Organization Management…"
    New-ManagementRoleAssignment -Name 'Application Mail.Send-Organization Management-Delegating' `
        -Role 'Application Mail.Send' -SecurityGroup 'Organization Management' -Delegating | Out-Null
    Start-Sleep -Seconds 30
}
if (-not (Get-ManagementRoleAssignment -Identity 'NSL-Website-Mail to NSL Mail Senders' -ErrorAction SilentlyContinue)) {
    New-ManagementRoleAssignment -Name 'NSL-Website-Mail to NSL Mail Senders' `
        -Role 'Application Mail.Send' -App $APPID -CustomResourceScope 'NSL Mail Senders Scope' | Out-Null
}
"Role assignment: ok"

# --- 4. proof -------------------------------------------------------------------
"--- should be IN SCOPE (hello@):"
Test-ServicePrincipalAuthorization -Identity $APPID -Resource $HELLO | Format-Table
"--- should be NOT in scope (Jeff):"
Test-ServicePrincipalAuthorization -Identity $APPID -Resource $JEFF | Format-Table

Disconnect-ExchangeOnline -Confirm:$false
@"

DONE. Wait ~30 minutes, then smoke-test (needs the secret; get it from the SWA
settings in the Azure portal, or reset it and re-set MAIL_CLIENT_SECRET):

  `$SECRET = Read-Host -AsSecureString 'MAIL_CLIENT_SECRET' | ConvertFrom-SecureString -AsPlainText
  `$t = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$TENANT/oauth2/v2.0/token" -Body @{ grant_type='client_credentials'; client_id='$APPID'; client_secret=`$SECRET; scope='https://graph.microsoft.com/.default' }
  `$p = @{ message = @{ subject='NSL mail smoke test'; body=@{ contentType='HTML'; content='<p>NSL-Website-Mail works.</p>' }; toRecipients = ,@{ emailAddress=@{ address='$JEFF' } } } } | ConvertTo-Json -Depth 8 -Compress
  # Same percent-encoded form the site's code uses (Uri.EscapeDataString): hello%40…
  Invoke-RestMethod -Method Post -Uri 'https://graph.microsoft.com/v1.0/users/$($HELLO -replace '@','%40')/sendMail' -Headers @{ Authorization="Bearer `$(`$t.access_token)" } -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(`$p))   # expect 202 (no output)
  Invoke-RestMethod -Method Post -Uri 'https://graph.microsoft.com/v1.0/users/$JEFF/sendMail'  -Headers @{ Authorization="Bearer `$(`$t.access_token)" } -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(`$p))   # expect 403 ErrorAccessDenied
"@
