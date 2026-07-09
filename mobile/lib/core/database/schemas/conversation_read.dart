import 'package:isar/isar.dart';

part 'conversation_read.g.dart';

/// Per-device read marker for a conversation. Local-only (never synced): unread
/// state is intentionally kept off the server to avoid a two-writer merge
/// conflict on an immutable, create-only message model.
///
/// A conversation's unread count is the number of messages in it that the
/// current user did NOT send and whose `createdAt` is after [lastReadAt].
@collection
class ConversationRead {
  Id id = Isar.autoIncrement;

  /// The conversation this marker is for.
  @Index(unique: true)
  late String conversationId;

  /// The newest message timestamp the user has seen in this conversation (UTC).
  late DateTime lastReadAt;
}
