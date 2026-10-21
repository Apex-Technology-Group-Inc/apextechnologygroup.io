/* Intro call request: composes a pre-filled email. Without JavaScript the
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
    var business = data.get('Business') || '';
    var lines = [
      'Name: ' + data.get('Name'),
      'Phone: ' + (data.get('Phone') || '-'),
      'Business: ' + business,
      'Parish: ' + data.get('Parish'),
      'Interested in: ' + (interests.length ? interests.join(', ') : '-'),
      '',
      "What's slowing us down:",
      data.get('Message') || '-',
      '',
      '(Sent from the New Orleans business services page)'
    ];
    var subject = 'Business technology: intro call request' + (business ? ' (' + business + ')' : '');
    window.location.href = 'mailto:' + TO +
      '?subject=' + encodeURIComponent(subject) +
      '&body=' + encodeURIComponent(lines.join('\n'));
  });
})();
