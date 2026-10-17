import 'package:wereda_connect/core/models/message.dart';
import 'package:wereda_connect/features/messaging/domain/repositories/base_message_repository.dart';

/// Group a flat list of messages into per-conversation summaries for the inbox.
///
/// Shared by the native (Isar) and web (HTTP) repositories so grouping,
/// counterpart resolution and unread counting behave identically.
///
/// [lastRead] maps conversationId → the user's last-read timestamp. Pass null
/// on web (no local read store) to suppress unread counts; on native, a
/// conversation absent from the map is treated as never read (all inbound
/// messages count as unread).
List<ConversationSummary> buildConversationSummaries(
  List<Message> messages,
  String me, {
  Map<String, DateTime>? lastRead,
}) {
  final byConversation = <String, List<Message>>{};
  for (final m in messages) {
    byConversation.putIfAbsent(m.conversationId, () => []).add(m);
  }

  final summaries = <ConversationSummary>[];
  byConversation.forEach((conversationId, msgs) {
    msgs.sort((a, b) => a.createdAt.compareTo(b.createdAt));
    final last = msgs.last;
    final lastFromMe = last.senderId == me;

    final counterpartId = lastFromMe ? last.recipientId : last.senderId;
    final counterpartName = lastFromMe ? last.recipientName : last.senderName;

    int unread;
    if (lastRead == null) {
      unread = 0;
    } else {
      final lr = lastRead[conversationId];
      unread = msgs
          .where(
            (m) => m.senderId != me && (lr == null || m.createdAt.isAfter(lr)),
          )
          .length;
    }

    summaries.add(
      ConversationSummary(
        conversationId: conversationId,
        contextType: last.contextType,
        contextId: last.contextId,
        counterpartId: counterpartId,
        counterpartName: counterpartName,
        lastMessageBody: last.body,
        lastMessageAt: last.createdAt,
        lastMessageFromMe: lastFromMe,
        unreadCount: unread,
      ),
    );
  });

  summaries.sort((a, b) => b.lastMessageAt.compareTo(a.lastMessageAt));
  return summaries;
}

/// Parse a `/messages` API item map into a [Message]. Phone numbers are never
/// kept, even from a server that still sends them (ESC-48).
Message parseMessage(Map<String, dynamic> m) {
  return Message()
    ..messageId = m['messageId'] as String? ?? ''
    ..conversationId = m['conversationId'] as String? ?? ''
    ..contextType = m['contextType'] as String? ?? ''
    ..contextId = m['contextId'] as String? ?? ''
    ..senderId = m['senderId'] as String? ?? ''
    ..recipientId = m['recipientId'] as String? ?? ''
    ..senderName = m['senderName'] as String?
    ..recipientName = m['recipientName'] as String?
    ..body = m['body'] as String? ?? ''
    ..createdAt = DateTime.tryParse(m['createdAt'] as String? ?? '')?.toUtc() ??
        DateTime.now().toUtc()
    ..version = (m['version'] as num?)?.toInt() ?? 1;
}
