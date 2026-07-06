import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/database/isar_service.dart'
    if (dart.library.html) 'package:wereda_connect/core/database/isar_service_web.dart';
import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_event.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_state.dart';
import 'package:wereda_connect/features/buyer/presentation/widgets/match_result_card.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/browse_listings_screen.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/crop_listing_detail_screen.dart';
import 'package:wereda_connect/features/order/presentation/screens/place_order_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/browse_trips_screen.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Screen showing match results for a buyer intent.
///
/// Displays intent summary, stale-data indicator, and a list of
/// matching crop listings with trip suggestions.
class MatchResultsScreen extends StatefulWidget {
  final BuyerIntent intent;
  final List<CropMatchResult>? initialMatches;

  const MatchResultsScreen({
    required this.intent,
    this.initialMatches,
    super.key,
  });

  @override
  State<MatchResultsScreen> createState() => _MatchResultsScreenState();
}

class _MatchResultsScreenState extends State<MatchResultsScreen> {
  @override
  void initState() {
    super.initState();
    // Load matches via BLoC only if not provided directly (e.g. from create).
    if (widget.initialMatches == null) {
      context
          .read<BuyerIntentBloc>()
          .add(LoadMatchResults(intentId: widget.intent.intentId));
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final cropType = CropType.fromKey(widget.intent.cropType);

    return Scaffold(
      appBar: AppBar(
        title: Text('${cropType.displayName(l10n)} ${l10n.findMatches}'),
        elevation: 0,
      ),
      body: widget.initialMatches != null
          ? _MatchResultsBody(
              intent: widget.intent,
              matches: widget.initialMatches!,
            )
          : BlocBuilder<BuyerIntentBloc, BuyerIntentState>(
              builder: (context, state) {
                if (state is BuyerIntentLoading) {
                  return const Center(child: CircularProgressIndicator());
                }
                if (state is MatchResultsLoaded) {
                  return _MatchResultsBody(
                    intent: state.intent,
                    matches: state.matches,
                    isStale: state.isStale,
                  );
                }
                if (state is BuyerIntentError) {
                  return Center(
                    child: Text(
                      localizeErrorCode(
                        state.code,
                        AppLocalizations.of(context)!,
                      ),
                    ),
                  );
                }
                return const Center(child: CircularProgressIndicator());
              },
            ),
    );
  }
}

class _MatchResultsBody extends StatelessWidget {
  final BuyerIntent intent;
  final List<CropMatchResult> matches;

  /// True when the marketplace refresh failed and these matches were computed
  /// against cached data — the body shows a "showing saved results" banner (C1).
  final bool isStale;

  const _MatchResultsBody({
    required this.intent,
    required this.matches,
    this.isStale = false,
  });

  String _timeSinceSync() {
    final lastSync = intent.lastMatchedAt ?? intent.createdAt;
    final diff = DateTime.now().toUtc().difference(lastSync);
    if (diff.inMinutes < 1) return '<1 min';
    if (diff.inMinutes < 60) return '${diff.inMinutes} min';
    if (diff.inHours < 24) return '${diff.inHours}h';
    return '${diff.inDays}d';
  }

  static Future<String> _lookupFarmerName(CropListing listing) async {
    if (listing.farmerName.isNotEmpty) return listing.farmerName;
    if (kIsWeb) return listing.farmerId;
    final isar = IsarService.instance.db;
    final farmer =
        await isar.users.filter().userIdEqualTo(listing.farmerId).findFirst();
    return farmer?.name ?? listing.farmerId;
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final cropType = CropType.fromKey(intent.cropType);
    final authState = context.read<AuthBloc>().state;
    final userId = authState is AuthAuthenticated ? authState.userId : '';
    final userRole =
        authState is AuthAuthenticated ? authState.role : UserRole.buyer;

    return SafeArea(
      child: Column(
        children: [
          if (isStale)
            Container(
              width: double.infinity,
              color: AppTheme.statusWarningContainer,
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
              child: Row(
                children: [
                  const Icon(
                    Icons.cloud_off,
                    size: 16,
                    color: AppTheme.textDark,
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      l10n.showingCachedResults,
                      style: const TextStyle(
                        fontSize: 12,
                        color: AppTheme.textDark,
                      ),
                    ),
                  ),
                ],
              ),
            ),
          // Header summary
          Container(
            width: double.infinity,
            padding: const EdgeInsets.all(16),
            color: AppTheme.primaryGreen.withAlpha(10),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  '${cropType.displayName(l10n)} · ${intent.quantityNeeded.toStringAsFixed(0)} ${l10n.quintals} · ≤${intent.maxPricePerKg.toStringAsFixed(0)} ${l10n.etbPerKg}',
                  style: const TextStyle(
                    fontSize: 15,
                    fontWeight: FontWeight.w600,
                    color: AppTheme.textDark,
                  ),
                ),
                const SizedBox(height: 4),
                Row(
                  children: [
                    const Icon(
                      Icons.arrow_forward,
                      size: 14,
                      color: AppTheme.textMuted,
                    ),
                    const SizedBox(width: 4),
                    Text(
                      '${intent.deliveryWoreda} · ${l10n.matchesFound(matches.length)}',
                      style: const TextStyle(
                        fontSize: 13,
                        color: AppTheme.textMuted,
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 4),
                Text(
                  l10n.syncedAgo(_timeSinceSync()),
                  style: const TextStyle(
                    fontSize: 11,
                    color: AppTheme.textMuted,
                    fontStyle: FontStyle.italic,
                  ),
                ),
              ],
            ),
          ),

          // Results list
          Expanded(
            child: matches.isEmpty
                ? _EmptyState()
                : ListView.builder(
                    padding: const EdgeInsets.all(16),
                    itemCount: matches.length,
                    itemBuilder: (context, index) {
                      final match = matches[index];
                      return MatchResultCard(
                        result: match,
                        onTap: () {
                          Navigator.push(
                            context,
                            MaterialPageRoute<void>(
                              builder: (_) => CropListingDetailScreen(
                                listing: match.listing,
                                currentUserId: userId,
                                currentUserRole: userRole,
                                deliveryWoreda: intent.deliveryWoreda,
                                quantityNeeded: intent.quantityNeeded,
                              ),
                            ),
                          );
                        },
                        // One tap from match to order — the matcher's best
                        // trip suggestion comes along preselected.
                        onOrder: () async {
                          final farmerName =
                              await _lookupFarmerName(match.listing);
                          if (!context.mounted) return;
                          await Navigator.push(
                            context,
                            MaterialPageRoute<void>(
                              builder: (_) => PlaceOrderScreen(
                                listing: match.listing,
                                farmerName: farmerName,
                                selectedTrip: match.suggestedTrips.isNotEmpty
                                    ? match.suggestedTrips.first
                                    : null,
                              ),
                            ),
                          );
                        },
                      );
                    },
                  ),
          ),
        ],
      ),
    );
  }
}

class _EmptyState extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              Icons.search_off,
              size: 64,
              color: AppTheme.textMuted.withAlpha(100),
            ),
            const SizedBox(height: 16),
            Text(
              l10n.noMatchesYet,
              style: const TextStyle(
                fontSize: 16,
                fontWeight: FontWeight.w500,
                color: AppTheme.textMuted,
              ),
            ),
            const SizedBox(height: 24),
            OutlinedButton(
              onPressed: () {
                Navigator.push(
                  context,
                  MaterialPageRoute<void>(
                    builder: (_) => const BrowseListingsScreen(),
                  ),
                );
              },
              child: Text(l10n.browseAllListings),
            ),
            const SizedBox(height: 8),
            OutlinedButton(
              onPressed: () {
                Navigator.push(
                  context,
                  MaterialPageRoute<void>(
                    builder: (_) => const BrowseTripsScreen(),
                  ),
                );
              },
              child: Text(l10n.browseAllTrips),
            ),
          ],
        ),
      ),
    );
  }
}
