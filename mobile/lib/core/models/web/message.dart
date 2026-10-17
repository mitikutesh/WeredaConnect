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

  /// No longer set (ESC-48): the order holds the numbers while its contact is open.
  String? senderPhone;
  String? recipientName;

  /// No longer set (ESC-48), see [senderPhone].
  String? recipientPhone;
  late String body;
  late DateTime createdAt;
  late int version;
}
