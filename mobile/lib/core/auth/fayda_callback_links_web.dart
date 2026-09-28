import 'dart:async';
import 'dart:js_interop';

import 'package:web/web.dart' as web;
import 'package:wereda_connect/core/auth/fayda_callback_links.dart';

/// Web: listen on the same-origin channel `web/fayda-callback.js` posts to.
/// The caller still checks the state, so a stray message cannot complete a flow.
Stream<Uri> faydaCallbackLinksImpl() {
  web.BroadcastChannel? channel;
  late final StreamController<Uri> controller;
  controller = StreamController<Uri>(
    onListen: () {
      channel = web.BroadcastChannel(faydaCallbackChannelName)
        ..onmessage = ((web.MessageEvent event) {
          final data = event.data;
          if (!data.isA<JSString>()) return;
          final uri = Uri.tryParse((data as JSString).toDart);
          if (uri != null) controller.add(uri);
        }).toJS;
    },
    onCancel: () {
      channel?.close();
      channel = null;
      return controller.close();
    },
  );
  return controller.stream;
}

String? webFaydaRedirectUriImpl() => '${Uri.base.origin}/fayda-callback.html';
