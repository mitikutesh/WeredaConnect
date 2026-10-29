import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/buyer/presentation/widgets/deliver_to_line.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/buyer_intent.dart';
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
import 'package:wereda_connect/shared/layout/breakpoints.dart';
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
            light: true,
            height: widget.embedded ? 116 : 146,
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
                        color: AppTheme.soilDark,
                        size: 24,
                      ),
                    ),
                  ),
                // Buyer · Find crops board: where the crops go, then the
                // title.
                const DeliverToLine(),
                const SizedBox(height: 8),
                Text(
                  l10n.findCrops,
                  style: const TextStyle(
                    fontSize: 26,
                    fontWeight: FontWeight.w800,
                    letterSpacing: -0.5,
                    color: AppTheme.soilDark,
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

  /// Mark a request fulfilled (the buyer's need is met): it drops out of the
  /// active list and stops generating match badges and push alerts.
  void _fulfillIntent(BuildContext context, BuyerIntent intent) {
    final l10n = AppLocalizations.of(context)!;
    context.read<BuyerIntentBloc>().add(
          FulfillIntent(intentId: intent.intentId, buyerId: intent.buyerId),
        );
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(l10n.requestFulfilled)),
    );
  }

  Future<void> _confirmDelete(BuildContext context, BuyerIntent intent) async {
    final l10n = AppLocalizations.of(context)!;
    final bloc = context.read<BuyerIntentBloc>();
    final messenger = ScaffoldMessenger.of(context);
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text(l10n.deleteRequest),
        content: Text(l10n.deleteRequestConfirm),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, false),
            child: Text(l10n.cancel),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(dialogContext, true),
            child: Text(l10n.delete),
          ),
        ],
      ),
    );
    if (confirmed != true) return;
    bloc.add(
      DeleteIntent(intentId: intent.intentId, buyerId: intent.buyerId),
    );
    messenger.showSnackBar(SnackBar(content: Text(l10n.requestDeleted)));
  }

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

        // Error with retry — a real failure must look like a failure, not
        // like an empty "no matches yet" state, or buyers never retry.
        if (state is BuyerIntentError) {
          return Center(
            child: Column(
              mainAxisAlignment: MainAxisAlignment.center,
              children: [
                Icon(
                  Icons.error_outline,
                  size: 56,
                  color: AppTheme.textMuted.withAlpha(150),
                ),
                const SizedBox(height: 16),
                Text(
                  l10n.somethingWentWrong,
                  style: const TextStyle(
                    fontSize: 16,
                    fontWeight: FontWeight.w600,
                    color: AppTheme.textDark,
                  ),
                ),
                const SizedBox(height: 4),
                Text(
                  localizeErrorCode(state.code, l10n),
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
                  color: AppTheme.statusWarningContainer,
                  padding: const EdgeInsets.symmetric(
                    horizontal: 20,
                    vertical: 8,
                  ),
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
              Expanded(
                child: RefreshIndicator(
                  onRefresh: onRefreshMatches,
                  child: ListView.separated(
                    padding: const EdgeInsets.fromLTRB(
                      20,
                      12,
                      20,
                      Breakpoints.fabClearance,
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
                        onFulfill: () => _fulfillIntent(context, intent),
                        onDelete: () => _confirmDelete(context, intent),
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
