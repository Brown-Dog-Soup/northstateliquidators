# Power Apps Build Guide — NSL Receiving

**Goal:** Build the canvas app Norm and Rob use on an iPad to scan items into the inventory system. The HW0009 scanner types into a field, Power Apps looks up the item against the LPN catalog, the receiver confirms, and a `line_items` row is written to Azure SQL.

This guide takes you step-by-step through the Power Apps Studio. Estimated time: **45–60 min** the first time, faster on iterations.

---

## Prerequisites

You must have:

1. **A Power Apps license** that includes premium connectors. Cheapest: Power Apps **per-app license** ($10/user/month per app, billed only when an app is shared with users). For development, the Power Apps developer plan (free) works.
2. **Microsoft 365 sign-in** as `jeffrey.blanchard@tenantiqpro.com` (or any user in TenantIQpro.com tenant) — the Power Apps environment lives on the same Entra tenant.
3. **The SQL connection details below** ready to paste into the connector setup.

### SQL connection details (paste these into the connector wizard)

| Field | Value |
|---|---|
| Server name | `sql-nsl-prod-nc5h2y.database.windows.net` |
| Database name | `sqldb-nsl-prod` |
| Authentication type | **SQL Server Authentication** |
| Username | `nsl_api` |
| Password | *(retrieve with the command below — don't paste it in plain text into a chat or screenshot)* |

Retrieve the SQL password into your clipboard:

```powershell
$pwd = az staticwebapp appsettings list -g rg-nsl-website -n stapp-nsl-website --query "properties.SqlConnectionString" -o tsv
if ($pwd -match 'Password=([^;]+)') { $matches[1] | Set-Clipboard; Write-Host 'Password copied to clipboard.' }
```

---

## Phase 1 — Create the canvas app

1. Open **https://make.powerapps.com** in a browser. Sign in as `jeffrey.blanchard@tenantiqpro.com`.
2. Top-left, confirm the **environment** dropdown reads **TenantIQpro** (or similar — the org's default). If you see `(default)` and it doesn't say TenantIQpro, switch to the right one — DO NOT build in a customer's environment.
3. Left nav → **Create** → **Blank app** → **Blank canvas app** → **Create**.
4. Name: `NSL Receiving` · Format: **Tablet**.
5. Click **Create**. Studio opens with one blank screen called `Screen1`.

---

## Phase 2 — Add the Azure SQL data source

1. Left rail → **Data** icon (cylinder).
2. **+ Add data** → search **SQL Server** → click **SQL Server** (the Microsoft connector with the blue cylinder icon).
3. **Connect using:** **SQL Server Authentication**.
4. Paste the **server name**, **database name**, **username** (`nsl_api`), and **password** (clipboard).
5. **Connect**. You'll see a "Choose a table" dialog.
6. Check these boxes (we'll add the procs separately):
   - `[dbo].[lpn_catalog]`
   - `[dbo].[line_items]`
   - `[dbo].[manifests]`
   - `[dbo].[v_recent_scans]`
7. **Connect**. The Data panel now shows those four under your SQL connection.

Stored procedures (sp_LookupCode, sp_RecordScan) get called via `Sql.ExecuteProcedureV2` formula — no UI data-source binding needed.

---

## Phase 3 — Rename Screen1 and lay out the receiving screen

1. Left tree → click **Screen1** → **Rename** to `ReceivingScreen`.
2. **Insert** → **Rectangle** at the top (sets the brand bar).
   - Properties: `Fill = ColorValue("#002868")` (NC navy), `Height = 80`, `Width = Parent.Width`.
3. **Insert** → **Label** on top of the rectangle.
   - `Text = "NSL Receiving — " & First(manifests).pallet_reference`
   - `Color = ColorValue("#F9D71C")` (warehouse yellow)
   - `Font = Font.'Open Sans'`, `FontWeight = Bold`, `Size = 24`
4. **Insert** → **Text input** below the brand bar. **This is the scan field.**
   - Name: `txtScan`
   - `HintText = "Scan an LPN, UPC, or ASIN"`
   - `Default = ""`
   - `Width = 800`, `Height = 60`, `Size = 22`
   - **`OnChange = ` paste this:**
     ```powerfx
     If(
       Len(txtScan.Text) >= 6,
       Set(
         scanResult,
         First(
           'Sql.sp_LookupCode'.Run(txtScan.Text).ResultSets.Table1
         )
       );
       If(
         IsBlank(scanResult),
         Set(scanStatus, "miss"),
         Set(scanStatus, "hit")
       )
     )
     ```
   - The HW0009 scanner ends every read with an Enter keystroke, which will fire this OnChange. Sub-second roundtrip to Azure SQL.
5. **Insert** → **Container** (or rectangle group) below the scan input. This is the "lookup result" panel. Inside it, add labels bound to the `scanResult` variable:

   | Label | Text formula |
   |---|---|
   | Title | `If(scanStatus = "hit", scanResult.title, "No match — flag for manual entry")` |
   | Brand | `"Brand: " & Coalesce(scanResult.brand, "—")` |
   | Source | `"Match: " & If(IsBlank(scanResult), "miss", scanResult.match_source)` |
   | MSRP | `"MSRP: $" & Text(scanResult.msrp, "[$-en-US]#,##0.00")` |
   | Condition | `"Condition: " & Coalesce(scanResult.condition, "—")` |

6. **Insert** → **Drop-down** for condition override:
   - Name: `ddCondition`
   - `Items = ["new", "open_box", "damaged", "untested", "customer_return"]`
   - `Default = scanResult.condition`
7. **Insert** → **Number input** for quantity:
   - Name: `numQty`, `Default = 1`, `Min = 1`, `Max = 999`
8. **Insert** → **Camera control** (or Add Picture if no camera):
   - Name: `camPhoto`
9. **Insert** → **Button** "Confirm Scan":
   - Name: `btnConfirm`
   - `Fill = ColorValue("#CC0000")` (NC red)
   - `Color = White`, `Size = 24`, `Width = 300`, `Height = 80`
   - `DisplayMode = If(IsBlank(txtScan.Text), DisplayMode.Disabled, DisplayMode.Edit)`
   - **`OnSelect = ` paste this:**
     ```powerfx
     // 1. Save the photo to a Power Apps blob (skipped in v1 — add later)
     // 2. Call sp_RecordScan
     Set(
       recordResult,
       First(
         'Sql.sp_RecordScan'.Run(
           First(manifests).id,
           txtScan.Text,
           numQty.Value,
           ddCondition.Selected.Value,
           "",
           ""
         ).ResultSets.Table1
       )
     );
     // 3. Reset the form for the next scan
     Reset(txtScan);
     Reset(numQty);
     Set(scanResult, Blank());
     Set(scanStatus, Blank());
     Notify("Logged: " & recordResult.title, NotificationType.Success, 2000);
     // 4. Refocus the scan field for the next read
     SetFocus(txtScan)
     ```
10. **Insert** → **Gallery** (vertical) at the bottom — recent scans list:
    - `Items = SortByColumns(v_recent_scans, "created_at", Descending)`
    - `TemplateSize = 80`, show `title`, `qty`, `condition`, `created_at` in the row.

11. **OnVisible** for `ReceivingScreen`:
    ```powerfx
    SetFocus(txtScan);
    Set(scanResult, Blank());
    Set(scanStatus, Blank())
    ```

---

## Phase 4 — Save, publish, share

1. Top right → **Save** (`Ctrl+S`).
2. **File → Save → Publish** (after the save finishes).
3. **Settings → Sharing →** add `norm@northstateliquidators.com` and `rob@northstateliquidators.com` as **Users**. They'll need a Power Apps per-app license assigned to their accounts before the share takes effect ($10/user/mo, allocated via the M365 admin center → Billing → Purchase services → search "Power Apps per app").

---

## Phase 5 — Install on Norm and Rob's iPad

1. On the iPad: App Store → install **"Power Apps"** (made by Microsoft, blue tile).
2. Open the app → sign in with their `@northstateliquidators.com` account.
3. The shared `NSL Receiving` app appears in the list. Tap to open. Save to home screen for one-tap launch.
4. Pair the Tera HW0009 scanner via Bluetooth (settings on the scanner — see scanner manual). Once paired, anything it reads types into whichever field has focus on the iPad. The app's `OnVisible` keeps focus on the scan input by default.

---

## Phase 6 — Smoke test

Have Norm or Rob:

1. Stand next to a real Amazon pallet that matches the imported manifest (`AMZ_3PL_20251121_020`).
2. Open NSL Receiving.
3. Scan an LPN sticker.
4. App shows: title, brand, MSRP, condition, "Match: lpn".
5. Tap **Confirm Scan**.
6. Notification: "Logged: <product name>".
7. Recent scans gallery refreshes; the new row is at the top.

If the lookup says "miss" for an LPN that's clearly on the manifest, double-check the scanner is set to **Enable**+**Tab/Enter suffix** mode (the HW0009 default is Enter, which is what the OnChange handler expects).

---

## Common gotchas

| Symptom | Fix |
|---|---|
| `'Sql.sp_LookupCode' isn't a valid function` | The stored procedure isn't visible to the connection. Re-run `db/power-apps-procs.sql` and confirm `nsl_api` was granted EXECUTE. |
| OnChange fires once per character not once per scan | The scanner is set to "no suffix" mode. Reconfigure to send Enter (`\r\n`) at end-of-scan. HW0009 manual page 12. |
| Connection prompt loops | Wrong password. Re-paste from clipboard via the PowerShell snippet at top. |
| "Cannot connect to server" from Power Apps preview | The SQL Server firewall might not include Power Apps' service IP. We have `AllowAllAzureIps` (start/end 0.0.0.0) which covers Power Apps' Azure-internal callers. If you tightened that down later, re-add. |
| "Not authorized" when Norm or Rob open the app | Per-app license not assigned to their account. M365 admin center → Licenses → assign Power Apps per-app to their UPN. |

---

## Future enhancements (not v1)

- **Photo capture** — currently the `OnSelect` for Confirm passes `""` for the photo URL. Hook up `camPhoto.Photo` → upload to Azure Blob via the SWA's `/api/upload-scan-photo` endpoint, then pass the returned URL to `sp_RecordScan`. ~30 min build.
- **Manifest picker** — second screen that lists `manifests` and lets the receiver pick which pallet they're working against, instead of always using "most recent."
- **Offline mode** — Power Apps offline support is limited; if WiFi flakes in the warehouse, switch to the PWA path documented in `SCAN-UI-DECISION.md`.
- **Migrate to PWA** — when usage grows past 3 users, the per-app license cost ($30+/mo) crosses over the dev cost of a custom HTML/JS PWA. The supporting `/api/lookup` and `/api/scan` endpoints in `api/` already exist for the migration.

---

## Where the data lives

| What | Where |
|---|---|
| Scanned units (one row each) | `dbo.line_items` in `sqldb-nsl-prod` |
| Catalog of LPNs from imported manifests | `dbo.lpn_catalog` |
| Pallet headers | `dbo.manifests` |
| Photos (when wired up) | `scan-photos` blob container in `stnslprodoofua53czivdq` |
| Recent scans view | `dbo.v_recent_scans` |
