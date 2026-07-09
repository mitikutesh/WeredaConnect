/// Web-safe Message model (no Isar dependency).
///
/// Mirrors [core/database/schemas/message.dart] without Isar annotations or
/// generated code, so it compiles to JavaScript without 64-bit integer errors.
class Message {
  int id = 0;

  late String messageId;
  late String conversationId;
  late String contextType;
  late String contextId;
  late String senderId;
  late String recipientId;
  String? senderName;
  String? senderPhone;
  String? recipientName;
  String? recipientPhone;
  late String body;
  late DateTime createdAt;
  late int version;
}
