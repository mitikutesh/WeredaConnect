import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_event.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_state.dart';
import 'package:wereda_connect/features/buyer/presentation/screens/create_intent_screen.dart';
import 'package:wereda_connect/features/buyer/presentation/screens/match_results_screen.dart';
import 'package:wereda_connect/features/buyer/presentation/widgets/intent_card.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/browse_listings_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/green_header.dart';

/// Combined "Find Crops" screen for buyers with two tabs: Browse and My Requests.
///
/// The **Browse** tab embeds [BrowseListingsScreen] to show all active
/// marketplace listings. The **My Requests** tab shows the buyer's active
/// intents and allows creating new ones.
///
/// When [embedded] is true the screen omits its own Scaffold and back arrow
/// so it can live inside the adaptive shell as a tab child.
class FindCropsScreen extends StatefulWidget {
  final bool embedded;

  const FindCropsScreen({
    this.embedded = false,
    super.key,
  });

  @override
  State<FindCropsScreen> createState() => _FindCropsScreenState();
}

class _FindCropsScreenState extends State<FindCropsScreen>
    with SingleTickerProviderStateMixin {
  late final TabController _tabController;

  @override
  void initState() {
    super.initState();
    _tabController = TabController(length: 2, vsync: this);
    _loadIntents();
  }

  @override
  void dispose() {
    _tabController.dispose();
    super.dispose();
  }

  String? _getUserId() {
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) return authState.userId;
    return null;
  }

  void _loadIntents() {
    final userId = _getUserId();
    if (userId != null) {
      context.read<BuyerIntentBloc>().add(LoadActiveIntents(buyerId: userId));
    }
  }

  /// Pull-to-refresh: re-pull the marketplace and recompute match counts for
  /// every active intent, then refresh the list. Returns when the BLoC has
  /// settled so the RefreshIndicator spinner stops at the right time.
  Future<void> _refreshMatches() async {
    final userId = _getUserId();
    if (userId == null) return;
    final bloc = context.read<BuyerIntentBloc>();
    // Subscribe before dispatching so we can't miss the terminal state.
    final settled = bloc.stream.firstWhere(
      (s) => s is ActiveIntentsLoaded || s is BuyerIntentError,
    );
    bloc.add(RefreshAllMatches(buyerId: userId));
    await settled;
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    final content = BlocListener<BuyerIntentBloc, BuyerIntentState>(
      listenWhen: (previous, current) => current is BuyerIntentCreated,
      listener: (context, state) => _loadIntents(),
      child: Column(
        children: [
          // ── Header ──────────────────────────────────────────────────
          GreenHeader(
            height: widget.embedded ? 100 : 130,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                if (!widget.embedded)
                  GestureDetector(
                    onTap: () => Navigator.of(context).pop(),
                    child: const Padding(
                      padding: EdgeInsets.only(top: 4, bottom: 4),
                      child: Icon(
                        Icons.arrow_back,
                        color: Colors.white,
                        size: 24,
                      ),
                    ),
                  ),
                Text(
                  l10n.findCrops,
                  style: const TextStyle(
                    fontSize: 22,
                    fontWeight: FontWeight.w700,
                    color: Colors.white,
                  ),
                ),
              ],
            ),
          ),

          // ── Tab bar ─────────────────────────────────────────────────
          Material(
            color: AppTheme.surfaceWhite,
            child: TabBar(
              controller: _tabController,
              indicatorColor: AppTheme.primaryGreen,
              labelColor: AppTheme.primaryGreen,
              unselectedLabelColor: AppTheme.textMuted,
              tabs: [
                Tab(text: l10n.browseAll),
                Tab(text: l10n.myRequests),
              ],
            ),
          ),

          // ── Tab views ───────────────────────────────────────────────
          Expanded(
            child: TabBarView(
              controller: _tabController,
              // Disable swipe-to-change-tab: the horizontal filter chips
              // inside the Browse tab would otherwise cause accidental tab
              // switches when the user scrolls the chip row.
              physics: const NeverScrollableScrollPhysics(),
              children: [
                // Browse tab — reuse the marketplace listings screen.
                // showHeader: false because FindCropsScreen owns the header.
                const BrowseListingsScreen(embedded: true, showHeader: false),

                // My Requests tab
                _MyRequestsTab(
                  onRefresh: _loadIntents,
                  onRefreshMatches: _refreshMatches,
                ),
              ],
            ),
          ),
        ],
      ),
    );

    return widget.embedded ? content : Scaffold(body: content);
  }
}

/// The "My Requests" tab content showing the buyer's active intents.
class _MyRequestsTab extends StatelessWidget {
  final VoidCallback onRefresh;

  /// Pull-to-refresh handler that re-pulls the marketplace and recomputes
  /// match counts (dispatches RefreshAllMatches). Returns when settled.
  final Future<void> Function() onRefreshMatches;

  const _MyRequestsTab({
    required this.onRefresh,
    required this.onRefreshMatches,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return BlocBuilder<BuyerIntentBloc, BuyerIntentState>(
      buildWhen: (previous, current) =>
          current is BuyerIntentLoading ||
          current is ActiveIntentsLoaded ||
          current is BuyerIntentError,
      builder: (context, state) {
        // Loading
        if (state is BuyerIntentLoading) {
          return const Center(child: CircularProgressIndicator());
        }

        // Error with retry
        if (state is BuyerIntentError) {
          return Center(
            child: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                Icon(
                  Icons.search_off,
                  size: 56,
                  color: AppTheme.textMuted.withAlpha(150),
                ),
                const SizedBox(height: 16),
                Text(
                  l10n.noMatchesYet,
                  style: const TextStyle(
                    fontSize: 16,
                    fontWeight: FontWeight.w600,
                    color: AppTheme.textDark,
                  ),
                ),
                const SizedBox(height: 4),
                Text(
                  l10n.whatDoYouNeedSubtitle,
                  style: const TextStyle(
                    fontSize: 13,
                    color: AppTheme.textMuted,
                  ),
                  textAlign: TextAlign.center,
                ),
                const SizedBox(height: 20),
                FilledButton(
                  onPressed: onRefresh,
                  child: Text(l10n.retry),
                ),
              ],
            ),
          );
        }

        // Loaded (empty or with intents)
        if (state is ActiveIntentsLoaded) {
          if (state.intents.isEmpty) {
            return _EmptyRequestsState(onRefresh: onRefresh);
          }

          return Column(
            children: [
              // Offline-first: warn when the marketplace refresh failed and these
              // counts/matches were computed against cached data (C1).
              if (state.isStale)
                Container(
                  width: double.infinity,
                  color: Colors.amber.shade100,
                  padding: const EdgeInsets.symmetric(
                    horizontal: 20,
                    vertical: 8,
                  ),
                  child: Row(
                    children: [
                      const Icon(Icons.cloud_off, size: 16, color: AppTheme.textDark),
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
              Expanded(
                child: RefreshIndicator(
                  onRefresh: onRefreshMatches,
                  child: ListView.separated(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 20,
                      vertical: 12,
                    ),
                    physics: const AlwaysScrollableScrollPhysics(),
                    itemCount: state.intents.length,
                    separatorBuilder: (_, __) => const SizedBox(height: 10),
                    itemBuilder: (context, index) {
                      final intent = state.intents[index];
                      return IntentCard(
                        intent: intent,
                        onTap: () async {
                          await Navigator.push(
                            context,
                            MaterialPageRoute<void>(
                              builder: (_) => BlocProvider.value(
                                value: context.read<BuyerIntentBloc>(),
                                child: MatchResultsScreen(intent: intent),
                              ),
                            ),
                          );
                          onRefresh();
                        },
                      );
                    },
                  ),
                ),
              ),
              // New Request button at the bottom
              Padding(
                padding: const EdgeInsets.only(bottom: 16),
                child: TextButton.icon(
                  onPressed: () async {
                    await Navigator.push(
                      context,
                      MaterialPageRoute<void>(
                        builder: (_) => const CreateIntentScreen(),
                      ),
                    );
                    onRefresh();
                  },
                  icon: const Icon(Icons.add, size: 16),
                  label: Text(l10n.newBuyingIntent),
                ),
              ),
            ],
          );
        }

        // Initial / fallback state
        return const Center(child: CircularProgressIndicator());
      },
    );
  }
}

/// Empty state for the My Requests tab when the buyer has no active intents.
class _EmptyRequestsState extends StatelessWidget {
  final VoidCallback onRefresh;

  const _EmptyRequestsState({required this.onRefresh});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            const Icon(
              Icons.search_off,
              size: 64,
              color: AppTheme.textMuted,
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
            const SizedBox(height: 4),
            Text(
              l10n.whatDoYouNeedSubtitle,
              style: const TextStyle(
                fontSize: 13,
                color: AppTheme.textMuted,
              ),
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: 24),
            FilledButton.icon(
              onPressed: () async {
                await Navigator.push(
                  context,
                  MaterialPageRoute<void>(
                    builder: (_) => const CreateIntentScreen(),
                  ),
                );
                onRefresh();
              },
              icon: const Icon(Icons.add),
              label: Text(l10n.newBuyingIntent),
            ),
          ],
        ),
      ),
    );
  }
}
