import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/features/messaging/domain/repositories/base_message_repository.dart';

/// Base class for inbox states.
abstract class InboxState extends Equatable {
  const InboxState();

  @override
  List<Object?> get props => [];
}

class InboxInitial extends InboxState {
  const InboxInitial();
}

class InboxLoading extends InboxState {
  const InboxLoading();
}

class InboxLoaded extends InboxState {
  final List<ConversationSummary> conversations;
  final int totalUnread;

  const InboxLoaded({required this.conversations, required this.totalUnread});

  @override
  List<Object?> get props => [
        // Include preview/time/unread per conversation, not just the id set:
        // a follow-up message changes the last-message preview without changing
        // the id set, and equal-state suppression would leave a stale preview.
        conversations
            .map(
              (c) =>
                  '${c.conversationId}|${c.lastMessageBody}|${c.lastMessageAt.toIso8601String()}|${c.unreadCount}',
            )
            .toList(),
        totalUnread,
      ];
}

class InboxError extends InboxState {
  final String message;
  final AppErrorCode code;

  const InboxError({required this.message, this.code = AppErrorCode.unknown});

  @override
  List<Object?> get props => [message, code];
}
