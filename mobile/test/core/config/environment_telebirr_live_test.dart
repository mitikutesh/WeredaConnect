import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/config/environment.dart';

/// ESC-44: which builds show the live Telebirr payment. CI passes
/// `ENVIRONMENT=prod`, which `Environment.current` does not recognise (it
/// falls back to development), so the raw define is read.
void main() {
  group('Environment.telebirrLiveFor', () {
    const cases = <(String, bool, bool)>[
      // (ENVIRONMENT, TELEBIRR_LIVE define, live?)
      ('prod', false, true),
      ('prod', true, true),
      ('production', false, true),
      ('PROD', false, true),
      (' production ', false, true),
      ('staging', false, false),
      ('staging', true, true),
      ('dev', false, false),
      ('dev', true, true),
      ('development', false, false),
      ('', false, false),
      ('', true, true),
    ];

    for (final (env, define, live) in cases) {
      test('ENVIRONMENT="$env", TELEBIRR_LIVE=$define -> live: $live', () {
        expect(Environment.telebirrLiveFor(env, define: define), live);
      });
    }
  });
}
