import 'package:equatable/equatable.dart';

import 'package:wereda_connect/features/conflict_resolution/data/models/conflict_event.dart';

/// Base class for conflict resolution BLoC states.
abstract class ConflictState extends Equatable {
  const ConflictState();

  @override
  List<Object?> get props => [];
}

/// No active conflict. Default state.
class ConflictInitial extends ConflictState {
  const ConflictInitial();
}

/// A conflict has been detected and is pending user acknowledgement.
/// The ConflictResolutionScreen should be displayed.
class ConflictPending extends ConflictState {
  final ConflictEvent conflict;

  const ConflictPending(this.conflict);

  @override
  List<Object?> get props => [conflict];
}

/// User has acknowledged the conflict. Dialog should be dismissed.
/// Transitions back to ConflictInitial after a brief moment.
class ConflictResolved extends ConflictState {
  const ConflictResolved();
}

/// An error occurred during conflict processing.
class ConflictError extends ConflictState {
  final String message;

  const ConflictError(this.message);

  @override
  List<Object?> get props => [message];
}
