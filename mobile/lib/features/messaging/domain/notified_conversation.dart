import 'package:wereda_connect/features/messaging/domain/repositories/base_message_repository.dart';

/// The conversation a tapped message notification names (FUP-90): the
/// thread id, its marketplace context and the sender, as
/// `MessageStreamFunction` puts them in the notification data
/// (messaging.md, Push).
class NotifiedConversation {
  const NotifiedConversation({
    required this.conversationId,
    required this.contextType,
    required this.contextId,
    required this.senderId,
  });

  final String conversationId;
  final String contextType;
  final String contextId;
  final String senderId;

  /// Whether [userId] is the recipient: the thread id is the deterministic
  /// one for this context and the pair of the sender and [userId], and
  /// [userId] is not the sender. A notification left in the tray for another
  /// account on a shared phone (FUP-121), or one whose ids do not add up, is
  /// not theirs.
  bool isFor(String userId) =>
      userId.isNotEmpty &&
      userId != senderId &&
      conversationId ==
          BaseMessageRepository.conversationIdFor(
            contextType: contextType,
            contextId: contextId,
            userA: userId,
            userB: senderId,
          );
}
