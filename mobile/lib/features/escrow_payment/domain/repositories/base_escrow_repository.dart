import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/core/models/payment_intent.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/escrow_result.dart';

/// Abstract base for escrow repositories.
///
/// Extracted from EscrowRepository so both mobile (Isar+API) and
/// web (API-only) implementations can share the same BLoC interface.
abstract class BaseEscrowRepository {
  Future<EscrowResult> initiatePayment({
    required String deliveryId,
    required String buyerId,
    required String farmerId,
    required double amount,
  });

  /// Open (or get back) the live Telebirr checkout of a `payment_submitted`
  /// order: `POST /escrow/order-checkout` (ESC-44). Online only; never
  /// queued, since a checkout replayed later could start a second payment.
  Future<OrderCheckoutResult> openOrderCheckout(String orderId);

  Future<EscrowResult> raiseDispute({
    required String escrowId,
    required String disputedBy,
    required String reason,
    String? description,
    List<String>? evidenceRefs,
  });

  Future<void> cancelIntent(String idempotencyKey);

  Future<Payment?> getEscrow(String escrowId);

  Future<Payment?> getEscrowByDelivery(String deliveryId);

  Future<List<Payment>> getHistory({
    required String userId,
    String? statusFilter,
    String? roleFilter,
  });

  Future<Payment?> refreshEscrow(String escrowId);

  Future<List<Payment>> refreshHistory({
    required String userId,
    String? statusFilter,
    String? roleFilter,
    int limit = 20,
    String? nextToken,
  });

  Future<List<PaymentIntent>> getPendingIntents();
}
