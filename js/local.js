/* Intro call request: sent by js/contact.js. This composes the pre-filled email
   used when our server can't be reached. Without JavaScript the form falls back
   to a plain mailto submission. */
(function () {
  if (!window.ApexContact) return;
  var TO = 'support@apextechnologygroup.io';

  window.ApexContact.composers['new-orleans'] = function (data) {
    var interests = data.getAll('Interest');
    var business = data.get('Business') || '';
    var lines = [
      'Name: ' + data.get('Name'),
      'Email: ' + data.get('Email'),
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
    return 'mailto:' + TO +
      '?subject=' + encodeURIComponent(subject) +
      '&body=' + encodeURIComponent(lines.join('\n'));
  };
})();
