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
  duplicatePallet:(id) => api('POST', `/api/pallets/${id}/duplicate`),
  deletePallet:(id) => api('DELETE', `/api/pallets/${id}`),
  archivePallet: (id, archived = true) => api('PATCH', `/api/pallets/${id}`, { archived }),
  setPalletCategory: (id, category) => api('PATCH', `/api/pallets/${id}`, { category }),

  // #6 publish state: draft | live | ghost | sold
  setPublishState: (id, publishState) => api('PATCH', `/api/pallets/${id}`, { publishState }),
  // #3 prices: send the keys so the server knows to set/clear them
  setPalletPrices: (id, listPrice, salePrice) =>
    api('PATCH', `/api/pallets/${id}`, { listPrice, salePrice }),
  // #9 build a pallet from checked inventory rows; add an ad-hoc no-barcode item
  createPalletFromItems: (lpns, displayName = null) =>
    api('POST', '/api/pallets/from-items', { lpns, displayName }),
  addPalletItem: (id, item) => api('POST', `/api/pallets/${id}/items`, item),
  // public-site read of live + recently-sold pallets
  publicPallets: () => api('GET', '/api/public/pallets'),

  // Square: sales dashboard, payment audit, refunds, reconcile, wholesale invoices
  salesSummary:     (days = 30) => api('GET', `/api/sales-summary?days=${days}`),
  squarePayments:   () => api('GET',  '/api/square-payments'),
  squareRefund:     (paymentId, reason = null) => api('POST', '/api/square-refund', { paymentId, reason }),
  squareReconcile:  () => api('POST', '/api/square-reconcile'),
  invoiceBox:       (id, email, name = null, price = null) => api('POST', `/api/pallets/${id}/invoice`, { email, name, price }),
  cancelBoxInvoice: (id) => api('POST', `/api/pallets/${id}/invoice-cancel`),

  patchItem:  (id,b) => api('PATCH',  `/api/items/${id}`, b),
  deleteItem: (id)   => api('DELETE', `/api/items/${id}`),
  bulkDeleteItems: (ids) => api('POST', '/api/items/bulk-delete', { ids }),

  // Inventory page — every catalog row + assignment status. Pass {status, lot, q}
  // to filter (q is a substring search across title/brand/upc/lpn).
  inventory: (opts = {}) => {
    const params = new URLSearchParams();
    if (opts.status) params.set('status', opts.status);
    if (opts.lot)    params.set('lot',    opts.lot);
    if (opts.q)      params.set('q',      opts.q);
    if (opts.limit)  params.set('limit',  String(opts.limit));
    const qs = params.toString();
    return api('GET', `/api/inventory${qs ? '?' + qs : ''}`);
  },
  inventorySummary: () => api('GET', '/api/inventory/summary'),
  // Put a specific quantity of an inventory item into a box. manifestId targets
  // an existing box; pass null + newBoxName to create one.
  allocateToBox: (lpn, qty, manifestId = null, newBoxName = null) =>
    api('POST', '/api/inventory/allocate', { lpn, qty, manifestId, newBoxName }),
  // Delete an inventory item (only if it's not already in a box → 409 otherwise).
  deleteInventoryItem: (lpn) =>
    api('DELETE', `/api/inventory?lpn=${encodeURIComponent(lpn)}`),
  // Where an item's units went: per-box breakdown with qty + sold status.
  inventoryAllocations: (lpn) =>
    api('GET', `/api/inventory/allocations?lpn=${encodeURIComponent(lpn)}`),
  // Delete many catalog items at once; items in a box are skipped server-side.
  bulkDeleteInventory: (lpns) =>
    api('POST', '/api/inventory/bulk-delete', { lpns }),
  setPalletGhost: (id, isGhost) => api('PATCH', `/api/pallets/${id}`, { isGhost }),
  generateGhostBackstock: (palletCount = 5, itemsPerPallet = 12, category = null) =>
    api('POST', '/api/pallets/generate-ghost-backstock', { palletCount, itemsPerPallet, category }),

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

  // #8 CSV import — POST the raw CSV text; filename rides in a header.
  importCsv: async (filename, text) => {
    const r = await fetch('/api/import-csv', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'text/csv', 'x-filename': filename || 'csv-upload.csv' },
      body: text
    });
    const body = await r.text();
    let data; try { data = body ? JSON.parse(body) : null; } catch { data = body; }
    if (!r.ok) throw Object.assign(new Error(`import failed: ${r.status}`), { data });
    return data;
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
