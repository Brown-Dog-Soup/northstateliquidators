import { apiClient, toast, fmtMoney } from './api.js';

const $ = sel => document.querySelector(sel);
const codeEl  = $('#code');
const lookup  = $('#lookup');
const confirmBtn = $('#confirm');   // not `confirm` — that would shadow window.confirm()
const recent  = $('#recent');
const meEl    = $('#me');
const palletEl= $('#active-pallet');

let activePallet = null;
let lookupResult = null;
let recentItems  = [];
let sellPriceTouched = false;  // user has typed in the sell-price field this scan

// Condition → fraction of ref price we suggest as a sell price.
// Receivers can override by typing in the field directly.
const SELL_MULT = {
  new:             0.80,
  open_box:        0.60,
  untested:        0.50,
  customer_return: 0.40,
  damaged:         0.20
};

// ---- bootstrap ---------------------------------------------------------
init();
async function init() {
  // who am I
  const me = await apiClient.me();
  meEl.innerHTML = me ? `${me.userDetails} · <a href="/logout">sign out</a>` : `<a href="/login">sign in</a>`;

  await loadPalletPicker();
  await loadRecent();
  codeEl.focus();
}

// Populate the active-pallet dropdown above the scan field. Filters to "draft"
// pallets — non-archived and still in the receiving phase. Restores the last
// selection from localStorage so a refresh doesn't lose context. Confirm Scan
// stays disabled until a pallet is picked AND a code is in the box.
async function loadPalletPicker() {
  const picker = $('#pallet-picker');
  const list = await apiClient.pallets();
  // Scan to boxes you're still building or have live: draft (default) or live.
  // Ghost/sold/archived boxes are not scannable targets.
  const draft = list.filter(p => !p.archived_at && (p.publish_state === 'draft' || p.publish_state === 'live' || (!p.publish_state && !p.is_ghost)));

  const storedId = localStorage.getItem('nsl.active.pallet');
  picker.innerHTML = '<option value="">— pick a pallet to scan to —</option>' +
    draft.map(p => `<option value="${p.manifest_id}"${p.manifest_id === storedId ? ' selected' : ''}>BOX #${p.pallet_number ?? '—'} — ${escape(p.display_name || `Pallet #${p.pallet_number}`)} (${p.unit_count || 0})</option>`).join('');

  activePallet = draft.find(p => p.manifest_id === storedId) || null;
  updatePalletDisplay();

  picker.addEventListener('change', async () => {
    const id = picker.value;
    activePallet = draft.find(p => p.manifest_id === id) || null;
    if (activePallet) localStorage.setItem('nsl.active.pallet', activePallet.manifest_id);
    else              localStorage.removeItem('nsl.active.pallet');
    updatePalletDisplay();
    await loadRecent();
  });
}

function updatePalletDisplay() {
  if (activePallet) {
    palletEl.textContent = `BOX #${activePallet.pallet_number ?? '—'} · ${activePallet.display_name} · ${activePallet.unit_count || 0} items`;
  } else {
    palletEl.textContent = 'no pallet selected';
  }
  // Re-evaluate Confirm-button state since pallet is required.
  syncConfirmEnabled();
}

// Single source of truth for whether Confirm Scan should be enabled.
// Required: an active pallet AND something in the code box. The lookup
// itself can flip the button via lookupResult; this function honors that
// when a pallet is selected.
function syncConfirmEnabled() {
  if (!activePallet) { confirmBtn.disabled = true; return; }
  if (!codeEl.value.trim()) { confirmBtn.disabled = true; return; }
  // Otherwise let the lookup state drive — renderLookup() flips it on
  // a successful match. If lookup hasn't run yet, leave whatever the
  // lookup logic decided.
}

// ---- scan flow ---------------------------------------------------------
let lookupTimer;
codeEl.addEventListener('input', () => {
  clearTimeout(lookupTimer);
  syncConfirmEnabled();   // typing without a pallet picked still keeps Confirm off
  if (codeEl.value.trim().length < 6) {
    renderLookup(null);
    return;
  }
  lookupTimer = setTimeout(doLookup, 200);
});
codeEl.addEventListener('keydown', e => {
  // HW0009 sends Enter after each scan; treat Enter as "lookup now and focus confirm"
  if (e.key === 'Enter') {
    e.preventDefault();
    clearTimeout(lookupTimer);
    doLookup().then(() => confirmBtn.focus());
  }
});

// --- keep the hardware scanner aimed at the scan box --------------------
// A USB barcode scanner types into whatever element has focus. On a warehouse
// tablet, receivers often tap elsewhere (the no-barcode lookup, a button, the
// pallet picker), which would send the next scan into the wrong field. Pull
// focus back to the scan box when the tab is re-activated or the user taps
// neutral space. Deliberate typing in a real field is left alone — clicks on an
// input/select/textarea/button/link/summary/label are respected.
function refocusScan() { try { codeEl.focus(); } catch { /* ignore */ } }
window.addEventListener('focus', refocusScan);
document.addEventListener('click', e => {
  if (e.target.closest('input, select, textarea, button, a, summary, label')) return;
  refocusScan();
});

async function doLookup() {
  const code = codeEl.value.trim();
  if (!code) return;
  try {
    const res = await fetch(`/api/lookup/${encodeURIComponent(code)}`, { credentials: 'same-origin' });
    if (res.status === 404) { renderLookup(null); return; }
    if (res.status === 422) {
      const body = await res.json().catch(() => ({}));
      lookupResult = null;
      lookup.innerHTML = `<div class="lookup-empty" style="color:#b00;">${escape(body.message || 'Invalid barcode — please rescan.')}</div>`;
      confirmBtn.disabled = true;
      toast(body.message || 'Invalid barcode — rescan', 'err', 3000);
      return;
    }
    if (!res.ok) throw new Error(`lookup ${res.status}`);
    lookupResult = await res.json();
    renderLookup(lookupResult);
  } catch (err) {
    toast(`Lookup error: ${err.message}`, 'err', 3000);
  }
}

function renderLookup(r) {
  if (!r) {
    lookupResult = null;
    lookup.innerHTML = `<div class="lookup-empty">${codeEl.value.trim() ? `No catalog match for ${escape(codeEl.value)} — will be flagged for manual entry.` : 'Scan a code to see product info.'}</div>`;
    confirmBtn.disabled = !codeEl.value.trim() || !activePallet;
    return;
  }

  const stockImg = r.image_url
    ? `
      <div style="display:flex;gap:16px;align-items:flex-start;margin-top:12px;padding-top:12px;border-top:1px solid var(--rule);">
        <img src="${escape(r.image_url)}" alt="stock photo"
             style="width:120px;height:120px;object-fit:contain;background:#fff;border:1px solid var(--rule);flex-shrink:0;"
             onerror="this.parentElement.style.display='none'">
        <div style="flex:1;min-width:0;">
          <label style="font-family:'JetBrains Mono',monospace;font-size:11px;letter-spacing:0.1em;text-transform:uppercase;color:#555;display:block;margin-bottom:6px;">Stock photo from ${r.match_source}</label>
          <label style="display:flex;align-items:center;gap:8px;cursor:pointer;font-family:Inter;text-transform:none;letter-spacing:0;color:var(--ink);font-size:14px;">
            <input type="checkbox" id="use-stock-photo" checked style="width:20px;height:20px;cursor:pointer;">
            Use this photo for the line item
          </label>
          <div style="font-size:12px;color:#666;margin-top:6px;">Uncheck if you want to take your own photo (or use the camera input below).</div>
        </div>
      </div>`
    : '';

  // The lookup card shows MSRP / COST / PRICE side by side so the receiver
  // can see all three values from the manifest before hitting Confirm. COST
  // and PRICE only appear when the catalog has them — UPCitemdb fallback
  // hits don't carry cost or wholesale, so those columns stay blank for
  // off-spreadsheet UPCs (genuine limitation of the public source).
  const priceBlock = `
    <div style="display:flex;gap:24px;align-items:flex-end;flex-wrap:wrap;">
      <div>
        <span style="font-family:'JetBrains Mono',monospace;font-size:11px;letter-spacing:0.1em;text-transform:uppercase;color:#888;display:block;font-weight:400;margin-bottom:2px;">MSRP</span>
        <span style="font-size:22px;font-weight:700;color:#222;">${fmtMoney(r.msrp)}</span>
      </div>
      <div>
        <span style="font-family:'JetBrains Mono',monospace;font-size:11px;letter-spacing:0.1em;text-transform:uppercase;color:#888;display:block;font-weight:400;margin-bottom:2px;">Cost</span>
        <span style="font-size:22px;font-weight:700;color:#222;">${r.unit_cost != null ? fmtMoney(r.unit_cost) : '—'}</span>
      </div>
      <div>
        <span style="font-family:'JetBrains Mono',monospace;font-size:11px;letter-spacing:0.1em;text-transform:uppercase;color:#0a5;display:block;font-weight:700;margin-bottom:2px;">Price</span>
        <span style="font-size:22px;font-weight:700;color:#0a5;">${r.wholesale_price != null ? fmtMoney(r.wholesale_price) : '—'}</span>
      </div>
      ${r.market_price != null ? `
      <div style="padding-left:24px;border-left:1px solid var(--rule);">
        <span style="font-family:'JetBrains Mono',monospace;font-size:11px;letter-spacing:0.1em;text-transform:uppercase;color:#a06400;display:block;font-weight:700;margin-bottom:2px;">Market</span>
        <span style="font-size:22px;font-weight:700;color:#a06400;">${fmtMoney(r.market_price)}</span>
      </div>` : ''}
    </div>
    ${!r.lpn ? `<div style="margin-top:8px;padding:8px 10px;background:#fff8e0;border:1px solid #e6d68f;font-size:13px;color:#6b5900;">
      No manifest match for this barcode — MSRP, cost and price aren't available.
      If the item has an <b>LPN sticker</b>, scan that instead to pull our numbers.
    </div>` : ''}`;

  lookup.innerHTML = `
    <div class="lookup-title">${escape(r.title || '')}</div>
    <div class="lookup-meta">
      <b>Brand:</b> ${escape(r.brand || '—')}  ·
      <b>Match:</b> ${r.match_source}  ·
      <b>LPN:</b> ${escape(r.lpn || '—')}  ·
      <b>UPC:</b> ${escape(r.upc || '—')}
    </div>
    ${r.description ? `<div class="lookup-desc" style="margin:8px 0 12px;font-size:13px;color:#555;line-height:1.45;max-height:96px;overflow:auto;">${escape(r.description)}</div>` : ''}
    ${priceBlock}
    <span class="lookup-condition">${escape(r.condition || 'unknown')}</span>
    ${stockImg}
  `;

  // pre-pick condition if catalog provides one. Map normalizes the manifest's
  // vocabulary (USED_GOOD, NEW, etc.) onto the receiver-visible dropdown values.
  // The condition-hint span makes it visible when the catalog drove the choice
  // — otherwise receivers can't tell whether the field was auto-set or just
  // sitting at the default 'untested'.
  const condHint = $('#condition-hint');
  const cond = (r.condition || '').toLowerCase().trim();
  const map = {
    'used_good':       'open_box',
    'used':            'open_box',
    'new':             'new',
    'open_box':        'open_box',
    'damaged':         'damaged',
    'salvage':         'damaged',
    'customer_return': 'customer_return',
    'untested':        'untested'
  };
  const sel = $('#condition');
  if (map[cond] && [...sel.options].some(o => o.value === map[cond])) {
    sel.value = map[cond];
    if (condHint) condHint.textContent = `(from manifest: ${r.condition})`;
  } else if (cond) {
    // Catalog had a value we don't have a dropdown option for — surface it raw
    // so the receiver knows what the manifest said and can pick the closest match.
    if (condHint) condHint.textContent = `(manifest says: ${r.condition} — pick closest)`;
  } else {
    if (condHint) condHint.textContent = '';
  }

  suggestSellPrice();
  confirmBtn.disabled = !activePallet;
}

// Compute and (unless the user has manually edited) fill in a suggested
// sell price = ref price × condition multiplier.
function suggestSellPrice() {
  const sp = $('#sell-price');
  const hint = $('#sell-price-hint');
  // Prefer the real market resale price as the basis; fall back to manifest MSRP.
  const ref = lookupResult?.market_price ?? lookupResult?.msrp;
  const cond = $('#condition').value;
  const mult = SELL_MULT[cond] ?? 0.5;

  if (!ref) {
    if (hint) hint.textContent = '';
    return;
  }
  const suggested = Math.round(ref * mult * 100) / 100;
  if (hint) hint.textContent = `(suggested ${(mult * 100).toFixed(0)}% of $${ref.toFixed(2)})`;
  if (!sellPriceTouched) sp.value = suggested.toFixed(2);
}

// Recalculate suggestion when the receiver changes condition
$('#condition').addEventListener('change', suggestSellPrice);
// Mark the field as user-edited so we stop overwriting it
$('#sell-price').addEventListener('input', () => { sellPriceTouched = true; });

confirmBtn.addEventListener('click', async () => {
  if (!codeEl.value.trim()) return;
  if (!activePallet) { toast('No active pallet — create one in Admin first.', 'err', 3000); return; }
  confirmBtn.disabled = true;
  confirmBtn.textContent = 'Saving…';

  try {
    // Decide which photo (if any) to attach.
    // Priority: receiver's own camera shot > stock image from lookup > none
    const file = $('#photo').files[0];
    const useStock = $('#use-stock-photo')?.checked && lookupResult?.image_url && !file;
    const stockUrl = useStock ? lookupResult.image_url : null;

    const sellRaw = $('#sell-price').value.trim();
    const sellPrice = sellRaw === '' ? null : Number(sellRaw);
    const lr = lookupResult;
    const record = {
      code:       codeEl.value.trim(),
      qty:        Number($('#qty').value) || 1,
      condition:  $('#condition').value,
      sellPrice:  Number.isFinite(sellPrice) ? sellPrice : null,
      manifestId: activePallet.manifest_id,
      photoUrl:   stockUrl,   // sp_RecordScan stores this on line_items.photo_blob_url
      // Carry the lookup result through so non-catalog hits (UPCitemdb) still
      // persist title/brand/category. sp_RecordScan prefers lpn_catalog values
      // when present, falls back to these.
      title:          lr?.title ?? null,
      brand:          lr?.brand ?? null,
      category:       lr?.category ?? null,
      // Persist a reference price even for off-catalog UPCs: manifest MSRP if we
      // have it, otherwise the market price from the lookup provider.
      msrp:           lr?.msrp ?? lr?.market_price ?? null,
      matchSource:    lr?.match_source ?? null,
      wholesalePrice: lr?.wholesale_price ?? null,  // PRICE column on Recent list
      description:    lr?.description ?? null,        // carried so a UPCitemdb hit keeps its description
      // Names the exact catalog row the lookup matched. Without it, a bridged
      // hit (retail UPC → ASIN → catalog) re-probes by the raw UPC server-side,
      // misses, and drops cost/price from the recorded line item.
      lpn:            lr?.lpn ?? null
    };
    const result = await apiClient.scan(record);

    // If receiver took their own photo, upload it (overrides any stock URL)
    if (file) {
      try { await apiClient.uploadPhoto('item', result.line_item_id, file); }
      catch (e) { toast(`Photo upload failed: ${e.message}`, 'err', 4000); }
    }

    toast(`Logged: ${result.title || 'item'}`, 'ok', 1500);
    resetForm();
    await loadRecent();
  } catch (err) {
    toast(`Save failed: ${err.message}`, 'err', 4000);
    confirmBtn.disabled = false;
  } finally {
    confirmBtn.textContent = 'Confirm Scan';
  }
});

// Decline = discard the in-flight scan without recording it. No DB write,
// no enrichment_log entry — receivers use this when the lookup pulled the
// wrong product or the item is unfit. Just resets the form.
$('#decline')?.addEventListener('click', () => {
  if (!codeEl.value.trim() && !lookupResult) return;   // nothing to decline
  resetForm();
  toast('Scan declined — discarded', 'ok', 1200);
});

function resetForm() {
  codeEl.value = '';
  $('#qty').value = '1';
  $('#condition').value = 'untested';
  $('#photo').value = '';
  $('#sell-price').value = '';
  $('#sell-price-hint').textContent = '';
  const ch = $('#condition-hint'); if (ch) ch.textContent = '';
  sellPriceTouched = false;
  renderLookup(null);
  codeEl.focus();
}

async function loadRecent() {
  if (!activePallet) return;
  try {
    const detail = await apiClient.pallet(activePallet.manifest_id);
    const allItems = detail.items || [];
    recentItems = allItems.slice(0, 8);
    palletEl.textContent = `${detail.pallet.display_name} · ${detail.pallet.unit_count || 0} items`;

    // Pallet-wide totals across ALL items on this manifest, not just the 8
    // recent ones — Rob asked for the running totals at the top of the page.
    const totals = allItems.reduce((acc, it) => {
      const q = Number(it.qty) || 1;
      acc.msrp  += Number(it.est_msrp        || 0) * q;
      acc.cost  += Number(it.unit_cost       || 0) * q;
      acc.price += Number(it.wholesale_price || 0) * q;
      return acc;
    }, { msrp: 0, cost: 0, price: 0 });
    const totalsEl = $('#pallet-totals');
    if (totalsEl) {
      totalsEl.innerHTML = `
        <span><b style="color:#888;letter-spacing:0.1em;text-transform:uppercase;font-size:10px;">MSRP </b>${fmtMoney(totals.msrp)}</span>
        <span><b style="color:#888;letter-spacing:0.1em;text-transform:uppercase;font-size:10px;">COST </b>${fmtMoney(totals.cost)}</span>
        <span><b style="color:#0a5;letter-spacing:0.1em;text-transform:uppercase;font-size:10px;">PRICE </b>${fmtMoney(totals.price)}</span>`;
    }

    recent.innerHTML = recentItems.map(it => `
      <div class="item-row" data-id="${it.id}">
        <div class="thumb"${it.photo_blob_url ? ` style="background-image:url('${escape(it.photo_blob_url)}')"` : ''}></div>
        <div class="body">
          <h4>${escape(it.title || it.lpn || it.upc || '(no title)')}</h4>
          <div class="meta">qty ${it.qty} · ${escape(it.condition || '—')} · ${escape(it.brand || '')} · ${escape((it.lpn || it.upc || '').slice(0, 16))}</div>
        </div>
        <div style="display:flex;flex-direction:column;align-items:flex-end;gap:2px;font-family:'JetBrains Mono',monospace;font-size:12px;min-width:120px;">
          <div><span style="color:#888;letter-spacing:0.1em;text-transform:uppercase;font-size:10px;">MSRP </span>${fmtMoney(it.est_msrp)}</div>
          <div><span style="color:#888;letter-spacing:0.1em;text-transform:uppercase;font-size:10px;">COST </span>${fmtMoney(it.unit_cost)}</div>
          <div><span style="color:#0a5;letter-spacing:0.1em;text-transform:uppercase;font-size:10px;font-weight:700;">PRICE </span><b>${fmtMoney(it.wholesale_price)}</b></div>
          <div style="display:flex;gap:6px;margin-top:2px;">
            <button class="edit-scan" data-id="${it.id}" title="Edit this item" style="background:none;border:1px solid #ccc;color:#444;padding:2px 8px;font-size:11px;cursor:pointer;">edit</button>
            <button class="undo-scan" data-id="${it.id}" title="Remove this scan" style="background:none;border:1px solid #d4ada6;color:#b00;padding:2px 8px;font-size:11px;cursor:pointer;">✕ undo</button>
          </div>
        </div>
      </div>
      <div class="item-edit" data-edit="${it.id}" hidden style="background:#fff8e0;border:1px solid #e6d68f;padding:14px 16px;margin:-1px 0 8px;">
        <div class="row" style="gap:12px;">
          <div class="col field" style="min-width:160px;"><label>Title</label><input type="text" data-f="title" value="${escape(it.title || '')}"></div>
          <div class="col field" style="min-width:120px;"><label>Brand</label><input type="text" data-f="brand" value="${escape(it.brand || '')}"></div>
          <div class="col field" style="max-width:90px;"><label>Qty</label><input type="number" min="1" data-f="qty" value="${it.qty}"></div>
          <div class="col field" style="max-width:160px;"><label>Condition</label>
            <select data-f="condition">
              ${['new','open_box','damaged','untested','customer_return'].map(c => `<option value="${c}"${(it.condition||'')===c?' selected':''}>${c}</option>`).join('')}
            </select>
          </div>
          <div class="col field" style="max-width:120px;"><label>Sell price</label><input type="number" step="0.01" min="0" data-f="sellPrice" value="${it.est_resale ?? ''}"></div>
        </div>
        <div class="field" style="margin-top:8px;"><label>Description</label><textarea data-f="description" rows="3">${escape(it.description || '')}</textarea></div>
        <div class="field" style="margin-top:8px;"><label>Notes</label><textarea data-f="notes" rows="2">${escape(it.notes || '')}</textarea></div>
        <div style="display:flex;gap:8px;margin-top:8px;">
          <button class="btn btn-primary save-scan" data-id="${it.id}">Save changes</button>
          <button class="btn btn-ghost cancel-scan" data-id="${it.id}">Cancel</button>
        </div>
      </div>
    `).join('') || '<div class="lookup-empty">No items yet on this pallet.</div>';

    // Edit toggles the inline editor for that row (closing any other open one).
    document.querySelectorAll('.edit-scan').forEach(b => b.addEventListener('click', e => {
      const id = e.currentTarget.dataset.id;
      const panel = document.querySelector(`.item-edit[data-edit="${id}"]`);
      if (!panel) return;
      const willOpen = panel.hidden;
      document.querySelectorAll('.item-edit').forEach(el => { el.hidden = true; });
      panel.hidden = !willOpen;
    }));
    document.querySelectorAll('.cancel-scan').forEach(b => b.addEventListener('click', e => {
      const panel = document.querySelector(`.item-edit[data-edit="${e.currentTarget.dataset.id}"]`);
      if (panel) panel.hidden = true;
    }));
    document.querySelectorAll('.save-scan').forEach(b => b.addEventListener('click', async e => {
      const id = e.currentTarget.dataset.id;
      const panel = document.querySelector(`.item-edit[data-edit="${id}"]`);
      const fields = {};
      panel.querySelectorAll('[data-f]').forEach(el => {
        const k = el.dataset.f;
        const v = el.value.trim();
        if (k === 'qty')            fields[k] = v === '' ? null : Number(v);
        else if (k === 'sellPrice') fields[k] = v === '' ? null : Number(v);
        else                        fields[k] = v;
      });
      try {
        await apiClient.patchItem(id, fields);
        toast('Saved', 'ok');
        await loadRecent();
      } catch (err) { toast(`Save failed: ${err.message}`, 'err', 4000); }
    }));

    document.querySelectorAll('.undo-scan').forEach(b => b.addEventListener('click', async e => {
      const id = e.currentTarget.dataset.id;
      const it = recentItems.find(i => i.id === id);
      const label = it?.title || it?.lpn || it?.upc || 'this scan';
      if (!confirm(`Undo scan: "${label}"?`)) return;
      try {
        await apiClient.deleteItem(id);
        toast('Removed', 'ok');
        await loadRecent();
      } catch (err) { toast(`Remove failed: ${err.message}`, 'err', 4000); }
    }));
  } catch (e) { /* ignore */ }
}

// ---- #6 no-barcode inventory lookup -----------------------------------
// Search inventory (any word order) and add a quantity of a match straight to
// the active pallet — for goods that never had a barcode to scan.
const invSearch  = $('#inv-search');
const invResults = $('#inv-results');
let invTimer;

invSearch?.addEventListener('input', () => {
  clearTimeout(invTimer);
  const q = invSearch.value.trim();
  if (q.length < 2) { invResults.innerHTML = ''; return; }
  invTimer = setTimeout(() => runInvSearch(q), 250);
});

async function runInvSearch(q) {
  if (!q) { invResults.innerHTML = ''; return; }
  invResults.innerHTML = '<div class="lookup-empty">Searching…</div>';
  try {
    const rows = await apiClient.inventory({ q, status: 'available', limit: 20 });
    invResults.innerHTML = rows.length
      ? rows.map(renderInvResult).join('')
      : '<div class="lookup-empty">No available items match.</div>';
  } catch (e) {
    invResults.innerHTML = `<div class="lookup-empty" style="color:#b00;">${escape(e.message)}</div>`;
  }
}

function renderInvResult(it) {
  const avail = Number(it.available_qty ?? 0);
  return `
    <div class="item-row" data-lpn="${escape(it.lpn)}">
      <div class="body">
        <h4>${escape(it.title || it.lpn || '(no title)')}</h4>
        <div class="meta">${escape(it.brand || '')}${it.brand ? ' · ' : ''}${escape(it.lpn || '')} · <b style="color:#0a5;">${avail} available</b></div>
      </div>
      <div style="display:flex;align-items:center;gap:6px;">
        <input type="number" class="inv-add-qty" min="1" max="${avail}" value="1" inputmode="numeric" style="width:60px;padding:6px;border:1.5px solid #ccc;border-radius:4px;font-size:15px;">
        <button class="btn inv-add" style="padding:6px 12px;font-size:13px;">Add to pallet</button>
      </div>
    </div>`;
}

invResults?.addEventListener('click', async e => {
  const btn = e.target.closest('.inv-add');
  if (!btn) return;
  if (!activePallet) { toast('Pick a pallet at the top first', 'err', 3000); return; }
  const row = e.target.closest('.item-row');
  const lpn = row.dataset.lpn;
  const qty = parseInt(row.querySelector('.inv-add-qty').value, 10);
  if (!qty || qty < 1) { toast('Enter a quantity of 1 or more', 'err', 2500); return; }
  btn.disabled = true;
  try {
    const r = await apiClient.allocateToBox(lpn, qty, activePallet.manifest_id);
    toast(`Added ${r.allocated} to ${r.display_name} · ${r.remaining} left`, 'ok', 2600);
    await loadRecent();
    runInvSearch(invSearch.value.trim());   // refresh remaining availability
  } catch (err) {
    toast(err.data?.error || err.message, 'err', 4000);
    btn.disabled = false;
  }
});

function escape(s) { return String(s ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c])); }
