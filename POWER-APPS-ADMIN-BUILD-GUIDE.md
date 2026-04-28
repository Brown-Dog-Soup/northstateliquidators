# NSL Admin Portal — Power Apps Build Guide

**Goal:** A second canvas app (`NSL Admin`) that lets you manage pallets — create new ones (auto-numbered or custom-named), choose whether each is sold as a lot or individualized, edit items, and upload photos for either a whole pallet or a specific item.

This is a sibling to the receiving app (`NSL Scan`). They live in the same Power Apps environment, talk to the same Azure SQL DB, and share the same `nsl_api` connection.

**Prereq:** the `NSL Scan` app is already built and working, the SQL stored procs from `db/power-apps-procs.sql` and `db/admin-portal-additions.sql` are applied, and the Azure SQL connection is reusable.

Estimated build time: **~75–90 min** the first time.

---

## Phase 1 — Create the canvas app

1. Go to **https://make.powerapps.com**, environment **TenantIQ Pro (default)**.
2. **+ Create** → **Blank app** → **Blank canvas app** → name `NSL Admin` · format **Tablet** → **Create**.
3. You land on Studio with a blank `Screen1`.

---

## Phase 2 — Reuse the existing SQL connection

1. Top toolbar → **Add data** → search **SQL Server** → click your existing connection (it shows `nsl_api` and the dev server name).
2. Pick these tables/views:
   - `[dbo].[manifests]`
   - `[dbo].[line_items]`
   - `[dbo].[lpn_catalog]`
   - `[dbo].[v_pallets]` *(new — created by the admin SQL additions)*
3. Click **Connect**.

---

## Phase 3 — Add Azure Blob Storage connector for photos

Photos for pallets and items go into the existing `scan-photos` blob container. Power Apps' Azure Blob Storage connector handles the upload natively.

**Get a SAS token (one-time):**

```powershell
$expiry = (Get-Date).AddYears(2).ToString('yyyy-MM-ddTHH:mm:ssZ')
$sas = az storage container generate-sas --account-name stnslprodoofua53czivdq `
       --name scan-photos --permissions rwl --expiry $expiry --auth-mode login `
       --as-user --output tsv
$sas | Set-Clipboard
Write-Host "SAS token (rwl, 2-yr expiry) copied to clipboard."
```

Or use the storage account key (faster):

```powershell
$key = az storage account keys list -g rg-nsl-prod -n stnslprodoofua53czivdq --query '[0].value' -o tsv
$key | Set-Clipboard
Write-Host "Storage account key copied to clipboard. (Key auth — easier than SAS for Power Apps.)"
```

**In Studio:**

1. Top toolbar → **Add data** → search **Azure Blob Storage** → click it.
2. **Authentication type:** **Access Key**.
3. **Azure Storage Account name:** `stnslprodoofua53czivdq`.
4. **Azure Storage Account Access Key:** paste from clipboard.
5. **Connect**.

When prompted to pick blobs, check the **`scan-photos`** container.

---

## Phase 4 — Screen 1: Pallets list

This is the home screen — a gallery of every pallet with its key stats.

**4.1 Rename `Screen1` → `scrPallets`** (right-click in tree → Rename).

**4.2 Set the screen Fill** (in the formula bar with property dropdown on `Fill`):
```
ColorValue("#F5F0E6")
```

**4.3 Add the brand bar** — same pattern as the receiving app:
- **+ Insert** → **Rectangle** → name `headerbar`. Position 0,0 · Width 1366 · Height 80 · Color `#002868`.
- **+ Insert** → **Label** → name `lblTitle`. Position 24,20 · Width 1318 · Height 40 · Text `"NSL Admin — Pallets"` · Color `#F9D71C` · Size 28 · FontWeight Bold.

**4.4 Add the "+ New Pallet" button.**
- **+ Insert** → **Button** → name `btnNewPallet`.
- Position 1100,110 · Width 220 · Height 60.
- **Text:** `"+ NEW PALLET"`
- **Fill:** `ColorValue("#CC0000")` · **Color:** White · **Size:** 18 · **FontWeight:** Bold.
- **OnSelect** (paste the whole formula):
  ```
  Set(
    newPallet,
    First(
      'sp_CreateManifest'.Run(
        Coalesce(txtNewPalletName.Text, ""),
        "",   // source
        "",   // pallet_reference
        "",   // notes
        Blank()  // total_cost
      ).ResultSets.Table1
    )
  );
  Notify("Created: " & newPallet.display_name, NotificationType.Success);
  Reset(txtNewPalletName);
  Refresh(v_pallets);
  Set(currentPalletId, newPallet.id);
  Navigate(scrPalletDetail, ScreenTransition.Cover)
  ```
- **Note:** if `'sp_CreateManifest'.Run(...)` errors with "function does not exist", this is the same stored-proc-syntax issue that the receiving app had. Fall back to using `Patch(manifests, Defaults(manifests), {...})` directly with `id: GUID()` and let the auto-number happen in code (compute `pallet_number: CountRows(manifests) + 1`). I'll write that fallback formula if you hit it.

**4.5 Add the "new pallet name" input** (above or to the left of the button).
- **+ Insert** → **Text input** → name `txtNewPalletName`.
- Position 60,110 · Width 1020 · Height 60 · Size 18.
- **HintText:** `"Custom name for this pallet (or leave blank to auto-number)"`
- **Default:** `""`

**4.6 Add the pallets gallery.**
- **+ Insert** → **Vertical gallery** → name `galPallets`.
- Position 60,200 · Width 1246 · Height 760.
- **Items** formula:
  ```
  SortByColumns(
    Filter(v_pallets, status <> 'sold'),
    "received_date",
    Descending
  )
  ```
- **TemplateSize:** `120`
- **Layout:** Title, subtitle, body (use Image and label layout if you want the pallet photo).

**4.7 Customize each gallery row.** Click the first item template to enter edit mode, add:
- **Image control** at left of row: `Image = If(IsBlank(ThisItem.photo_url), SampleImage, ThisItem.photo_url)`. Size ~100×100.
- **Title label:** `Text = ThisItem.display_name & " — " & Coalesce(Text(ThisItem.received_date, "[$-en-US]mm/dd"), "")`. Size 18 · Bold.
- **Subtitle label:** `Text = "Items: " & ThisItem.item_count & " · Units: " & Coalesce(Text(ThisItem.unit_count), "0") & " · Est. resale: $" & Text(Coalesce(ThisItem.total_est_resale, 0), "[$-en-US]#,##0.00")`. Size 14 · gray.
- **Status pill label:** `Text = Upper(ThisItem.sell_mode)`, Fill = colored by mode (e.g., `Switch(ThisItem.sell_mode, "lot", ColorValue("#F9D71C"), "individual", ColorValue("#4ADE80"), "mixed", ColorValue("#F7941D"), ColorValue("#888888"))`).
- **OnSelect of the gallery item:**
  ```
  Set(currentPalletId, ThisItem.manifest_id);
  Navigate(scrPalletDetail)
  ```

---

## Phase 5 — Screen 2: Pallet detail

A new screen with toggle for sell mode, photo upload for the pallet, and a gallery of its line_items.

**5.1 Add a new screen.** Top → **+ New screen** → **Blank** → rename `scrPalletDetail`.

**5.2 Brand bar** — same pattern as scrPallets but title formula:
```
"Pallet: " & LookUp(v_pallets, manifest_id = currentPalletId).display_name
```

**5.3 Back button** to scrPallets:
- Top-left button: text `"← BACK TO PALLETS"`, OnSelect: `Navigate(scrPallets, ScreenTransition.UnCover)`.

**5.4 Sell-mode toggle — three buttons.**
- For **each** of three buttons (`btnLot`, `btnIndividual`, `btnMixed`):
  - Position them in a row at Y=120.
  - Width 250 · Height 70 · Size 18 · Bold.
  - Texts: `"SELL AS LOT"`, `"INDIVIDUALIZE"`, `"MIXED"`.
  - **Fill** formula (highlights the currently-selected mode):
    ```
    If(LookUp(v_pallets, manifest_id = currentPalletId).sell_mode = "lot",
       ColorValue("#CC0000"),
       ColorValue("#888888"))
    ```
    *(adjust the `"lot"` to match each button.)*
  - **OnSelect** for `btnLot`:
    ```
    'sp_SetSellMode'.Run(currentPalletId, "lot");
    Refresh(v_pallets);
    Notify("Sell mode: Lot", NotificationType.Success)
    ```
    Same pattern for `btnIndividual` (`"individual"`) and `btnMixed` (`"mixed"`).

**5.5 Pallet photo upload area.**
- **+ Insert** → **Image** → name `imgPalletHero`. Position 60,220 · Width 400 · Height 300.
- **Image** = `Coalesce(LookUp(v_pallets, manifest_id = currentPalletId).photo_url, SampleImage)`.
- **+ Insert** → **Add picture** control (gives you a "Browse" button + camera) → name `pickPalletPhoto`. Position 60,540 · Width 400.
- **+ Insert** → **Button** → name `btnUploadPalletPhoto`. Position 60,610 · Width 400 · Height 50.
- **Text:** `"UPLOAD PALLET PHOTO"`. **DisplayMode:** `If(IsBlank(pickPalletPhoto.Media), DisplayMode.Disabled, DisplayMode.Edit)`.
- **OnSelect:**
  ```
  Set(uploadName, "pallets/" & currentPalletId & ".jpg");
  Set(uploadResult,
      AzureBlobStorage.CreateBlockBlobV3("scan-photos", uploadName, pickPalletPhoto.Media)
  );
  Patch(
    manifests,
    LookUp(manifests, id = currentPalletId),
    { photo_url: uploadResult.Path }
  );
  Refresh(v_pallets);
  Notify("Pallet photo uploaded", NotificationType.Success);
  Reset(pickPalletPhoto)
  ```

**5.6 Items gallery on the right side.**
- **+ Insert** → **Vertical gallery** → name `galItems`.
- Position 500,220 · Width 800 · Height 700.
- **Items:** `Filter(line_items, manifest_id = currentPalletId)`
- **TemplateSize:** `100`.
- Each row shows: photo thumb, title, qty, condition, "individual" / "lot" toggle.
- **OnSelect** of the row item:
  ```
  Set(currentItemId, ThisItem.id);
  Navigate(scrItemDetail)
  ```

---

## Phase 6 — Screen 3: Item detail (with photo upload)

Optional but useful — lets you edit a single line_item (photo, condition, notes, price).

**6.1 Add a new screen `scrItemDetail`.**

**6.2 Brand bar.**

**6.3 Back to pallet:** OnSelect: `Navigate(scrPalletDetail, ScreenTransition.UnCover)`.

**6.4 Item photo upload** — same pattern as the pallet photo:
- **Add picture** named `pickItemPhoto`.
- **Image** showing current photo: `Coalesce(LookUp(line_items, id = currentItemId).photo_blob_url, SampleImage)`.
- **Upload button OnSelect:**
  ```
  Set(uploadName, "items/" & currentItemId & ".jpg");
  Set(uploadResult,
      AzureBlobStorage.CreateBlockBlobV3("scan-photos", uploadName, pickItemPhoto.Media)
  );
  Patch(
    line_items,
    LookUp(line_items, id = currentItemId),
    { photo_blob_url: uploadResult.Path }
  );
  Notify("Item photo uploaded", NotificationType.Success);
  Reset(pickItemPhoto)
  ```

**6.5 Editable fields:** condition dropdown, qty input, est_resale input, notes text area.
- Bind each control's `Default` to the corresponding `LookUp(line_items, id = currentItemId).<field>`.
- Add a **Save** button:
  ```
  Patch(
    line_items,
    LookUp(line_items, id = currentItemId),
    {
      condition: ddCondition.Selected.Value,
      qty: Value(txtQty.Text),
      est_resale: Value(txtResale.Text),
      notes: txtNotes.Text
    }
  );
  Notify("Saved", NotificationType.Success);
  Navigate(scrPalletDetail, ScreenTransition.UnCover)
  ```

---

## Phase 7 — Save, publish, share

1. Save (`Ctrl+S`).
2. Top right → **Publish** (or File → Save → Publish).
3. **Settings → Sharing →** add the same users (`norm@`, `rob@`) plus yourself as **Co-owner**.
4. Norm and Rob need a Power Apps Premium per-app license to use this app — same as the receiving app, $5/user/mo per app. If you bought one license per user for the receiving app, you'll need a separate per-app license for this admin app, OR upgrade to **Power Apps Premium per user** at $20/user/mo to cover unlimited apps.

---

## Phase 8 — How the workflows actually run

**Norm gets a new pallet from a truck:**
1. Open **NSL Admin** on iPad.
2. Either type a name like "Tuesday Truck Apparel Pallet" or leave blank → tap **+ NEW PALLET**.
3. App auto-creates the pallet (`Pallet #042` if blank) and navigates to the detail screen.
4. Tap the camera icon → take a photo of the loaded pallet → tap **UPLOAD PALLET PHOTO**.
5. Switch to **NSL Scan** app, the new pallet is automatically the most-recent so all scans go to it.
6. Scan items as they come off — each scan gets a row in `line_items` with `manifest_id` = this pallet.
7. After unloading, return to NSL Admin → tap into the pallet → tap **SELL AS LOT** or **INDIVIDUALIZE** based on the value/effort tradeoff.

**Mixed-mode workflow** (some items as lot, some individualized):
- Tap **MIXED** on the pallet.
- Open each individual line_item (drill into Item Detail screen) → flip a per-item flag (we can add a `sell_individually BIT` column later if you want true mixed mode).
- For now, MIXED just signals intent; the Shopify-push function (Phase 4 of BuildoutDB.md, not yet built) will read the per-item flag.

---

## Common gotchas

| Symptom | Fix |
|---|---|
| "AzureBlobStorage isn't a valid name" | Connector wasn't added. Re-run Phase 3. |
| `'sp_CreateManifest'.Run(...)` errors with "function does not exist" | Stored procedures aren't enabled in this Power Apps version. Fall back to `Patch(manifests, Defaults(manifests), {id: GUID(), display_name: txtNewPalletName.Text, pallet_number: CountRows(manifests) + 1, status: "receiving", sell_mode: "undecided", created_at: Now()})`. |
| `'sp_SetSellMode'.Run(...)` errors | Same — fall back to `Patch(manifests, LookUp(manifests, id = currentPalletId), {sell_mode: "lot"})`. |
| Image control shows broken-link X after upload | The blob upload returned a path but the URL needs the storage account hostname. Update the Patch to use `"https://stnslprodoofua53czivdq.blob.core.windows.net/scan-photos/" & uploadName` instead of `uploadResult.Path`. |
| Photos don't load when viewing in Norm/Rob's app | The blob container is private. Either generate a SAS-signed URL per item view, or set the container to `Blob` public access (less secure but simpler for an internal-only admin tool). |

---

## What this app is NOT yet doing (next phases)

- **Shopify push** — when a pallet's sell_mode is set to `lot` or `individualized`, an Azure Function should fire and create the Shopify product(s). That's the next BuildoutDB phase.
- **Photo gallery view** — currently each item has one photo. Multi-photo support requires a `line_item_photos` child table.
- **Bulk item edit** — selecting multiple items in the gallery to set the same condition/price.
- **Manifest XLSX upload directly from the admin portal** — currently we POST the XLSX via PowerShell to `/api/import-manifest`. A button in the admin app could do the same (Power Apps' HTTP request action calls our `/api/import-manifest` endpoint).

---

## Files referenced

| File | What it is |
|---|---|
| `db/admin-portal-additions.sql` | Schema additions (display_name, photo_url, pallet_number) + sequence + sp_CreateManifest + sp_SetSellMode + v_pallets view |
| `db/power-apps-procs.sql` | The receiving-app stored procs (sp_LookupCode, sp_RecordScan, v_recent_scans) |
| `POWER-APPS-BUILD-GUIDE.md` | Sibling guide for the receiving app (NSL Scan) |
| `api/Functions/LookupFunction.cs` + `ScanFunction.cs` | HTTP fallback endpoints if Power Apps SQL connector ever has issues |
