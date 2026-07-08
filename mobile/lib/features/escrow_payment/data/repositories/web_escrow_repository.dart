import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/core/models/payment_intent.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/escrow_result.dart';
import 'package:wereda_connect/features/escrow_payment/domain/repositories/base_escrow_repository.dart';

/// Web-only escrow repository.
///
/// All operations use HTTP calls via ApiClient. No Isar dependency.
/// Web always has connectivity, so no offline queuing is needed.
class WebEscrowRepository extends BaseEscrowRepository {
  final ApiClient _api;

  WebEscrowRepository({required ApiClient api}) : _api = api;

  @override
  Future<EscrowResult> initiatePayment({
    required String deliveryId,
    required String buyerId,
    required String farmerId,
    required double amount,
  }) async {
    try {
      final data = await _api.post<Map<String, dynamic>>(
        '/escrow/initiate',
        data: {
          'deliveryId': deliveryId,
          'buyerId': buyerId,
          'farmerId': farmerId,
          'amount': amount,
          // currency is required by the contract (fixed "ETB"); omitting it made the
          // server reject web initiations with ESCROW_004 (Review H3).
          'currency': 'ETB',
          'idempotencyKey':
              '$buyerId:$deliveryId:initiate:${DateTime.now().toUtc().millisecondsSinceEpoch}',
        },
      );
      // toPayUrl is transient (async gateway only); read it straight off the
      // response so the UI can open Telebirr. Mirrors EscrowRepository.
      return EscrowSuccess(
        _parsePayment(data),
        toPayUrl: data['toPayUrl'] as String?,
      );
    } on Exception catch (e) {
      return EscrowError('ESCROW_CLIENT', e.toString());
    }
  }

  @override
  Future<EscrowResult> raiseDispute({
    required String escrowId,
    required String disputedBy,
    required String reason,
    String? description,
    List<String>? evidenceRefs,
  }) async {
    try {
      final data = await _api.post<Map<String, dynamic>>(
        '/escrow/dispute',
        data: {
          'escrowId': escrowId,
          'disputedBy': disputedBy,
          'reason': reason,
          'idempotencyKey':
              '$disputedBy:$escrowId:dispute:${DateTime.now().toUtc().millisecondsSinceEpoch}',
          if (description != null) 'description': description,
          if (evidenceRefs != null) 'evidenceRefs': evidenceRefs,
        },
      );
      return EscrowSuccess(_parsePayment(data));
    } on Exception catch (e) {
      return EscrowError('ESCROW_CLIENT', e.toString());
    }
  }

  @override
  Future<void> cancelIntent(String idempotencyKey) async {
    // No local intents on web — nothing to cancel
  }

  @override
  Future<Payment?> getEscrow(String escrowId) async {
    // ONLY a genuine 404 (no such escrow) maps to null. Auth/server/rate-limit/
    // network failures propagate as ApiException so the BLoC surfaces a
    // distinct, retryable error rather than a misleading "payment not found".
    try {
      final data = await _api.get<Map<String, dynamic>>(
        '/escrow/${Uri.encodeComponent(escrowId)}',
      );
      return _parsePayment(data);
    } on NotFoundException {
      return null;
    }
  }

  @override
  Future<Payment?> getEscrowByDelivery(String deliveryId) async {
    // F-33: resolve the escrow for THIS delivery via the dedicated endpoint
    // (server enforces party-or-admin). /escrow/history does NOT filter by
    // deliveryId, so the old history-based lookup returned an arbitrary escrow.
    //
    // ONLY a genuine 404 (no escrow exists for this delivery) maps to null.
    // Auth (401/403), server (5xx), rate-limit, and network/timeout failures
    // propagate as ApiException so the BLoC can surface a distinct, retryable
    // error instead of misreporting "payment not found" — essential in an
    // offline-first app where "no escrow yet" must differ from "request failed".
    try {
      final data = await _api.get<Map<String, dynamic>>(
        '/escrow/by-delivery/${Uri.encodeComponent(deliveryId)}',
      );
      return _parsePayment(data);
    } on NotFoundException {
      return null;
    }
  }

  @override
  Future<List<Payment>> getHistory({
    required String userId,
    String? statusFilter,
    String? roleFilter,
  }) async {
    return refreshHistory(
      userId: userId,
      statusFilter: statusFilter,
      roleFilter: roleFilter,
    );
  }

  @override
  Future<Payment?> refreshEscrow(String escrowId) async {
    // Silent background refresh: web has no local cache, so on a transient
    // failure return null (the BLoC no-ops on null and keeps the currently
    // displayed escrow) instead of surfacing an error over good data. This
    // mirrors EscrowRepository.refreshEscrow, which falls back to cache.
    // A genuine load error is surfaced via getEscrow on the initial load path.
    try {
      return await getEscrow(escrowId);
    } on ApiException {
      return null;
    }
  }

  @override
  Future<List<Payment>> refreshHistory({
    required String userId,
    String? statusFilter,
    String? roleFilter,
    int limit = 20,
    String? nextToken,
  }) async {
    // GET /escrow/history scopes by the caller's JWT + the `role` query param
    // (see EscrowFunction.HandleHistoryAsync). It does NOT read farmerId/buyerId
    // — sending those left role null, so the server defaulted to the buyer GSI
    // and a farmer saw zero payouts. Send `role` to match the server + mobile.
    final params = <String, dynamic>{'limit': '$limit'};
    if (roleFilter != null) params['role'] = roleFilter;
    if (statusFilter != null) params['status'] = statusFilter;
    if (nextToken != null) params['nextToken'] = nextToken;

    final data = await _api.get<Map<String, dynamic>>(
      '/escrow/history',
      queryParameters: params,
    );
    final items = data['escrows'] as List<dynamic>? ??
        data['items'] as List<dynamic>? ??
        [];
    return items
        .map((item) => _parsePaymentMap(item as Map<String, dynamic>))
        .toList();
  }

  @override
  Future<List<PaymentIntent>> getPendingIntents() async {
    // No local payment intents on web
    return [];
  }

  Payment _parsePayment(Map<String, dynamic> data) {
    if (data.containsKey('paymentId') || data.containsKey('escrowId')) {
      return _parsePaymentMap(data);
    }
    if (data.containsKey('escrow')) {
      return _parsePaymentMap(data['escrow'] as Map<String, dynamic>);
    }
    return _parsePaymentMap(data);
  }

  Payment _parsePaymentMap(Map<String, dynamic> m) {
    return Payment()
      ..paymentId = m['paymentId'] as String? ?? m['escrowId'] as String? ?? ''
      ..deliveryId = m['deliveryId'] as String? ?? ''
      ..farmerId = m['farmerId'] as String? ?? ''
      ..buyerId = m['buyerId'] as String? ?? ''
      ..amount = (m['amount'] as num?)?.toDouble() ?? 0
      ..commission = (m['commission'] as num?)?.toDouble() ?? 0
      ..commissionRate = (m['commissionRate'] as num?)?.toDouble() ?? 0.025
      ..farmerPayout = (m['farmerPayout'] as num?)?.toDouble() ?? 0
      ..telebirrRef = m['telebirrRef'] as String?
      ..status = _parsePaymentStatus(m['status'] as String?)
      ..createdAt =
          DateTime.tryParse(m['createdAt'] as String? ?? '') ?? DateTime.now()
      ..updatedAt =
          DateTime.tryParse(m['updatedAt'] as String? ?? '') ?? DateTime.now()
      ..expiresAt = DateTime.tryParse(m['expiresAt'] as String? ?? '')
      ..disputeWindowEndsAt =
          DateTime.tryParse(m['disputeWindowEndsAt'] as String? ?? '')
      ..releasedAt = DateTime.tryParse(m['releasedAt'] as String? ?? '')
      ..refundedAt = DateTime.tryParse(m['refundedAt'] as String? ?? '')
      ..completedAt = DateTime.tryParse(m['completedAt'] as String? ?? '')
      ..disputeReason = m['disputeReason'] as String?
      ..disputeDescription = m['disputeDescription'] as String?
      ..version = (m['version'] as num?)?.toInt() ?? 1;
  }

  PaymentStatus _parsePaymentStatus(String? s) {
    return PaymentStatus.values.firstWhere(
      (e) => e.name == s,
      orElse: () => PaymentStatus.initiated,
    );
  }
}
