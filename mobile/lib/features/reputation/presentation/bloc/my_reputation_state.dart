import 'package:equatable/equatable.dart';

import 'package:wereda_connect/features/reputation/domain/models/own_reputation.dart';
import 'package:wereda_connect/features/reputation/domain/models/reputation_event.dart';

/// Where a part of the My reputation screen stands.
enum ReputationLoad {
  loading,
  ready,

  /// No connection, and nothing is saved on the device.
  unavailable,

  /// The server answered with an error (not a connection problem), and
  /// nothing is saved on the device.
  failed,
}

/// The My reputation screen: the user's own standing and, apart from it,
/// their event history (the standing still shows when the history cannot
/// be read).
class MyReputationState extends Equatable {
  final ReputationLoad status;
  final OwnReputation? reputation;

  /// When [reputation] was fetched.
  final DateTime? fetchedAt;

  /// [reputation] is the saved copy: the request failed.
  final bool stale;

  final ReputationLoad eventsStatus;
  final List<ReputationEvent> events;

  /// The next page's token; null on the last page.
  final String? nextToken;
  final bool loadingMore;

  /// The last "show more" failed (offline): the button stays to try again.
  final bool moreFailed;

  const MyReputationState({
    this.status = ReputationLoad.loading,
    this.reputation,
    this.fetchedAt,
    this.stale = false,
    this.eventsStatus = ReputationLoad.loading,
    this.events = const [],
    this.nextToken,
    this.loadingMore = false,
    this.moreFailed = false,
  });

  MyReputationState copyWith({
    ReputationLoad? status,
    OwnReputation? reputation,
    DateTime? fetchedAt,
    bool? stale,
    ReputationLoad? eventsStatus,
    List<ReputationEvent>? events,
    String? Function()? nextToken,
    bool? loadingMore,
    bool? moreFailed,
  }) =>
      MyReputationState(
        status: status ?? this.status,
        reputation: reputation ?? this.reputation,
        fetchedAt: fetchedAt ?? this.fetchedAt,
        stale: stale ?? this.stale,
        eventsStatus: eventsStatus ?? this.eventsStatus,
        events: events ?? this.events,
        nextToken: nextToken == null ? this.nextToken : nextToken(),
        loadingMore: loadingMore ?? this.loadingMore,
        moreFailed: moreFailed ?? this.moreFailed,
      );

  @override
  List<Object?> get props => [
        status,
        reputation,
        fetchedAt,
        stale,
        eventsStatus,
        events,
        nextToken,
        loadingMore,
        moreFailed,
      ];
}
