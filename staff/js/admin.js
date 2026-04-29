import { apiClient, toast, fmtMoney, NSL_CATEGORIES } from './api.js';

const $ = sel => document.querySelector(sel);
const meEl = $('#me');
const galleryEl = $('#pallets');
const titleSuffix = $('#title-suffix');

let pallets = [];
let current = null;
let currentItems = [];
let showArchived = false;
let selectedItemIds = new Set();   // bulk-delete selection state for the open pallet

init();
async function init() {
  const me = await apiClient.me();
  meEl.innerHTML = me ? `${me.userDetails} · <a href="/logout">sign out</a>` : `<a href="/login">sign in</a>`;

  await loadList();

  // route by hash so a refresh keeps the user where they were
  window.addEventListener('hashchange', route);
  route();
}

function route() {
  const m = location.hash.match(/^#\/pallet\/([0-9a-f-]+)/i);
  if (m) showDetail(m[1]); else showList();
}

// ---- list view ---------------------------------------------------------
async function loadList() {
  pallets = await apiClient.pallets({ includeArchived: showArchived });
  galleryEl.innerHTML = pallets.map(p => {
    const archived = !!p.archived_at;
    return `
    <a class="gallery-card${archived ? ' archived' : ''}" href="#/pallet/${p.manifest_id}" style="${archived ? 'opacity:0.55;' : ''}">
      <div class="thumb"${p.photo_url ? ` style="background-image:url('${escape(p.photo_url)}')"` : ''}></div>
      <div class="body">
        <h3>${escape(p.display_name || `Pallet #${p.pallet_number}`)}${archived ? ' <span style="font-size:10px;color:#999;font-weight:400;letter-spacing:0.1em;text-transform:uppercase;">· archived</span>' : ''}</h3>
        <div class="stats">
          ${p.item_count || 0} items · ${p.unit_count || 0} units<br>
          MSRP: <b>${fmtMoney(p.total_msrp)}</b>${p.total_est_resale ? ` · resale: ${fmtMoney(p.total_est_resale)}` : ''}
          ${p.category ? `<br><span style="color:#0a5;">${escape(p.category)}</span>` : ''}
        </div>
        <span class="pill ${p.sell_mode || 'undecided'}">${p.sell_mode || 'undecided'}</span>
      </div>
    </a>
  `;}).join('') || '<p style="color:#666;">No pallets yet — create one above.</p>';
}

function showList() {
  $('#view-list').hidden = false;
  $('#view-detail').hidden = true;
  titleSuffix.textContent = 'Pallets';
  loadList();
}

$('#new-pallet').addEventListener('click', async () => {
  try {
    const r = await apiClient.createPallet({ displayName: $('#new-name').value.trim() || null });
    $('#new-name').value = '';
    toast(`Created: ${r.display_name}`, 'ok');
    await loadList();
    location.hash = `#/pallet/${r.id}`;
  } catch (e) { toast(`Create failed: ${e.message}`, 'err', 4000); }
});

// Populate category dropdown once at module load. Includes a blank "—" entry
// so an unset pallet doesn't get accidentally tagged on first save.
(function populateCategoryDropdown() {
  const sel = $('#cat'); if (!sel) return;
  sel.innerHTML = `<option value="">—</option>` +
    NSL_CATEGORIES.map(c => `<option value="${c}">${c}</option>`).join('');
})();

// ---- detail view -------------------------------------------------------
async function showDetail(id) {
  $('#view-list').hidden = true;
  $('#view-detail').hidden = false;
  selectedItemIds.clear();
  updateBulkDeleteUI();

  try {
    const detail = await apiClient.pallet(id);
    current = detail.pallet;
    currentItems = detail.items || [];
  } catch (e) { toast(`Load failed: ${e.message}`, 'err', 4000); return; }

  titleSuffix.textContent = current.display_name;
  $('#dn').value = current.display_name || '';
  $('#notes').value = current.notes || '';
  $('#cat').value = current.category || '';
  $('#cur-mode').textContent = (current.sell_mode || 'undecided').toUpperCase();
  $('#items-count').textContent = `(${currentItems.length} item${currentItems.length === 1 ? '' : 's'})`;

  // Archive button label flips between Archive / Restore
  const archBtn = $('#archive-pallet');
  if (archBtn) archBtn.textContent = current.archived_at ? 'Restore pallet' : 'Archive pallet';

  const photo = current.photo_url;
  $('#pallet-photo').style.backgroundImage = photo ? `url('${photo}')` : '';

  $('#stats').textContent =
    `pallet #     ${current.pallet_number}\n` +
    `received     ${current.received_date ? new Date(current.received_date).toLocaleString() : '—'}\n` +
    `status       ${current.status}\n` +
    `sell mode    ${current.sell_mode}\n` +
    `items        ${current.item_count || 0}\n` +
    `units        ${current.unit_count || 0}\n` +
    `MSRP total   ${fmtMoney(current.total_msrp)}\n` +
    `est. resale  ${current.total_est_resale ? fmtMoney(current.total_est_resale) : '— (pending enrichment)'}\n` +
    `cost         ${fmtMoney(current.total_cost)}`;

  // mark active sell-mode button
  document.querySelectorAll('.mode-toggle button').forEach(b =>
    b.classList.toggle('active', b.dataset.mode === current.sell_mode)
  );

  // items list — each row is collapsible; click "Edit" to expand inline editor.
  // Per-row checkbox feeds bulk-delete state; clicking the row body opens edit.
  $('#items').innerHTML = currentItems.map(it => `
    <div class="item-row" data-id="${it.id}">
      <input type="checkbox" class="item-select" data-id="${it.id}" style="width:18px;height:18px;cursor:pointer;flex-shrink:0;align-self:center;margin-right:4px;">
      <div class="thumb"${it.photo_blob_url ? ` style="background-image:url('${escape(it.photo_blob_url)}')"` : ''}></div>
      <div class="body">
        <h4>${escape(it.title || it.lpn || it.upc || '(no title)')}</h4>
        <div class="meta">qty ${it.qty} · ${escape(it.condition || '—')} · ${escape(it.brand || '')} · ${escape(it.lpn || it.upc || '')}</div>
        <div class="meta" style="margin-top:4px;">
          MSRP ${fmtMoney(it.est_msrp)}${it.est_resale ? ` · sell ${fmtMoney(it.est_resale)}` : ''}
        </div>
      </div>
      <div style="display:flex;flex-direction:column;gap:4px;align-items:flex-end;">
        <div class="price">${fmtMoney(it.est_resale || it.est_msrp)}</div>
        <div style="display:flex;gap:6px;">
          <button class="btn btn-ghost edit-item" data-id="${it.id}" style="padding:4px 10px;font-size:11px;">Edit</button>
          <button class="btn btn-danger del-item" data-id="${it.id}" style="padding:4px 10px;font-size:11px;background:var(--nc-red,#CC0000);color:#fff;border:none;">Delete</button>
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
      <div class="field" style="margin-top:8px;"><label>Notes</label><textarea data-f="notes" rows="2">${escape(it.notes || '')}</textarea></div>
      <div style="display:flex;gap:8px;margin-top:8px;">
        <button class="btn btn-primary save-item" data-id="${it.id}">Save changes</button>
        <button class="btn btn-ghost cancel-item" data-id="${it.id}">Cancel</button>
      </div>
    </div>
  `).join('') || '<div class="lookup-empty">No items scanned to this pallet yet.</div>';

  // Wire item-row buttons
  document.querySelectorAll('.edit-item').forEach(b => b.addEventListener('click', e => {
    const id = e.currentTarget.dataset.id;
    const panel = document.querySelector(`.item-edit[data-edit="${id}"]`);
    if (!panel) return;
    const willOpen = panel.hidden;            // true if currently hidden → opening
    document.querySelectorAll('.item-edit').forEach(el => { el.hidden = true; });
    panel.hidden = !willOpen;                 // open if it was closed; otherwise stay closed
  }));
  document.querySelectorAll('.cancel-item').forEach(b => b.addEventListener('click', e => {
    const id = e.currentTarget.dataset.id;
    const panel = document.querySelector(`.item-edit[data-edit="${id}"]`);
    if (panel) panel.hidden = true;
  }));
  document.querySelectorAll('.save-item').forEach(b => b.addEventListener('click', async e => {
    const id = e.currentTarget.dataset.id;
    const panel = document.querySelector(`.item-edit[data-edit="${id}"]`);
    const fields = {};
    panel.querySelectorAll('[data-f]').forEach(el => {
      const k = el.dataset.f;
      const v = el.value.trim();
      if (k === 'qty')         fields[k] = v === '' ? null : Number(v);
      else if (k === 'sellPrice') fields[k] = v === '' ? null : Number(v);
      else                       fields[k] = v;
    });
    try {
      await apiClient.patchItem(id, fields);
      toast('Saved', 'ok');
      await showDetail(current.manifest_id);
    } catch (err) { toast(`Save failed: ${err.message}`, 'err', 4000); }
  }));
  document.querySelectorAll('.del-item').forEach(b => b.addEventListener('click', async e => {
    const id = e.currentTarget.dataset.id;
    const item = currentItems.find(i => i.id === id);
    const label = item?.title || item?.lpn || item?.upc || 'this item';
    if (!confirm(`Delete "${label}"? This cannot be undone.`)) return;
    try {
      await apiClient.deleteItem(id);
      toast('Deleted', 'ok');
      await showDetail(current.manifest_id);
    } catch (err) { toast(`Delete failed: ${err.message}`, 'err', 4000); }
  }));

  // Per-row checkboxes feed bulk-delete state
  document.querySelectorAll('.item-select').forEach(cb => cb.addEventListener('change', e => {
    const id = e.currentTarget.dataset.id;
    if (e.currentTarget.checked) selectedItemIds.add(id);
    else                          selectedItemIds.delete(id);
    updateBulkDeleteUI();
  }));
}

function updateBulkDeleteUI() {
  const btn = $('#bulk-delete');
  const cnt = $('#bulk-count');
  if (!btn || !cnt) return;
  const n = selectedItemIds.size;
  cnt.textContent = String(n);
  btn.disabled = n === 0;
  btn.style.opacity = n === 0 ? 0.5 : 1;
  // Sync the "Select all" checkbox tri-state
  const selAll = $('#select-all-items');
  if (selAll) {
    if (n === 0)                                 { selAll.checked = false; selAll.indeterminate = false; }
    else if (n === currentItems.length && n > 0) { selAll.checked = true;  selAll.indeterminate = false; }
    else                                         { selAll.checked = false; selAll.indeterminate = true; }
  }
}

$('#back').addEventListener('click', () => { location.hash = ''; });

document.querySelectorAll('.mode-toggle button').forEach(b => {
  b.addEventListener('click', async () => {
    if (!current) return;
    try {
      await apiClient.patchPallet(current.manifest_id, { sellMode: b.dataset.mode });
      toast(`Sell mode: ${b.dataset.mode}`, 'ok');
      await showDetail(current.manifest_id);
    } catch (e) { toast(`Update failed: ${e.message}`, 'err', 4000); }
  });
});

$('#save-meta').addEventListener('click', async () => {
  if (!current) return;
  try {
    await apiClient.patchPallet(current.manifest_id, {
      displayName: $('#dn').value.trim(),
      notes:       $('#notes').value,
      category:    $('#cat').value   // empty string → server stores '' (treat as unset visually)
    });
    toast('Saved', 'ok');
    await showDetail(current.manifest_id);
  } catch (e) { toast(`Save failed: ${e.message}`, 'err', 4000); }
});

// Show-archived toggle on the list view
$('#show-archived')?.addEventListener('change', e => {
  showArchived = e.currentTarget.checked;
  loadList();
});

// Archive / Restore button on the detail view
$('#archive-pallet')?.addEventListener('click', async () => {
  if (!current) return;
  const archiving = !current.archived_at;
  const verb = archiving ? 'Archive' : 'Restore';
  if (!confirm(`${verb} "${current.display_name}"?`)) return;
  try {
    await apiClient.archivePallet(current.manifest_id, archiving);
    toast(`${archiving ? 'Archived' : 'Restored'}`, 'ok');
    if (archiving) location.hash = '';   // bounce back to list when archiving
    else           await showDetail(current.manifest_id);
  } catch (e) { toast(`${verb} failed: ${e.message}`, 'err', 4000); }
});

// Hard delete pallet (rare; archive is the safer default).
$('#delete-pallet')?.addEventListener('click', async () => {
  if (!current) return;
  const n = currentItems.length;
  const msg = `PERMANENTLY DELETE "${current.display_name}" and all ${n} item${n === 1 ? '' : 's'} on it?\n\nThis cannot be undone. Type the pallet name to confirm.`;
  const typed = prompt(msg);
  if (typed == null) return;
  if (typed !== current.display_name) { toast('Name did not match — delete cancelled.', 'err', 3000); return; }
  try {
    await apiClient.deletePallet(current.manifest_id);
    toast('Pallet deleted', 'ok');
    location.hash = '';
  } catch (e) { toast(`Delete failed: ${e.message}`, 'err', 4000); }
});

// Bulk delete items
$('#select-all-items')?.addEventListener('change', e => {
  const on = e.currentTarget.checked;
  selectedItemIds.clear();
  if (on) currentItems.forEach(it => selectedItemIds.add(it.id));
  document.querySelectorAll('.item-select').forEach(cb => { cb.checked = on; });
  updateBulkDeleteUI();
});

$('#bulk-delete')?.addEventListener('click', async () => {
  const ids = [...selectedItemIds];
  if (ids.length === 0) return;
  if (!confirm(`Delete ${ids.length} selected item${ids.length === 1 ? '' : 's'}? This cannot be undone.`)) return;
  try {
    const r = await apiClient.bulkDeleteItems(ids);
    toast(`Deleted ${r.deleted} item${r.deleted === 1 ? '' : 's'}`, 'ok');
    selectedItemIds.clear();
    if (current) await showDetail(current.manifest_id);
  } catch (e) { toast(`Bulk delete failed: ${e.message}`, 'err', 4000); }
});

$('#photo-input').addEventListener('change', () => {
  $('#photo-upload').disabled = !$('#photo-input').files[0];
});

$('#photo-upload').addEventListener('click', async () => {
  const file = $('#photo-input').files[0];
  if (!file || !current) return;
  $('#photo-upload').disabled = true;
  $('#photo-upload').textContent = 'Uploading…';
  try {
    await apiClient.uploadPhoto('pallet', current.manifest_id, file);
    toast('Pallet photo uploaded', 'ok');
    $('#photo-input').value = '';
    await showDetail(current.manifest_id);
  } catch (e) { toast(`Upload failed: ${e.message}`, 'err', 4000); }
  finally { $('#photo-upload').textContent = 'Upload pallet photo'; }
});

$('#set-active').addEventListener('click', () => {
  if (!current) return;
  localStorage.setItem('nsl.active.pallet', current.manifest_id);
  toast(`Active pallet: ${current.display_name}`, 'ok');
});

function escape(s) { return String(s ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c])); }
