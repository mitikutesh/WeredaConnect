import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';

/// Base class for buyer intent states.
abstract class BuyerIntentState extends Equatable {
  const BuyerIntentState();

  @override
  List<Object?> get props => [];
}

/// Initial state before any events are processed.
class BuyerIntentInitial extends BuyerIntentState {
  const BuyerIntentInitial();
}

/// Loading state while processing an event.
class BuyerIntentLoading extends BuyerIntentState {
  const BuyerIntentLoading();
}

/// Active intents loaded for the home screen section.
///
/// [isStale] is true when the marketplace refresh failed and the matches/counts
/// were computed against cached data (offline-first) — the UI can warn the buyer
/// that results may be out of date.
class ActiveIntentsLoaded extends BuyerIntentState {
  final List<BuyerIntent> intents;
  final bool isStale;

  const ActiveIntentsLoaded({required this.intents, this.isStale = false});

  @override
  List<Object?> get props => [intents, isStale];
}

/// Match results loaded for a specific intent.
class MatchResultsLoaded extends BuyerIntentState {
  final BuyerIntent intent;
  final List<CropMatchResult> matches;
  final bool isStale;

  const MatchResultsLoaded({
    required this.intent,
    required this.matches,
    this.isStale = false,
  });

  @override
  List<Object?> get props => [intent, matches, isStale];
}

/// A new buyer intent was created successfully.
class BuyerIntentCreated extends BuyerIntentState {
  final BuyerIntent intent;
  final List<CropMatchResult> matches;
  final bool isStale;

  const BuyerIntentCreated({
    required this.intent,
    required this.matches,
    this.isStale = false,
  });

  @override
  List<Object?> get props => [intent, matches, isStale];
}

/// An error occurred while processing an event. [code] drives the localized,
/// user-facing message; [message] is the raw detail, kept for logging/special
/// cases (do NOT show it directly to users).
class BuyerIntentError extends BuyerIntentState {
  final String message;
  final AppErrorCode code;

  const BuyerIntentError({
    required this.message,
    this.code = AppErrorCode.unknown,
  });

  @override
  List<Object?> get props => [message, code];
}
