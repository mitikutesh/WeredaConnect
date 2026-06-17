import 'package:flutter_test/flutter_test.dart';
import 'package:wereda_connect/core/auth/web_test_auth_seam.dart';

/// Locks the F-37 production gate: the web test auth seam must be enabled ONLY
/// for known non-production environments and provably disabled for every
/// prod-ish / unknown value — including the `prod` vs `production` dart-define
/// mismatch that would defeat a naive `!isProduction` gate.
void main() {
  group('web test auth seam — production gate (F-37)', () {
    test('enabled for known non-prod environments', () {
      expect(seamEnabledForEnv('dev'), isTrue);
      expect(seamEnabledForEnv('development'), isTrue);
      expect(seamEnabledForEnv('staging'), isTrue);
    });

    test('disabled (fail-closed) for prod-ish and unknown environments', () {
      const denied = [
        'prod', // the literal CI passes for production builds
        'production', // the Dart enum name
        'PROD',
        'Production',
        '', // empty / unset
        'canary',
        'devv', // typo
        'test',
      ];
      for (final env in denied) {
        expect(
          seamEnabledForEnv(env),
          isFalse,
          reason: 'env "$env" must NOT enable the test auth seam',
        );
      }
    });

    test(
        'default test build (ENVIRONMENT unset -> development) enables the seam',
        () {
      // `flutter test` runs with no --dart-define, so _kEnv defaults to
      // 'development' and the seam is available for dev/e2e builds.
      expect(kWebTestAuthSeamEnabled, isTrue);
    });
  });
}
