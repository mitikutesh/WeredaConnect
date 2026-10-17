import 'dart:async';

import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/features/messaging/domain/repositories/base_message_repository.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/conversation_event.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/conversation_state.dart';

/// BLoC for a single conversation thread. Route-scoped (created per thread
/// screen) so its reactive subscription and state never leak into sibling
/// screens. Sending is optimistic — the message is written locally first, so
/// it appears immediately even offline.
class ConversationBloc extends Bloc<ConversationEvent, ConversationState> {
  final BaseMessageRepository _repository;

  BaseMessageRepository get repository => _repository;

  StreamSubscription<void>? _changes;
  String? _conversationId;

  ConversationBloc({required BaseMessageRepository repository})
      : _repository = repository,
        super(const ConversationInitial()) {
    on<LoadThread>(_onLoad);
    on<ThreadRefreshRequested>(_onRefresh);
    on<SendMessageRequested>(_onSend);

    _changes = _repository.watchChanges().listen((_) {
      if (_conversationId != null) add(const ThreadRefreshRequested());
    });
  }

  Future<void> _onLoad(
    LoadThread event,
    Emitter<ConversationState> emit,
  ) async {
    _conversationId = event.conversationId;
    emit(const ConversationLoading());
    await _reload(emit);
  }

  Future<void> _onRefresh(
    ThreadRefreshRequested event,
    Emitter<ConversationState> emit,
  ) async {
    await _reload(emit);
  }

  Future<void> _onSend(
    SendMessageRequested event,
    Emitter<ConversationState> emit,
  ) async {
    try {
      await _repository.sendMessage(
        contextType: event.contextType,
        contextId: event.contextId,
        senderId: event.senderId,
        senderName: event.senderName,
        recipientId: event.recipientId,
        recipientName: event.recipientName,
        body: event.body,
      );
      await _reload(emit);
    } on Object catch (e) {
      emit(ConversationError(message: e.toString(), code: mapErrorToCode(e)));
    }
  }

  Future<void> _reload(Emitter<ConversationState> emit) async {
    final conversationId = _conversationId;
    if (conversationId == null) return;
    try {
      final messages = await _repository.loadThread(conversationId);
      // Viewing the thread clears its unread count.
      await _repository.markConversationRead(conversationId);
      emit(ConversationLoaded(messages: messages));
    } on Object catch (e) {
      emit(ConversationError(message: e.toString(), code: mapErrorToCode(e)));
    }
  }

  @override
  Future<void> close() {
    _changes?.cancel();
    return super.close();
  }
}
