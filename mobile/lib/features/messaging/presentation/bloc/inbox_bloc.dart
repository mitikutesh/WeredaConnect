import 'dart:async';

import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/features/messaging/domain/repositories/base_message_repository.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_event.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_state.dart';

/// BLoC for the conversations inbox + the global unread badge.
///
/// On [LoadInbox] it pulls from the server (best-effort) then reads the local
/// store; a local-store change (send, or a pulled inbound message) re-reads
/// without a network round-trip. Safe to keep app-global — it only reads.
class InboxBloc extends Bloc<InboxEvent, InboxState> {
  final BaseMessageRepository _repository;

  BaseMessageRepository get repository => _repository;

  StreamSubscription<void>? _changes;
  String? _userId;

  InboxBloc({required BaseMessageRepository repository})
      : _repository = repository,
        super(const InboxInitial()) {
    on<LoadInbox>(_onLoad);
    on<InboxRefreshRequested>(_onRefresh);

    _changes = _repository.watchChanges().listen((_) {
      if (_userId != null) add(const InboxRefreshRequested());
    });
  }

  Future<void> _onLoad(LoadInbox event, Emitter<InboxState> emit) async {
    _userId = event.userId;
    emit(const InboxLoading());
    await _load(emit, pull: true);
  }

  Future<void> _onRefresh(
    InboxRefreshRequested event,
    Emitter<InboxState> emit,
  ) async {
    await _load(emit, pull: false);
  }

  Future<void> _load(Emitter<InboxState> emit, {required bool pull}) async {
    final userId = _userId;
    if (userId == null) return;
    try {
      if (pull) {
        await _repository.pullMessages(userId); // best-effort; swallows offline
      }
      final conversations = await _repository.loadConversations(userId);
      final totalUnread =
          conversations.fold<int>(0, (sum, c) => sum + c.unreadCount);
      emit(InboxLoaded(conversations: conversations, totalUnread: totalUnread));
    } on Object catch (e) {
      emit(InboxError(message: e.toString(), code: mapErrorToCode(e)));
    }
  }

  @override
  Future<void> close() {
    _changes?.cancel();
    return super.close();
  }
}
