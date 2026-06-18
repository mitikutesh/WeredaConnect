import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/auth/auth_service.dart';
import 'package:wereda_connect/core/models/user.dart';

/// F-34: the authoritative role comes from the server login response.
/// `AuthService.loginOnline` must resolve it case-insensitively and FAIL CLOSED
/// (never silently default to farmer) when the role is unrecognized or missing.
///
/// The online login path itself is gated behind `Environment.isApiConfigured`
/// (compile-time `--dart-define`, empty under plain `flutter test`), so the
/// fail-closed parse logic is unit-tested directly via the extracted
/// `resolveServerRole`.
void main() {
  group('resolveServerRole', () {
    test('resolves each of the three known roles', () {
      expect(resolveServerRole('farmer'), UserRole.farmer);
      expect(resolveServerRole('trucker'), UserRole.trucker);
      expect(resolveServerRole('buyer'), UserRole.buyer);
    });

    test('matches case-insensitively', () {
      expect(resolveServerRole('BUYER'), UserRole.buyer);
      expect(resolveServerRole('Trucker'), UserRole.trucker);
      expect(resolveServerRole('FaRmEr'), UserRole.farmer);
    });

    test('returns null for an unrecognized role (fail-closed, never farmer)',
        () {
      expect(resolveServerRole('wizard'), isNull);
      // "admin" is not a mobile app role — it must not resolve to anything.
      expect(resolveServerRole('admin'), isNull);
    });

    test('returns null for a missing, empty, or blank role', () {
      expect(resolveServerRole(null), isNull);
      expect(resolveServerRole(''), isNull);
      expect(resolveServerRole('   '), isNull);
    });
  });
}
