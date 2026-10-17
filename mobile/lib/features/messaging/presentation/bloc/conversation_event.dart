import 'package:equatable/equatable.dart';

/// Base class for conversation (thread) events.
abstract class ConversationEvent extends Equatable {
  const ConversationEvent();

  @override
  List<Object?> get props => [];
}

/// Open a conversation thread and mark it read.
class LoadThread extends ConversationEvent {
  final String conversationId;

  const LoadThread(this.conversationId);

  @override
  List<Object?> get props => [conversationId];
}

/// Re-read the thread from the local store (fired when it changes).
class ThreadRefreshRequested extends ConversationEvent {
  const ThreadRefreshRequested();
}

/// Send a message in this conversation's context.
class SendMessageRequested extends ConversationEvent {
  final String contextType;
  final String contextId;
  final String senderId;
  final String? senderName;
  final String recipientId;
  final String? recipientName;
  final String body;

  const SendMessageRequested({
    required this.contextType,
    required this.contextId,
    required this.senderId,
    required this.recipientId,
    required this.body,
    this.senderName,
    this.recipientName,
  });

  @override
  List<Object?> get props => [
        contextType,
        contextId,
        senderId,
        recipientId,
        body,
      ];
}
