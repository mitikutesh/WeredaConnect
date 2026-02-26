import 'package:isar/isar.dart';

part 'payment_intent.g.dart';

/// Represents a locally queued escrow payment intent for offline support.
///
/// When a buyer initiates a payment without connectivity, the intent is
/// stored here with [IntentStatus.pendingSync] and processed by
/// [EscrowSyncService] when connectivity returns.
///
/// This schema is local-only and transient. Completed/failed intents
/// are cleaned up after 24 hours (MH2 security requirement).
@collection
class PaymentIntent {
  /// Isar auto-generated ID (internal use only)
  Id id = Isar.autoIncrement;

  /// Client-generated idempotency key.
  /// Format: {buyerId}:{deliveryId}:initiate:{timestamp}
  ///
  /// Generated ONCE at intent creation and reused on all retry attempts.
  /// This is critical for preventing double-charges (MH2).
  @Index(unique: true)
  late String idempotencyKey;

  /// Delivery this payment covers
  @Index()
  late String deliveryId;

  /// Buyer initiating payment (must match JWT sub claim)
  late String buyerId;

  /// Farmer receiving payment
  late String farmerId;

  /// Amount in ETB (100-500,000)
  late double amount;

  /// Current sync status of this intent
  @Index()
  @Enumerated(EnumType.name)
  late IntentStatus status;

  /// When the intent was created (UTC)
  late DateTime createdAt;

  /// Number of sync attempts (for exponential backoff)
  int retryCount = 0;

  /// Last sync attempt time (for exponential backoff calculation)
  DateTime? lastAttemptAt;

  /// Error message from last failed attempt (not logged externally - MH7)
  String? lastError;
}

/// Sync status of a payment intent in the offline queue.
enum IntentStatus {
  /// Queued locally, waiting for connectivity
  pendingSync,

  /// Currently being sent to server
  syncing,

  /// Server confirmed, escrow created
  completed,

  /// Server rejected (validation error, etc.)
  failed,

  /// User cancelled before sync
  cancelled,
}
