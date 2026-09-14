import { apiClient, toast } from './api.js';

const $ = sel => document.querySelector(sel);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c]));

init();
async function init() {
  const me = await apiClient.me();
  $('#me').innerHTML = me ? `${me.userDetails} · <a href="/logout">sign out</a>` : `<a href="/login">sign in</a>`;
  await loadMembers();
}

// Rows come back newest first from GET /api/members (ORDER BY created_at DESC).
async function loadMembers() {
  let rows;
  try { rows = await apiClient.members(); }
  catch (e) {
    $('#members-count').textContent = 'Members unavailable';
    toast(`Load failed: ${e.message}`, 'err', 4000);
    return;
  }
  rows = rows || [];
  $('#members-count').textContent = `${rows.length} member${rows.length === 1 ? '' : 's'}`;

  $('#members-table').innerHTML =
    `<tr><th>Member #</th><th>Name</th><th>Email</th><th>Phone</th><th>City/State</th><th>Zip</th><th>Heard from</th><th>Joined</th><th>Welcome email</th></tr>` +
    (rows.map(m => {
      const cityState = [m.city, m.state].filter(Boolean).join(', ');
      const addressLines = [m.address1, m.address2, cityState].filter(Boolean).map(esc);
      return `
      <tr>
        <td class="num">${esc(m.member_number)}</td>
        <td>${esc([m.first_name, m.last_name].filter(Boolean).join(' '))}</td>
        <td>${m.email ? `<a href="mailto:${esc(m.email)}">${esc(m.email)}</a>` : '—'}</td>
        <td>${m.phone ? `<a href="tel:${esc(m.phone)}">${esc(m.phone)}</a>` : '—'}</td>
        <td>${addressLines.length ? addressLines.join('<br>') : '—'}</td>
        <td>${esc(m.zip || '—')}</td>
        <td>${esc(m.how_heard || '—')}</td>
        <td>${m.created_at ? new Date(m.created_at).toLocaleDateString() : '—'}</td>
        <td>${m.welcome_sent_at
          ? new Date(m.welcome_sent_at).toLocaleDateString()
          : `<button type="button" class="btn btn-secondary resend-welcome" data-num="${esc(m.member_number)}" style="padding:4px 10px;font-size:11px;">Send welcome</button>`}</td>
      </tr>`;
    }).join('') || '<tr><td colspan="9" style="color:#666;">No members yet — the signup form is on the public site.</td></tr>');

  document.querySelectorAll('.resend-welcome').forEach(b => b.addEventListener('click', async () => {
    b.disabled = true; b.textContent = 'Sending…';
    try {
      const r = await apiClient.resendWelcome(b.dataset.num);
      toast(r.sent ? `Welcome email sent to member ${b.dataset.num}` : 'Email not sent — check MAIL settings / logs', r.sent ? 'ok' : 'err', 3500);
      await loadMembers();
    } catch (e) {
      toast(`Send failed: ${e.data?.error || e.message}`, 'err', 4000);
      b.disabled = false; b.textContent = 'Send welcome';
    }
  }));
}
