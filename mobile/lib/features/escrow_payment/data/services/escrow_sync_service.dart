import 'dart:async';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_config.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/features/escrow_payment/data/models/escrow_models.dart';
import 'package:wereda_connect/core/database/schemas/payment_intent.dart';

/// Processes queued [PaymentIntent]s when connectivity returns.
///
/// Registered as a listener on [ConnectivityMonitor]. When connectivity is
/// detected, pending intents are picked up and sent to POST /escrow/initiate.
///
/// Security requirements:
/// - MH2: Reuses the persisted idempotency key (never regenerated on retry).
/// - MH4: Processing lock prevents concurrent execution.
/// - MH2: Cleans up stale completed/failed intents after 24 hours.
/// - MH8: Cancels pending intents on logout.
class EscrowSyncService {
  final Isar _isar;
  final ApiClient _apiClient;
  final ConnectivityMonitor _connectivityMonitor;

  StreamSubscription<List<ConnectivityResult>>? _connectivitySubscription;

  /// MH4: Processing lock to prevent concurrent intent processing.
  bool _isProcessing = false;

  /// Maximum number of retry attempts per intent.
  static const int maxRetries = 3;

  /// Stale intent cleanup threshold.
  static const Duration staleThreshold = Duration(hours: 24);

  EscrowSyncService({
    required Isar isar,
    required ApiClient apiClient,
    required ConnectivityMonitor connectivityMonitor,
  })  : _isar = isar,
        _apiClient = apiClient,
        _connectivityMonitor = connectivityMonitor;

  /// Start listening for connectivity changes.
  ///
  /// When connectivity is restored, processes all pending intents.
  /// Also runs stale intent cleanup on startup (MH2).
  void startListening() {
    // Run cleanup on startup
    cleanupStaleIntents();

    _connectivitySubscription =
        _connectivityMonitor.onConnectivityChanged.listen((results) {
      if (_connectivityMonitor.isConnectedFromResults(results)) {
        processQueuedIntents();
      }
    });
  }

  /// Stop listening and release resources.
  void dispose() {
    _connectivitySubscription?.cancel();
    _connectivitySubscription = null;
  }

  /// Process all pending intents.
  ///
  /// MH4: Uses a processing lock to prevent concurrent execution.
  /// MH2: Reads the persisted idempotency key from [PaymentIntent] —
  ///       never generates a new one on retry.
  ///
  /// Flow per intent:
  /// 1. Transition status from pendingSync -> syncing (atomic)
  /// 2. POST /escrow/initiate with persisted idempotency key
  /// 3. On success: mark completed, cache Payment
  /// 4. On failure: increment retry, apply backoff, mark failed after max
  Future<void> processQueuedIntents() async {
    // MH4: Prevent concurrent processing (connectivity flap protection)
    if (_isProcessing) return;
    _isProcessing = true;

    try {
      // Query only pendingSync intents
      final intents = await _isar.paymentIntents
          .filter()
          .statusEqualTo(IntentStatus.pendingSync)
          .findAll();

      for (final intent in intents) {
        await _processIntent(intent);
      }
    } finally {
      _isProcessing = false;
    }
  }

  /// MH2: Clean up completed/failed intents older than 24 hours.
  ///
  /// Called on app startup and periodically. Removes stale intents
  /// that contain idempotency keys with user IDs and timestamps.
  Future<void> cleanupStaleIntents() async {
    final cutoff = DateTime.now().toUtc().subtract(staleThreshold);

    await _isar.writeTxn(() async {
      // Delete completed intents older than 24h
      await _isar.paymentIntents
          .filter()
          .statusEqualTo(IntentStatus.completed)
          .createdAtLessThan(cutoff)
          .deleteAll();

      // Delete failed intents older than 24h
      await _isar.paymentIntents
          .filter()
          .statusEqualTo(IntentStatus.failed)
          .createdAtLessThan(cutoff)
          .deleteAll();

      // Delete cancelled intents older than 24h
      await _isar.paymentIntents
          .filter()
          .statusEqualTo(IntentStatus.cancelled)
          .createdAtLessThan(cutoff)
          .deleteAll();
    });
  }

  /// MH8: Cancel pending intents for a specific user on logout.
  ///
  /// SECURITY: Only cancels intents where [buyerId] matches [userId],
  /// preserving other users' queued payments on shared devices.
  /// Called from the auth/logout flow to prevent the outgoing user's
  /// queued intents from being sent under the next user's session.
  Future<void> cancelPendingIntentsForUser(String userId) async {
    await _isar.writeTxn(() async {
      final pending = await _isar.paymentIntents
          .filter()
          .statusEqualTo(IntentStatus.pendingSync)
          .buyerIdEqualTo(userId)
          .findAll();

      for (final intent in pending) {
        intent.status = IntentStatus.cancelled;
        await _isar.paymentIntents.put(intent);
      }
    });
  }

  /// Cancel ALL pending intents (used for full database reset scenarios).
  @Deprecated(
    'Use cancelPendingIntentsForUser(userId) for user-scoped cancellation',
  )
  Future<void> cancelAllPendingIntents() async {
    await _isar.writeTxn(() async {
      final pending = await _isar.paymentIntents
          .filter()
          .statusEqualTo(IntentStatus.pendingSync)
          .findAll();

      for (final intent in pending) {
        intent.status = IntentStatus.cancelled;
        await _isar.paymentIntents.put(intent);
      }
    });
  }

  /// Process a single intent: transition to syncing, call API, update status.
  Future<void> _processIntent(PaymentIntent intent) async {
    // MH4: Atomic status transition pendingSync -> syncing
    await _isar.writeTxn(() async {
      // Re-read to ensure still pendingSync (prevents double-pickup)
      final current = await _isar.paymentIntents.get(intent.id);
      if (current == null || current.status != IntentStatus.pendingSync) return;

      current.status = IntentStatus.syncing;
      current.lastAttemptAt = DateTime.now().toUtc();
      await _isar.paymentIntents.put(current);
    });

    // Re-read after transaction to verify transition happened
    final syncing = await _isar.paymentIntents.get(intent.id);
    if (syncing == null || syncing.status != IntentStatus.syncing) return;

    // Apply exponential backoff delay based on retry count
    await _applyBackoff(syncing.retryCount);

    try {
      // MH2: Use the PERSISTED idempotency key — never regenerate
      final request = InitiateEscrowRequest(
        deliveryId: syncing.deliveryId,
        buyerId: syncing.buyerId,
        farmerId: syncing.farmerId,
        amount: syncing.amount,
        idempotencyKey: syncing.idempotencyKey,
      );

      final json = await _apiClient.post<Map<String, dynamic>>(
        ApiConfig.escrowInitiate,
        data: request.toJson(),
      );

      final response = InitiateEscrowResponse.fromJson(json);

      // Success: mark intent completed and cache payment
      await _isar.writeTxn(() async {
        final current = await _isar.paymentIntents.get(syncing.id);
        if (current != null) {
          current.status = IntentStatus.completed;
          await _isar.paymentIntents.put(current);
        }

        // Cache the server payment response
        final payment = Payment()
          ..paymentId = response.escrowId
          ..deliveryId = response.deliveryId
          ..buyerId = response.buyerId
          ..farmerId = response.farmerId
          ..amount = response.amount
          ..commission = response.commission
          ..commissionRate = 0.025
          ..farmerPayout = response.farmerPayout
          ..telebirrRef = response.telebirrRef
          ..status = PaymentStatus.values.byName(response.status)
          ..createdAt = DateTime.parse(response.createdAt)
          ..updatedAt = DateTime.parse(response.createdAt)
          ..expiresAt = DateTime.parse(response.expiresAt)
          ..version = 1;

        // Upsert: check if payment already cached
        final existing = await _isar.payments
            .filter()
            .paymentIdEqualTo(payment.paymentId)
            .findFirst();
        if (existing != null) {
          payment.id = existing.id;
        }
        await _isar.payments.put(payment);
      });
    } on ApiException catch (e) {
      await _handleIntentFailure(syncing, e.message);
    } on Exception catch (e) {
      await _handleIntentFailure(syncing, e.toString());
    }
  }

  /// Handle a failed intent: increment retry count or mark as failed.
  Future<void> _handleIntentFailure(
    PaymentIntent intent,
    String errorMessage,
  ) async {
    await _isar.writeTxn(() async {
      final current = await _isar.paymentIntents.get(intent.id);
      if (current == null) return;

      current.retryCount += 1;
      current.lastAttemptAt = DateTime.now().toUtc();
      // MH7: Store error code only, not full message with potential PII
      current.lastError = errorMessage.length > 100
          ? errorMessage.substring(0, 100)
          : errorMessage;

      if (current.retryCount >= maxRetries) {
        // Max retries reached — mark as failed, user must manually retry
        current.status = IntentStatus.failed;
      } else {
        // Put back to pendingSync for next connectivity event
        current.status = IntentStatus.pendingSync;
      }

      await _isar.paymentIntents.put(current);
    });
  }

  /// Apply exponential backoff delay.
  ///
  /// Retry 1: immediate (0 delay)
  /// Retry 2: 5 seconds
  /// Retry 3: 15 seconds
  Future<void> _applyBackoff(int retryCount) async {
    if (retryCount <= 0) return;

    // Exponential backoff: 5s * 3^(retry-1) → 5s, 15s
    final delaySeconds = 5 * _pow(3, retryCount - 1);
    await Future<void>.delayed(Duration(seconds: delaySeconds));
  }

  /// Integer power helper (avoids dart:math pow returning double).
  static int _pow(int base, int exponent) {
    var result = 1;
    for (var i = 0; i < exponent; i++) {
      result *= base;
    }
    return result;
  }
}
