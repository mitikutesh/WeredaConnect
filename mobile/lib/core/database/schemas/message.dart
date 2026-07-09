import 'package:isar/isar.dart';

part 'message.g.dart';

/// A single chat message between the two parties of a marketplace context
/// (crop / order / trip / delivery).
///
/// Offline-first: written locally in Isar first, then synced via the sync
/// queue (`entityType: 'message'`, create-only). A conversation is a
/// client-side view — messages grouped by the deterministic [conversationId]
/// `"{contextType}:{contextId}:{userIdLow}:{userIdHigh}"`. The server
/// denormalizes sender/recipient name + phone authoritatively, so those fields
/// are trustworthy after a sync and power the Call/Copy actions.
@collection
class Message {
  /// Isar auto-generated ID (internal use only).
  Id id = Isar.autoIncrement;

  /// Client-generated UUID; the server's partition key. Unique so a re-sync of
  /// the same message is a local upsert, never a duplicate row.
  @Index(unique: true)
  late String messageId;

  /// Deterministic conversation grouping key.
  @Index()
  late String conversationId;

  /// Context kind: `crop` | `order` | `trip` | `delivery`.
  late String contextType;

  /// Id of the context entity (cropId / orderId / tripId / deliveryId).
  late String contextId;

  /// User who sent the message.
  @Index()
  late String senderId;

  /// The counterpart the message is addressed to.
  @Index()
  late String recipientId;

  /// Sender display name (denormalized).
  String? senderName;

  /// Sender phone in E.164 (denormalized, for Call/Copy).
  String? senderPhone;

  /// Recipient display name (denormalized).
  String? recipientName;

  /// Recipient phone in E.164 (denormalized, for Call/Copy).
  String? recipientPhone;

  /// Message text.
  late String body;

  /// Compose time (UTC). Threads sort by this ascending.
  @Index()
  late DateTime createdAt;

  /// Version number (always 1 — messages are immutable).
  late int version;
}
