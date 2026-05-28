import { apiClient, toast } from './api.js';

const $ = sel => document.querySelector(sel);

init();
async function init() {
  const me = await apiClient.me();
  $('#me').innerHTML = me ? `${me.userDetails} · <a href="/logout">sign out</a>` : `<a href="/login">sign in</a>`;
}

const fileInput = $('#csv-file');
const btn = $('#import-btn');

fileInput.addEventListener('change', () => { btn.disabled = !fileInput.files[0]; });

btn.addEventListener('click', async () => {
  const file = fileInput.files[0];
  if (!file) return;
  btn.disabled = true;
  btn.textContent = 'Importing…';
  $('#result').innerHTML = '';
  try {
    const text = await file.text();
    const r = await apiClient.importCsv(file.name, text);
    $('#result').innerHTML = `
      <div style="background:#E6F4EA;border:1px solid #1B7E3A;padding:14px 16px;">
        <b style="font-family:Anton;letter-spacing:0.03em;text-transform:uppercase;color:#1B7E3A;">Imported</b>
        <p style="margin:6px 0 0;font-family:'JetBrains Mono',monospace;font-size:13px;line-height:1.7;">
          file: ${escape(r.filename)}<br>
          rows read: ${r.rows}<br>
          added: <b>${r.inserted}</b><br>
          updated: <b>${r.updated}</b><br>
          skipped: ${r.skipped}
        </p>
        <p style="margin:10px 0 0;font-size:13px;">New items are <b>Available</b> in <a href="inventory.html" style="color:#002868;">Inventory</a> — check them off there to build a box.</p>
      </div>`;
    toast(`Imported ${r.inserted} new, ${r.updated} updated`, 'ok', 3000);
    fileInput.value = '';
  } catch (e) {
    const detail = e.data?.error || e.message;
    $('#result').innerHTML = `<div style="background:#FCE4E4;border:1px solid #C0392B;padding:14px 16px;color:#90251c;">Import failed: ${escape(detail)}</div>`;
    toast(`Import failed: ${detail}`, 'err', 4000);
  } finally {
    btn.disabled = false;
    btn.textContent = 'Import';
  }
});

// Downloadable starter template so non-tech staff get the columns right.
$('#download-template').addEventListener('click', e => {
  e.preventDefault();
  const csv = [
    'sku,title,brand,category,condition,qty,msrp,cost,price',
    'BELLA-TEE-BLK-M,Bella Canvas Tee Black M,Bella Canvas,Apparel,new,48,12.00,1.50,4.00',
    ',Mystery Home Goods Box,,Home Goods,untested,1,,,25.00'
  ].join('\r\n');
  const blob = new Blob([csv], { type: 'text/csv' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = 'nsl-inventory-template.csv';
  a.click();
  URL.revokeObjectURL(a.href);
});

function escape(s) { return String(s ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c])); }
