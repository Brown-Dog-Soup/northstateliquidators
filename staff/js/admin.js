import { apiClient, toast, fmtMoney } from './api.js';

const $ = sel => document.querySelector(sel);
const meEl = $('#me');
const galleryEl = $('#pallets');
const titleSuffix = $('#title-suffix');

let pallets = [];
let current = null;
let currentItems = [];

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
  pallets = await apiClient.pallets();
  galleryEl.innerHTML = pallets.map(p => `
    <a class="gallery-card" href="#/pallet/${p.manifest_id}">
      <div class="thumb"${p.photo_url ? ` style="background-image:url('${escape(p.photo_url)}')"` : ''}></div>
      <div class="body">
        <h3>${escape(p.display_name || `Pallet #${p.pallet_number}`)}</h3>
        <div class="stats">
          ${p.item_count || 0} items · ${p.unit_count || 0} units<br>
          MSRP: <b>${fmtMoney(p.total_msrp)}</b>${p.total_est_resale ? ` · resale: ${fmtMoney(p.total_est_resale)}` : ''}
        </div>
        <span class="pill ${p.sell_mode || 'undecided'}">${p.sell_mode || 'undecided'}</span>
      </div>
    </a>
  `).join('') || '<p style="color:#666;">No pallets yet — create one above.</p>';
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

// ---- detail view -------------------------------------------------------
async function showDetail(id) {
  $('#view-list').hidden = true;
  $('#view-detail').hidden = false;

  try {
    const detail = await apiClient.pallet(id);
    current = detail.pallet;
    currentItems = detail.items || [];
  } catch (e) { toast(`Load failed: ${e.message}`, 'err', 4000); return; }

  titleSuffix.textContent = current.display_name;
  $('#dn').value = current.display_name || '';
  $('#notes').value = current.notes || '';
  $('#cur-mode').textContent = (current.sell_mode || 'undecided').toUpperCase();
  $('#items-count').textContent = `(${currentItems.length} item${currentItems.length === 1 ? '' : 's'})`;

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

  // items list
  $('#items').innerHTML = currentItems.map(it => `
    <div class="item-row" data-id="${it.id}">
      <div class="thumb"${it.photo_blob_url ? ` style="background-image:url('${escape(it.photo_blob_url)}')"` : ''}></div>
      <div class="body">
        <h4>${escape(it.title || it.lpn || it.upc || '(no title)')}</h4>
        <div class="meta">qty ${it.qty} · ${escape(it.condition || '—')} · ${escape(it.brand || '')} · ${escape(it.lpn || it.upc || '')}</div>
      </div>
      <div class="price">${fmtMoney(it.est_msrp)}</div>
    </div>
  `).join('') || '<div class="lookup-empty">No items scanned to this pallet yet.</div>';
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
      notes: $('#notes').value
    });
    toast('Saved', 'ok');
    await showDetail(current.manifest_id);
  } catch (e) { toast(`Save failed: ${e.message}`, 'err', 4000); }
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
