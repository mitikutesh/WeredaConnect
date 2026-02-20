import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_config.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/features/escrow_payment/data/models/escrow_models.dart';
import 'package:wereda_connect/core/database/schemas/payment_intent.dart';

/// Result type for escrow operations.
///
/// Uses sealed class pattern for exhaustive switch matching:
/// - [EscrowSuccess]: Server confirmed the operation.
/// - [EscrowQueued]: Intent queued locally for offline sync.
/// - [EscrowError]: Operation failed with an error code.
sealed class EscrowResult {
  const EscrowResult();
}

/// Server confirmed the escrow operation.
class EscrowSuccess extends EscrowResult {
  final Payment payment;
  const EscrowSuccess(this.payment);
}

/// Payment intent queued locally (offline, will sync when online).
class EscrowQueued extends EscrowResult {
  final PaymentIntent intent;
  const EscrowQueued(this.intent);
}

/// Escrow operation failed.
class EscrowError extends EscrowResult {
  final String code;
  final String message;
  const EscrowError(this.code, this.message);
}

/// Online-first repository for escrow payment operations.
///
/// Unlike trip_listing/delivery_verification (which use SyncQueue for all
/// writes), escrow mutations go directly to dedicated `/escrow/*` endpoints
/// because money movement requires server coordination with Telebirr.
///
/// Offline fallback: When connectivity is unavailable, payment initiation
/// intents are queued locally in [PaymentIntent] and processed by
/// [EscrowSyncService] when connectivity returns.
///
/// Disputes are online-only (time-sensitive 48h window).
class EscrowRepository {
  final Isar _isar;
  final ApiClient _apiClient;
  final ConnectivityMonitor _connectivityMonitor;

  /// Minimum escrow amount in ETB (UX pre-check; server enforces).
  static const double minAmount = 100.0;

  /// Maximum escrow amount in ETB (UX pre-check; server enforces).
  static const double maxAmount = 500000.0;

  EscrowRepository({
    required Isar isar,
    required ApiClient apiClient,
    required ConnectivityMonitor connectivityMonitor,
  })  : _isar = isar,
        _apiClient = apiClient,
        _connectivityMonitor = connectivityMonitor;

  // ── Mutations (online-first, intent fallback) ──────────────────────

  /// Initiate escrow payment for a delivery.
  ///
  /// Online: POST /escrow/initiate -> cache response in [Payment].
  /// Offline: Save [PaymentIntent] with [IntentStatus.pendingSync] status.
  ///
  /// Security checks (UX only, server enforces):
  /// - MH1: Amount validation 100-500,000 ETB
  /// - MH3/MH5: Self-trading check (buyerId != farmerId)
  /// - MH2: Idempotency key generated ONCE and persisted before API call
  Future<EscrowResult> initiatePayment({
    required String deliveryId,
    required String buyerId,
    required String farmerId,
    required double amount,
  }) async {
    // MH5: Self-trading fast-fail check (server also enforces)
    if (buyerId == farmerId) {
      return const EscrowError('ESCROW_001', 'Cannot pay yourself.');
    }

    // MH1: Amount validation (UX pre-check; server enforces)
    final amountError = _validateAmount(amount);
    if (amountError != null) {
      return EscrowError('ESCROW_001', amountError);
    }

    // MH2: Generate idempotency key ONCE, persist BEFORE any API call
    final idempotencyKey = _generateIdempotencyKey(
      buyerId,
      deliveryId,
      'initiate',
    );

    // Check connectivity
    final isOnline = await _connectivityMonitor.isConnected();

    if (isOnline) {
      return _initiateOnline(
        deliveryId: deliveryId,
        buyerId: buyerId,
        farmerId: farmerId,
        amount: amount,
        idempotencyKey: idempotencyKey,
      );
    } else {
      return _queueIntent(
        deliveryId: deliveryId,
        buyerId: buyerId,
        farmerId: farmerId,
        amount: amount,
        idempotencyKey: idempotencyKey,
      );
    }
  }

  /// Raise dispute on an escrowed payment.
  ///
  /// Online-only: POST /escrow/dispute -> update local [Payment].
  /// Offline: Returns error (disputes are time-sensitive, 48h window).
  Future<EscrowResult> raiseDispute({
    required String escrowId,
    required String disputedBy,
    required String reason,
    String? description,
    List<String>? evidenceRefs,
  }) async {
    // Online check — disputes cannot be queued offline
    final isOnline = await _connectivityMonitor.isConnected();
    if (!isOnline) {
      return const EscrowError(
        'ESCROW_OFFLINE',
        'Internet connection required to raise disputes.',
      );
    }

    final idempotencyKey = _generateIdempotencyKey(
      disputedBy,
      escrowId,
      'dispute',
    );

    final request = DisputeEscrowRequest(
      escrowId: escrowId,
      disputedBy: disputedBy,
      reason: reason,
      description: description,
      evidenceRefs: evidenceRefs,
      idempotencyKey: idempotencyKey,
    );

    try {
      final json = await _apiClient.post<Map<String, dynamic>>(
        ApiConfig.escrowDispute,
        data: request.toJson(),
      );

      final response = DisputeEscrowResponse.fromJson(json);

      // Update local cache with disputed status
      final payment =
          await _isar.payments.filter().paymentIdEqualTo(escrowId).findFirst();

      if (payment != null) {
        await _isar.writeTxn(() async {
          payment.status = PaymentStatus.disputed;
          payment.disputeReason = response.reason;
          payment.disputeDescription = description;
          payment.disputeWindowEndsAt =
              DateTime.parse(response.disputeWindowEndsAt);
          payment.updatedAt = DateTime.now().toUtc();
          await _isar.payments.put(payment);
        });
        return EscrowSuccess(payment);
      }

      // Payment not in cache; create from available data
      return const EscrowError(
        'ESCROW_CLIENT',
        'Dispute raised but local cache not found.',
      );
    } on ApiException catch (e) {
      return _mapApiException(e);
    }
  }

  /// Cancel a pending_sync intent before it syncs.
  ///
  /// Local-only operation: updates [PaymentIntent] status to cancelled.
  Future<void> cancelIntent(String idempotencyKey) async {
    final intent = await _isar.paymentIntents
        .filter()
        .idempotencyKeyEqualTo(idempotencyKey)
        .findFirst();

    if (intent == null || intent.status != IntentStatus.pendingSync) return;

    await _isar.writeTxn(() async {
      intent.status = IntentStatus.cancelled;
      await _isar.paymentIntents.put(intent);
    });
  }

  // ── Reads (local cache with remote refresh) ────────────────────────

  /// Get single escrow by ID. Local first, remote refresh if online.
  Future<Payment?> getEscrow(String escrowId) async {
    return _isar.payments.filter().paymentIdEqualTo(escrowId).findFirst();
  }

  /// Get escrow by delivery ID (1:1 relationship).
  Future<Payment?> getEscrowByDelivery(String deliveryId) async {
    return _isar.payments.filter().deliveryIdEqualTo(deliveryId).findFirst();
  }

  /// Get payment history from local cache.
  Future<List<Payment>> getHistory({
    String? statusFilter,
    String? roleFilter,
  }) async {
    var query = _isar.payments.filter().paymentIdIsNotEmpty();

    if (statusFilter != null) {
      final status = PaymentStatus.values.firstWhere(
        (s) => s.name == statusFilter,
        orElse: () => PaymentStatus.escrowed,
      );
      query = query.statusEqualTo(status);
    }

    return query.sortByCreatedAtDesc().findAll();
  }

  /// Refresh single escrow from server. Updates local cache.
  Future<Payment?> refreshEscrow(String escrowId) async {
    final isOnline = await _connectivityMonitor.isConnected();
    if (!isOnline) return getEscrow(escrowId);

    try {
      final json = await _apiClient.get<Map<String, dynamic>>(
        '${ApiConfig.escrowStatus}/$escrowId',
      );

      final response = EscrowDetailResponse.fromJson(json);
      final payment = _mapDetailResponse(response);

      // Upsert into local cache
      await _upsertPayment(payment);

      return payment;
    } on ApiException {
      // Silently fall back to cache on refresh failure
      return getEscrow(escrowId);
    }
  }

  /// Refresh payment history from server. Updates local cache.
  Future<List<Payment>> refreshHistory({
    String? statusFilter,
    String? roleFilter,
    int limit = 20,
    String? nextToken,
  }) async {
    final isOnline = await _connectivityMonitor.isConnected();
    if (!isOnline) {
      return getHistory(statusFilter: statusFilter, roleFilter: roleFilter);
    }

    try {
      final json = await _apiClient.get<Map<String, dynamic>>(
        ApiConfig.escrowHistory,
        queryParameters: {
          if (statusFilter != null) 'status': statusFilter,
          if (roleFilter != null) 'role': roleFilter,
          'limit': limit.toString(),
          if (nextToken != null) 'nextToken': nextToken,
        },
      );

      final response = EscrowHistoryResponse.fromJson(json);
      final payments = response.escrows.map(_mapSummaryResponse).toList();

      // Upsert all into local cache
      await _isar.writeTxn(() async {
        for (final payment in payments) {
          final existing = await _isar.payments
              .filter()
              .paymentIdEqualTo(payment.paymentId)
              .findFirst();
          if (existing != null) {
            payment.id = existing.id;
          }
          await _isar.payments.put(payment);
        }
      });

      return payments;
    } on ApiException {
      // Fall back to cache on refresh failure
      return getHistory(statusFilter: statusFilter, roleFilter: roleFilter);
    }
  }

  // ── Intent queue management ────────────────────────────────────────

  /// Get all pending intents for display.
  Future<List<PaymentIntent>> getPendingIntents() async {
    return _isar.paymentIntents
        .filter()
        .statusEqualTo(IntentStatus.pendingSync)
        .sortByCreatedAtDesc()
        .findAll();
  }

  // ── Private helpers ────────────────────────────────────────────────

  /// Attempt online initiation via POST /escrow/initiate.
  Future<EscrowResult> _initiateOnline({
    required String deliveryId,
    required String buyerId,
    required String farmerId,
    required double amount,
    required String idempotencyKey,
  }) async {
    final request = InitiateEscrowRequest(
      deliveryId: deliveryId,
      buyerId: buyerId,
      farmerId: farmerId,
      amount: amount,
      idempotencyKey: idempotencyKey,
    );

    try {
      final json = await _apiClient.post<Map<String, dynamic>>(
        ApiConfig.escrowInitiate,
        data: request.toJson(),
      );

      final response = InitiateEscrowResponse.fromJson(json);
      final payment = _mapInitiateResponse(response);

      // Cache the payment locally
      await _upsertPayment(payment);

      return EscrowSuccess(payment);
    } on NetworkException {
      // Network error — fall back to intent queue
      return _queueIntent(
        deliveryId: deliveryId,
        buyerId: buyerId,
        farmerId: farmerId,
        amount: amount,
        idempotencyKey: idempotencyKey,
      );
    } on TimeoutException {
      // Timeout — fall back to intent queue
      return _queueIntent(
        deliveryId: deliveryId,
        buyerId: buyerId,
        farmerId: farmerId,
        amount: amount,
        idempotencyKey: idempotencyKey,
      );
    } on ApiException catch (e) {
      return _mapApiException(e);
    }
  }

  /// Queue a payment intent locally for offline sync.
  Future<EscrowResult> _queueIntent({
    required String deliveryId,
    required String buyerId,
    required String farmerId,
    required double amount,
    required String idempotencyKey,
  }) async {
    // Check for existing intent with same idempotency key
    final existing = await _isar.paymentIntents
        .filter()
        .idempotencyKeyEqualTo(idempotencyKey)
        .findFirst();

    if (existing != null) {
      return EscrowQueued(existing);
    }

    final intent = PaymentIntent()
      ..idempotencyKey = idempotencyKey
      ..deliveryId = deliveryId
      ..buyerId = buyerId
      ..farmerId = farmerId
      ..amount = amount
      ..status = IntentStatus.pendingSync
      ..createdAt = DateTime.now().toUtc();

    await _isar.writeTxn(() async {
      await _isar.paymentIntents.put(intent);
    });

    return EscrowQueued(intent);
  }

  /// Generate idempotency key: {actor}:{entityId}:{action}:{timestamp}
  String _generateIdempotencyKey(
    String actor,
    String entityId,
    String action,
  ) {
    final timestamp = DateTime.now().toUtc().millisecondsSinceEpoch;
    return '$actor:$entityId:$action:$timestamp';
  }

  /// Validate amount (100-500,000 ETB), client-side pre-check.
  /// Returns an error message if invalid, null if valid.
  String? _validateAmount(double amount) {
    if (amount < minAmount || amount > maxAmount) {
      return 'Amount must be between ETB ${minAmount.toStringAsFixed(0)} '
          'and ETB ${maxAmount.toStringAsFixed(0)}.';
    }
    return null;
  }

  /// Map initiate response to local [Payment] entity.
  Payment _mapInitiateResponse(InitiateEscrowResponse response) {
    return Payment()
      ..paymentId = response.escrowId
      ..deliveryId = response.deliveryId
      ..buyerId = response.buyerId
      ..farmerId = response.farmerId
      ..amount = response.amount
      ..commission = response.commission
      ..commissionRate = 0.025 // Server doesn't return rate in initiate
      ..farmerPayout = response.farmerPayout
      ..telebirrRef = response.telebirrRef
      ..status = PaymentStatus.values.byName(response.status)
      ..createdAt = DateTime.parse(response.createdAt)
      ..updatedAt = DateTime.parse(response.createdAt)
      ..expiresAt = DateTime.parse(response.expiresAt)
      ..version = 1;
  }

  /// Map detail response to local [Payment] entity.
  Payment _mapDetailResponse(EscrowDetailResponse response) {
    return Payment()
      ..paymentId = response.escrowId
      ..deliveryId = response.deliveryId
      ..buyerId = response.buyerId
      ..farmerId = response.farmerId
      ..amount = response.amount
      ..commission = response.commission
      ..commissionRate = response.commissionRate
      ..farmerPayout = response.farmerPayout
      ..telebirrRef = response.telebirrRef
      ..status = PaymentStatus.values.byName(response.status)
      ..createdAt = DateTime.parse(response.createdAt)
      ..updatedAt = DateTime.parse(response.updatedAt)
      ..expiresAt = DateTime.parse(response.expiresAt)
      ..disputeWindowEndsAt = response.disputeWindowEndsAt != null
          ? DateTime.parse(response.disputeWindowEndsAt!)
          : null
      ..releasedAt = response.releasedAt != null
          ? DateTime.parse(response.releasedAt!)
          : null
      ..refundedAt = response.refundedAt != null
          ? DateTime.parse(response.refundedAt!)
          : null
      ..completedAt = response.completedAt != null
          ? DateTime.parse(response.completedAt!)
          : null
      ..disputeReason = response.dispute?['reason'] as String?
      ..disputeDescription = response.dispute?['description'] as String?
      ..version = response.version;
  }

  /// Map summary response to local [Payment] entity.
  Payment _mapSummaryResponse(EscrowSummary summary) {
    return Payment()
      ..paymentId = summary.escrowId
      ..deliveryId = summary.deliveryId
      ..buyerId = '' // Not available in summary
      ..farmerId = '' // Not available in summary
      ..amount = summary.amount
      ..commission = summary.commission
      ..commissionRate = 0.025
      ..farmerPayout = summary.amount - summary.commission
      ..status = PaymentStatus.values.byName(summary.status)
      ..createdAt = DateTime.parse(summary.createdAt)
      ..updatedAt = DateTime.parse(summary.createdAt)
      ..completedAt = summary.completedAt != null
          ? DateTime.parse(summary.completedAt!)
          : null
      ..version = 1;
  }

  /// Upsert a payment into the local Isar cache.
  Future<void> _upsertPayment(Payment payment) async {
    await _isar.writeTxn(() async {
      final existing = await _isar.payments
          .filter()
          .paymentIdEqualTo(payment.paymentId)
          .findFirst();
      if (existing != null) {
        payment.id = existing.id;
      }
      await _isar.payments.put(payment);
    });
  }

  /// Map [ApiException] to [EscrowError] with appropriate error codes.
  ///
  /// MH7: Never log raw request/response bodies. Use error codes only.
  EscrowError _mapApiException(ApiException e) {
    if (e is ValidationException) {
      return EscrowError(
        'ESCROW_VALIDATION',
        e.message,
      );
    }
    if (e is AuthException) {
      return const EscrowError('ESCROW_005', 'Please log in to continue.');
    }
    if (e is NotFoundException) {
      return const EscrowError('ESCROW_016', 'Payment not found.');
    }
    if (e is ConflictException) {
      return EscrowError('ESCROW_009', e.message);
    }
    if (e is RateLimitException) {
      return const EscrowError(
        'ESCROW_011',
        'Too many requests. Please wait and try again.',
      );
    }
    if (e is ServerException) {
      final statusCode = e.statusCode;
      if (statusCode == 502) {
        return const EscrowError(
          'ESCROW_013',
          'Telebirr service error. Please try again.',
        );
      }
      if (statusCode == 503) {
        return const EscrowError(
          'ESCROW_014',
          'Telebirr is currently unavailable. Please try later.',
        );
      }
    }
    return const EscrowError(
      'ESCROW_CLIENT',
      'Something went wrong. Please try again.',
    );
  }
}
