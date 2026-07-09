/// Web-safe ConversationRead model (no Isar dependency).
///
/// Mirrors [core/database/schemas/conversation_read.dart]. On web there is no
/// local Isar store, so read-state is not persisted (web relies on refresh);
/// this type exists only to keep the shared model surface compiling.
class ConversationRead {
  int id = 0;

  late String conversationId;
  late DateTime lastReadAt;
}
