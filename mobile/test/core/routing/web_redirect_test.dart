import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/routing/app_router.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/shared/navigation/role_navigation.dart';

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

    test('signed in on an auth route goes to the role\'s landing tab', () {
      expect(webRedirect(_signedIn(UserRole.farmer), '/register'), '/');
      expect(webRedirect(_signedIn(UserRole.trucker), '/login'), '/');
      // A buyer has no Home: Deliveries is their first tab.
      expect(webRedirect(_signedIn(UserRole.buyer), '/login'), '/deliveries');
      expect(
        webRedirect(_signedIn(UserRole.buyer), '/forgot-password'),
        '/deliveries',
      );
    });

    test('a buyer at `/` goes to Deliveries; a farmer and a trucker stay', () {
      expect(webRedirect(_signedIn(UserRole.buyer), '/'), '/deliveries');
      expect(webRedirect(_signedIn(UserRole.farmer), '/'), isNull);
      expect(webRedirect(_signedIn(UserRole.trucker), '/'), isNull);
    });

    test('the landing tab never redirects again (no loop)', () {
      for (final role in UserRole.values) {
        final landing = landingRouteForRole(role);
        expect(
          webRedirect(_signedIn(role), landing),
          isNull,
          reason: role.name,
        );
      }
    });

    test(
        'a wrong-role URL goes to the landing tab instead of mounting a 403 '
        'screen', () {
      expect(webRedirect(_signedIn(UserRole.trucker), '/find-crops'), '/');
      expect(webRedirect(_signedIn(UserRole.trucker), '/marketplace'), '/');
      expect(webRedirect(_signedIn(UserRole.trucker), '/my-crops'), '/');
      expect(webRedirect(_signedIn(UserRole.farmer), '/trips'), '/');
      expect(webRedirect(_signedIn(UserRole.farmer), '/find-crops'), '/');
      expect(webRedirect(_signedIn(UserRole.farmer), '/marketplace'), '/');
      expect(
        webRedirect(_signedIn(UserRole.buyer), '/my-crops'),
        '/deliveries',
      );
      expect(
        webRedirect(_signedIn(UserRole.buyer), '/listings'),
        '/deliveries',
      );
    });

    test('own-role and shared routes stay put', () {
      const shared = [
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
      expect(webRedirect(_signedIn(UserRole.buyer), '/find-crops'), isNull);
    });

    // Old bookmarks open the tab itself: the bar marks it and shows its
    // create button (on the old address it marked nothing).
    test('an old tab address goes to the tab', () {
      expect(
        webRedirect(_signedIn(UserRole.farmer), '/listings'),
        '/my-crops',
      );
      expect(
        webRedirect(_signedIn(UserRole.buyer), '/marketplace'),
        '/find-crops',
      );
      // The tabs themselves stay put (no redirect loop).
      expect(webRedirect(_signedIn(UserRole.farmer), '/my-crops'), isNull);
      expect(webRedirect(_signedIn(UserRole.buyer), '/find-crops'), isNull);
    });

    // Hard product rule: a buyer browses ALL trips; with no Home, `/trips`
    // (Find Trips) is how they get there.
    test('a buyer may always open `/trips`', () {
      expect(webRedirect(_signedIn(UserRole.buyer), '/trips'), isNull);
    });

    test('ESC-21: an order page opens for every role, nothing deeper', () {
      for (final role in UserRole.values) {
        final landing = landingRouteForRole(role);
        expect(
          webRedirect(_signedIn(role), '/orders/ord-7f3a'),
          isNull,
          reason: role.name,
        );
        expect(webRedirect(_signedIn(role), '/orders/a/b'), landing);
        expect(webRedirect(_signedIn(role), '/orders/'), landing);
        // An encoded slash or dot segment never reaches an API path.
        expect(
          webRedirect(_signedIn(role), '/orders/..%2F..%2Fescrow%2Fhistory'),
          landing,
        );
        expect(webRedirect(_signedIn(role), '/orders/..'), landing);
      }
      expect(
        webRedirect(const AuthUnauthenticated(), '/orders/ord-7f3a'),
        '/login',
      );
    });
  });
}
