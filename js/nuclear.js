/* Consultation request: composes a pre-filled email. Without JavaScript the
   form falls back to a plain mailto submission. */
(function () {
  var form = document.getElementById('consult-form');
  if (!form) return;
  var TO = 'support@apextechnologygroup.io';

  form.addEventListener('submit', function (event) {
    event.preventDefault();
    if (!form.reportValidity()) return;

    var data = new FormData(form);
    var interests = data.getAll('Interest');
    var org = data.get('Organization') || '';
    var lines = [
      'Name: ' + data.get('Name'),
      'Role: ' + (data.get('Role') || '-'),
      'Organization / site: ' + org,
      'Areas of interest: ' + (interests.length ? interests.join(', ') : '-'),
      'Timeframe: ' + data.get('Timeframe'),
      '',
      'What we are trying to solve:',
      data.get('Message') || '-',
      '',
      '(Sent from the Nuclear Training Technology & Simulator Engineering Services page)'
    ];
    var subject = 'Nuclear training services: consultation request' + (org ? ' (' + org + ')' : '');
    window.location.href = 'mailto:' + TO +
      '?subject=' + encodeURIComponent(subject) +
      '&body=' + encodeURIComponent(lines.join('\n'));
  });
})();
