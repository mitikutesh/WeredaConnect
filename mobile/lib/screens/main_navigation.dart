import 'dart:async';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_bloc.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_state.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/screens/conflict_resolution_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/browse_listings_screen.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/create_crop_listing_screen.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/delivery_list_screen.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/screens/payment_history_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/browse_trips_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/create_trip_screen.dart';
import 'package:wereda_connect/screens/home_screen.dart';
import 'package:wereda_connect/screens/profile_screen.dart';
import 'package:wereda_connect/screens/settings_screen.dart';

/// Main navigation wrapper with bottom navigation bar and drawer
class MainNavigation extends StatefulWidget {
  const MainNavigation({super.key});

  @override
  State<MainNavigation> createState() => _MainNavigationState();
}

class _MainNavigationState extends State<MainNavigation> {
  int _currentIndex = 0;
  bool _isOnline = true; // Optimistic default until first stream event
  StreamSubscription<List<ConnectivityResult>>? _connectivitySub;

  @override
  void initState() {
    super.initState();
    final monitor = context.read<ConnectivityMonitor>();
    // Seed with real current state
    monitor.isConnected().then((online) {
      if (mounted) setState(() => _isOnline = online);
    });
    // Listen for changes (debounced — safe on flaky 3G)
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

  List<Widget> _buildScreens() {
    return [
      HomeScreen(
        onNavigateToTab: (index) {
          setState(() => _currentIndex = index);
        },
      ),
      const BrowseListingsScreen(),
      const BrowseTripsScreen(),
      const DeliveryListScreen(),
      const PaymentHistoryScreen(),
      ProfileScreen(
        onNavigateToTab: (index) {
          setState(() => _currentIndex = index);
        },
      ),
    ];
  }

  void _showLogoutConfirmation(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    showDialog<void>(
      context: context,
      builder: (dialogContext) {
        return AlertDialog(
          title: Text(l10n.logout),
          content: Text(l10n.logoutConfirmation),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(dialogContext).pop(),
              child: Text(l10n.cancel),
            ),
            TextButton(
              onPressed: () {
                Navigator.of(dialogContext).pop();
                // clearCache: false preserves offline credentials
                // (important for Ethiopian rural users who share devices)
                context.read<AuthBloc>().add(
                      const AuthLogoutRequested(clearCache: false),
                    );
              },
              style: TextButton.styleFrom(foregroundColor: Colors.red),
              child: Text(l10n.logout),
            ),
          ],
        );
      },
    );
  }

  Widget? _buildFab(UserRole role) {
    // FAB on tab 1 only for farmer (Create Listing)
    if (_currentIndex == 1 && role == UserRole.farmer) {
      return FloatingActionButton(
        onPressed: () {
          Navigator.push(
            context,
            MaterialPageRoute<void>(
              builder: (context) => const CreateCropListingScreen(),
            ),
          );
        },
        child: const Icon(Icons.add),
      );
    }
    // FAB on tab 2 only for trucker (Create Trip)
    if (_currentIndex == 2 && role == UserRole.trucker) {
      return FloatingActionButton(
        onPressed: () {
          Navigator.push(
            context,
            MaterialPageRoute<void>(
              builder: (context) => const CreateTripScreen(),
            ),
          );
        },
        child: const Icon(Icons.add),
      );
    }
    return null;
  }

  String _getListingsTabLabel(AppLocalizations l10n, UserRole role) {
    return role == UserRole.farmer ? l10n.myListings : l10n.browseListings;
  }

  String _getTripsTabLabel(AppLocalizations l10n, UserRole role) {
    return role == UserRole.trucker ? l10n.myTrips : l10n.browseTrips;
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
            appBar: AppBar(
              title: Text(_getTitle(l10n)),
              backgroundColor: Theme.of(context).colorScheme.inversePrimary,
              actions: [
                IconButton(
                  icon: Icon(
                    _isOnline
                        ? Icons.cloud_done_outlined
                        : Icons.cloud_off_outlined,
                    color: _isOnline
                        ? Theme.of(context).colorScheme.primary
                        : Theme.of(context)
                            .colorScheme
                            .onSurface
                            .withValues(alpha: 0.4),
                  ),
                  tooltip: _isOnline ? l10n.offlineReady : l10n.offlineMode,
                  onPressed: () {
                    ScaffoldMessenger.of(context).showSnackBar(
                      SnackBar(
                        content: Text(
                          _isOnline
                              ? l10n.syncComplete
                              : l10n.offlineModeIndicator,
                        ),
                      ),
                    );
                  },
                ),
              ],
            ),
            drawer: _buildDrawer(context, l10n),
            body: screens[_currentIndex],
            floatingActionButton: _buildFab(role),
            bottomNavigationBar: NavigationBar(
              selectedIndex: _currentIndex,
              onDestinationSelected: (index) {
                setState(() {
                  _currentIndex = index;
                });
              },
              destinations: [
                NavigationDestination(
                  icon: const Icon(Icons.home_outlined),
                  selectedIcon: const Icon(Icons.home),
                  label: l10n.home,
                ),
                NavigationDestination(
                  icon: const Icon(Icons.list_alt_outlined),
                  selectedIcon: const Icon(Icons.list_alt),
                  label: _getListingsTabLabel(l10n, role),
                ),
                NavigationDestination(
                  icon: const Icon(Icons.local_shipping_outlined),
                  selectedIcon: const Icon(Icons.local_shipping),
                  label: _getTripsTabLabel(l10n, role),
                ),
                NavigationDestination(
                  icon: const Icon(Icons.inventory_2_outlined),
                  selectedIcon: const Icon(Icons.inventory_2),
                  label: l10n.deliveries,
                ),
                NavigationDestination(
                  icon: const Icon(Icons.account_balance_wallet_outlined),
                  selectedIcon: const Icon(Icons.account_balance_wallet),
                  label: l10n.escrowPayments,
                ),
                NavigationDestination(
                  icon: const Icon(Icons.person_outline),
                  selectedIcon: const Icon(Icons.person),
                  label: l10n.profile,
                ),
              ],
            ),
          ),
        );
      },
    );
  }

  String _getTitle(AppLocalizations l10n) {
    switch (_currentIndex) {
      case 0:
        return l10n.appTitle;
      case 1:
        return l10n.cropListings;
      case 2:
        return l10n.tripListings;
      case 3:
        return l10n.deliveryMyDeliveries;
      case 4:
        return l10n.escrowPayments;
      case 5:
        return l10n.myProfile;
      default:
        return l10n.appTitle;
    }
  }

  Widget _buildDrawer(BuildContext context, AppLocalizations l10n) {
    return Drawer(
      child: ListView(
        padding: EdgeInsets.zero,
        children: [
          DrawerHeader(
            decoration: BoxDecoration(
              color: Theme.of(context).colorScheme.primaryContainer,
            ),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              mainAxisAlignment: MainAxisAlignment.end,
              children: [
                Icon(
                  Icons.agriculture,
                  size: 48,
                  color: Theme.of(context).colorScheme.onPrimaryContainer,
                ),
                const SizedBox(height: 8),
                Text(
                  l10n.appTitle,
                  style: Theme.of(context).textTheme.headlineSmall?.copyWith(
                        color: Theme.of(context).colorScheme.onPrimaryContainer,
                      ),
                ),
                Text(
                  _isOnline ? l10n.offlineReady : l10n.offlineMode,
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(
                        color: Theme.of(context).colorScheme.onPrimaryContainer,
                      ),
                ),
              ],
            ),
          ),
          ListTile(
            leading: const Icon(Icons.settings),
            title: Text(l10n.settings),
            onTap: () {
              Navigator.pop(context); // Close drawer
              Navigator.push(
                context,
                MaterialPageRoute<void>(
                  builder: (context) => const SettingsScreen(),
                ),
              );
            },
          ),
          ListTile(
            leading: const Icon(Icons.info_outline),
            title: Text(l10n.about),
            onTap: () {
              Navigator.pop(context); // Close drawer
              showAboutDialog(
                context: context,
                applicationName: l10n.appName,
                applicationVersion: '1.0.0',
                applicationIcon: const Icon(Icons.agriculture, size: 48),
                children: [
                  Text(l10n.aboutDescription),
                  const SizedBox(height: 8),
                  Text(l10n.aboutTagline),
                ],
              );
            },
          ),
          const Divider(),
          ListTile(
            leading: const Icon(Icons.logout, color: Colors.red),
            title: Text(
              l10n.logout,
              style: const TextStyle(color: Colors.red),
            ),
            onTap: () {
              Navigator.pop(context); // Close drawer
              _showLogoutConfirmation(context);
            },
          ),
        ],
      ),
    );
  }
}
