import 'dart:async';

import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/conflict_resolution/data/services/conflict_notification_service.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_event.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_state.dart';

/// BLoC that manages conflict resolution UI state.
///
/// Subscribes to [ConflictNotificationService] stream and emits
/// [ConflictPending] when a conflict is detected. The conflict has
/// already been resolved (server wins) — this BLoC only manages
/// the user notification flow.
///
/// State flow:
///   ConflictInitial → ConflictPending → ConflictResolved → ConflictInitial
class ConflictBloc extends Bloc<ConflictBlocEvent, ConflictState> {
  StreamSubscription<dynamic>? _conflictSubscription;

  ConflictBloc({
    required ConflictNotificationService notificationService,
  }) : super(const ConflictInitial()) {
    on<ConflictDetected>(_onConflictDetected);
    on<AcceptServerVersion>(_onAcceptServerVersion);
    on<DismissConflict>(_onDismissConflict);

    // Subscribe to conflict notification stream
    _conflictSubscription = notificationService.onConflict.listen(
      (event) => add(ConflictDetected(event)),
    );
  }

  void _onConflictDetected(
    ConflictDetected event,
    Emitter<ConflictState> emit,
  ) {
    emit(ConflictPending(event.conflict));
  }

  void _onAcceptServerVersion(
    AcceptServerVersion event,
    Emitter<ConflictState> emit,
  ) {
    emit(const ConflictResolved());
    emit(const ConflictInitial());
  }

  void _onDismissConflict(
    DismissConflict event,
    Emitter<ConflictState> emit,
  ) {
    emit(const ConflictResolved());
    emit(const ConflictInitial());
  }

  @override
  Future<void> close() {
    _conflictSubscription?.cancel();
    return super.close();
  }
}
