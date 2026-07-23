/* Neon mode — an opt-in cyberpunk skin.
   Loaded synchronously in <head> so the saved theme is applied before first paint. */
(function () {
  var KEY  = 'apex-theme';
  var root = document.documentElement;

  try {
    if (localStorage.getItem(KEY) === 'neon') root.setAttribute('data-theme', 'neon');
  } catch (e) { /* storage blocked — fall back to the default theme */ }

  function isOn()  { return root.getAttribute('data-theme') === 'neon'; }
  function label() { return isOn() ? 'Jack out' : 'Jack in'; }

  document.addEventListener('DOMContentLoaded', function () {
    var btn = document.querySelector('.theme-toggle');
    if (!btn) return;

    function sync() {
      btn.setAttribute('aria-pressed', isOn() ? 'true' : 'false');
      btn.setAttribute('title', label());
      btn.setAttribute('aria-label', label() + ' — toggle neon mode');
    }

    btn.addEventListener('click', function () {
      if (isOn()) root.removeAttribute('data-theme');
      else        root.setAttribute('data-theme', 'neon');
      try { localStorage.setItem(KEY, isOn() ? 'neon' : 'default'); } catch (e) {}
      sync();
    });

    sync();
  });
})();
