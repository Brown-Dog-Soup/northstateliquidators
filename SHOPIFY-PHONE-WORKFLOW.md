# Shopify Phone Workflow — Norm & Rob

**Goal:** Snap a photo on the warehouse floor, list a product in under 90 seconds, and have it appear on northstateliquidators.com automatically.

---

## One-time setup (15 min, do this once)

### 1. Install the Shopify app
- iPhone → App Store → search **"Shopify"** → install (white "S" on green icon, made by Shopify Inc.)
- Android → Play Store → same

### 2. Sign in
- Open the app → **Log in**
- Email: your `@northstateliquidators.com` email (norm@ or rob@)
- Password: same one you use for everything else NSL
- When 2FA pops up, the code goes to the **`sales@northstateliquidators.com`** shared inbox — open Outlook on your phone, grab the 6-digit code, paste it back into Shopify

> **Note for now:** while we're testing on the development store, you may need to use shared dev-store login info Jeff sent you instead. Once we flip the live store on, the above (your @northstateliquidators.com login) is what you'll use.

### 3. Tap around once so you know where things are
- Bottom nav: **Home, Orders, Products, Store, More**
- That's it. You spend 95% of your time on the **Products** tab.

---

## Listing a product (the actual loop — do this every time)

You're standing next to a pallet. You spot a thing worth selling. Here's the flow:

### Step 1 — Tap the **+** button on the Products tab (top-right corner)

### Step 2 — Take the photo
- Tap **Add image** → **Take photo**
- Frame it on a clean spot of the floor or against a wall. Good light = sells faster.
- Take 1–4 photos. First photo = the "hero" shot, that's what shows up on the website.

### Step 3 — Title it like a real listing
Bad: `Mixer`
Good: `KitchenAid Artisan Stand Mixer (Open Box) — Empire Red, 5 qt`

Pattern: **Brand · Model · Condition · Key spec/color**

### Step 4 — Set the price
- Type the **price** you're selling at (e.g., `189`)
- Optionally type the **Compare-at price** = the original retail/MSRP (e.g., `429`). This is what shows up as the strike-through "Was $429" on the website. Customers love seeing the discount.

### Step 5 — Quantity
- Scroll to **Inventory** → toggle **Track quantity** ON → set quantity to `1` (or however many of the exact item you have)

### Step 6 — Tag it `featured` if you want it on the website
This is the magic word. Three rules:

| Tag | What happens |
|---|---|
| `featured` | Shows up on **northstateliquidators.com** in the "This week's hunt" grid |
| `retail` | Single-item retail listing (default for most things) |
| `wholesale` | Pallet/bulk listing (different flow — talk to Jeff) |

To add a tag: scroll to **Organization → Tags** → type `featured` → tap return → tap **Save**.

You can use multiple tags. `featured, retail, kitchen` is fine.

### Step 7 — Description (optional but worth 30 seconds)
Two sentences max. Be honest about condition. Examples that sell:

> "Open box, never used. Original packaging included. Tested at our warehouse — works perfectly."

> "Customer return. Strap has minor scuff. Zippers all work, lining clean."

### Step 8 — Hit **Save** (top-right)

That's it. Product is live in the Shopify store.

---

## Getting it onto northstateliquidators.com

**You don't have to do anything.** Tagging it `featured` is the trigger. Jeff (or a scheduled job we'll wire up later) runs a one-line sync that pulls all `featured` products from Shopify and writes them onto the website. Re-run takes about 5 seconds.

**For now, while we're testing**, text Jeff after you add a `featured` product and he'll re-run the sync. Once the workflow is automatic this won't be needed.

---

## What if you want to STOP showing something on the website?

Two ways:
- **Remove the tag.** In the product detail screen → Tags → tap the X next to `featured` → Save. Item stays in Shopify but disappears from the website on the next sync.
- **Mark it sold-out.** Set quantity to 0. The item still shows on Shopify but the "Add to cart" button greys out. (Better than removing — shows you have inventory turning over, which is good signal.)

---

## Common gotchas

| Symptom | Fix |
|---|---|
| Photo is too dark / blurry | Retake. Phone camera is fine; just not in shadow. |
| Price won't save | Make sure you typed only digits + decimal (no `$`). Use `189` or `189.00`, not `$189`. |
| "Track quantity" toggle won't move | Tap the row label, not the slider — iOS bug. |
| Want to delete a product entirely | Product detail → scroll to bottom → **Delete product**. |
| Two products with the same title | Shopify auto-appends `-1`, `-2` to URLs. Edit one's title to differentiate. |
| Forgot to add `featured` tag | Open the product → Tags → add it → Save. Next sync picks it up. |

---

## Quick rules of thumb

1. **Photos sell more than copy.** Spend 20 seconds framing the shot, 5 seconds on the description.
2. **Compare-at price is your friend.** "$80 (was $349)" outperforms "$80" by a wide margin.
3. **Price ending in 9 or 5** outperforms round numbers (`$179` > `$180`, `$45` > `$50`).
4. **Tag everything you list with at least one of:** `retail`, `wholesale`. Use `featured` only for things you actually want on the homepage.
5. **One product = one SKU.** If you have 12 identical Yeti coolers, that's quantity = 12, not 12 separate listings.
6. **Be honest about damage.** "Strap has scuff" sells better than discovering it after delivery.

---

## When in doubt

Text Jeff. The first 10 listings are the ones you'll mess up; after that it's muscle memory.
