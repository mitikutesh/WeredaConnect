import 'package:equatable/equatable.dart';

import 'package:wereda_connect/features/conflict_resolution/data/models/conflict_event.dart'
    as model;

/// Base class for conflict resolution BLoC events.
abstract class ConflictBlocEvent extends Equatable {
  const ConflictBlocEvent();

  @override
  List<Object?> get props => [];
}

/// A sync conflict was detected and auto-resolved.
/// Triggers the UI to show the conflict dialog.
class ConflictDetected extends ConflictBlocEvent {
  final model.ConflictEvent conflict;

  const ConflictDetected(this.conflict);

  @override
  List<Object?> get props => [conflict];
}

/// User acknowledged the conflict by tapping "OK, Got It".
/// Dismisses the dialog and returns to ConflictInitial.
class AcceptServerVersion extends ConflictBlocEvent {
  const AcceptServerVersion();
}

/// User dismissed the conflict dialog without explicit acceptance.
/// Same effect as AcceptServerVersion (server already won), but tracked
/// separately for analytics.
class DismissConflict extends ConflictBlocEvent {
  const DismissConflict();
}
