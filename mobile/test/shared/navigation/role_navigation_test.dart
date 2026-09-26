import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/user.dart';
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
          '/',
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
  });

  group('tabsForRole — role-shaped bars (design brief 2026-07-06)', () {
    test('farmer: Home · My Crops · Orders · Deliveries', () {
      expect(tabsForRole(UserRole.farmer), const [
        RoleTab.home,
        RoleTab.myCrops,
        RoleTab.orders,
        RoleTab.deliveries,
      ]);
    });

    test('trucker: Home · My Trips · Deliveries · Payments', () {
      expect(tabsForRole(UserRole.trucker), const [
        RoleTab.home,
        RoleTab.myTrips,
        RoleTab.deliveries,
        RoleTab.payments,
      ]);
    });

    test('buyer: Home · Market · Orders · Deliveries', () {
      expect(tabsForRole(UserRole.buyer), const [
        RoleTab.home,
        RoleTab.market,
        RoleTab.orders,
        RoleTab.deliveries,
      ]);
    });

    test('every role has exactly four bar slots (FAB takes the fifth)', () {
      for (final role in UserRole.values) {
        expect(tabsForRole(role).length, 4, reason: role.name);
      }
    });

    test('workspace tab sits at index 1 for every role', () {
      expect(tabsForRole(UserRole.farmer)[1], RoleTab.myCrops);
      expect(tabsForRole(UserRole.trucker)[1], RoleTab.myTrips);
      expect(tabsForRole(UserRole.buyer)[1], RoleTab.market);
    });
  });

  group('roleTabIndex', () {
    test('resolves in-bar destinations to their index', () {
      expect(roleTabIndex(UserRole.farmer, RoleTab.orders), 2);
      expect(roleTabIndex(UserRole.trucker, RoleTab.payments), 3);
      expect(roleTabIndex(UserRole.buyer, RoleTab.deliveries), 3);
    });

    test('returns null for off-bar destinations (callers must push)', () {
      expect(roleTabIndex(UserRole.farmer, RoleTab.payments), isNull);
      expect(roleTabIndex(UserRole.trucker, RoleTab.orders), isNull);
      expect(roleTabIndex(UserRole.buyer, RoleTab.payments), isNull);
    });
  });

  group('sidebarExtrasForRole', () {
    test('web sidebar restores exactly the off-bar destinations', () {
      for (final role in UserRole.values) {
        final bar = tabsForRole(role);
        for (final extra in sidebarExtrasForRole(role)) {
          expect(bar.contains(extra), isFalse, reason: '${role.name} $extra');
          expect(roleTabIndex(role, extra), isNull);
        }
      }
    });
  });
}
