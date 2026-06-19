import { apiClient, toast, fmtMoney } from './api.js';

const $ = sel => document.querySelector(sel);
const meEl = $('#me');

let debounceTimer = null;
let lastRows = [];                  // rows currently shown (for select-all)
let boxes = [];                     // working boxes available as allocation targets
const selectedLpns = new Set();     // #9 build-a-box selection (keyed by lpn)

init();
async function init() {
  const me = await apiClient.me();
  meEl.innerHTML = me ? `${me.userDetails} · <a href="/logout">sign out</a>` : `<a href="/login">sign in</a>`;

  await loadBoxes();
  await loadSummary();
  await loadResults();

  $('#search').addEventListener('input', () => {
    clearTimeout(debounceTimer);
    debounceTimer = setTimeout(loadResults, 250);
  });
  $('#filter-status').addEventListener('change', loadResults);
  $('#filter-lot').addEventListener('change', loadResults);
  $('#reset-filters').addEventListener('click', () => {
    $('#search').value = '';
    $('#filter-status').value = '';
    $('#filter-lot').value = '';
    loadResults();
  });

  // Per-row allocate / delete actions (event delegation — rows re-render often).
  $('#results').addEventListener('click', onResultsClick);
  $('#results').addEventListener('change', onResultsChange);
}

// Draft/live working boxes the user can drop items into. Ghost/sold/archived
// pallets are not valid targets, so filter them out.
async function loadBoxes() {
  try {
    const all = await apiClient.pallets();
    boxes = (all || []).filter(p => p.publish_state === 'draft' || p.publish_state === 'live');
  } catch { boxes = []; }
}

function boxOptions() {
  return '<option value="">— pick a box —</option>'
    + boxes.map(b => `<option value="${b.manifest_id}">${escape(b.display_name || ('Box #' + b.pallet_number))}</option>`).join('')
    + '<option value="__new__">＋ New box…</option>';
}

async function loadSummary() {
  try {
    const s = await apiClient.inventorySummary();

    // Top cards: Available, In a box, Ghost, Sold/Done, totals.
    const cardsHost = $('#summary-cards');
    const byStatus = Object.fromEntries((s.byStatus || []).map(r => [r.status, r.n]));
    const cards = [
      { label: 'Available',  value: byStatus.available || 0, color: '#0a5' },
      { label: 'In a box', value: (byStatus.on_pallet || 0) + (byStatus.individual || 0) + (byStatus.allocated || 0), color: '#002868' },
      { label: 'Ghost',      value: byStatus.ghost || 0, color: '#888' },
      { label: 'Sold / Done', value: (byStatus.sold || 0) + (byStatus.archived || 0), color: '#b00' }
    ];
    cardsHost.innerHTML = cards.map(c => `
      <div class="col card" style="min-width:160px;flex:1;">
        <div style="font-family:'JetBrains Mono',monospace;font-size:10px;letter-spacing:0.1em;text-transform:uppercase;color:#888;">${c.label}</div>
        <div style="font-family:Anton;font-size:36px;color:${c.color};line-height:1;margin:6px 0 0;">${c.value.toLocaleString()}</div>
      </div>
    `).join('') + `
      <div class="col card" style="min-width:200px;flex:1.4;">
        <div style="font-family:'JetBrains Mono',monospace;font-size:10px;letter-spacing:0.1em;text-transform:uppercase;color:#888;">Catalog totals</div>
        <div style="font-family:'JetBrains Mono',monospace;font-size:13px;margin-top:4px;line-height:1.6;">
          MSRP <b>${fmtMoney(s.totals?.total_msrp)}</b><br>
          COST <b>${fmtMoney(s.totals?.total_cost)}</b><br>
          PRICE <b style="color:#0a5;">${fmtMoney(s.totals?.total_wholesale)}</b>
        </div>
      </div>
    `;

    // Lot dropdown
    const lotSel = $('#filter-lot');
    lotSel.innerHTML = '<option value="">All lots</option>' +
      (s.lots || []).map(l => `<option value="${escape(l.lot)}">${l.lot_type ? escape(l.lot_type) + ': ' : ''}${escape(l.lot)} (${l.n})</option>`).join('');
  } catch (e) {
    toast(`Summary load failed: ${e.message}`, 'err', 4000);
  }
}

async function loadResults() {
  const opts = {
    status: $('#filter-status').value || null,
    lot:    $('#filter-lot').value || null,
    q:      $('#search').value.trim() || null,
    limit:  500
  };
  $('#results-meta').textContent = 'Loading…';
  try {
    const rows = await apiClient.inventory(opts);
    lastRows = rows;
    $('#results-meta').textContent = `${rows.length} item${rows.length === 1 ? '' : 's'}${rows.length === 500 ? ' (showing first 500 — refine filters to see more)' : ''}`;
    $('#results').innerHTML = rows.map(renderRow).join('') || '<div class="lookup-empty">No items match.</div>';
    wireRowChecks();
  } catch (e) {
    $('#results-meta').textContent = '';
    toast(`Inventory load failed: ${e.message}`, 'err', 4000);
  }
}

function renderRow(it) {
  const statusBadge = renderBadge(it.status, it.assigned_pallet_is_ghost);
  const palletLink = it.assigned_pallet_id
    ? `<a href="admin.html#/pallet/${it.assigned_pallet_id}" style="color:#002868;text-decoration:underline;">${escape(it.assigned_pallet_name || `Pallet #${it.assigned_pallet_number}`)}</a>`
    : '';
  const checked = selectedLpns.has(it.lpn) ? ' checked' : '';
  const avail = Number(it.available_qty ?? 0);
  const allocated = Number(it.allocated_qty ?? 0);
  return `
    <div class="item-row" data-lpn="${escape(it.lpn)}" data-title="${escape(it.title || it.lpn || '')}">
      <input type="checkbox" class="inv-select" data-lpn="${escape(it.lpn)}"${checked} style="width:20px;height:20px;cursor:pointer;flex-shrink:0;align-self:center;">
      <div class="body">
        <h4>${escape(it.title || it.lpn || it.upc || '(no title)')}</h4>
        <div class="meta">
          <b>qty ${it.qty_in_manifest ?? 1}</b> · ${escape(it.brand || '')}${it.brand ? ' · ' : ''}${escape(it.lpn || '')}${it.upc ? ' · UPC ' + escape(it.upc) : ''}${(it.order_number || it.source_pallet_id || it.lot_id) ? ' · Lot ' + escape(it.order_number || it.source_pallet_id || it.lot_id) : ''}
        </div>
        <div class="meta" style="margin-top:4px;">
          ${statusBadge}
          ${allocated > 0 ? `· <button class="alloc-toggle" style="background:none;border:none;color:#002868;text-decoration:underline;cursor:pointer;font:inherit;padding:0;"><b>${allocated}</b> in boxes ▾</button>` : ''}
          ${palletLink ? '· on ' + palletLink : ''}
          ${it.scanned_at ? ' · scanned ' + new Date(it.scanned_at).toLocaleDateString() : ''}
        </div>
        <div class="alloc-detail" hidden style="margin-top:6px;"></div>
        ${renderAllocStrip(it, avail, allocated)}
      </div>
      <div style="display:flex;flex-direction:column;align-items:flex-end;gap:2px;font-family:'JetBrains Mono',monospace;font-size:12px;min-width:120px;">
        <div><span style="color:#888;letter-spacing:0.1em;text-transform:uppercase;font-size:10px;">MSRP </span>${fmtMoney(it.msrp)}</div>
        <div><span style="color:#888;letter-spacing:0.1em;text-transform:uppercase;font-size:10px;">COST </span>${fmtMoney(it.unit_cost)}</div>
        <div><span style="color:#0a5;letter-spacing:0.1em;text-transform:uppercase;font-size:10px;font-weight:700;">PRICE </span><b>${fmtMoney(it.wholesale_price)}</b></div>
      </div>
    </div>`;
}

// The split-quantity control. Only shown when there are units left to place.
// A trash button appears only when nothing has been allocated yet (so deletion
// can't strand units already in a box — the server enforces this too).
function renderAllocStrip(it, avail, allocated) {
  if (avail <= 0) return '';
  const del = allocated === 0
    ? `<button class="alloc-del" title="Delete this item" style="margin-left:auto;background:none;border:none;color:#b00;cursor:pointer;font-size:16px;line-height:1;">🗑</button>`
    : '';
  return `
    <div class="alloc-strip" style="margin-top:8px;display:flex;align-items:center;gap:6px;flex-wrap:wrap;">
      <span style="font-size:11px;color:#0a5;font-weight:700;text-transform:uppercase;letter-spacing:0.08em;">${avail} available</span>
      <input type="number" class="alloc-qty" min="1" max="${avail}" value="1" inputmode="numeric"
        style="width:64px;padding:6px 8px;border:1.5px solid #ccc;border-radius:4px;font-size:15px;">
      <span style="font-size:12px;color:#888;">into</span>
      <select class="alloc-box" style="padding:6px 8px;border:1.5px solid #ccc;border-radius:4px;font-size:13px;max-width:220px;">
        ${boxOptions()}
      </select>
      <input type="text" class="alloc-newname" placeholder="New box name (optional)" hidden
        style="padding:6px 8px;border:1.5px solid #ccc;border-radius:4px;font-size:13px;width:170px;">
      <button class="btn alloc-add" style="padding:6px 14px;font-size:13px;">Add to box →</button>
      ${del}
    </div>`;
}

// Per-box allocation breakdown for the "N in boxes ▾" expander. Sold boxes are
// flagged so the team knows those units are gone, not pullable.
function renderAllocDetail(rows) {
  return `<div style="border-left:2px solid #dde6f5;padding:4px 0 4px 10px;">
    ${rows.map(r => {
      const name = escape(r.display_name || ('Box #' + r.pallet_number));
      const sold = r.is_sold
        ? ` <span style="background:#fce4e4;color:#b00;padding:0 5px;border-radius:2px;font-size:10px;font-weight:700;letter-spacing:0.05em;text-transform:uppercase;">SOLD — gone</span>`
        : '';
      return `<div style="padding:2px 0;font-size:12px;"><a href="admin.html#/pallet/${r.manifest_id}" style="color:#002868;text-decoration:underline;">${name}</a> · <b>${r.qty}</b> unit${r.qty === 1 ? '' : 's'}${sold}</div>`;
    }).join('')}
  </div>`;
}

function renderBadge(status, isGhost) {
  const map = {
    available:  { txt: 'AVAILABLE',  bg: '#dff5e8', color: '#0a5' },
    allocated:  { txt: 'IN A BOX',   bg: '#dde6f5', color: '#002868' },
    on_pallet:  { txt: 'ON PALLET',  bg: '#dde6f5', color: '#002868' },
    individual: { txt: 'INDIVIDUAL', bg: '#fff4d4', color: '#a06400' },
    ghost:      { txt: 'GHOST',      bg: '#eee',    color: '#666' },
    archived:   { txt: 'ARCHIVED',   bg: '#eee',    color: '#888' },
    sold:       { txt: 'SOLD',       bg: '#fce4e4', color: '#b00' },
    unknown:    { txt: 'UNKNOWN',    bg: '#f4f4f4', color: '#666' }
  };
  const m = map[status] || map.unknown;
  return `<span style="background:${m.bg};color:${m.color};padding:1px 6px;border-radius:2px;font-family:'JetBrains Mono',monospace;font-size:10px;letter-spacing:0.1em;text-transform:uppercase;font-weight:700;">${m.txt}</span>`;
}

// ---- per-row allocate / delete -----------------------------------------
function onResultsChange(e) {
  const sel = e.target.closest('.alloc-box');
  if (!sel) return;
  const strip = sel.closest('.alloc-strip');
  const newName = strip?.querySelector('.alloc-newname');
  if (newName) newName.hidden = sel.value !== '__new__';
}

async function onResultsClick(e) {
  const toggle = e.target.closest('.alloc-toggle');
  const addBtn = e.target.closest('.alloc-add');
  const delBtn = e.target.closest('.alloc-del');
  if (!toggle && !addBtn && !delBtn) return;

  const row = e.target.closest('.item-row');
  const lpn = row?.dataset.lpn;
  const title = row?.dataset.title || lpn;
  if (!lpn) return;

  // Expand/collapse the per-box breakdown ("N in boxes ▾").
  if (toggle) {
    const panel = row.querySelector('.alloc-detail');
    if (!panel) return;
    if (!panel.hidden) { panel.hidden = true; return; }
    panel.hidden = false;
    panel.innerHTML = '<span style="font-size:12px;color:#888;">Loading…</span>';
    try {
      const rows = await apiClient.inventoryAllocations(lpn);
      panel.innerHTML = rows.length ? renderAllocDetail(rows)
        : '<span style="font-size:12px;color:#888;">No box allocations.</span>';
    } catch (err) { panel.innerHTML = `<span style="font-size:12px;color:#b00;">${escape(err.message)}</span>`; }
    return;
  }

  if (delBtn) {
    if (!confirm(`Delete "${title}" from inventory? This can't be undone.`)) return;
    delBtn.disabled = true;
    try {
      await apiClient.deleteInventoryItem(lpn);
      toast('Item deleted', 'ok', 1800);
      await refresh();
    } catch (err) {
      const msg = err.status === 409 ? (err.data?.error || 'Item is already in a box') : err.message;
      toast(msg, 'err', 4000);
      delBtn.disabled = false;
    }
    return;
  }

  // Add-to-box
  const strip = addBtn.closest('.alloc-strip');
  const qty = parseInt(strip.querySelector('.alloc-qty').value, 10);
  const boxSel = strip.querySelector('.alloc-box');
  const newName = strip.querySelector('.alloc-newname')?.value.trim() || null;
  const boxVal = boxSel.value;

  if (!qty || qty < 1) { toast('Enter a quantity of 1 or more', 'err', 2500); return; }
  if (!boxVal) { toast('Pick a box (or choose “New box”)', 'err', 2500); return; }

  const manifestId = boxVal === '__new__' ? null : boxVal;
  const newBoxName = boxVal === '__new__' ? newName : null;

  addBtn.disabled = true;
  try {
    const r = await apiClient.allocateToBox(lpn, qty, manifestId, newBoxName);
    toast(`Added ${r.allocated} to ${r.display_name} · ${r.remaining} left`, 'ok', 2600);
    await refresh();
  } catch (err) {
    toast(err.data?.error || err.message, 'err', 4000);
    addBtn.disabled = false;
  }
}

// Reload boxes (an add may have created one), summary cards, and the list.
async function refresh() {
  await loadBoxes();
  await loadSummary();
  await loadResults();
}

// ---- #9 build a box from checked items ---------------------------------
function wireRowChecks() {
  document.querySelectorAll('.inv-select').forEach(cb => cb.addEventListener('change', e => {
    const lpn = e.currentTarget.dataset.lpn;
    if (e.currentTarget.checked) selectedLpns.add(lpn);
    else                         selectedLpns.delete(lpn);
    updateBuildBar();
  }));
  // reflect any prior selection state on the freshly-rendered "select all" box
  const selAll = $('#select-all');
  if (selAll) { selAll.checked = false; selAll.indeterminate = false; }
}

function updateBuildBar() {
  const bar = $('#build-bar');
  const cnt = $('#build-count');
  if (!bar || !cnt) return;
  cnt.textContent = String(selectedLpns.size);
  bar.hidden = selectedLpns.size === 0;
}

$('#select-all')?.addEventListener('change', e => {
  const on = e.currentTarget.checked;
  lastRows.forEach(r => { if (on) selectedLpns.add(r.lpn); else selectedLpns.delete(r.lpn); });
  document.querySelectorAll('.inv-select').forEach(cb => { cb.checked = on; });
  updateBuildBar();
});

$('#build-clear')?.addEventListener('click', () => {
  selectedLpns.clear();
  document.querySelectorAll('.inv-select').forEach(cb => { cb.checked = false; });
  const selAll = $('#select-all'); if (selAll) selAll.checked = false;
  updateBuildBar();
});

$('#build-pallet')?.addEventListener('click', async () => {
  const lpns = [...selectedLpns];
  if (lpns.length === 0) return;
  const name = $('#build-name').value.trim() || null;
  const btn = $('#build-pallet');
  btn.disabled = true;
  try {
    const r = await apiClient.createPalletFromItems(lpns, name);
    toast(`Created ${r.display_name} (${r.items_added} items)`, 'ok', 2500);
    selectedLpns.clear();
    // jump straight to the new box in the admin app
    location.href = `admin.html#/pallet/${r.id}`;
  } catch (e) { toast(`Create failed: ${e.message}`, 'err', 4000); btn.disabled = false; }
});

$('#build-delete')?.addEventListener('click', async () => {
  const lpns = [...selectedLpns];
  if (lpns.length === 0) return;
  if (!confirm(`Delete ${lpns.length} selected item${lpns.length === 1 ? '' : 's'} from inventory? Items already in a box are skipped. This can't be undone.`)) return;
  const btn = $('#build-delete');
  btn.disabled = true;
  try {
    const r = await apiClient.bulkDeleteInventory(lpns);
    const msg = r.skipped > 0
      ? `Deleted ${r.deleted}; skipped ${r.skipped} already in a box`
      : `Deleted ${r.deleted} item${r.deleted === 1 ? '' : 's'}`;
    toast(msg, 'ok', 3000);
    selectedLpns.clear();
    const selAll = $('#select-all'); if (selAll) selAll.checked = false;
    updateBuildBar();
    await refresh();
  } catch (e) { toast(`Delete failed: ${e.message}`, 'err', 4000); }
  finally { btn.disabled = false; }
});

function escape(s) { return String(s ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c])); }
