import 'dart:async';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/screens/messages_inbox_screen.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_state.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/screens/conflict_resolution_screen.dart';
import 'package:wereda_connect/features/buyer/presentation/screens/create_intent_screen.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/browse_listings_screen.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/create_crop_listing_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/browse_trips_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/create_trip_screen.dart';
import 'package:wereda_connect/screens/deliveries_tab_screen.dart';
import 'package:wereda_connect/screens/home_screen.dart';
import 'package:wereda_connect/screens/market_screen.dart';
import 'package:wereda_connect/screens/orders_tab_screen.dart';
import 'package:wereda_connect/screens/payments_tab_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/navigation/role_navigation.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Main navigation with a labeled center FAB + role-shaped 4-tab bar.
///
/// Tab sets come from [tabsForRole] — each role's bar reflects its actual
/// job (see role_navigation.dart). The FAB is the role's create verb.
/// No AppBar — each screen manages its own header.
/// Profile accessible from header icon on each screen.
class MainNavigation extends StatefulWidget {
  const MainNavigation({super.key});

  @override
  State<MainNavigation> createState() => _MainNavigationState();
}

class _MainNavigationState extends State<MainNavigation> {
  int _currentIndex = 0;
  bool _isOnline = true;
  int _homeRefreshKey = 0;
  StreamSubscription<List<ConnectivityResult>>? _connectivitySub;

  @override
  void initState() {
    super.initState();
    final monitor = context.read<ConnectivityMonitor>();
    monitor.isConnected().then((online) {
      if (mounted) setState(() => _isOnline = online);
    });
    _connectivitySub = monitor.onConnectivityChanged.listen((results) {
      final online = monitor.isConnectedFromResults(results);
      if (mounted) setState(() => _isOnline = online);
    });
  }

  @override
  void dispose() {
    _connectivitySub?.cancel();
    super.dispose();
  }

  void _refreshHome() {
    if (mounted) setState(() => _homeRefreshKey++);
  }

  List<Widget> _buildScreens(UserRole role) {
    return [
      for (final tab in tabsForRole(role))
        switch (tab) {
          RoleTab.home => HomeScreen(
              key: ValueKey('home_$_homeRefreshKey'),
              isOnline: _isOnline,
              onNavigateToTab: (index) {
                setState(() => _currentIndex = index);
              },
            ),
          RoleTab.myCrops =>
            const BrowseListingsScreen(ownerMode: true, embedded: true),
          RoleTab.myTrips => const BrowseTripsScreen(embedded: true),
          RoleTab.market => const MarketScreen(),
          RoleTab.orders => const OrdersTabScreen(),
          RoleTab.deliveries => const DeliveriesTabScreen(),
          RoleTab.payments => const PaymentsTabScreen(),
          // Never a bar tab (sidebar-extra only); reachable from the home
          // header on mobile. Handled here to keep the switch exhaustive.
          RoleTab.messages => const MessagesInboxScreen(),
        },
    ];
  }

  Future<void> _onFabPressed(BuildContext context, UserRole role) async {
    switch (role) {
      case UserRole.farmer:
        await Navigator.push(
          context,
          MaterialPageRoute<void>(
            builder: (_) => const CreateCropListingScreen(),
          ),
        );
      case UserRole.trucker:
        await Navigator.push(
          context,
          MaterialPageRoute<void>(
            builder: (_) => const CreateTripScreen(),
          ),
        );
      case UserRole.buyer:
        await Navigator.push(
          context,
          MaterialPageRoute<void>(
            builder: (_) => const CreateIntentScreen(),
          ),
        );
    }
    _refreshHome();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return BlocBuilder<AuthBloc, AuthState>(
      buildWhen: (prev, curr) =>
          prev is! AuthAuthenticated ||
          curr is! AuthAuthenticated ||
          prev.role != curr.role,
      builder: (context, authState) {
        final role =
            authState is AuthAuthenticated ? authState.role : UserRole.farmer;
        final screens = _buildScreens(role);
        final tabs = tabsForRole(role);
        // Role changes rebuild the tab set; never index past it.
        if (_currentIndex >= screens.length) _currentIndex = 0;

        return BlocListener<ConflictBloc, ConflictState>(
          listener: (context, state) {
            if (state is ConflictPending) {
              final messenger = ScaffoldMessenger.of(context);
              final message =
                  l10n.conflictBannerSingle(state.conflict.entityType);
              messenger.showMaterialBanner(
                MaterialBanner(
                  backgroundColor: AppTheme.statusWarning.withAlpha(25),
                  leading: const Icon(
                    Icons.sync,
                    size: 24,
                    color: AppTheme.statusWarning,
                  ),
                  content: Text(
                    message,
                    style: Theme.of(context).textTheme.bodyLarge,
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                  ),
                  actions: [
                    TextButton(
                      onPressed: () {
                        messenger.hideCurrentMaterialBanner();
                        Navigator.push(
                          context,
                          MaterialPageRoute<void>(
                            builder: (_) => BlocProvider.value(
                              value: context.read<ConflictBloc>(),
                              child: ConflictResolutionScreen(
                                conflict: state.conflict,
                              ),
                            ),
                          ),
                        );
                      },
                      child: Text(l10n.conflictBannerAction),
                    ),
                  ],
                  onVisible: () {
                    Future.delayed(const Duration(seconds: 8), () {
                      messenger.hideCurrentMaterialBanner();
                    });
                  },
                ),
              );
            }
          },
          child: Scaffold(
            body: screens[_currentIndex],
            // The role's one create verb, labeled — an icon-only "+"
            // assumes app-convention literacy; a verb assumes nothing.
            floatingActionButton: FloatingActionButton.extended(
              onPressed: () => _onFabPressed(context, role),
              shape: const StadiumBorder(),
              icon: const Icon(Icons.add),
              label: Text(
                switch (role) {
                  UserRole.farmer => l10n.sellCrops,
                  UserRole.trucker => l10n.postTrip,
                  UserRole.buyer => l10n.newBuyingIntent,
                },
              ),
            ),
            floatingActionButtonLocation:
                FloatingActionButtonLocation.centerDocked,
            bottomNavigationBar: BottomAppBar(
              shape: const AutomaticNotchedShape(
                RoundedRectangleBorder(),
                StadiumBorder(),
              ),
              notchMargin: 8,
              child: Row(
                mainAxisAlignment: MainAxisAlignment.spaceAround,
                children: [
                  for (var i = 0; i < tabs.length; i++) ...[
                    if (i == 2) const SizedBox(width: 48), // FAB gap
                    _NavItem(
                      icon: tabs[i].icon,
                      activeIcon: tabs[i].activeIcon,
                      label: tabs[i].label(l10n),
                      index: i,
                      currentIndex: _currentIndex,
                      onTap: () => setState(() => _currentIndex = i),
                    ),
                  ],
                ],
              ),
            ),
          ),
        );
      },
    );
  }
}

class _NavItem extends StatelessWidget {
  final IconData icon;
  final IconData activeIcon;
  final String label;
  final int index;
  final int currentIndex;
  final VoidCallback onTap;

  const _NavItem({
    required this.icon,
    required this.activeIcon,
    required this.label,
    required this.index,
    required this.currentIndex,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    final isActive = index == currentIndex;
    final color = isActive ? AppTheme.primaryGreen : AppTheme.textMuted;

    return InkWell(
      onTap: onTap,
      borderRadius: BorderRadius.circular(8),
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(isActive ? activeIcon : icon, color: color, size: 24),
            const SizedBox(height: 2),
            Text(
              label,
              style: TextStyle(
                fontSize: 11,
                fontWeight: FontWeight.w500,
                color: color,
              ),
            ),
          ],
        ),
      ),
    );
  }
}
