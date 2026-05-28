# Wishlist Part 2 — remaining items build log

Live checkpoint so a mid-build hang never loses the place. Update the status
table after each step. Single themed commit at the end, after verifying the
SQL applies, the API builds, and the flow works together.

Source: Rob's "Website Backend Wishlist - Part 2" email (2026-05-06).
Quick wins #1/#5/#7 + description plumbing already shipped 2026-05-28.

## Findings from the live DB (2026-05-28)

- **#5 "Sold" mystery — already fixed.** `v_inventory` status breakdown is
  available 2820 / on_pallet 92 / ghost 56 / **sold 0**. The earlier
  ghost-vs-sold fix already moved the 56 ghost-backstock items out of the
  "Sold" bucket. Nothing to build — confirm to Rob.
- **#4 data-pull — data gap, not a bug.** `lpn_catalog`: 354 rows have
  `product_image_url` (all of them LPN rows), 2614 LPN rows have none, and
  **all 2968 rows have empty `description`** (Amazon B-Stock manifests carry no
  description). Scan UI already renders image + description when present, and
  `sp_RecordScan` now persists UPCitemdb descriptions. So images/descriptions
  only appear for (a) LPN rows the image job enriched, (b) UPCitemdb hits. Full
  coverage = run the enrichment job wider + add description scraping — a data
  task, out of scope for code. Documented for Rob.
- **#2 auto number — mostly exists.** `manifests.pallet_number` +
  `dbo.seq_pallet_number` + `sp_CreateManifest` auto-name "Pallet #042" already
  there. Work = surface the box number prominently in the UI.
- Public `index.html` is fully static. For #6 "Live publishes to website" to be
  real, add a public read path and render live pallets there.

## Data model (one migration: `db/wishlist2-part2.sql`, idempotent)

`dbo.manifests` new columns:
- `publish_state VARCHAR(20) NOT NULL DEFAULT 'draft'`, CHECK in
  (`draft`,`live`,`ghost`,`sold`). **#6**
- `list_price DECIMAL(12,2) NULL` — published ask override. **#3**
- `sale_price DECIMAL(12,2) NULL` — when set + below list, strike list & show this. **#3**

Backfill: `is_ghost=1 → 'ghost'`, else `'draft'`.

`publish_state` is the master; keep `is_ghost`/`sold_at` in sync so existing
views keep working. New `sp_SetPublishState @manifest_id, @publish_state`:
- `ghost` → is_ghost=1, sold_at=COALESCE(sold_at,now); items untouched (inventory unaffected).
- `sold`  → is_ghost=0, sold_at=COALESCE(sold_at,now); set line_items.sold_at (inventory consumed).
- `live`/`draft` → is_ghost=0, sold_at=NULL, clear line_items.sold_at.

Views/procs:
- `v_pallets` += publish_state, list_price, sale_price.
- `v_public_pallets` (new): archived_at IS NULL AND publish_state IN
  (live,ghost,sold); exposes display fields + `ask_price` =
  COALESCE(sale_price, list_price, total_wholesale) and `is_sold` flag.
- `sp_CreatePalletFromCatalog @display_name, @lpns_json` (#9): make a manifest,
  insert line_items from catalog rows for each lpn.

## API (`api/Functions`)

- `PalletsFunction.UpdatePalletRequest` += `publishState`, `listPrice`,
  `salePrice`. Route publishState through sp_SetPublishState; set prices in the
  UPDATE builder. Keep `isGhost` working but map it to publishState.
- `GET /api/public/pallets` (anonymous) → v_public_pallets. **#6 public read**
- `POST /api/pallets/from-items` { displayName?, lpns:[] } → sp_CreatePalletFromCatalog. **#9**
- `POST /api/pallets/{id}/items` { title, qty, condition, sellPrice, ... } →
  add an ad-hoc barcode-less line item. **#9 companion (Bella Canvas)**
- `POST /api/import-csv` (CSV body) → parse + upsert lpn_catalog (synthetic
  `NSL-…` lpn when no barcode). **#8**

## Staff UI

- admin: **Listing Status** module — Live/Draft/Ghost/Sold buttons
  (patchPallet({publishState})). Keep lot/individual/mixed as secondary "Sell as". **#6**
- admin: list_price + sale_price inputs + computed ask. **#3**
- admin: show **BOX #N** prominently on detail header + list cards. **#2**
- admin: "Add item without barcode" quick form on pallet detail. **#9**
- inventory: per-row checkbox + sticky "Create pallet from N selected". **#9**
- new `staff/import.html` + `import.js`: upload CSV → /api/import-csv. **#8**
- scan: pallet picker filters on publish_state (draft/live) not status.

## Public site

- `index.html` "On the dock today" ledger fetches `/api/public/pallets`; renders
  LIVE pallets, with ghost/sold shown as SOLD. Falls back to existing static
  rows if the API returns nothing / errors (page never looks empty). **#6**

## Status

| # | Item | Status |
|---|------|--------|
| 4 | Data-pull audit | DONE (audit only — documented, no code) |
| 5 | "Sold" mystery | DONE (already fixed; confirm to Rob) |
| — | SQL migration `wishlist2-part2.sql` | DONE — applied to prod, procs tested + verified |
| 6 | Live/Draft/Ghost/Sold + public read | DONE (DB+API+admin UI+public ledger+scan filter) |
| 2 | Box/pallet number surfacing | DONE (BOX # on detail header + gallery cards) |
| 3 | Price override + sale box | DONE (admin pricing card + public strike-through) |
| 9 | Manual pallet build + barcode-less add | DONE (inventory multi-select + admin add-item) |
| 8 | CSV import | DONE (`/api/import-csv` + `staff/import.html`) |
| — | Verify | DB procs tested live ✓; JS syntax ✓; routing ✓; C# compile = CI gate on push |
| — | Commit + deploy | TODO — push triggers SWA build (compiles C#) |
| — | Reply to Rob (SEND) | TODO — after deploy succeeds |

## Verification notes (2026-05-28)
- `sp_CreatePalletFromCatalog` → items_added=2 ✓.
- `sp_SetPublishState`: live→(live,ghost=0,sold_at null); sold→(sold, items.sold_at set);
  ghost→(ghost,is_ghost=1, in v_public_pallets is_sold=1); draft→(items.sold_at cleared,
  not in public view). All ✓. Test pallet deleted.
- All staff JS pass `node --check`.
- `staticwebapp.config.json`: added `/api/public/*` anonymous route BEFORE `/api/*`
  so the marketing site can read live pallets without a login.
- No local .NET SDK → C# compiles in the SWA GitHub Action; watch that run after push.
