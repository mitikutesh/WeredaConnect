import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/database/schemas/buyer_intent.dart';
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
class ActiveIntentsLoaded extends BuyerIntentState {
  final List<BuyerIntent> intents;

  const ActiveIntentsLoaded({required this.intents});

  @override
  List<Object?> get props => [intents];
}

/// Match results loaded for a specific intent.
class MatchResultsLoaded extends BuyerIntentState {
  final BuyerIntent intent;
  final List<CropMatchResult> matches;

  const MatchResultsLoaded({
    required this.intent,
    required this.matches,
  });

  @override
  List<Object?> get props => [intent, matches];
}

/// A new buyer intent was created successfully.
class BuyerIntentCreated extends BuyerIntentState {
  final BuyerIntent intent;
  final List<CropMatchResult> matches;

  const BuyerIntentCreated({
    required this.intent,
    required this.matches,
  });

  @override
  List<Object?> get props => [intent, matches];
}

/// An error occurred while processing an event.
class BuyerIntentError extends BuyerIntentState {
  final String message;

  const BuyerIntentError({required this.message});

  @override
  List<Object?> get props => [message];
}
