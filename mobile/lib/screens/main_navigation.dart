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
import 'package:wereda_connect/features/buyer/presentation/screens/create_intent_screen.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/create_crop_listing_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/create_trip_screen.dart';
import 'package:wereda_connect/screens/deliveries_tab_screen.dart';
import 'package:wereda_connect/screens/home_screen.dart';
import 'package:wereda_connect/screens/orders_tab_screen.dart';
import 'package:wereda_connect/screens/payments_tab_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Main navigation with center FAB + 4-tab bottom bar.
///
/// Tabs: Home | Orders | [FAB] | Deliveries | Payments
/// The FAB creates a listing/trip or opens market depending on role.
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

  List<Widget> _buildScreens() {
    return [
      HomeScreen(
        key: ValueKey('home_$_homeRefreshKey'),
        isOnline: _isOnline,
        onNavigateToTab: (index) {
          setState(() => _currentIndex = index);
        },
      ),
      const OrdersTabScreen(),
      const DeliveriesTabScreen(),
      const PaymentsTabScreen(),
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
        final screens = _buildScreens();

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
            floatingActionButton: FloatingActionButton(
              onPressed: () => _onFabPressed(context, role),
              child: const Icon(Icons.add),
            ),
            floatingActionButtonLocation:
                FloatingActionButtonLocation.centerDocked,
            bottomNavigationBar: BottomAppBar(
              shape: const CircularNotchedRectangle(),
              notchMargin: 8,
              child: Row(
                mainAxisAlignment: MainAxisAlignment.spaceAround,
                children: [
                  _NavItem(
                    icon: Icons.home_outlined,
                    activeIcon: Icons.home,
                    label: l10n.home,
                    index: 0,
                    currentIndex: _currentIndex,
                    onTap: () => setState(() => _currentIndex = 0),
                  ),
                  _NavItem(
                    icon: Icons.receipt_long_outlined,
                    activeIcon: Icons.receipt_long,
                    label: l10n.orders,
                    index: 1,
                    currentIndex: _currentIndex,
                    onTap: () => setState(() => _currentIndex = 1),
                  ),
                  const SizedBox(width: 48), // FAB gap
                  _NavItem(
                    icon: Icons.local_shipping_outlined,
                    activeIcon: Icons.local_shipping,
                    label: l10n.deliveries,
                    index: 2,
                    currentIndex: _currentIndex,
                    onTap: () => setState(() => _currentIndex = 2),
                  ),
                  _NavItem(
                    icon: Icons.account_balance_wallet_outlined,
                    activeIcon: Icons.account_balance_wallet,
                    label: l10n.payments,
                    index: 3,
                    currentIndex: _currentIndex,
                    onTap: () => setState(() => _currentIndex = 3),
                  ),
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
