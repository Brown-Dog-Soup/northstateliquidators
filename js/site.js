/* ============================================================================
   North State Liquidators — shared public script (Wishlist 4, §6.1)

   Classic script (NOT a module). Exposes window.NSL plus the legacy globals
   index.html relied on: window.nslCheckoutEnabled, window.nslBuyBox,
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
  function fetchPublicPallets() {
    if (!palletsPromise) {
      palletsPromise = fetch('/api/public/pallets', { credentials: 'omit' })
        .then(r => { if (!r.ok) throw new Error('http ' + r.status); return r.json(); })
        .then(rows => Array.isArray(rows) ? rows : [])
        .catch(err => { palletsPromise = null; throw err; });
    }
    return palletsPromise;
  }

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
<article class="box-card" data-id="${esc(p.manifest_id)}" data-state="${esc(p.publish_state)}" data-size="${esc(p.box_size || '')}">
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
      ${window.nslCheckoutEnabled && live ? `<a class="view buy" href="#" data-buy="${esc(p.manifest_id)}">Buy now →</a>` : ''}
    </div>
  </div>
</article>`;
  }

  function bindCardClicks(mount) {
    if (mount.dataset.nslBound) return;
    mount.dataset.nslBound = '1';
    mount.addEventListener('click', e => {
      const buy = e.target.closest('a[data-buy]');
      if (buy && mount.contains(buy)) { e.preventDefault(); buyBox(buy.getAttribute('data-buy'), buy); return; }
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

  // ── checkout (same behaviour index.html had inline) ──────────────────────
  window.nslCheckoutEnabled = window.nslCheckoutEnabled || false;

  async function buyBox(id, el) {
    const prev = el ? el.textContent : '';
    if (el) el.textContent = 'One sec…';
    try {
      const r = await fetch(`/api/public/checkout/${encodeURIComponent(id)}`, { method: 'POST', credentials: 'omit' });
      const j = await r.json();
      if (!r.ok) { alert(j.error || 'This box is no longer available.'); location.reload(); return; }
      window.location.href = j.url;   // Square-hosted checkout
    } catch {
      if (el) el.textContent = prev;
      alert(`Couldn't start checkout — call us at ${PHONE} and we'll take care of you.`);
    }
  }
  window.nslBuyBox = buyBox;

  // ── manifest modal (§6.5) ────────────────────────────────────────────────
  const MF_NOTE = 'Pickup in Wake Forest · Free delivery to the Raleigh Flea Market every Friday · $10 delivery within 20 miles of our warehouse · We ship. Call to claim this box.';
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
    const close = () => { overlay.hidden = true; document.body.style.overflow = ''; };
    overlay.addEventListener('click', e => { if (e.target === overlay) close(); });
    overlay.querySelector('.mf-close').addEventListener('click', close);
    document.addEventListener('keydown', e => { if (e.key === 'Escape' && !overlay.hidden) close(); });
    mf = { overlay, titleEl, subEl, bodyEl, close };
    return mf;
  }

  async function showManifest(id, name) {
    const m = mountManifestModal();
    m.titleEl.textContent = name || 'Manifest';
    m.subEl.textContent = '';
    m.bodyEl.innerHTML = '<p class="mf-loading">Loading manifest…</p>';
    m.overlay.hidden = false;
    document.body.style.overflow = 'hidden';
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
        ? `<p style="margin:0 0 16px;"><a href="#" data-buy-modal="${esc(p.manifest_id)}" style="display:inline-block;background:#002868;color:#fff;font-weight:700;padding:12px 26px;text-decoration:none;">Buy this box — ${money(p.ask_price)}</a></p>`
        : '');
      const dlLink = items.length
        ? `<p style="margin:0 0 14px;font-size:14px;"><a class="view" href="/api/public/pallets/${encodeURIComponent(id)}/manifest">⬇ Download this manifest (Excel)</a></p>`
        : '';
      const bindBuy = () => m.bodyEl.querySelector('a[data-buy-modal]')?.addEventListener('click', e => {
        e.preventDefault();
        buyBox(e.currentTarget.getAttribute('data-buy-modal'), e.currentTarget);
      });
      if (items.length === 0) {
        m.bodyEl.innerHTML = blurb + `<p class="mf-empty">No itemized manifest available for this box yet — give us a call for details.</p>`;
        bindBuy();
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
      bindBuy();
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
    let lastFocus = null;

    const close = () => { overlay.hidden = true; document.body.style.overflow = ''; if (lastFocus && lastFocus.focus) lastFocus.focus(); };
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
      lastFocus = document.activeElement;
      const n = storedMember();
      if (n) showResult(n, JOIN_SUB_STORED); else showForm();
      overlay.hidden = false;
      document.body.style.overflow = 'hidden';
      const first = n ? overlay.querySelector('.mf-close') : overlay.querySelector('#join-first');
      if (first) first.focus();
    };

    overlay.addEventListener('click', e => { if (e.target === overlay) close(); });
    overlay.querySelector('.mf-close').addEventListener('click', close);
    document.addEventListener('keydown', e => { if (e.key === 'Escape' && !overlay.hidden) close(); });
    overlay.querySelector('.join-reset').addEventListener('click', e => { e.preventDefault(); forgetMember(); showForm(); overlay.querySelector('#join-first').focus(); });
    form.addEventListener('input', clearError);

    form.addEventListener('submit', async e => {
      e.preventDefault();
      clearError();
      const v = id => (overlay.querySelector('#' + id).value || '').trim();
      const body = {
        firstName: v('join-first'), lastName: v('join-last'), email: v('join-email'), phone: v('join-phone'),
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
      // Online checkout: Buy buttons render only when the backend says Square
      // checkout is enabled (SQUARE_CHECKOUT_ENABLED app setting — the kill switch).
      checkoutReady();
      mountJoinModal();
    }
    if (o.joinTrigger) document.querySelectorAll(o.joinTrigger).forEach(bindJoinTrigger);
    joinTriggers().forEach(bindJoinTrigger);
    relabelTriggers();
    // Returns the checkout probe so callers that render Buy buttons can await it.
    return checkoutReady();
  }

  let checkoutProbe = null;
  function checkoutReady() {
    if (!checkoutProbe) {
      checkoutProbe = fetch('/api/public/checkout-status', { credentials: 'omit' })
        .then(x => x.json())
        .then(cs => { window.nslCheckoutEnabled = !!cs.enabled; return window.nslCheckoutEnabled; })
        .catch(() => false);
    }
    return checkoutProbe;
  }

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
    boxCardHtml, renderBoxCards, renderJustDropped, renderRecentlySold, condPill, condMixHtml,
    // modal + checkout
    showManifest, buyBox, initPage, openJoin, checkoutReady,
    // helpers
    esc, money, pctOfMsrp,
  };
})();
