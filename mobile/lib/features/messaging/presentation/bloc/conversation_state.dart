import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/message.dart';

/// Base class for conversation (thread) states.
abstract class ConversationState extends Equatable {
  const ConversationState();

  @override
  List<Object?> get props => [];
}

class ConversationInitial extends ConversationState {
  const ConversationInitial();
}

class ConversationLoading extends ConversationState {
  const ConversationLoading();
}

class ConversationLoaded extends ConversationState {
  final List<Message> messages;

  const ConversationLoaded({required this.messages});

  @override
  List<Object?> get props => [messages.map((m) => m.messageId).toList()];
}

class ConversationError extends ConversationState {
  final String message;
  final AppErrorCode code;

  const ConversationError({
    required this.message,
    this.code = AppErrorCode.unknown,
  });

  @override
  List<Object?> get props => [message, code];
}
