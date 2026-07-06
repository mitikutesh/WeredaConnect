import 'package:flutter/material.dart';

import 'package:wereda_connect/features/crop_listing/presentation/screens/browse_listings_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/browse_trips_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Buyer market hub — the see-everything view.
///
/// Two segments over one concept ("what can I buy / how can I move it"):
/// - Crops: ALL crop listings from every farmer
/// - Transport: ALL trips from every trucker
///
/// The buyer-sees-all visibility rule is enforced by the embedded browse
/// screens' own role-aware queries; this hub only composes them.
class MarketScreen extends StatefulWidget {
  const MarketScreen({super.key});

  @override
  State<MarketScreen> createState() => _MarketScreenState();
}

class _MarketScreenState extends State<MarketScreen> {
  int _segment = 0;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return SafeArea(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 8),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  l10n.market,
                  style: Theme.of(context).textTheme.headlineMedium,
                ),
                const SizedBox(height: 12),
                SegmentedButton<int>(
                  segments: [
                    ButtonSegment(
                      value: 0,
                      icon: const Icon(Icons.eco_outlined),
                      label: Text(l10n.marketCrops),
                    ),
                    ButtonSegment(
                      value: 1,
                      icon: const Icon(Icons.route_outlined),
                      label: Text(l10n.marketTransport),
                    ),
                  ],
                  selected: {_segment},
                  onSelectionChanged: (selection) {
                    setState(() => _segment = selection.first);
                  },
                  style: SegmentedButton.styleFrom(
                    selectedBackgroundColor: AppTheme.primary,
                    selectedForegroundColor: Colors.white,
                  ),
                ),
              ],
            ),
          ),
          Expanded(
            child: IndexedStack(
              index: _segment,
              children: const [
                BrowseListingsScreen(embedded: true, showHeader: false),
                BrowseTripsScreen(embedded: true),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
