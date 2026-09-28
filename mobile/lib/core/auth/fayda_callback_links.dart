// Stub on mobile/VM; the BroadcastChannel relay only on web. Keeps dart:js_interop
// out of the mobile build while this facade stays VM-analyzable/testable.
import 'package:wereda_connect/core/auth/fayda_callback_links_stub.dart'
    if (dart.library.html) 'package:wereda_connect/core/auth/fayda_callback_links_web.dart'
    as impl;

/// Name of the BroadcastChannel `web/fayda-callback.js` posts the callback URL on.
const String faydaCallbackChannelName = 'wereda-fayda-callback';

/// Fayda callback URIs as they arrive.
///
/// Mobile: the `weredaconnect://fayda-callback` deep link (app_links).
/// Web: browsers cannot open that scheme, so Fayda redirects to
/// `web/fayda-callback.html` in a new tab, which relays its URL to this tab
/// over a same-origin BroadcastChannel. The server accepts that redirect only
/// while the mock provider is in use.
Stream<Uri> faydaCallbackLinks() => impl.faydaCallbackLinksImpl();

/// The web callback page for this origin, or null off the web.
String? webFaydaRedirectUri() => impl.webFaydaRedirectUriImpl();
