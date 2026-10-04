/* Accessible mobile navigation; product links work without JavaScript. */
(function () {
  var toggle = document.querySelector('.home .nav-toggle');
  var menu = document.getElementById('main-menu');
  if (!toggle || !menu) return;
  function setOpen(open) {
    menu.classList.toggle('open', open);
    toggle.setAttribute('aria-expanded', String(open));
    toggle.setAttribute('aria-label', open ? 'Close menu' : 'Open menu');
  }
  toggle.addEventListener('click', function () {
    setOpen(toggle.getAttribute('aria-expanded') !== 'true');
  });
  menu.addEventListener('click', function (event) {
    if (event.target.closest('a')) setOpen(false);
  });
  document.addEventListener('keydown', function (event) {
    if (event.key === 'Escape' && menu.classList.contains('open')) {
      setOpen(false);
      toggle.focus();
    }
  });
  window.matchMedia('(max-width: 760px)').addEventListener('change', function () {
    setOpen(false);
  });
})();
