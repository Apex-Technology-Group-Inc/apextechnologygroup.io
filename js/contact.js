/* Contact forms: every form[data-contact] posts to /api/contact and shows the
   result in place. If the API can't be reached (GitHub Pages, an outage) the
   page's mailto composer takes over, so a request is never lost. Pages register
   their own composer in ApexContact.composers[source]; without one, every field
   is listed. Without JavaScript the form falls back to a plain mailto submission. */
window.ApexContact = (function () {
  var ENDPOINT = '/api/contact';
  var TO = 'support@apextechnologygroup.io';
  var loadedAt = Date.now();
  var composers = {};

  function track(type, ctx) {
    if (window.ApexAnalytics) window.ApexAnalytics.track(type, ctx);
  }

  function defaultCompose(data) {
    var interests = data.getAll('Interest');
    var org = data.get('Organization') || '';
    var lines = [
      'Name: ' + data.get('Name'),
      'Email: ' + data.get('Email'),
      'Phone: ' + (data.get('Phone') || '-'),
      'Organization: ' + (org || '-'),
      'Interested in: ' + (interests.length ? interests.join(', ') : '-'),
      '',
      data.get('Message') || '-',
      '',
      '(Sent from the apextechnologygroup.io homepage)'
    ];
    var subject = 'Website inquiry' + (org ? ' (' + org + ')' : '');
    return 'mailto:' + TO + '?subject=' + encodeURIComponent(subject) + '&body=' + encodeURIComponent(lines.join('\n'));
  }

  function wire(form) {
    var source = form.getAttribute('data-contact');
    var status = form.querySelector('.form-status');
    var button = form.querySelector('button[type="submit"]');
    var started = false;

    function show(text, kind) {
      status.textContent = text;
      status.className = 'form-status' + (kind ? ' ' + kind : '');
      status.hidden = false;
    }

    function fallback() {
      button.disabled = false;
      show("We couldn't reach our server, so we've opened a pre-filled email instead. Just press send.", 'warn');
      window.location.href = (composers[source] || defaultCompose)(new FormData(form));
    }

    form.addEventListener('focusin', function () {
      if (started) return;
      started = true;
      track('form_start', { form: source });
    });

    form.addEventListener('submit', function (event) {
      event.preventDefault();
      if (!form.reportValidity()) return;

      var data = new FormData(form);
      var payload = {
        source: source,
        name: data.get('Name') || '',
        email: data.get('Email') || '',
        phone: data.get('Phone') || null,
        organization: data.get('Organization') || data.get('Business') || null,
        role: data.get('Role') || null,
        parish: data.get('Parish') || null,
        timeframe: data.get('Timeframe') || null,
        interests: data.getAll('Interest'),
        message: data.get('Message') || null,
        hp: data.get('company_website') || '',
        elapsed_ms: Date.now() - loadedAt
      };

      button.disabled = true;
      show('Sending…');

      fetch(ENDPOINT, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
        credentials: 'same-origin'
      }).then(function (res) {
        if (res.status === 202) {
          form.classList.add('is-sent');
          show('Thanks, ' + payload.name.trim() + ". Your message is in our inbox, and we'll reply to " + payload.email.trim() + '.', 'ok');
          track('form_submit', { form: source });
          return;
        }
        if (res.status === 400) {
          return res.json().catch(function () { return {}; }).then(function (body) {
            button.disabled = false;
            show(body.error || 'Please check the form and try again.', 'warn');
          });
        }
        if (res.status === 429) {
          button.disabled = false;
          show('Too many requests from this connection. Please email us directly at ' + TO + '.', 'warn');
          return;
        }
        fallback();
      }).catch(fallback);
    });
  }

  Array.prototype.forEach.call(document.querySelectorAll('form[data-contact]'), wire);

  return { composers: composers };
})();
