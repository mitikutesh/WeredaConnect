import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_event.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_state.dart';
import 'package:wereda_connect/features/buyer/presentation/screens/create_intent_screen.dart';
import 'package:wereda_connect/features/buyer/presentation/screens/match_results_screen.dart';
import 'package:wereda_connect/features/buyer/presentation/widgets/deliver_to_line.dart';
import 'package:wereda_connect/features/buyer/presentation/widgets/intent_card.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/browse_listings_screen.dart';
import 'package:wereda_connect/screens/profile_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// The buyer's Find Crops screen (Fieldway Canvas: Market.dc.html): "Deliver
/// to" with the place tile, the title, then the Browse / My Requests
/// segmented switch.
///
/// **Browse** embeds [BrowseListingsScreen] over every farmer's active
/// listing; its search box and price range stay out of the default view,
/// behind the header's search button, as the board shows the list bare.
/// **My Requests** lists the buyer's active intents and creates new ones.
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

class _FindCropsScreenState extends State<FindCropsScreen> {
  /// 0 = Browse, 1 = My Requests.
  int _segment = 0;

  /// Whether Browse shows its search box and price range.
  bool _filtersOpen = false;

  @override
  void initState() {
    super.initState();
    _loadIntents();
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
    // "My Requests · 1": the buyer's active intents, when any.
    final intentState = context.watch<BuyerIntentBloc>().state;
    final intentCount =
        intentState is ActiveIntentsLoaded ? intentState.intents.length : 0;

    final content = BlocListener<BuyerIntentBloc, BuyerIntentState>(
      listenWhen: (previous, current) => current is BuyerIntentCreated,
      listener: (context, state) => _loadIntents(),
      child: FwContentWidth(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            SafeArea(
              bottom: false,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  if (!widget.embedded)
                    Padding(
                      padding: const EdgeInsets.fromLTRB(8, 4, 8, 0),
                      child: IconButton(
                        onPressed: () => Navigator.of(context).pop(),
                        tooltip: l10n.back,
                        icon: const Icon(
                          Icons.arrow_back,
                          color: AppTheme.soilDark,
                        ),
                      ),
                    ),
                  // Buyer · Find crops board: where the crops go, then the
                  // title, with the search button and the profile square.
                  FwPageHeader(
                    eyebrowWidget: const DeliverToLine(),
                    title: l10n.findCrops,
                    trailing: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        if (_segment == 0) ...[
                          FwSquareButton(
                            semanticLabel: l10n.marketSearchToggle,
                            background: _filtersOpen
                                ? AppTheme.fieldGreenTint
                                : AppTheme.surfaceWhite,
                            onTap: () =>
                                setState(() => _filtersOpen = !_filtersOpen),
                            child: Icon(
                              Icons.search,
                              size: 20,
                              color: _filtersOpen
                                  ? AppTheme.fieldGreen
                                  : AppTheme.soilDark,
                            ),
                          ),
                          const SizedBox(width: 8),
                        ],
                        FwSquareButton(
                          semanticLabel: l10n.profile,
                          onTap: () => Navigator.push(
                            context,
                            MaterialPageRoute<void>(
                              builder: (_) => const ProfileScreen(),
                            ),
                          ),
                          child: const Icon(
                            Icons.person_outline,
                            size: 20,
                            color: AppTheme.soilDark,
                          ),
                        ),
                      ],
                    ),
                  ),
                ],
              ),
            ),

            // ── The board's segmented switch ───────────────────────────
            Padding(
              padding: const EdgeInsets.fromLTRB(20, 0, 20, 4),
              child: FwSegmented<int>(
                segments: [
                  FwSegment(value: 0, label: l10n.browseAll),
                  FwSegment(
                    value: 1,
                    label: l10n.myRequests,
                    count: intentCount > 0 ? intentCount : null,
                  ),
                ],
                selected: _segment,
                onChanged: (segment) => setState(() => _segment = segment),
              ),
            ),

            // ── Segment bodies (both kept alive: the list keeps its scroll,
            // filters and reads while the buyer looks at their requests;
            // the hidden one is off stage with its animations stopped) ──
            Expanded(
              child: Stack(
                children: [
                  for (final (index, body) in [
                    // Browse: the marketplace list without its own header;
                    // the search box and price range follow the header
                    // button.
                    BrowseListingsScreen(
                      embedded: true,
                      showHeader: false,
                      showFilters: _filtersOpen,
                    ),
                    _MyRequestsTab(
                      onRefresh: _loadIntents,
                      onRefreshMatches: _refreshMatches,
                    ),
                  ].indexed)
                    Offstage(
                      offstage: _segment != index,
                      child: TickerMode(
                        enabled: _segment == index,
                        child: body,
                      ),
                    ),
                ],
              ),
            ),
          ],
        ),
      ),
    );

    return widget.embedded ? content : Scaffold(body: content);
  }
}

/// The "My Requests" segment showing the buyer's active intents.
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

        // Error with retry: a real failure must look like a failure, not
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
              // Offline-first: warn when the marketplace refresh failed and
              // these counts/matches were computed against cached data (C1).
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

/// Empty state for the My Requests segment when the buyer has no active
/// intents.
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
