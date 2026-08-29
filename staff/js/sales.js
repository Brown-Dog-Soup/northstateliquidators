import { apiClient, toast, fmtMoney } from './api.js';

const $ = sel => document.querySelector(sel);
const money = cents => fmtMoney((cents ?? 0) / 100);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c]));

let days = Number(localStorage.getItem('nsl.sales.days')) || 30;

init();
async function init() {
  const me = await apiClient.me();
  $('#me').innerHTML = me ? `${me.userDetails} · <a href="/logout">sign out</a>` : `<a href="/login">sign in</a>`;
  document.querySelectorAll('.range button').forEach(b => b.addEventListener('click', () => {
    days = Number(b.dataset.days);
    localStorage.setItem('nsl.sales.days', String(days));
    loadAll();
  }));
  $('#reconcile')?.addEventListener('click', reconcile);
  await loadAll();
}

async function loadAll() {
  document.querySelectorAll('.range button').forEach(b =>
    b.classList.toggle('active', Number(b.dataset.days) === days));
  await Promise.all([loadSummary(), loadPayments()]);
}

async function loadSummary() {
  let s;
  try { s = await apiClient.salesSummary(days); }
  catch (e) {
    $('#tiles').innerHTML = `<div class="tile"><div class="lbl">Sales data unavailable</div><div class="sub">${esc(e.message)}</div></div>`;
    return;
  }
  const margin = s.sales.reduce((a, x) => a + (x.margin_cents ?? 0), 0);
  const marginKnown = s.sales.some(x => x.margin_cents != null);
  $('#tiles').innerHTML = `
    <div class="tile"><div class="lbl">Gross · ${s.days}d</div><div class="val">${money(s.gross_cents)}</div>
      <div class="sub">${s.sale_count} sale${s.sale_count === 1 ? '' : 's'}</div></div>
    <div class="tile"><div class="lbl">Website</div><div class="val">${money(s.web_cents)}</div></div>
    <div class="tile"><div class="lbl">Floor / other</div><div class="val">${money(s.floor_cents)}</div></div>
    <div class="tile"><div class="lbl">Web margin</div>
      <div class="val ${margin >= 0 ? 'pos' : 'neg'}">${marginKnown ? money(margin) : '—'}</div>
      <div class="sub">sale − our cost, matched boxes</div></div>
    <div class="tile"><div class="lbl">Refunded</div><div class="val">${money(s.refunded_cents)}</div></div>`;

  $('#sales-table').innerHTML =
    `<tr><th>Date</th><th>Channel</th><th>Box</th><th class="money">Amount</th><th class="money">Our cost</th><th class="money">Margin</th></tr>` +
    (s.sales.map(x => `
      <tr>
        <td>${x.created_at ? new Date(x.created_at).toLocaleString() : '—'}</td>
        <td><span class="chan ${x.channel}">${x.channel}</span></td>
        <td>${x.pallet_number ? `#${x.pallet_number} ${esc(x.display_name || '')}` : '<span style="color:#999;">in-person sale</span>'}</td>
        <td class="money">${money(x.amount_cents)}${x.refunded_cents ? ` <span class="neg">(−${money(x.refunded_cents)})</span>` : ''}</td>
        <td class="money">${x.cost != null ? fmtMoney(x.cost) : '—'}</td>
        <td class="money ${x.margin_cents != null ? (x.margin_cents >= 0 ? 'pos' : 'neg') : ''}">${x.margin_cents != null ? money(x.margin_cents) : '—'}</td>
      </tr>`).join('') || '<tr><td colspan="6" style="color:#666;">No sales in this window.</td></tr>');

  $('#payouts-table').innerHTML =
    `<tr><th>Arrives</th><th>Status</th><th class="money">Amount</th></tr>` +
    (s.payouts.map(p => `
      <tr><td>${esc(p.arrival || '—')}</td><td>${esc(p.status || '—')}</td>
      <td class="money">${money(p.amount_cents)}</td></tr>`).join('')
      || '<tr><td colspan="3" style="color:#666;">No deposits in this window.</td></tr>');
}

async function loadPayments() {
  let rows;
  try { rows = await apiClient.squarePayments(); }
  catch (e) { toast(`Payment log failed: ${e.message}`, 'err', 4000); return; }

  const flagged = rows.filter(r => r.needs_refund);
  $('#attention').hidden = flagged.length === 0;
  if (flagged.length) {
    $('#attention-table').innerHTML =
      `<tr><th>Date</th><th>Box</th><th class="money">Amount</th><th>Why</th><th></th></tr>` +
      flagged.map(r => `
        <tr>
          <td>${new Date(r.created_at).toLocaleString()}</td>
          <td>${r.pallet_number ? `#${r.pallet_number} ${esc(r.display_name || '')}` : '<span class="flag">no box matched</span>'}</td>
          <td class="money">${money(r.amount_cents)}</td>
          <td class="flag">${r.status === 'UNMATCHED' ? 'payment matched no box' : 'box was already sold'}</td>
          <td><button class="btn btn-danger do-refund" data-pid="${esc(r.square_payment_id)}" data-amt="${r.amount_cents}"
                style="padding:4px 12px;font-size:11px;background:#b42318;color:#fff;border:none;">Refund</button></td>
        </tr>`).join('');
    document.querySelectorAll('.do-refund').forEach(b => b.addEventListener('click', () => refund(b)));
  }

  $('#payments-table').innerHTML =
    `<tr><th>Date</th><th>Box</th><th class="money">Amount</th><th>Status</th></tr>` +
    (rows.map(r => `
      <tr>
        <td>${new Date(r.created_at).toLocaleString()}</td>
        <td>${r.pallet_number ? `#${r.pallet_number} ${esc(r.display_name || '')}` : '—'}</td>
        <td class="money">${money(r.amount_cents)}</td>
        <td>${r.needs_refund ? '<span class="flag">⚠ needs refund</span>' : esc(r.status)}</td>
      </tr>`).join('') || '<tr><td colspan="4" style="color:#666;">No website payments recorded yet.</td></tr>');
}

async function refund(btn) {
  const pid = btn.dataset.pid;
  if (!confirm(`Refund ${money(Number(btn.dataset.amt))} back to the buyer's card? This cannot be undone.`)) return;
  btn.disabled = true;
  btn.textContent = 'Refunding…';
  try {
    const r = await apiClient.squareRefund(pid);
    toast(`Refund ${r.refundStatus}`, 'ok', 2500);
    await loadPayments();
  } catch (e) {
    toast(`Refund failed: ${e.message}`, 'err', 4000);
    btn.disabled = false;
    btn.textContent = 'Refund';
  }
}

async function reconcile() {
  const btn = $('#reconcile');
  btn.disabled = true;
  btn.textContent = 'Reconciling…';
  try {
    const r = await apiClient.squareReconcile();
    toast(`Reconcile: ${r.healed} healed, ${r.retired} link${r.retired === 1 ? '' : 's'} retired, ${r.stillOpen} open, ${r.needsRefund} flagged`, 'ok', 4000);
    await loadAll();
  } catch (e) { toast(`Reconcile failed: ${e.message}`, 'err', 4000); }
  finally { btn.disabled = false; btn.textContent = 'Reconcile with Square'; }
}
