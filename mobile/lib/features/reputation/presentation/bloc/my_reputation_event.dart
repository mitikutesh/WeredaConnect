import 'dart:async';

import 'package:equatable/equatable.dart';

import 'package:wereda_connect/features/reputation/domain/models/reputation_event.dart';

abstract class MyReputationEvent extends Equatable {
  const MyReputationEvent();

  @override
  List<Object?> get props => [];
}

/// Load the user's own reputation and the first page of their events.
/// [force] asks the server even when the saved answer is fresh
/// (pull-to-refresh). [done] completes when the load is over, whatever came
/// of it, so the refresh indicator knows when to stop.
class MyReputationRequested extends MyReputationEvent {
  final bool force;
  final Completer<void>? done;

  const MyReputationRequested({this.force = false, this.done});

  @override
  List<Object?> get props => [force];
}

/// Load the next page of events.
class MyReputationMoreRequested extends MyReputationEvent {
  const MyReputationMoreRequested();
}

/// The appeal for [eventId] reached the server: show its new state.
class MyReputationAppealSent extends MyReputationEvent {
  final String eventId;
  final ReputationAppeal appeal;

  const MyReputationAppealSent({required this.eventId, required this.appeal});

  @override
  List<Object?> get props => [eventId, appeal.status];
}
