@TestOn('browser')
library;

import 'dart:js_interop';

import 'package:flutter_test/flutter_test.dart';
import 'package:web/web.dart' as web;
import 'package:wereda_connect/core/auth/fayda_callback_links.dart';

// Browser only: `flutter test --platform chrome test/core/auth/fayda_callback_links_web_test.dart`.
void main() {
  test('the web callback page is on this origin', () {
    expect(webFaydaRedirectUri(), '${Uri.base.origin}/fayda-callback.html');
  });

  test('a URL posted on the relay channel arrives as a callback', () async {
    final received = <Uri>[];
    final sub = faydaCallbackLinks().listen(received.add);
    final sender = web.BroadcastChannel(faydaCallbackChannelName);

    // Non-string messages are ignored; the string one is delivered.
    sender.postMessage(42.toJS);
    sender.postMessage(
      '${Uri.base.origin}/fayda-callback.html?code=c-1&state=st-1'.toJS,
    );
    await Future<void>.delayed(const Duration(milliseconds: 200));

    expect(received, [
      Uri.parse('${Uri.base.origin}/fayda-callback.html?code=c-1&state=st-1'),
    ]);
    sender.close();
    await sub.cancel();
  });

  test('nothing arrives after the listener cancels', () async {
    final received = <Uri>[];
    final sub = faydaCallbackLinks().listen(received.add);
    await sub.cancel();

    final sender = web.BroadcastChannel(faydaCallbackChannelName)
      ..postMessage('${Uri.base.origin}/fayda-callback.html?state=s'.toJS);
    await Future<void>.delayed(const Duration(milliseconds: 200));

    expect(received, isEmpty);
    sender.close();
  });
}
