import 'package:flutter/material.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Listings screen - browse and manage crop listings
class ListingsScreen extends StatelessWidget {
  const ListingsScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return Center(
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          Icon(
            Icons.list_alt,
            size: 80,
            color: Theme.of(context).colorScheme.secondary,
          ),
          const SizedBox(height: 24),
          Text(
            l10n.noCropListings,
            style: Theme.of(context).textTheme.titleLarge,
          ),
          const SizedBox(height: 16),
          Text(
            l10n.comingSoon,
            style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                  color: Theme.of(context).colorScheme.onSurface.withOpacity(0.6),
                ),
          ),
          const SizedBox(height: 32),
          FilledButton.icon(
            onPressed: () {
              ScaffoldMessenger.of(context).showSnackBar(
                SnackBar(content: Text(l10n.comingSoon)),
              );
            },
            icon: const Icon(Icons.add),
            label: Text(l10n.createListing),
          ),
        ],
      ),
    );
  }
}

