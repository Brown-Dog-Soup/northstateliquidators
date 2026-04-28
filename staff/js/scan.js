import { apiClient, toast, fmtMoney } from './api.js';

const $ = sel => document.querySelector(sel);
const codeEl  = $('#code');
const lookup  = $('#lookup');
const confirm = $('#confirm');
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

  // pick active pallet (most-recent OR localStorage choice)
  const storedId = localStorage.getItem('nsl.active.pallet');
  const list = await apiClient.pallets();
  activePallet = list.find(p => p.manifest_id === storedId) || list[0];
  palletEl.textContent = activePallet
    ? `${activePallet.display_name} · ${activePallet.item_count} items so far`
    : 'no pallets — open admin and create one';

  await loadRecent();
  codeEl.focus();
}

// ---- scan flow ---------------------------------------------------------
let lookupTimer;
codeEl.addEventListener('input', () => {
  clearTimeout(lookupTimer);
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
    doLookup().then(() => confirm.focus());
  }
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
      confirm.disabled = true;
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
    confirm.disabled = !codeEl.value.trim();
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

  // "Ref price" is reference data only — never a guaranteed sell price.
  // For UPCitemdb hits it's the lowest recorded marketplace price; for
  // lpn_catalog it's the manifest MSRP.
  lookup.innerHTML = `
    <div class="lookup-title">${escape(r.title || '')}</div>
    <div class="lookup-meta">
      <b>Brand:</b> ${escape(r.brand || '—')}  ·
      <b>Match:</b> ${r.match_source}  ·
      <b>LPN:</b> ${escape(r.lpn || '—')}  ·
      <b>UPC:</b> ${escape(r.upc || '—')}
    </div>
    <div class="lookup-price"><span style="font-family:'JetBrains Mono',monospace;font-size:11px;letter-spacing:0.1em;text-transform:uppercase;color:#555;display:block;font-weight:400;margin-bottom:2px;">Ref price</span>${fmtMoney(r.msrp)}</div>
    <span class="lookup-condition">${escape(r.condition || 'unknown')}</span>
    ${stockImg}
  `;

  // pre-pick condition if catalog provides one
  const cond = (r.condition || '').toLowerCase();
  const map = { 'used_good': 'open_box', 'new': 'new', 'customer_return': 'customer_return', 'salvage': 'damaged' };
  const sel = $('#condition');
  if (map[cond] && [...sel.options].some(o => o.value === map[cond])) sel.value = map[cond];

  suggestSellPrice();
  confirm.disabled = false;
}

// Compute and (unless the user has manually edited) fill in a suggested
// sell price = ref price × condition multiplier.
function suggestSellPrice() {
  const sp = $('#sell-price');
  const hint = $('#sell-price-hint');
  const ref = lookupResult?.msrp;
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

confirm.addEventListener('click', async () => {
  if (!codeEl.value.trim()) return;
  if (!activePallet) { toast('No active pallet — create one in Admin first.', 'err', 3000); return; }
  confirm.disabled = true;
  confirm.textContent = 'Saving…';

  try {
    // Decide which photo (if any) to attach.
    // Priority: receiver's own camera shot > stock image from lookup > none
    const file = $('#photo').files[0];
    const useStock = $('#use-stock-photo')?.checked && lookupResult?.image_url && !file;
    const stockUrl = useStock ? lookupResult.image_url : null;

    const sellRaw = $('#sell-price').value.trim();
    const sellPrice = sellRaw === '' ? null : Number(sellRaw);
    const record = {
      code:       codeEl.value.trim(),
      qty:        Number($('#qty').value) || 1,
      condition:  $('#condition').value,
      sellPrice:  Number.isFinite(sellPrice) ? sellPrice : null,
      manifestId: activePallet.manifest_id,
      photoUrl:   stockUrl   // sp_RecordScan stores this on line_items.photo_blob_url
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
    confirm.disabled = false;
  } finally {
    confirm.textContent = 'Confirm Scan';
  }
});

function resetForm() {
  codeEl.value = '';
  $('#qty').value = '1';
  $('#condition').value = 'untested';
  $('#photo').value = '';
  $('#sell-price').value = '';
  $('#sell-price-hint').textContent = '';
  sellPriceTouched = false;
  renderLookup(null);
  codeEl.focus();
}

async function loadRecent() {
  if (!activePallet) return;
  try {
    const detail = await apiClient.pallet(activePallet.manifest_id);
    recentItems = (detail.items || []).slice(0, 8);
    palletEl.textContent = `${detail.pallet.display_name} · ${detail.pallet.item_count} items`;
    recent.innerHTML = recentItems.map(it => `
      <div class="item-row" data-id="${it.id}">
        <div class="thumb"${it.photo_blob_url ? ` style="background-image:url('${escape(it.photo_blob_url)}')"` : ''}></div>
        <div class="body">
          <h4>${escape(it.title || it.lpn || it.upc || '(no title)')}</h4>
          <div class="meta">qty ${it.qty} · ${escape(it.condition || '—')} · ${escape(it.brand || '')} · ${escape((it.lpn || it.upc || '').slice(0, 16))}</div>
        </div>
        <div style="display:flex;flex-direction:column;align-items:flex-end;gap:4px;">
          <div class="price">${fmtMoney(it.est_resale || it.est_msrp)}</div>
          <button class="undo-scan" data-id="${it.id}" title="Remove this scan" style="background:none;border:1px solid #d4ada6;color:#b00;padding:2px 8px;font-size:12px;cursor:pointer;font-family:'JetBrains Mono',monospace;">✕ undo</button>
        </div>
      </div>
    `).join('') || '<div class="lookup-empty">No items yet on this pallet.</div>';

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

function escape(s) { return String(s ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c])); }
