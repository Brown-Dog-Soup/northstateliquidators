/* ============================================================================
   North State Liquidators — shared public script (Wishlist 4, §6.1)

   Classic script (NOT a module). Exposes window.NSL plus the legacy globals
   index.html relied on: window.nslCheckoutEnabled, window.nslBuyBox (now: add to cart + open the cart),
   window.showManifest.

   Pages use it like this:
     <link rel="stylesheet" href="css/site.css">           (in <head>)
     <script src="js/site.js"></script>                     (before </body>)
     <script>
       NSL.initPage({ joinTrigger: '#join-open' });
       NSL.renderJustDropped({ mount: '#just-dropped-grid', limit: 8, countLine: '#pallet-count-line' });
       NSL.renderRecentlySold({ mount: '#sold-grid', section: '#sold-section', limit: 8 });
     </script>

   MEMBER SIGN-UP OPENER — the small button the index stream places top-left
   in .topbar .inner (first child). Copy exactly:

     <button type="button" id="join-open" class="join-btn" data-open-register>★ Join — get your member #</button>

   initPage() binds the selector passed as joinTrigger AND auto-binds every
   element carrying data-open-register (or id="join-open"), on any page. When
   localStorage['nsl.member'] exists the label becomes "★ Member #2600001".

   HARD RULE: nothing here ever reads or renders what we paid for a box or
   any margin figure. /api/public/* rows do not carry them; keep it that way.
   ========================================================================== */
(function () {
  'use strict';

  const PHONE = '(919) 526-0112';

  // ── helpers ──────────────────────────────────────────────────────────────
  const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const money = n => (n === null || n === undefined || n === '' || isNaN(Number(n))) ? '—'
    : '$' + Number(n).toLocaleString('en-US', { maximumFractionDigits: 0 });
  const num = n => (n === null || n === undefined || n === '') ? null : Number(n);
  const ts = v => { const t = Date.parse(v || ''); return isNaN(t) ? 0 : t; };
  const pctOfMsrp = (ask, msrp) => {
    const a = num(ask), m = num(msrp);
    if (!a || !m || a <= 0 || m <= 0 || a >= m) return '';
    return `${Math.round(a / m * 100)}% of MSRP`;
  };
  const $ = (sel, root) => (root || document).querySelector(sel);
  const fmtWeight = w => { const n = num(w); return n && n > 0 ? String(Math.round(n * 10) / 10) : ''; };

  // ── constants ────────────────────────────────────────────────────────────
  const SIZE_LABELS = { mega_box: 'Mega Box', mini_pallet: 'Mini Pallet', full_pallet: 'Full Pallet', individual: 'Individual' };

  // Single source for condition pills, tooltips and faq.html anchors (§6.4).
  const CONDITION_DEFS = {
    new:             { label: 'New / Overstock',   emoji: '📦', cls: 'new',      anchor: '#cond-overstock',       tip: 'Brand-new merchandise that a retailer has more of than it needs or can reasonably sell.' },
    like_new:        { label: 'Used – Like New',   emoji: '⭐', cls: 'likenew',  anchor: '#cond-like-new',        tip: 'Previously purchased, opened, or used but in excellent condition with little to no visible signs of use.' },
    used_good:       { label: 'Used – Good',       emoji: '👍', cls: 'good',     anchor: '#cond-used-good',       tip: 'Previously used and in good, functional condition, but may show visible signs of normal use.' },
    open_box:        { label: 'Open Box',          emoji: '📦', cls: 'shelf',    anchor: '#cond-open-box',        tip: 'Original packaging opened but not necessarily used — returned, inspected, displayed, or repackaged.' },
    customer_return: { label: 'Customer Return',   emoji: '↩️', cls: 'returns',  anchor: '#cond-customer-return', tip: "Returned by a customer; usually Used – Like New or Used – Good. We don't open sealed returns to test them." },
    untested:        { label: 'Untested',          emoji: '❓', cls: 'untested', anchor: '#cond-untested',        tip: 'Not powered on or checked by us. Sold as-is.' },
    damaged:         { label: 'Damaged / Salvage', emoji: '⚠️', cls: 'damaged',  anchor: '#cond-damaged',         tip: 'Known cosmetic or functional damage, or missing parts. Priced for parts or repair.' },
  };

  function normalizeCondition(raw) {
    const k = String(raw ?? '').trim().toLowerCase().replace(/[\s-]+/g, '_');
    if (!k) return 'untested';
    if (k === 'new') return 'new';
    if (k === 'used_like_new' || k === 'like_new') return 'like_new';
    if (k === 'used_good') return 'used_good';
    if (k === 'open_box') return 'open_box';
    if (k === 'customer_return') return 'customer_return';
    if (k === 'salvage' || k === 'damaged') return 'damaged';
    if (k === 'untested') return 'untested';
    // loose matches for anything the catalog throws at us
    if (k.includes('like_new')) return 'like_new';
    if (k.includes('return')) return 'customer_return';
    if (k.includes('open') || k.includes('shelf')) return 'open_box';
    if (k.includes('damage') || k.includes('salvage')) return 'damaged';
    if (k.includes('good')) return 'used_good';
    if (k.includes('new')) return 'new';
    return 'untested';
  }

  function condPill(raw) {
    const def = CONDITION_DEFS[normalizeCondition(raw)] || CONDITION_DEFS.untested;
    return `<span class="cond ${def.cls}" data-tip="${esc(def.tip)}" title="${esc(def.tip)}">${esc(def.label)} <a class="info" href="faq.html${def.anchor}" aria-label="What does ${esc(def.label)} mean?">ⓘ</a></span>`;
  }

  // 'new:30,customer_return:10,untested:2' → top 3 buckets (merged by normalized key)
  function condMixHtml(mix) {
    if (!mix) return '';
    const buckets = {};
    String(mix).split(',').forEach(part => {
      const i = part.lastIndexOf(':');
      if (i < 0) return;
      const key = normalizeCondition(part.slice(0, i));
      const units = Number(part.slice(i + 1)) || 0;
      buckets[key] = (buckets[key] || 0) + units;
    });
    return Object.entries(buckets)
      .sort((a, b) => b[1] - a[1])
      .slice(0, 3)
      .map(([key, units]) => `<span class="cond-item"><span class="cond-count">${units}</span> ${condPill(key)}</span>`)
      .join(' ');
  }

  // ── views (§6.1 VIEW_DEFS) ───────────────────────────────────────────────
  const byLiveDesc = (a, b) => (ts(b.live_at) - ts(a.live_at)) || (ts(b.received_date) - ts(a.received_date));
  const isLive = r => r.publish_state === 'live';
  const discount = r => {
    const l = num(r.list_price), s = num(r.sale_price);
    return (l && s && l > 0 && s < l) ? (l - s) / l : 0;
  };
  const SIZE_EMPTY = `Nothing in this size right now — check back Friday or call ${PHONE}.`;
  const sizeView = key => ({
    title: SIZE_LABELS[key] + (key === 'individual' ? ' Items' : 's'),
    eyebrow: 'Pick your size',
    sub: `Every live ${SIZE_LABELS[key]} on the floor right now.`,
    empty: SIZE_EMPTY,
    test: r => isLive(r) && r.box_size === key,
    sort: byLiveDesc,
  });
  const VIEW_DEFS = {
    all:  { title: 'All Inventory', eyebrow: 'Everything on the floor', sub: 'Every live box and pallet — photos and manifests on each one. Pickup in Wake Forest or we deliver.',
            empty: `Nothing live right now — fresh loads land every week. Call ${PHONE} for what's on the floor.`,
            test: isLive, sort: byLiveDesc },
    new:  { title: 'Just Dropped', eyebrow: 'Live in the last 48 hours', sub: 'The newest boxes and pallets — live within the last 48 hours. First come, first served.',
            empty: `Nothing new in the last 48 hours — see All Inventory, or call ${PHONE}.`,
            test: r => !!r.is_just_dropped, sort: (a, b) => ts(b.live_at) - ts(a.live_at) },
    hot:  { title: '🔥 Hot Deals', eyebrow: 'Live boxes on sale right now', sub: 'Marked down from the list price. When they go, they go.',
            empty: `No hot deals right now — check back Friday or call ${PHONE}.`,
            test: r => isLive(r) && !!r.is_on_sale, sort: (a, b) => (discount(b) - discount(a)) || byLiveDesc(a, b) },
    mega_box:    sizeView('mega_box'),
    mini_pallet: sizeView('mini_pallet'),
    full_pallet: sizeView('full_pallet'),
    individual:  sizeView('individual'),
    sold: { title: 'Recently Sold', eyebrow: 'Already claimed', sub: 'Boxes and pallets already claimed by our buyers. New loads land every week — when something drops, don\'t wait.',
            empty: 'Nothing sold recently.',
            test: r => !!r.is_sold, sort: (a, b) => ts(b.sold_at) - ts(a.sold_at) },
  };
  VIEW_DEFS.mega_box.title = 'Mega Boxes';
  VIEW_DEFS.mini_pallet.title = 'Mini Pallets';
  VIEW_DEFS.full_pallet.title = 'Full Pallets';

  function filterByView(rows, view) {
    const def = VIEW_DEFS[view] || VIEW_DEFS.all;
    return (rows || []).filter(def.test).slice().sort(def.sort);
  }

  // ── data ─────────────────────────────────────────────────────────────────
  let palletsPromise = null;
  let lastRows = null;                      // last successful feed — the cart bar totals from it
  function fetchPublicPallets() {
    if (!palletsPromise) {
      palletsPromise = fetch('/api/public/pallets', { credentials: 'omit' })
        .then(r => { if (!r.ok) throw new Error('http ' + r.status); return r.json(); })
        .then(rows => { lastRows = Array.isArray(rows) ? rows : []; return lastRows; })
        .catch(err => { palletsPromise = null; throw err; });
    }
    return palletsPromise;
  }
  // The cart drawer must never trust a feed cached for the page lifetime.
  function refreshPublicPallets() { palletsPromise = null; return fetchPublicPallets(); }

  // ── the ONE box card (§6.3) ──────────────────────────────────────────────
  function boxCardHtml(p) {
    const live = isLive(p);
    const sold = !!p.is_sold;
    const onSale = !!p.is_on_sale && num(p.list_price) != null;
    const size = p.box_size && SIZE_LABELS[p.box_size] ? SIZE_LABELS[p.box_size] : '';
    const name = p.display_name || ('Box #' + p.pallet_number);
    const weight = fmtWeight(p.weight_lbs);
    const photo = p.photo_url ? ` style="background-image:url('${esc(p.photo_url)}')"` : '';
    const blurb = p.public_description
      ? (p.public_description.length > 120 ? p.public_description.slice(0, 120) + '…' : p.public_description)
      : '';
    const struck = onSale ? p.list_price : p.total_msrp;
    return `
<article class="box-card" data-id="${esc(p.manifest_id)}" data-state="${esc(p.publish_state)}" data-size="${esc(p.box_size || '')}"${cartHas(p.manifest_id) ? ' data-in-cart' : ''}>
  <div class="box-photo"${photo}>
    ${size ? `<span class="box-size">${esc(size)}</span>` : ''}
    ${p.is_just_dropped && !sold ? `<span class="box-flag new">Just dropped</span>` : ''}
    ${onSale && live ? `<span class="box-flag hot">🔥 Hot deal</span>` : ''}
    ${sold ? `<span class="box-stamp">SOLD</span>` : ''}
  </div>
  <div class="box-body">
    <div class="box-top"><span class="box-no">BOX #${esc(p.pallet_number)}</span><span class="box-cat">${esc(p.category || 'Mixed Goods')}</span></div>
    <h3 class="box-name">${esc(name)}</h3>
    <div class="box-stats">${p.unit_count || 0} units · ${p.item_count || 0} items${weight ? ' · ~' + esc(weight) + ' lb' : ''}</div>
    <div class="box-price">
      <span class="price">${money(p.ask_price)}</span>
      ${num(struck) ? `<span class="msrp">${money(struck)}</span>` : ''}
      <span class="pct">${esc(pctOfMsrp(p.ask_price, p.total_msrp))}</span>
    </div>
    ${p.condition_mix ? `<div class="box-cond">${condMixHtml(p.condition_mix)}</div>` : ''}
    ${p.highlight_title ? `<div class="box-featured">★ Featured: ${esc(p.highlight_title)}${num(p.highlight_msrp) ? ` · ${money(p.highlight_msrp)} MSRP` : ''}</div>` : ''}
    ${blurb ? `<p class="box-blurb">${esc(blurb)}</p>` : ''}
    <div class="box-actions">
      <a class="view" href="#" data-id="${esc(p.manifest_id)}" data-name="${esc(name)}">View Manifest →</a>
      ${window.nslCheckoutEnabled && live ? cartButtonHtml(p.manifest_id) : ''}
    </div>
  </div>
</article>`;
  }

  function bindCardClicks(mount) {
    if (mount.dataset.nslBound) return;
    mount.dataset.nslBound = '1';
    mount.addEventListener('click', e => {
      const cartBtn = e.target.closest('button[data-cart]');
      if (cartBtn && mount.contains(cartBtn)) { onCartButton(cartBtn.getAttribute('data-cart')); return; }
      const view = e.target.closest('a.view[data-id]');
      if (view && mount.contains(view)) { e.preventDefault(); showManifest(view.getAttribute('data-id'), view.getAttribute('data-name')); }
    });
  }

  function renderBoxCards(rows, mountSelector, emptyText) {
    const mount = typeof mountSelector === 'string' ? $(mountSelector) : mountSelector;
    if (!mount) return;
    if (!rows || rows.length === 0) {
      mount.innerHTML = `<p class="box-empty">${esc(emptyText || VIEW_DEFS.all.empty)}</p>`;
      return;
    }
    mount.innerHTML = rows.map(boxCardHtml).join('');
    bindCardClicks(mount);
  }

  const JUST_DROPPED_EMPTY = `Fresh loads land every week — call ${PHONE} for what's on the floor right now.`;

  async function renderJustDropped(opts) {
    const o = opts || {};
    const mount = $(o.mount || '#just-dropped-grid');
    const countLine = o.countLine ? $(o.countLine) : null;
    const limit = o.limit || 8;
    if (!mount) return;
    await checkoutReady();   // Buy buttons depend on it
    let rows;
    try { rows = await fetchPublicPallets(); }
    catch { mount.innerHTML = `<p class="box-empty">${esc(JUST_DROPPED_EMPTY)}</p>`; return; }
    const live = filterByView(rows, 'all');
    if (live.length === 0) { mount.innerHTML = `<p class="box-empty">${esc(JUST_DROPPED_EMPTY)}</p>`; return; }
    const fresh = filterByView(rows, 'new');
    if (fresh.length > 0) {
      renderBoxCards(fresh.slice(0, limit), mount);
      if (countLine) countLine.textContent = `${fresh.length} just dropped · updated continuously`;
    } else {
      const shown = live.slice(0, limit);
      renderBoxCards(shown, mount);
      if (countLine) countLine.textContent = `Showing the newest ${shown.length} live box${shown.length === 1 ? '' : 'es'}`;
    }
  }

  // Same card HTML the home page used before (sold-card/sold-photo/sold-stamp
  // live in index.html's CSS) so the section looks identical apart from the stamp.
  async function renderRecentlySold(opts) {
    const o = opts || {};
    const mount = $(o.mount || '#sold-grid');
    const section = o.section ? $(o.section) : null;
    const limit = o.limit || 8;
    if (!mount) return;
    let rows;
    try { rows = await fetchPublicPallets(); } catch { return; }
    const sold = filterByView(rows, 'sold').slice(0, limit);
    if (sold.length === 0) return;
    mount.innerHTML = sold.map(p => {
      const photo = p.photo_url
        ? ` style="background-image:url('${esc(p.photo_url)}');background-size:cover;background-position:center;"`
        : '';
      return `
          <div class="sold-card">
            <div class="sold-photo"${photo}><span class="sold-stamp">SOLD</span></div>
            <div class="sold-meta">
              <h3>${esc(p.display_name || ('Box #' + p.pallet_number))}</h3>
              <div class="sold-sub">${p.unit_count || 0} items · ${money(p.total_msrp)} retail</div>
            </div>
          </div>`;
    }).join('');
    if (section) section.hidden = false;
  }

  // ── stats bar (live inventory counts, §Rob-1 — never show a stale number) ─
  const fmtCountPrice = n => n >= 1000 ? ('$' + (n / 1000).toFixed(1) + 'K') : ('$' + Math.round(n).toLocaleString('en-US'));

  async function renderCounts(opts) {
    const o = opts || {};
    const bar = $(o.bar || '#counts-bar');
    if (!bar) return;
    let rows;
    try { rows = await fetchPublicPallets(); }
    catch { bar.hidden = true; return; }
    const live = (rows || []).filter(isLive);
    if (live.length === 0) { bar.hidden = true; return; }

    const boxes = live.length;
    const units = live.reduce((sum, r) => sum + (num(r.unit_count) || 0), 0);
    const price = live.reduce((sum, r) => sum + (num(r.ask_price) || 0), 0);

    let sumAsk = 0, sumMsrp = 0;
    live.forEach(r => {
      const ask = num(r.ask_price), msrp = num(r.total_msrp);
      if (ask > 0 && msrp > 0) { sumAsk += ask; sumMsrp += msrp; }
    });

    const boxesEl = $(o.boxes || '#count-boxes');
    const unitsEl = $(o.units || '#count-units');
    const priceEl = $(o.price || '#count-price');
    const pctEl = $(o.pct || '#count-pct');

    if (boxesEl) boxesEl.textContent = boxes.toLocaleString('en-US');
    if (unitsEl) unitsEl.textContent = units.toLocaleString('en-US');
    if (priceEl) priceEl.textContent = fmtCountPrice(price);
    if (pctEl) pctEl.textContent = sumMsrp > 0 ? (Math.round((1 - sumAsk / sumMsrp) * 100) + '%') : '—';

    bar.hidden = false;
  }

  // ── cart state (spec §3) ─────────────────────────────────────────────────
  window.nslCheckoutEnabled = window.nslCheckoutEnabled || false;
  const CART_KEY = 'nsl.cart';
  let CART_MAX = 20;                        // overwritten by /api/public/checkout-status
  let cartMem = null;                       // in-memory fallback (private mode / storage blocked)
  let openCartHook = null;                  // set by the drawer module

  function cartIds() {
    if (cartMem) return cartMem.slice();
    try {
      const raw = JSON.parse(localStorage.getItem(CART_KEY) || '[]');
      return Array.isArray(raw) ? raw.filter(x => typeof x === 'string').map(x => x.toLowerCase()) : [];
    } catch { return []; }
  }
  function saveCart(ids) {
    const uniq = Array.from(new Set(ids.map(x => String(x).toLowerCase()))).slice(0, CART_MAX);
    try { localStorage.setItem(CART_KEY, JSON.stringify(uniq)); cartMem = null; }
    catch { cartMem = uniq; }
    syncCartUi();
    return uniq;
  }
  function cartHas(id) { return cartIds().includes(String(id).toLowerCase()); }
  function cartAdd(id) {
    const ids = cartIds();
    const key = String(id).toLowerCase();
    if (ids.includes(key)) return true;
    if (ids.length >= CART_MAX) {
      if (openCartHook) openCartHook(`Your cart is full (${CART_MAX} boxes). Remove one to add another.`);
      return false;
    }
    ids.push(key);
    saveCart(ids);
    return true;
  }
  function cartRemove(id) { saveCart(cartIds().filter(x => x !== String(id).toLowerCase())); }
  function cartClear() { saveCart([]); }

  function cartButtonHtml(id) {
    const on = cartHas(id);
    return `<button type="button" class="cart-btn" data-cart="${esc(id)}" aria-pressed="${on}">${on ? '✓ In cart' : '+ Add to cart'}</button>`;
  }

  // One tap adds; tapping "✓ In cart" opens the drawer (removal lives there,
  // so a stray second tap can't silently drop a box).
  function onCartButton(id) {
    if (cartHas(id)) { if (openCartHook) openCartHook(); return; }
    cartAdd(id);
  }

  function cartTotalCents(ids) {
    if (!lastRows) return null;
    const byId = new Map(lastRows.map(r => [String(r.manifest_id).toLowerCase(), r]));
    let cents = 0;
    for (const id of ids) { const r = byId.get(id); if (r && isLive(r)) cents += Math.round(Number(r.ask_price || 0) * 100); }
    return cents;
  }
  const dollars = cents => '$' + (cents / 100).toLocaleString('en-US', { minimumFractionDigits: cents % 100 ? 2 : 0, maximumFractionDigits: 2 });

  function syncCartUi() {
    const ids = cartIds();
    const n = ids.length;
    const on = !!window.nslCheckoutEnabled;
    document.querySelectorAll('button[data-cart]').forEach(b => {
      const inCart = ids.includes(String(b.getAttribute('data-cart')).toLowerCase());
      // Kill switch off = no cart controls at all, not a relabelled one.
      b.hidden = !on;
      b.setAttribute('aria-pressed', String(inCart));
      b.textContent = inCart ? '✓ In cart' : '+ Add to cart';
    });
    document.querySelectorAll('.box-card[data-id]').forEach(c =>
      c.toggleAttribute('data-in-cart', ids.includes(String(c.getAttribute('data-id')).toLowerCase())));
    document.querySelectorAll('.cart-count').forEach(el => { el.textContent = String(n); });
    document.querySelectorAll('.cart-head-btn').forEach(el => {
      el.hidden = !on;
      el.setAttribute('aria-label', `Cart, ${n} box${n === 1 ? '' : 'es'}`);
    });
    const bar = document.getElementById('cart-bar');
    if (bar) {
      bar.hidden = !(on && n > 0);
      const cents = cartTotalCents(ids);
      bar.querySelector('.cart-bar-text').textContent =
        `${n} box${n === 1 ? '' : 'es'} in your cart` + (cents != null ? ` · ${dollars(cents)}` : '');
    }
  }

  // Legacy global (index.html once called it): add the box and open the cart.
  window.nslBuyBox = id => { if (cartAdd(id) && openCartHook) openCartHook(); };

  // ── cart drawer + phone bar (spec §3) ────────────────────────────────────
  const CART_NOTE = "Pickup in Wake Forest — we'll reach out after payment to arrange it. Free delivery to the Raleigh Flea Market on Fridays.";
  let cart = null;

  function mountCart() {
    if (cart) return cart;
    const actions = document.querySelector('.site-nav .actions, .nav .actions');
    if (actions && !actions.querySelector('.cart-head-btn')) {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'cart-head-btn';
      btn.hidden = true;
      btn.innerHTML = `🛒 Cart <span class="cart-count">0</span>`;
      btn.addEventListener('click', () => openCart());
      actions.appendChild(btn);
    }
    let bar = document.getElementById('cart-bar');
    if (!bar) {
      bar = document.createElement('div');
      bar.id = 'cart-bar';
      bar.className = 'cart-bar';
      bar.hidden = true;
      bar.innerHTML = `<span class="cart-bar-text"></span><button type="button" class="cart-bar-open">View cart →</button>`;
      bar.querySelector('.cart-bar-open').addEventListener('click', () => openCart());
      document.body.appendChild(bar);
    }
    let overlay = document.getElementById('cart-drawer');
    if (!overlay) {
      overlay = document.createElement('div');
      overlay.id = 'cart-drawer';
      overlay.className = 'mf-overlay cart-overlay';
      overlay.hidden = true;
      overlay.setAttribute('role', 'dialog');
      overlay.setAttribute('aria-modal', 'true');
      overlay.setAttribute('aria-labelledby', 'cart-title');
      overlay.innerHTML = `
  <div class="mf-box cart-box">
    <button class="mf-close" type="button" aria-label="Close">&times;</button>
    <div class="mf-head"><h3 id="cart-title">Your cart</h3><p class="mf-sub" id="cart-sub"></p></div>
    <div class="mf-body" id="cart-body"></div>
    <div class="mf-foot cart-foot">
      <p class="cart-notice" id="cart-notice" aria-live="polite" hidden></p>

      <fieldset class="cart-deliv" id="cart-deliv">
        <legend>How do you want them?</legend>
        <label class="deliv-opt"><input type="radio" name="nsl-deliv" value="pickup" checked>
          <span class="deliv-label">Pick up at our Wake Forest warehouse</span><span class="deliv-price">Free</span></label>
        <label class="deliv-opt" id="deliv-opt-delivery"><input type="radio" name="nsl-deliv" value="delivery" disabled>
          <span class="deliv-label">Delivered to you<span class="deliv-to" id="deliv-to"></span></span><span class="deliv-price">${dollars(DELIVERY_CENTS)}</span></label>
        <div class="deliv-zip" id="deliv-zip-row">
          <label for="deliv-zip">Your zip</label>
          <input id="deliv-zip" inputmode="numeric" maxlength="5" autocomplete="postal-code" placeholder="27587">
          <button type="button" class="btn btn-ghost" id="deliv-check">Check</button>
        </div>
        <div class="deliv-addr" id="deliv-addr-row" hidden>
          <label for="deliv-addr">Street address</label>
          <input id="deliv-addr" maxlength="300" autocomplete="street-address" placeholder="123 Main St, Wake Forest">
        </div>
        <label class="deliv-opt"><input type="radio" name="nsl-deliv" value="flea">
          <span class="deliv-label">Meet us at the Raleigh Flea Market on Friday</span><span class="deliv-price">Free</span></label>
      </fieldset>

      <dl class="cart-receipt" id="cart-receipt">
        <div><dt>Subtotal</dt><dd id="cart-sub-amt">$0</dd></div>
        <div id="cart-deliv-line" hidden><dt>Delivery</dt><dd id="cart-deliv-amt">$0</dd></div>
        <div><dt id="cart-tax-label">Sales tax (${TAX_PCT}%)</dt><dd id="cart-tax-amt">$0</dd></div>
      </dl>
      <div class="cart-total"><span>Total</span><strong id="cart-total">$0</strong></div>
      <p class="note" id="cart-note">${esc(CART_NOTE)}</p>
      <button type="button" class="btn btn-primary cart-checkout" id="cart-checkout">Checkout with Square →</button>
    </div>
  </div>`;
      document.body.appendChild(overlay);
    }
    const body = overlay.querySelector('#cart-body');
    const sub = overlay.querySelector('#cart-sub');
    const notice = overlay.querySelector('#cart-notice');
    const total = overlay.querySelector('#cart-total');
    const checkout = overlay.querySelector('#cart-checkout');
    const deliv = {
      set: overlay.querySelector('#cart-deliv'),
      radios: Array.from(overlay.querySelectorAll('input[name="nsl-deliv"]')),
      optDelivery: overlay.querySelector('#deliv-opt-delivery'),
      to: overlay.querySelector('#deliv-to'),
      zipRow: overlay.querySelector('#deliv-zip-row'),
      zip: overlay.querySelector('#deliv-zip'),
      check: overlay.querySelector('#deliv-check'),
      addrRow: overlay.querySelector('#deliv-addr-row'),
      addr: overlay.querySelector('#deliv-addr'),
      price: overlay.querySelector('#deliv-opt-delivery .deliv-price'),
    };
    const receipt = {
      sub: overlay.querySelector('#cart-sub-amt'),
      delLine: overlay.querySelector('#cart-deliv-line'),
      del: overlay.querySelector('#cart-deliv-amt'),
      tax: overlay.querySelector('#cart-tax-amt'),
      taxLabel: overlay.querySelector('#cart-tax-label'),
      note: overlay.querySelector('#cart-note'),
    };
    deliv.radios.forEach(r => r.addEventListener('change', () => {
      if (r.checked) { setDeliveryChoice(r.value); syncDelivery(); renderTotals(); }
    }));
    deliv.check.addEventListener('click', checkZip);
    deliv.zip.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); checkZip(); } });
    deliv.addr.addEventListener('input', () => { try { localStorage.setItem('nsl.addr', deliv.addr.value.trim()); } catch { /* ignore */ } });
    overlay.addEventListener('click', e => { if (e.target === overlay) closeCart(); });
    overlay.querySelector('.mf-close').addEventListener('click', closeCart);
    body.addEventListener('click', e => {
      const rm = e.target.closest('button[data-remove]');
      if (!rm) return;
      cartRemove(rm.getAttribute('data-remove'));
      renderCart();
    });
    checkout.addEventListener('click', checkoutCart);
    // Back from Square's hosted page restores this page from bfcache with the
    // button still reading "One sec…" and possibly a box that just sold.
    window.addEventListener('pageshow', e => {
      if (!e.persisted) return;
      resetCheckoutBtn();
      syncCartUi();
      if (!overlay.hidden) renderCart();
    });
    // Another tab changed the cart.
    window.addEventListener('storage', e => {
      if (e.key !== CART_KEY) return;
      cartMem = null;
      syncCartUi();
      if (!overlay.hidden) renderCart();
    });
    cart = { overlay, body, sub, notice, total, checkout, deliv, receipt };
    openCartHook = openCart;
    return cart;
  }

  function cartNotice(msg) {
    const c = mountCart();
    c.notice.textContent = msg;
    c.notice.hidden = !msg;
  }

  function openCart(noticeText) {
    const c = mountCart();
    if (mf && !mf.overlay.hidden) closeOverlay(mf.overlay);   // drawer replaces the manifest modal
    if (c.overlay.hidden) openOverlay(c.overlay);
    renderCart().then(() => { if (noticeText) cartNotice(noticeText); });
  }
  function closeCart() { if (cart) closeOverlay(cart.overlay); }

  function validateCart(rows) {
    const byId = new Map((rows || []).map(r => [String(r.manifest_id).toLowerCase(), r]));
    const kept = [], removed = [];
    cartIds().forEach(id => {
      const r = byId.get(id);
      if (r && isLive(r) && Number(r.ask_price) > 0) kept.push(r);
      else removed.push(r ? r.pallet_number : null);
    });
    return { kept, removed };
  }

  function removedNotice(removed) {
    const known = removed.filter(n => n != null).map(n => '#' + n);
    if (known.length === removed.length && known.length === 1) return `BOX ${known[0]} was just sold — removed from your cart.`;
    if (known.length === removed.length) return `Boxes ${known.join(', ')} were just sold — removed from your cart.`;
    return 'Some boxes in your cart are no longer available — removed.';
  }

  // Enable/disable the $10 option from what we know about the shopper's zip.
  // If a member signed up on this device we already have it (spec §8.5) and
  // the zip input never appears; otherwise they type it once.
  function syncDelivery() {
    const c = mountCart();
    const z = storedZip();
    const ok = zipQualifies(z);
    const choice = deliveryChoice();
    c.deliv.radios.forEach(r => { r.checked = r.value === choice; });
    c.deliv.optDelivery.querySelector('input').disabled = !ok;
    c.deliv.optDelivery.classList.toggle('is-off', !ok);
    c.deliv.to.textContent = ok ? ` (to ${z})` : '';
    c.deliv.zipRow.hidden = ok;
    c.deliv.addrRow.hidden = !(ok && choice === 'delivery');
    if (c.deliv.addrRow.hidden === false && !c.deliv.addr.value) c.deliv.addr.value = storedAddr();
    c.receipt.note.textContent =
      choice === 'flea' ? (FLEA_NOTE || CART_NOTE)
      : choice === 'delivery' ? "We'll call to schedule the drop — usually within a couple of days."
      : CART_NOTE;
  }

  function checkZip() {
    const c = mountCart();
    const z = (c.deliv.zip.value || '').trim();
    if (!/^\d{5}$/.test(z)) { cartNotice('Enter a 5-digit zip code.'); c.deliv.zip.focus(); return; }
    try { localStorage.setItem('nsl.zip', z); } catch { /* ignore */ }
    if (zipQualifies(z)) {
      cartNotice(`Good news — we deliver to ${z} for ${dollars(DELIVERY_CENTS)}.`);
      setDeliveryChoice('delivery');
    } else {
      cartNotice(`We can't reach ${z} on our own truck — pickup and the Friday flea-market drop are both free.`);
    }
    syncDelivery();
    renderTotals();
  }

  // This has to reproduce Square's arithmetic exactly, not approximate it:
  // a total here that differs from the hosted page is a phone call to the
  // warehouse, not a bug report.
  let goodsCents = 0;
  let goodsLines = [];                  // per-box cents, in the order shown
  // SquarePayloads applies ONE tax at scope LINE_ITEM to every box line and to
  // the delivery service charge, so Square rounds the tax once per line and
  // adds those up. Rounding the cart total instead drifts a cent: two $6.67
  // boxes are 48 + 48 = 96, where round(1334 × 7.25%) = 97.
  const lineTax = cents => Math.round(cents * TAX_PCT / 100);
  function renderTotals() {
    const c = mountCart();
    const choice = deliveryChoice();
    // No goods, no delivery: an empty cart must read $0, not $10 + tax on the fee.
    const del = (goodsCents > 0 && choice === 'delivery') ? DELIVERY_CENTS : 0;
    const taxCents = goodsLines.reduce((sum, cents) => sum + lineTax(cents), 0) + (del ? lineTax(del) : 0);
    // The drawer is mounted before /api/public/checkout-status answers, so the
    // rate and the fee baked into its markup are the defaults. Re-state both
    // from the live config: a label quoting a rate we are not charging is a
    // number we have told the shopper that isn't true.
    c.receipt.taxLabel.textContent = `Sales tax (${TAX_PCT}%)`;
    c.deliv.price.textContent = dollars(DELIVERY_CENTS);
    c.receipt.sub.textContent = dollars(goodsCents);
    c.receipt.delLine.hidden = del === 0;
    c.receipt.del.textContent = dollars(del);
    c.receipt.tax.textContent = dollars(taxCents);
    c.total.textContent = dollars(goodsCents + del + taxCents);
  }

  async function renderCart() {
    const c = mountCart();
    const ids = cartIds();
    cartNotice('');
    c.sub.textContent = '';
    c.total.textContent = '$0';
    c.checkout.disabled = true;
    if (!ids.length) {
      c.body.innerHTML = `<p class="cart-empty">Your cart is empty. <a class="view" href="shop.html?view=all">Shop what's on the floor →</a></p>`;
      c.deliv.set.hidden = true;
      goodsCents = 0;
      goodsLines = [];
      renderTotals();
      return;
    }
    c.deliv.set.hidden = false;
    c.body.innerHTML = '<p class="mf-loading">Checking your boxes…</p>';
    let rows;
    try { rows = await refreshPublicPallets(); }
    catch {
      // Never prune on a failed fetch — the ids are all we have.
      c.body.innerHTML = `<p class="mf-empty">Couldn't check your cart right now — try again in a moment or call ${PHONE}.</p>`;
      return;
    }
    const { kept, removed } = validateCart(rows);
    if (removed.length) {
      saveCart(kept.map(r => r.manifest_id));
      cartNotice(removedNotice(removed));
    }
    if (!kept.length) {
      c.body.innerHTML = `<p class="cart-empty">Everything in your cart just sold. <a class="view" href="shop.html?view=new">See what just dropped →</a></p>`;
      c.deliv.set.hidden = true;
      goodsCents = 0;
      goodsLines = [];
      renderTotals();
      return;
    }
    let cents = 0;
    const lines = [];
    c.body.innerHTML = `<ul class="cart-list">` + kept.map(p => {
      const line = Math.round(Number(p.ask_price) * 100);
      lines.push(line);
      cents += line;
      return `
      <li class="cart-row">
        <span class="cart-thumb"${p.photo_url ? ` style="background-image:url('${esc(p.photo_url)}')"` : ''}></span>
        <span class="cart-info"><span class="box-no">BOX #${esc(p.pallet_number)}</span><span class="cart-name">${esc(p.display_name || ('Box #' + p.pallet_number))}</span></span>
        <span class="cart-price">${money(p.ask_price)}</span>
        <button type="button" class="cart-remove" data-remove="${esc(p.manifest_id)}" aria-label="Remove BOX #${esc(p.pallet_number)}">&times;</button>
      </li>`;
    }).join('') + `</ul>`;
    c.sub.textContent = `${kept.length} box${kept.length === 1 ? '' : 'es'}`;
    goodsCents = cents;
    goodsLines = lines;
    syncDelivery();
    renderTotals();
    c.checkout.disabled = false;
  }

  function resetCheckoutBtn() {
    if (!cart) return;
    cart.checkout.textContent = 'Checkout with Square →';
    cart.checkout.disabled = cartIds().length === 0;
  }

  async function checkoutCart() {
    const c = mountCart();
    const ids = cartIds();
    if (!ids.length) return;
    c.checkout.disabled = true;
    c.checkout.textContent = 'One sec…';
    let leaving = false;                 // heading to Square: leave the button reading "One sec…"
    try {
      const choice = deliveryChoice();
      const addr = choice === 'delivery' ? (c.deliv.addr.value || '').trim() : '';
      if (choice === 'delivery' && !addr) {
        cartNotice('Add the street address for the delivery.');
        c.deliv.addr.focus();
        return;                          // the finally below puts the button back
      }
      let member = '';
      try { member = localStorage.getItem('nsl.member') || ''; } catch { /* ignore */ }
      const r = await fetch('/api/public/checkout', {
        method: 'POST', credentials: 'omit',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ids, delivery: choice, zip: choice === 'delivery' ? storedZip() : null,
                               address: addr || null, memberNumber: member || null }),
      });
      let j = {};
      try { j = await r.json(); } catch { /* non-JSON body */ }
      if (r.ok && j.url) { leaving = true; window.location.href = j.url; return; }   // Square-hosted checkout
      if (r.status === 409) {
        const gone = Array.isArray(j.unavailable) ? j.unavailable.map(x => String(x).toLowerCase()) : null;
        if (gone && gone.length) saveCart(ids.filter(id => !gone.includes(id)));
        await renderCart();                                            // fresh fetch prunes the rest
        cartNotice(j.error || 'Some boxes in your cart are no longer available.');
        return;
      }
      if (r.status === 400 && (j.field === 'zip' || j.field === 'address')) {
        // The server is the authority on the delivery radius; our copy of the
        // zip list can be stale if Rob just edited it.
        cartNotice(j.error || 'Check the delivery address.');
        if (j.field === 'zip') { setDeliveryChoice('pickup'); syncDelivery(); renderTotals(); c.deliv.zip.focus(); }
        else c.deliv.addr.focus();
        return;
      }
      if (r.status === 503) { cartNotice(`Online checkout is paused right now — call us at ${PHONE} and we'll take care of you.`); return; }
      cartNotice(j.error || `Couldn't start checkout — call us at ${PHONE} and we'll take care of you.`);
    } catch {
      cartNotice(`Couldn't start checkout — call us at ${PHONE} and we'll take care of you.`);
    } finally {
      if (!leaving) resetCheckoutBtn();
    }
  }

  // ── shared overlay behaviour (focus restore, Tab wrap, Escape, scroll lock)
  const OVERLAY_FOCUSABLE = 'button:not([disabled]), a[href], input:not([disabled]), [tabindex]:not([tabindex="-1"])';
  const overlayStack = [];
  function openOverlay(overlay, firstFocus) {
    overlayStack.push({ overlay, lastFocus: document.activeElement });
    overlay.hidden = false;
    document.body.style.overflow = 'hidden';
    (firstFocus || overlay.querySelector('.mf-close') || overlay).focus();
  }
  function closeOverlay(overlay) {
    const i = overlayStack.findIndex(s => s.overlay === overlay);
    const entry = i >= 0 ? overlayStack.splice(i, 1)[0] : null;
    overlay.hidden = true;
    if (overlayStack.length === 0) document.body.style.overflow = '';
    if (entry && entry.lastFocus && entry.lastFocus.focus) entry.lastFocus.focus();
  }
  document.addEventListener('keydown', e => {
    const top = overlayStack[overlayStack.length - 1];
    if (!top) return;
    if (e.key === 'Escape') { closeOverlay(top.overlay); return; }
    if (e.key !== 'Tab') return;
    const f = top.overlay.querySelectorAll(OVERLAY_FOCUSABLE);
    if (!f.length) return;
    const first = f[0], last = f[f.length - 1];
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
  });

  // ── manifest modal (§6.5) ────────────────────────────────────────────────
  const MF_NOTE = 'Pickup in Wake Forest · Free delivery to the Raleigh Flea Market every Friday · $10 delivery within 20 miles of our warehouse · Call to claim this box.';
  let mf = null;
  function mountManifestModal() {
    if (mf) return mf;
    let overlay = document.getElementById('manifest-modal');
    if (!overlay) {
      overlay = document.createElement('div');
      overlay.id = 'manifest-modal';
      overlay.className = 'mf-overlay';
      overlay.hidden = true;
      overlay.setAttribute('role', 'dialog');
      overlay.setAttribute('aria-modal', 'true');
      overlay.setAttribute('aria-labelledby', 'mf-title');
      overlay.innerHTML = `
  <div class="mf-box">
    <button class="mf-close" type="button" aria-label="Close">&times;</button>
    <div class="mf-head">
      <h3 id="mf-title">Manifest</h3>
      <p class="mf-sub" id="mf-sub"></p>
    </div>
    <div class="mf-body" id="mf-body"></div>
    <div class="mf-foot">
      <span class="note">${esc(MF_NOTE)}</span>
      <a class="btn btn-primary" href="tel:+19195260112">Call ${PHONE}</a>
    </div>
  </div>`;
      document.body.appendChild(overlay);
    }
    const titleEl = overlay.querySelector('#mf-title');
    const subEl = overlay.querySelector('#mf-sub');
    const bodyEl = overlay.querySelector('#mf-body');
    bodyEl.addEventListener('click', e => {
      const b = e.target.closest('button[data-cart]');
      if (b) onCartButton(b.getAttribute('data-cart'));
    });
    const close = () => closeOverlay(overlay);
    overlay.addEventListener('click', e => { if (e.target === overlay) close(); });
    overlay.querySelector('.mf-close').addEventListener('click', close);
    mf = { overlay, titleEl, subEl, bodyEl, close };
    return mf;
  }

  async function showManifest(id, name) {
    const m = mountManifestModal();
    m.titleEl.textContent = name || 'Manifest';
    m.subEl.textContent = '';
    m.bodyEl.innerHTML = '<p class="mf-loading">Loading manifest…</p>';
    openOverlay(m.overlay);
    try {
      const r = await fetch(`/api/public/pallets/${encodeURIComponent(id)}/items`, { credentials: 'omit' });
      if (!r.ok) throw new Error('http ' + r.status);
      const data = await r.json();
      const p = data.pallet || {};
      const items = (data.items || []).slice().sort((a, b) => (b.is_highlight ? 1 : 0) - (a.is_highlight ? 1 : 0));
      const units = p.unit_count || items.length;
      const weight = fmtWeight(p.weight_lbs);
      const size = p.box_size && SIZE_LABELS[p.box_size] ? SIZE_LABELS[p.box_size] : '';
      m.subEl.textContent = `${units} unit${units === 1 ? '' : 's'} · Est. retail ${money(p.total_msrp)} · Price ${money(p.ask_price)}`
        + (weight ? ` · Ships at ~${weight} lb` : '')
        + (size ? ` · ${size}` : '');
      const blurb = (p.public_description
        ? `<p style="font-style:italic;color:#555;margin:0 0 14px;line-height:1.5;">${esc(p.public_description)}</p>`
        : '') + (window.nslCheckoutEnabled && p.publish_state === 'live'
        ? `<p style="margin:0 0 16px;">${cartButtonHtml(p.manifest_id)} <span style="font-family:'Anton',sans-serif;font-size:20px;color:var(--nc-red);margin-left:10px;">${money(p.ask_price)}</span></p>`
        : '');
      const dlLink = items.length
        ? `<p style="margin:0 0 14px;font-size:14px;"><a class="view" href="/api/public/pallets/${encodeURIComponent(id)}/manifest">⬇ Download this manifest (Excel)</a></p>`
        : '';
      if (items.length === 0) {
        m.bodyEl.innerHTML = blurb + `<p class="mf-empty">No itemized manifest available for this box yet — give us a call for details.</p>`;
        return;
      }
      m.bodyEl.innerHTML = blurb + dlLink + `
        <table class="mf-table">
          <thead><tr><th>Item</th><th>Condition</th><th>Qty</th><th>Est. Retail</th><th></th></tr></thead>
          <tbody>
            ${items.map(it => `
              <tr${it.is_highlight ? ' class="mf-row-featured"' : ''}>
                <td>${it.photo_blob_url ? `<span class="mf-thumb" style="background-image:url('${esc(it.photo_blob_url)}')"></span>` : ''}<span class="mf-item"><strong>${it.is_highlight ? '<span class="mf-star" title="Featured item">★</span>' : ''}${esc(it.title || 'Item')}</strong>${it.brand ? `<small>${esc(it.brand)}</small>` : ''}</span></td>
                <td>${it.condition ? condPill(it.condition) : '—'}</td>
                <td>${it.qty || 1}</td>
                <td>${money(it.est_msrp)}</td>
                <td>${it.title ? `<a class="view" style="white-space:nowrap;font-size:12px;" target="_blank" rel="noopener" href="https://www.google.com/search?q=${encodeURIComponent([it.brand, it.title].filter(Boolean).join(' '))}">Price&nbsp;check&nbsp;→</a>` : ''}</td>
              </tr>`).join('')}
          </tbody>
        </table>`;
    } catch (e) {
      m.bodyEl.innerHTML = `<p class="mf-empty">Couldn't load the manifest right now — please call ${PHONE}.</p>`;
    }
  }
  window.showManifest = showManifest;

  // ── join / member sign-up modal (§6.6) ───────────────────────────────────
  const MEMBER_KEY = 'nsl.member';
  const JOIN_SUB_DEFAULT = "We'll email you first when new boxes drop. Write it down or screenshot this — give your number at the warehouse.";
  const JOIN_SUB_BACK = "Welcome back — that email is already registered. We don't show the number again here; ask at the register and we'll look it up.";
  const JOIN_SUB_STORED = 'You already signed up on this device — give this number at the warehouse.';
  const EMAIL_RE = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;
  let join = null;

  function storedMember() {
    try { return localStorage.getItem(MEMBER_KEY) || ''; } catch { return ''; }
  }
  function rememberMember(n) {
    try { localStorage.setItem(MEMBER_KEY, n); } catch { /* private mode etc. */ }
  }
  function forgetMember() {
    try { localStorage.removeItem(MEMBER_KEY); } catch { /* ignore */ }
  }
  function joinTriggers() {
    return Array.from(document.querySelectorAll('#join-open, [data-open-register]'));
  }
  function relabelTriggers() {
    const n = storedMember();
    if (!n) return;
    joinTriggers().forEach(el => { el.textContent = `★ Member #${n}`; });
  }

  function mountJoinModal() {
    if (join) return join;
    let overlay = document.getElementById('join-modal');
    if (!overlay) {
      overlay = document.createElement('div');
      overlay.id = 'join-modal';
      overlay.className = 'mf-overlay';
      overlay.hidden = true;
      overlay.setAttribute('role', 'dialog');
      overlay.setAttribute('aria-modal', 'true');
      overlay.setAttribute('aria-labelledby', 'join-title');
      overlay.innerHTML = `
  <div class="mf-box join-box">
    <button class="mf-close" type="button" aria-label="Close">&times;</button>
    <div class="mf-head"><h3 id="join-title">Become a member</h3><p class="mf-sub">Free. Get a member number, first dibs on drops, and flea-market deals.</p></div>
    <form id="join-form" class="mf-body" novalidate>
      <div class="join-row"><label>First name <input id="join-first" name="firstName" required maxlength="100" autocomplete="given-name"></label>
                            <label>Last name <input id="join-last" name="lastName" required maxlength="100" autocomplete="family-name"></label></div>
      <label>Email <input id="join-email" name="email" type="email" required maxlength="320" autocomplete="email" inputmode="email"></label>
      <label>Phone <span style="text-transform:none;letter-spacing:0;color:#999;">(optional)</span> <input id="join-phone" name="phone" type="tel" maxlength="30" autocomplete="tel" inputmode="tel"></label>
      <label>Street address <span style="text-transform:none;letter-spacing:0;color:#999;">(optional — helps us tell if you're within 20 miles for delivery)</span> <input id="join-address1" name="address1" maxlength="200" autocomplete="address-line1"></label>
      <label>Apt / Suite <span style="text-transform:none;letter-spacing:0;color:#999;">(optional)</span> <input id="join-address2" name="address2" maxlength="100" autocomplete="address-line2"></label>
      <div class="join-row three"><label>City <input id="join-city" name="city" maxlength="120" autocomplete="address-level2"></label>
                                  <label>State <input id="join-state" name="state" maxlength="2" value="NC" autocomplete="address-level1"></label>
                                  <label>Zip <input id="join-zip" name="zip" maxlength="10" autocomplete="postal-code" inputmode="numeric"></label></div>
      <label>How did you hear about us? <input id="join-how" name="howHeard" maxlength="200"></label>
      <label class="join-hp" aria-hidden="true">Website <input id="join-website" name="website" tabindex="-1" autocomplete="off"></label>
      <p id="join-error" class="join-error" hidden></p>
      <button id="join-submit" class="btn btn-primary" type="submit">Get my member number</button>
    </form>
    <div id="join-result" class="mf-body" hidden>
      <p class="join-big">You're member <strong id="join-number">#2600001</strong></p>
      <p id="join-result-sub">${esc(JOIN_SUB_DEFAULT)}</p>
      <p><a class="join-reset" href="#">Not you? Sign up with a different email</a></p>
    </div>
  </div>`;
      document.body.appendChild(overlay);
    }
    const form = overlay.querySelector('#join-form');
    const result = overlay.querySelector('#join-result');
    const errEl = overlay.querySelector('#join-error');
    const submit = overlay.querySelector('#join-submit');
    const numberEl = overlay.querySelector('#join-number');
    const subEl = overlay.querySelector('#join-result-sub');

    const close = () => { closeOverlay(overlay); };
    const showError = msg => { errEl.textContent = msg; errEl.hidden = false; };
    const clearError = () => { errEl.textContent = ''; errEl.hidden = true; };
    const showResult = (n, sub) => {
      form.hidden = true;
      result.hidden = false;
      numberEl.textContent = n ? `#${n}` : '—';
      numberEl.parentElement.hidden = !n;   // no "You're member —" when there is no number to show
      subEl.textContent = sub;
    };
    const showForm = () => { result.hidden = true; form.hidden = false; clearError(); };

    const open = () => {
      const n = storedMember();
      if (n) showResult(n, JOIN_SUB_STORED); else showForm();
      openOverlay(overlay, n ? null : overlay.querySelector('#join-first'));
    };

    overlay.addEventListener('click', e => { if (e.target === overlay) close(); });
    overlay.querySelector('.mf-close').addEventListener('click', close);
    overlay.querySelector('.join-reset').addEventListener('click', e => { e.preventDefault(); forgetMember(); showForm(); overlay.querySelector('#join-first').focus(); });
    form.addEventListener('input', clearError);

    form.addEventListener('submit', async e => {
      e.preventDefault();
      clearError();
      const v = id => (overlay.querySelector('#' + id).value || '').trim();
      const body = {
        firstName: v('join-first'), lastName: v('join-last'), email: v('join-email'), phone: v('join-phone'),
        address1: v('join-address1'), address2: v('join-address2'),
        city: v('join-city'), state: v('join-state').toUpperCase(), zip: v('join-zip'), howHeard: v('join-how'),
        website: v('join-website'),
      };
      if (!body.firstName) { showError('First name is required.'); overlay.querySelector('#join-first').focus(); return; }
      if (!body.lastName) { showError('Last name is required.'); overlay.querySelector('#join-last').focus(); return; }
      if (!body.email || !EMAIL_RE.test(body.email)) { showError('Enter a valid email address.'); overlay.querySelector('#join-email').focus(); return; }
      if (body.state && !/^[A-Za-z]{2}$/.test(body.state)) { showError('State should be 2 letters (e.g. NC).'); overlay.querySelector('#join-state').focus(); return; }

      submit.disabled = true;
      const prev = submit.textContent;
      submit.textContent = 'One sec…';
      try {
        const r = await fetch('/api/public/register', {
          method: 'POST', credentials: 'omit',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(body),
        });
        let j = {};
        try { j = await r.json(); } catch { /* non-JSON error body */ }
        if (!r.ok) {
          showError(j.error || (r.status === 429
            ? 'Too many signups from this connection — try again in a minute.'
            : `Couldn't sign you up right now — call ${PHONE}.`));
          return;
        }
        const n = j.memberNumber;
        if (n) {
          rememberMember(n);
          relabelTriggers();
          showResult(n, JOIN_SUB_DEFAULT);
        } else if (j.alreadyRegistered) {
          // The API never echoes an existing number back (anyone could type
          // someone else's email) — say so instead of showing a blank number.
          showResult(null, JOIN_SUB_BACK);
        } else {
          showResult(null, "Thanks — you're on the list.");
        }
      } catch {
        showError(`Couldn't sign you up right now — call ${PHONE}.`);
      } finally {
        submit.disabled = false;
        submit.textContent = prev;
      }
    });

    join = { overlay, open, close };
    return join;
  }

  function openJoin() { mountJoinModal().open(); }

  function bindJoinTrigger(el) {
    if (!el || el.dataset.nslJoinBound) return;
    el.dataset.nslJoinBound = '1';
    el.addEventListener('click', e => { e.preventDefault(); openJoin(); });
  }

  // ── page init (§6.1) ─────────────────────────────────────────────────────
  let inited = false;
  function initPage(opts) {
    const o = opts || {};
    if (!inited) {
      inited = true;
      mountJoinModal();
      if (typeof mountCart === 'function') mountCart();          // Task 11 defines it
      // Cart controls render only when the backend says Square checkout is
      // enabled (SQUARE_CHECKOUT_ENABLED app setting — the kill switch).
      checkoutReady().then(syncCartUi);
    }
    if (o.joinTrigger) document.querySelectorAll(o.joinTrigger).forEach(bindJoinTrigger);
    joinTriggers().forEach(bindJoinTrigger);
    relabelTriggers();
    return checkoutReady();
  }

  let checkoutProbe = null;
  // Delivery config, filled from /api/public/checkout-status. The zip list is
  // Rob's delivery radius (spec §8.4), not customer data; the server re-checks
  // every zip on checkout, so this copy is only here to enable/disable a radio
  // without a round trip.
  let TAX_PCT = 7.25, DELIVERY_CENTS = 1000, DELIVERY_ZIPS = [], FLEA_NOTE = '';
  function checkoutReady() {
    if (!checkoutProbe) {
      checkoutProbe = fetch('/api/public/checkout-status', { credentials: 'omit' })
        .then(x => x.json())
        .then(cs => {
          window.nslCheckoutEnabled = !!cs.enabled;
          if (Number(cs.cartMax) > 0) CART_MAX = Number(cs.cartMax);
          if (Number(cs.taxPercent) > 0) TAX_PCT = Number(cs.taxPercent);
          if (Number(cs.deliveryCents) > 0) DELIVERY_CENTS = Number(cs.deliveryCents);
          DELIVERY_ZIPS = Array.isArray(cs.deliveryZips) ? cs.deliveryZips.map(String) : [];
          FLEA_NOTE = cs.fleaNote || '';
          return window.nslCheckoutEnabled;
        })
        .catch(() => false);
    }
    return checkoutProbe;
  }

  // ── delivery choice (spec §8.3) ──────────────────────────────────────────
  // Remembered per device, like the cart itself. 'pickup' is always the safe
  // default: it is free and it is what NSL did before this existed.
  const DELIV_KEY = 'nsl.delivery';
  function storedZip()  { try { return localStorage.getItem('nsl.zip')  || ''; } catch { return ''; } }
  function storedAddr() { try { return localStorage.getItem('nsl.addr') || ''; } catch { return ''; } }
  function zipQualifies(z) { return /^\d{5}$/.test(z) && DELIVERY_ZIPS.includes(z); }
  function deliveryChoice() {
    let d = 'pickup';
    try { d = localStorage.getItem(DELIV_KEY) || 'pickup'; } catch { /* private mode */ }
    if (d === 'delivery' && !zipQualifies(storedZip())) return 'pickup';   // never leave a $10 selected that no longer qualifies
    return (d === 'delivery' || d === 'flea') ? d : 'pickup';
  }
  function setDeliveryChoice(d) { try { localStorage.setItem(DELIV_KEY, d); } catch { /* ignore */ } }

  // Auto-bind openers even on pages that never call initPage.
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => { joinTriggers().forEach(bindJoinTrigger); relabelTriggers(); });
  } else {
    joinTriggers().forEach(bindJoinTrigger);
    relabelTriggers();
  }

  window.NSL = {
    // data
    fetchPublicPallets, filterByView, VIEW_DEFS, SIZE_LABELS, CONDITION_DEFS, normalizeCondition,
    // rendering
    boxCardHtml, renderBoxCards, renderJustDropped, renderRecentlySold, renderCounts, condPill, condMixHtml,
    // modal + checkout
    showManifest, initPage, openJoin, checkoutReady,
    // cart
    cartIds, cartHas, cartAdd, cartRemove, cartClear, cartButtonHtml, syncCartUi, refreshPublicPallets, openCart,
    // helpers
    esc, money, pctOfMsrp,
  };
})();
