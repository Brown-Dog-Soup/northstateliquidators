/* NSL Loading Dock theme — minimal client-side glue */
(function () {
  'use strict';

  // Live cart count update on add-to-cart submission
  document.addEventListener('submit', function (e) {
    if (!e.target.matches('form[action="/cart/add"]')) return;
    e.preventDefault();
    var form = e.target;
    var btn = form.querySelector('button[type="submit"]');
    var originalText = btn ? btn.textContent : '';
    if (btn) { btn.textContent = 'Adding…'; btn.disabled = true; }

    fetch('/cart/add.js', {
      method: 'POST',
      body: new FormData(form),
      headers: { 'Accept': 'application/json' }
    })
    .then(function (r) { return r.json(); })
    .then(function () { return fetch('/cart.js').then(function (r) { return r.json(); }); })
    .then(function (cart) {
      var counter = document.getElementById('cart-count');
      if (counter) counter.textContent = cart.item_count;
      if (btn) { btn.textContent = '✓ In cart'; }
      setTimeout(function () { if (btn) { btn.textContent = originalText; btn.disabled = false; } }, 1400);
    })
    .catch(function () {
      if (btn) { btn.textContent = originalText; btn.disabled = false; }
      window.location.href = '/cart';
    });
  });
})();
