import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/features/reputation/data/reputation_repository.dart';
import 'package:wereda_connect/features/reputation/domain/models/own_reputation.dart';
import 'package:wereda_connect/features/reputation/domain/models/reputation_event.dart';
import 'package:wereda_connect/features/reputation/presentation/bloc/my_reputation_bloc.dart';
import 'package:wereda_connect/features/reputation/presentation/bloc/my_reputation_event.dart';
import 'package:wereda_connect/features/reputation/presentation/bloc/my_reputation_state.dart';
import 'package:wereda_connect/features/reputation/presentation/widgets/appeal_dialog.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fieldway_lists.dart';
import 'package:wereda_connect/shared/widgets/join_chrome.dart';

/// ESC-31: the signed-in farmer's or trucker's own reputation
/// (reputation-api.md sections 2, 4, 6 and 13): their standing and penalty
/// points with what each threshold does, their rating with what was
/// deducted, and the history of events with expiry and appeal.
///
/// Only ever the user's own data ([userId] is the signed-in user; the
/// server looks every route up under the caller). Buyers have no
/// reputation, so Profile opens this for farmers and truckers only.
///
/// One screen for the phone and the web. Offline it shows the saved copy
/// with its date; an appeal needs a connection and the form says so.
/// While the server's penalties flag is off only the rating is shown.
class MyReputationScreen extends StatefulWidget {
  final String userId;
  final ReputationRepository repository;

  /// Picks the appeal photo; the app's picker when null. Tests inject one.
  final AppealPhotoPicker? pickPhoto;

  const MyReputationScreen({
    required this.userId,
    required this.repository,
    this.pickPhoto,
    super.key,
  });

  @override
  State<MyReputationScreen> createState() => _MyReputationScreenState();
}

class _MyReputationScreenState extends State<MyReputationScreen> {
  late final MyReputationBloc _bloc = MyReputationBloc(
    repository: widget.repository,
    userId: widget.userId,
  )..add(const MyReputationRequested());

  @override
  void dispose() {
    _bloc.close();
    super.dispose();
  }

  Future<void> _refresh() async {
    _bloc.add(const MyReputationRequested(force: true));
    // The indicator stays until the answer (or the saved copy) is in.
    await _bloc.stream
        .firstWhere((s) => s.status != ReputationLoad.loading)
        .timeout(const Duration(seconds: 30), onTimeout: () => _bloc.state);
  }

  Future<void> _appeal(ReputationEvent event) async {
    final appeal = await showAppealDialog(
      context,
      repository: widget.repository,
      userId: widget.userId,
      event: event,
      pickPhoto: widget.pickPhoto,
    );
    if (appeal == null || !mounted) return;
    _bloc.add(MyReputationAppealSent(eventId: event.eventId, appeal: appeal));
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(AppLocalizations.of(context)!.reputationAppealSent),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Scaffold(
      body: SafeArea(
        child: FwContentWidth(
          child: BlocBuilder<MyReputationBloc, MyReputationState>(
            bloc: _bloc,
            builder: (context, state) {
              return RefreshIndicator(
                onRefresh: _refresh,
                child: ListView(
                  physics: const AlwaysScrollableScrollPhysics(),
                  padding: const EdgeInsets.fromLTRB(20, 16, 20, 32),
                  children: [
                    if (Navigator.of(context).canPop()) ...[
                      const Align(
                        alignment: AlignmentDirectional.centerStart,
                        child: JoinBackButton(),
                      ),
                      const SizedBox(height: 12),
                    ],
                    FwPageHeader(
                      padding: EdgeInsets.zero,
                      title: l10n.reputationMyTitle,
                    ),
                    const SizedBox(height: 16),
                    ..._body(context, l10n, state),
                  ],
                ),
              );
            },
          ),
        ),
      ),
    );
  }

  List<Widget> _body(
    BuildContext context,
    AppLocalizations l10n,
    MyReputationState state,
  ) {
    final reputation = state.reputation;
    if (reputation == null) {
      if (state.status == ReputationLoad.loading) {
        return const [
          Padding(
            padding: EdgeInsets.all(32),
            child: Center(child: CircularProgressIndicator()),
          ),
        ];
      }
      return [
        FwInfoNote(
          key: const Key('reputation_unavailable'),
          icon: Icons.cloud_off_outlined,
          text: l10n.reputationOfflineNoCopy,
        ),
        const SizedBox(height: 12),
        Align(
          alignment: AlignmentDirectional.centerStart,
          child: OutlinedButton(
            onPressed: () =>
                _bloc.add(const MyReputationRequested(force: true)),
            child: Text(l10n.retry),
          ),
        ),
      ];
    }

    final fetchedAt = state.fetchedAt;
    return [
      if (state.stale && fetchedAt != null) ...[
        FwInfoNote(
          key: const Key('reputation_saved_copy'),
          icon: Icons.cloud_off_outlined,
          text: l10n.reputationShowingSaved(_day(fetchedAt)),
        ),
        const SizedBox(height: 12),
      ],
      if (reputation.penaltiesEnabled) ...[
        _StandingCard(reputation: reputation),
        const SizedBox(height: 12),
      ],
      _RatingCard(reputation: reputation),
      if (!state.stale && fetchedAt != null) ...[
        const SizedBox(height: 8),
        Text(
          l10n.reputationAsOf(_day(reputation.asOf ?? fetchedAt)),
          style: FwText.body.copyWith(fontSize: 12),
        ),
      ],
      if (reputation.penaltiesEnabled) ...[
        const SizedBox(height: 22),
        FwSectionLabel(l10n.reputationEventsTitle),
        const SizedBox(height: 8),
        ..._events(l10n, state),
      ],
    ];
  }

  List<Widget> _events(AppLocalizations l10n, MyReputationState state) {
    if (state.eventsStatus == ReputationLoad.loading) {
      return const [
        Padding(
          padding: EdgeInsets.all(16),
          child: Center(child: CircularProgressIndicator()),
        ),
      ];
    }
    if (state.eventsStatus == ReputationLoad.unavailable) {
      return [
        FwInfoNote(
          key: const Key('reputation_history_unavailable'),
          icon: Icons.cloud_off_outlined,
          text: l10n.reputationHistoryNeedsConnection,
        ),
      ];
    }
    if (state.events.isEmpty) {
      return [
        FwCard(
          child: Text(
            l10n.reputationEventsEmpty,
            key: const Key('reputation_events_empty'),
            style: FwText.body,
          ),
        ),
      ];
    }
    return [
      for (final event in state.events) ...[
        _EventCard(
          key: ValueKey('reputation_event_${event.eventId}'),
          event: event,
          // An appeal is offered only where the server says it takes one.
          onAppeal: event.appeal.canAppeal ? () => _appeal(event) : null,
        ),
        const SizedBox(height: 10),
      ],
      if (state.nextToken != null)
        Align(
          child: state.loadingMore
              ? const Padding(
                  padding: EdgeInsets.all(8),
                  child: SizedBox(
                    width: 22,
                    height: 22,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  ),
                )
              : Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    if (state.moreFailed)
                      Padding(
                        padding: const EdgeInsets.only(bottom: 6),
                        child: Text(
                          l10n.noInternetConnection,
                          style: FwText.body,
                        ),
                      ),
                    OutlinedButton(
                      key: const Key('reputation_load_more'),
                      onPressed: () =>
                          _bloc.add(const MyReputationMoreRequested()),
                      child: Text(l10n.reputationLoadMore),
                    ),
                  ],
                ),
        ),
    ];
  }
}

/// "Nov 7, 2026" for the Ethiopia calendar day of [instant].
String _day(DateTime instant) =>
    DateFormat.yMMMd().format(dayInEthiopia(instant));

/// One decimal, as ratings are written everywhere.
String _stars(double value) => value.toStringAsFixed(1);

/// The standing: the tier in plain words, the points, what the next
/// threshold does and how points go away. Respectful: it says what
/// happens, never what the person is.
class _StandingCard extends StatelessWidget {
  final OwnReputation reputation;

  const _StandingCard({required this.reputation});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final restriction = reputation.restriction;
    final restricted = reputation.tier == 'restricted' || restriction != null;
    final low = reputation.tier == 'low';
    final warning = reputation.tier == 'warning';
    final lowAt = reputation.lowThreshold;
    final restrictAt = reputation.restrictedThreshold;
    final override = reputation.override;
    final nextExpiry = reputation.nextPointExpiryAt;

    final String title;
    final String body;
    final IconData icon;
    final Color ink;
    final Color ground;
    if (restricted) {
      title = l10n.reputationTierRestricted;
      final until = restriction?.until;
      body = until != null
          ? l10n.reputationRestrictedUntil(_day(until))
          : l10n.reputationRestrictedReview;
      icon = Icons.pause_circle_outline;
      ink = AppTheme.statusError;
      ground = AppTheme.statusErrorContainer;
    } else if (low) {
      title = l10n.reputationLowTag;
      body = l10n.reputationTierLowBody;
      icon = Icons.error_outline;
      ink = AppTheme.statusError;
      ground = AppTheme.statusErrorContainer;
    } else if (warning) {
      title = l10n.reputationTierWarning;
      body = lowAt != null
          ? l10n.reputationWarning(lowAt)
          : l10n.reputationRecoverHint;
      icon = Icons.info_outline;
      ink = AppTheme.statusWarning;
      ground = AppTheme.statusWarningContainer;
    } else {
      title = l10n.reputationTierGood;
      body = l10n.reputationTierGoodBody;
      icon = Icons.check_circle_outline;
      ink = AppTheme.fieldGreen;
      ground = AppTheme.fieldGreenTint;
    }

    final notes = <String>[
      // What comes next, only while it can still be avoided.
      if (!restricted && !low && !warning && lowAt != null)
        l10n.reputationWarning(lowAt),
      if (!restricted && (low || warning) && restrictAt != null)
        l10n.reputationRestrictAt(restrictAt),
      if (reputation.activePoints > 0 && nextExpiry != null)
        l10n.reputationNextExpiry(_day(nextExpiry)),
      if (reputation.activePoints > 0) l10n.reputationRecoverHint,
    ];

    return FwCard(
      key: const Key('reputation_standing'),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Container(
                width: 40,
                height: 40,
                decoration: BoxDecoration(
                  color: ground,
                  borderRadius: BorderRadius.circular(12),
                ),
                child: Icon(icon, size: 22, color: ink),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(title, style: FwText.cardTitle),
                    const SizedBox(height: 2),
                    Text(
                      l10n.reputationMyPoints(reputation.activePoints),
                      key: const Key('reputation_points'),
                      style: FwText.body.copyWith(
                        fontWeight: FontWeight.w700,
                        color: AppTheme.soilDark,
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ),
          const SizedBox(height: 12),
          Text(body, style: FwText.body),
          for (final note in notes) ...[
            const SizedBox(height: 6),
            Text(note, style: FwText.body),
          ],
          if (override != null && override.until != null) ...[
            const SizedBox(height: 10),
            Text(
              override.tag == 'clear'
                  ? l10n.reputationOverrideClear(_day(override.until!))
                  : l10n.reputationOverrideLow(_day(override.until!)),
              style: FwText.body,
            ),
            if (override.reason != null && override.reason!.trim().isNotEmpty)
              Text(override.reason!.trim(), style: FwText.body),
          ],
        ],
      ),
    );
  }
}

/// The rating: the buyers' average stays visible and honest, with what the
/// penalties took off and what buyers see, and the completed orders.
class _RatingCard extends StatelessWidget {
  final OwnReputation reputation;

  const _RatingCard({required this.reputation});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final average = reputation.ratingAverage;
    final deduction = reputation.ratingDeduction ?? 0;
    final effective = reputation.effectiveRating;
    final total = reputation.totalOrders90d;
    return FwCard(
      key: const Key('reputation_rating'),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const Icon(Icons.star_rounded, size: 22, color: AppTheme.money),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  reputation.hasRating
                      ? '${_stars(average!)} · '
                          '${l10n.ratingsCount(reputation.ratingCount)}'
                      : l10n.noRatingsYet,
                  style: FwText.cardTitle,
                ),
              ),
            ],
          ),
          if (reputation.hasRating) ...[
            const SizedBox(height: 8),
            Text(
              // Shown as deducted only when a tenth or more came off.
              deduction >= 0.05
                  ? l10n.reputationRatingBreakdown(
                      _stars(average!),
                      _stars(deduction),
                    )
                  : l10n.reputationRatingFromBuyers(_stars(average!)),
              style: FwText.body,
            ),
            if (deduction >= 0.05 && effective != null) ...[
              const SizedBox(height: 4),
              Text(
                l10n.reputationShownToBuyers(_stars(effective)),
                style: FwText.body,
              ),
            ],
          ],
          if (total > 0) ...[
            const SizedBox(height: 8),
            Text(
              l10n.reputationCompletedOrders(
                reputation.completedOrders90d,
                total,
              ),
              style: FwText.body,
            ),
          ],
        ],
      ),
    );
  }
}

/// One event: what happened and when, its points, when they stop counting
/// and where its appeal stands.
class _EventCard extends StatelessWidget {
  final ReputationEvent event;
  final VoidCallback? onAppeal;

  const _EventCard({required this.event, super.key, this.onAppeal});

  static String _title(AppLocalizations l10n, String code) => switch (code) {
        'DECLINED_REQUEST' => l10n.reputationEventDeclined,
        'RESPONSE_EXPIRED' => l10n.reputationEventExpired,
        'CANCELLED_AFTER_ACCEPT' => l10n.reputationEventCancelled,
        'LATE_CANCEL' => l10n.reputationEventLateCancel,
        'TRIP_CANCELLED_WITH_BOOKINGS' => l10n.reputationEventTripCancelled,
        'NO_SHOW' => l10n.reputationEventNoShow,
        'DELIVERY_OVERDUE' => l10n.reputationEventOverdue,
        'CANCELLED_TRANSPORT_FAILED' =>
          l10n.reputationEventTransportFailedCancel,
        'ORDER_COMPLETED' => l10n.reputationEventCompleted,
        _ => l10n.reputationEventOther,
      };

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final occurredAt = event.occurredAt;
    final expires = event.pointsExpireAt;
    final appeal = event.appeal;
    final completed = event.code == 'ORDER_COMPLETED';
    final deadline = appeal.deadlineAt;

    final String? appealLine = switch (appeal.status) {
      'pending' => l10n.reputationAppealPending,
      'review' => l10n.reputationAppealReview,
      'excused' => l10n.reputationAppealAccepted,
      'upheld' => l10n.reputationAppealUpheld,
      _ => null,
    };

    final lines = <String>[
      if (!completed && event.status == 'excused')
        l10n.reputationEventStatusExcused
      else if (!completed && event.status == 'expired')
        l10n.reputationEventStatusExpired
      else if (!completed && event.points > 0) ...[
        if (event.earnedBack > 0 && event.remainingPoints < event.points)
          l10n.reputationEventRemaining(event.remainingPoints),
        if (expires != null) l10n.reputationPointExpires(_day(expires)),
      ],
      if (appealLine != null) appealLine,
      if (appeal.note != null) l10n.reputationAppealNote(appeal.note!),
      if (onAppeal != null && deadline != null)
        l10n.reputationAppealBy(_day(deadline)),
    ];

    return FwCard(
      padding: const EdgeInsets.all(14),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(_title(l10n, event.code), style: FwText.cardTitle),
                    if (occurredAt != null) ...[
                      const SizedBox(height: 2),
                      Text(
                        _day(occurredAt),
                        style: FwText.body.copyWith(fontSize: 12),
                      ),
                    ],
                  ],
                ),
              ),
              if (!completed) ...[
                const SizedBox(width: 10),
                FwPill(
                  label: event.points > 0
                      ? l10n.reputationEventPoints(event.points)
                      : l10n.reputationEventNoPoints,
                  background: event.points > 0 && event.isActive
                      ? AppTheme.actionCream
                      : AppTheme.segmentTrack,
                  foreground: event.points > 0 && event.isActive
                      ? AppTheme.money
                      : AppTheme.unselectedNav,
                ),
              ],
            ],
          ),
          for (final line in lines) ...[
            const SizedBox(height: 6),
            Text(line, style: FwText.body),
          ],
          if (onAppeal != null) ...[
            const SizedBox(height: 8),
            Align(
              alignment: AlignmentDirectional.centerEnd,
              child: OutlinedButton(
                key: Key('reputation_appeal_${event.eventId}'),
                onPressed: onAppeal,
                child: Text(l10n.reputationAppeal),
              ),
            ),
          ],
        ],
      ),
    );
  }
}
