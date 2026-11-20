import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/app_localizations_en.dart';
import 'package:wereda_connect/shared/navigation/role_navigation.dart';

void main() {
  group('webRoutesForRole (SIM-21 web role guard)', () {
    test('farmer: own crops, orders, deliveries, payments, messages only', () {
      final routes = webRoutesForRole(UserRole.farmer);
      expect(
        routes,
        containsAll([
          '/',
          '/my-crops',
          '/listings',
          '/orders',
          '/deliveries',
          '/payments',
          '/messages',
          '/profile',
          '/settings',
        ]),
      );
      expect(routes, isNot(contains('/find-crops')));
      expect(routes, isNot(contains('/marketplace')));
      expect(routes, isNot(contains('/trips')));
    });

    test('trucker: trips, deliveries, payments, orders, messages only', () {
      final routes = webRoutesForRole(UserRole.trucker);
      expect(
        routes,
        containsAll([
          '/',
          '/trips',
          '/deliveries',
          '/payments',
          '/orders',
          '/messages',
          '/profile',
          '/settings',
        ]),
      );
      expect(routes, isNot(contains('/my-crops')));
      expect(routes, isNot(contains('/listings')));
      expect(routes, isNot(contains('/find-crops')));
      expect(routes, isNot(contains('/marketplace')));
    });

    test('buyer: market and ALL trips, never a farmer own-crops view', () {
      final routes = webRoutesForRole(UserRole.buyer);
      expect(
        routes,
        containsAll([
          '/find-crops',
          '/marketplace',
          '/trips',
          '/orders',
          '/deliveries',
          '/payments',
          '/messages',
          '/profile',
          '/settings',
        ]),
      );
      expect(routes, isNot(contains('/my-crops')));
      expect(routes, isNot(contains('/listings')));
    });

    // A buyer has no Home: `/` is not theirs, and the redirect sends it on
    // to Deliveries.
    test('`/` is a route only for a role with a Home', () {
      expect(webRoutesForRole(UserRole.farmer), contains('/'));
      expect(webRoutesForRole(UserRole.trucker), contains('/'));
      expect(webRoutesForRole(UserRole.buyer), isNot(contains('/')));
    });

    test('every tab and every extra is a route its role may open', () {
      for (final role in UserRole.values) {
        final routes = webRoutesForRole(role);
        for (final tab in [
          ...tabsForRole(role),
          ...sidebarExtrasForRole(role),
        ]) {
          expect(routes, contains(tab.route), reason: '${role.name} $tab');
        }
      }
    });
  });

  group('tabsForRole: three tabs, then the create button (canvas page M)', () {
    test('farmer: Home, My Crops, Orders', () {
      expect(tabsForRole(UserRole.farmer), const [
        RoleTab.home,
        RoleTab.myCrops,
        RoleTab.orders,
      ]);
    });

    test('trucker: Home, My Trips, Deliveries', () {
      expect(tabsForRole(UserRole.trucker), const [
        RoleTab.home,
        RoleTab.myTrips,
        RoleTab.deliveries,
      ]);
    });

    test('buyer: Deliveries, Market, Orders (no Home)', () {
      expect(tabsForRole(UserRole.buyer), const [
        RoleTab.deliveries,
        RoleTab.market,
        RoleTab.orders,
      ]);
      expect(tabsForRole(UserRole.buyer), isNot(contains(RoleTab.home)));
    });

    test('every role has exactly three bar tabs, none of them twice', () {
      for (final role in UserRole.values) {
        final tabs = tabsForRole(role);
        expect(tabs.length, 3, reason: role.name);
        expect(tabs.toSet().length, 3, reason: role.name);
      }
    });

    test('workspace tab sits at index 1 for every role', () {
      expect(tabsForRole(UserRole.farmer)[1], RoleTab.myCrops);
      expect(tabsForRole(UserRole.trucker)[1], RoleTab.myTrips);
      expect(tabsForRole(UserRole.buyer)[1], RoleTab.market);
    });

    // Role visibility: a bar never offers another role's workspace.
    test('no role gets another role\'s workspace tab', () {
      expect(tabsForRole(UserRole.farmer), isNot(contains(RoleTab.myTrips)));
      expect(tabsForRole(UserRole.farmer), isNot(contains(RoleTab.market)));
      expect(tabsForRole(UserRole.trucker), isNot(contains(RoleTab.myCrops)));
      expect(tabsForRole(UserRole.trucker), isNot(contains(RoleTab.market)));
      expect(tabsForRole(UserRole.buyer), isNot(contains(RoleTab.myCrops)));
      expect(tabsForRole(UserRole.buyer), isNot(contains(RoleTab.myTrips)));
    });
  });

  group('landingRouteForRole', () {
    test('the first tab: Home for a farmer and a trucker', () {
      expect(landingRouteForRole(UserRole.farmer), '/');
      expect(landingRouteForRole(UserRole.trucker), '/');
    });

    test('a buyer lands on Deliveries', () {
      expect(landingRouteForRole(UserRole.buyer), '/deliveries');
    });

    test('is always a route the role may open', () {
      for (final role in UserRole.values) {
        expect(
          webRoutesForRole(role),
          contains(landingRouteForRole(role)),
          reason: role.name,
        );
      }
    });
  });

  group('roleTabIndex', () {
    test('resolves in-bar destinations to their index', () {
      expect(roleTabIndex(UserRole.farmer, RoleTab.orders), 2);
      expect(roleTabIndex(UserRole.trucker, RoleTab.deliveries), 2);
      expect(roleTabIndex(UserRole.buyer, RoleTab.deliveries), 0);
      expect(roleTabIndex(UserRole.buyer, RoleTab.orders), 2);
    });

    test('returns null for off-bar destinations (callers must push)', () {
      expect(roleTabIndex(UserRole.farmer, RoleTab.deliveries), isNull);
      expect(roleTabIndex(UserRole.farmer, RoleTab.payments), isNull);
      expect(roleTabIndex(UserRole.trucker, RoleTab.payments), isNull);
      expect(roleTabIndex(UserRole.trucker, RoleTab.orders), isNull);
      expect(roleTabIndex(UserRole.buyer, RoleTab.home), isNull);
      expect(roleTabIndex(UserRole.buyer, RoleTab.findTrips), isNull);
      expect(roleTabIndex(UserRole.buyer, RoleTab.payments), isNull);
    });
  });

  group('sidebarExtrasForRole', () {
    test('farmer: Deliveries, Messages, Payments', () {
      expect(sidebarExtrasForRole(UserRole.farmer), const [
        RoleTab.deliveries,
        RoleTab.messages,
        RoleTab.payments,
      ]);
    });

    test('trucker: Payments, Messages, Orders', () {
      expect(sidebarExtrasForRole(UserRole.trucker), const [
        RoleTab.payments,
        RoleTab.messages,
        RoleTab.orders,
      ]);
    });

    // Hard product rule: a buyer can always browse ALL trips. With no
    // Home to link to them, Find Trips is the buyer's first extra.
    test('buyer: Find Trips (`/trips`), Messages, Payments', () {
      expect(sidebarExtrasForRole(UserRole.buyer), const [
        RoleTab.findTrips,
        RoleTab.messages,
        RoleTab.payments,
      ]);
      expect(RoleTab.findTrips.route, '/trips');
      final AppLocalizations en = AppLocalizationsEn();
      expect(RoleTab.findTrips.label(en), en.findTrips);
      expect(webRoutesForRole(UserRole.buyer), contains('/trips'));
    });

    test('only a buyer gets Find Trips', () {
      for (final role in [UserRole.farmer, UserRole.trucker]) {
        expect(
          [...tabsForRole(role), ...sidebarExtrasForRole(role)],
          isNot(contains(RoleTab.findTrips)),
          reason: role.name,
        );
      }
    });

    test('web sidebar restores exactly the off-bar destinations', () {
      for (final role in UserRole.values) {
        final bar = tabsForRole(role);
        for (final extra in sidebarExtrasForRole(role)) {
          expect(bar.contains(extra), isFalse, reason: '${role.name} $extra');
          expect(roleTabIndex(role, extra), isNull);
        }
      }
    });

    // What left the bar in the redesign must stay one tap away.
    test('Deliveries, Orders, Payments and Messages stay reachable', () {
      for (final role in UserRole.values) {
        expect(
          [...tabsForRole(role), ...sidebarExtrasForRole(role)],
          containsAll([
            RoleTab.deliveries,
            RoleTab.orders,
            RoleTab.payments,
            RoleTab.messages,
          ]),
          reason: role.name,
        );
      }
    });
  });
}
