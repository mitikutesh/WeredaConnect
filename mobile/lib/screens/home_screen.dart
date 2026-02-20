import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/create_crop_listing_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/create_trip_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Home screen - main landing page after login
class HomeScreen extends StatelessWidget {
  final ValueChanged<int>? onNavigateToTab;

  const HomeScreen({super.key, this.onNavigateToTab});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final authState = context.watch<AuthBloc>().state;
    final role = authState is AuthAuthenticated
        ? authState.role
        : UserRole.farmer;

    return Center(
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          Icon(
            Icons.agriculture,
            size: 100,
            color: Theme.of(context).colorScheme.primary,
          ),
          const SizedBox(height: 24),
          Text(
            l10n.welcomeMessage,
            style: Theme.of(context).textTheme.headlineMedium,
            textAlign: TextAlign.center,
          ),
          const SizedBox(height: 16),
          Text(
            l10n.offlineReady,
            style: Theme.of(context).textTheme.bodyLarge,
          ),
          const SizedBox(height: 32),
          // Quick action cards per role
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 24),
            child: Column(
              children: _buildQuickActions(context, l10n, role),
            ),
          ),
        ],
      ),
    );
  }

  List<Widget> _buildQuickActions(
    BuildContext context,
    AppLocalizations l10n,
    UserRole role,
  ) {
    switch (role) {
      case UserRole.farmer:
        return [
          _QuickActionCard(
            icon: Icons.add_circle_outline,
            title: l10n.createListing,
            subtitle: l10n.cropListings,
            onTap: () {
              Navigator.push(
                context,
                MaterialPageRoute<void>(
                  builder: (context) => const CreateCropListingScreen(),
                ),
              );
            },
          ),
          const SizedBox(height: 12),
          _QuickActionCard(
            icon: Icons.local_shipping_outlined,
            title: l10n.browseTrips,
            subtitle: l10n.tripListings,
            onTap: () => onNavigateToTab?.call(2),
          ),
        ];
      case UserRole.trucker:
        return [
          _QuickActionCard(
            icon: Icons.local_shipping_outlined,
            title: l10n.createTrip,
            subtitle: l10n.tripListings,
            onTap: () {
              Navigator.push(
                context,
                MaterialPageRoute<void>(
                  builder: (context) => const CreateTripScreen(),
                ),
              );
            },
          ),
          const SizedBox(height: 12),
          _QuickActionCard(
            icon: Icons.list_alt_outlined,
            title: l10n.browseListings,
            subtitle: l10n.cropListings,
            onTap: () => onNavigateToTab?.call(1),
          ),
        ];
      case UserRole.buyer:
        return [
          _QuickActionCard(
            icon: Icons.list_alt_outlined,
            title: l10n.browseListings,
            subtitle: l10n.cropListings,
            onTap: () => onNavigateToTab?.call(1),
          ),
          const SizedBox(height: 12),
          _QuickActionCard(
            icon: Icons.local_shipping_outlined,
            title: l10n.browseTrips,
            subtitle: l10n.tripListings,
            onTap: () => onNavigateToTab?.call(2),
          ),
        ];
    }
  }
}

class _QuickActionCard extends StatelessWidget {
  final IconData icon;
  final String title;
  final String subtitle;
  final VoidCallback onTap;

  const _QuickActionCard({
    required this.icon,
    required this.title,
    required this.subtitle,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return Card(
      child: ListTile(
        leading:
            Icon(icon, size: 32, color: Theme.of(context).colorScheme.primary),
        title: Text(title, style: const TextStyle(fontWeight: FontWeight.bold)),
        subtitle: Text(subtitle),
        trailing: const Icon(Icons.arrow_forward_ios, size: 16),
        onTap: onTap,
      ),
    );
  }
}
