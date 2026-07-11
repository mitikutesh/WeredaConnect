import 'package:wereda_connect/core/models/message.dart';

/// A conversation as shown in the inbox: the latest message plus the
/// counterpart's contact details (for the Call/Copy actions) and unread count.
class ConversationSummary {
  final String conversationId;
  final String contextType;
  final String contextId;

  /// The other participant (never the current user).
  final String counterpartId;
  final String? counterpartName;
  final String? counterpartPhone;

  /// Preview + timing of the most recent message in the conversation.
  final String lastMessageBody;
  final DateTime lastMessageAt;
  final bool lastMessageFromMe;

  /// Messages the current user has not yet read (0 when caught up).
  final int unreadCount;

  const ConversationSummary({
    required this.conversationId,
    required this.contextType,
    required this.contextId,
    required this.counterpartId,
    required this.counterpartName,
    required this.counterpartPhone,
    required this.lastMessageBody,
    required this.lastMessageAt,
    required this.lastMessageFromMe,
    required this.unreadCount,
  });
}

/// Abstract base for message repositories.
///
/// Extracted so both mobile (Isar + sync queue + local pull) and web
/// (HTTP-only) implementations share the same BLoC interface. Messages are
/// create-only and immutable; a conversation is a client-side grouping.
abstract class BaseMessageRepository {
  /// The deterministic conversation id for a context and a pair of users.
  /// Direction-independent: the two ids are sorted so both participants derive
  /// the same key. MUST match the server's formula in ProcessMessageAsync.
  static String conversationIdFor({
    required String contextType,
    required String contextId,
    required String userA,
    required String userB,
  }) {
    final ids = [userA, userB]..sort();
    return '$contextType:$contextId:${ids[0]}:${ids[1]}';
  }

  /// Send a message. Writes locally first (offline-first) then enqueues a
  /// `message` sync op. Name/phone are stored locally for display but NOT sent —
  /// the server denormalizes them authoritatively.
  Future<Message> sendMessage({
    required String contextType,
    required String contextId,
    required String senderId,
    required String recipientId,
    required String body,
    String? senderName,
    String? senderPhone,
    String? recipientName,
    String? recipientPhone,
  });

  /// All messages in a conversation, oldest first (best-effort; may be empty
  /// offline on web).
  Future<List<Message>> loadThread(String conversationId);

  /// The current user's conversations, most-recent first (best-effort).
  Future<List<ConversationSummary>> loadConversations(String currentUserId);

  /// Total unread messages across all conversations (0 when caught up).
  Future<int> totalUnread(String currentUserId);

  /// Mark a conversation read up to now. Idempotent: implementations skip the
  /// write (and any change event) when the marker already covers the newest
  /// message — the thread bloc calls this on every refresh AND refreshes on
  /// every change event, so a repeated write would loop forever.
  Future<void> markConversationRead(String conversationId);

  /// Pull messages addressed to / from the current user from the server into
  /// the local store (Isar on mobile, the session cache on web). Returns the
  /// number of new messages stored (0 when offline — never throws).
  Future<int> pullMessages(String currentUserId);

  /// Fires whenever the local message store changes (native only; an empty
  /// stream on web). BLoCs listen to this to refresh reactively.
  Stream<void> watchChanges();

  /// Emits each batch of messages addressed to the current user that a pull
  /// newly discovered — i.e. "a message just arrived". The very first
  /// hydration of an empty store is NOT emitted (bootstrapping a device must
  /// not replay the whole history as notifications).
  Stream<List<Message>> watchInbound();
}
