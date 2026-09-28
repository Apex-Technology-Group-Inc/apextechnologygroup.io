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

  /* "Destroy this website" badge — only exists in the DOM while jacked in,
     so the default theme never loads the third-party image. */
  var badge = null;
  function syncBadge() {
    var footer = document.querySelector('footer .container');
    if (!footer) return;
    if (isOn() && !badge) {
      badge = document.createElement('a');
      badge.className = 'destroy-badge';
      badge.href = 'https://destroy.spritefusion.com/?url=' + encodeURIComponent('https://apextechnologygroup.io') + '&from=badge';
      badge.target = '_blank';
      badge.rel = 'noopener';
      badge.innerHTML = '<img src="https://destroy.spritefusion.com/badge.svg" alt="Destroy this website" width="180" height="40">';
      footer.appendChild(badge);
    } else if (!isOn() && badge) {
      badge.remove();
      badge = null;
    }
  }

  document.addEventListener('DOMContentLoaded', function () {
    syncBadge();

    var btn = document.querySelector('.theme-toggle');
    if (!btn) return;

    function sync() {
      btn.setAttribute('aria-pressed', isOn() ? 'true' : 'false');
      btn.setAttribute('title', label());
      btn.setAttribute('aria-label', label() + ' — toggle neon mode');
      syncBadge();
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
