import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import 'package:wereda_connect/core/routing/route_names.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/screens/login_screen.dart';
import 'package:wereda_connect/features/auth/screens/register_screen.dart';
import 'package:wereda_connect/features/buyer/presentation/screens/find_crops_screen.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/browse_listings_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/browse_trips_screen.dart';
import 'package:wereda_connect/screens/deliveries_tab_screen.dart';
import 'package:wereda_connect/screens/home_screen.dart';
import 'package:wereda_connect/screens/orders_tab_screen.dart';
import 'package:wereda_connect/screens/payments_tab_screen.dart';
import 'package:wereda_connect/screens/profile_screen.dart';
import 'package:wereda_connect/screens/settings_screen.dart';
import 'package:wereda_connect/shared/layout/adaptive_shell.dart';

/// Creates the go_router configuration for web navigation.
///
/// On mobile, the existing AuthGate + MainNavigation pattern handles routing.
/// On web, go_router provides URL-based navigation with browser back/forward.
GoRouter createWebRouter(AuthBloc authBloc) {
  return GoRouter(
    refreshListenable: _AuthRefreshNotifier(authBloc),
    redirect: (context, state) {
      final authState = authBloc.state;
      final isAuthenticated = authState is AuthAuthenticated;
      final isAuthRoute = state.matchedLocation == '/login' ||
          state.matchedLocation == '/register';

      if (!isAuthenticated && !isAuthRoute) return '/login';
      if (isAuthenticated && isAuthRoute) return '/';
      return null;
    },
    routes: [
      // Auth routes (no shell)
      GoRoute(
        path: '/login',
        name: RouteNames.login,
        builder: (context, state) => const LoginScreen(),
      ),
      GoRoute(
        path: '/register',
        name: RouteNames.register,
        builder: (context, state) => const RegisterScreen(),
      ),

      // Authenticated routes with adaptive shell
      ShellRoute(
        builder: (context, state, child) => AdaptiveShell(child: child),
        routes: [
          GoRoute(
            path: '/',
            name: RouteNames.home,
            builder: (context, state) => const HomeScreen(),
          ),
          GoRoute(
            path: '/my-crops',
            name: RouteNames.myCrops,
            builder: (context, state) =>
                const BrowseListingsScreen(ownerMode: true, embedded: true),
          ),
          GoRoute(
            path: '/find-crops',
            name: RouteNames.findCrops,
            builder: (context, state) => const FindCropsScreen(embedded: true),
          ),
          GoRoute(
            path: '/trips',
            name: RouteNames.trips,
            builder: (context, state) =>
                const BrowseTripsScreen(embedded: true),
          ),
          // Keep legacy routes for backward compatibility
          GoRoute(
            path: '/listings',
            name: RouteNames.listings,
            builder: (context, state) =>
                const BrowseListingsScreen(ownerMode: true, embedded: true),
          ),
          GoRoute(
            path: '/marketplace',
            name: RouteNames.marketplace,
            builder: (context, state) => const FindCropsScreen(embedded: true),
          ),
          GoRoute(
            path: '/orders',
            name: RouteNames.orders,
            builder: (context, state) => const OrdersTabScreen(),
          ),
          GoRoute(
            path: '/deliveries',
            name: RouteNames.deliveries,
            builder: (context, state) => const DeliveriesTabScreen(),
          ),
          GoRoute(
            path: '/payments',
            name: RouteNames.payments,
            builder: (context, state) => const PaymentsTabScreen(),
          ),
          GoRoute(
            path: '/profile',
            name: RouteNames.profile,
            builder: (context, state) => const ProfileScreen(),
          ),
          GoRoute(
            path: '/settings',
            name: RouteNames.settings,
            builder: (context, state) => const SettingsScreen(),
          ),
        ],
      ),
    ],
  );
}

/// Notifies GoRouter when auth state changes.
class _AuthRefreshNotifier extends ChangeNotifier {
  _AuthRefreshNotifier(AuthBloc authBloc) {
    authBloc.stream.listen((_) => notifyListeners());
  }
}
