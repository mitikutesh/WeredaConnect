import 'package:flutter/material.dart';

import 'package:wereda_connect/features/crop_listing/presentation/screens/browse_listings_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/browse_trips_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Buyer market hub — the see-everything view (Fieldway Canvas: Market.dc.html).
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
            padding: const EdgeInsets.fromLTRB(20, 16, 20, 10),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  l10n.market,
                  style: const TextStyle(
                    fontSize: 26,
                    fontWeight: FontWeight.w800,
                    letterSpacing: -0.5,
                    color: Color(0xFF15201A),
                  ),
                ),
                const SizedBox(height: 12),
                // Fieldway segmented control in #ECEAE2
                Container(
                  padding: const EdgeInsets.all(4),
                  decoration: BoxDecoration(
                    color: const Color(0xFFECEAE2),
                    borderRadius: BorderRadius.circular(14),
                  ),
                  child: Row(
                    children: [
                      Expanded(
                        child: GestureDetector(
                          onTap: () => setState(() => _segment = 0),
                          child: Container(
                            height: 38,
                            decoration: BoxDecoration(
                              color: _segment == 0 ? Colors.white : Colors.transparent,
                              borderRadius: BorderRadius.circular(11),
                              boxShadow: _segment == 0
                                  ? [
                                      BoxShadow(
                                        color: Colors.black.withAlpha(15),
                                        blurRadius: 4,
                                        offset: const Offset(0, 1),
                                      ),
                                    ]
                                  : null,
                            ),
                            child: Row(
                              mainAxisAlignment: MainAxisAlignment.center,
                              children: [
                                Icon(
                                  Icons.eco,
                                  size: 16,
                                  color: _segment == 0
                                      ? AppTheme.fieldGreen
                                      : const Color(0xFF5E6862),
                                ),
                                const SizedBox(width: 6),
                                Text(
                                  l10n.marketCrops,
                                  style: TextStyle(
                                    fontSize: 13,
                                    fontWeight: _segment == 0
                                        ? FontWeight.w700
                                        : FontWeight.w600,
                                    color: _segment == 0
                                        ? const Color(0xFF15201A)
                                        : const Color(0xFF4A544E),
                                  ),
                                ),
                              ],
                            ),
                          ),
                        ),
                      ),
                      Expanded(
                        child: GestureDetector(
                          onTap: () => setState(() => _segment = 1),
                          child: Container(
                            height: 38,
                            decoration: BoxDecoration(
                              color: _segment == 1 ? Colors.white : Colors.transparent,
                              borderRadius: BorderRadius.circular(11),
                              boxShadow: _segment == 1
                                  ? [
                                      BoxShadow(
                                        color: Colors.black.withAlpha(15),
                                        blurRadius: 4,
                                        offset: const Offset(0, 1),
                                      ),
                                    ]
                                  : null,
                            ),
                            child: Row(
                              mainAxisAlignment: MainAxisAlignment.center,
                              children: [
                                Icon(
                                  Icons.local_shipping_outlined,
                                  size: 16,
                                  color: _segment == 1
                                      ? const Color(0xFF8A5410)
                                      : const Color(0xFF5E6862),
                                ),
                                const SizedBox(width: 6),
                                Text(
                                  l10n.marketTransport,
                                  style: TextStyle(
                                    fontSize: 13,
                                    fontWeight: _segment == 1
                                        ? FontWeight.w700
                                        : FontWeight.w600,
                                    color: _segment == 1
                                        ? const Color(0xFF15201A)
                                        : const Color(0xFF4A544E),
                                  ),
                                ),
                              ],
                            ),
                          ),
                        ),
                      ),
                    ],
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
