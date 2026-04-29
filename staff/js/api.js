// Thin fetch wrapper around the SWA-managed Functions in /api/*.
// Same-origin so cookies (SWA Entra session) ride along automatically.

export async function api(method, path, body = null, opts = {}) {
  const init = {
    method,
    headers: { 'Accept': 'application/json' },
    credentials: 'same-origin',
    ...opts
  };
  if (body && !(body instanceof Blob) && !(body instanceof ArrayBuffer)) {
    init.headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(body);
  } else if (body) {
    init.body = body;
  }
  const r = await fetch(path, init);
  const text = await r.text();
  let data; try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!r.ok) throw Object.assign(new Error(`${method} ${path} -> ${r.status}`), { status: r.status, data });
  return data;
}

// Pallet-level top-bucket categories. Editable at /staff/admin pallet detail.
// Stored in manifests.category. Receiver-applied bucket independent of the
// per-item category column on line_items (which carries manifest taxonomy).
export const NSL_CATEGORIES = [
  'Apparel',
  'Electronics',
  'Appliances',
  'Furniture',
  'Home Goods',
  'Holiday',
  'Mixed Goods'
];

export const apiClient = {
  health:           () => api('GET',  '/api/health'),
  lookup:    (code) => api('GET',  `/api/lookup/${encodeURIComponent(code)}`),
  scan:    (record) => api('POST', '/api/scan', record),

  // pallets({ includeArchived: true }) surfaces archived pallets too.
  pallets:    (opts = {}) => api('GET',  `/api/pallets${opts.includeArchived ? '?includeArchived=true' : ''}`),
  createPallet:  (b) => api('POST', '/api/pallets', b),
  pallet:    (id) => api('GET',  `/api/pallets/${id}`),
  patchPallet:(id,b) => api('PATCH', `/api/pallets/${id}`, b),
  deletePallet:(id) => api('DELETE', `/api/pallets/${id}`),
  archivePallet: (id, archived = true) => api('PATCH', `/api/pallets/${id}`, { archived }),
  setPalletCategory: (id, category) => api('PATCH', `/api/pallets/${id}`, { category }),

  patchItem:  (id,b) => api('PATCH',  `/api/items/${id}`, b),
  deleteItem: (id)   => api('DELETE', `/api/items/${id}`),
  bulkDeleteItems: (ids) => api('POST', '/api/items/bulk-delete', { ids }),

  // POST a Blob/ArrayBuffer; sets Content-Type from the Blob's type
  uploadPhoto: async (kind, id, blob) => {
    const r = await fetch(`/api/upload-photo?kind=${kind}&id=${id}`, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': blob.type || 'image/jpeg' },
      body: blob
    });
    if (!r.ok) throw new Error(`upload failed: ${r.status}`);
    return r.json();
  },

  // SWA built-in user info
  me: async () => {
    try { const r = await fetch('/.auth/me', { credentials: 'same-origin' });
          const j = await r.json();
          return j.clientPrincipal; }
    catch { return null; }
  }
};

export function toast(msg, kind = 'ok', durationMs = 2000) {
  let el = document.querySelector('.toast');
  if (!el) { el = document.createElement('div'); el.className = 'toast'; document.body.appendChild(el); }
  el.textContent = msg;
  el.className = `toast show ${kind}`;
  clearTimeout(toast._t);
  toast._t = setTimeout(() => { el.className = 'toast'; }, durationMs);
}

export function fmtMoney(n) {
  if (n === null || n === undefined || n === '') return '—';
  return '$' + Number(n).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}
