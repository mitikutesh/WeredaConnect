import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/database/isar_service.dart'
    if (dart.library.html) 'package:wereda_connect/core/database/isar_service_web.dart';
import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/delivery.dart';
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
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/delivery_card.dart';
import 'package:wereda_connect/features/order/presentation/screens/place_order_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/browse_trips_screen.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fieldway_lists.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_summary_tile.dart';

/// Screen showing match results for a buyer intent (a sibling of the
/// detail boards: Back and the title in the page, the request in a summary
/// card, then the matching listings).
///
/// Displays intent summary, stale-data indicator, and a list of
/// matching crop listings with trip suggestions.
class MatchResultsScreen extends StatefulWidget {
  final BuyerIntent intent;
  final List<CropMatchResult>? initialMatches;

  /// True when [initialMatches] were computed against cached data (offline
  /// intent creation) so the "showing saved results" banner is shown.
  final bool initialIsStale;

  const MatchResultsScreen({
    required this.intent,
    this.initialMatches,
    this.initialIsStale = false,
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
      body: SafeArea(
        bottom: false,
        child: FwContentWidth(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              FwDetailHeader(
                title: l10n.restMatchesFor(cropType.displayName(l10n)),
              ),
              Expanded(
                child: widget.initialMatches != null
                    ? _MatchResultsBody(
                        intent: widget.intent,
                        matches: widget.initialMatches!,
                        isStale: widget.initialIsStale,
                      )
                    : BlocBuilder<BuyerIntentBloc, BuyerIntentState>(
                        // Ignore states meant for other screens (e.g. the
                        // ActiveIntentsLoaded emitted when an order fulfills
                        // this intent) so the results stay on screen instead
                        // of a spinner.
                        buildWhen: (previous, current) =>
                            current is BuyerIntentLoading ||
                            current is MatchResultsLoaded ||
                            current is BuyerIntentError,
                        builder: (context, state) {
                          if (state is BuyerIntentLoading) {
                            return const Center(
                              child: CircularProgressIndicator(),
                            );
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
                              child: Padding(
                                padding: const EdgeInsets.all(24),
                                child: Text(
                                  localizeErrorCode(
                                    state.code,
                                    AppLocalizations.of(context)!,
                                  ),
                                  textAlign: TextAlign.center,
                                ),
                              ),
                            );
                          }
                          return const Center(
                            child: CircularProgressIndicator(),
                          );
                        },
                      ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _MatchResultsBody extends StatelessWidget {
  final BuyerIntent intent;
  final List<CropMatchResult> matches;

  /// True when the marketplace refresh failed and these matches were computed
  /// against cached data: the body shows a "showing saved results" note (C1).
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

    // 0 budget = "no budget set": show "any price", not "≤0".
    final budget = intent.maxPricePerKg > 0
        ? '≤${intent.maxPricePerKg.toStringAsFixed(0)} ${l10n.etbPerKg}'
        : l10n.anyPrice;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        if (isStale)
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 0, 20, 10),
            child: FwInfoNote(
              icon: Icons.cloud_off,
              text: l10n.showingCachedResults,
              background: AppTheme.actionCream,
              foreground: AppTheme.actionInk,
            ),
          ),
        // The request this list answers: crop, amount, budget, where to.
        Padding(
          padding: const EdgeInsets.fromLTRB(20, 0, 20, 12),
          child: FwCard(
            padding: const EdgeInsets.all(14),
            child: Row(
              children: [
                DeliveryCropTile(
                  cropKey: intent.cropType,
                  status: DeliveryStatus.pending,
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        '${cropType.displayName(l10n)} · '
                        '${intent.quantityNeeded.toStringAsFixed(0)} ${l10n.quintals} · '
                        '$budget',
                        style: const TextStyle(
                          fontSize: 15,
                          fontWeight: FontWeight.w700,
                          color: AppTheme.soilDark,
                        ),
                      ),
                      const SizedBox(height: 2),
                      Text(
                        '${intent.deliveryWoreda} · ${l10n.matchesFound(matches.length)}',
                        style: const TextStyle(
                          fontSize: 12,
                          color: AppTheme.mutedSteel,
                        ),
                      ),
                      Text(
                        l10n.syncedAgo(_timeSinceSync()),
                        style: const TextStyle(
                          fontSize: 12,
                          color: AppTheme.mutedSteel,
                        ),
                      ),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),

        // Results list
        Expanded(
          child: matches.isEmpty
              ? _EmptyState()
              : ListView.builder(
                  padding: const EdgeInsets.fromLTRB(20, 0, 20, 24),
                  itemCount: matches.length,
                  itemBuilder: (context, index) {
                    final match = matches[index];
                    final intentBloc = context.read<BuyerIntentBloc>();
                    return MatchResultCard(
                      result: match,
                      intent: intent,
                      onTap: () {
                        intentBloc.add(
                          LogMatchOpened(
                            intent: intent,
                            result: match,
                            rank: index,
                          ),
                        );
                        Navigator.push(
                          context,
                          MaterialPageRoute<void>(
                            // Carry the BLoC + intent so an order placed
                            // from the detail screen can fulfill this
                            // intent too.
                            builder: (_) => BlocProvider.value(
                              value: intentBloc,
                              child: CropListingDetailScreen(
                                listing: match.listing,
                                currentUserId: userId,
                                currentUserRole: userRole,
                                deliveryWoreda: intent.deliveryWoreda,
                                quantityNeeded: intent.quantityNeeded,
                                intent: intent,
                              ),
                            ),
                          ),
                        );
                      },
                      // One tap from match to order: the matcher's best
                      // trip suggestion comes along preselected.
                      onOrder: () async {
                        intentBloc.add(
                          LogMatchOpened(
                            intent: intent,
                            result: match,
                            rank: index,
                          ),
                        );
                        // ESC-15: the trip's trucker is asked to commit,
                        // so with several trucks the buyer picks one.
                        final trips = match.suggestedTrips;
                        final Trip? trip;
                        if (trips.length > 1) {
                          trip = await showChooseTripSheet(context, trips);
                          if (trip == null) return;
                        } else {
                          trip = trips.isNotEmpty ? trips.first : null;
                        }
                        if (!context.mounted) return;
                        final farmerName =
                            await _lookupFarmerName(match.listing);
                        if (!context.mounted) return;
                        await Navigator.push(
                          context,
                          MaterialPageRoute<void>(
                            builder: (_) => BlocProvider.value(
                              value: intentBloc,
                              child: PlaceOrderScreen(
                                listing: match.listing,
                                farmerName: farmerName,
                                selectedTrip: trip,
                                intent: intent,
                              ),
                            ),
                          ),
                        );
                      },
                    );
                  },
                ),
        ),
      ],
    );
  }
}

/// No listing matches the request yet: say so and offer the market and
/// the trips instead.
class _EmptyState extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return Center(
      child: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(20, 16, 20, 32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Container(
              width: 56,
              height: 56,
              alignment: Alignment.center,
              decoration: BoxDecoration(
                color: AppTheme.segmentTrack,
                borderRadius: BorderRadius.circular(18),
              ),
              child: const Icon(
                Icons.search_off,
                size: 28,
                color: AppTheme.mutedSteel,
              ),
            ),
            const SizedBox(height: 14),
            Text(
              l10n.noMatchesYet,
              textAlign: TextAlign.center,
              style: const TextStyle(
                fontSize: 16,
                fontWeight: FontWeight.w700,
                color: AppTheme.soilDark,
              ),
            ),
            const SizedBox(height: 20),
            OutlinedButton(
              onPressed: () {
                Navigator.push(
                  context,
                  MaterialPageRoute<void>(
                    builder: (_) => const BrowseListingsScreen(),
                  ),
                );
              },
              style: _secondaryStyle,
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
              style: _secondaryStyle,
              child: Text(l10n.browseAllTrips),
            ),
          ],
        ),
      ),
    );
  }

  static final ButtonStyle _secondaryStyle = OutlinedButton.styleFrom(
    minimumSize: const Size.fromHeight(52),
    textStyle: const TextStyle(
      fontFamily: AppTheme.fontFamily,
      fontFamilyFallback: AppTheme.ethiopicFontFallback,
      fontSize: 15,
      fontWeight: FontWeight.w700,
    ),
  );
}
