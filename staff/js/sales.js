import { apiClient, toast, fmtMoney } from './api.js';

const $ = sel => document.querySelector(sel);
const money = cents => fmtMoney((cents ?? 0) / 100);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c]));
const n = v => Number(v || 0);
const plural = (count, one, many) => `${count} ${count === 1 ? one : many}`;
// Short and the same in all three tables. Seconds are never the question here,
// and every character of a nowrap date column is a character of sideways swipe
// between a phone and the Refund button.
const when = iso => iso ? new Date(iso).toLocaleString([], { dateStyle: 'short', timeStyle: 'short' }) : '—';

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
  // gross = what Square collected for GOODS (web + floor). Sales tax and the
  // delivery fee are held money, not revenue, and have their own tile. Boxes
  // marked SOLD in admin are listed separately and NOT added in: a floor sale
  // rung up on the terminal is already in Floor / other, and marking that box
  // SOLD afterwards is normal — adding admin on top would count it twice.
  const adminCount = s.admin_count ?? s.sales.filter(x => x.source === 'admin').length;
  const squareNote = s.square_error
    ? `<div class="tile" style="grid-column:1 / -1;background:#FFF3C4;color:#7a5a00;"><div class="lbl">Square unavailable</div>
        <div class="sub" style="color:#7a5a00;">${esc(s.square_error)}</div></div>`
    : '';
  $('#tiles').innerHTML = squareNote + `
    <div class="tile"><div class="lbl">Gross · ${s.days}d</div><div class="val">${money(s.gross_cents)}</div>
      <div class="sub">${plural(s.sale_count, 'Square sale', 'Square sales')} · web + floor · goods only</div></div>
    <div class="tile"><div class="lbl">Website</div><div class="val">${money(s.web_cents)}</div>
      <div class="sub">boxes sold online, before tax and delivery</div></div>
    <div class="tile"><div class="lbl">Floor / other</div><div class="val">${money(s.floor_cents)}</div></div>
    <div class="tile"><div class="lbl">Tax + delivery collected</div><div class="val">${money(s.tax_delivery_cents)}</div>
      <div class="sub">Held for NCDOR and the truck — not revenue.</div></div>
    <div class="tile"><div class="lbl">Marked sold (admin)</div><div class="val">${money(s.admin_cents)}</div>
      <div class="sub">${adminCount} box${adminCount === 1 ? '' : 'es'} marked SOLD in admin — not in Gross; may already be a floor sale</div></div>
    <div class="tile"><div class="lbl">Margin</div>
      <div class="val ${margin >= 0 ? 'pos' : 'neg'}">${marginKnown ? money(margin) : '—'}</div>
      <div class="sub">goods − our cost, boxes with a cost</div></div>
    <div class="tile"><div class="lbl">Refunded</div><div class="val">${money(s.refunded_cents)}</div></div>`;

  $('#sales-table').innerHTML =
    `<tr><th>Date</th><th>Channel</th><th>Box</th><th class="money">Amount</th><th class="money">Tax / Del</th><th class="money">Our cost</th><th class="money">Margin</th></tr>` +
    (s.sales.map(x => `
      <tr>
        <td>${when(x.created_at)}</td>
        <td><span class="chan ${esc(x.channel)}">${esc(x.channel)}</span></td>
        <td>${x.boxes
              ? esc(x.boxes) + (x.box_count > 1
                  ? ` <span style="color:#888;font-size:11px;">(${x.box_count} boxes)</span>`
                  : ` ${esc(x.display_name || '')}`)
              : x.pallet_number
                ? `#${x.pallet_number} ${esc(x.display_name || '')}`
                : x.channel === 'floor'
                  ? '<span style="color:#999;">in-person sale</span>'
                  : '<span style="color:#999;">no box handed over</span>'}${x.note ? `<div class="subnote">${esc(x.note)}</div>` : ''}</td>
        <td class="money">${x.amount_cents === 0 && x.source === 'admin'
            ? '<span title="No price set on this box">—</span>'
            : money(x.amount_cents)}${x.refunded_cents ? ` <span class="neg">(−${money(x.refunded_cents)})</span>` : ''}</td>
        <td class="money">${x.tax_cents ? money(x.tax_cents) : '—'}${x.delivery_cents
            ? ` <span title="${esc(x.delivery_method || '')}">+${money(x.delivery_cents)} del</span>` : ''}</td>
        <td class="money">${x.cost != null ? fmtMoney(x.cost) : '—'}</td>
        <td class="money ${x.margin_cents != null ? (x.margin_cents >= 0 ? 'pos' : 'neg') : ''}">${x.margin_cents != null ? money(x.margin_cents) : '—'}</td>
      </tr>`).join('') || '<tr><td colspan="7" style="color:#666;">No sales in this window.</td></tr>');

  $('#payouts-table').innerHTML =
    `<tr><th>Arrives</th><th>Status</th><th class="money">Amount</th></tr>` +
    (s.payouts.map(p => `
      <tr><td>${esc(p.arrival || '—')}</td><td>${esc(p.status || '—')}</td>
      <td class="money">${money(p.amount_cents)}</td></tr>`).join('')
      || '<tr><td colspan="3" style="color:#666;">No deposits in this window.</td></tr>');
}

// ---------------------------------------------------------------------------
// Needs attention
//
// Rob and Norman read this table to decide whether to hand a stranger money
// back, so every cell has to say what actually happened. It used to guess the
// reason from one boolean — UNMATCHED or "box was already sold" — which meant a
// DELETED box printed "no box matched" in the Box column and "box was already
// sold" as the reason, side by side, both wrong.
// ---------------------------------------------------------------------------

/// What is actually owed on a flagged row, in cents, or null when we do not
/// know the payment total. refund_due_cents is what the unavailable boxes and
/// their tax come to; anything already refunded comes OFF it, because refunds
/// accumulate and a second click must not send the whole owed figure again.
function owedCents(r) {
  const back = n(r.refunded_cents);
  if (r.refund_due_cents != null) return Math.max(0, n(r.refund_due_cents) - back);
  // PARTIAL_REFUND_FLAGGED with no owed figure is fulfilment saying, in as many
  // words, that it could NOT work out the debt: boxes on this order did sell,
  // but a box the buyer paid for has no line on our copy. The whole payment is
  // the bar for CLEARING the flag — it is not an amount to hand back, because
  // the buyer is keeping the boxes that did arrive. Unknown, not "all of it".
  if (String(r.status || '') === 'PARTIAL_REFUND_FLAGGED') return null;
  if (r.amount_cents == null) return null;
  return Math.max(0, n(r.amount_cents) - back);
}

/// The Box column. "no box matched" is reserved for a payment that hit no order
/// at all — every other empty state has a different, truer sentence.
function attentionBox(r) {
  if (r.boxes) return esc(r.boxes);
  if (r.pallet_number) return `#${r.pallet_number} ${esc(r.display_name || '')}`;
  if (n(r.sold_boxes) > 0)
    return `<span class="flag">${plural(n(r.sold_boxes), 'box', 'boxes')} sold — the box record has since been deleted</span>`;
  if (n(r.box_lines) > 0)
    return `<span class="flag">nothing handed over (${plural(n(r.box_lines), 'box', 'boxes')} on the order)</span>`;
  return '<span class="flag">no box matched</span>';
}

/// Why this row is flagged, derived from the status the webhook or fulfilment
/// actually wrote. Returns plain text — the caller escapes it.
function attentionReason(r) {
  const s = String(r.status || '');
  // Agree with the button. A flagged row whose debt is settled says so rather
  // than quoting its old status, which would read as money still outstanding
  // beside a greyed-out "Nothing owed".
  if (owedCents(r) === 0)
    return 'everything owed on this payment has gone back — there is nothing left to refund here';
  if (s === 'UNMATCHED')      return 'this payment matched no order — nothing was sold';
  if (s === 'REFUND_FLAGGED') return 'nothing was handed over on this payment — the whole amount is owed back';
  if (s === 'PARTIAL_REFUND_FLAGGED') {
    if (n(r.unavailable_boxes) > 0)
      return `${plural(n(r.unavailable_boxes), 'box was', 'boxes were')} no longer available — a partial refund is owed`;
    return 'the order lines do not add up — work the amount out from the Square receipt';
  }
  if (s === 'PARTIAL_REFUNDED') return 'partly refunded — the rest of what is owed has not gone back yet';
  if (s === 'REFUND_PENDING' || s === 'REFUND_APPROVED')
    return 'refund sent to Square — it has not settled, so this stays here until it does';
  if (s === 'REFUND_FAILED')   return 'the refund FAILED at Square — the money is still owed';
  if (s === 'REFUND_REJECTED') return 'Square REJECTED the refund — the money is still owed';
  if (s.startsWith('REFUND_')) return 'refund ' + s.slice(7).toLowerCase().replace(/_/g, ' ');
  if (s === 'COMPLETED')       return 'flagged by hand — check the Square receipt';
  return s.toLowerCase().replace(/_/g, ' ');
}

/// The action cell. A Refund button that asks for the wrong amount is worse
/// than a disabled one, so there are only three outcomes: look the amount up,
/// refund exactly what is owed, or nothing to do.
function attentionAction(r) {
  const pid = esc(r.square_payment_id);
  if (r.amount_cents == null)
    return `<button class="btn do-lookup" data-pid="${pid}" style="padding:4px 12px;font-size:11px;"
              title="We have no amount for this payment, so no refund can be worked out and nothing can clear this row. This asks Square what it was for and writes it down. It does not refund anything.">Get amount from Square</button>`;
  const owed = owedCents(r);
  if (owed === null)
    return `<button class="btn" disabled style="padding:4px 12px;font-size:11px;"
              title="Boxes on this order did sell, but our copy of the order is incomplete, so we cannot work out what is owed — and a button that guesses would guess with the buyer's money. Read the amount off the Square receipt and refund it in the Square Dashboard.">Amount unclear</button>`;
  if (owed === 0)
    return `<button class="btn" disabled style="padding:4px 12px;font-size:11px;"
              title="What was owed on this payment has already gone back. Refund the rest in the Square Dashboard if you mean to.">Nothing owed</button>`;
  return `<button class="btn btn-danger do-refund" data-pid="${pid}" data-amt="${owed}" data-full="${n(r.amount_cents)}"
            style="padding:4px 12px;font-size:11px;background:#b42318;color:#fff;border:none;"
            title="Refunds the amount owed including that box's sales tax. Typing a different amount does NOT add tax.">Refund ${money(owed)}</button>`;
}

async function loadPayments() {
  let rows;
  try { rows = await apiClient.squarePayments(); }
  catch (e) { toast(`Payment log failed: ${e.message}`, 'err', 4000); return; }

  const flagged = rows.filter(r => r.needs_refund);
  $('#attention').hidden = flagged.length === 0;
  if (flagged.length) {
    $('#attention-table').innerHTML =
      `<tr><th>Date</th><th>Box</th><th class="money">Paid</th><th class="money">Owed back</th><th>Why</th><th></th></tr>` +
      flagged.map(r => `
        <tr>
          <td>${when(r.created_at)}</td>
          <td>${attentionBox(r)}</td>
          <td class="money">${r.amount_cents == null
              ? '<span class="flag" title="Square did not tell us what this payment was for.">unknown</span>'
              : money(r.amount_cents)}${r.refunded_cents ? ` <span class="neg">(−${money(r.refunded_cents)})</span>` : ''}</td>
          <td class="money">${owedCents(r) == null ? '—' : money(owedCents(r))}</td>
          <td class="flag why">${esc(attentionReason(r))}</td>
          <td>${attentionAction(r)}</td>
        </tr>`).join('');
    document.querySelectorAll('.do-refund').forEach(b => b.addEventListener('click', () => refund(b)));
    document.querySelectorAll('.do-lookup').forEach(b => b.addEventListener('click', () => lookUpAmount(b)));
  }

  $('#payments-table').innerHTML =
    `<tr><th>Date</th><th>Box</th><th class="money">Amount</th><th>Status</th></tr>` +
    (rows.map(r => `
      <tr>
        <td>${when(r.created_at)}</td>
        <td>${r.boxes ? esc(r.boxes) : r.pallet_number ? `#${r.pallet_number} ${esc(r.display_name || '')}` : '—'}${
          r.delivery_method && r.delivery_method !== 'pickup'
            ? ` <span class="tag">${r.delivery_method === 'flea' ? 'flea market' : 'delivery'}</span>` : ''}</td>
        <td class="money">${r.amount_cents == null ? '—' : money(r.amount_cents)}${
          r.refunded_cents ? ` <span class="neg">(−${money(r.refunded_cents)})</span>` : ''}</td>
        <td>${r.needs_refund ? '<span class="flag">⚠ needs refund</span>' : esc(r.status)}</td>
      </tr>`).join('') || '<tr><td colspan="4" style="color:#666;">No website payments recorded yet.</td></tr>');
}

async function refund(btn) {
  const pid = btn.dataset.pid;
  const amt = Number(btn.dataset.amt);
  const full = Number(btn.dataset.full);
  // Say WHICH refund this is. On a partial-unavailable cart the button sends
  // the price of the boxes we could not hand over plus their sales tax — not
  // the whole order, which the buyer is keeping the rest of.
  const msg = amt >= full
    ? `Refund the full ${money(amt)} back to the buyer? This cannot be undone.`
    : `Refund ${money(amt)} — the part of this ${money(full)} payment the buyer is owed, including the sales tax on it. The rest stays with the sale.\n\nThis cannot be undone.`;
  if (!confirm(msg)) return;
  const label = btn.textContent;
  btn.disabled = true;
  btn.textContent = 'Refunding…';
  try {
    const r = await apiClient.squareRefund(pid, null, amt);
    toast(r.settled
      ? `Refunded ${money(r.amountCents)} — settled.`
      : `Refund of ${money(r.amountCents)} sent — Square says ${String(r.refundStatus).toLowerCase()}, so this row stays flagged until the money moves.`,
      r.settled ? 'ok' : 'err', 5000);
    await loadPayments();
  } catch (e) {
    toast(`Refund failed: ${e.data?.error || e.message}`, 'err', 5000);
    btn.disabled = false;
    btn.textContent = label;
  }
}

/// Amendment 2's exit. A payment we recorded with no amount can never be
/// cleared by a refund — that is deliberate — so it would sit here forever,
/// including after a staff member refunded it by hand in Square exactly as the
/// old error message told them to. This asks Square what the payment was for.
/// It never says "refunded", because nothing was refunded here.
async function lookUpAmount(btn) {
  btn.disabled = true;
  btn.textContent = 'Asking Square…';
  try {
    const r = await apiClient.squarePaymentAmount(btn.dataset.pid);
    toast(r.cleared
      ? `Square says ${money(r.amountCents)} — already covered by ${money(r.refundedCents)} refunded, so this row is cleared.`
      : `Square says ${money(r.amountCents)} — nothing refunded yet. Refund is now enabled on this row.`,
      'ok', 5000);
    await loadPayments();
  } catch (e) {
    toast(`Could not get the amount: ${e.data?.error || e.message}`, 'err', 6000);
    btn.disabled = false;
    btn.textContent = 'Get amount from Square';
  }
}

/// One line, and the number that matters last. Both response shapes go through
/// here: the ordinary sweep, and the 503 a host shutdown returns, which carries
/// an `aborted` marker and NO needsRefund count — printing "undefined flagged"
/// is how this toast got here in the first place.
function reconcileSummary(r) {
  const parts = [
    `${plural(n(r.healed), 'sale', 'sales')} healed`,
    `${plural(n(r.linksDeleted), 'link', 'links')} retired`,
  ];
  if (n(r.refundsApplied))
    parts.push(`${plural(n(r.refundsApplied), 'refund', 'refunds')} applied (${money(r.refundCentsApplied)})`);
  if (n(r.squareErrors))
    parts.push(`⚠ Square wouldn't answer for ${plural(n(r.squareErrors), 'order', 'orders')}`);
  if (r.needsRefund != null)
    parts.push(n(r.needsRefund) ? `⚠ ${plural(n(r.needsRefund), 'payment needs', 'payments need')} attention`
                                : 'nothing needs attention');
  return parts.join(' · ');
}

const reconcileBad = r => n(r.needsRefund) > 0 || n(r.squareErrors) > 0 || n(r.refundErrors) > 0;

async function reconcile() {
  const btn = $('#reconcile');
  btn.disabled = true;
  btn.textContent = 'Reconciling…';
  try {
    const r = await apiClient.squareReconcile();
    toast(`Reconcile: ${reconcileSummary(r)}`, reconcileBad(r) ? 'err' : 'ok', 6000);
    await loadAll();
  } catch (e) {
    // A host shutdown mid-sweep answers 503 with everything it did commit. That
    // is a partial success, not a failure: say what landed and that a re-run
    // finishes the job.
    const d = e.data;
    if (e.status === 503 && d?.aborted) {
      toast(`Reconcile stopped early (${d.aborted}) — ${reconcileSummary(d)}. Run it again to finish.`, 'err', 7000);
      await loadAll();
    } else {
      toast(`Reconcile failed: ${d?.error || e.message}`, 'err', 5000);
    }
  }
  finally { btn.disabled = false; btn.textContent = 'Reconcile with Square'; }
}
