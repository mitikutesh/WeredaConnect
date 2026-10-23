import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/config/environment.dart';

/// FUP-22: CI and `scripts/deploy.sh` pass `ENVIRONMENT=dev|staging|prod`,
/// but `Environment.current` used to match only the enum names, so a `prod`
/// build ran with the development settings. Every alias now maps explicitly,
/// and an unknown value never falls back to development.
void main() {
  group('Environment.environmentFor', () {
    const cases = <(String, AppEnvironment)>[
      // (ENVIRONMENT define, environment)
      ('', AppEnvironment.development), // no define: a local run
      ('dev', AppEnvironment.development), // CI and deploy.sh
      ('development', AppEnvironment.development),
      ('staging', AppEnvironment.staging),
      ('prod', AppEnvironment.production), // CI and deploy.sh
      ('production', AppEnvironment.production),
      ('PROD', AppEnvironment.production),
      (' Prod ', AppEnvironment.production),
      ('Staging', AppEnvironment.staging),
      ('DEV', AppEnvironment.development),
    ];

    for (final (raw, expected) in cases) {
      test('"$raw" -> ${expected.name}', () {
        expect(Environment.environmentFor(raw), expected);
        // Known values never depend on the build mode.
        expect(Environment.environmentFor(raw, strict: false), expected);
      });
    }

    const unknown = ['canary', 'devv', 'stage', 'test', 'prd', 'live'];

    // The default `strict` is kDebugMode, and flutter test is a debug build.
    test('an unknown value fails loudly in a debug build', () {
      for (final raw in unknown) {
        expect(
          () => Environment.environmentFor(raw),
          throwsArgumentError,
          reason: raw,
        );
      }
    });

    test('an unknown value gets the production settings in a release build',
        () {
      for (final raw in unknown) {
        expect(
          Environment.environmentFor(raw, strict: false),
          AppEnvironment.production,
          reason: raw,
        );
      }
    });

    test('no define (flutter test, a local run) is development', () {
      expect(Environment.current, AppEnvironment.development);
      expect(Environment.config, same(Environment.development));
    });
  });

  group('a prod build uses the production config', () {
    test('the production config is the restrictive one', () {
      const prod = Environment.production;
      expect(prod.isProduction, isTrue);
      expect(prod.isDevelopment, isFalse);
      expect(prod.enableLogging, isFalse);
      expect(prod.connectTimeout, const Duration(seconds: 15));
      expect(prod.receiveTimeout, const Duration(seconds: 15));
      expect(
        prod.connectTimeout,
        lessThan(Environment.development.connectTimeout),
      );
    });
  });

  group('Environment.telebirrLiveFor follows environmentFor', () {
    test('live exactly when the environment is production', () {
      for (final raw in ['', 'dev', 'development', 'staging', 'prod', 'PROD']) {
        expect(
          Environment.telebirrLiveFor(raw, define: false),
          Environment.environmentFor(raw) == AppEnvironment.production,
          reason: raw,
        );
      }
    });

    test('an unknown value is live in a release build, like production', () {
      expect(
        Environment.telebirrLiveFor('canary', define: false, strict: false),
        isTrue,
      );
    });

    test('an unknown value fails loudly in a debug build', () {
      expect(
        () => Environment.telebirrLiveFor('canary', define: false),
        throwsArgumentError,
      );
    });

    test('TELEBIRR_LIVE=true is live whatever the environment', () {
      expect(
        Environment.telebirrLiveFor('canary', define: true),
        isTrue,
      );
    });
  });
}
