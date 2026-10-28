/* Consultation request: sent by js/contact.js. This composes the pre-filled
   email used when our server can't be reached. Without JavaScript the form
   falls back to a plain mailto submission. */
(function () {
  if (!window.ApexContact) return;
  var TO = 'support@apextechnologygroup.io';

  window.ApexContact.composers['nuclear'] = function (data) {
    var interests = data.getAll('Interest');
    var org = data.get('Organization') || '';
    var lines = [
      'Name: ' + data.get('Name'),
      'Email: ' + data.get('Email'),
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
    return 'mailto:' + TO +
      '?subject=' + encodeURIComponent(subject) +
      '&body=' + encodeURIComponent(lines.join('\n'));
  };
})();
