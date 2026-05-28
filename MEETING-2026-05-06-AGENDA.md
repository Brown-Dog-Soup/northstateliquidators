# NSL Walkthrough — Wednesday, May 6, 2026

Rob — here's everything you raised plus a few open questions, in plain English.
No tech-speak. We'll go in this order, you push back wherever it doesn't make sense.

---

## 1. Pricing on UPC scans

### Your question
> "UPC codes not pulling cost and wholesale pricing. If there are multiple same items listed with different cost and pricing, how does the lookup work when scanned?"

### What I found

**A.** When a UPC code is on the master inventory spreadsheet, the system pulls cost and wholesale fine — same as an LPN scan.

**B.** When a UPC code is **not** on the master spreadsheet (an item from a customer return, walk-in, etc.), the system falls back to a free public product database. That database has the title and a photo, but **does not carry cost or wholesale price**. So those columns will be blank for those items, no way around it short of paying for a fancier lookup service.

**C.** On the duplicates: out of 362 UPCs in the system today, **29 of them appear more than once** at different cost or price (because the same item came in on different pallets at different times). Right now the system just grabs whichever one it finds first — basically random. That's a real bug.

### What I'd like to do about it

Three options for the duplicates, easiest to fanciest:

  1. **Use the most recent one.** If the same UPC was bought twice, use the cost and price from the most recent purchase. Honest default — your cost basis is whatever you paid last.
  2. **Use the one tied to the pallet you're scanning to.** A little smarter — picks the price from the same lot the pallet came from. (See below — this is the right answer if we tag pallets with their source lot.)
  3. **Show all the matches and let the receiver pick.** Most accurate, but slower for the receiver.

**My vote:** option #2 — tag each pallet with its source-lot identifier when you create it, then the lookup uses that tag to pick the right cost. Honest cost basis tracked per-lot, no friction at scan time, and it scales as the catalog grows.

### Which identifier ties an item back to its source lot?

Each catalog row already carries five different "where did this come from" fields:

| Field | What it is | Example value |
|---|---|---|
| `lot_id` | Internal lot identifier from the supplier's manifest | `AMZ_3PL_20251121_020` |
| `pallet_id` | Source pallet identifier | `LIQ:3PL:PALLET:LNRM:PAL-49825` |
| `order_number` | Amazon order number | `AMZ0N-OJ5-4G8R` |
| `source_manifest` | The filename of the spreadsheet we imported | `INVENTORY MASTER.xlsx` |
| `source_pallet_ref` | The NSL Lot # column from your spreadsheet | `TRGET-O1A-0R17` |

**Decision for you:** which one of these does your supplier consistently provide on every manifest? Whichever one is most reliable becomes the "lot tag" we use to pick the right cost for the right pallet.

For the off-spreadsheet UPCs (no cost, no wholesale): we can either accept those columns being blank for those items, or pay around $30/month for a service that fills them in. Probably not worth it until volume is bigger.

**Decision for you:** option 1, 2, or 3? And do you care about paying for the cost/wholesale fill-in for off-spreadsheet items?

---

## 2. CSV / spreadsheet uploads

### Your question
> "Is there a way for us to upload a .csv file to have all inventory loaded once we purchase?"

### Short answer: Yes.

Most of it is already built — there's a backend that takes a spreadsheet, reads it, dedupes it, and loads everything into the system in seconds. What's missing is just a page on the staff site for you to drop the file onto.

About a half-day of work to build the upload page. After that, you and Norm can drop a master inventory spreadsheet right onto the staff site and watch it load — no more emailing it to me first.

**One catch:** the backend that already exists was originally written for Amazon's B-Stock format. The Master Inventory format you've been sending uses a different layout. I'll need to teach the backend that second format too, but it's straightforward.

**Decision for you:** good to go on this? Anything else you'd want it to do — show a preview before loading, email a summary, anything?

---

## 3. The big picture — how does an item get from the truck to the website?

### Your question
> "General flow of information overview from backend receiving to front end website and how Shopify factors in."

### How it works today

  1. **You buy a pallet.** The supplier sends a master spreadsheet listing what's on it.
  2. **I load that spreadsheet** into the system (this is the part you want to do yourselves — see #2 above).
  3. **You receive the truck.** Staff scans each item into a pallet on the staff site. Pricing, photos, condition all auto-fill.
  4. **You list items on Shopify.** Right now this is manual — Norm or you opens the Shopify mobile app on your phone, types in the title, sets the price, snaps a photo, hits publish.
  5. **The website (`northstateliquidators.com`) shows what's tagged "featured"** in Shopify. I have to run a script on my laptop to pull that list and update the website. Manual.
  6. **Someone buys an item.** Shopify takes the order. **Right now the staff site doesn't know about it.** Your scanned-in inventory and your Shopify catalog are two separate systems that don't talk to each other.

### Where the gaps are

Three big ones:

  - **Receiving and Shopify are disconnected.** What you scan in doesn't automatically end up listed for sale.
  - **The website only updates when I run a script.** Anything you change in Shopify isn't visible on the site until I refresh it.
  - **Sales don't flow back.** When something sells, the staff site has no idea — the pallet still shows it as in-stock.

### What I think we should build (the DRAFT / LIVE / SOLD idea you sketched)

That sketch you sent fixes all three at once. The idea:

  - **DRAFT** = pallet is being built. Not visible to customers anywhere.
  - **LIVE** = you flip the switch. The system automatically lists the items on Shopify and refreshes the public site. Customers can buy.
  - **SOLD** = an item or pallet sold (either from Shopify or in person). The staff site marks it sold so it disappears from "available."

That's the missing handoff. It also fixes the Sell Mode confusion you flagged ("I didn't want to click 'Sell as Lot' and have it go live" — DRAFT/LIVE/SOLD makes it explicit when something goes public).

**Decision for you:**
  - **What goes on Shopify when a pallet flips to LIVE?** The pallet itself as a single listing (for wholesale buyers), or each individual item (for retail), or both?
  - **Who can flip something to LIVE?** You and Norm both? Or one of you with the other getting notified?
  - **For pricing on Shopify** — your Pallet / Starting / Sale price idea: should that flow apply per-item too, or only per-pallet?

---

## 4. Other things worth covering

A few small ones that'll come up:

- **REAL vs GHOST listings.** The "show as sold to make the catalog look bigger" idea. Easy to build, but worth talking through where the line is — when does it cross from "fluff up the catalog" into "looks like we're hiding inventory." Your call entirely.

- **Three-tier pricing on a pallet** (Pallet Price auto-rolled-up from items, Starting Price as your override, Sale Price as the crossed-out discount). I have this clear in my head — quick to build once we've agreed on DRAFT/LIVE/SOLD.

- **Bulk pallet handling.** If you receive a truck with 50 boxes that are identical, do you want to scan each individually or scan one and tell the system "× 50"? Tied to the pallet-vs-item Shopify question above.

- **Cost.** Reminder: per the agreement we signed, all the work is $0. The only money on the table is third-party services (Microsoft email, Shopify, the domain, the small Apify charge for Amazon photos). Anything I'm building costs you nothing.

---

## Quick checklist for tonight

I want to walk away with answers on:

- [ ] **UPC duplicates:** option 1 (most recent), 2 (active pallet), or 3 (let receiver pick)?
- [ ] **CSV upload:** green light to build the upload page?
- [ ] **Pallet vs item on Shopify:** which one (or both)?
- [ ] **Who can flip LIVE:** you, Norm, both?
- [ ] **REAL/GHOST:** are we doing it, and how aggressively?

Anything else on your mind, just bring it up — I've got the rest of the evening.

— Jeff
