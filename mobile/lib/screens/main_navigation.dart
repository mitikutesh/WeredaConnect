import 'dart:async';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_bloc.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_state.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/screens/conflict_resolution_screen.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/create_crop_listing_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/create_trip_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/browse_listings_screen.dart';
import 'package:wereda_connect/screens/home_screen.dart';
import 'package:wereda_connect/screens/orders_screen.dart';
import 'package:wereda_connect/screens/profile_screen.dart';

/// Main navigation with 4-tab bottom bar.
///
/// Tabs: Home, Market, Orders, Profile
/// No AppBar — each screen manages its own header.
/// No drawer — settings/about/logout moved to Profile.
class MainNavigation extends StatefulWidget {
  const MainNavigation({super.key});

  @override
  State<MainNavigation> createState() => _MainNavigationState();
}

class _MainNavigationState extends State<MainNavigation> {
  int _currentIndex = 0;
  bool _isOnline = true;
  StreamSubscription<List<ConnectivityResult>>? _connectivitySub;

  /// Counter incremented when returning from create screens to force
  /// HomeScreen to reload its data (overview stats + recent activity).
  int _homeRefreshKey = 0;

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

  Future<void> _onFabPressed(BuildContext context, UserRole role) async {
    switch (role) {
      case UserRole.farmer:
        await Navigator.push(
          context,
          MaterialPageRoute<void>(
            builder: (_) => const CreateCropListingScreen(),
          ),
        );
        if (mounted) setState(() => _homeRefreshKey++);
      case UserRole.trucker:
        await Navigator.push(
          context,
          MaterialPageRoute<void>(
            builder: (_) => const CreateTripScreen(),
          ),
        );
        if (mounted) setState(() => _homeRefreshKey++);
      case UserRole.buyer:
        // Buyer FAB navigates to market
        setState(() => _currentIndex = 1);
    }
  }

  List<Widget> _buildScreens() {
    return [
      HomeScreen(
        key: ValueKey('home_$_homeRefreshKey'),
        isOnline: _isOnline,
        onNavigateToTab: (index) {
          setState(() => _currentIndex = index);
        },
      ),
      const BrowseListingsScreen(),
      const OrdersScreen(),
      const ProfileScreen(),
    ];
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
        final screens = _buildScreens();
        final role = authState is AuthAuthenticated
            ? authState.role
            : UserRole.farmer;

        return BlocListener<ConflictBloc, ConflictState>(
          listener: (context, state) {
            if (state is ConflictPending) {
              final messenger = ScaffoldMessenger.of(context);
              final message =
                  l10n.conflictBannerSingle(state.conflict.entityType);
              messenger.showMaterialBanner(
                MaterialBanner(
                  backgroundColor: Colors.amber.shade50,
                  leading: Icon(
                    Icons.sync,
                    size: 24,
                    color: Colors.amber.shade700,
                  ),
                  content: Text(
                    message,
                    style: const TextStyle(fontSize: 16),
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
            floatingActionButton: SizedBox(
              width: 52,
              height: 52,
              child: FloatingActionButton(
                onPressed: () => _onFabPressed(context, role),
                backgroundColor: AppTheme.accentGold,
                elevation: 4,
                shape: const CircleBorder(),
                child: const Icon(
                  Icons.add,
                  size: 28,
                  color: Colors.white,
                ),
              ),
            ),
            floatingActionButtonLocation:
                FloatingActionButtonLocation.centerDocked,
            bottomNavigationBar: BottomAppBar(
              shape: const CircularNotchedRectangle(),
              notchMargin: 6,
              color: AppTheme.surfaceWhite,
              surfaceTintColor: Colors.transparent,
              elevation: 0,
              child: SizedBox(
                height: 60,
                child: Row(
                  children: [
                    _NavItem(
                      icon: Icons.home_outlined,
                      activeIcon: Icons.home,
                      label: l10n.home,
                      isActive: _currentIndex == 0,
                      onTap: () => setState(() => _currentIndex = 0),
                    ),
                    _NavItem(
                      icon: Icons.storefront_outlined,
                      activeIcon: Icons.storefront,
                      label: l10n.market,
                      isActive: _currentIndex == 1,
                      onTap: () => setState(() => _currentIndex = 1),
                    ),
                    const SizedBox(width: 48), // Space for FAB
                    _NavItem(
                      icon: Icons.receipt_long_outlined,
                      activeIcon: Icons.receipt_long,
                      label: l10n.orders,
                      isActive: _currentIndex == 2,
                      onTap: () => setState(() => _currentIndex = 2),
                    ),
                    _NavItem(
                      icon: Icons.person_outline,
                      activeIcon: Icons.person,
                      label: l10n.profile,
                      isActive: _currentIndex == 3,
                      onTap: () => setState(() => _currentIndex = 3),
                    ),
                  ],
                ),
              ),
            ),
          ),
        );
      },
    );
  }
}

/// Single bottom nav item with icon + label.
class _NavItem extends StatelessWidget {
  final IconData icon;
  final IconData activeIcon;
  final String label;
  final bool isActive;
  final VoidCallback onTap;

  const _NavItem({
    required this.icon,
    required this.activeIcon,
    required this.label,
    required this.isActive,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return Expanded(
      child: InkWell(
        onTap: onTap,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            Icon(
              isActive ? activeIcon : icon,
              size: 24,
              color: isActive ? AppTheme.primaryGreen : AppTheme.textMuted,
            ),
            const SizedBox(height: 2),
            Text(
              label,
              style: TextStyle(
                fontSize: 11,
                fontWeight: isActive ? FontWeight.w600 : FontWeight.w400,
                color: isActive ? AppTheme.primaryGreen : AppTheme.textMuted,
              ),
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
            ),
          ],
        ),
      ),
    );
  }
}
