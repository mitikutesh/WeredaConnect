import 'dart:async';

import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/features/messaging/domain/conversation_context_resolver.dart';
import 'package:wereda_connect/features/messaging/domain/repositories/base_message_repository.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_event.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_state.dart';

/// BLoC for the conversations inbox + the global unread badge.
///
/// On [LoadInbox] it pulls from the server (best-effort) then reads the local
/// store; a local-store change (send, or a pulled inbound message) re-reads
/// without a network round-trip. Safe to keep app-global — it only reads.
class InboxBloc extends Bloc<InboxEvent, InboxState> {
  /// How often to poll the server for new messages while the app is open.
  /// One tiny GET per minute keeps chat feeling live without hurting 3G data
  /// budgets; background delivery is FCM's job, not the poll's.
  static const Duration pollInterval = Duration(seconds: 60);

  final BaseMessageRepository _repository;

  BaseMessageRepository get repository => _repository;

  /// Resolves what each thread is about (linked crop/trip). Exposed here the
  /// same way [repository] is, so thread screens and inbox tiles share one
  /// cached instance. Null in tests / when context labels aren't wired.
  final ConversationContextResolver? contextResolver;

  StreamSubscription<void>? _changes;
  Timer? _pollTimer;
  String? _userId;

  InboxBloc({required BaseMessageRepository repository, this.contextResolver})
      : _repository = repository,
        super(const InboxInitial()) {
    on<LoadInbox>(_onLoad);
    on<InboxRefreshRequested>(_onRefresh);
    on<InboxPollRequested>(_onPoll);

    _changes = _repository.watchChanges().listen((_) {
      if (_userId != null) add(const InboxRefreshRequested());
    });
  }

  Future<void> _onLoad(LoadInbox event, Emitter<InboxState> emit) async {
    _userId = event.userId;
    _pollTimer ??= Timer.periodic(
      pollInterval,
      (_) => add(const InboxPollRequested()),
    );
    emit(const InboxLoading());
    await _load(emit, pull: true);
  }

  Future<void> _onRefresh(
    InboxRefreshRequested event,
    Emitter<InboxState> emit,
  ) async {
    await _load(emit, pull: false);
  }

  Future<void> _onPoll(
    InboxPollRequested event,
    Emitter<InboxState> emit,
  ) async {
    await _load(emit, pull: true, silent: true);
  }

  Future<void> _load(
    Emitter<InboxState> emit, {
    required bool pull,
    bool silent = false,
  }) async {
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
      // A failed background poll must not blank a healthy inbox.
      if (!silent) {
        emit(InboxError(message: e.toString(), code: mapErrorToCode(e)));
      }
    }
  }

  @override
  Future<void> close() {
    _changes?.cancel();
    _pollTimer?.cancel();
    return super.close();
  }
}
