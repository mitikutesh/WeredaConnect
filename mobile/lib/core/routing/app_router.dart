import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';

import 'package:wereda_connect/core/routing/route_names.dart';
import 'package:wereda_connect/core/utils/entity_id.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/screens/forgot_password_screen.dart';
import 'package:wereda_connect/features/auth/screens/login_screen.dart';
import 'package:wereda_connect/features/auth/screens/register_screen.dart';
import 'package:wereda_connect/features/auth/screens/reset_password_screen.dart';
import 'package:wereda_connect/features/auth/widgets/security_prompt_listener.dart';
import 'package:wereda_connect/features/buyer/presentation/screens/find_crops_screen.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/browse_listings_screen.dart';
import 'package:wereda_connect/features/live_trip/presentation/share_consent_listener.dart';
import 'package:wereda_connect/features/messaging/presentation/screens/messages_inbox_screen.dart';
import 'package:wereda_connect/features/messaging/presentation/widgets/message_arrival_listener.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/screens/order_detail_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/browse_trips_screen.dart';
import 'package:wereda_connect/screens/deliveries_tab_screen.dart';
import 'package:wereda_connect/screens/home_screen.dart';
import 'package:wereda_connect/screens/orders_tab_screen.dart';
import 'package:wereda_connect/screens/payments_tab_screen.dart';
import 'package:wereda_connect/screens/profile_screen.dart';
import 'package:wereda_connect/screens/settings_screen.dart';
import 'package:wereda_connect/shared/layout/adaptive_shell.dart';
import 'package:wereda_connect/shared/navigation/role_navigation.dart';

const _authRoutes = {
  '/login',
  '/register',
  '/forgot-password',
  '/reset-password',
};

/// Prefix of the order-detail route (`/orders/{orderId}`, ESC-21).
const orderDetailRoutePrefix = '/orders/';

/// Where the web router sends [location] for [authState], or null to stay.
///
/// Pure so it can be unit-tested without mounting the shell. Besides the
/// sign-in gate it applies the role guard (SIM-21): a wrong-role URL (typed,
/// bookmarked, stale) must not mount a screen whose loads the server would
/// refuse with 403, so it goes home instead. An order's own page is open to
/// every role: the server answers it only for the order's parties, and the
/// screen says so to anyone else.
String? webRedirect(AuthState authState, String location) {
  final isAuthRoute = _authRoutes.contains(location);
  if (authState is! AuthAuthenticated) {
    return isAuthRoute ? null : '/login';
  }
  if (isAuthRoute) return '/';
  // Only a plain id: an encoded slash or dot segment never reaches a path.
  if (location.startsWith(orderDetailRoutePrefix) &&
      isPlainEntityId(location.substring(orderDetailRoutePrefix.length))) {
    return null;
  }
  if (!webRoutesForRole(authState.role).contains(location)) return '/';
  return null;
}

/// Creates the go_router configuration for web navigation.
///
/// On mobile, the existing AuthGate + MainNavigation pattern handles routing.
/// On web, go_router provides URL-based navigation with browser back/forward.
GoRouter createWebRouter(AuthBloc authBloc) {
  final pushedRoutes = ShellPushedRoutesObserver();
  return GoRouter(
    refreshListenable: _AuthRefreshNotifier(authBloc),
    redirect: (context, state) =>
        webRedirect(authBloc.state, state.matchedLocation),
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
      GoRoute(
        path: '/forgot-password',
        name: RouteNames.forgotPassword,
        builder: (context, state) => ForgotPasswordScreen(
          initialPhoneNumber: state.uri.queryParameters['phone'],
        ),
      ),
      GoRoute(
        path: '/reset-password',
        name: RouteNames.resetPassword,
        builder: (context, state) => ResetPasswordScreen(
          phoneNumber: state.uri.queryParameters['phone'] ?? '',
        ),
      ),

      // Authenticated routes with adaptive shell. The arrival listener shows
      // an in-app banner when a chat message lands while the web app is open.
      ShellRoute(
        observers: [pushedRoutes],
        builder: (context, state, child) => MessageArrivalListener(
          child: SecurityPromptListener(
            // LT-09: a trucker's first pickup scan of a trip asks whether
            // to share where the truck is.
            child: ShareConsentListener(
              child: AdaptiveShell(pushedRoutes: pushedRoutes, child: child),
            ),
          ),
        ),
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
          // ESC-21: an order's own page (the bell, the Action-needed cards and
          // trip bookings open it on web), with a route-scoped OrderBloc like
          // the Orders tab's detail.
          GoRoute(
            path: '/orders/:orderId',
            name: RouteNames.orderDetail,
            builder: (context, state) {
              final orderId = state.pathParameters['orderId'] ?? '';
              return BlocProvider<OrderBloc>(
                create: (_) => OrderBloc(
                  repository: context.read<OrderBloc>().repository,
                ),
                child: OrderDetailScreen(orderId: orderId),
              );
            },
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
            path: '/messages',
            name: RouteNames.messages,
            builder: (context, state) => const MessagesInboxScreen(),
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
    _subscription = authBloc.stream.listen((_) => notifyListeners());
  }

  late final StreamSubscription<AuthState> _subscription;

  @override
  void dispose() {
    _subscription.cancel();
    super.dispose();
  }
}
