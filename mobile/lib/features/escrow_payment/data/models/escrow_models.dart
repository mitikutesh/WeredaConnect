/// Escrow DTO models mirroring server DTOs from EscrowDtos.cs.
///
/// These are used for API request/response serialization only.
/// Local persistence uses [Payment] and [PaymentIntent] Isar schemas.
library;

// ──────────────────────────────────────────────
// Request DTOs
// ──────────────────────────────────────────────

/// POST /escrow/initiate request body.
///
/// Note: commission, commissionRate, and farmerPayout are NOT sent
/// in the request — they are calculated server-side only (MH3).
class InitiateEscrowRequest {
  final String deliveryId;
  final String buyerId;
  final String farmerId;
  final double amount;
  final String currency;
  final String idempotencyKey;

  const InitiateEscrowRequest({
    required this.deliveryId,
    required this.buyerId,
    required this.farmerId,
    required this.amount,
    required this.idempotencyKey,
    this.currency = 'ETB',
  });

  Map<String, dynamic> toJson() => {
        'deliveryId': deliveryId,
        'buyerId': buyerId,
        'farmerId': farmerId,
        'amount': amount,
        'currency': currency,
        'idempotencyKey': idempotencyKey,
      };
}

/// POST /escrow/dispute request body.
class DisputeEscrowRequest {
  final String escrowId;
  final String disputedBy;
  final String reason;
  final String idempotencyKey;
  final String? description;
  final List<String>? evidenceRefs;

  const DisputeEscrowRequest({
    required this.escrowId,
    required this.disputedBy,
    required this.reason,
    required this.idempotencyKey,
    this.description,
    this.evidenceRefs,
  });

  Map<String, dynamic> toJson() => {
        'escrowId': escrowId,
        'disputedBy': disputedBy,
        'reason': reason,
        if (description != null) 'description': description,
        if (evidenceRefs != null) 'evidenceRefs': evidenceRefs,
        'idempotencyKey': idempotencyKey,
      };
}

// ──────────────────────────────────────────────
// Response DTOs
// ──────────────────────────────────────────────

/// POST /escrow/initiate success response.
class InitiateEscrowResponse {
  final String escrowId;
  final String deliveryId;
  final String buyerId;
  final String farmerId;
  final double amount;
  final double commission;
  final double farmerPayout;
  final String currency;
  final String status;
  final String createdAt;
  final String expiresAt;
  final String? telebirrRef;
  final String? message;
  final bool isDuplicate;

  /// Async gateways only: the Telebirr H5 checkout URL the buyer opens to pay.
  /// Present when [status] is `initiated` (real gateway); null for the mock
  /// (which returns `escrowed` directly). Transient — never persisted.
  final String? toPayUrl;

  const InitiateEscrowResponse({
    required this.escrowId,
    required this.deliveryId,
    required this.buyerId,
    required this.farmerId,
    required this.amount,
    required this.commission,
    required this.farmerPayout,
    required this.currency,
    required this.status,
    required this.createdAt,
    required this.expiresAt,
    this.telebirrRef,
    this.message,
    this.isDuplicate = false,
    this.toPayUrl,
  });

  factory InitiateEscrowResponse.fromJson(Map<String, dynamic> json) {
    return InitiateEscrowResponse(
      escrowId: json['escrowId'] as String,
      deliveryId: json['deliveryId'] as String,
      buyerId: json['buyerId'] as String,
      farmerId: json['farmerId'] as String,
      amount: (json['amount'] as num).toDouble(),
      commission: (json['commission'] as num).toDouble(),
      farmerPayout: (json['farmerPayout'] as num).toDouble(),
      currency: json['currency'] as String,
      status: json['status'] as String,
      createdAt: json['createdAt'] as String,
      expiresAt: json['expiresAt'] as String,
      telebirrRef: json['telebirrRef'] as String?,
      message: json['message'] as String?,
      isDuplicate: json['isDuplicate'] as bool? ?? false,
      toPayUrl: json['toPayUrl'] as String?,
    );
  }
}

/// POST /escrow/order-checkout success response (ESC-01, ESC-44).
///
/// [toPayUrl] is transient: never persisted, never in any other response.
class OrderCheckoutResponse {
  final String orderId;

  /// The server's order total, the amount Telebirr charges.
  final double amount;
  final String currency;
  final String toPayUrl;

  /// When the checkout stops being payable (ISO 8601, UTC, server clock).
  final DateTime openUntil;

  /// True when this is the same open checkout returned again.
  final bool isExisting;

  const OrderCheckoutResponse({
    required this.orderId,
    required this.amount,
    required this.currency,
    required this.toPayUrl,
    required this.openUntil,
    this.isExisting = false,
  });

  factory OrderCheckoutResponse.fromJson(Map<String, dynamic> json) {
    return OrderCheckoutResponse(
      orderId: json['orderId'] as String,
      amount: (json['amount'] as num).toDouble(),
      currency: json['currency'] as String? ?? 'ETB',
      toPayUrl: json['toPayUrl'] as String,
      openUntil: DateTime.parse(json['openUntil'] as String).toUtc(),
      isExisting: json['isExisting'] as bool? ?? false,
    );
  }
}

/// POST /escrow/dispute success response.
class DisputeEscrowResponse {
  final String escrowId;
  final String status;
  final String disputeId;
  final String disputedBy;
  final String reason;
  final String disputeWindowEndsAt;
  final String createdAt;
  final String? message;

  const DisputeEscrowResponse({
    required this.escrowId,
    required this.status,
    required this.disputeId,
    required this.disputedBy,
    required this.reason,
    required this.disputeWindowEndsAt,
    required this.createdAt,
    this.message,
  });

  factory DisputeEscrowResponse.fromJson(Map<String, dynamic> json) {
    return DisputeEscrowResponse(
      escrowId: json['escrowId'] as String,
      status: json['status'] as String,
      disputeId: json['disputeId'] as String,
      disputedBy: json['disputedBy'] as String,
      reason: json['reason'] as String,
      disputeWindowEndsAt: json['disputeWindowEndsAt'] as String,
      createdAt: json['createdAt'] as String,
      message: json['message'] as String?,
    );
  }
}

/// GET /escrow/{id} full escrow detail response.
class EscrowDetailResponse {
  final String escrowId;
  final String deliveryId;
  final String buyerId;
  final String farmerId;
  final double amount;
  final double commission;
  final double commissionRate;
  final double farmerPayout;

  /// Transport leg of the held amount; null when the order carried no trucker
  /// split. amount = farmerPayout + truckerPayout + commission.
  final double? truckerPayout;
  final String? truckerId;

  /// Backing order (order-funded escrows), and the refund-on-its-way flag
  /// (SIM-20, see EscrowSummary).
  final String? orderId;
  final bool refundPending;
  final String? refundReason;
  final String currency;
  final String status;
  final String createdAt;
  final String updatedAt;
  final String expiresAt;
  final int version;
  final String? telebirrRef;
  final Map<String, dynamic>? dispute;
  final String? disputeWindowEndsAt;

  /// FRD-13: earliest payout (ISO 8601); sent to the farmer and the trucker
  /// only.
  final String? releaseNotBefore;
  final String? releasedAt;
  final String? refundedAt;
  final String? completedAt;

  const EscrowDetailResponse({
    required this.escrowId,
    required this.deliveryId,
    required this.buyerId,
    required this.farmerId,
    required this.amount,
    required this.commission,
    required this.commissionRate,
    required this.farmerPayout,
    required this.currency,
    required this.status,
    required this.createdAt,
    required this.updatedAt,
    required this.expiresAt,
    required this.version,
    this.telebirrRef,
    this.truckerPayout,
    this.truckerId,
    this.orderId,
    this.refundPending = false,
    this.refundReason,
    this.dispute,
    this.disputeWindowEndsAt,
    this.releaseNotBefore,
    this.releasedAt,
    this.refundedAt,
    this.completedAt,
  });

  factory EscrowDetailResponse.fromJson(Map<String, dynamic> json) {
    return EscrowDetailResponse(
      // Parse defensively (mirrors the web _parsePaymentMap): the contract's GET
      // examples show partial bodies, so optional/omitted fields must not throw a
      // TypeError that bubbles past the ApiException handlers into a generic error
      // (Review M5 + parity). escrowId/status are the only truly-required fields.
      escrowId: json['escrowId'] as String,
      deliveryId: json['deliveryId'] as String? ?? '',
      buyerId: json['buyerId'] as String? ?? '',
      farmerId: json['farmerId'] as String? ?? '',
      amount: (json['amount'] as num?)?.toDouble() ?? 0,
      commission: (json['commission'] as num?)?.toDouble() ?? 0,
      commissionRate: (json['commissionRate'] as num?)?.toDouble() ?? 0.025,
      farmerPayout: (json['farmerPayout'] as num?)?.toDouble() ?? 0,
      truckerPayout: (json['truckerPayout'] as num?)?.toDouble(),
      truckerId: json['truckerId'] as String?,
      orderId: json['orderId'] as String?,
      refundPending: json['refundPending'] as bool? ?? false,
      refundReason: json['refundReason'] as String?,
      currency: json['currency'] as String? ?? 'ETB',
      status: json['status'] as String,
      createdAt: json['createdAt'] as String? ?? '',
      updatedAt:
          json['updatedAt'] as String? ?? json['createdAt'] as String? ?? '',
      expiresAt: json['expiresAt'] as String? ?? '',
      version: (json['version'] as num?)?.toInt() ?? 1,
      telebirrRef: json['telebirrRef'] as String?,
      dispute: json['dispute'] as Map<String, dynamic>?,
      disputeWindowEndsAt: json['disputeWindowEndsAt'] as String?,
      releaseNotBefore: json['releaseNotBefore'] as String?,
      releasedAt: json['releasedAt'] as String?,
      refundedAt: json['refundedAt'] as String?,
      completedAt: json['completedAt'] as String?,
    );
  }
}

/// GET /escrow/history success response.
class EscrowHistoryResponse {
  final List<EscrowSummary> escrows;
  final bool hasMore;
  final int total;
  final String? nextToken;

  const EscrowHistoryResponse({
    required this.escrows,
    required this.hasMore,
    required this.total,
    this.nextToken,
  });

  factory EscrowHistoryResponse.fromJson(Map<String, dynamic> json) {
    return EscrowHistoryResponse(
      escrows: (json['escrows'] as List<dynamic>)
          .map((e) => EscrowSummary.fromJson(e as Map<String, dynamic>))
          .toList(),
      hasMore: json['hasMore'] as bool? ?? false,
      total: json['total'] as int? ?? 0,
      nextToken: json['nextToken'] as String?,
    );
  }
}

/// Summary DTO for payment history listings.
class EscrowSummary {
  final String escrowId;

  /// Empty until an order-funded escrow is linked to its delivery.
  final String deliveryId;

  /// Set on order-funded escrows and order-linked initiations.
  final String? orderId;

  /// Party ids let the device cache history rows it did not initiate
  /// (offline getHistory filters by own-party id). Empty on responses from
  /// servers that predate the order-funded escrow leg.
  final String buyerId;
  final String farmerId;
  final double amount;
  final double commission;

  /// Per-party legs of [amount] (amount = farmerPayout + truckerPayout +
  /// commission). Null on responses from servers that predate SIM-15; callers
  /// fall back to amount - commission - truckerPayout.
  final double? farmerPayout;
  final double? truckerPayout;
  final String? truckerId;
  final String currency;
  final String status;
  final String createdAt;
  final String? counterpartyName;
  final String? completedAt;

  /// Auto-refund deadline; the server sets it to "now" when a cancelled
  /// order's refund is scheduled.
  final String? expiresAt;

  /// SIM-20: the money is still held but on its way back to the buyer. Such a
  /// row is labelled "Refund pending", never "Funds held safely".
  final bool refundPending;
  final String? refundReason;

  /// FRD-13: earliest payout; on the farmer's and the trucker's history only.
  final String? releaseNotBefore;

  const EscrowSummary({
    required this.escrowId,
    required this.deliveryId,
    required this.amount,
    required this.commission,
    required this.currency,
    required this.status,
    required this.createdAt,
    this.orderId,
    this.buyerId = '',
    this.farmerId = '',
    this.farmerPayout,
    this.truckerPayout,
    this.truckerId,
    this.counterpartyName,
    this.completedAt,
    this.expiresAt,
    this.refundPending = false,
    this.refundReason,
    this.releaseNotBefore,
  });

  factory EscrowSummary.fromJson(Map<String, dynamic> json) {
    return EscrowSummary(
      escrowId: json['escrowId'] as String,
      // Order-funded escrows have no delivery until one is created and linked;
      // the server sends "" but be tolerant of a null from older payloads.
      deliveryId: json['deliveryId'] as String? ?? '',
      orderId: json['orderId'] as String?,
      buyerId: json['buyerId'] as String? ?? '',
      farmerId: json['farmerId'] as String? ?? '',
      amount: (json['amount'] as num).toDouble(),
      commission: (json['commission'] as num).toDouble(),
      farmerPayout: (json['farmerPayout'] as num?)?.toDouble(),
      truckerPayout: (json['truckerPayout'] as num?)?.toDouble(),
      truckerId: json['truckerId'] as String?,
      currency: json['currency'] as String,
      status: json['status'] as String,
      createdAt: json['createdAt'] as String,
      counterpartyName: json['counterpartyName'] as String?,
      completedAt: json['completedAt'] as String?,
      expiresAt: json['expiresAt'] as String?,
      refundPending: json['refundPending'] as bool? ?? false,
      refundReason: json['refundReason'] as String?,
      releaseNotBefore: json['releaseNotBefore'] as String?,
    );
  }
}

/// Immutable audit record for escrow state changes (read-only from client).
class EscrowAuditEntry {
  final String auditId;
  final String escrowId;
  final String action;
  final String fromState;
  final String toState;
  final String actor;
  final String timestamp;
  final String? telebirrRef;
  final Map<String, String>? metadata;

  const EscrowAuditEntry({
    required this.auditId,
    required this.escrowId,
    required this.action,
    required this.fromState,
    required this.toState,
    required this.actor,
    required this.timestamp,
    this.telebirrRef,
    this.metadata,
  });

  factory EscrowAuditEntry.fromJson(Map<String, dynamic> json) {
    return EscrowAuditEntry(
      auditId: json['auditId'] as String,
      escrowId: json['escrowId'] as String,
      action: json['action'] as String,
      fromState: json['fromState'] as String,
      toState: json['toState'] as String,
      actor: json['actor'] as String,
      timestamp: json['timestamp'] as String,
      telebirrRef: json['telebirrRef'] as String?,
      metadata: (json['metadata'] as Map<String, dynamic>?)
          ?.map((k, v) => MapEntry(k, v as String)),
    );
  }
}
