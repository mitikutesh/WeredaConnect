import 'package:flutter/foundation.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/reputation/data/reputation_repository.dart';
import 'package:wereda_connect/features/reputation/presentation/bloc/my_reputation_event.dart';
import 'package:wereda_connect/features/reputation/presentation/bloc/my_reputation_state.dart';

/// ESC-31: drives the My reputation screen for the signed-in farmer or
/// trucker [userId]. Route-local: the screen creates one and closes it.
///
/// Only the user's own data: every read goes through
/// [ReputationRepository], which has no route for anyone else's.
class MyReputationBloc extends Bloc<MyReputationEvent, MyReputationState> {
  MyReputationBloc({
    required ReputationRepository repository,
    required this.userId,
  })  : _repository = repository,
        super(const MyReputationState()) {
    on<MyReputationRequested>(_onRequested);
    on<MyReputationMoreRequested>(_onMoreRequested);
    on<MyReputationAppealSent>(_onAppealSent);
  }

  final ReputationRepository _repository;
  final String userId;

  Future<void> _onRequested(
    MyReputationRequested event,
    Emitter<MyReputationState> emit,
  ) async {
    try {
      await _load(event, emit);
    } finally {
      final done = event.done;
      if (done != null && !done.isCompleted) done.complete();
    }
  }

  Future<void> _load(
    MyReputationRequested event,
    Emitter<MyReputationState> emit,
  ) async {
    // A refresh keeps what is on screen until the new answer is here.
    if (state.reputation == null) {
      emit(state.copyWith(status: ReputationLoad.loading));
    }
    try {
      final own = await _repository.loadOwn(userId, force: event.force);
      emit(
        state.copyWith(
          status: ReputationLoad.ready,
          reputation: own.reputation,
          fetchedAt: own.fetchedAt,
          stale: own.stale,
        ),
      );
    } on Object catch (e) {
      debugPrint('[MyReputationBloc] own reputation unavailable: $e');
      if (state.reputation == null) {
        emit(state.copyWith(status: ReputationLoad.unavailable));
      } else {
        emit(state.copyWith(stale: true));
      }
      return;
    }

    // While the server's penalties flag is off there is no history to show
    // and none is asked for.
    if (!(state.reputation?.penaltiesEnabled ?? false)) {
      emit(
        state.copyWith(
          eventsStatus: ReputationLoad.ready,
          events: const [],
          nextToken: () => null,
        ),
      );
      return;
    }
    try {
      final result = await _repository.loadEvents(userId);
      emit(
        state.copyWith(
          eventsStatus: ReputationLoad.ready,
          events: result.page.events,
          nextToken: () => result.page.nextToken,
          moreFailed: false,
        ),
      );
    } on Object catch (e) {
      debugPrint('[MyReputationBloc] events unavailable: $e');
      if (state.eventsStatus != ReputationLoad.ready) {
        emit(state.copyWith(eventsStatus: ReputationLoad.unavailable));
      }
    }
  }

  Future<void> _onMoreRequested(
    MyReputationMoreRequested event,
    Emitter<MyReputationState> emit,
  ) async {
    final token = state.nextToken;
    if (token == null || state.loadingMore) return;
    emit(state.copyWith(loadingMore: true, moreFailed: false));
    try {
      final result = await _repository.loadEvents(userId, nextToken: token);
      final known = {for (final e in state.events) e.eventId};
      emit(
        state.copyWith(
          events: [
            ...state.events,
            ...result.page.events.where((e) => !known.contains(e.eventId)),
          ],
          nextToken: () => result.page.nextToken,
          loadingMore: false,
        ),
      );
    } on Object catch (e) {
      debugPrint('[MyReputationBloc] next page failed: $e');
      emit(state.copyWith(loadingMore: false, moreFailed: true));
    }
  }

  void _onAppealSent(
    MyReputationAppealSent event,
    Emitter<MyReputationState> emit,
  ) {
    emit(
      state.copyWith(
        events: [
          for (final e in state.events)
            e.eventId == event.eventId ? e.withAppeal(event.appeal) : e,
        ],
      ),
    );
  }
}
