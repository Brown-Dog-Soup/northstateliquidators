# Wishlist 4 — build contract

Source: Rob's 2026-09-11 list (backend B1–B9, frontend F1–F12). B10 (pallet
grouping) is OUT OF SCOPE — "more of a conversation".

Four builders code against this document **in parallel, without talking to
each other**, on branch `feature/wishlist4` (off `main`). Every name below —
column, view column, proc, route, JSON field, element id, CSS class, JS
function — is the name. If you think a name is wrong, use it anyway and leave
a `// CONTRACT:` comment; the integrator reconciles. Never push, never touch
`main`, never deploy, never `dotnet publish`.

Owner decisions already made (do not re-open):

1. **Fake-demand = ONE button, "Sold → inventory".** Original box shows SOLD on
   the public site for 48 h, then drops off (view-based exclusion, no
   scheduler). A clone with a new BOX # and the same items appears in DRAFT
   immediately. B2 and B6 are the same feature.
2. **Registration = lightweight capture now.** `dbo.members`, public form,
   member number `YY` + 5 digits (`2600001`), no login. Must be adoptable by
   the Reseller Program later (`RESELLER-PROGRAM-DESIGN.md`).
3. **Hot Deals = live boxes on sale** (`sale_price` set and below `list_price`
   — `is_on_sale` already exists).
4. Everything on the list except pallet grouping. PR only.

Hard rules (all streams):

- `/api/public/*` and public pages NEVER expose `unit_cost`, `wholesale_price`,
  `total_cost_units`, `total_cost`, `total_wholesale`, `notes`, `lpn`, any
  margin figure, or `sold_to_inventory_at` (it reveals the fake sale).
- Simple, visible, existing patterns over abstraction. Users are two
  non-technical owners on a warehouse floor.
- No new npm/NuGet dependencies.
- Commit only your own files (see file ownership). End every commit message with:

  ```
  Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01623GLQkokorNZ1VKDgJisK
  ```

---

## 0. Streams and file ownership

Strict: no two streams edit the same file. Cross-layer pieces of one item are
listed as tasks inside the owning stream's section, tagged with the item id.

| Stream | Owns (may create/edit) |
|---|---|
| **db-api** | `db/wishlist4.sql`, everything under `api/` (`Functions/*.cs`, `Services/*.cs`), `staticwebapp.config.json` |
| **admin** | everything under `staff/` (`admin.html`, `js/admin.js`, `js/api.js`, `sales.html`, `js/sales.js`, new `members.html` + `js/members.js`, `index.html`, `css/staff.css`) |
| **public-index** | `index.html`, `avatar-norm.jpeg`, `avatar-rob.jpeg` (copied from `docs/`) |
| **public-pages** | new `shop.html`, `faq.html`, `css/site.css`, `js/site.js`; plus `sitemap.xml`, `thanks.html` |

Nobody edits this document during the build; the integrator updates the
status table at merge time.

## 1. Summary table

| Item | What | Stream | Also touches (task listed in that stream) | Status |
|---|---|---|---|---|
| B1 | Admin-marked SOLD boxes show in Sales | db-api | admin (`sales.js` render) | done — compile + syntax verified; not smoke-tested against a running API |
| B2/B6 | "Sold → inventory" button (clone + 48 h public SOLD) | db-api | admin (button + pill), public (view exclusion is DB) | done — §2.10 SQL checks NOT run (no DB access from worktree); Jeff runs `db/wishlist4.sql` + §2.10 before the API deploys |
| B3 | Box weight field | db-api | admin (field), public-pages (modal "Ships at ~N lb") | done |
| B4 | "XX% of MSRP!" auto-prefill of Website description | admin | — | done (front-end only) |
| B5 | Have Costs counts units not rows | db-api | admin (render `units_with_cost / unit_count`) | done |
| B7 | Audit trail (status + price changes) | db-api | admin (History panel) | done — not smoke-tested |
| B8 | Highlight item checkbox → "Featured" line on public card | db-api | admin (checkbox), public-pages (card line) | done |
| B9 | Just Dropped = live within last 48 h (`live_at`) | db-api | public-index / public-pages (`is_just_dropped`) | done — `live_at` backfill runs with the migration |
| B10 | Pallet grouping | — | OUT OF SCOPE | — |
| F1 | FAQ page from `docs/FAQ.md` | public-pages | public-index (nav + footer link) | done — Returns text is a placeholder; 3 NSL condition definitions need Rob's OK (§8) |
| F2 | Condition ⓘ tooltips | public-pages | — (index consumes via site.js) | done |
| F3 | Registration box + modal, member number | db-api | public-pages (modal in site.js), public-index (trigger), admin (`members.html`) | done — public pages tested against a mock of `/api/public/register`, not the real API |
| F4 | "🔥 Hot Deals" hero button | public-index | public-pages (`shop.html?view=hot`) | done |
| F5 | Bigger SOLD stamp on Recently Sold | public-index | — | done |
| F6 | "Ask" → "Price", MSRP same size | public-pages | public-index (removes the old table) | done |
| F7 | Just Dropped as cards, 8 max (4×2), "See all" | public-index | public-pages (renderer) | done — no live browser check at 400 px yet (PR preview) |
| F8 | Shop Inventory → full live inventory as cards | public-pages | public-index (links) | done |
| F9 | Shop Your Way tiles → per-size pages | public-pages | public-index (links) | done — size pages only show boxes with `box_size` set in admin (§8.5) |
| F10 | Raleigh pickup → Wake Forest | public-index | public-pages (`thanks.html`, modal footer) | done — `<title>`/meta keep Raleigh on purpose (§8.11) |
| F11 | Flea-market Friday delivery + $10 / 20 mi | public-index | public-pages (modal footer, faq) | done — no delivery fee at checkout (§8.1) |
| F12 | Owner avatars | public-index | — | done — figcaption corners clip inside the circle on narrow phones; cosmetic |
| — | `db/wishlist4.sql` applied to prod (Jeff, before merge) | integrator | — | not done — Jeff applies to `sqldb-nsl-prod` and runs the §2.10 checks |
| — | Verify + PR | integrator | — | gap — merged 4 branches with no conflicts; `dotnet build` 0 errors; `node --check` clean; grep gate clean; seams cross-checked, no mismatches. PR not opened (no push from this session); no SWA preview smoke yet (§10.3) |

Box size (`box_size`) is not a wishlist item but F7/F8/F9 are impossible
without it (no size field exists today). It rides with db-api + admin.

---

## 2. Data model — `db/wishlist4.sql` (db-api)

One idempotent, re-runnable file in the style of `db/wishlist3-part2.sql`:
`IF COL_LENGTH(...) IS NULL` guards, `IF NOT EXISTS (sys.tables)` for tables,
`IF OBJECT_ID(...) DROP` + `CREATE` for views/procs, `GRANT ... TO nsl_api`,
`GO` batches, `PRINT` at the end, and a rollback-notes comment block at the
bottom (§9). Order inside the file: columns → tables → procs → views → grants.

### 2.1 `dbo.manifests` — new columns

| Column | Type | Default | Notes |
|---|---|---|---|
| `box_size` | `VARCHAR(20) NULL` | — | `CONSTRAINT CK_manifests_box_size CHECK (box_size IN ('mega_box','mini_pallet','full_pallet','individual'))` — add the CHECK with its own `IF NOT EXISTS (sys.check_constraints WHERE name=...)` guard |
| `weight_lbs` | `DECIMAL(8,2) NULL` | — | B3 |
| `live_at` | `DATETIME2 NULL` | — | B9. Stamped by `sp_SetPublishState`. Backfill once from `updated_at`, but a live box edited in the last 48 h gets `now − 49 h` instead (any PATCH bumps `updated_at`, so a plain copy would put long-live boxes in "Just Dropped" on launch day): `UPDATE dbo.manifests SET live_at = COALESCE(live_at, CASE WHEN updated_at >= DATEADD(HOUR,-48,SYSUTCDATETIME()) THEN DATEADD(HOUR,-49,SYSUTCDATETIME()) ELSE updated_at END) WHERE publish_state='live' AND live_at IS NULL;` |
| `sold_to_inventory_at` | `DATETIME2 NULL` | — | B2. Set only by `sp_SoldToInventory`; cleared by `sp_SetPublishState` on live/draft |

### 2.2 `dbo.line_items` — new column

| Column | Type | Default |
|---|---|---|
| `is_highlight` | `BIT NOT NULL` | `CONSTRAINT DF_line_items_is_highlight DEFAULT 0` |

### 2.3 `dbo.manifest_history` (B7) — new table

```sql
CREATE TABLE dbo.manifest_history (
    id          BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    manifest_id UNIQUEIDENTIFIER     NOT NULL,
    changed_at  DATETIME2            NOT NULL CONSTRAINT DF_manifest_history_changed_at DEFAULT SYSUTCDATETIME(),
    changed_by  NVARCHAR(200)        NULL,      -- SWA userDetails (email) | 'square' | NULL
    field       VARCHAR(40)          NOT NULL,  -- publish_state | list_price | sale_price | box_size | sell_mode | sold_to_inventory
    old_value   NVARCHAR(400)        NULL,
    new_value   NVARCHAR(400)        NULL,
    CONSTRAINT FK_manifest_history_manifest FOREIGN KEY (manifest_id) REFERENCES dbo.manifests(id)
);
CREATE INDEX IX_manifest_history_manifest ON dbo.manifest_history (manifest_id, changed_at DESC);
GRANT SELECT, INSERT, DELETE ON dbo.manifest_history TO nsl_api;
```

Rows are written by the **API layer**, not triggers (§3.4). Because of the FK,
`DeletePallet` must `DELETE FROM dbo.manifest_history WHERE manifest_id=@id`
inside its transaction before deleting the manifest (hence the DELETE grant).

### 2.4 `dbo.members` (F3) — new table

```sql
CREATE TABLE dbo.members (
    id            INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    member_number CHAR(7)       NOT NULL CONSTRAINT UQ_members_member_number UNIQUE,   -- '2600001'
    first_name    NVARCHAR(100) NOT NULL,
    last_name     NVARCHAR(100) NOT NULL,
    email         NVARCHAR(320) NOT NULL CONSTRAINT UQ_members_email UNIQUE,           -- stored lower-cased + trimmed
    phone         VARCHAR(30)   NULL,
    city          NVARCHAR(120) NULL,
    state         VARCHAR(2)    NULL,        -- upper-cased 2-letter
    zip           VARCHAR(10)   NULL,
    how_heard     NVARCHAR(200) NULL,
    source        NVARCHAR(40)  NOT NULL CONSTRAINT DF_members_source DEFAULT 'web',    -- web | floor | import
    created_at    DATETIME2     NOT NULL CONSTRAINT DF_members_created_at DEFAULT SYSUTCDATETIME()
);
GRANT SELECT, INSERT ON dbo.members TO nsl_api;
```

Case-insensitivity: the DB default collation (`SQL_Latin1_General_CP1_CI_AS`)
makes the UNIQUE constraint case-insensitive already; the proc also lower-cases
before insert so the stored value is canonical.

Reseller-program adoption path (not built now, do not paint into a corner):
`dbo.members` IS the future `dbo.resellers` row. When Entra External ID lands,
add `external_id NVARCHAR(200) NULL UNIQUE`, `business_name`, `email_verified_at`,
`phone_verified_at`, and the `reseller_purchases` table keyed on `members.id`;
`member_number` becomes the "give your name at the register" lookup key.
Nothing in this build stores credentials, and `id INT IDENTITY` deviates
from the UNIQUEIDENTIFIER convention on purpose (guidance) — it never leaves the
API; `member_number` is the public key.

### 2.5 `dbo.sp_SetPublishState` — re-declare (adds `live_at` + clears `sold_to_inventory_at`)

Same signature `(@manifest_id UNIQUEIDENTIFIER, @publish_state VARCHAR(20))`,
same result set, same item-level effects as `db/wishlist2-part2.sql`. Changes in
the manifests UPDATE only:

```sql
live_at = CASE WHEN @publish_state = 'live' AND (publish_state <> 'live' OR live_at IS NULL)
               THEN SYSUTCDATETIME() ELSE live_at END,
sold_to_inventory_at = CASE WHEN @publish_state IN ('live','draft') THEN NULL ELSE sold_to_inventory_at END,
```

Semantics: `live_at` = the moment the box most recently BECAME live. Re-clicking
Live on an already-live box does not refresh it; draft → live again does.
Staff "undoing" a fake sale by clicking Live/Draft on the original clears the
fake-sale marker.

### 2.6 `dbo.sp_SoldToInventory` (B2/B6) — new proc

```sql
CREATE PROCEDURE dbo.sp_SoldToInventory
    @manifest_id UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;

    DECLARE @ps VARCHAR(20), @ghost BIT, @archived DATETIME2, @orig_num INT, @orig_name NVARCHAR(200);
    SELECT @ps = publish_state, @ghost = is_ghost, @archived = archived_at,
           @orig_num = pallet_number, @orig_name = display_name
    FROM dbo.manifests WHERE id = @manifest_id;

    IF @ps IS NULL            BEGIN RAISERROR('Box not found.', 16, 1); RETURN; END;
    IF @ghost = 1 OR @ps = 'ghost' BEGIN RAISERROR('Ghost boxes are already fictitious — nothing to clone.', 16, 1); RETURN; END;
    IF @ps = 'sold'           BEGIN RAISERROR('Box is already sold.', 16, 1); RETURN; END;
    IF @archived IS NOT NULL  BEGIN RAISERROR('Restore the box before using Sold to inventory.', 16, 1); RETURN; END;

    DECLARE @now DATETIME2 = SYSUTCDATETIME();
    DECLARE @clone_id UNIQUEIDENTIFIER = NEWID();
    DECLARE @clone_num INT = NEXT VALUE FOR dbo.seq_pallet_number;

    BEGIN TRAN;

    -- (a) clone: same name (NOT "(copy)" — it is the same physical box), draft, fresh timestamps,
    --     no checkout/invoice/sold/live markers.
    INSERT INTO dbo.manifests
        (id, source, pallet_reference, received_date, status, sell_mode, publish_state,
         display_name, pallet_number, category, total_cost, photo_url, notes, public_description,
         list_price, sale_price, box_size, weight_lbs, is_ghost)
    SELECT
        @clone_id, source, pallet_reference, @now, status, sell_mode, 'draft',
        display_name, @clone_num, category, total_cost, photo_url,
        CONCAT(COALESCE(notes + CHAR(10), ''), 'Cloned from BOX #', @orig_num, ' via Sold -> inventory on ', CONVERT(VARCHAR(19), @now, 120), ' UTC.'),
        public_description, list_price, sale_price, box_size, weight_lbs, 0
    FROM dbo.manifests WHERE id = @manifest_id;

    INSERT INTO dbo.line_items
        (id, manifest_id, upc, lpn, asin, qty, condition, photo_blob_url, enrich_status, enrich_source,
         title, description, brand, category, est_msrp, est_resale, unit_cost, wholesale_price, notes,
         is_highlight, created_at, enriched_at)
    SELECT
        NEWID(), @clone_id, upc, lpn, asin, qty, condition, photo_blob_url, enrich_status, enrich_source,
        title, description, brand, category, est_msrp, est_resale, unit_cost, wholesale_price, notes,
        is_highlight, @now, enriched_at
    FROM dbo.line_items WHERE manifest_id = @manifest_id;
    DECLARE @copied INT = @@ROWCOUNT;

    -- (b) original: SOLD + fake-sale marker; items consumed (inventory lives on in the clone).
    UPDATE dbo.manifests
    SET publish_state = 'sold', is_ghost = 0, sold_at = @now, sold_to_inventory_at = @now, updated_at = @now
    WHERE id = @manifest_id;
    UPDATE dbo.line_items SET sold_at = COALESCE(sold_at, @now) WHERE manifest_id = @manifest_id;

    COMMIT TRAN;

    SELECT @manifest_id AS original_id, @orig_num AS original_pallet_number,
           @clone_id AS clone_id, @clone_num AS clone_pallet_number,
           @orig_name AS clone_display_name, @copied AS items_copied;
END;
GO
GRANT EXECUTE ON dbo.sp_SoldToInventory TO nsl_api;
```

The clone deliberately does NOT copy `checkout_link_id/checkout_order_id/
checkout_url/checkout_created_at/invoice_id/invoice_url/live_at/sold_at/
sold_to_inventory_at/archived_at`. The original keeps its catalog links.

### 2.7 `dbo.sp_RegisterMember` (F3) — new proc

```sql
CREATE PROCEDURE dbo.sp_RegisterMember
    @first_name NVARCHAR(100),
    @last_name  NVARCHAR(100),
    @email      NVARCHAR(320),
    @phone      VARCHAR(30)   = NULL,
    @city       NVARCHAR(120) = NULL,
    @state      VARCHAR(2)    = NULL,
    @zip        VARCHAR(10)   = NULL,
    @how_heard  NVARCHAR(200) = NULL,
    @source     NVARCHAR(40)  = 'web'
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    SET @email = LOWER(LTRIM(RTRIM(@email)));
    SET @state = UPPER(NULLIF(LTRIM(RTRIM(@state)), ''));

    BEGIN TRAN;
    -- serialize number generation: two signups in the same second must not collide.
    -- sp_getapplock returns <0 on timeout instead of raising — check it, or a
    -- slow burst runs unserialized and dies on UQ_members_member_number.
    DECLARE @lock INT;
    EXEC @lock = sp_getapplock @Resource = 'nsl_member_number', @LockMode = 'Exclusive',
                               @LockOwner = 'Transaction', @LockTimeout = 5000;
    IF @lock < 0
    BEGIN
        ROLLBACK TRAN;
        RAISERROR('Could not reserve a member number right now — please try again.', 16, 1);
        RETURN;
    END;

    DECLARE @existing CHAR(7) = (SELECT member_number FROM dbo.members WHERE email = @email);
    IF @existing IS NOT NULL
    BEGIN
        COMMIT TRAN;
        SELECT @existing AS member_number, CAST(1 AS BIT) AS already_registered;
        RETURN;
    END;

    -- year = Eastern local year ("first person in 2026 = 2600001"), 5-digit per-year counter
    DECLARE @yy CHAR(2) = RIGHT(CAST(YEAR(SYSDATETIMEOFFSET() AT TIME ZONE 'Eastern Standard Time') AS VARCHAR(4)), 2);
    DECLARE @next INT = ISNULL((SELECT MAX(CAST(RIGHT(member_number, 5) AS INT))
                                FROM dbo.members WHERE LEFT(member_number, 2) = @yy), 0) + 1;
    DECLARE @num CHAR(7) = @yy + RIGHT('00000' + CAST(@next AS VARCHAR(5)), 5);

    INSERT INTO dbo.members (member_number, first_name, last_name, email, phone, city, state, zip, how_heard, source)
    VALUES (@num, LTRIM(RTRIM(@first_name)), LTRIM(RTRIM(@last_name)), @email,
            NULLIF(LTRIM(RTRIM(@phone)), ''), NULLIF(LTRIM(RTRIM(@city)), ''), @state,
            NULLIF(LTRIM(RTRIM(@zip)), ''), NULLIF(LTRIM(RTRIM(@how_heard)), ''), COALESCE(@source, 'web'));
    COMMIT TRAN;

    SELECT @num AS member_number, CAST(0 AS BIT) AS already_registered;
END;
GO
GRANT EXECUTE ON dbo.sp_RegisterMember TO nsl_api;
```

### 2.8 `dbo.v_pallets` — re-declare in FULL (CURRENT definition = `db/square-invoices.sql` + additions)

The last declaration wins; this file must carry every column that exists
today (`notes`, `public_description`, `invoice_id`, `invoice_url`, ...).
`items_with_cost` is KEPT for compatibility; `units_with_cost` is ADDED (B5).

```sql
IF OBJECT_ID('dbo.v_pallets', 'V') IS NOT NULL DROP VIEW dbo.v_pallets;
GO
CREATE VIEW dbo.v_pallets AS
SELECT
    m.id                    AS manifest_id,
    m.pallet_number,
    m.display_name,
    m.source,
    m.pallet_reference,
    m.received_date,
    m.sold_at,
    m.status,
    m.sell_mode,
    m.publish_state,
    m.list_price,
    m.sale_price,
    m.category,
    m.archived_at,
    m.is_ghost,
    m.total_cost,
    m.photo_url,
    m.notes,
    m.public_description,
    m.invoice_id,
    m.invoice_url,
    m.box_size,                 -- NEW
    m.weight_lbs,               -- NEW
    m.live_at,                  -- NEW
    m.sold_to_inventory_at,     -- NEW (staff only; never selected by public routes)
    agg.item_count,
    agg.unit_count,
    agg.total_msrp,
    agg.total_cost_units,
    agg.total_wholesale,
    agg.total_est_resale,
    agg.items_enriched,
    agg.items_with_cost,
    agg.units_with_cost,        -- NEW (B5)
    cond.condition_mix,         -- NEW  e.g. 'new:12,customer_return:4,untested:1'
    hl.highlight_title,         -- NEW (B8)
    hl.highlight_msrp,          -- NEW
    hl.highlight_photo          -- NEW (raw blob URL; API signs it)
FROM dbo.manifests m
OUTER APPLY (
    SELECT
        COUNT(li.id)                      AS item_count,
        SUM(li.qty)                       AS unit_count,
        SUM(li.est_msrp * li.qty)         AS total_msrp,
        SUM(li.unit_cost * li.qty)        AS total_cost_units,
        SUM(li.wholesale_price * li.qty)  AS total_wholesale,
        SUM(li.est_resale * li.qty)       AS total_est_resale,
        SUM(CASE WHEN li.enrich_status = 'hit'  THEN 1      ELSE 0 END) AS items_enriched,
        SUM(CASE WHEN li.unit_cost IS NOT NULL  THEN 1      ELSE 0 END) AS items_with_cost,
        SUM(CASE WHEN li.unit_cost IS NOT NULL  THEN li.qty ELSE 0 END) AS units_with_cost
    FROM dbo.line_items li
    WHERE li.manifest_id = m.id
) agg
OUTER APPLY (
    SELECT STRING_AGG(CAST(x.cond AS NVARCHAR(60)) + ':' + CAST(x.units AS VARCHAR(10)), ',')
               WITHIN GROUP (ORDER BY x.units DESC) AS condition_mix
    FROM (
        SELECT COALESCE(li.condition, 'untested') AS cond, SUM(li.qty) AS units
        FROM dbo.line_items li WHERE li.manifest_id = m.id
        GROUP BY COALESCE(li.condition, 'untested')
    ) x
) cond
OUTER APPLY (
    SELECT TOP 1 li.title AS highlight_title, li.est_msrp AS highlight_msrp, li.photo_blob_url AS highlight_photo
    FROM dbo.line_items li
    WHERE li.manifest_id = m.id AND li.is_highlight = 1
    ORDER BY li.est_msrp DESC, li.created_at ASC
) hl;
GO
GRANT SELECT ON dbo.v_pallets TO nsl_api;
```

### 2.9 `dbo.v_public_pallets` — re-declare in FULL

```sql
IF OBJECT_ID('dbo.v_public_pallets', 'V') IS NOT NULL DROP VIEW dbo.v_public_pallets;
GO
CREATE VIEW dbo.v_public_pallets AS
SELECT
    p.manifest_id,
    p.pallet_number,
    p.display_name,
    p.category,
    p.publish_state,
    p.received_date,
    p.sold_at,
    p.live_at,                  -- NEW
    p.photo_url,
    p.public_description,
    p.box_size,                 -- NEW
    p.weight_lbs,               -- NEW
    p.item_count,
    p.unit_count,
    p.total_msrp,
    p.list_price,
    p.sale_price,
    p.condition_mix,            -- NEW
    p.highlight_title,          -- NEW
    p.highlight_msrp,           -- NEW
    p.highlight_photo,          -- NEW
    CAST(CASE WHEN p.publish_state IN ('ghost','sold') THEN 1 ELSE 0 END AS BIT) AS is_sold,
    CAST(CASE WHEN p.sale_price IS NOT NULL AND p.list_price IS NOT NULL
                   AND p.sale_price < p.list_price
              THEN 1 ELSE 0 END AS BIT) AS is_on_sale,
    COALESCE(p.sale_price, p.list_price, p.total_wholesale) AS ask_price
FROM dbo.v_pallets p
WHERE p.archived_at IS NULL
  AND p.publish_state IN ('live','ghost','sold')
  -- B2: a fake-sold original is public for 48 h, then disappears (no scheduler)
  AND (p.sold_to_inventory_at IS NULL
       OR p.sold_to_inventory_at >= DATEADD(HOUR, -48, SYSUTCDATETIME()));
GO
GRANT SELECT ON dbo.v_public_pallets TO nsl_api;
```

`is_just_dropped` is NOT a view column — the API computes it (§3.2) so the
window lives in one C# constant.

### 2.10 Verification queries (db-api runs these against a scratch box, then deletes it)

- `EXEC dbo.sp_SetPublishState @id,'live'` → `live_at` set; again → unchanged; `'draft'` then `'live'` → refreshed.
- `EXEC dbo.sp_SoldToInventory @id` → original `publish_state='sold'`, `sold_to_inventory_at` set, items `sold_at` set; clone draft with same item count, `live_at IS NULL`, new `pallet_number`; original visible in `v_public_pallets`; `UPDATE ... sold_to_inventory_at = DATEADD(HOUR,-49,...)` → gone from `v_public_pallets`.
- `EXEC dbo.sp_RegisterMember 'A','B','X@Y.com'` twice → same `member_number`, second call `already_registered=1`; different email → next number.
- `SELECT units_with_cost, unit_count, condition_mix, highlight_title FROM dbo.v_pallets WHERE pallet_number = 90`.

---

## 3. API (db-api) — `api/Functions/*.cs`

Conventions: request bodies camelCase records with `PropertyNameCaseInsensitive`;
DB-row responses come back as Dapper dynamic rows (snake_case) exactly like
today; hand-built responses are camelCase where noted. Photo URLs pass through
`SignRowPhotos` — **add `highlight_photo` to the keys it signs** (in
`PalletsFunction.SignRowPhotos(object?)`).

### 3.1 New helper — `api/Services/ClientPrincipal.cs`

```csharp
public static class ClientPrincipal
{
    /// userDetails (preferred_username) from SWA's x-ms-client-principal header
    /// (base64 JSON {identityProvider,userId,userDetails,userRoles}); null when absent.
    public static string? UserDetails(HttpRequest req) { ... }
}
```

### 3.2 `PalletsFunction`

**`UpdatePalletRequest`** gains:

```csharp
string?  boxSize,     // 'mega_box'|'mini_pallet'|'full_pallet'|'individual'; "" or null-with-key-present clears
decimal? weightLbs    // send the key with null to clear (same HasKey pattern as listPrice)
```

Validation: `boxSize` not in the four values (and not "") → 400
`{ error: "boxSize must be one of mega_box | mini_pallet | full_pallet | individual" }`.
`weightLbs` `<= 0` → stored NULL.

**Audit (B7)** in `Update`: before applying anything, read
`SELECT publish_state, list_price, sale_price, box_size, sell_mode FROM dbo.manifests WHERE id=@id`.
After all updates, read again and insert one `dbo.manifest_history` row per
changed field among `publish_state`, `list_price`, `sale_price`, `box_size`,
`sell_mode` with `changed_by = ClientPrincipal.UserDetails(req)`. Values as
plain strings (`"180.00"`, `"live"`, NULL). Put this in a private
`WriteHistoryAsync(conn, id, before, after, who)` helper used by the routes
below too.

**`ItemsWithCatalogSql`**: add `li.is_highlight` to the SELECT list.

**`Duplicate`**: copy `box_size, weight_lbs` onto the new manifest (same
explicit UPDATE as `category`), and add `is_highlight` to the line_items copy.

**`Delete`**: delete `dbo.manifest_history` rows for the id inside the
transaction, before `line_items`.

**`PublicPallets`** — `GET /api/public/pallets` (anonymous). New constant
`internal const int JustDroppedHours = 48;`. SQL:

```sql
SELECT manifest_id, pallet_number, display_name, category, publish_state,
       received_date, sold_at, live_at, photo_url, public_description,
       box_size, weight_lbs, item_count, unit_count, total_msrp, list_price, sale_price,
       condition_mix, highlight_title, highlight_msrp, highlight_photo,
       is_sold, is_on_sale, ask_price,
       CAST(CASE WHEN publish_state = 'live' AND live_at >= DATEADD(HOUR, -@hrs, SYSUTCDATETIME())
                 THEN 1 ELSE 0 END AS BIT) AS is_just_dropped
FROM dbo.v_public_pallets
ORDER BY CASE WHEN publish_state = 'live' THEN 0 ELSE 1 END,
         COALESCE(live_at, sold_at, received_date) DESC
```

Public row shape (snake_case, unchanged fields kept):

```json
{ "manifest_id": "...", "pallet_number": 90, "display_name": "...", "category": "Electronics",
  "publish_state": "live", "received_date": "...", "sold_at": null, "live_at": "2026-09-10T18:02:00",
  "photo_url": "https://...sas", "public_description": "15% of MSRP!",
  "box_size": "mega_box", "weight_lbs": 42.5,
  "item_count": 18, "unit_count": 42, "total_msrp": 1240.00, "list_price": 200.00, "sale_price": 180.00,
  "condition_mix": "new:30,customer_return:10,untested:2",
  "highlight_title": "Dyson V8", "highlight_msrp": 399.99, "highlight_photo": "https://...sas",
  "is_sold": false, "is_on_sale": true, "ask_price": 180.00, "is_just_dropped": true }
```

**`PublicPalletItems`** — `GET /api/public/pallets/{id}/items`: pallet SELECT
adds `box_size, weight_lbs, condition_mix, highlight_title, highlight_msrp,
highlight_photo, live_at`; items SELECT adds `is_highlight`. Still no cost
columns.

**NEW `POST /api/pallets/{id}/sold-to-inventory`** (`[Function("SoldToInventory")]`, staff):

1. `EXEC dbo.sp_SoldToInventory @manifest_id=@id` (proc RAISERRORs → catch
   `SqlException` with `Number == 50000` → 409 `{ error: ex.Message }`).
2. If the original had `checkout_link_id` and Square is configured: retire the
   link exactly as `SquareReconcile` does (`_square.DeletePaymentLinkAsync` +
   NULL the four checkout columns). Inject `SquareService` into
   `PalletsFunction` (already a singleton). If Square is not configured, skip.
3. History rows on the ORIGINAL: `('publish_state', <prev>, 'sold')` and
   `('sold_to_inventory', NULL, 'BOX #<clonePalletNumber>')`. One row on the
   CLONE: `('publish_state', NULL, 'draft')` with `old_value` NULL and
   `new_value = 'draft (cloned from BOX #<originalPalletNumber>)'`.
4. Response 200 (camelCase):

```json
{ "originalId": "...", "originalPalletNumber": 83, "cloneId": "...", "clonePalletNumber": 104,
  "cloneDisplayName": "Tuesday Truck Electronics", "itemsCopied": 18 }
```

**NEW `GET /api/pallets/{id}/history`** (`[Function("PalletHistory")]`, staff) →
`SELECT TOP 200 id, changed_at, changed_by, field, old_value, new_value FROM dbo.manifest_history WHERE manifest_id=@id ORDER BY changed_at DESC, id DESC`
→ JSON array of those rows (snake_case). 404 if the manifest does not exist.

### 3.3 `ItemsFunction`

`PatchRequest` gains `bool? isHighlight` → `sets.Add("is_highlight = @h")`.
The returned row adds `is_highlight`. Nothing else changes.

### 3.4 `SquareFunction`

- Webhook and reconcile: when they call `sp_SetPublishState ... 'sold'`, also
  insert a `manifest_history` row `('publish_state', <prev>, 'sold')` with
  `changed_by = 'square'` (`prev` is already in hand in both places). Same for
  `InvoiceBox` (live → draft, `changed_by = ClientPrincipal.UserDetails(req)`).

- **`SalesSummary` — `GET /api/sales-summary?days=N` (B1).** The Square half is
  optional: when Square is unconfigured or its list calls throw, the route still
  returns the admin-marked rows (they only need the DB) with `square_error` set
  and empty `payouts`; `sales.js` shows a one-line notice instead of the error
  tile. After building the Square list, add admin-marked sales:

```sql
SELECT m.id AS manifest_id, m.pallet_number, m.display_name, m.sold_at,
       COALESCE(v.sale_price, v.list_price, v.total_wholesale) AS ask_price,
       v.total_cost, v.total_cost_units
FROM dbo.manifests m
JOIN dbo.v_pallets v ON v.manifest_id = m.id
WHERE m.publish_state = 'sold'
  AND m.is_ghost = 0
  AND m.sold_to_inventory_at IS NULL                       -- fake sales are not revenue
  AND m.sold_at >= @begin
  AND NOT EXISTS (SELECT 1 FROM dbo.payments p                                -- never double count a WEB sale,
                  WHERE p.manifest_id = m.id                                   -- but a refunded / refund-flagged
                    AND p.needs_refund = 0 AND p.status LIKE 'COMPLETED%')    -- row must not hide a later re-sale
ORDER BY m.sold_at DESC
```

Each becomes a sale row with `amount_cents = round(ask_price*100)` (0 when
`ask_price` is NULL — still listed so Rob sees the box), `cost = total_cost ??
total_cost_units`, `margin_cents` when cost known. Merge with the Square rows,
sort by `created_at` DESC.

**Deviation (review fix): `admin_cents` is NOT folded into `gross_cents`.** A
floor sale rung up on the Square terminal has no order link, so it already sits
in the Square list as `channel: floor` (no box). When staff then mark that box
SOLD in admin — the normal counter workflow — the box also matches this query.
Adding `admin_cents` on top would count every floor sale twice. So
`gross_cents = square_cents`, `sale_count` counts Square rows only, and the
admin rows are listed with their own `admin_count`/`admin_cents` and a note that
they may already be a floor sale. If Rob wants admin-marked boxes in gross, add
a real link (e.g. a "no Square payment" flag on the box) for the `NOT EXISTS`
to key on. Response shape (snake_case, additions marked):

```json
{
  "days": 30,
  "gross_cents": 100000,          // = square_cents (what Square collected, web + floor)
  "square_cents": 100000,         // NEW
  "web_cents": 60000,
  "floor_cents": 40000,
  "admin_cents": 23400,           // NEW — listed separately, may overlap floor_cents
  "refunded_cents": 0,
  "sale_count": 5,                // Square rows only (matches gross_cents)
  "admin_count": 2,               // NEW
  "square_error": null,           // NEW — message when Square was unconfigured / failed
  "sales": [
    { "payment_id": "abc", "created_at": "...", "amount_cents": 18000, "refunded_cents": 0,
      "channel": "web", "source": "square",                       // source NEW
      "pallet_number": 91, "display_name": "...", "cost": 61.20, "margin_cents": 11880, "note": null },
    { "payment_id": null, "created_at": "2026-09-08T15:10:00Z", "amount_cents": 23400, "refunded_cents": 0,
      "channel": "admin", "source": "admin",
      "pallet_number": 83, "display_name": "...", "cost": 90.00, "margin_cents": 14400,
      "note": "Marked sold in admin — no Square payment linked to this box; if it was rung up on the terminal it is already in Floor / other" }
  ],
  "payouts": [ ... unchanged ... ]
}
```

`channel` values are now `web | floor | admin`. `source` is `square | admin`.

### 3.5 NEW `api/Functions/MembersFunction.cs` (F3)

```
POST /api/public/register        anonymous  (already covered by the /api/public/* SWA rule — confirm, no config change needed)
GET  /api/members                staff
GET  /api/members/export.csv     staff
```

**`POST /api/public/register`** body (camelCase):

```json
{ "firstName": "Norm", "lastName": "Turner", "email": "norm@x.com", "phone": "919-555-0100",
  "city": "Wake Forest", "state": "NC", "zip": "27587", "howHeard": "Facebook", "website": "" }
```

Rules, in order:

1. Honeypot: `website` non-empty → log at Information, return **200**
   `{ "memberNumber": null, "alreadyRegistered": false }` (nothing stored).
2. Soft per-IP rate limit, in-memory: `static ConcurrentDictionary<string,(int count, DateTime windowStart)>`;
   window 60 s, limit 5. IP = first value of `x-forwarded-for` **only if it
   parses as an IP** (`:port` stripped), else
   `req.HttpContext.Connection.RemoteIpAddress`. Because the header is
   caller-controlled, two backstops: a **global cap of 30 signups per 60 s per
   instance** (any IP), and the dictionary is hard-capped at 5000 keys. Over
   either limit → **429**
   `{ "error": "Too many signups from this connection — try again in a minute." }`.
3. Validation → 400 `{ "error": "<message>" }`: `firstName`/`lastName` required
   (trimmed, ≤100); `email` required, ≤320, must match
   `^[^@\s]+@[^@\s]+\.[^@\s]+$`; `state` if present must be 2 letters; `zip` ≤10;
   `phone` ≤30; `howHeard` ≤200.
4. `EXEC dbo.sp_RegisterMember ...` with `@source='web'`. A `RAISERROR`
   (50000) from the proc — the member-number lock timed out — → **503**
   `{ "error": "<proc message>" }`; nothing stored, the form retries.
5. **200** `{ "memberNumber": "2600001", "alreadyRegistered": false }`.
   **Returning email → `{ "memberNumber": null, "alreadyRegistered": true }`**
   — the number is never echoed back for an existing email (anyone could type
   someone else's address and get their number); the UI tells them to ask at
   the register.

**`GET /api/members`** → JSON array of `SELECT id, member_number, first_name, last_name, email, phone, city, state, zip, how_heard, source, created_at FROM dbo.members ORDER BY created_at DESC` (snake_case rows).

**`GET /api/members/export.csv`** → `text/csv; charset=utf-8`,
`Content-Disposition: attachment; filename="nsl-members-YYYYMMDD.csv"`, header
row `member_number,first_name,last_name,email,phone,city,state,zip,how_heard,source,created_at`,
same order as the list, RFC-4180 quoting (double any `"`; quote fields
containing `,` `"` or newline), UTF-8 BOM prefix so Excel opens it cleanly.
Formula-injection guard: a field starting with `=` `+` `-` `@` tab or CR gets a
leading `'` and is force-quoted (every text column was typed by an anonymous
visitor and the file is opened in Excel).

### 3.6 `staticwebapp.config.json`

No change required: `/api/public/*` is already anonymous and precedes `/api/*`.
db-api confirms and leaves the file untouched unless a route is found blocked.

### 3.7 `api.js` additions are the admin stream's job (§4.1) — db-api does not touch `staff/`.

---

## 4. Staff admin (admin stream) — `staff/**`

### 4.1 `staff/js/api.js` — add to `apiClient`

```js
soldToInventory: (id)      => api('POST', `/api/pallets/${id}/sold-to-inventory`),
palletHistory:   (id)      => api('GET',  `/api/pallets/${id}/history`),
setBoxSize:      (id, boxSize)   => api('PATCH', `/api/pallets/${id}`, { boxSize }),
setWeight:       (id, weightLbs) => api('PATCH', `/api/pallets/${id}`, { weightLbs }),
members:         ()        => api('GET',  '/api/members'),
```

Export the size list next to `NSL_CATEGORIES`:

```js
export const NSL_BOX_SIZES = [
  { value: 'mega_box',    label: 'Mega Box' },
  { value: 'mini_pallet', label: 'Mini Pallet' },
  { value: 'full_pallet', label: 'Full Pallet' },
  { value: 'individual',  label: 'Individual' },
];
```

### 4.2 `staff/admin.html` — detail view

**Listing status card** (after the `.publish-toggle` grid, before the help `<p>`):

```html
<button id="sold-to-inventory" class="btn btn-yellow" style="width:100%;margin-top:8px;">Sold → inventory</button>
<p id="sold-to-inventory-note" style="margin:6px 0 0;font-size:12px;color:#666;line-height:1.5;">
  Shows this box as SOLD on the website for 48 hours, then it disappears. A copy with a new BOX # and the same items
  is created in Draft right away so you can put it back on the site later.
</p>
```

Confirm text (admin.js): `Mark BOX #${n} as SOLD on the website and create a Draft copy with a new box number?\n\nThe SOLD box stays on the site for 48 hours, then disappears. This does not count as a real sale.`
On success: toast `Sold → inventory: BOX #${originalPalletNumber} shows SOLD · new Draft BOX #${clonePalletNumber}` (4000 ms), then `location.hash = '#/pallet/' + r.cloneId`.
Button disabled (opacity .45, title "Only for Live or Draft real boxes") when `publish_state` is `sold`/`ghost`, `is_ghost`, or `archived_at`.

Help paragraph: append `· <b>Sold → inventory</b> = looks sold for 48 h, then a Draft copy takes its place.`

**Meta card** (`#dn` / `#cat` / `#pubdesc` / `#notes`): add two fields between `#cat` and `#pubdesc`:

```html
<div class="field">
  <label for="box-size">Box size <span style="font-weight:400;color:#666;font-size:10px;letter-spacing:0;text-transform:none;">— drives the Shop pages on the website</span></label>
  <select id="box-size"></select>       <!-- populated from NSL_BOX_SIZES with a leading "—" blank option -->
</div>
<div class="field">
  <label for="weight-lbs">Box weight (lb) <span style="font-weight:400;color:#666;font-size:10px;letter-spacing:0;text-transform:none;">— for shipping quotes</span></label>
  <input type="number" step="0.1" min="0" id="weight-lbs" placeholder="e.g. 42.5"/>
</div>
```

`#save-meta` sends `{ displayName, notes, publicDescription, category, boxSize: $('#box-size').value, weightLbs: <number or null> }` (always include both keys).

**Website description auto-prefill (B4)** — front-end only, in `showDetail`:
- `askNow = current.sale_price ?? current.list_price ?? current.total_wholesale`
- If `current.public_description` is empty/whitespace AND `askNow > 0` AND `current.total_msrp > 0`:
  `pct = Math.round(askNow / current.total_msrp * 100)`; set `#pubdesc.value = \`${pct}% of MSRP!\``,
  add class `auto` to `#pubdesc` (CSS `textarea.auto { background:#fffbe6; }`), and show
  `<span id="pubdesc-auto-hint">auto — edit or Save to keep</span>` (an element placed right after the textarea, `hidden` by default).
- The user's first keystroke removes `.auto` + hides the hint. Saving persists whatever is in the box (today's `#save-meta` path). **Never overwrite existing text.** Also re-run the prefill after `#save-pricing` succeeds (price changed → percent changes) only if the box is still empty or still `.auto`.

**Stats `<pre>`**: `have costs   ${current.units_with_cost ?? 0} of ${current.unit_count ?? 0} units` (B5). Add lines `size         ${sizeLabel || '—'}`, `weight       ${current.weight_lbs ? current.weight_lbs + ' lb' : '—'}`, `live since   ${current.live_at ? new Date(current.live_at).toLocaleString() : '—'}`.

**History card (B7)** — new card after the Stats card:

```html
<div class="card" style="margin-top:16px;">
  <details id="history">
    <summary style="cursor:pointer;font-family:Anton;letter-spacing:0.02em;text-transform:uppercase;font-size:16px;">History</summary>
    <ul id="history-list" style="list-style:none;margin:10px 0 0;padding:0;font-size:13px;line-height:1.7;"></ul>
  </details>
</div>
```

Loaded lazily on first `toggle` open via `apiClient.palletHistory(id)`; newest first;
one `<li>` per row formatted `Sep 11 2:14 PM · Rob · Price $180.00 → $150.00`:
- date: `toLocaleString('en-US', { month:'short', day:'numeric', hour:'numeric', minute:'2-digit' })`
- who: `changed_by` part before `@` (`rob@...` → `rob`, capitalize first letter); `'square'` → `Square`; null → `—`
- field labels: `publish_state`→`Status`, `list_price`→`Price`, `sale_price`→`Sale price`, `box_size`→`Size`, `sell_mode`→`Sell as`, `sold_to_inventory`→`Sold → inventory`
- money fields via `fmtMoney`; null → `—`; other fields raw.
- Empty → `<li style="color:#888;">No changes recorded yet.</li>`.

**Items list (B8)** — in each `.item-row`, after the `.item-select` checkbox add:

```html
<label class="hl-toggle" title="Feature this item on the website under the box">
  <input type="checkbox" class="item-highlight" data-id="${it.id}" ${it.is_highlight ? 'checked' : ''}> ★
</label>
```

`change` → `apiClient.patchItem(id, { isHighlight: checked })` → toast `Featured` / `Un-featured`; no full re-render needed (update `currentItems`). CSS in `staff.css`: `.hl-toggle { display:flex; align-items:center; gap:3px; font-size:14px; color:#B7700B; cursor:pointer; flex-shrink:0; }` and `.item-row.highlighted { border-left:4px solid var(--warehouse-yellow); }` (toggle the class live). Highlighted rows also show `★ Featured` in the `.meta` line.

### 4.3 `staff/admin.html` / `admin.js` — list view

- Cards: `${p.units_with_cost ?? 0}/${p.unit_count ?? 0} units have costs` (orange when `<`). Add size badge `<span class="pill size">${label}</span>` when `box_size` set (`.pill.size { background:#FFF3C4; color:#7a5a00; }`). Pill for fake-sold originals: when `p.sold_to_inventory_at` render `<span class="pill sold-inv">sold → inventory</span>` INSTEAD of the `sold` pill (`.pill.sold-inv { background:#FDE7E7; color:#8a1f1f; }`).
- Table: column `Have costs` → `${units_with_cost}/${unit_count}`; add column `{ key:'box_size', label:'Size' }` as a `<select data-f="boxSize">` inline (autosave on change via `apiClient.setBoxSize`); add `{ key:'weight_lbs', label:'Lb' }` numeric input `data-f="weightLbs"` (autosave via `apiClient.setWeight`); status cell shows `sold → inventory` pill as above. Update `colspan` accordingly.

### 4.4 `staff/sales.html` + `staff/js/sales.js` (B1 render)

- Tiles: `Gross · Nd` sub reads `${sale_count} Square sales · web + floor`; add tile `<div class="tile"><div class="lbl">Marked sold (admin)</div><div class="val">${money(s.admin_cents)}</div><div class="sub">${admin_count} boxes marked SOLD in admin — not in Gross; may already be a floor sale</div></div>`; rename "Web margin" → "Margin" with sub `sale − our cost, boxes with a cost`. When `square_error` is set, a full-width yellow tile `Square unavailable` + the message goes first (admin rows still render).
- Table: Channel cell `<span class="chan ${x.channel}">${x.channel}</span>`; add CSS `.chan.admin { background:#FDE7E7; color:#8a1f1f; }`. When `x.note` is set, render it under the box name as `<div style="font-size:11px;color:#888;">${esc(x.note)}</div>`. Amount `—` when `amount_cents === 0 && x.source === 'admin'` with title `No price set on this box`.
- Nothing else changes.

### 4.5 NEW `staff/members.html` + `staff/js/members.js` (F3)

Same header bar as `sales.html` (`NSL · Members`). Body:

```html
<div class="wrap">
  <div style="display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:12px;margin-bottom:16px;">
    <p id="members-count" style="margin:0;color:#555;">Loading…</p>
    <a id="members-export" class="btn btn-secondary" href="/api/members/export.csv" style="padding:8px 16px;font-size:12px;">⬇ Download CSV</a>
  </div>
  <div class="twrap"><table class="stable" id="members-table"></table></div>
</div>
```

Copy the `.stable/.twrap` CSS block from `sales.html`. Columns: `Member #`, `Name`, `Email` (mailto), `Phone` (tel), `City/State`, `Zip`, `Heard from`, `Joined` (`toLocaleDateString`). Newest first as returned. Count line: `N members`.

`staff/index.html`: add a tile after Sales, color `#0b6e99`:
`<h2>Members</h2><p>Everyone who signed up on the website for a member number. Download as CSV for email blasts.</p>` → `Open Members →` linking `members.html`.

---

## 5. Public — `index.html` (public-index stream)

`index.html` keeps all of its inline CSS except the manifest-modal block
(`.mf-overlay` … `.mf-foot .note`, currently ~lines 315–337), which moves to
`css/site.css`. It gains ONE stylesheet link and ONE script, placed exactly:

```html
<!-- in <head>, AFTER the closing </style> of the inline block -->
<link rel="stylesheet" href="css/site.css">
<!-- last thing before </body>, replacing BOTH inline <script> blocks at the bottom (loadLivePallets + manifest modal) -->
<script src="js/site.js"></script>
<script>
  NSL.initPage({ joinTrigger: '#join-open' });
  NSL.renderJustDropped({ mount: '#just-dropped-grid', limit: 8, countLine: '#pallet-count-line' });
  NSL.renderRecentlySold({ mount: '#sold-grid', section: '#sold-section', limit: 8 });
</script>
```

Delete the manifest modal markup (`<div id="manifest-modal" ...>`) — site.js
creates it. Delete the six hardcoded demo `<tr>` rows and the whole `.ledger`
table.

### 5.1 Structural edits (by section, top to bottom)

**Top bar** `.topbar .inner` — FIRST child becomes the join trigger (F3, "top-left"):

```html
<button type="button" id="join-open" class="join-btn">★ Join — get your member #</button>
```

`.join-btn { background:var(--warehouse-yellow); color:var(--ink); border:0; font-family:'Anton',sans-serif; font-size:12px; letter-spacing:.06em; text-transform:uppercase; padding:5px 10px; cursor:pointer; }`
site.js swaps the label to `★ Member #2600001` when `localStorage['nsl.member']` exists.

Replace `RALEIGH, NC · PICKUP OR SHIP` with `WAKE FOREST, NC · PICKUP · DELIVERY · WE SHIP` (F10).

**Nav** `<nav>`: `Shop Inventory` → `href="shop.html?view=all"`; `Mega Boxes` → `shop.html?view=mega_box`; `Pallets` → `shop.html?view=full_pallet`; add `<a href="faq.html">FAQ</a>` before `Contact`. Actions: red `.btn-primary` `Shop Inventory` → `href="shop.html?view=all"`.

**Ticker**: both `RALEIGH PICKUP · LOCAL DELIVERY · WE SHIP` spans → `WAKE FOREST PICKUP · FREE FRIDAY DELIVERY TO THE RALEIGH FLEA MARKET · $10 DELIVERY WITHIN 20 MILES · WE SHIP`.

**Hero CTA** (F4/F8):

```html
<div class="hero-cta">
  <a class="btn-shop" href="shop.html?view=all">🛒 Shop Inventory</a>
  <a class="btn-shop btn-hot" id="hero-hot-deals" href="shop.html?view=hot"><span class="flame" aria-hidden="true">🔥</span> Hot Deals</a>
  <a class="btn-hero-ghost" href="#how">How It Works</a>
</div>
```

Same size as `.btn-shop` (it IS `.btn-shop`). CSS:

```css
.btn-hot { background: var(--nc-red); color: #fff; border-color: #7a0000; position: relative; }
.btn-hot:hover { background: var(--nc-red-dark); }
.btn-hot .flame { display:inline-block; font-size: 24px; transform-origin: 50% 90%; animation: flicker 0.9s ease-in-out infinite alternate; filter: drop-shadow(0 0 6px rgba(255,140,0,.85)); }
.btn-hot::before { content:'🔥'; position:absolute; top:-14px; left:10px; font-size:16px; opacity:.9; animation: flicker 1.3s ease-in-out infinite alternate-reverse; }
.btn-hot::after  { content:'🔥'; position:absolute; top:-12px; right:12px; font-size:14px; opacity:.85; animation: flicker 1.1s ease-in-out infinite alternate; }
@keyframes flicker { from { transform: scale(1) rotate(-4deg); } to { transform: scale(1.18) rotate(5deg); } }
@media (prefers-reduced-motion: reduce) { .btn-hot .flame, .btn-hot::before, .btn-hot::after { animation: none; } }
```

**Shop Your Way tiles** (F9): `Shop Mega Boxes` → `shop.html?view=mega_box`; `Shop Mini Pallets` → `shop.html?view=mini_pallet`; `Shop Full Pallets` → `shop.html?view=full_pallet`; `Shop Individual Items` → `shop.html?view=individual`.

**How It Works step 04** (F10/F11):

```html
<h3>Pick Up Or We Deliver</h3>
<p>Pickup in Wake Forest. Free delivery to the Raleigh Flea Market every Friday. $10 delivery within 20 miles of our warehouse. We ship too.</p>
```

**Just Dropped section** (`#inventory`) (F6/F7/B9): subhead → `The newest boxes and pallets on the floor — live within the last 48 hours. Photos and manifests on each one. Pickup in Wake Forest or we'll deliver.` Replace the `.ledger` table with:

```html
<div class="box-grid" id="just-dropped-grid"></div>
<div style="margin-top:16px; display:flex; justify-content:space-between; align-items:center; flex-wrap:wrap; gap:12px; font-family:'JetBrains Mono',monospace; font-size:12px; color:#555;">
  <span id="pallet-count-line">Live inventory — updated continuously</span>
  <a class="see-all" href="shop.html?view=new">See all just dropped →</a>
  <a href="tel:+19195260112" style="color: var(--nc-red); text-decoration:none; font-weight:700;">CALL TO BUY → (919) 526-0112</a>
</div>
```

`.box-grid` and card styles come from `site.css` (4-up, 2-up ≤ 900 px, 1-up ≤ 560 px). `.see-all { color: var(--nc-navy); font-weight:700; text-decoration:none; border-bottom:2px solid var(--warehouse-yellow); }` in index CSS.

Behaviour (in site.js, index just mounts): shows `is_just_dropped` boxes, newest `live_at` first, max 8. If NONE are just-dropped, fall back to the newest 8 live boxes and set the count line to `Showing the newest N live boxes`. If the API fails / zero live: render `<p class="box-empty">Fresh loads land every week — call (919) 526-0112 for what's on the floor right now.</p>`. (The fake demo rows are gone for good.)

**Recently Sold** (F5): keep markup; CSS `.sold-stamp` → `font-size: 42px; border-width: 6px; padding: 4px 22px;` (≈1.6×). Rendering moves to `NSL.renderRecentlySold` (same card HTML as today so the section looks identical apart from the stamp).

**Owners** (F12): copy `docs/Norman Avatar.jpeg` → `/avatar-norm.jpeg` and `docs/Rob Avatar.jpeg` → `/avatar-rob.jpeg` (repo root, `git add` both). Replace the two placeholder divs:

```html
<div class="owner-photos">
  <figure class="p"><img src="avatar-norm.jpeg" alt="Norm Turner, co-owner" loading="lazy"><figcaption class="tag"><strong>NORM TURNER</strong>Co-owner · Does the sorting</figcaption></figure>
  <figure class="p"><img src="avatar-rob.jpeg"  alt="Rob TeCarr, co-owner"  loading="lazy"><figcaption class="tag"><strong>ROB TECARR</strong>Co-owner · Talks to the trucks</figcaption></figure>
</div>
```

CSS: `.owner-photos .p { aspect-ratio: 1/1; margin:0; border-radius:50%; overflow:hidden; background:#1a1c1f; border:4px solid var(--warehouse-yellow); position:relative; } .owner-photos .p img { width:100%; height:100%; object-fit:cover; display:block; } .owner-photos .p .tag { bottom:10%; left:12%; right:12%; text-align:center; }`. Remove the hatched `background-image` rule. Eyebrow `Raleigh, NC · Family-owned` → `Wake Forest, NC · Family-owned`; body copy `haul it to Raleigh` → `haul it to Wake Forest`.

**Footer**: `WAREHOUSE · RALEIGH, NC` → `WAREHOUSE · WAKE FOREST, NC`; blurb `Raleigh's direct source` → `The Triangle's direct source`; links: `Mega Boxes` → `shop.html?view=mega_box`, `Mini Pallets` → `shop.html?view=mini_pallet`, `Full Pallets` → `shop.html?view=full_pallet`, `Just Dropped` → `shop.html?view=new`, add `<a href="shop.html?view=hot">Hot Deals</a>`; under How It Works add `<a href="faq.html">FAQ</a>` and `Pickup & Shipping` → `faq.html#delivery`; copyright `RALEIGH, NC` → `WAKE FOREST, NC`.

**JSON-LD**: `addressLocality` → `Wake Forest`. Leave `<title>`/meta "Raleigh" (it is the market people search for) — flagged in §8.

**Recently-sold / Just-dropped inline `<script>` blocks**: deleted (replaced by the three-line mount above). `window.nslCheckoutEnabled` / `window.nslBuyBox` / `window.showManifest` are now provided by site.js with the same names, so nothing else on the page breaks.

---

## 6. Public — `shop.html`, `faq.html`, `css/site.css`, `js/site.js` (public-pages stream)

### 6.1 `js/site.js` — classic script (NOT a module), exposes `window.NSL`

```js
window.NSL = {
  // data
  fetchPublicPallets(),                         // Promise<row[]>  GET /api/public/pallets (credentials:'omit'), cached per page load
  filterByView(rows, view),                     // see VIEW_DEFS
  VIEW_DEFS,                                    // { all, new, hot, mega_box, mini_pallet, full_pallet, individual, sold }
  SIZE_LABELS,                                  // { mega_box:'Mega Box', mini_pallet:'Mini Pallet', full_pallet:'Full Pallet', individual:'Individual' }
  CONDITION_DEFS,                               // §6.4
  normalizeCondition(raw),                      // 'CUSTOMER_RETURN' | 'customer_return' → 'customer_return'
  // rendering
  boxCardHtml(row),                             // string — ONE card (§6.3)
  renderBoxCards(rows, mountSelector),          // innerHTML + binds View/Buy clicks; empty → .box-empty message
  renderJustDropped({ mount, limit, countLine }),
  renderRecentlySold({ mount, section, limit }),
  condPill(raw),                                // '<span class="cond returns" data-tip="…" title="…">Customer Return <i class="info">ⓘ</i></span>'
  condMixHtml(condition_mix),                   // top 3 buckets as condPills, e.g. "30 New · 10 Customer Return · 2 Untested"
  // modal + checkout (same window.* names index.html used before)
  showManifest(id, name),                       // also window.showManifest
  buyBox(id, el),                               // also window.nslBuyBox
  initPage({ joinTrigger }),                    // checkout-status probe, join modal mount, member label; safe to call once per page
  // helpers
  esc(s), money(n), pctOfMsrp(ask, msrp)        // pctOfMsrp → '15% of MSRP' | ''
};
```

`initPage` sets `window.nslCheckoutEnabled` from `GET /api/public/checkout-status`
exactly as index.html does today, then `mountJoinModal()`, then binds
`joinTrigger` (if the element exists).

**`VIEW_DEFS`** (`filterByView` applies `rows.filter(def.test)` then `def.sort`):

| view | test | sort | Page title / eyebrow |
|---|---|---|---|
| `all` | `publish_state==='live'` | live_at desc, then received_date desc | `All Inventory` / `Everything on the floor` |
| `new` | `is_just_dropped` | live_at desc | `Just Dropped` / `Live in the last 48 hours` |
| `hot` | `publish_state==='live' && is_on_sale` | biggest `(list_price-sale_price)/list_price` first | `🔥 Hot Deals` / `Live boxes on sale right now` |
| `mega_box` / `mini_pallet` / `full_pallet` / `individual` | `publish_state==='live' && box_size===view` | live_at desc | `Mega Boxes` etc. / `Pick your size` |
| `sold` | `is_sold` | sold_at desc | `Recently Sold` / `Already claimed` |

Unknown/missing `view` → `all`.

### 6.2 `shop.html`

One page. Reads `new URLSearchParams(location.search).get('view')`. Layout:
same `<head>` fonts + `css/site.css`; a compact header (`.site-nav`: logo →
`index.html`, links `Shop All`, `Just Dropped`, `Hot Deals`, `Mega Boxes`,
`Mini Pallets`, `Full Pallets`, `Individual`, `FAQ`, plus `#join-open`
button); a `.view-tabs` strip with the same 8 views as pill links
(`.view-tab.active` for current); `<h1 id="shop-title">` + `<p id="shop-sub">`
from VIEW_DEFS; `<div class="box-grid" id="shop-grid">`; a `.site-foot` with
phone + `faq.html` + `index.html`. Body: `NSL.initPage({joinTrigger:'#join-open'})`
then fetch → `filterByView` → `renderBoxCards(rows,'#shop-grid')`. Empty state
text per view: `Nothing in this size right now — check back Friday or call (919) 526-0112.`
`<title>` = `${VIEW_DEFS[view].title} — North State Liquidators`.
`<meta name="robots" content="index,follow">`; canonical `https://northstateliquidators.com/shop.html?view=<view>`.

### 6.3 Box card — the ONE card both pages render (`NSL.boxCardHtml`)

Modeled on the pallet-admin card. No cost anywhere.

```html
<article class="box-card" data-id="{manifest_id}" data-state="{publish_state}" data-size="{box_size||''}">
  <div class="box-photo" style="background-image:url('{photo_url}')">
    <span class="box-size">{SIZE_LABELS[box_size]}</span>                 <!-- only when box_size -->
    <span class="box-flag new">Just dropped</span>                        <!-- is_just_dropped && !is_sold -->
    <span class="box-flag hot">🔥 Hot deal</span>                          <!-- is_on_sale && live -->
    <span class="box-stamp">SOLD</span>                                    <!-- is_sold -->
  </div>
  <div class="box-body">
    <div class="box-top"><span class="box-no">BOX #{pallet_number}</span><span class="box-cat">{category||'Mixed Goods'}</span></div>
    <h3 class="box-name">{display_name || 'Box #n'}</h3>
    <div class="box-stats">{unit_count} units · {item_count} items{weight_lbs ? ' · ~'+weight_lbs+' lb' : ''}</div>
    <div class="box-price">
      <span class="price">{money(ask_price)}</span>
      <span class="msrp">{money(is_on_sale ? list_price : total_msrp)}</span>   <!-- struck; on sale shows the old price -->
      <span class="pct">{pctOfMsrp(ask_price,total_msrp)}</span>
    </div>
    <div class="box-cond">{condMixHtml(condition_mix)}</div>
    <div class="box-featured">★ Featured: {highlight_title} · {money(highlight_msrp)} MSRP</div>   <!-- only when highlight_title -->
    <p class="box-blurb">{public_description truncated 120}</p>                                   <!-- only when set -->
    <div class="box-actions">
      <a class="view" href="#" data-id="{manifest_id}" data-name="{display_name}">View Manifest →</a>
      <a class="view buy" href="#" data-buy="{manifest_id}">Buy now →</a>      <!-- only when window.nslCheckoutEnabled && live -->
    </div>
  </div>
</article>
```

`.price` and `.msrp` are the SAME font-size (Anton 24 px); `.msrp` grey
`#777` + `line-through`; `.price` `var(--nc-red)` (F6). `.box-stamp` = the
Recently Sold stamp look (Anton 42 px, red, rotated −12°). `.box-grid { display:grid; grid-template-columns:repeat(4,1fr); gap:20px; }` with `@media (max-width:900px){2 cols}` and `@media (max-width:560px){1 col}`.

### 6.4 Conditions — `CONDITION_DEFS` (F2) — single source for pills, tooltips, faq anchors

Raw values seen in `line_items.condition`: `new`, `open_box`, `damaged`,
`untested`, `customer_return`, `salvage`, and catalog-sourced `NEW`,
`USED_GOOD`, `USED_LIKE_NEW`, `CUSTOMER_RETURN`, `SALVAGE`, `OPEN_BOX`.
`normalizeCondition` lower-cases, then maps `used_like_new|like_new→like_new`,
`used_good→used_good`, `salvage→damaged`, unknown→`untested`.

| key | label | emoji | css class | faq anchor | tooltip (verbatim first sentence from FAQ where it exists) |
|---|---|---|---|---|---|
| `new` | New / Overstock | 📦 | `new` | `#cond-overstock` | Brand-new merchandise that a retailer has more of than it needs or can reasonably sell. |
| `like_new` | Used – Like New | ⭐ | `likenew` | `#cond-like-new` | Previously purchased, opened, or used but in excellent condition with little to no visible signs of use. |
| `used_good` | Used – Good | 👍 | `good` | `#cond-used-good` | Previously used and in good, functional condition, but may show visible signs of normal use. |
| `open_box` | Open Box | 📦 | `shelf` | `#cond-open-box` | Original packaging opened but not necessarily used — returned, inspected, displayed, or repackaged. |
| `customer_return` | Customer Return | ↩️ | `returns` | `#cond-customer-return` | Returned by a customer; usually Used – Like New or Used – Good. We don't open sealed returns to test them. *(NOT in FAQ — needs Rob's OK)* |
| `untested` | Untested | ❓ | `untested` | `#cond-untested` | Not powered on or checked by us. Sold as-is. *(NOT in FAQ — needs Rob's OK)* |
| `damaged` | Damaged / Salvage | ⚠️ | `damaged` | `#cond-damaged` | Known cosmetic or functional damage, or missing parts. Priced for parts or repair. *(NOT in FAQ — needs Rob's OK)* |

`condPill(raw)` output: `<span class="cond {class}" data-tip="{tooltip}" title="{tooltip}">{label} <a class="info" href="faq.html{anchor}" aria-label="What does {label} mean?">ⓘ</a></span>`.
CSS: `.cond` as today (mono 11 px pill) + colour per class (`new`/`likenew` green, `good`/`shelf` blue, `returns` orange, `untested` grey `#EFE9D8`, `damaged` `#FBE3E3`/`#8a1f1f`); hover bubble `.cond[data-tip]:hover::after { content: attr(data-tip); position:absolute; ... max-width:260px; background:var(--ink); color:#fff; font:12px/1.4 Inter; padding:8px 10px; border-radius:3px; z-index:20; white-space:normal; text-transform:none; letter-spacing:0; }` with `.cond { position:relative; }`. Touch users tap ⓘ → FAQ.

### 6.5 Manifest modal (moved from index.html into site.js + site.css)

`NSL.showManifest(id, name)` creates `#manifest-modal` on first use with the
SAME ids/classes as today (`mf-overlay`, `mf-box`, `mf-close`, `mf-head`,
`#mf-title`, `#mf-sub`, `#mf-body`, `mf-foot`, `mf-table`, `mf-thumb`,
`mf-item`, `mf-loading`, `mf-empty`) and the same fetch of
`/api/public/pallets/{id}/items`. Changes:

- sub line: `${units} units · Est. retail ${money(total_msrp)} · Price ${money(ask_price)}` + `` · Ships at ~${weight_lbs} lb`` when set + `` · ${SIZE_LABELS[box_size]}`` when set (F6/B3).
- Condition cells use `NSL.condPill` (tooltips).
- Highlighted item rows get class `mf-row-featured` (yellow left border) and `★` before the title; sort puts `is_highlight` rows first.
- Footer note: `Pickup in Wake Forest · Free delivery to the Raleigh Flea Market every Friday · $10 delivery within 20 miles of our warehouse · We ship. Call to claim this box.` (F10/F11). Note: no delivery/shipping fee is collected at checkout (§8).
- Buy button + download link unchanged.

### 6.6 Join modal (F3) — created by `mountJoinModal()` in site.js

```html
<div id="join-modal" class="mf-overlay" hidden role="dialog" aria-modal="true" aria-labelledby="join-title">
  <div class="mf-box join-box">
    <button class="mf-close" type="button" aria-label="Close">&times;</button>
    <div class="mf-head"><h3 id="join-title">Become a member</h3><p class="mf-sub">Free. Get a member number, first dibs on drops, and flea-market deals.</p></div>
    <form id="join-form" class="mf-body" novalidate>
      <div class="join-row"><label>First name <input id="join-first" name="firstName" required maxlength="100"></label>
                            <label>Last name  <input id="join-last"  name="lastName"  required maxlength="100"></label></div>
      <label>Email <input id="join-email" name="email" type="email" required maxlength="320"></label>
      <label>Phone <input id="join-phone" name="phone" type="tel" maxlength="30"></label>
      <div class="join-row"><label>City <input id="join-city" name="city" maxlength="120"></label>
                            <label>State <input id="join-state" name="state" maxlength="2" value="NC"></label>
                            <label>Zip <input id="join-zip" name="zip" maxlength="10"></label></div>
      <label>How did you hear about us? <input id="join-how" name="howHeard" maxlength="200"></label>
      <label class="join-hp" aria-hidden="true">Website <input id="join-website" name="website" tabindex="-1" autocomplete="off"></label>  <!-- honeypot, CSS: position:absolute; left:-9999px -->
      <p id="join-error" class="join-error" hidden></p>
      <button id="join-submit" class="btn btn-primary" type="submit">Get my member number</button>
    </form>
    <div id="join-result" class="mf-body" hidden>
      <p class="join-big">You're member <strong id="join-number">#2600001</strong></p>
      <p id="join-result-sub">Write it down or screenshot this — give your number at the warehouse.</p>
    </div>
  </div>
</div>
```

Submit: client checks required + email regex → `POST /api/public/register`
(JSON, `credentials:'omit'`). 200 with a `memberNumber` → hide form, show result;
if `alreadyRegistered` (`memberNumber` is null) the number line is hidden and the
sub line reads `Welcome back — that email is already registered. We don't show the number again here; ask at the register and we'll look it up.`
For a new number store `localStorage['nsl.member'] = memberNumber` (try/catch) and relabel every
`#join-open` on the page to `★ Member #<n>`. 400/429 → show `error` in
`#join-error`. Network failure → `Couldn't sign you up right now — call (919) 526-0112.`
The trigger `#join-open` may exist on any page; the modal mounts on all three public pages.

### 6.7 `faq.html` (F1)

Built from `docs/FAQ.md` **verbatim** (same section order, same wording), one
`<details class="faq">` per `##` section, `<summary>` = heading, first section
open by default. Section ids: `#why`, `#expect`, `#resell`, `#why-so-much`,
`#conditions` (the "What do the different condition labels mean?" section —
each `###` label gets its own `<div class="cond-def" id="cond-…">` using the
anchors in §6.4 and the emoji), `#note`, then TWO extra sections not in the
markdown:

- `#delivery` — `Pickup, delivery & shipping`: `Pickup at our warehouse in Wake Forest, NC (by appointment). Free delivery to the Raleigh Flea Market every Friday. $10 delivery anywhere within 20 miles of the warehouse. We can ship most boxes — call for a quote.`
- `#returns` — `Returns` (placeholder, flagged §8): `All sales final unless otherwise stated; contact us within 48 hours of pickup for any issue.`

Also add `cond-def` entries for `customer_return`, `untested`, `damaged` with
the §6.4 tooltip text, visually marked `(NSL definition)`. Same header/footer
as `shop.html`. `<title>FAQ — North State Liquidators</title>`.

### 6.8 `sitemap.xml` + `thanks.html`

- `sitemap.xml`: add `<url>` entries for `https://northstateliquidators.com/shop.html` (weekly, 0.9) and `https://northstateliquidators.com/faq.html` (monthly, 0.6); bump `lastmod` to the build date.
- `thanks.html`: `arrange pickup in Raleigh (or local delivery)` → `arrange pickup in Wake Forest, Friday delivery to the Raleigh Flea Market, or local delivery`.

---

## 7. Cross-stream names (the lookup table)

**DB columns**: `manifests.box_size`, `manifests.weight_lbs`, `manifests.live_at`, `manifests.sold_to_inventory_at`, `line_items.is_highlight`; tables `dbo.manifest_history`, `dbo.members`.
**View columns (new)**: `units_with_cost`, `condition_mix`, `highlight_title`, `highlight_msrp`, `highlight_photo`, `box_size`, `weight_lbs`, `live_at`, `sold_to_inventory_at` (v_pallets only).
**Procs**: `sp_SetPublishState` (re-declared), `sp_SoldToInventory(@manifest_id)`, `sp_RegisterMember(@first_name,@last_name,@email,@phone,@city,@state,@zip,@how_heard,@source)`.
**Routes**: `POST /api/pallets/{id}/sold-to-inventory`, `GET /api/pallets/{id}/history`, `POST /api/public/register`, `GET /api/members`, `GET /api/members/export.csv`; changed: `PATCH /api/pallets/{id}` (+`boxSize`,`weightLbs`), `PATCH /api/items/{id}` (+`isHighlight`), `GET /api/public/pallets` (+fields, `is_just_dropped`), `GET /api/sales-summary` (+`source`,`note`,`admin_cents`,`square_cents`,`admin_count`,`square_error`, channel `admin`; `gross_cents` = `square_cents`).
**Request JSON**: `boxSize`, `weightLbs`, `isHighlight`, `firstName`, `lastName`, `email`, `phone`, `city`, `state`, `zip`, `howHeard`, `website`.
**Response JSON (camelCase)**: `originalId`, `originalPalletNumber`, `cloneId`, `clonePalletNumber`, `cloneDisplayName`, `itemsCopied`, `memberNumber`, `alreadyRegistered`, `error`.
**Constants**: C# `PalletsFunction.JustDroppedHours = 48`; SQL 48 h literal in `v_public_pallets`; JS `NSL_BOX_SIZES` (api.js), `NSL.SIZE_LABELS`, `NSL.VIEW_DEFS`, `NSL.CONDITION_DEFS`.
**Admin ids**: `#sold-to-inventory`, `#sold-to-inventory-note`, `#box-size`, `#weight-lbs`, `#pubdesc-auto-hint`, `#history`, `#history-list`, `.item-highlight`, `.hl-toggle`, `#members-table`, `#members-count`, `#members-export`.
**Admin classes**: `.pill.size`, `.pill.sold-inv`, `.chan.admin`, `textarea.auto`, `.item-row.highlighted`.
**Public ids**: `#join-open`, `#join-modal`, `#join-form`, `#join-first`, `#join-last`, `#join-email`, `#join-phone`, `#join-city`, `#join-state`, `#join-zip`, `#join-how`, `#join-website`, `#join-error`, `#join-submit`, `#join-result`, `#join-number`, `#join-result-sub`, `#hero-hot-deals`, `#just-dropped-grid`, `#pallet-count-line`, `#sold-grid`, `#sold-section`, `#shop-grid`, `#shop-title`, `#shop-sub`, `#manifest-modal`, `#mf-title`, `#mf-sub`, `#mf-body`.
**Public classes (site.css)**: `.box-grid`, `.box-card`, `.box-photo`, `.box-size`, `.box-flag(.new|.hot)`, `.box-stamp`, `.box-body`, `.box-top`, `.box-no`, `.box-cat`, `.box-name`, `.box-stats`, `.box-price`, `.price`, `.msrp`, `.pct`, `.box-cond`, `.box-featured`, `.box-blurb`, `.box-actions`, `.view`, `.buy`, `.box-empty`, `.cond(.new|.likenew|.good|.shelf|.returns|.untested|.damaged)`, `.cond .info`, `.mf-*` (all existing), `.mf-row-featured`, `.join-btn`, `.join-box`, `.join-row`, `.join-hp`, `.join-error`, `.join-big`, `.site-nav`, `.view-tabs`, `.view-tab(.active)`, `.site-foot`. Index-only: `.btn-hot`, `.flame`, `.see-all`, `.sold-stamp` (enlarged), `.owner-photos .p img`.
**Global JS (window)**: `NSL`, `nslCheckoutEnabled`, `nslBuyBox`, `showManifest`. `localStorage['nsl.member']`.
**Files**: `db/wishlist4.sql`, `api/Services/ClientPrincipal.cs`, `api/Functions/MembersFunction.cs`, `staff/members.html`, `staff/js/members.js`, `shop.html`, `faq.html`, `css/site.css`, `js/site.js`, `avatar-norm.jpeg`, `avatar-rob.jpeg`.

Design tokens shared by `site.css` (copy from index): `--nc-red #CC0000`, `--nc-red-dark #991F1F`, `--warehouse-yellow #F9D71C`, `--pallet-orange #F7941D`, `--nc-navy #002868`, `--ink #0E1116`, `--paper #F5F0E6`, `--concrete #2A2D31`, `--rule #D7CFBF`; fonts Anton / Inter / JetBrains Mono / Permanent Marker via the same Google Fonts link.

---

## 8. Known gaps — tell Rob

1. **No delivery/shipping fee at checkout.** Square payment links carry the box price only (`SquareFunction.CreateCheckout`). The $10 / 20-mile delivery and any shipping are collected in person or by invoice. Adding a delivery option to checkout is a follow-on.
2. **Returns policy is a placeholder.** `docs/FAQ.md` has no returns text; `faq.html#returns` ships with "All sales final unless otherwise stated; contact us within 48 hours of pickup for any issue." — Rob to approve or rewrite.
3. **Three condition definitions are ours, not the FAQ's**: Customer Return, Untested, Damaged/Salvage (§6.4). Rob to approve wording.
4. **Sold → inventory and the catalog pool.** Availability on the Inventory page is `qty_in_manifest − SUM(line_items.qty)` (box-quantities.sql). The clone's copied line items count against that pool a second time (the original's consumed items still count too), so such items can read as over-allocated. Existing "Duplicate pallet" and ghost generation have the same effect today, so this is not new — but flag it. Precise fix if wanted later: exclude `line_items` whose manifest has `sold_to_inventory_at IS NOT NULL` from the three pool sums in `sp_AllocateCatalogToBox`, `sp_CreatePalletFromCatalog`, and `v_inventory`.
5. **Just Dropped needs `box_size` set to appear on the size pages.** Boxes with no size show under Shop All / Just Dropped / Hot Deals only. Staff must pick a size in admin for each live box (one-time chore; the table view makes it fast).
6. **Fake-sold boxes and Square links.** The API retires an open payment link BEFORE Sold → inventory runs; if Square refuses, the route returns 502 and the box is untouched (just retry). If Square is not configured the link stays until the next "Reconcile with Square", which now also sweeps fake-sold boxes (`sold_to_inventory_at IS NOT NULL`). A payment on a fake-sold box would land as REFUND_FLAGGED (existing behaviour).
7. **Sales: admin-marked boxes with no price** appear with `—` amount (still listed so nothing is invisible). Set a Box price to get revenue + margin.
8. **Counts strip on the homepage** (`14 / 2,410 / $14.3K / 76%`) stays hardcoded — not on the list. Cheap follow-on: compute from live rows in `renderJustDropped`.
9. **Registration is capture only** — no confirmation email (no outbound mail exists; see reseller design §8), no login, no dedupe by phone. Duplicates by email get "already registered" but NOT the existing number (staff look it up on the Members page); the route still reveals whether an email is signed up.
12. **Sales: admin-marked boxes are not in Gross.** A box rung up on the Square terminal and then marked SOLD in admin would otherwise count twice, so the "Marked sold (admin)" tile is informational. Ask Rob whether a "no Square payment taken" checkbox on the box is worth adding so those can join Gross.
10. **Rate limit is per Functions instance** (in-memory); scale-out resets it. Acceptable for a signup form.
11. **SEO copy still says Raleigh** in `<title>`/meta (the market), while pickup/warehouse copy says Wake Forest. Intentional; say so if asked.

---

## 9. Rollback notes (also appear as a comment block at the end of `db/wishlist4.sql`)

Code: revert the merge commit on `main` (SWA redeploys the previous build).
The DB changes are additive and safe to leave in place with the old code
(old code ignores new columns; `items_with_cost` still exists; old
`v_public_pallets` exclusion simply stops applying if you re-run the previous
view script). To fully reverse:

```sql
-- views back to the square-invoices.sql / wishlist3-part2.sql definitions:
--   re-run db/square-invoices.sql (v_pallets) then the v_public_pallets block of db/wishlist3-part2.sql
-- proc back to the wishlist2-part2.sql definition:
--   re-run the sp_SetPublishState block of db/wishlist2-part2.sql
DROP PROCEDURE IF EXISTS dbo.sp_SoldToInventory;
DROP PROCEDURE IF EXISTS dbo.sp_RegisterMember;
DROP TABLE IF EXISTS dbo.manifest_history;   -- loses the audit trail
DROP TABLE IF EXISTS dbo.members;            -- loses signups: export /api/members/export.csv FIRST
ALTER TABLE dbo.line_items DROP CONSTRAINT DF_line_items_is_highlight; ALTER TABLE dbo.line_items DROP COLUMN is_highlight;
ALTER TABLE dbo.manifests DROP CONSTRAINT CK_manifests_box_size;
ALTER TABLE dbo.manifests DROP COLUMN box_size, weight_lbs, live_at, sold_to_inventory_at;
```

Fake-sold originals that were marked SOLD keep `publish_state='sold'` after a
rollback; their clones remain as Draft boxes — both are ordinary rows.

---

## 10. Integration order + verification (integrator)

1. db-api: `db/wishlist4.sql` reviewed; Jeff applies to `sqldb-nsl-prod` (idempotent — re-run is safe) BEFORE the API deploys, because `v_public_pallets`/`sp_SetPublishState` changes are backward compatible but the API's new SELECT columns are not.
2. All streams: `node --check` on every JS file; C# compiles in the SWA GitHub Action on the PR (no local `dotnet publish`).
3. Smoke on the PR preview (SWA staging URL): admin → set size/weight, feature an item, Sold → inventory on a test box, History shows rows; Sales shows an admin row; `/shop.html?view=hot` and `?view=new`; join form returns a member number; `/faq.html#cond-open-box` scrolls; tooltips hover; Recently Sold stamp bigger; avatars round.
4. Grep gate before merge: `grep -n "unit_cost\|wholesale\|total_cost" js/site.js shop.html faq.html index.html` must return nothing.
5. Open a PR from `feature/wishlist4` → `main`. Never merge; Jeff merges (push-to-main = production).
