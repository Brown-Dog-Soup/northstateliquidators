import { apiClient, toast, fmtMoney } from './api.js';

const $ = sel => document.querySelector(sel);
const meEl = $('#me');

let debounceTimer = null;

init();
async function init() {
  const me = await apiClient.me();
  meEl.innerHTML = me ? `${me.userDetails} · <a href="/logout">sign out</a>` : `<a href="/login">sign in</a>`;

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
}

async function loadSummary() {
  try {
    const s = await apiClient.inventorySummary();

    // Top cards: Available, On a pallet, Sold/Other, totals.
    const cardsHost = $('#summary-cards');
    const byStatus = Object.fromEntries((s.byStatus || []).map(r => [r.status, r.n]));
    const cards = [
      { label: 'Available',  value: byStatus.available || 0, color: '#0a5' },
      { label: 'On a pallet', value: (byStatus.on_pallet || 0) + (byStatus.individual || 0), color: '#002868' },
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
      (s.lots || []).map(l => `<option value="${escape(l.lot)}">${escape(l.lot)} (${l.n})</option>`).join('');
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
    $('#results-meta').textContent = `${rows.length} item${rows.length === 1 ? '' : 's'}${rows.length === 500 ? ' (showing first 500 — refine filters to see more)' : ''}`;
    $('#results').innerHTML = rows.map(renderRow).join('') || '<div class="lookup-empty">No items match.</div>';
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
  return `
    <div class="item-row">
      <div class="body">
        <h4>${escape(it.title || it.lpn || it.upc || '(no title)')}</h4>
        <div class="meta">
          ${escape(it.brand || '')}${it.brand ? ' · ' : ''}${escape(it.lpn || '')}${it.upc ? ' · UPC ' + escape(it.upc) : ''}${it.source_pallet_ref ? ' · Lot ' + escape(it.source_pallet_ref) : ''}
        </div>
        <div class="meta" style="margin-top:4px;">
          ${statusBadge}
          ${palletLink ? '· on ' + palletLink : ''}
          ${it.scanned_at ? ' · scanned ' + new Date(it.scanned_at).toLocaleDateString() : ''}
        </div>
      </div>
      <div style="display:flex;flex-direction:column;align-items:flex-end;gap:2px;font-family:'JetBrains Mono',monospace;font-size:12px;min-width:120px;">
        <div><span style="color:#888;letter-spacing:0.1em;text-transform:uppercase;font-size:10px;">MSRP </span>${fmtMoney(it.msrp)}</div>
        <div><span style="color:#888;letter-spacing:0.1em;text-transform:uppercase;font-size:10px;">COST </span>${fmtMoney(it.unit_cost)}</div>
        <div><span style="color:#0a5;letter-spacing:0.1em;text-transform:uppercase;font-size:10px;font-weight:700;">PRICE </span><b>${fmtMoney(it.wholesale_price)}</b></div>
      </div>
    </div>`;
}

function renderBadge(status, isGhost) {
  const map = {
    available:  { txt: 'AVAILABLE',  bg: '#dff5e8', color: '#0a5' },
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

function escape(s) { return String(s ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c])); }
