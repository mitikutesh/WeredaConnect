import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Role-shaped navigation (design brief 2026-07-06).
///
/// Five slots per role — Home and the role's fulfilment view anchor the
/// ends, the middle is the role's actual job, and the center FAB is the
/// role's single create verb:
///
///   Farmer:  Home · My Crops · [Sell Crop]   · Orders     · Deliveries
///   Trucker: Home · My Trips · [New Trip]    · Deliveries · Payments
///   Buyer:   Home · Market   · [New Request] · Orders     · Deliveries
///
/// This file is the single source of truth for which destination sits at
/// which index — the mobile shell, the web shell, and every "jump to tab"
/// call site must resolve through it so they can never disagree.
enum RoleTab {
  home,
  myCrops,
  myTrips,
  market,
  orders,
  deliveries,
  payments,
  messages,
}

extension RoleTabVisual on RoleTab {
  IconData get icon => switch (this) {
        RoleTab.home => Icons.home_outlined,
        RoleTab.myCrops => Icons.eco_outlined,
        RoleTab.myTrips => Icons.route_outlined,
        RoleTab.market => Icons.storefront_outlined,
        RoleTab.orders => Icons.receipt_long_outlined,
        RoleTab.deliveries => Icons.local_shipping_outlined,
        RoleTab.payments => Icons.account_balance_wallet_outlined,
        RoleTab.messages => Icons.chat_bubble_outline,
      };

  IconData get activeIcon => switch (this) {
        RoleTab.home => Icons.home,
        RoleTab.myCrops => Icons.eco,
        RoleTab.myTrips => Icons.route,
        RoleTab.market => Icons.storefront,
        RoleTab.orders => Icons.receipt_long,
        RoleTab.deliveries => Icons.local_shipping,
        RoleTab.payments => Icons.account_balance_wallet,
        RoleTab.messages => Icons.chat_bubble,
      };

  String label(AppLocalizations l10n) => switch (this) {
        RoleTab.home => l10n.home,
        RoleTab.myCrops => l10n.myCrops,
        RoleTab.myTrips => l10n.myTrips,
        RoleTab.market => l10n.market,
        RoleTab.orders => l10n.orders,
        RoleTab.deliveries => l10n.deliveries,
        RoleTab.payments => l10n.payments,
        RoleTab.messages => l10n.messages,
      };

  /// Web route for this destination (go_router).
  String get route => switch (this) {
        RoleTab.home => '/',
        RoleTab.myCrops => '/my-crops',
        RoleTab.myTrips => '/trips',
        RoleTab.market => '/find-crops',
        RoleTab.orders => '/orders',
        RoleTab.deliveries => '/deliveries',
        RoleTab.payments => '/payments',
        RoleTab.messages => '/messages',
      };
}

/// The four bar destinations for [role] (the FAB floats centred above them).
List<RoleTab> tabsForRole(UserRole role) {
  return switch (role) {
    UserRole.farmer => const [
        RoleTab.home,
        RoleTab.myCrops,
        RoleTab.orders,
        RoleTab.deliveries,
      ],
    UserRole.trucker => const [
        RoleTab.home,
        RoleTab.myTrips,
        RoleTab.deliveries,
        RoleTab.payments,
      ],
    UserRole.buyer => const [
        RoleTab.home,
        RoleTab.market,
        RoleTab.orders,
        RoleTab.deliveries,
      ],
  };
}

/// Destinations that didn't earn a bar slot for [role] but stay reachable
/// from the web sidebar (desktop/tablet has room) and, without a sidebar,
/// from the header's account button (account_menu.dart).
List<RoleTab> sidebarExtrasForRole(UserRole role) {
  return switch (role) {
    UserRole.farmer => const [RoleTab.messages, RoleTab.payments],
    UserRole.trucker => const [RoleTab.messages, RoleTab.orders],
    UserRole.buyer => const [RoleTab.messages, RoleTab.payments],
  };
}

/// Web routes (go_router paths) [role] may open. A URL outside this set is
/// redirected to `/` before any screen mounts, so a wrong-role page never
/// fires the requests the server would refuse with 403 (SIM-21). Legacy
/// aliases stay for old bookmarks; buyers may browse ALL trips (their Home
/// links there), farmers and truckers may not.
Set<String> webRoutesForRole(UserRole role) {
  final routes = <String>{
    for (final tab in tabsForRole(role)) tab.route,
    for (final tab in sidebarExtrasForRole(role)) tab.route,
    RoleTab.home.route,
    '/profile',
    '/settings',
  };
  switch (role) {
    case UserRole.farmer:
      routes.add('/listings');
    case UserRole.buyer:
      routes.addAll(['/marketplace', '/trips']);
    case UserRole.trucker:
      break;
  }
  return routes;
}

/// Index of [tab] in [role]'s bar, or null when it isn't a tab for this
/// role — callers must then push the destination as a screen instead.
int? roleTabIndex(UserRole role, RoleTab tab) {
  final index = tabsForRole(role).indexOf(tab);
  return index == -1 ? null : index;
}
