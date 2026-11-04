import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// The web loading splash (audit L-3): on a 3G link the app's code takes
/// most of a minute to arrive and the page was white until Flutter's first
/// frame. The splash must show at once, so it may not fetch anything, and
/// must leave on the first frame.
void main() {
  final html = File('web/index.html').readAsStringSync();

  test('a splash with the three languages, announced politely', () {
    expect(html, contains('<div id="wc-splash" role="status">'));
    expect(html, contains('<p lang="en">'));
    expect(html, contains('<p lang="am">'));
    expect(html, contains('<p lang="om">'));
  });

  test('it leaves on Flutter\'s first frame', () {
    expect(html, contains("addEventListener('flutter-first-frame'"));
    expect(html, contains("getElementById('wc-splash')"));
    expect(html, contains('splash.remove()'));
    // The listener is in place before the app's code can start.
    expect(
      html.indexOf('flutter-first-frame'),
      lessThan(html.indexOf('flutter_bootstrap.js')),
    );
  });

  test('it fetches nothing: no external stylesheet, script, font or image', () {
    final attributes = RegExp(r'''(?:src|href)\s*=\s*["']([^"']+)["']''')
        .allMatches(html)
        .map((m) => m.group(1)!);
    for (final url in attributes) {
      expect(url, isNot(startsWith('http')), reason: url);
      expect(url, isNot(startsWith('//')), reason: url);
    }
    expect(html, isNot(contains('@import')));
    expect(html, isNot(contains('@font-face')));
    expect(html, isNot(contains('url(')));
  });

  test('a viewport tag, so a phone draws it at its own width', () {
    expect(
      html,
      contains('<meta name="viewport" content="width=device-width'),
    );
  });
}
