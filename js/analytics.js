/* ════════════════════════════════════════════════════════
   Analytics: Google Analytics 4, plus first-party events sent to /api/events
   and stored on our own server (MongoDB). Disclosed on privacy.html.

   ┌───────────────────────────────────────────────────────┐
   │  SET GA_ID to the Measurement ID of the GA4 property   │
   │  for apextechnologygroup.io (Admin → Data streams →    │
   │  Web). Until then GA stays off. Use a NEW property:    │
   │  sharing one with flightdose.com or toxfinder.com      │
   │  merges both sites' data.                              │
   └───────────────────────────────────────────────────────┘

   GA loads only on apextechnologygroup.io, so local testing never reaches it.
   First-party events go to whichever /api is serving the page; on GitHub Pages
   there is none and the beacon fails silently. Pattern from flightdose.com's
   js/analytics.js and ToxFinder's web/src/lib/analytics.ts.
   ════════════════════════════════════════════════════════ */
window.ApexAnalytics = (function () {
  'use strict';

  var GA_ID = 'G-3GV0VSQTT0';
  var HOST = 'apextechnologygroup.io';
  var ENDPOINT = '/api/events';
  var SESSION_KEY = 'apex.sid';

  // The placeholder contains an underscore precisely so it cannot satisfy this test.
  var gaOn = location.hostname === HOST && /^G-[A-Z0-9]{6,}$/.test(GA_ID);
  var queue = [];
  var timer = null;

  function gtag() { window.dataLayer.push(arguments); }

  if (gaOn) {
    window.dataLayer = window.dataLayer || [];
    window.gtag = gtag;
    var s = document.createElement('script');
    s.async = true;
    s.src = 'https://www.googletagmanager.com/gtag/js?id=' + encodeURIComponent(GA_ID);
    document.head.appendChild(s);
    gtag('js', new Date());
    gtag('config', GA_ID, {
      // This site runs no ads, so the advertising half of GA is switched off.
      allow_google_signals: false,
      allow_ad_personalization_signals: false
    });
  }

  // GA's Enhanced Measurement already records page views, form starts and
  // outbound clicks, so only what it can't see goes to GA. A contact form
  // submission is GA4's recommended generate_lead event; mark it as a key event.
  var GA_NAMES = { cta_click: 'cta_click', form_submit: 'generate_lead' };

  /* Per tab, not per person: it resets when the tab closes. */
  function session() {
    try {
      var id = sessionStorage.getItem(SESSION_KEY);
      if (!id) {
        id = window.crypto && crypto.randomUUID ? crypto.randomUUID() : Date.now().toString(36) + Math.random().toString(36).slice(2);
        sessionStorage.setItem(SESSION_KEY, id);
      }
      return id;
    } catch (e) { return null; }
  }

  function flush() {
    clearTimeout(timer);
    timer = null;
    while (queue.length) {
      var body = JSON.stringify({ session: session(), events: queue.splice(0, 25) });
      var sent = false;
      try { sent = navigator.sendBeacon(ENDPOINT, new Blob([body], { type: 'application/json' })); } catch (e) { /* fall through */ }
      if (!sent) {
        try {
          fetch(ENDPOINT, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: body, keepalive: true, credentials: 'same-origin' })
            .catch(function () { /* analytics never breaks the page */ });
        } catch (e) { /* ditto */ }
      }
    }
  }

  function track(type, ctx) {
    if (gaOn && GA_NAMES[type]) gtag('event', GA_NAMES[type], ctx || {});
    if (location.protocol === 'file:') return;
    queue.push({ t: type, path: location.pathname, at: new Date().toISOString(), ctx: ctx || null });
    if (!timer) timer = setTimeout(flush, 4000);
  }

  function pageContext() {
    var ctx = {};
    try {
      var ref = document.referrer ? new URL(document.referrer).hostname : '';
      if (ref && ref !== location.hostname) ctx.ref = ref;
    } catch (e) { /* unparseable referrer */ }
    var params = new URLSearchParams(location.search);
    ['utm_source', 'utm_medium', 'utm_campaign', 'utm_term', 'utm_content'].forEach(function (k) {
      var v = params.get(k);
      if (v) ctx[k] = v.slice(0, 100);
    });
    var w = window.innerWidth;
    ctx.vw = w < 640 ? 'phone' : w < 1024 ? 'tablet' : 'desktop';
    return ctx;
  }

  /* Which part of the page a click came from, for "did the hero or the footer send them?". */
  function placementOf(el) {
    if (el.closest('nav')) return 'nav';
    if (el.closest('footer')) return 'footer';
    var section = el.closest('section[id]');
    if (section) return section.id;
    return el.closest('section.home-hero, section.nx-hero') ? 'hero' : 'page';
  }

  function labelOf(el) {
    return (el.getAttribute('data-track') || el.getAttribute('aria-label') || el.textContent || '')
      .replace(/[↗↘→↓]/g, '').replace(/\s+/g, ' ').trim().slice(0, 80);
  }

  document.addEventListener('click', function (event) {
    var el = event.target.closest && event.target.closest('a, button');
    if (!el || el.type === 'submit') return;
    var href = el.getAttribute('href') || '';
    var where = placementOf(el);

    if (/^(mailto|tel):/i.test(href)) {
      track('outbound_click', { kind: href.split(':')[0].toLowerCase(), label: labelOf(el), where: where });
      flush();
    } else if (el.tagName === 'A' && el.hostname && el.hostname !== location.hostname) {
      track('outbound_click', { host: el.hostname, label: labelOf(el), where: where });
      flush();
    } else if (el.matches('.button, .nav-contact, .text-link, [data-track]')) {
      track('cta_click', { label: labelOf(el), href: href.slice(0, 200), where: where });
    }
  }, true);

  document.addEventListener('visibilitychange', function () {
    if (document.visibilityState === 'hidden') flush();
  });
  window.addEventListener('pagehide', flush);

  track('page_view', pageContext());

  return { track: track };
})();
