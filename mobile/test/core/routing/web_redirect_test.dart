import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/routing/app_router.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';

const _tokens = AuthTokens(
  accessToken: 'access',
  refreshToken: 'refresh',
  idToken: 'id',
);

AuthState _signedIn(UserRole role) =>
    AuthAuthenticated(userId: 'user-1', tokens: _tokens, role: role);

void main() {
  group('webRedirect (go_router redirect, SIM-21 role guard)', () {
    test('signed out: everything but the auth routes goes to /login', () {
      const signedOut = AuthUnauthenticated();
      expect(webRedirect(signedOut, '/'), '/login');
      expect(webRedirect(signedOut, '/orders'), '/login');
      expect(webRedirect(signedOut, '/login'), isNull);
      expect(webRedirect(signedOut, '/register'), isNull);
      expect(webRedirect(signedOut, '/forgot-password'), isNull);
    });

    test('signed in on an auth route goes home', () {
      expect(webRedirect(_signedIn(UserRole.buyer), '/login'), '/');
      expect(webRedirect(_signedIn(UserRole.farmer), '/register'), '/');
    });

    test('a wrong-role URL goes home instead of mounting a 403 screen', () {
      expect(webRedirect(_signedIn(UserRole.trucker), '/find-crops'), '/');
      expect(webRedirect(_signedIn(UserRole.trucker), '/my-crops'), '/');
      expect(webRedirect(_signedIn(UserRole.farmer), '/trips'), '/');
      expect(webRedirect(_signedIn(UserRole.farmer), '/find-crops'), '/');
      expect(webRedirect(_signedIn(UserRole.buyer), '/my-crops'), '/');
      expect(webRedirect(_signedIn(UserRole.buyer), '/listings'), '/');
    });

    test('own-role and shared routes stay put', () {
      const shared = [
        '/',
        '/orders',
        '/deliveries',
        '/payments',
        '/messages',
        '/profile',
        '/settings',
      ];
      for (final role in UserRole.values) {
        for (final route in shared) {
          expect(
            webRedirect(_signedIn(role), route),
            isNull,
            reason: '${role.name} $route',
          );
        }
      }
      expect(webRedirect(_signedIn(UserRole.trucker), '/trips'), isNull);
      expect(webRedirect(_signedIn(UserRole.farmer), '/my-crops'), isNull);
      expect(webRedirect(_signedIn(UserRole.farmer), '/listings'), isNull);
      expect(webRedirect(_signedIn(UserRole.buyer), '/find-crops'), isNull);
      expect(webRedirect(_signedIn(UserRole.buyer), '/marketplace'), isNull);
      expect(webRedirect(_signedIn(UserRole.buyer), '/trips'), isNull);
    });
  });
}
