import 'dart:js_interop';

import 'package:web/web.dart' as web;

/// Web: a plain reload revalidates the page and `flutter_bootstrap.js`
/// (served with `max-age=0`), but `main.dart.js` is served `immutable` for a
/// year (`deploy-webapp.yml`) and its name never changes, so the browser
/// would run its cached copy again. Fetching it with `cache: 'reload'` skips
/// the browser cache and stores the fresh copy, which the reload then runs.
/// The path is relative to the page's base href, as the Flutter loader reads
/// it.
Future<bool> reloadLatestWebAppImpl() async {
  try {
    await web.window
        .fetch('main.dart.js'.toJS, web.RequestInit(cache: 'reload'))
        .toDart;
  } on Object {
    // Offline or refused: the reload still revalidates what it can.
  }
  web.window.location.reload();
  return true;
}
