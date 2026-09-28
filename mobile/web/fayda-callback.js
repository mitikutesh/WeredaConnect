// Fayda (mock eSignet) redirects the web app here in a new tab. Relay the
// callback URL to the tab that started verification (same-origin only), drop
// the one-time code from this tab's address bar and history, then close.
(function () {
  var status = document.getElementById('status');
  // Nothing to relay (e.g. a reload after the query was stripped below): a
  // stateless message would only disturb a verification still in progress.
  if (!window.location.search) {
    if (status) {
      status.textContent = 'You can close this tab and return to WeredaConnect.';
    }
    return;
  }
  var url = window.location.href;
  try {
    window.history.replaceState(null, '', window.location.pathname);
  } catch (e) {}
  var relayed = false;
  try {
    var channel = new BroadcastChannel('wereda-fayda-callback');
    channel.postMessage(url);
    channel.close();
    relayed = true;
  } catch (e) {}
  if (status) {
    status.textContent = relayed
      ? 'Fayda verification finished. You can close this tab and return to WeredaConnect.'
      : 'This browser cannot return to WeredaConnect automatically. Please verify from the mobile app.';
  }
  if (relayed) {
    setTimeout(function () { window.close(); }, 300);
  }
})();
