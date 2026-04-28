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

  lookup.innerHTML = `
    <div class="lookup-title">${escape(r.title || '')}</div>
    <div class="lookup-meta">
      <b>Brand:</b> ${escape(r.brand || '—')}  ·
      <b>Match:</b> ${r.match_source}  ·
      <b>LPN:</b> ${escape(r.lpn || '—')}  ·
      <b>UPC:</b> ${escape(r.upc || '—')}
    </div>
    <div class="lookup-price">${fmtMoney(r.msrp)}</div>
    <span class="lookup-condition">${escape(r.condition || 'unknown')}</span>
    ${stockImg}
  `;

  // pre-pick condition if catalog provides one
  const cond = (r.condition || '').toLowerCase();
  const map = { 'used_good': 'open_box', 'new': 'new', 'customer_return': 'customer_return', 'salvage': 'damaged' };
  const sel = $('#condition');
  if (map[cond] && [...sel.options].some(o => o.value === map[cond])) sel.value = map[cond];
  confirm.disabled = false;
}

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

    const record = {
      code:       codeEl.value.trim(),
      qty:        Number($('#qty').value) || 1,
      condition:  $('#condition').value,
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
      <div class="item-row">
        <div class="thumb"${it.photo_blob_url ? ` style="background-image:url('${escape(it.photo_blob_url)}')"` : ''}></div>
        <div class="body">
          <h4>${escape(it.title || it.lpn || it.upc || '(no title)')}</h4>
          <div class="meta">qty ${it.qty} · ${escape(it.condition || '—')} · ${escape(it.brand || '')} · ${escape((it.lpn || it.upc || '').slice(0, 16))}</div>
        </div>
        <div class="price">${fmtMoney(it.est_msrp)}</div>
      </div>
    `).join('') || '<div class="lookup-empty">No items yet on this pallet.</div>';
  } catch (e) { /* ignore */ }
}

function escape(s) { return String(s ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c])); }
