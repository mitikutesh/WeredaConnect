using System.Globalization;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// Core escrow business logic implementing the full escrow state machine
/// per the escrow-protocol.md contract. Manages fund holds, releases,
/// disputes, and refunds via Telebirr integration.
/// AOT-compatible — no reflection used.
/// </summary>
public sealed class EscrowService
{
    private readonly IEscrowRepository _escrowRepo;
    private readonly IEscrowAuditRepository _auditRepo;
    private readonly IEscrowIdempotencyRepository? _idempotencyRepoOrNull;
    private readonly ITelebirrGateway? _telebirrGatewayOrNull;
    private readonly IUserRepository? _userRepo;
    private readonly IOrderRepository? _orderRepo;
    private readonly IDeliveryRepository? _deliveryRepo;
    private readonly IOrderCheckoutRepository? _orderCheckoutRepo;

    // ESC-03: moves the order and delivery to a dispute verdict before it is settled.
    private readonly DisputeVerdictMirror _verdictMirror;
    private readonly ITransactionAuditRepository? _transactionAuditRepo;

    // ESC-09: moves the order after the money (disbursing, settled).
    private readonly OrderMoneyMirror _orderMoneyMirror;

    // Money-moving entry points (initiate/release/dispute/refund) require the
    // Telebirr + idempotency wiring. The order-funded escrow paths
    // (CreateFromFundedOrderAsync / LinkDeliveryToOrderEscrowAsync /
    // ScheduleRefundForOrderAsync) never touch them, so SyncFunction constructs
    // this service without those deps; calling a money-moving method on such an
    // instance is a programming error surfaced by these guards.
    private IEscrowIdempotencyRepository _idempotencyRepo =>
        _idempotencyRepoOrNull ?? throw new InvalidOperationException(
            "EscrowService constructed without idempotency wiring — money-moving operations unavailable.");

    private ITelebirrGateway _telebirrGateway =>
        _telebirrGatewayOrNull ?? throw new InvalidOperationException(
            "EscrowService constructed without a Telebirr gateway — money-moving operations unavailable.");

    // Who/where for the current operation, set at each public entry point. Lambda
    // runs one invocation per instance at a time, so a per-call field is safe.
    // Lets the escrow audit trail finally record ipAddress + correlationId.
    private AuditContext? _auditContext;

    /// <summary>
    /// Delivery states in which a new escrow may be initiated (contract §Delivery State
    /// Validation: "must be pending or pickedUp"). `inTransit` is intentionally excluded —
    /// the contract only permits it when an escrow pre-dated transit, which cannot be true
    /// at initiation.
    /// </summary>
    private static readonly HashSet<string> EscrowableDeliveryStates = new()
    {
        "pending", "pickedUp"
    };

    private const decimal DefaultCommissionRate = 0.025m;
    private const decimal MinCommission = 5.0m;
    private const decimal MinAmount = 100.0m;
    private const decimal MaxAmount = 500_000.0m;
    private const decimal FaydaThreshold = 10_000.0m;
    private const int EscrowExpiryDays = 7;
    private const int DisputeWindowHours = 48;
    private const int MaxDisputeDescriptionLength = 1000;
    private const int MaxEvidenceRefs = 5;
    private const int MaxEvidenceRefLength = 200;

    /// <summary>
    /// ESC-01: how long an order checkout stays payable; matches the preOrder's
    /// timeout_express (120 minutes) in TelebirrSandboxGateway.
    /// </summary>
    private static readonly TimeSpan OrderCheckoutWindow = TimeSpan.FromMinutes(120);

    /// <summary>
    /// ESC-01: extra time a checkout blocks a new attempt after its window, for clock skew and
    /// a payment Telebirr accepts in the last minute.
    /// </summary>
    private static readonly TimeSpan OrderCheckoutSafetyMargin = TimeSpan.FromMinutes(10);

    /// <summary>
    /// ESC-01: new checkout attempts are refused this long after the order entered
    /// <c>payment_submitted</c>. The order cron marks such orders <c>payment_failed</c> after
    /// 24 hours, so no checkout can still be open (window + margin) when that happens.
    /// </summary>
    private static readonly TimeSpan OrderPaymentAcceptWindow = TimeSpan.FromHours(20);

    /// <summary>ESC-01: order statuses in which the buyer's money is already held.</summary>
    private static readonly HashSet<string> FundedOrderStatuses = new()
    {
        "funded", "accepted", "pickup_confirmed", "in_transit", "delivered",
        "disbursing", "settled", "disputed", "dispute_resolved"
    };

    /// <summary>
    /// ESC-01: gateway failures after which Telebirr may still have created the payment
    /// (the request timed out, or the answer was lost or unreadable). Anything else is a
    /// definite refusal.
    /// </summary>
    private static readonly HashSet<string> UncertainGatewayErrors = new()
    {
        TelebirrGatewayErrors.Timeout, TelebirrGatewayErrors.Network, "TELEBIRR_PREORDER"
    };

    /// <summary>
    /// ESC-05: failure code of a gateway call that threw (<c>GATEWAY_EXCEPTION:&lt;type&gt;</c>).
    /// The request may have reached Telebirr, so the outcome is unknown.
    /// </summary>
    private const string GatewayExceptionCodePrefix = "GATEWAY_EXCEPTION:";

    /// <summary>
    /// The one queue failure after which nothing can have moved: the gateway said it has no
    /// payout API. Every other <c>MANUAL_QUEUE_FAILED:&lt;reason&gt;</c> is an unknown outcome
    /// (ESC-13, ESC-05).
    /// </summary>
    private const string QueueFailedNotSupportedCode =
        ManualPayoutFields.QueueFailedCodePrefix + ManualPayoutFields.ReasonPayoutNotSupported;

    /// <summary>
    /// Attempts for a write that must land after the gateway may have moved money (the settled
    /// status, the manual payout queue), 150 ms times the attempt number apart.
    /// </summary>
    private const int MaxSettlementWriteAttempts = 4;

    /// <summary>
    /// ESC-05: statuses whose stale settlement lock the recovery step parks. <c>disputed</c> only
    /// carries a lock taken before ESC-03 (1.9.0), when release and refund still locked it.
    /// </summary>
    internal static readonly string[] StaleLockStatuses = { "escrowed", "dispute_resolved", "disputed" };

    /// <summary>
    /// System actor ID used by cron jobs and automated operations.
    /// </summary>
    internal const string SystemActor = "system";

    /// <summary>
    /// ESC-07: server-only escrow field the dispute freeze writes with the status: the timestamp
    /// (and so the deterministic key) of its <c>dispute</c> audit row.
    /// </summary>
    internal const string DisputeAuditAtField = "disputeAuditAt";

    private static readonly HashSet<string> ValidDisputeReasons = new()
    {
        "damaged_goods", "wrong_quantity", "wrong_crop",
        "not_delivered", "quality_mismatch", "other"
    };

    private static readonly HashSet<string> ValidReleaseTriggers = new()
    {
        "delivery_confirmed", "dispute_resolved", "admin_override"
    };

    private static readonly HashSet<string> ValidRefundTriggers = new()
    {
        "dispute_resolved", "delivery_timeout", "admin_override"
    };

    /// <summary>
    /// Escrow statuses that still hold or govern money (mirrors the 6b
    /// duplicate-initiation check). refunded/completed/cancelled are inert. ESC-03: a
    /// <c>dispute_resolved</c> escrow still holds the money until its verdict is settled.
    /// </summary>
    private static readonly HashSet<string> ActiveEscrowStatuses = new()
    {
        "initiated", "escrowed", "released", "disputed", "dispute_resolved"
    };

    /// <summary>
    /// Order statuses that end the order's life — no escrow may be created for
    /// or linked to a terminal order (order-lifecycle.md terminal states).
    /// </summary>
    private static readonly HashSet<string> TerminalOrderStatuses = new()
    {
        "rejected", "cancelled", "settled", "payment_failed", "dispute_resolved"
    };

    /// <summary>
    /// Sink for the money alarm lines (ESC-13). Defaults to stdout, which Lambda ships to
    /// the function's CloudWatch log group where the settlement-failure metric filter
    /// counts them. Tests replace it to capture the lines.
    /// </summary>
    public Action<string> AlarmLog { get; init; } = Console.WriteLine;

    public EscrowService(
        IEscrowRepository escrowRepo,
        IEscrowAuditRepository auditRepo,
        IEscrowIdempotencyRepository? idempotencyRepo,
        ITelebirrGateway? telebirrGateway,
        IUserRepository? userRepo = null,
        IOrderRepository? orderRepo = null,
        IDeliveryRepository? deliveryRepo = null,
        IOrderCheckoutRepository? orderCheckoutRepo = null,
        ITransactionAuditRepository? transactionAuditRepo = null,
        ICropRepository? cropRepo = null)
    {
        _escrowRepo = escrowRepo ?? throw new ArgumentNullException(nameof(escrowRepo));
        _auditRepo = auditRepo ?? throw new ArgumentNullException(nameof(auditRepo));
        _idempotencyRepoOrNull = idempotencyRepo;
        _telebirrGatewayOrNull = telebirrGateway;
        _userRepo = userRepo;
        _orderRepo = orderRepo;
        _deliveryRepo = deliveryRepo;
        _orderCheckoutRepo = orderCheckoutRepo;
        _transactionAuditRepo = transactionAuditRepo;
        _verdictMirror = new DisputeVerdictMirror(orderRepo, deliveryRepo, transactionAuditRepo, cropRepo);
        _orderMoneyMirror = new OrderMoneyMirror(orderRepo, deliveryRepo, transactionAuditRepo, _verdictMirror);
    }

    // ──────────────────────────────────────────────
    // Public API
    // ──────────────────────────────────────────────

    /// <summary>
    /// Initiate escrow payment for a delivery. Holds funds in buyer's Telebirr account.
    /// </summary>
    public async Task<EscrowResult<InitiateEscrowResponse>> InitiateAsync(
        InitiateEscrowRequest request, string authenticatedUserId, AuditContext? auditContext = null)
    {
        _auditContext = auditContext;

        // 1. Validate buyerId matches JWT sub → ESCROW_006
        if (!string.Equals(request.BuyerId, authenticatedUserId, StringComparison.Ordinal))
        {
            return EscrowResult<InitiateEscrowResponse>.Failure(
                403, "ESCROW_006", "buyerId does not match authenticated user.", "buyerId");
        }

        // 2. Check idempotency → return cached if duplicate
        var existing = await _idempotencyRepo.GetAsync(request.IdempotencyKey);
        if (existing != null)
        {
            var existingEscrowId = GetString(existing, "escrowId");
            if (!string.IsNullOrEmpty(existingEscrowId))
            {
                var cachedEscrow = await _escrowRepo.GetByIdAsync(existingEscrowId);
                if (cachedEscrow != null)
                {
                    // Ownership check: an idempotency key must NEVER disclose another user's
                    // escrow. The key is client-supplied, so a caller could present a victim's
                    // key; only return the cached escrow to its own buyer (A01 IDOR fix).
                    if (!string.Equals(
                            GetString(cachedEscrow, "buyerId"), authenticatedUserId, StringComparison.Ordinal))
                    {
                        return EscrowResult<InitiateEscrowResponse>.Failure(
                            403, "ESCROW_006", "buyerId does not match authenticated user.", "buyerId");
                    }

                    return EscrowResult<InitiateEscrowResponse>.Duplicate(
                        BuildInitiateResponse(cachedEscrow, isDuplicate: true));
                }
            }
        }

        // 3. Validate amount 100-500,000 ETB → ESCROW_001
        if (request.Amount < MinAmount || request.Amount > MaxAmount)
        {
            return EscrowResult<InitiateEscrowResponse>.Failure(
                400, "ESCROW_001",
                $"Invalid amount. Must be between {MinAmount} and {MaxAmount} ETB.",
                "amount");
        }

        // 3b. Validate 2-decimal (santim) precision — Telebirr cannot move fractional santim.
        if (decimal.Round(request.Amount, 2) != request.Amount)
        {
            return EscrowResult<InitiateEscrowResponse>.Failure(
                400, "ESCROW_001",
                "Invalid amount. ETB supports at most 2 decimal places (santim).",
                "amount");
        }

        // 4. Validate currency is ETB → ESCROW_004
        if (!string.Equals(request.Currency, "ETB", StringComparison.Ordinal))
        {
            return EscrowResult<InitiateEscrowResponse>.Failure(
                400, "ESCROW_004", "Currency must be ETB.", "currency");
        }

        // 5. Validate idempotency key format → ESCROW_003
        if (!IsValidIdempotencyKey(request.IdempotencyKey))
        {
            return EscrowResult<InitiateEscrowResponse>.Failure(
                400, "ESCROW_003",
                "Invalid idempotency key format. Expected: {actor}:{entityId}:{action}:{timestamp}",
                "idempotencyKey");
        }

        // 6. Check self-trading (buyerId != farmerId)
        if (string.Equals(request.BuyerId, request.FarmerId, StringComparison.Ordinal))
        {
            return EscrowResult<InitiateEscrowResponse>.Failure(
                400, "ESCROW_001",
                "Self-trading not allowed. Buyer and farmer must be different users.",
                "farmerId");
        }

        // 6b. Check for an existing active escrow on this delivery.
        var existingEscrowForDelivery = await _escrowRepo.GetByDeliveryIdAsync(request.DeliveryId);
        if (existingEscrowForDelivery != null)
        {
            var existingStatus = existingEscrowForDelivery.TryGetValue("status", out var statusObj)
                ? statusObj?.ToString() : null;
            // Only block if escrow is in an active state (not refunded/completed/expired)
            if (existingStatus is "initiated" or "escrowed" or "released" or "disputed" or "dispute_resolved")
            {
                // SIM-24: when that escrow is the order-funded record (the buyer paid at
                // order time and delivery linkage stamped this deliveryId on it), the
                // truthful answer is the double-charge code ESCROW_024, which clients key
                // their copy on. It is decided BEFORE the per-delivery duplicate answer so
                // the linked case cannot shadow it; ESCROW_009 is reserved for a second
                // delivery-initiated escrow.
                if (IsOrderFundedEscrow(existingEscrowForDelivery))
                {
                    return EscrowResult<InitiateEscrowResponse>.Failure(
                        409, "ESCROW_024",
                        "Funds for this order are already held in escrow.",
                        "orderId");
                }

                return EscrowResult<InitiateEscrowResponse>.Failure(
                    409, "ESCROW_009",
                    "An active escrow already exists for this delivery.",
                    "deliveryId");
            }
        }

        // 6c. Validate deliveryId is present → ESCROW_002
        if (string.IsNullOrWhiteSpace(request.DeliveryId))
        {
            return EscrowResult<InitiateEscrowResponse>.Failure(
                400, "ESCROW_002", "Missing or invalid deliveryId.", "deliveryId");
        }

        // 6d. Validate the delivery exists and is in an escrowable state.
        //     ESCROW_008 (404) if not found, ESCROW_010 (422) if wrong state.
        //     Capture counterparty names so escrow history shows a human name rather
        //     than falling back to the raw UUID in MapToEscrowSummaryDto.
        string? counterpartyBuyerName = null;
        string? counterpartyFarmerName = null;
        string? deliveryOrderId = null;
        if (_deliveryRepo != null)
        {
            var delivery = await _deliveryRepo.GetByIdAsync(request.DeliveryId);
            if (delivery == null)
            {
                return EscrowResult<InitiateEscrowResponse>.Failure(
                    404, "ESCROW_008", "Delivery not found.", "deliveryId");
            }

            // SIM-04: bind the payment to the delivery's own parties. Without this any
            // authenticated user could hold money against someone else's delivery (and
            // block the real buyer with ESCROW_009) or name an arbitrary payee.
            var deliveryBuyerId = GetStringOrNull(delivery, "buyerId");
            if (string.IsNullOrEmpty(deliveryBuyerId)
                || !string.Equals(deliveryBuyerId, authenticatedUserId, StringComparison.Ordinal))
            {
                return EscrowResult<InitiateEscrowResponse>.Failure(
                    403, "ESCROW_026", "Only the buyer on this delivery can initiate its escrow.", "deliveryId");
            }

            var deliveryFarmerId = GetStringOrNull(delivery, "farmerId");
            if (string.IsNullOrEmpty(deliveryFarmerId)
                || !string.Equals(deliveryFarmerId, request.FarmerId, StringComparison.Ordinal))
            {
                return EscrowResult<InitiateEscrowResponse>.Failure(
                    400, "ESCROW_027", "farmerId does not match the farmer on this delivery.", "farmerId");
            }

            var deliveryStatus = delivery.TryGetValue("status", out var ds) ? ds?.ToString() : null;
            if (deliveryStatus == null || !EscrowableDeliveryStates.Contains(deliveryStatus))
            {
                return EscrowResult<InitiateEscrowResponse>.Failure(
                    422, "ESCROW_010",
                    $"Delivery is not in a valid state for payment (was '{deliveryStatus}').",
                    "deliveryId");
            }

            counterpartyBuyerName = GetStringOrNull(delivery, "buyerName");
            counterpartyFarmerName = GetStringOrNull(delivery, "farmerName");
            deliveryOrderId = GetStringOrNull(delivery, "orderId");
        }

        // 6e. Resolve the linked order. The delivery's own orderId is authoritative; a
        //     client-supplied orderId may only echo it (SIM-04) and is the fallback when
        //     the delivery carries none.
        if (!string.IsNullOrEmpty(request.OrderId) && !string.IsNullOrEmpty(deliveryOrderId)
            && !string.Equals(request.OrderId, deliveryOrderId, StringComparison.Ordinal))
        {
            return EscrowResult<InitiateEscrowResponse>.Failure(
                400, "ESCROW_VALIDATION", "Referenced order is for a different delivery.", "orderId");
        }

        var linkedOrderId = string.IsNullOrEmpty(deliveryOrderId) ? request.OrderId : deliveryOrderId;

        // 6f. Double-charge guard: when the delivery's order already holds an active
        //     escrow (the order-funded leg created at order funding), a second payment
        //     for the same goods must be rejected → ESCROW_024.
        var guardOrderId = linkedOrderId;
        if (!string.IsNullOrEmpty(guardOrderId))
        {
            var orderEscrow = await GetActiveEscrowByOrderIdAsync(guardOrderId);
            if (orderEscrow != null)
            {
                return EscrowResult<InitiateEscrowResponse>.Failure(
                    409, "ESCROW_024",
                    "Funds for this order are already held in escrow.",
                    "orderId");
            }
        }

        // 6g. SIM-04: the escrow must pay for exactly the linked order. Load it once
        //     (reused for the payout split below), bind it to this buyer and delivery,
        //     and require amount == orderTotal. Fail closed when the order cannot be
        //     read: an amount that cannot be verified must never be held.
        Dictionary<string, object>? linkedOrder = null;
        if (!string.IsNullOrEmpty(linkedOrderId) && _orderRepo != null)
        {
            linkedOrder = await _orderRepo.GetByIdAsync(linkedOrderId);
            if (linkedOrder == null)
            {
                return EscrowResult<InitiateEscrowResponse>.Failure(
                    422, "ESCROW_VALIDATION",
                    "Linked order not found; the escrow amount cannot be verified.", "orderId");
            }

            if (linkedOrder.TryGetValue("buyerId", out var ob) && ob?.ToString() is string obs
                && !string.Equals(obs, request.BuyerId, StringComparison.Ordinal))
            {
                return EscrowResult<InitiateEscrowResponse>.Failure(
                    403, "ESCROW_VALIDATION", "Referenced order does not belong to this buyer.", "orderId");
            }

            if (linkedOrder.TryGetValue("deliveryId", out var od) && od?.ToString() is string ods
                && !string.Equals(ods, request.DeliveryId, StringComparison.Ordinal))
            {
                return EscrowResult<InitiateEscrowResponse>.Failure(
                    400, "ESCROW_VALIDATION", "Referenced order is for a different delivery.", "orderId");
            }

            // ESC-01: the order itself says its money is held (funded, or paid by a live
            // Telebirr checkout) even when its escrow record could not be written. Never take
            // a second payment for it.
            if (FundedOrderStatuses.Contains(GetString(linkedOrder, "status"))
                || !string.IsNullOrEmpty(GetStringOrNull(linkedOrder, OrderFundingService.PaymentOrderIdField)))
            {
                return EscrowResult<InitiateEscrowResponse>.Failure(
                    409, "ESCROW_024",
                    "Funds for this order are already held in escrow.",
                    "orderId");
            }

            var orderTotal = RoundMoney(GetDecimal(linkedOrder, "orderTotal"));
            if (orderTotal <= 0m || orderTotal != RoundMoney(request.Amount))
            {
                return EscrowResult<InitiateEscrowResponse>.Failure(
                    400, "ESCROW_028",
                    $"amount must equal the order total of {orderTotal.ToString("0.00", CultureInfo.InvariantCulture)} ETB.",
                    "amount");
            }
        }

        // 7. User validation + Fayda ID verification for high-value transactions → ESCROW_007
        var buyerFaydaVerified = false;
        var farmerFaydaVerified = false;
        var requiresFayda = request.Amount > FaydaThreshold;
        var buyerPhone = request.BuyerId; // resolved from the profile below when available

        // Fail closed: a high-value transaction MUST have a verification source.
        if (requiresFayda && _userRepo == null)
        {
            return EscrowResult<InitiateEscrowResponse>.Failure(
                403, "ESCROW_007",
                "Fayda ID verification is required for transactions above 10,000 ETB, "
                + "but the verification service is unavailable.",
                "buyerId");
        }

        if (_userRepo != null)
        {
            var buyer = await _userRepo.GetUserByIdAsync(request.BuyerId);
            var farmer = await _userRepo.GetUserByIdAsync(request.FarmerId);
            buyerPhone = buyer?.PhoneNumber ?? buyerPhone;

            // Farmer must exist, have the farmer role, and be phone-verified (contract §User
            // Validation). These use ESCROW_VALIDATION (not ESCROW_008/006, which carry
            // "delivery not found" / "buyerId mismatch" messages) to avoid misleading the client.
            if (farmer == null)
            {
                return EscrowResult<InitiateEscrowResponse>.Failure(
                    404, "ESCROW_VALIDATION", "Farmer account not found.", "farmerId");
            }

            if (!string.Equals(farmer.Role, "farmer", StringComparison.OrdinalIgnoreCase))
            {
                return EscrowResult<InitiateEscrowResponse>.Failure(
                    403, "ESCROW_VALIDATION", "Recipient is not a registered farmer.", "farmerId");
            }

            // Fail closed: a missing buyer record means we cannot confirm phone verification.
            if (!farmer.IsPhoneVerified || buyer == null || !buyer.IsPhoneVerified)
            {
                return EscrowResult<InitiateEscrowResponse>.Failure(
                    403, "ESCROW_VALIDATION", "Both buyer and farmer must be phone-verified.", "farmerId");
            }

            buyerFaydaVerified = buyer?.FaydaVerified == true;
            farmerFaydaVerified = farmer.FaydaVerified;

            if (requiresFayda && !buyerFaydaVerified)
            {
                return EscrowResult<InitiateEscrowResponse>.Failure(
                    403, "ESCROW_007",
                    "Buyer Fayda ID verification required for transactions above 10,000 ETB.",
                    "buyerId");
            }

            if (requiresFayda && !farmerFaydaVerified)
            {
                return EscrowResult<InitiateEscrowResponse>.Failure(
                    403, "ESCROW_007",
                    "Farmer Fayda ID verification required for transactions above 10,000 ETB.",
                    "farmerId");
            }
        }

        // 8. Calculate commission: round(max(amount * 0.025, 5.00 ETB)) to 2 dp (santim).
        var commission = RoundMoney(Math.Max(request.Amount * DefaultCommissionRate, MinCommission));
        var farmerPayout = request.Amount - commission;

        // 9. Create escrow record with status "initiated"
        var now = DateTime.UtcNow;
        var escrowId = $"escrow-{Guid.NewGuid():N}";
        var createdAt = now.ToString("o");
        var expiresAt = now.AddDays(EscrowExpiryDays).ToString("o");

        // The linked order (resolved and bound in 6g) supplies the 3-way split amounts.
        // It applies whether the client sent orderId or the delivery carried it, so the
        // delivery-initiated leg pays out exactly like the order-funded leg.
        decimal? orderCropCost = null;
        decimal? orderTransportCost = null;
        decimal? orderCommissionAmount = null;
        string? truckerId = null;
        if (linkedOrder != null)
        {
            if (linkedOrder.TryGetValue("cropCost", out var cc))
            {
                orderCropCost = decimal.TryParse(cc?.ToString(), out var ccVal) ? ccVal : null;
            }

            if (linkedOrder.TryGetValue("transportCost", out var tc))
            {
                orderTransportCost = decimal.TryParse(tc?.ToString(), out var tcVal) ? tcVal : null;
            }

            if (linkedOrder.TryGetValue("commissionAmount", out var ca))
            {
                orderCommissionAmount = decimal.TryParse(ca?.ToString(), out var caVal) ? caVal : null;
            }

            if (linkedOrder.TryGetValue("truckerId", out var tid))
            {
                truckerId = tid?.ToString();
            }
        }

        // When an order supplies split amounts, use them instead of the default calculation —
        // but ALWAYS reconcile the full split to the held amount so the platform never releases
        // more (or less) than was escrowed. The reconciliation must run whenever ANY split
        // component is present (not just cropCost); a transport-only order previously skipped
        // this and over-released by the transport cost (C2/C3 payout-integrity fix).
        decimal truckerPayout = RoundMoney(orderTransportCost ?? 0m);
        if (orderCropCost.HasValue || orderTransportCost.HasValue || orderCommissionAmount.HasValue)
        {
            farmerPayout = RoundMoney(orderCropCost ?? 0m);
            commission = RoundMoney(orderCommissionAmount ?? commission);

            if (farmerPayout + truckerPayout + commission != RoundMoney(request.Amount))
            {
                return EscrowResult<InitiateEscrowResponse>.Failure(
                    400, "ESCROW_001",
                    "Order split (farmer + trucker + commission) does not reconcile to the escrow amount.",
                    "amount");
            }
        }

        var escrowRecord = new Dictionary<string, object>
        {
            { "escrowId", escrowId },
            { "deliveryId", request.DeliveryId },
            { "buyerId", request.BuyerId },
            { "farmerId", request.FarmerId },
            { "amount", request.Amount },
            { "commission", commission },
            { "commissionRate", DefaultCommissionRate },
            { "farmerPayout", farmerPayout },
            { "currency", "ETB" },
            { "status", "initiated" },
            { "buyerFaydaVerified", buyerFaydaVerified },
            { "farmerFaydaVerified", farmerFaydaVerified },
            { "createdAt", createdAt },
            { "updatedAt", createdAt },
            { "expiresAt", expiresAt },
            { "version", 1 }
        };

        if (!string.IsNullOrEmpty(linkedOrderId))
        {
            escrowRecord["orderId"] = linkedOrderId;
        }

        if (truckerPayout > 0)
        {
            escrowRecord["truckerPayout"] = truckerPayout;
        }

        if (!string.IsNullOrEmpty(truckerId))
        {
            escrowRecord["truckerId"] = truckerId;
        }
        // Persist counterparty names (sourced from the delivery record above) so escrow
        // history shows a human name rather than falling back to the raw UUID.
        if (!string.IsNullOrEmpty(counterpartyBuyerName))
        {
            escrowRecord["buyerName"] = counterpartyBuyerName;
        }

        if (!string.IsNullOrEmpty(counterpartyFarmerName))
        {
            escrowRecord["farmerName"] = counterpartyFarmerName;
        }

        // 10. Store idempotency record BEFORE calling Telebirr (conditional write prevents races)
        var stored = await _idempotencyRepo.StoreAsync(request.IdempotencyKey, escrowId, "processing");
        if (!stored)
        {
            // Concurrent request already claimed this idempotency key
            return EscrowResult<InitiateEscrowResponse>.Failure(
                409, "ESCROW_003", "Duplicate request detected. Processing in progress.", "idempotencyKey");
        }

        // 11. Save escrow to DynamoDB
        await _escrowRepo.CreateAsync(escrowRecord);

        // 11. Audit log: "" → "initiated"
        await AppendAuditAsync(escrowId, "initiate", "", "initiated", authenticatedUserId);

        // 12. Call Telebirr HoldFunds.
        //     transactionRef = the per-attempt idempotency key (contract §Telebirr: the ref maps
        //     to the escrow idempotencyKey). It is unique per attempt, so a legitimate
        //     re-initiation after a terminal escrow on the same delivery does NOT dedupe to a
        //     stale hold (a per-delivery ref would — that was a phantom-hold bug).
        //     NOTE: a truly-concurrent double-initiation with DISTINCT keys is still guarded only
        //     by the (non-atomic) active-escrow check above; an atomic delivery-uniqueness write
        //     (TransactWriteItems) is tracked as follow-up.
        var holdResponse = await _telebirrGateway.HoldFundsAsync(new HoldFundsRequest
        {
            TransactionRef = request.IdempotencyKey,
            BuyerPhone = buyerPhone,
            Amount = request.Amount,
            Description = $"WeredaConnect escrow for delivery {request.DeliveryId}"
        });

        // 12b. Asynchronous C2B (real gateway): a ToPayUrl means funds are NOT yet
        //      confirmed — the buyer must complete payment in Telebirr, and the
        //      webhook (TelebirrWebhookFunction) later transitions initiated→escrowed.
        //      Keep status "initiated", leave the idempotency record "processing"
        //      (reconciliation/expiry handle a webhook that never arrives), and return
        //      the ToPayUrl transiently. The ToPayUrl is NEVER persisted (SEC-05).
        if (holdResponse.Success && holdResponse.ToPayUrl != null)
        {
            await AppendAuditAsync(escrowId, "hold_pending", "initiated", "initiated",
                "system", holdResponse.TelebirrRef);

            return EscrowResult<InitiateEscrowResponse>.Success(new InitiateEscrowResponse
            {
                EscrowId = escrowId,
                DeliveryId = request.DeliveryId,
                BuyerId = request.BuyerId,
                FarmerId = request.FarmerId,
                Amount = request.Amount,
                Commission = commission,
                FarmerPayout = farmerPayout,
                Currency = "ETB",
                Status = "initiated",
                TelebirrRef = holdResponse.TelebirrRef,
                CreatedAt = createdAt,
                ExpiresAt = expiresAt,
                Message = "Escrow initiated. Complete payment in Telebirr to hold funds.",
                ToPayUrl = holdResponse.ToPayUrl
            });
        }

        // 13. If hold succeeds synchronously (mock): transition to "escrowed", audit, update idempotency
        if (holdResponse.Success)
        {
            var holdRef = holdResponse.TelebirrRef ?? "";
            await _escrowRepo.UpdateStatusAsync(escrowId, createdAt, "escrowed", "initiated",
                new Dictionary<string, object>
                {
                    { "telebirrRef", holdRef },
                    { "telebirrHoldRef", holdRef },
                    { "version", 2 }
                });

            await AppendAuditAsync(escrowId, "hold_success", "initiated", "escrowed",
                "system", holdResponse.TelebirrRef);

            await _idempotencyRepo.UpdateAsync(request.IdempotencyKey, "completed");

            // 14. Return InitiateEscrowResponse
            return EscrowResult<InitiateEscrowResponse>.Success(new InitiateEscrowResponse
            {
                EscrowId = escrowId,
                DeliveryId = request.DeliveryId,
                BuyerId = request.BuyerId,
                FarmerId = request.FarmerId,
                Amount = request.Amount,
                Commission = commission,
                FarmerPayout = farmerPayout,
                Currency = "ETB",
                Status = "escrowed",
                TelebirrRef = holdResponse.TelebirrRef,
                CreatedAt = createdAt,
                ExpiresAt = expiresAt,
                Message = "Escrow initiated. Funds held in escrow."
            });
        }

        // Hold failed — cancel orphaned escrow, audit, and delete idempotency record
        // to allow clean retry with the same key (critical for 3G/intermittent connectivity)
        await _escrowRepo.UpdateStatusAsync(escrowId, createdAt, "cancelled", "initiated");
        await AppendAuditAsync(escrowId, "hold_failed", "initiated", "cancelled",
            "system", metadata: holdResponse.ErrorCode);
        await _idempotencyRepo.DeleteAsync(request.IdempotencyKey);

        return EscrowResult<InitiateEscrowResponse>.Failure(
            502, "ESCROW_013",
            holdResponse.ErrorMessage ?? "Telebirr gateway error during fund hold.");
    }

    /// <summary>
    /// Create the escrow record for an order that just became "funded" — the payment
    /// leg has already verified and holds the money, so the record is born "escrowed"
    /// and no Telebirr call is made (escrow-protocol.md §Order-funded escrow).
    ///
    /// Idempotent by key design: escrowId = "escrow-order-{orderId}" and
    /// createdAt = the order's paymentVerifiedAt, so retries and races collapse onto
    /// the same (PK, SK) and CreateAsync's attribute_not_exists condition rejects
    /// duplicates. No deliveryId is written — the sparse DeliveryIndex gains the
    /// record only once LinkDeliveryToOrderEscrowAsync attaches a delivery.
    ///
    /// Returns true when an active escrow exists for the order after this call
    /// (created now, or already present).
    /// </summary>
    public async Task<bool> CreateFromFundedOrderAsync(
        Dictionary<string, object> order, string actor, AuditContext? auditContext = null)
    {
        _auditContext = auditContext;

        var orderId = GetString(order, "orderId");
        var buyerId = GetString(order, "buyerId");
        var farmerId = GetString(order, "farmerId");
        if (orderId.Length == 0 || buyerId.Length == 0 || farmerId.Length == 0)
        {
            return false;
        }

        // Self-trade orders must never mint an escrow (mirrors InitiateAsync step 6).
        if (string.Equals(buyerId, farmerId, StringComparison.Ordinal))
        {
            return false;
        }

        var amount = RoundMoney(GetDecimal(order, "orderTotal"));
        if (amount <= 0)
        {
            // Legacy orders without the cost breakdown: crop subtotal.
            amount = RoundMoney(GetDecimal(order, "quantity") * GetDecimal(order, "pricePerUnit"));
        }

        if (amount <= 0)
        {
            return false;
        }

        // Fast path — funding retried, or a delivery-leg escrow already covers it.
        if (await GetActiveEscrowByOrderIdAsync(orderId) != null)
        {
            return true;
        }

        // Split from the order's own cost fields; commission is the exact remainder so
        // farmerPayout + truckerPayout + commission always reconciles to the held
        // amount (release pays the payouts, refund pays the amount — drift would
        // over- or under-release).
        var truckerPayout = RoundMoney(Math.Clamp(GetDecimalOrNull(order, "transportCost") ?? 0m, 0m, amount));
        var truckerId = GetStringOrNull(order, "truckerId");
        var farmerPayout = RoundMoney(GetDecimalOrNull(order, "cropCost") ?? 0m);
        if (farmerPayout <= 0m || farmerPayout + truckerPayout > amount)
        {
            // Legacy/inconsistent split: default commission rule, farmer takes the rest.
            var defaultCommission = RoundMoney(Math.Max(amount * DefaultCommissionRate, MinCommission));
            farmerPayout = Math.Max(amount - truckerPayout - defaultCommission, 0m);
        }

        var commission = amount - farmerPayout - truckerPayout;

        // Fayda flags are best-effort here: the money is ALREADY held by the payment
        // leg, so refusing the record would only hide it. KYC enforcement for the
        // order-payment leg is a contract-documented follow-up.
        var buyerFaydaVerified = false;
        var farmerFaydaVerified = false;
        if (_userRepo != null)
        {
            var buyer = await _userRepo.GetUserByIdAsync(buyerId);
            var farmer = await _userRepo.GetUserByIdAsync(farmerId);
            buyerFaydaVerified = buyer?.FaydaVerified == true;
            farmerFaydaVerified = farmer?.FaydaVerified == true;
        }

        var createdAt = GetStringOrNull(order, "paymentVerifiedAt")
            ?? DateTime.UtcNow.ToString("o");
        var expiresAt = (DateTime.TryParse(
                createdAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var createdAtUtc)
                ? createdAtUtc
                : DateTime.UtcNow)
            .AddDays(EscrowExpiryDays).ToString("o");
        // A live Telebirr payment records its reference in a server-only field (ESC-01); it
        // wins over cbeTransactionId, which the buyer's client can still write.
        var paymentRef = GetStringOrNull(order, OrderFundingService.PaymentOrderIdField)
            ?? GetStringOrNull(order, "cbeTransactionId")
            ?? $"ORDER-FUND-{orderId}";
        var escrowId = $"escrow-order-{orderId}";

        var record = new Dictionary<string, object>
        {
            { "escrowId", escrowId },
            { "orderId", orderId },
            { "buyerId", buyerId },
            { "farmerId", farmerId },
            { "amount", amount },
            { "commission", commission },
            { "commissionRate", GetDecimalOrNull(order, "commissionRate") ?? DefaultCommissionRate },
            { "farmerPayout", farmerPayout },
            { "currency", "ETB" },
            { "status", "escrowed" },
            { "source", "order" },
            { "telebirrRef", paymentRef },
            { "telebirrHoldRef", paymentRef },
            { "buyerFaydaVerified", buyerFaydaVerified },
            { "farmerFaydaVerified", farmerFaydaVerified },
            { "createdAt", createdAt },
            { "updatedAt", DateTime.UtcNow.ToString("o") },
            { "expiresAt", expiresAt },
            { "version", 1 }
        };

        if (truckerPayout > 0)
        {
            record["truckerPayout"] = truckerPayout;
        }

        if (!string.IsNullOrEmpty(truckerId))
        {
            record["truckerId"] = truckerId;
        }

        var buyerName = GetStringOrNull(order, "buyerName");
        var farmerName = GetStringOrNull(order, "farmerName");
        if (!string.IsNullOrEmpty(buyerName))
        {
            record["buyerName"] = buyerName;
        }

        if (!string.IsNullOrEmpty(farmerName))
        {
            record["farmerName"] = farmerName;
        }

        try
        {
            await _escrowRepo.CreateAsync(record);
        }
        catch (ConditionalCheckFailedException)
        {
            // Same deterministic key already written by a concurrent attempt.
            return true;
        }

        await AppendAuditAsync(
            escrowId, "order_funded", "", "escrowed", actor,
            telebirrRef: paymentRef, metadata: $"orderId={orderId}");

        return true;
    }

    /// <summary>
    /// ESC-01: open the live Telebirr checkout for a <c>payment_submitted</c> order and return
    /// its checkout URL. The payment gates (KYC, step-up) already ran when the order moved to
    /// <c>payment_submitted</c>; this only binds a Telebirr payment to the order.
    ///
    /// Money safety:
    /// - The amount, buyer and merch_order_id come from the server, never the client.
    /// - An order has at most one open checkout. A retry while it is open gets the same payment
    ///   back (a freshly signed URL for the stored prepay_id), never a second payable one.
    /// - The merch_order_id mapping is written before Telebirr is called and is never deleted,
    ///   so a payment Telebirr confirms late still reaches its order. When Telebirr's answer is
    ///   lost, the checkout stays blocked until its window ends.
    /// </summary>
    public async Task<EscrowResult<OrderCheckoutResponse>> CreateOrderCheckoutAsync(
        string orderId, string authenticatedUserId, AuditContext? auditContext = null)
    {
        _auditContext = auditContext;

        if (_orderRepo == null || _orderCheckoutRepo == null
            || _telebirrGatewayOrNull == null || _idempotencyRepoOrNull == null)
        {
            return EscrowResult<OrderCheckoutResponse>.Failure(
                503, "ESCROW_012", "Payment is temporarily unavailable. Please try again.");
        }

        // Mock payments fund an order at payment submit, so there is no checkout to open.
        // Decided before any write so nothing is left behind.
        if (!_telebirrGatewayOrNull.SupportsHostedCheckout)
        {
            return EscrowResult<OrderCheckoutResponse>.Failure(
                409, "ESCROW_031",
                "Payments are confirmed automatically in this environment; there is no Telebirr checkout to open.");
        }

        if (string.IsNullOrWhiteSpace(orderId))
        {
            return EscrowResult<OrderCheckoutResponse>.Failure(400, "ESCROW_033", "orderId is required.", "orderId");
        }

        var order = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
        if (order == null)
        {
            return EscrowResult<OrderCheckoutResponse>.Failure(404, "ESCROW_033", "Order not found.", "orderId");
        }

        var validation = ValidateOrderForCheckout(order, authenticatedUserId);
        if (validation != null)
        {
            return validation;
        }

        var now = DateTime.UtcNow;
        var cycle = PaymentCycle.Of(order);
        var slot = await _orderCheckoutRepo.GetSlotAsync(orderId);
        if (slot != null && slot.IsOpen(now))
        {
            return await ResumeOpenCheckoutAsync(slot, cycle);
        }

        // No new payment once the order has waited too long: the order cron is about to mark
        // it payment_failed, and a checkout opened now could outlive that.
        if (cycle.HasRunOut(now))
        {
            return PaymentTimeRunOut();
        }

        var attempt = (slot?.Attempt ?? 0) + 1;
        var newSlot = new OrderCheckoutSlot
        {
            OrderId = orderId,
            BuyerId = authenticatedUserId,
            Attempt = attempt,
            MerchOrderId = BuildOrderMerchOrderId(orderId, attempt),
            Amount = RoundMoney(GetDecimal(order, "orderTotal")),
            Outcome = OrderCheckoutOutcome.Opening,
            OpenedAtUtc = now,
            OpenUntilUtc = now.Add(OrderCheckoutWindow).Add(OrderCheckoutSafetyMargin)
        };
        if (!await _orderCheckoutRepo.TryOpenSlotAsync(newSlot, slot?.Attempt))
        {
            return CheckoutBusy();
        }

        return await StartCheckoutAttemptAsync(newSlot, cycle);
    }

    /// <summary>
    /// ESC-01: the order's current payment attempt, from its entry into <c>payment_submitted</c>
    /// (<c>paymentSubmittedAt</c>; <c>createdAt</c> for orders from before it was stamped, as
    /// the order cron does). A checkout is bound to it, so a payment opened in an earlier
    /// attempt can never fund a later one.
    /// </summary>
    private readonly record struct PaymentCycle(string? SubmittedAt, DateTime? ClockStartUtc)
    {
        public static PaymentCycle Of(Dictionary<string, object> order)
        {
            var submittedAt = GetStringOrNull(order, "paymentSubmittedAt");
            return new PaymentCycle(submittedAt, ParseUtcOrNull(submittedAt) ?? ParseUtcOrNull(GetStringOrNull(order, "createdAt")));
        }

        /// <summary>True once no new Telebirr payment may start for this attempt.</summary>
        public bool HasRunOut(DateTime nowUtc)
            => ClockStartUtc != null && nowUtc > ClockStartUtc.Value.Add(OrderPaymentAcceptWindow);

        private static DateTime? ParseUtcOrNull(string? value)
            => DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
    }

    private static EscrowResult<OrderCheckoutResponse> PaymentTimeRunOut()
        => EscrowResult<OrderCheckoutResponse>.Failure(
            409, "ESCROW_030",
            "The payment time for this order has run out. It will be marked as failed; then submit the payment again.",
            "orderId");

    /// <summary>
    /// ESC-01: order-level preconditions for a checkout. Mirrors what
    /// <see cref="CreateFromFundedOrderAsync"/> needs, so money is never captured for an order
    /// whose escrow could not then be created. Null when the order may be paid.
    /// </summary>
    private static EscrowResult<OrderCheckoutResponse>? ValidateOrderForCheckout(
        Dictionary<string, object> order, string authenticatedUserId)
    {
        var buyerId = GetString(order, "buyerId");
        if (!string.Equals(buyerId, authenticatedUserId, StringComparison.Ordinal))
        {
            return EscrowResult<OrderCheckoutResponse>.Failure(
                403, "ESCROW_026", "Only the order's buyer can pay for it.", "orderId");
        }

        var status = GetString(order, "status");
        if (FundedOrderStatuses.Contains(status))
        {
            return EscrowResult<OrderCheckoutResponse>.Failure(
                409, "ESCROW_024", "Funds for this order are already held in escrow.", "orderId");
        }

        if (status != "payment_submitted")
        {
            return EscrowResult<OrderCheckoutResponse>.Failure(
                409, "ESCROW_030", $"The order is not waiting for payment (status: {status}).", "orderId");
        }

        var farmerId = GetString(order, "farmerId");
        if (farmerId.Length == 0 || string.Equals(farmerId, buyerId, StringComparison.Ordinal))
        {
            return EscrowResult<OrderCheckoutResponse>.Failure(
                422, "ESCROW_030", "This order cannot be paid online.", "orderId");
        }

        var amount = RoundMoney(GetDecimal(order, "orderTotal"));
        if (amount < MinAmount || amount > MaxAmount)
        {
            return EscrowResult<OrderCheckoutResponse>.Failure(
                400, "ESCROW_001",
                $"Invalid amount. Telebirr takes between {MinAmount} and {MaxAmount} ETB in one payment.",
                "amount");
        }

        return null;
    }

    /// <summary>
    /// ESC-01: answer a checkout request while the order's checkout is still open. Returns the
    /// same payment when its prepay_id is known, finishes an attempt whose earlier request
    /// died before Telebirr was called, and otherwise refuses until the window ends.
    /// </summary>
    private async Task<EscrowResult<OrderCheckoutResponse>> ResumeOpenCheckoutAsync(
        OrderCheckoutSlot slot, PaymentCycle cycle)
    {
        if (slot.Outcome == OrderCheckoutOutcome.UrlIssued && !string.IsNullOrEmpty(slot.PrepayId))
        {
            var url = _telebirrGateway.BuildCheckoutUrl(slot.PrepayId);
            if (!string.IsNullOrEmpty(url))
            {
                return EscrowResult<OrderCheckoutResponse>.Success(BuildCheckoutResponse(slot, url, isExisting: true));
            }
        }

        // No mapping means the earlier request stopped before calling Telebirr, so no payment
        // exists for this attempt yet: it is safe to run it now.
        if (slot.Outcome == OrderCheckoutOutcome.Opening
            && await _orderCheckoutRepo!.GetMappingAsync(slot.MerchOrderId) == null)
        {
            return await StartCheckoutAttemptAsync(slot, cycle);
        }

        return EscrowResult<OrderCheckoutResponse>.Failure(
            409, "ESCROW_032",
            $"A Telebirr payment for this order is already open until {slot.OpenUntilUtc:o}. "
            + "Finish it in Telebirr, or try again after that time.",
            "orderId");
    }

    /// <summary>
    /// ESC-01: run one checkout attempt: write the merch_order_id mapping, create the Telebirr
    /// payment, then record the outcome on the slot. The mapping carries the order's payment
    /// attempt (<paramref name="cycle"/>), which the webhook checks before funding.
    /// </summary>
    private async Task<EscrowResult<OrderCheckoutResponse>> StartCheckoutAttemptAsync(
        OrderCheckoutSlot slot, PaymentCycle cycle)
    {
        var escrowId = $"escrow-order-{slot.OrderId}";

        // Checked before every Telebirr call, including an attempt resumed late, so no payment
        // can still be open when the order cron fails the order.
        if (cycle.HasRunOut(DateTime.UtcNow))
        {
            return PaymentTimeRunOut();
        }

        // Everything that can fail without Telebirr runs before the mapping is written, so a
        // failure here leaves the slot "opening" with no mapping, which a retry finishes.
        var buyerPhone = await ResolveRefundPhoneAsync(slot.BuyerId);

        if (!await _orderCheckoutRepo!.StoreMappingAsync(
                slot.MerchOrderId, slot.OrderId, slot.BuyerId, slot.Amount, cycle.SubmittedAt))
        {
            // A concurrent request is running this same attempt.
            return CheckoutBusy();
        }

        HoldFundsResponse hold;
        try
        {
            hold = await _telebirrGateway.HoldFundsAsync(new HoldFundsRequest
            {
                TransactionRef = slot.MerchOrderId,
                BuyerPhone = buyerPhone,
                Amount = slot.Amount,
                Description = $"WeredaConnect order {ShortSafeId(slot.OrderId)}"
            });
        }
        catch (Exception ex)
        {
            hold = new HoldFundsResponse
            {
                Success = false,
                ErrorCode = "TELEBIRR_NETWORK",
                ErrorMessage = $"[{ex.GetType().Name}]"
            };
        }

        // The payment's life starts when Telebirr answers, so the window (plus margin) is
        // measured from now, not from when the slot was written.
        var openUntil = DateTime.UtcNow.Add(OrderCheckoutWindow).Add(OrderCheckoutSafetyMargin);

        if (hold.Success && !string.IsNullOrEmpty(hold.ToPayUrl) && !string.IsNullOrEmpty(hold.PrepayId))
        {
            await _orderCheckoutRepo.UpdateSlotOutcomeAsync(
                slot.OrderId, slot.Attempt, OrderCheckoutOutcome.UrlIssued, hold.PrepayId, openUntil);
            await AppendAuditAsync(escrowId, "order_checkout_opened", "", "", slot.BuyerId,
                metadata: $"orderId={slot.OrderId},merchOrderId={slot.MerchOrderId},attempt={slot.Attempt}");
            return EscrowResult<OrderCheckoutResponse>.Success(
                BuildCheckoutResponse(slot with { OpenUntilUtc = openUntil }, hold.ToPayUrl, isExisting: false));
        }

        // Without a URL the buyer cannot pay; whether Telebirr kept a payment is unknown unless
        // it explicitly refused.
        var definite = !hold.Success && !string.IsNullOrEmpty(hold.ErrorCode)
            && !UncertainGatewayErrors.Contains(hold.ErrorCode);
        var outcome = definite ? OrderCheckoutOutcome.Failed : OrderCheckoutOutcome.Unknown;
        await _orderCheckoutRepo.UpdateSlotOutcomeAsync(
            slot.OrderId, slot.Attempt, outcome, openUntilUtc: definite ? null : openUntil);
        if (definite)
        {
            await _idempotencyRepo.UpdateAsync(slot.MerchOrderId, "failed");
        }

        await AppendAuditAsync(escrowId, definite ? "order_checkout_failed" : "order_checkout_unknown", "", "", SystemActor,
            metadata: $"orderId={slot.OrderId},merchOrderId={slot.MerchOrderId},error={hold.ErrorCode}");

        return EscrowResult<OrderCheckoutResponse>.Failure(
            502, "ESCROW_013",
            definite
                ? "Telebirr could not start the payment. Please try again."
                : $"Telebirr did not answer. If you already paid, the payment will be applied; otherwise try again after {openUntil:o}.");
    }

    private static EscrowResult<OrderCheckoutResponse> CheckoutBusy()
        => EscrowResult<OrderCheckoutResponse>.Failure(
            409, "ESCROW_032", "A Telebirr payment for this order is being opened. Try again in a moment.", "orderId");

    private static OrderCheckoutResponse BuildCheckoutResponse(OrderCheckoutSlot slot, string toPayUrl, bool isExisting)
        => new()
        {
            OrderId = slot.OrderId,
            Amount = slot.Amount,
            Currency = "ETB",
            ToPayUrl = toPayUrl,
            OpenUntil = slot.OpenUntilUtc.ToString("o", CultureInfo.InvariantCulture),
            IsExisting = isExisting
        };

    /// <summary>
    /// ESC-01: a compact, server-generated Telebirr merch_order_id for an order checkout
    /// attempt: "WCO", 24 upper-case hex characters of SHA-256 over the exact order id (so
    /// distinct ids never collapse onto one, whatever characters they contain), then the attempt
    /// number (3 digits). ASCII letters and digits only, 30 characters.
    /// </summary>
    public static string BuildOrderMerchOrderId(string orderId, int attempt)
    {
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(orderId)))[..24];
        return $"WCO{hash}{attempt.ToString("D3", CultureInfo.InvariantCulture)}";
    }

    /// <summary>An order id reduced to ASCII letters, digits and dashes (at most 36) for text sent to Telebirr.</summary>
    private static string ShortSafeId(string id)
    {
        var safe = new string(id.Where(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-')
            .ToArray());
        return safe.Length > 36 ? safe[..36] : safe;
    }

    /// <summary>
    /// Attach a newly created delivery to its order's escrow so every by-delivery
    /// mechanism (delivered-confirmation stamping, auto-release cron, self-pickup
    /// fail-closed QR gate, GET /escrow/by-delivery) applies to order-funded money.
    ///
    /// Self-heals a missing record (funding-time create failed, or the order predates
    /// the order-funded leg) while the order still holds money; re-links when the
    /// previously linked delivery was cancelled; refuses terminal orders.
    /// Returns true when the order's escrow is linked to deliveryId on return.
    /// </summary>
    public async Task<bool> LinkDeliveryToOrderEscrowAsync(
        string orderId, string deliveryId, Dictionary<string, object> order, string actor,
        AuditContext? auditContext = null)
    {
        _auditContext = auditContext;

        if (string.IsNullOrEmpty(orderId) || string.IsNullOrEmpty(deliveryId))
        {
            return false;
        }

        var orderStatus = GetString(order, "status");
        if (TerminalOrderStatuses.Contains(orderStatus))
        {
            return false;
        }

        var escrow = await GetActiveEscrowByOrderIdAsync(orderId);
        if (escrow == null)
        {
            // Self-heal: only while the order is still in a money-holding state.
            if (orderStatus is not ("funded" or "accepted"))
            {
                return false;
            }

            if (!await CreateFromFundedOrderAsync(order, actor, auditContext))
            {
                return false;
            }

            escrow = await GetActiveEscrowByOrderIdAsync(orderId);
            if (escrow == null)
            {
                return false;
            }
        }

        if (!string.Equals(GetString(escrow, "status"), "escrowed", StringComparison.Ordinal))
        {
            // released/disputed escrows must not be re-pointed at a new delivery.
            return false;
        }

        var escrowId = GetString(escrow, "escrowId");
        var createdAt = GetString(escrow, "createdAt");
        var linkedDeliveryId = GetStringOrNull(escrow, "deliveryId");
        if (!string.IsNullOrEmpty(linkedDeliveryId))
        {
            if (string.Equals(linkedDeliveryId, deliveryId, StringComparison.Ordinal))
            {
                return true;
            }

            // Re-link only when the currently linked delivery is dead; an active
            // linked delivery keeps the escrow (prevents hijacking a live handoff).
            // Consistent read (ESC-08): the replacement is usually created right after
            // the cancel, and a stale read would keep the escrow on the dead delivery.
            if (_deliveryRepo == null)
            {
                return false;
            }

            var linkedDelivery = await _deliveryRepo.GetByIdAsync(linkedDeliveryId, consistentRead: true);
            if (linkedDelivery != null
                && !string.Equals(GetString(linkedDelivery, "status"), "cancelled", StringComparison.Ordinal))
            {
                return false;
            }
        }

        var linkFields = new Dictionary<string, object> { { "deliveryId", deliveryId } };

        // SIM-15: an escrow minted before the order was bound to a truck carries no
        // truckerId; stamp it from the order now so the trucker's history
        // (TruckerStatusIndex) and by-delivery reads see it. The payout split is
        // never touched here (commission is the exact remainder fixed at creation).
        var orderTruckerId = GetStringOrNull(order, "truckerId");
        if (string.IsNullOrEmpty(GetStringOrNull(escrow, "truckerId"))
            && !string.IsNullOrEmpty(orderTruckerId))
        {
            linkFields["truckerId"] = orderTruckerId;
        }

        await _escrowRepo.UpdateStatusAsync(
            escrowId, createdAt, "escrowed", expectedCurrentStatus: "escrowed",
            additionalFields: linkFields);

        await AppendAuditAsync(
            escrowId, "delivery_linked", "escrowed", "escrowed", actor,
            metadata: $"deliveryId={deliveryId}");

        return true;
    }

    /// <summary>
    /// Schedule a buyer refund for an order's escrow by expiring it now: the hourly
    /// auto-refund cron (StatusExpiresIndex) picks it up and pays the buyer through
    /// the standard gateway path in mock and live alike. Only applies while the
    /// escrow is "escrowed" and its delivery is unconfirmed — confirmed money is
    /// governed by the dispute window / release flow instead.
    /// Returns true when a refund was scheduled.
    /// </summary>
    public async Task<bool> ScheduleRefundForOrderAsync(
        string orderId, string reason, string actor, AuditContext? auditContext = null)
    {
        _auditContext = auditContext;

        if (string.IsNullOrEmpty(orderId))
        {
            return false;
        }

        var escrow = await GetActiveEscrowByOrderIdAsync(orderId);
        if (escrow == null
            || !string.Equals(GetString(escrow, "status"), "escrowed", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(GetStringOrNull(escrow, "deliveryConfirmedAt")))
        {
            return false;
        }

        var escrowId = GetString(escrow, "escrowId");
        var createdAt = GetString(escrow, "createdAt");

        await _escrowRepo.UpdateStatusAsync(
            escrowId, createdAt, "escrowed", expectedCurrentStatus: "escrowed",
            additionalFields: new Dictionary<string, object>
            {
                { "expiresAt", DateTime.UtcNow.ToString("o") },
                { "refundReason", reason }
            });

        await AppendAuditAsync(
            escrowId, "refund_scheduled", "escrowed", "escrowed", actor, metadata: reason);

        return true;
    }

    /// <summary>
    /// ESC-06: before a buyer cancels an accepted order after a transport failure, check that its
    /// money can be refunded by <see cref="ScheduleRefundForOrderAsync"/>. Every escrow the order
    /// index lists as active, and the order's own <c>escrow-order-{orderId}</c>, is re-read
    /// strongly consistent: each one that still holds money must be plain <c>escrowed</c> (no
    /// settlement lock, no open manual payout, no confirmed delivery), and an order escrow that
    /// already settled refuses too. When the held money has no record at all (the funding-time
    /// create failed, <c>ORDER_ESCROW_CREATE_FAILED</c>), it is created first, as delivery linkage
    /// does, so the refund has a record to pay out of.
    /// </summary>
    public async Task<OrderEscrowCancelReadiness> PrepareOrderEscrowForCancelAsync(
        Dictionary<string, object> order, string actor, AuditContext? auditContext = null)
    {
        _auditContext = auditContext;

        var orderId = GetString(order, "orderId");
        if (orderId.Length == 0)
        {
            return OrderEscrowCancelReadiness.Unavailable;
        }

        var orderEscrowId = $"escrow-order-{orderId}";
        var candidates = new List<string>();
        foreach (var row in await _escrowRepo.QueryByOrderIdAsync(orderId) ?? new List<Dictionary<string, object>>())
        {
            var id = GetString(row, "escrowId");
            if (id.Length > 0 && ActiveEscrowStatuses.Contains(GetString(row, "status")) && !candidates.Contains(id))
            {
                candidates.Add(id);
            }
        }

        if (!candidates.Contains(orderEscrowId))
        {
            candidates.Add(orderEscrowId);
        }

        var plain = 0;
        foreach (var escrowId in candidates)
        {
            var current = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
            if (current == null)
            {
                continue;
            }

            if (IsPlainEscrowed(current))
            {
                plain++;
            }
            else if (ActiveEscrowStatuses.Contains(GetString(current, "status")) || escrowId == orderEscrowId)
            {
                return OrderEscrowCancelReadiness.Busy;
            }
        }

        if (plain > 0)
        {
            return OrderEscrowCancelReadiness.Ready;
        }

        if (!await CreateFromFundedOrderAsync(order, actor, auditContext))
        {
            return OrderEscrowCancelReadiness.Unavailable;
        }

        var created = await _escrowRepo.GetByIdAsync(orderEscrowId, consistentRead: true);
        return created != null && IsPlainEscrowed(created)
            ? OrderEscrowCancelReadiness.Ready
            : OrderEscrowCancelReadiness.Unavailable;
    }

    /// <summary>ESC-06: held money nothing else owns: escrowed, unlocked, not queued, delivery not confirmed.</summary>
    private static bool IsPlainEscrowed(Dictionary<string, object> escrow)
        => string.Equals(GetString(escrow, "status"), "escrowed", StringComparison.Ordinal)
           && !IsManualPayoutOpen(escrow)
           && GetStringOrNull(escrow, SettlementLockFields.Lock) == null
           && string.IsNullOrEmpty(GetStringOrNull(escrow, "deliveryConfirmedAt"));

    /// <summary>
    /// Newest active escrow for an order via the sparse OrderIndex GSI. An order may
    /// legitimately have inert rows (refunded/cancelled) next to one active one.
    ///
    /// The GSI is eventually consistent and can miss a just-written record — which
    /// would leave a delivery permanently unlinked (money never released). The
    /// order-funded escrow has a deterministic id, so a base-table read closes
    /// that window.
    /// </summary>
    private async Task<Dictionary<string, object>?> GetActiveEscrowByOrderIdAsync(string orderId)
    {
        var escrows = await _escrowRepo.QueryByOrderIdAsync(orderId)
            ?? new List<Dictionary<string, object>>();
        foreach (var escrow in escrows)
        {
            if (ActiveEscrowStatuses.Contains(GetString(escrow, "status")))
            {
                return escrow;
            }
        }

        var deterministic = await _escrowRepo.GetByIdAsync($"escrow-order-{orderId}");
        if (deterministic != null
            && ActiveEscrowStatuses.Contains(GetString(deterministic, "status")))
        {
            return deterministic;
        }

        return null;
    }

    /// <summary>
    /// Release escrowed funds to the farmer. Transitions: escrowed/disputed → released → completed.
    /// Only callable by system (cron) or admin. Regular users cannot release.
    /// </summary>
    /// <param name="role">The caller's JWT role claim (e.g. "admin"). Pass null for system/cron calls.</param>
    public async Task<EscrowResult<ReleaseEscrowResponse>> ReleaseAsync(
        ReleaseEscrowRequest request, string authenticatedUserId, string? role = null,
        AuditContext? auditContext = null, bool adminGroupVerified = false)
    {
        var progress = new SettlementProgress();
        try
        {
            return await ReleaseCoreAsync(request, authenticatedUserId, role, auditContext, adminGroupVerified, progress);
        }
        catch (Exception ex) when (progress.Locked && !progress.Recorded)
        {
            MoneyAlarmLog.MarkSettlementInFlight(ex);
            throw;
        }
    }

    /// <summary>
    /// ESC-05: whether a release or refund took the settlement lock, so an exception that escapes
    /// afterwards is logged as an unknown outcome and one before it as a plain failure. Once the
    /// settled status is written (<see cref="Recorded"/>) the money is accounted for, so a later
    /// bookkeeping failure (an audit row, the completed write) is a plain failure again.
    /// </summary>
    private sealed class SettlementProgress
    {
        public bool Locked { get; set; }

        public bool Recorded { get; set; }
    }

    private async Task<EscrowResult<ReleaseEscrowResponse>> ReleaseCoreAsync(
        ReleaseEscrowRequest request, string authenticatedUserId, string? role,
        AuditContext? auditContext, bool adminGroupVerified, SettlementProgress progress)
    {
        _auditContext = auditContext;

        // 1. Authorization: only system or admin can release funds → ESCROW_015
        if (!await IsSystemOrAdminAsync(authenticatedUserId, role, adminGroupVerified))
        {
            return EscrowResult<ReleaseEscrowResponse>.Failure(
                403, "ESCROW_015", "Only system or admin can release escrow funds.");
        }

        // 1b. Validate idempotency key format → ESCROW_003
        if (!IsValidIdempotencyKey(request.IdempotencyKey))
        {
            return EscrowResult<ReleaseEscrowResponse>.Failure(
                400, "ESCROW_003", "Invalid idempotency key format.", "idempotencyKey");
        }

        // 1c. Validate trigger enum
        if (!ValidReleaseTriggers.Contains(request.Trigger))
        {
            return EscrowResult<ReleaseEscrowResponse>.Failure(
                400, "ESCROW_VALIDATION",
                $"Invalid release trigger. Must be one of: {string.Join(", ", ValidReleaseTriggers)}",
                "trigger");
        }

        // 2. Check idempotency
        var existing = await _idempotencyRepo.GetAsync(request.IdempotencyKey);
        if (existing != null)
        {
            var existingEscrowId = GetString(existing, "escrowId");
            if (!string.IsNullOrEmpty(existingEscrowId)
                && GetString(existing, "status") == "completed")
            {
                var cached = await _escrowRepo.GetByIdAsync(existingEscrowId);
                if (cached != null)
                {
                    return EscrowResult<ReleaseEscrowResponse>.Duplicate(
                        BuildReleaseResponse(cached));
                }
            }
        }

        // 3. Get escrow by ID → ESCROW_016 if not found
        var escrowItem = await _escrowRepo.GetByIdAsync(request.EscrowId);
        if (escrowItem == null)
        {
            return EscrowResult<ReleaseEscrowResponse>.Failure(
                404, "ESCROW_016", "Escrow not found.");
        }

        // 4. Validate state transition: "escrowed", or "dispute_resolved" with a verdict that
        //    does not contradict a release (ESC-03).
        var currentStatus = GetString(escrowItem, "status");
        var refusal = SettlementRefusal(escrowItem, "release");
        if (refusal != null)
        {
            return EscrowResult<ReleaseEscrowResponse>.Failure(422, "ESCROW_INVALID_TRANSITION", refusal);
        }

        // 4a. ESC-02: a settlement already waiting for an admin in the manual payout queue is
        //     neither retried nor audited again, so the hourly sweeps stay quiet.
        if (IsManualPayoutOpen(escrowItem))
        {
            return ManualPayoutWaiting<ReleaseEscrowResponse>();
        }

        // 4a2. ESC-03: the order and delivery follow the verdict before any money moves; a
        //      verdict they cannot follow is not settled.
        if (currentStatus == "dispute_resolved"
            && await MirrorVerdictAsync(escrowItem, authenticatedUserId, "release") is { } block)
        {
            return EscrowResult<ReleaseEscrowResponse>.Failure(block.HttpStatus, block.Code, block.Message);
        }

        // 4a3. ESC-09 Part B: an admin release of a plain escrowed escrow pays only an order that is
        //      delivered (a lagging one is first advanced), so the order can follow the money.
        if (currentStatus == "escrowed" && authenticatedUserId != SystemActor
            && await CheckAdminReleaseOrderAsync(escrowItem, authenticatedUserId) is { } releaseBlock)
        {
            return EscrowResult<ReleaseEscrowResponse>.Failure(releaseBlock.HttpStatus, releaseBlock.Code, releaseBlock.Message);
        }

        var farmerPayout = GetDecimal(escrowItem, "farmerPayout");
        var commission = GetDecimal(escrowItem, "commission");
        var holdRef = GetStringOrNull(escrowItem, "telebirrHoldRef") ?? "";
        var createdAt = GetString(escrowItem, "createdAt");
        var currentVersion = GetInt(escrowItem, "version");

        // 4b. Payout destination and identity. Money goes to the farmer's registered
        //     phone (immutable since registration), and only to a farmer whose phone
        //     is proven and, above the NBE threshold, whose Fayda identity is verified.
        //     Checked before the lock so a blocked release leaves the escrow untouched
        //     for an admin to settle.
        //     ESC-10: the hourly sweeps audit a block once per reason (stored on the escrow), an
        //     admin's own attempt each time.
        var farmerId = GetString(escrowItem, "farmerId");
        var (payout, payoutBlock) = await ResolvePayoutAsync(farmerId, GetDecimal(escrowItem, "amount"), "farmer");
        if (!payout.IsSuccess)
        {
            var blockReason = payoutBlock ?? "unknown";
            if (authenticatedUserId == SystemActor)
            {
                await NoteSettlementBlockedAsync(escrowItem, currentStatus,
                    SettlementBlockFields.Value("release", SettlementBlockFields.GatePayout, blockReason),
                    "release_blocked_kyc", PayoutBlockedMetadata(blockReason));
            }
            else
            {
                await AppendAuditAsync(request.EscrowId, "release_blocked_kyc", currentStatus, currentStatus,
                    authenticatedUserId, metadata: PayoutBlockedMetadata(blockReason));
            }

            return EscrowResult<ReleaseEscrowResponse>.Failure(
                payout.Error!.HttpStatus, payout.Error.Code, payout.Error.Message);
        }

        if (authenticatedUserId == SystemActor)
        {
            await ClearSettlementBlockAsync(escrowItem, "release", SettlementBlockFields.GatePayout);
        }

        var farmerPhone = payout.Value!;

        // 4c. ESC-05: the trucker's destination is read before the lock too, so no read can fail
        //     between the two gateway calls. An unverified trucker is not called: the leg stays
        //     pending with the reason, and the hourly trucker retry pays it once the gate passes (ESC-04).
        var truckerPayout = GetDecimalOrNull(escrowItem, "truckerPayout");
        var truckerPayoutAmount = truckerPayout ?? 0m;
        var hasTruckerLeg = truckerPayoutAmount > 0;
        EscrowResult<string>? truckerPayoutDest = null;
        string? truckerBlockReason = null;
        if (hasTruckerLeg)
        {
            (truckerPayoutDest, truckerBlockReason) = await ResolveTruckerPayeeAsync(
                GetStringOrNull(escrowItem, "truckerId") ?? "", truckerPayoutAmount);
        }

        // 5. Acquire the settlement lock BEFORE the irreversible Telebirr call. This is the
        //    atomic mutual-exclusion guard: at most ONE settlement (release OR refund, across
        //    any idempotency keys) may move funds for this escrow. A caller that loses the race
        //    NEVER calls Telebirr, so a release-vs-refund or release-vs-release race cannot
        //    double-settle. (Fixes the earlier design where funds moved before the state CAS.)
        var lockAcquired = await _escrowRepo.TryAcquireSettlementLockAsync(
            request.EscrowId, createdAt, "release", currentStatus);
        if (!lockAcquired)
        {
            var latest = await _escrowRepo.GetByIdAsync(request.EscrowId);
            var latestStatus = latest != null ? GetString(latest, "status") : "";
            if (latest != null && latestStatus is "released" or "completed")
            {
                // A prior release already settled this escrow — safe to return the result.
                return EscrowResult<ReleaseEscrowResponse>.Duplicate(BuildReleaseResponse(latest));
            }
            if (latest != null && IsManualPayoutOpen(latest))
            {
                return ManualPayoutWaiting<ReleaseEscrowResponse>();
            }
            // A settlement (possibly a refund) is in progress or the escrow moved on.
            // Do NOT call Telebirr.
            return EscrowResult<ReleaseEscrowResponse>.Failure(
                409, "ESCROW_INVALID_TRANSITION",
                "A settlement is already in progress or completed for this escrow.");
        }

        // ESC-05: from here on the gateway may have been called. The lock is released only after a
        // definite refusal, and an exception that escapes is an unknown outcome.
        progress.Locked = true;

        // 6. Call Telebirr ReleaseFunds with the farmer payout amount (only the lock winner reaches
        //    here). Nothing that can fail runs between the lock and this call (ESC-05).
        var releaseResponse = await CallReleaseAsync(new ReleaseFundsRequest
        {
            TransactionRef = ReleaseTransactionRef(request.EscrowId),
            HoldRef = holdRef,
            FarmerPhone = farmerPhone,
            Amount = farmerPayout,
            PlatformCommission = commission
        });

        // Record idempotency for the cached-duplicate return on a same-key replay (best-effort;
        // the settlement lock, not this write, is the concurrency guard). After the call (ESC-05).
        await StoreIdempotencyBestEffortAsync(request.IdempotencyKey, request.EscrowId);

        // 6a. ESC-02: a gateway that cannot pay out, or reports a payout without a reference,
        //     sends the whole settlement to the manual payout queue, and so (ESC-05) does an
        //     answer that does not say whether the money moved. The escrow keeps its status, and
        //     the settlement lock stays held on purpose so no other release or refund can run
        //     until an admin has paid every leg.
        var farmerManualReason = ManualPayoutReason(
            releaseResponse.Success, releaseResponse.ErrorCode, releaseResponse.TelebirrRef);
        var farmerOutcomeUnknown = farmerManualReason == null
            && IsOutcomeUnknownAnswer(releaseResponse.Success, releaseResponse.ErrorCode);
        if (farmerOutcomeUnknown)
        {
            await AppendFailureAuditBestEffortAsync(request.EscrowId, "release_failed", currentStatus,
                authenticatedUserId, releaseResponse.ErrorCode);
            farmerManualReason = ManualPayoutFields.ReasonGatewayOutcomeUnknown;
        }

        if (farmerManualReason != null)
        {
            var legs = new List<ManualPayoutLegSeed>
            {
                new(ManualPayoutFields.LegFarmer, ManualPayoutFields.LegPending, null, null, null)
            };
            if (hasTruckerLeg)
            {
                legs.Add(new(ManualPayoutFields.LegTrucker, ManualPayoutFields.LegPending, null, null, null));
            }

            // Only PAYOUT_NOT_SUPPORTED proves nothing moved. Any other reason may have paid the
            // farmer, so its queue write is retried and the lock is never released (ESC-05).
            var nothingMoved = farmerManualReason == ManualPayoutFields.ReasonPayoutNotSupported;
            if (await QueueManualPayoutAsync(request.EscrowId, createdAt, ManualPayoutFields.OpRelease,
                    farmerManualReason, currentStatus, legs, authenticatedUserId,
                    nothingMoved ? 1 : MaxSettlementWriteAttempts))
            {
                await UpdateIdempotencyBestEffortAsync(request.IdempotencyKey, request.EscrowId, "manual_payout");
                // ESC-09: the release is committed (the queue owns it), so the order is disbursing.
                await FollowMoneyAsync(request.EscrowId, "release");
                return farmerOutcomeUnknown
                    ? OutcomeUnknownWaiting<ReleaseEscrowResponse>()
                    : ManualPayoutWaiting<ReleaseEscrowResponse>();
            }

            if (!nothingMoved)
            {
                return await OutcomeUnknownNotQueuedAsync<ReleaseEscrowResponse>(
                    request.EscrowId, "release_failed", currentStatus, authenticatedUserId, farmerManualReason, null);
            }

            // The queue write failed and nothing moved: today's retryable failure path. The lock
            // goes first, so a failed audit write cannot strand it (ESC-05).
            await _escrowRepo.ReleaseSettlementLockAsync(request.EscrowId, createdAt);
            progress.Locked = false;
            await _idempotencyRepo.DeleteAsync(request.IdempotencyKey);
            await AppendAuditAsync(request.EscrowId, "release_failed", currentStatus, currentStatus,
                authenticatedUserId, metadata: QueueFailedNotSupportedCode);
            return EscrowResult<ReleaseEscrowResponse>.Failure(
                502, "ESCROW_013",
                "The payout could not be queued for manual settlement. It is retried by the next sweep.");
        }

        if (!releaseResponse.Success)
        {
            // A definite refusal: nothing moved. Release the lock + idempotency record so the next
            // sweep retries, then audit (the lock first, so a failed audit write cannot strand it).
            await _escrowRepo.ReleaseSettlementLockAsync(request.EscrowId, createdAt);
            progress.Locked = false;
            await _idempotencyRepo.DeleteAsync(request.IdempotencyKey);
            await AppendAuditAsync(request.EscrowId, "release_failed", currentStatus, currentStatus,
                authenticatedUserId, metadata: releaseResponse.ErrorCode);
            return EscrowResult<ReleaseEscrowResponse>.Failure(
                502, "ESCROW_013",
                releaseResponse.ErrorMessage ?? "Telebirr gateway error during fund release.");
        }

        // 5b. 3-way split: If trucker payout exists, release transport cost to trucker.
        string? truckerReleaseRef = null;
        var truckerReleasePending = false;
        string? truckerFailureCode = null;
        if (hasTruckerLeg)
        {
            var truckerRelease = truckerPayoutDest!.IsSuccess
                ? await CallReleaseAsync(new ReleaseFundsRequest
                {
                    TransactionRef = TruckerReleaseTransactionRef(request.EscrowId),
                    HoldRef = holdRef,
                    FarmerPhone = truckerPayoutDest.Value!,
                    Amount = truckerPayoutAmount,
                    PlatformCommission = 0m
                })
                // No verified destination yet: the leg stays pending (truckerPayoutPending)
                // instead of paying an unknown wallet.
                : new ReleaseFundsResponse { Success = false, ErrorMessage = truckerPayoutDest.Error!.Message };

            // ESC-02: decided before the success branch, so a trucker "success" without a
            // reference is queued too instead of completing with no truckerTelebirrRef.
            var truckerManualReason = truckerPayoutDest.IsSuccess
                ? ManualPayoutReason(truckerRelease.Success, truckerRelease.ErrorCode, truckerRelease.TelebirrRef)
                : null;
            var truckerOutcomeUnknown = truckerPayoutDest.IsSuccess && truckerManualReason == null
                && IsOutcomeUnknownAnswer(truckerRelease.Success, truckerRelease.ErrorCode);
            if (truckerOutcomeUnknown)
            {
                await AppendFailureAuditBestEffortAsync(request.EscrowId, "trucker_release_failed", currentStatus,
                    SystemActor, truckerRelease.ErrorCode);
                truckerManualReason = ManualPayoutFields.ReasonGatewayOutcomeUnknown;
            }

            if (truckerRelease.Success && truckerManualReason == null)
            {
                truckerReleaseRef = truckerRelease.TelebirrRef;
            }
            else
            {
                // ESC-02: the farmer is paid but the gateway could not pay the trucker (or gave
                // no reference, or, ESC-05, did not say). Queue the trucker leg with the farmer leg
                // already closed by the gateway, retrying the write like the released write below.
                // If it still fails: a PAYOUT_NOT_SUPPORTED trucker leg falls back to a pending
                // trucker leg (nothing moved for the trucker); any other reason keeps the lock and
                // never settles, so an unknown leg is never retried blind. A pending leg (also for
                // an unverified trucker, never called) is retried hourly by the trucker retry (ESC-04).
                if (truckerManualReason != null)
                {
                    var legs = new List<ManualPayoutLegSeed>
                    {
                        new(ManualPayoutFields.LegFarmer, ManualPayoutFields.LegPaid, releaseResponse.TelebirrRef,
                            ManualPayoutFields.PaidByGateway, DateTime.UtcNow.ToString("o")),
                        new(ManualPayoutFields.LegTrucker, ManualPayoutFields.LegPending, null, null, null)
                    };
                    if (await QueueManualPayoutAsync(request.EscrowId, createdAt, ManualPayoutFields.OpRelease,
                            truckerManualReason, currentStatus, legs, authenticatedUserId, MaxSettlementWriteAttempts))
                    {
                        await UpdateIdempotencyBestEffortAsync(request.IdempotencyKey, request.EscrowId, "manual_payout");
                        await FollowMoneyAsync(request.EscrowId, "release");
                        return truckerOutcomeUnknown
                            ? OutcomeUnknownWaiting<ReleaseEscrowResponse>()
                            : ManualPayoutWaiting<ReleaseEscrowResponse>();
                    }

                    if (truckerManualReason != ManualPayoutFields.ReasonPayoutNotSupported)
                    {
                        return await OutcomeUnknownNotQueuedAsync<ReleaseEscrowResponse>(
                            request.EscrowId, "trucker_release_failed", currentStatus, SystemActor, truckerManualReason,
                            $"op=release farmerRef={MoneyAlarmLog.LogId(releaseResponse.TelebirrRef)}");
                    }

                    truckerRelease = new ReleaseFundsResponse { Success = false, ErrorCode = QueueFailedNotSupportedCode };
                }

                // Do NOT silently swallow: audit the failure (after the status write) and mark the
                // payout as owed; the escrow stays released until the trucker retry pays it (ESC-04).
                truckerReleasePending = true;
                truckerFailureCode = truckerRelease.ErrorCode;
            }
        }

        var now = DateTime.UtcNow.ToString("o");

        // 6. Transition to "released" (optimistic concurrency on current status)
        var releaseFields = new Dictionary<string, object>
        {
            { "telebirrReleaseRef", releaseResponse.TelebirrRef ?? "" },
            { "releasedAt", now },
            { "version", currentVersion + 1 }
        };
        if (!string.IsNullOrEmpty(truckerReleaseRef))
        {
            releaseFields["truckerTelebirrRef"] = truckerReleaseRef;
        }

        if (truckerReleasePending)
        {
            releaseFields[TruckerPayoutFields.Pending] = true;
            releaseFields[TruckerPayoutFields.PendingSince] = now;
            if (truckerBlockReason != null)
            {
                releaseFields[TruckerPayoutFields.BlockReason] = truckerBlockReason;
                releaseFields[TruckerPayoutFields.BlockReasonAt] = now;
            }
        }

        // We hold the settlement lock, so the status is still `currentStatus`. Funds have ALREADY
        // moved, so the escrow MUST advance to 'released': transient failures are retried, and if
        // the write never lands the lock stays held and the recovery step parks the escrow for an
        // admin (ESC-05). This write never calls Telebirr.
        await WriteSettledStatusAsync(request.EscrowId, createdAt, "released", currentStatus, releaseFields,
            $"op=release farmerRef={MoneyAlarmLog.LogId(releaseResponse.TelebirrRef)} truckerRef={MoneyAlarmLog.LogId(truckerReleaseRef)}");
        // The payout is on record: what follows is bookkeeping, and the crash-recovery sweep
        // completes a released escrow whose later writes fail.
        progress.Recorded = true;

        // ESC-05: the trucker leg's audit row after the status write, so a failed audit write
        // cannot leave a paid escrow unrecorded.
        if (truckerReleaseRef != null)
        {
            await AppendAuditAsync(request.EscrowId, "trucker_release", currentStatus, currentStatus,
                SystemActor, truckerReleaseRef);
        }

        if (truckerReleasePending)
        {
            // ESC-04: a gate block is a known wait for the trucker to verify, like
            // release_blocked_kyc: audited with its reason, no money alarm (the overdue alarm
            // fires after 24 hours), at the time the row stores with the reason, so the hourly
            // retry re-ensures a row this write lost. A gateway refusal is a settlement failure.
            if (truckerBlockReason != null)
            {
                await EnsureAuditAsync(request.EscrowId, now, TruckerBlockedAuditAction, currentStatus, currentStatus,
                    SystemActor, metadata: PayoutBlockedMetadata(truckerBlockReason));
            }
            else
            {
                await AppendAuditAsync(request.EscrowId, "trucker_release_failed", currentStatus, currentStatus,
                    SystemActor, metadata: truckerFailureCode);
            }
        }

        // 7. Audit log
        await AppendAuditAsync(request.EscrowId, "release", currentStatus, "released",
            authenticatedUserId, releaseResponse.TelebirrRef);

        // ESC-04: the farmer is paid and the trucker leg is owed, so the escrow stays released
        // (never completed with a leg unpaid). The hourly trucker retry completes it.
        if (truckerReleasePending)
        {
            await UpdateIdempotencyBestEffortAsync(request.IdempotencyKey, request.EscrowId, "completed");
            Console.WriteLine(
                $"ESCROW_TRUCKER_PAYOUT_PENDING escrowId={MoneyAlarmLog.LogId(request.EscrowId)} reason={truckerBlockReason ?? "gateway_refused"}");
            // ESC-09: the escrow stays released while a leg is owed, so the order is disbursing.
            await FollowMoneyAsync(request.EscrowId, "release");
            return EscrowResult<ReleaseEscrowResponse>.Success(new ReleaseEscrowResponse
            {
                EscrowId = request.EscrowId,
                Status = "released",
                FarmerPayout = farmerPayout,
                TruckerPayout = truckerPayout,
                Commission = commission,
                TelebirrRef = releaseResponse.TelebirrRef,
                TruckerTelebirrRef = null,
                ReleasedAt = now,
                Message = "Farmer paid. The transport payout is pending and is retried every hour."
            });
        }

        // 8. Transition to "completed" (optimistic concurrency on "released")
        await _escrowRepo.UpdateStatusAsync(request.EscrowId, createdAt, "completed", "released",
            new Dictionary<string, object>
            {
                { "completedAt", now },
                { "version", currentVersion + 2 }
            });

        // 9. Audit log
        await AppendAuditAsync(request.EscrowId, "complete", "released", "completed", "system");

        await UpdateIdempotencyBestEffortAsync(request.IdempotencyKey, request.EscrowId, "completed");

        // ESC-09: every leg is paid, so the order is settled (through disbursing).
        await FollowMoneyAsync(request.EscrowId, "release");

        // 10. Return ReleaseEscrowResponse
        return EscrowResult<ReleaseEscrowResponse>.Success(new ReleaseEscrowResponse
        {
            EscrowId = request.EscrowId,
            Status = "completed",
            FarmerPayout = farmerPayout,
            TruckerPayout = truckerPayout,
            Commission = commission,
            TelebirrRef = releaseResponse.TelebirrRef,
            TruckerTelebirrRef = truckerReleaseRef,
            ReleasedAt = now,
            Message = truckerPayout.HasValue
                ? "Funds released: farmer, trucker, and platform commission split."
                : "Funds released to farmer's Telebirr account."
        });
    }

    /// <summary>
    /// Raise a dispute on an escrowed payment. Must be within 48 hours of delivery confirmation.
    /// </summary>
    public async Task<EscrowResult<DisputeEscrowResponse>> DisputeAsync(
        DisputeEscrowRequest request, string authenticatedUserId, AuditContext? auditContext = null)
    {
        _auditContext = auditContext;

        // 0. Validate idempotency key format → ESCROW_003
        if (!IsValidIdempotencyKey(request.IdempotencyKey))
        {
            return EscrowResult<DisputeEscrowResponse>.Failure(
                400, "ESCROW_003", "Invalid idempotency key format.", "idempotencyKey");
        }

        // 1. Check idempotency
        var existing = await _idempotencyRepo.GetAsync(request.IdempotencyKey);
        if (existing != null)
        {
            var existingEscrowId = GetString(existing, "escrowId");
            if (!string.IsNullOrEmpty(existingEscrowId)
                && GetString(existing, "status") == "completed")
            {
                var cached = await _escrowRepo.GetByIdAsync(existingEscrowId);
                if (cached != null)
                {
                    return EscrowResult<DisputeEscrowResponse>.Duplicate(
                        BuildDisputeResponse(cached));
                }
            }
        }

        // 2. Get escrow → ESCROW_016
        var escrowItem = await _escrowRepo.GetByIdAsync(request.EscrowId);
        if (escrowItem == null)
        {
            return EscrowResult<DisputeEscrowResponse>.Failure(
                404, "ESCROW_016", "Escrow not found.");
        }

        // 3. Validate disputedBy matches JWT sub
        if (!string.Equals(request.DisputedBy, authenticatedUserId, StringComparison.Ordinal))
        {
            return EscrowResult<DisputeEscrowResponse>.Failure(
                403, "ESCROW_019",
                "disputedBy does not match authenticated user.", "disputedBy");
        }

        // 4. Validate disputedBy is buyer or farmer on this escrow → ESCROW_019
        var buyerId = GetString(escrowItem, "buyerId");
        var farmerId = GetString(escrowItem, "farmerId");
        if (!string.Equals(request.DisputedBy, buyerId, StringComparison.Ordinal)
            && !string.Equals(request.DisputedBy, farmerId, StringComparison.Ordinal))
        {
            return EscrowResult<DisputeEscrowResponse>.Failure(
                403, "ESCROW_019",
                "User is not buyer or farmer on this escrow.", "disputedBy");
        }

        // 5. Validate escrow is "escrowed" → ESCROW_INVALID_TRANSITION
        var currentStatus = GetString(escrowItem, "status");
        if (currentStatus == "disputed")
        {
            // ESC-07: a retry after a dispute whose audit row or mirror did not land (the request
            // failed after the freeze) finishes them; both are idempotent. A second dispute is
            // 409 ESCROW_020 on every path, as when it loses the write race.
            try
            {
                await RepairFrozenDisputeAsync(escrowItem);
            }
            catch (Exception ex)
            {
                // The refusal stands; the next retry or a delivery dispute repairs it.
                Console.WriteLine(
                    $"DISPUTE_REPAIR_FAILED escrowId={MoneyAlarmLog.LogId(request.EscrowId)} reason=[{ex.GetType().Name}]");
            }

            return EscrowResult<DisputeEscrowResponse>.Failure(
                409, "ESCROW_020", "A dispute already exists for this escrow.");
        }

        if (currentStatus != "escrowed")
        {
            return EscrowResult<DisputeEscrowResponse>.Failure(
                422, "ESCROW_INVALID_TRANSITION",
                $"Cannot dispute escrow in '{currentStatus}' state. Must be 'escrowed'.");
        }

        // 5a. ESC-02: a settlement already waiting in the manual payout queue cannot be
        //     disputed; the queue would settle over the open dispute.
        if (IsManualPayoutOpen(escrowItem))
        {
            return DisputeRefusedManualPayout();
        }

        // 6. Validate dispute window (deliveryConfirmedAt + 48h) → ESCROW_018
        var deliveryConfirmedAt = GetStringOrNull(escrowItem, "deliveryConfirmedAt");
        if (deliveryConfirmedAt != null
            && DateTime.TryParse(deliveryConfirmedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var confirmedTime))
        {
            if (DateTime.UtcNow > confirmedTime.AddHours(DisputeWindowHours))
            {
                return EscrowResult<DisputeEscrowResponse>.Failure(
                    400, "ESCROW_018",
                    "Dispute window has expired. Must be within 48 hours of delivery confirmation.");
            }
        }

        // 7. Validate reason enum → ESCROW_017
        if (!ValidDisputeReasons.Contains(request.Reason))
        {
            return EscrowResult<DisputeEscrowResponse>.Failure(
                400, "ESCROW_017",
                $"Invalid dispute reason. Must be one of: {string.Join(", ", ValidDisputeReasons)}",
                "reason");
        }

        // 8. Check for existing dispute → ESCROW_020
        if (GetStringOrNull(escrowItem, "disputeId") != null)
        {
            return EscrowResult<DisputeEscrowResponse>.Failure(
                409, "ESCROW_020", "A dispute already exists for this escrow.");
        }

        // 9. Validate dispute description length → S2 security fix
        if (request.Description != null && request.Description.Length > MaxDisputeDescriptionLength)
        {
            return EscrowResult<DisputeEscrowResponse>.Failure(
                400, "ESCROW_017",
                $"Description must be {MaxDisputeDescriptionLength} characters or fewer.",
                "description");
        }

        // 10. Validate evidence refs count and length → S2 security fix
        if (request.EvidenceRefs != null)
        {
            if (request.EvidenceRefs.Count > MaxEvidenceRefs)
            {
                return EscrowResult<DisputeEscrowResponse>.Failure(
                    400, "ESCROW_017",
                    $"Maximum {MaxEvidenceRefs} evidence references allowed.",
                    "evidenceRefs");
            }

            if (request.EvidenceRefs.Any(r => string.IsNullOrWhiteSpace(r) || r.Length > MaxEvidenceRefLength))
            {
                return EscrowResult<DisputeEscrowResponse>.Failure(
                    400, "ESCROW_017",
                    $"Each evidence reference must be non-empty and at most {MaxEvidenceRefLength} characters.",
                    "evidenceRefs");
            }
        }

        var now = DateTime.UtcNow;
        var disputeId = $"dispute-{Guid.NewGuid():N}";
        var createdAt = GetString(escrowItem, "createdAt");
        var disputeCreatedAt = now.ToString("o");

        // Calculate dispute window end
        var disputeWindowEndsAt = (deliveryConfirmedAt != null
            && DateTime.TryParse(deliveryConfirmedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var ct))
            ? ct.AddHours(DisputeWindowHours).ToString("o")
            : now.AddHours(DisputeWindowHours).ToString("o");

        // Store idempotency (conditional write prevents races)
        var stored = await _idempotencyRepo.StoreAsync(request.IdempotencyKey, request.EscrowId, "processing");
        if (!stored)
        {
            return EscrowResult<DisputeEscrowResponse>.Failure(
                409, "ESCROW_003", "Duplicate request detected. Processing in progress.", "idempotencyKey");
        }

        // 13. Transition to "disputed"
        var additionalFields = new Dictionary<string, object>
        {
            { "disputeId", disputeId },
            { "disputedBy", request.DisputedBy },
            { "disputeReason", request.Reason },
            { "disputeCreatedAt", disputeCreatedAt },
            { DisputeAuditAtField, disputeCreatedAt },
            { "disputeWindowEndsAt", disputeWindowEndsAt }
        };

        if (!string.IsNullOrEmpty(request.Description))
        {
            additionalFields["disputeDescription"] = request.Description;
        }

        if (request.EvidenceRefs is { Count: > 0 })
        {
            additionalFields["disputeEvidenceRefs"] = string.Join(",", request.EvidenceRefs);
        }

        try
        {
            await _escrowRepo.UpdateStatusAsync(request.EscrowId, createdAt, "disputed", "escrowed", additionalFields);
        }
        catch (ConditionalCheckFailedException)
        {
            // Lost a race: the escrow moved, or it was queued for a manual payout meanwhile.
            // ESC-07: every outcome frees the idempotency key (a retry is answered from the
            // escrow as it is then) and none is a 500.
            await _idempotencyRepo.DeleteAsync(request.IdempotencyKey);
            var latest = await _escrowRepo.GetByIdAsync(request.EscrowId, consistentRead: true);
            return DisputeRaceRefusal(latest);
        }

        // 10. Audit log. ESC-07: at a deterministic key (disputeCreatedAt), so a retry that finds
        // the escrow already disputed can write a row this call lost, and never a second one.
        await EnsureAuditAsync(request.EscrowId, disputeCreatedAt, "dispute", "escrowed", "disputed", authenticatedUserId);

        // SIM-12: a dispute freezes the whole trade, not just the money. Mirror it onto
        // the order (delivered -> disputed, which blocks ratings) and the delivery.
        await MirrorDisputeAsync(
            request.EscrowId, GetStringOrNull(escrowItem, "orderId"), GetStringOrNull(escrowItem, "deliveryId"),
            request.DisputedBy, request.Reason, markDelivery: true);

        await _idempotencyRepo.UpdateAsync(request.IdempotencyKey, "completed");

        // 11. Return DisputeEscrowResponse
        return EscrowResult<DisputeEscrowResponse>.Success(new DisputeEscrowResponse
        {
            EscrowId = request.EscrowId,
            Status = "disputed",
            DisputeId = disputeId,
            DisputedBy = request.DisputedBy,
            Reason = request.Reason,
            DisputeWindowEndsAt = disputeWindowEndsAt,
            CreatedAt = now.ToString("o"),
            Message = "Dispute registered. Platform will review within 48 hours."
        });
    }

    /// <summary>
    /// 409 or 422 for a dispute whose escrow write failed its condition (ESC-07: never a 500).
    /// Checked in the order the write's conditions are: the manual payout queue (ESC-02), a
    /// settlement in flight (ESC-03), then the status.
    /// </summary>
    private static EscrowResult<DisputeEscrowResponse> DisputeRaceRefusal(Dictionary<string, object>? latest)
    {
        if (latest == null)
        {
            return EscrowResult<DisputeEscrowResponse>.Failure(404, "ESCROW_016", "Escrow not found.");
        }

        if (IsManualPayoutOpen(latest))
        {
            return DisputeRefusedManualPayout();
        }

        // ESC-03: a settlement holds the lock; it wins, and the dispute is refused.
        if (GetStringOrNull(latest, "settlementLock") != null)
        {
            return EscrowResult<DisputeEscrowResponse>.Failure(
                409, "ESCROW_INVALID_TRANSITION", "A settlement is in progress for this escrow; it can no longer be disputed.");
        }

        var status = GetString(latest, "status");
        return status switch
        {
            "disputed" => EscrowResult<DisputeEscrowResponse>.Failure(
                409, "ESCROW_020", "A dispute already exists for this escrow."),
            "escrowed" => EscrowResult<DisputeEscrowResponse>.Failure(
                409, "ESCROW_INVALID_TRANSITION", "The escrow changed while the dispute was recorded. Try again."),
            _ => EscrowResult<DisputeEscrowResponse>.Failure(
                422, "ESCROW_INVALID_TRANSITION", $"Cannot dispute escrow in '{status}' state. Must be 'escrowed'.")
        };
    }

    /// <summary>Order statuses a dispute mirror may first advance to <c>delivered</c> (ESC-07).</summary>
    private static readonly HashSet<string> LogisticsOrderStatuses = new(StringComparer.Ordinal)
    {
        "accepted", "pickup_confirmed", "in_transit"
    };

    /// <summary>
    /// SIM-12, ESC-07: move the delivery and order behind a frozen dispute to <c>disputed</c>.
    /// Called only once the escrow is <c>disputed</c> (frozen now, or already): by
    /// <see cref="DisputeAsync"/>, and by the sync delivery dispute after its own delivery write
    /// (<paramref name="markDelivery"/> false). Best-effort: the money is already frozen, so a
    /// failed leg never fails the dispute, and the verdict mirror repairs it before settlement.
    /// <list type="bullet">
    /// <item>Ids: the escrow's, else the delivery's <c>orderId</c>, else the order's
    /// <c>activeDeliveryId</c>.</item>
    /// <item>Delivery: <c>delivered</c> to <c>disputed</c> (status-conditioned, version added),
    /// one transaction-audit row <c>dispute</c>.</item>
    /// <item>Order: <c>delivered</c> to <c>disputed</c>; an order left at <c>accepted</c>,
    /// <c>pickup_confirmed</c> or <c>in_transit</c> behind a <c>delivered</c> or <c>disputed</c>
    /// delivery is first advanced to <c>delivered</c>. Version-checked system writes from a
    /// consistent read, the third conflict gives up, one transaction-audit row per hop
    /// (<c>order_updated</c>, <c>dispute</c>). Any other status is left alone.</item>
    /// </list>
    /// A failed leg is logged as <c>DISPUTE_PROPAGATION_FAILED</c> and audited on the escrow as
    /// <c>dispute_propagation_failed</c> with <c>metadata = legs=&lt;leg&gt;:&lt;reason&gt;</c>.
    /// Returns those legs, or null when every leg reached <c>disputed</c> or had nothing to do.
    /// </summary>
    public async Task<string?> MirrorDisputeAsync(
        string escrowId, string? orderId, string? deliveryId, string disputedBy, string? reason, bool markDelivery)
    {
        var failures = new List<string>();
        string? deliveryStatus = null;

        try
        {
            if (string.IsNullOrEmpty(orderId) && _deliveryRepo != null && !string.IsNullOrEmpty(deliveryId))
            {
                var delivery = await _deliveryRepo.GetByIdAsync(deliveryId);
                orderId = delivery != null ? GetStringOrNull(delivery, "orderId") : null;
            }

            if (string.IsNullOrEmpty(deliveryId) && _orderRepo != null && !string.IsNullOrEmpty(orderId))
            {
                var order = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
                deliveryId = order != null ? GetStringOrNull(order, "activeDeliveryId") : null;
            }
        }
        catch (Exception ex)
        {
            failures.Add($"lookup:[{ex.GetType().Name}]");
        }

        var transactionId = string.IsNullOrEmpty(orderId) ? escrowId : orderId;
        var metadata = $"escrowId={escrowId}";

        if (_deliveryRepo != null && !string.IsNullOrEmpty(deliveryId))
        {
            try
            {
                // False means the delivery is missing or no longer delivered: nothing to mirror.
                if (markDelivery && await _deliveryRepo.TryMarkDisputedAsync(deliveryId, disputedBy, reason))
                {
                    await TransactionAuditWriter.EmitAsync(_transactionAuditRepo, null, transactionId, "delivery", deliveryId,
                        "dispute", "delivered", "disputed", AuditContext.ForSystem(), metadata);
                }

                var latest = await _deliveryRepo.GetByIdAsync(deliveryId, consistentRead: true);
                deliveryStatus = latest != null ? GetStringOrNull(latest, "status") : null;
            }
            catch (Exception ex)
            {
                failures.Add($"delivery:[{ex.GetType().Name}]");
            }
        }

        if (_orderRepo != null && !string.IsNullOrEmpty(orderId))
        {
            try
            {
                var orderFailure = await MoveOrderToDisputedAsync(orderId, deliveryStatus, transactionId, metadata);
                if (orderFailure != null)
                {
                    failures.Add($"order:{orderFailure}");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"order:[{ex.GetType().Name}]");
            }
        }

        if (failures.Count == 0)
        {
            return null;
        }

        var legs = string.Join(",", failures);
        Console.WriteLine(
            $"DISPUTE_PROPAGATION_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} orderId={MoneyAlarmLog.LogId(orderId)} deliveryId={MoneyAlarmLog.LogId(deliveryId)} legs={legs}");
        try
        {
            await AppendAuditAsync(
                escrowId, "dispute_propagation_failed", "disputed", "disputed", disputedBy, metadata: $"legs={legs}");
        }
        catch (Exception ex)
        {
            // The ledger write itself failed; the dispute is still recorded on the escrow.
            Console.WriteLine(
                $"DISPUTE_PROPAGATION_AUDIT_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} reason=[{ex.GetType().Name}]");
        }

        return legs;
    }

    /// <summary>
    /// The order hops of <see cref="MirrorDisputeAsync"/>: at most two (advance to
    /// <c>delivered</c>, then <c>disputed</c>), each retried on a version conflict until the
    /// third. Returns the failure reason, or null when the order is <c>disputed</c> now or had
    /// nothing to do (missing, already <c>disputed</c> or later, terminal, before delivery).
    /// </summary>
    private async Task<string?> MoveOrderToDisputedAsync(
        string orderId, string? deliveryStatus, string transactionId, string metadata)
    {
        const int maxConflicts = 3;
        var conflicts = 0;
        var order = await _orderRepo!.GetByIdAsync(orderId, consistentRead: true);
        while (order != null)
        {
            var status = GetString(order, "status");
            string next;
            if (status == "delivered")
            {
                next = "disputed";
            }
            else if (LogisticsOrderStatuses.Contains(status) && deliveryStatus is "delivered" or "disputed")
            {
                next = "delivered";
            }
            else
            {
                return null;
            }

            var update = await _orderRepo.UpdateAsync(
                orderId, new Dictionary<string, object> { { "status", next } }, GetInt(order, "version"));
            if (update.IsSuccess)
            {
                await TransactionAuditWriter.EmitAsync(_transactionAuditRepo, null, transactionId, "order", orderId,
                    next == "disputed" ? "dispute" : "order_updated", status, next, AuditContext.ForSystem(), metadata);
                if (next == "disputed")
                {
                    return null;
                }

                order = update.Value ?? await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
                continue;
            }

            if (update.Error?.HttpStatus != 409)
            {
                return update.Error?.Code ?? "unknown";
            }

            if (++conflicts >= maxConflicts)
            {
                return "conflict";
            }

            order = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
        }

        return null;
    }

    /// <summary>
    /// ESC-03: why a release or refund may not settle this escrow, or null when it may.
    /// Settleable: <c>escrowed</c>, and <c>dispute_resolved</c> unless the verdict says the
    /// other way (<c>farmer_wins</c> only releases, <c>buyer_wins</c> only refunds; a legacy
    /// verdict without a direction, such as <c>partial_refund</c>, may go either way). A
    /// <c>disputed</c> escrow settles only through a recorded verdict. The verdict never
    /// changes once the escrow left <c>disputed</c>, so a non-consistent read is enough here;
    /// the lock then requires the exact status this check saw.
    /// </summary>
    private static string? SettlementRefusal(Dictionary<string, object> escrow, string op)
    {
        var status = GetString(escrow, "status");
        var verb = op == "release" ? "release" : "refund";
        if (status == "disputed")
        {
            return $"Cannot {verb} a disputed escrow. Record the verdict first (POST /admin/disputes/{{id}}/resolve).";
        }

        if (status is not ("escrowed" or "dispute_resolved"))
        {
            return $"Cannot {verb} escrow in '{status}' state. Must be 'escrowed' or 'dispute_resolved'.";
        }

        var resolution = GetStringOrNull(escrow, "disputeResolution");
        var contrary = op == "release" ? "buyer_wins" : "farmer_wins";
        if (status == "dispute_resolved" && resolution == contrary)
        {
            return $"The dispute verdict is '{resolution}': this escrow can only be "
                   + (op == "release" ? "refunded." : "released.");
        }

        return null;
    }

    /// <summary>Why a settlement of a dispute verdict does not go ahead on this run (ESC-03).</summary>
    private sealed record SettlementBlock(int HttpStatus, string Code, string Message);

    /// <summary>
    /// ESC-03: before a <c>dispute_resolved</c> escrow is settled, move its order and delivery
    /// to the verdict. The admin verdict already did most of it; here it repairs a leg that
    /// failed then and, for a <c>buyer_wins</c> refund before pickup, cancels the order (and its
    /// pending delivery) and restores its stock. Returns why the settlement must not go ahead:
    /// <list type="bullet">
    /// <item>a read failed before the legs were decided, or a <c>buyer_wins</c> refund could not cancel
    /// the order or give its stock back first (<c>503 ESCROW_012</c>, fail closed; the next run
    /// retries). Logged as <c>DISPUTE_VERDICT_SETTLEMENT_BLOCKED</c> for the money alarm (escrow-cron,
    /// three of four hourly periods) and audited once as <c>dispute_settlement_blocked</c>;</item>
    /// <item>a leg cannot follow the verdict (<c>422 ESCROW_INVALID_TRANSITION</c>, alarm line
    /// <c>DISPUTE_VERDICT_ORDER_MISMATCH</c>), unless an admin overrides a <c>buyer_wins</c> refund
    /// with a reason.</item>
    /// </list>
    /// A leg whose write failed does not block the settlement. Failed legs are audited once per
    /// escrow and action, at the verdict time plus one or two ticks.
    /// </summary>
    private async Task<SettlementBlock?> MirrorVerdictAsync(
        Dictionary<string, object> escrow, string actor, string op, string? overrideReason = null)
    {
        var resolution = GetStringOrNull(escrow, "disputeResolution");
        if (resolution is not ("farmer_wins" or "buyer_wins"))
        {
            return null;
        }

        var escrowId = GetString(escrow, "escrowId");
        var resolvedAt = GetStringOrNull(escrow, "disputeResolvedAt") ?? GetString(escrow, "createdAt");
        DisputeVerdictMirrorResult result;
        try
        {
            result = await _verdictMirror.ApplyAsync(escrow, resolution, resolvedAt, cancelBeforePickup: op == "refund");
        }
        catch (Exception ex)
        {
            // The mirror does not throw; if it ever does, nothing is known, so fail closed.
            return await SettlementBlockedAsync(escrowId, resolvedAt, actor, op, $"[{ex.GetType().Name}]");
        }

        if (result.BlocksSettlement)
        {
            return await SettlementBlockedAsync(escrowId, resolvedAt, actor, op, result.FailedLegs);
        }

        if (result.FailedLegs == null)
        {
            return null;
        }

        if (result.IsMismatch && overrideReason != null && op == "refund" && resolution == "buyer_wins")
        {
            return await RecordMismatchOverrideAsync(escrowId, result, actor, overrideReason);
        }

        if (result.IsMismatch)
        {
            AlarmLog(MoneyAlarmLog.DisputeVerdictOrderMismatch(escrowId, op, result.FailedLegs));
        }
        else
        {
            Console.WriteLine(
                $"DISPUTE_VERDICT_PROPAGATION_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} orderId={MoneyAlarmLog.LogId(result.OrderId)} deliveryId={MoneyAlarmLog.LogId(result.DeliveryId)} legs={MoneyAlarmLog.LogText(result.FailedLegs)}");
        }

        try
        {
            await EnsureAuditAsync(escrowId, DisputeVerdictMirror.VerdictAuditTimestamp(resolvedAt, result.AuditAction),
                result.AuditAction, "dispute_resolved", "dispute_resolved", actor,
                metadata: $"legs={result.FailedLegs};op={op};at={DateTime.UtcNow:o}");
        }
        catch (Exception ex)
        {
            // The ledger write failed; the answer stays the same and the next run writes the row.
            Console.WriteLine(
                $"DISPUTE_VERDICT_PROPAGATION_AUDIT_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} reason=[{ex.GetType().Name}]");
        }

        return result.IsMismatch
            ? new SettlementBlock(422, "ESCROW_INVALID_TRANSITION",
                $"The order or delivery behind this verdict cannot follow it ({result.FailedLegs}). Nothing was settled: "
                + "bring the order to a status the verdict can follow first.")
            : null;
    }

    /// <summary>
    /// The settlement fails closed (read, cancel or restock did not land). The hourly sweep retries;
    /// a cause that persists is alarmed (blocked in three of four hourly periods on escrow-cron) and the
    /// admin timeline shows one <c>dispute_settlement_blocked</c> row with the reason.
    /// </summary>
    private async Task<SettlementBlock> SettlementBlockedAsync(
        string escrowId, string resolvedAt, string actor, string op, string? legs)
    {
        AlarmLog(MoneyAlarmLog.DisputeVerdictSettlementBlocked(escrowId, op, legs));
        try
        {
            await EnsureAuditAsync(escrowId,
                DisputeVerdictMirror.VerdictAuditTimestamp(resolvedAt, DisputeVerdictMirror.SettlementBlockedAuditAction),
                DisputeVerdictMirror.SettlementBlockedAuditAction, "dispute_resolved", "dispute_resolved", actor,
                metadata: $"legs={legs};op={op};at={DateTime.UtcNow:o}");
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"DISPUTE_VERDICT_PROPAGATION_AUDIT_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} reason=[{ex.GetType().Name}]");
        }

        return new SettlementBlock(503, "ESCROW_012",
            "The order behind this verdict could not be read, cancelled or restocked. Nothing was settled; try again.");
    }

    /// <summary>
    /// ESC-03: an admin in the admin group refunds a <c>buyer_wins</c> verdict although its order
    /// cannot follow it (the goods are already moving). The escrow audit records the admin, the
    /// failed legs and the reason before any money moves (no row, no refund); the transaction
    /// audit and a flag on the order (its status is left alone) are best-effort.
    /// </summary>
    private async Task<SettlementBlock?> RecordMismatchOverrideAsync(
        string escrowId, DisputeVerdictMirrorResult result, string actor, string reason)
    {
        var metadata = $"legs={result.FailedLegs};reason={reason}";
        try
        {
            await AppendAuditAsync(escrowId, "dispute_order_mismatch_overridden", "dispute_resolved", "dispute_resolved",
                actor, metadata: metadata);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"DISPUTE_VERDICT_OVERRIDE_AUDIT_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} reason=[{ex.GetType().Name}]");
            return new SettlementBlock(503, "ESCROW_012",
                "The override could not be recorded. Nothing was settled; try again.");
        }

        Console.WriteLine(
            $"DISPUTE_VERDICT_MISMATCH_OVERRIDDEN escrowId={MoneyAlarmLog.LogId(escrowId)} admin={MoneyAlarmLog.LogId(actor)} legs={MoneyAlarmLog.LogText(result.FailedLegs)}");
        var transactionId = string.IsNullOrEmpty(result.OrderId) ? escrowId : result.OrderId;
        await TransactionAuditWriter.EmitAsync(_transactionAuditRepo, null, transactionId, "escrow", escrowId,
            "dispute_mismatch_override", "dispute_resolved", "dispute_resolved",
            _auditContext ?? new AuditContext { Actor = actor, ActorRole = "admin" }, metadata: metadata);

        if (!string.IsNullOrEmpty(result.OrderId)
            && !await _verdictMirror.FlagOrderAsync(result.OrderId, new Dictionary<string, object>
            {
                { "disputeOverrideAt", DateTime.UtcNow.ToString("o") },
                { "disputeOverrideBy", actor },
                { "disputeOverrideReason", reason },
                { "disputeOverrideEscrowId", escrowId }
            }))
        {
            Console.WriteLine(
                $"DISPUTE_VERDICT_OVERRIDE_FLAG_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} orderId={MoneyAlarmLog.LogId(result.OrderId)}");
        }

        return null;
    }

    /// <summary>
    /// SIM-12, ESC-07: freeze the escrow behind a delivery disputed through sync, before the
    /// delivery or order is written. Same status write and audit entry as
    /// <see cref="DisputeAsync"/>, but no idempotency record (the sync operation is already
    /// idempotent) and no window check (the delivery handler enforced the 48-hour window). The
    /// delivery's free-text reason is kept as the description under the <c>other</c> reason code.
    /// <para>
    /// The escrow: for a delivery with an order, the order escrow (<c>escrow-order-{orderId}</c>,
    /// strongly consistent, any status); otherwise, or when the order has none (paid through
    /// <c>POST /escrow/initiate</c>), the one on the <c>DeliveryIndex</c>, re-read consistently.
    /// The decision is always made on the base row: a manual payout queued (ESC-02) or a
    /// settlement lock held (ESC-03) refuses first, then <c>escrowed</c> is frozen,
    /// <c>disputed</c> is already frozen, and every other status (<c>initiated</c>,
    /// <c>released</c>, <c>completed</c>, <c>refunded</c>, <c>dispute_resolved</c>,
    /// <c>cancelled</c>, <c>held</c>, unknown) is refused. An order escrow linked to another
    /// delivery is refused too. Refusals are audited on the escrow; a caller writes nothing on a
    /// refusal. Throws when the outcome is unknown (the caller answers with a retry).
    /// </para>
    /// </summary>
    public async Task<DeliveryDisputeFreezeResult> FreezeForDeliveryDisputeAsync(
        string deliveryId, string? orderId, string disputedBy, string? description)
    {
        var escrow = await FindDeliveryDisputeEscrowAsync(deliveryId, orderId);
        if (escrow == null)
        {
            return new DeliveryDisputeFreezeResult(DeliveryDisputeFreeze.NoEscrow);
        }

        var escrowId = GetString(escrow, "escrowId");
        var linkedDeliveryId = GetStringOrNull(escrow, "deliveryId");
        if (!string.IsNullOrEmpty(linkedDeliveryId)
            && !string.Equals(linkedDeliveryId, deliveryId, StringComparison.Ordinal))
        {
            var status = GetString(escrow, "status");
            await AppendDisputeRefusalAuditAsync(escrowId, "dispute_refused_other_delivery", status, disputedBy,
                deliveryId, $"deliveryId={deliveryId}");
            Console.WriteLine($"ESCROW_DISPUTE_REFUSED_OTHER_DELIVERY escrowId={MoneyAlarmLog.LogId(escrowId)}");
            return new DeliveryDisputeFreezeResult(DeliveryDisputeFreeze.RefusedOtherDelivery, escrowId, status);
        }

        var decided = await ClassifyDeliveryDisputeAsync(escrow, deliveryId, disputedBy);
        if (decided != null)
        {
            return decided;
        }

        var now = DateTime.UtcNow;
        var disputeCreatedAt = now.ToString("o");
        var confirmedAt = GetStringOrNull(escrow, "deliveryConfirmedAt");
        var disputeWindowEndsAt = confirmedAt != null
            && DateTime.TryParse(confirmedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ct)
            ? ct.AddHours(DisputeWindowHours).ToString("o")
            : now.AddHours(DisputeWindowHours).ToString("o");

        var fields = new Dictionary<string, object>
        {
            { "disputeId", $"dispute-{Guid.NewGuid():N}" },
            { "disputedBy", disputedBy },
            { "disputeReason", "other" },
            { "disputeCreatedAt", disputeCreatedAt },
            { DisputeAuditAtField, disputeCreatedAt },
            { "disputeWindowEndsAt", disputeWindowEndsAt }
        };
        if (!string.IsNullOrWhiteSpace(description))
        {
            fields["disputeDescription"] = description.Length > MaxDisputeDescriptionLength
                ? description[..MaxDisputeDescriptionLength]
                : description;
        }

        try
        {
            await _escrowRepo.UpdateStatusAsync(escrowId, GetString(escrow, "createdAt"), "disputed", "escrowed", fields);
        }
        catch (ConditionalCheckFailedException)
        {
            // Lost a race: a settlement took the lock, the payout was queued, or another
            // dispute froze it meanwhile.
            var latest = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
            var after = latest != null ? await ClassifyDeliveryDisputeAsync(latest, deliveryId, disputedBy) : null;
            if (after != null)
            {
                return after;
            }

            // Still escrowed with nothing in the way, or gone: the outcome is unknown.
            throw;
        }

        // At a deterministic key: when this write fails after the freeze, the client's retry finds
        // the escrow AlreadyDisputed and writes the row then, never a second one.
        await EnsureAuditAsync(escrowId, disputeCreatedAt, "dispute", "escrowed", "disputed", disputedBy);
        return new DeliveryDisputeFreezeResult(DeliveryDisputeFreeze.Frozen, escrowId, "disputed");
    }

    /// <summary>The escrow a delivery dispute freezes, as a strongly consistent base row (ESC-07).</summary>
    private async Task<Dictionary<string, object>?> FindDeliveryDisputeEscrowAsync(string deliveryId, string? orderId)
    {
        if (!string.IsNullOrEmpty(orderId))
        {
            var orderEscrow = await _escrowRepo.GetByIdAsync($"escrow-order-{orderId}", consistentRead: true);
            if (orderEscrow != null)
            {
                return orderEscrow;
            }
        }

        // The DeliveryIndex copy only names the escrow: it can lag the base row.
        var indexed = await _escrowRepo.GetByDeliveryIdAsync(deliveryId);
        var indexedId = indexed != null ? GetStringOrNull(indexed, "escrowId") : null;
        return string.IsNullOrEmpty(indexedId)
            ? null
            : await _escrowRepo.GetByIdAsync(indexedId, consistentRead: true);
    }

    /// <summary>
    /// ESC-07: what a delivery dispute may do with this escrow row. Null when it is
    /// <c>escrowed</c> with nothing in the way (freeze it); otherwise the outcome, audited when it
    /// is a refusal. The manual payout marker and the lock are checked before the status, so a
    /// <c>disputed</c> row a manual payout queue still owns is refused, never mirrored.
    /// </summary>
    private async Task<DeliveryDisputeFreezeResult?> ClassifyDeliveryDisputeAsync(
        Dictionary<string, object> escrow, string deliveryId, string disputedBy)
    {
        var escrowId = GetString(escrow, "escrowId");
        var status = GetString(escrow, "status");

        // ESC-02: a queued manual payout is not frozen (the admin queue owns it). The refusal
        // is audited so the admin sees the delivery dispute on the escrow's timeline.
        if (IsManualPayoutOpen(escrow))
        {
            await AppendDisputeRefusalAuditAsync(escrowId, "dispute_refused_manual_payout", status, disputedBy,
                deliveryId, $"deliveryId={deliveryId};op={GetStringOrNull(escrow, ManualPayoutFields.Op)}");
            Console.WriteLine($"ESCROW_MANUAL_PAYOUT_DISPUTE_REFUSED escrowId={MoneyAlarmLog.LogId(escrowId)}");
            return new DeliveryDisputeFreezeResult(DeliveryDisputeFreeze.RefusedManualPayout, escrowId, status);
        }

        if (GetStringOrNull(escrow, "settlementLock") != null)
        {
            await RefuseFreezeSettlementInFlightAsync(escrowId, deliveryId, disputedBy, status);
            return new DeliveryDisputeFreezeResult(DeliveryDisputeFreeze.RefusedSettlementInFlight, escrowId, status);
        }

        switch (status)
        {
            case "escrowed":
                return null;
            case "disputed":
                // The freeze that made it disputed may have lost its audit row (ESC-07).
                await EnsureDisputeAuditAsync(escrow);
                return new DeliveryDisputeFreezeResult(DeliveryDisputeFreeze.AlreadyDisputed, escrowId, status);
            default:
                await AppendDisputeRefusalAuditAsync(escrowId, "dispute_refused_not_held", status, disputedBy,
                    deliveryId, $"deliveryId={deliveryId}");
                Console.WriteLine(
                    $"ESCROW_DISPUTE_REFUSED_NOT_HELD escrowId={MoneyAlarmLog.LogId(escrowId)} status={MoneyAlarmLog.LogId(status)}");
                return new DeliveryDisputeFreezeResult(DeliveryDisputeFreeze.RefusedNotHeld, escrowId, status);
        }
    }

    /// <summary>
    /// ESC-03: a delivery dispute that lands while a release or refund holds the settlement lock
    /// is not frozen onto the escrow (the settlement wins). Audited so the admin sees the
    /// delivery dispute on the escrow's timeline.
    /// </summary>
    private async Task RefuseFreezeSettlementInFlightAsync(
        string escrowId, string deliveryId, string disputedBy, string status)
    {
        await AppendDisputeRefusalAuditAsync(escrowId, "dispute_refused_settlement_in_flight", status, disputedBy,
            deliveryId, $"deliveryId={deliveryId}");
        Console.WriteLine($"ESCROW_DISPUTE_REFUSED_SETTLEMENT_IN_FLIGHT escrowId={MoneyAlarmLog.LogId(escrowId)}");
    }

    /// <summary>
    /// ESC-07: the escrow's <c>dispute</c> audit row at its deterministic key, written only if
    /// absent. Repairs a freeze whose own audit write failed after the status write. The key is the
    /// <see cref="DisputeAuditAtField"/> marker the freeze wrote with the status: an escrow disputed
    /// before ESC-07 has no marker (its row sits at the old write's own time), so it is left alone
    /// rather than given a second row in the immutable ledger.
    /// </summary>
    private async Task EnsureDisputeAuditAsync(Dictionary<string, object> escrow)
    {
        var auditAt = GetStringOrNull(escrow, DisputeAuditAtField);
        if (string.IsNullOrEmpty(auditAt))
        {
            return;
        }

        await EnsureAuditAsync(GetString(escrow, "escrowId"), auditAt, "dispute", "escrowed", "disputed",
            GetStringOrNull(escrow, "disputedBy") ?? SystemActor);
    }

    /// <summary>
    /// ESC-07: a party's <c>POST /escrow/dispute</c> found the escrow already <c>disputed</c>. When
    /// no manual payout or settlement owns it, finish what an earlier dispute request may have left
    /// undone after its freeze: the audit row and the delivery and order mirror (both idempotent).
    /// </summary>
    private async Task RepairFrozenDisputeAsync(Dictionary<string, object> escrow)
    {
        if (IsManualPayoutOpen(escrow) || GetStringOrNull(escrow, "settlementLock") != null)
        {
            return;
        }

        await EnsureDisputeAuditAsync(escrow);
        await MirrorDisputeAsync(
            GetString(escrow, "escrowId"), GetStringOrNull(escrow, "orderId"), GetStringOrNull(escrow, "deliveryId"),
            GetStringOrNull(escrow, "disputedBy") ?? SystemActor, GetStringOrNull(escrow, "disputeReason"),
            markDelivery: true);
    }

    /// <summary>
    /// ESC-07: audit a refused delivery dispute once per escrow, delivery and reason each UTC day.
    /// Clients before sync-protocol 1.12.0 resend a refused dispute on every reconnect, so the row
    /// carries a deterministic <c>auditId</c> (action, delivery, day) and is written only when the
    /// escrow's trail has none yet; it keeps the time of the day's first refusal. A failed read
    /// writes the row anyway (a duplicate is better than a lost refusal).
    /// </summary>
    private async Task AppendDisputeRefusalAuditAsync(
        string escrowId, string action, string status, string actor, string deliveryId, string metadata)
    {
        var now = DateTime.UtcNow;
        var auditId = $"audit-{action}-{new string(deliveryId.Where(char.IsAsciiLetterOrDigit).ToArray())}-"
                      + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        try
        {
            var trail = await _auditRepo.GetAuditTrailAsync(escrowId);
            if (trail != null && trail.Any(a => GetStringOrNull(a, "auditId") == auditId))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"DISPUTE_REFUSAL_AUDIT_READ_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} reason=[{ex.GetType().Name}]");
        }

        await _auditRepo.AppendAsync(BuildAuditEntry(
            auditId, now.ToString("o"), escrowId, action, status, status, actor, null, metadata));
    }

    /// <summary>
    /// Refund escrowed funds to the buyer (full amount, no commission deducted).
    /// Only callable by system (cron) or admin. Regular users cannot refund.
    /// </summary>
    /// <param name="role">The caller's JWT role claim (e.g. "admin"). Pass null for system/cron calls.</param>
    public async Task<EscrowResult<RefundEscrowResponse>> RefundAsync(
        RefundEscrowRequest request, string authenticatedUserId, string? role = null,
        AuditContext? auditContext = null, bool adminGroupVerified = false)
    {
        var progress = new SettlementProgress();
        try
        {
            return await RefundCoreAsync(request, authenticatedUserId, role, auditContext, adminGroupVerified, progress);
        }
        catch (Exception ex) when (progress.Locked && !progress.Recorded)
        {
            MoneyAlarmLog.MarkSettlementInFlight(ex);
            throw;
        }
    }

    private async Task<EscrowResult<RefundEscrowResponse>> RefundCoreAsync(
        RefundEscrowRequest request, string authenticatedUserId, string? role,
        AuditContext? auditContext, bool adminGroupVerified, SettlementProgress progress)
    {
        _auditContext = auditContext;

        // 1. Authorization: only system or admin can refund → ESCROW_021
        if (!await IsSystemOrAdminAsync(authenticatedUserId, role, adminGroupVerified))
        {
            return EscrowResult<RefundEscrowResponse>.Failure(
                403, "ESCROW_021", "Only system or admin can refund escrow funds.");
        }

        // 1b. Validate idempotency key format → ESCROW_003
        if (!IsValidIdempotencyKey(request.IdempotencyKey))
        {
            return EscrowResult<RefundEscrowResponse>.Failure(
                400, "ESCROW_003", "Invalid idempotency key format.", "idempotencyKey");
        }

        // 1c. Validate trigger enum
        if (!ValidRefundTriggers.Contains(request.Trigger))
        {
            return EscrowResult<RefundEscrowResponse>.Failure(
                400, "ESCROW_VALIDATION",
                $"Invalid refund trigger. Must be one of: {string.Join(", ", ValidRefundTriggers)}",
                "trigger");
        }

        // 1d. ESC-03: refunding a verdict over an order mismatch is an admin-group decision with a reason.
        string? overrideReason = null;
        if (request.OverrideOrderMismatch)
        {
            if (!adminGroupVerified)
            {
                return EscrowResult<RefundEscrowResponse>.Failure(
                    403, "ESCROW_021", "Only an admin in the admin group can override an order mismatch.");
            }

            overrideReason = request.Reason?.Trim() ?? "";
            if (overrideReason.Length is < 10 or > 500)
            {
                return EscrowResult<RefundEscrowResponse>.Failure(
                    400, "ESCROW_VALIDATION",
                    "reason must be 10 to 500 characters when overriding an order mismatch.", "reason");
            }
        }

        // 2. Check idempotency
        var existing = await _idempotencyRepo.GetAsync(request.IdempotencyKey);
        if (existing != null)
        {
            var existingEscrowId = GetString(existing, "escrowId");
            if (!string.IsNullOrEmpty(existingEscrowId)
                && GetString(existing, "status") == "completed")
            {
                var cached = await _escrowRepo.GetByIdAsync(existingEscrowId);
                if (cached != null)
                {
                    return EscrowResult<RefundEscrowResponse>.Duplicate(
                        BuildRefundResponse(cached));
                }
            }
        }

        // 3. Get escrow → ESCROW_016
        var escrowItem = await _escrowRepo.GetByIdAsync(request.EscrowId);
        if (escrowItem == null)
        {
            return EscrowResult<RefundEscrowResponse>.Failure(
                404, "ESCROW_016", "Escrow not found.");
        }

        // 4. Validate state: "escrowed", or "dispute_resolved" with a verdict that does not
        //    contradict a refund (ESC-03) → ESCROW_INVALID_TRANSITION
        var currentStatus = GetString(escrowItem, "status");
        var refusal = SettlementRefusal(escrowItem, "refund");
        if (refusal != null)
        {
            return EscrowResult<RefundEscrowResponse>.Failure(422, "ESCROW_INVALID_TRANSITION", refusal);
        }

        // 4a. ESC-02: already waiting for an admin in the manual payout queue (see ReleaseAsync).
        if (IsManualPayoutOpen(escrowItem))
        {
            return ManualPayoutWaiting<RefundEscrowResponse>();
        }

        // 4a2. ESC-03: the order and delivery follow the verdict before any money moves; a
        //      verdict they cannot follow is not settled.
        if (currentStatus == "dispute_resolved"
            && await MirrorVerdictAsync(escrowItem, authenticatedUserId, "refund", overrideReason) is { } block)
        {
            return EscrowResult<RefundEscrowResponse>.Failure(block.HttpStatus, block.Code, block.Message);
        }

        // 4a3. ESC-09 Part B: an admin refund of a plain escrowed escrow ends an order that never got
        //      going first and refuses goods on the move unless overridden, so the order follows the money.
        if (currentStatus == "escrowed" && authenticatedUserId != SystemActor
            && await PrepareAdminRefundOrderAsync(escrowItem, authenticatedUserId, overrideReason) is { } refundBlock)
        {
            return EscrowResult<RefundEscrowResponse>.Failure(refundBlock.HttpStatus, refundBlock.Code, refundBlock.Message);
        }

        var amount = GetDecimal(escrowItem, "amount");
        var holdRef = GetStringOrNull(escrowItem, "telebirrHoldRef") ?? "";
        var createdAt = GetString(escrowItem, "createdAt");
        var currentVersion = GetInt(escrowItem, "version");

        // ESC-05: the refund phone is read before the lock, so nothing can fail between the lock
        // and the gateway call.
        var buyerPhone = await ResolveRefundPhoneAsync(GetString(escrowItem, "buyerId"));

        // Acquire the settlement lock BEFORE the irreversible Telebirr call. Mutual exclusion
        // across release/refund and across idempotency keys: a refund that loses the race to a
        // release (or another refund) NEVER calls Telebirr, so funds cannot be double-moved.
        var lockAcquired = await _escrowRepo.TryAcquireSettlementLockAsync(
            request.EscrowId, createdAt, "refund", currentStatus);
        if (!lockAcquired)
        {
            var latest = await _escrowRepo.GetByIdAsync(request.EscrowId);
            var latestStatus = latest != null ? GetString(latest, "status") : "";
            if (latest != null && latestStatus == "refunded")
            {
                // A prior refund already settled this escrow — safe to return the result.
                return EscrowResult<RefundEscrowResponse>.Duplicate(BuildRefundResponse(latest));
            }
            if (latest != null && IsManualPayoutOpen(latest))
            {
                return ManualPayoutWaiting<RefundEscrowResponse>();
            }
            // A settlement (possibly a release) is in progress or already completed.
            // Do NOT call Telebirr.
            return EscrowResult<RefundEscrowResponse>.Failure(
                409, "ESCROW_INVALID_TRANSITION",
                "A settlement is already in progress or completed for this escrow.");
        }

        // ESC-05: see ReleaseAsync. The gateway may be called from here on.
        progress.Locked = true;

        // 5. Call Telebirr RefundFunds with the full amount (only the lock winner reaches here).
        var refundResponse = await CallRefundAsync(new RefundFundsRequest
        {
            TransactionRef = RefundTransactionRef(request.EscrowId),
            HoldRef = holdRef,
            BuyerPhone = buyerPhone,
            Amount = amount
        });

        // Record idempotency for the cached-duplicate return (best-effort; the lock is the guard).
        await StoreIdempotencyBestEffortAsync(request.IdempotencyKey, request.EscrowId);

        // 5a. ESC-02: the gateway cannot refund (or gave no reference, or, ESC-05, did not say
        //     whether it refunded): queue the buyer leg for an admin, keep the lock, leave the
        //     status alone. See ReleaseAsync 6a.
        var refundManualReason = ManualPayoutReason(
            refundResponse.Success, refundResponse.ErrorCode, refundResponse.TelebirrRef);
        var refundOutcomeUnknown = refundManualReason == null
            && IsOutcomeUnknownAnswer(refundResponse.Success, refundResponse.ErrorCode);
        if (refundOutcomeUnknown)
        {
            await AppendFailureAuditBestEffortAsync(request.EscrowId, "refund_failed", currentStatus,
                authenticatedUserId, refundResponse.ErrorCode);
            refundManualReason = ManualPayoutFields.ReasonGatewayOutcomeUnknown;
        }

        if (refundManualReason != null)
        {
            var legs = new List<ManualPayoutLegSeed>
            {
                new(ManualPayoutFields.LegBuyer, ManualPayoutFields.LegPending, null, null, null)
            };
            var nothingMoved = refundManualReason == ManualPayoutFields.ReasonPayoutNotSupported;
            if (await QueueManualPayoutAsync(request.EscrowId, createdAt, ManualPayoutFields.OpRefund,
                    refundManualReason, currentStatus, legs, authenticatedUserId,
                    nothingMoved ? 1 : MaxSettlementWriteAttempts))
            {
                await UpdateIdempotencyBestEffortAsync(request.IdempotencyKey, request.EscrowId, "manual_payout");
                return refundOutcomeUnknown
                    ? OutcomeUnknownWaiting<RefundEscrowResponse>()
                    : ManualPayoutWaiting<RefundEscrowResponse>();
            }

            if (!nothingMoved)
            {
                return await OutcomeUnknownNotQueuedAsync<RefundEscrowResponse>(
                    request.EscrowId, "refund_failed", currentStatus, authenticatedUserId, refundManualReason, null);
            }

            await _escrowRepo.ReleaseSettlementLockAsync(request.EscrowId, createdAt);
            progress.Locked = false;
            await _idempotencyRepo.DeleteAsync(request.IdempotencyKey);
            await AppendAuditAsync(request.EscrowId, "refund_failed", currentStatus, currentStatus,
                authenticatedUserId, metadata: QueueFailedNotSupportedCode);
            return EscrowResult<RefundEscrowResponse>.Failure(
                502, "ESCROW_013",
                "The refund could not be queued for manual settlement. It is retried by the next sweep.");
        }

        if (!refundResponse.Success)
        {
            // A definite refusal: release the lock + idempotency record so the next sweep
            // retries, then audit (the lock first, so a failed audit write cannot strand it).
            await _escrowRepo.ReleaseSettlementLockAsync(request.EscrowId, createdAt);
            progress.Locked = false;
            await _idempotencyRepo.DeleteAsync(request.IdempotencyKey);
            await AppendAuditAsync(request.EscrowId, "refund_failed", currentStatus, currentStatus,
                authenticatedUserId, metadata: refundResponse.ErrorCode);
            return EscrowResult<RefundEscrowResponse>.Failure(
                502, "ESCROW_013",
                refundResponse.ErrorMessage ?? "Telebirr gateway error during refund.");
        }

        var now = DateTime.UtcNow.ToString("o");

        // 6. Transition to "refunded". We hold the settlement lock so status is still
        //    `currentStatus`. The buyer has been paid, so transient failures are retried like the
        //    release write (ESC-05); if the write never lands the lock stays held and the
        //    recovery step parks the escrow for an admin.
        await WriteSettledStatusAsync(request.EscrowId, createdAt, "refunded", currentStatus,
            new Dictionary<string, object>
            {
                { "telebirrRefundRef", refundResponse.TelebirrRef ?? "" },
                { "refundedAt", now },
                { "version", currentVersion + 1 }
            },
            $"op=refund buyerRef={MoneyAlarmLog.LogId(refundResponse.TelebirrRef)}");
        progress.Recorded = true;

        // 6. Audit log
        await AppendAuditAsync(request.EscrowId, "refund", currentStatus, "refunded",
            authenticatedUserId, refundResponse.TelebirrRef);

        await UpdateIdempotencyBestEffortAsync(request.IdempotencyKey, request.EscrowId, "completed");

        // 7. Return RefundEscrowResponse
        return EscrowResult<RefundEscrowResponse>.Success(new RefundEscrowResponse
        {
            EscrowId = request.EscrowId,
            Status = "refunded",
            RefundAmount = amount,
            TelebirrRef = refundResponse.TelebirrRef,
            RefundedAt = now,
            Message = "Funds refunded to buyer's Telebirr account."
        });
    }

    // ──────────────────────────────────────────────
    // Gateway outcomes and the settlement lock (ESC-05)
    // ──────────────────────────────────────────────

    /// <summary>The stable Telebirr references of the three payout legs.</summary>
    private static string ReleaseTransactionRef(string escrowId) => $"release:{escrowId}";

    private static string TruckerReleaseTransactionRef(string escrowId) => $"release:{escrowId}:trucker";

    private static string RefundTransactionRef(string escrowId) => $"refund:{escrowId}";

    /// <summary>The reference a manual payout leg was (or would have been) sent to the gateway under.</summary>
    private static string GatewayTransactionRef(string escrowId, string leg) => leg switch
    {
        ManualPayoutFields.LegFarmer => ReleaseTransactionRef(escrowId),
        ManualPayoutFields.LegTrucker => TruckerReleaseTransactionRef(escrowId),
        _ => RefundTransactionRef(escrowId)
    };

    /// <summary>
    /// A failure code after which Telebirr may have moved the money: a timeout, a lost or
    /// unreadable answer, a gateway call that threw, or a manual payout queue write that failed
    /// for any reason but <c>PAYOUT_NOT_SUPPORTED</c> (ESC-13, ESC-05).
    /// </summary>
    internal static bool IsOutcomeUnknownCode(string? code)
        => code != null
           && (UncertainGatewayErrors.Contains(code)
               || code.StartsWith(GatewayExceptionCodePrefix, StringComparison.Ordinal)
               || (code.StartsWith(ManualPayoutFields.QueueFailedCodePrefix, StringComparison.Ordinal)
                   && code != QueueFailedNotSupportedCode));

    private static bool IsOutcomeUnknownAnswer(bool success, string? errorCode)
        => !success && IsOutcomeUnknownCode(errorCode);

    /// <summary>
    /// The gateway release call. A call that throws may have reached Telebirr, and no answer at
    /// all is a lost answer: both are unknown outcomes, never a definite refusal.
    /// </summary>
    private async Task<ReleaseFundsResponse> CallReleaseAsync(ReleaseFundsRequest request)
    {
        try
        {
            var response = await _telebirrGateway.ReleaseFundsAsync(request);
            return response is null
                ? new ReleaseFundsResponse { Success = false, ErrorCode = TelebirrGatewayErrors.Network, ErrorMessage = NoGatewayAnswer }
                : response;
        }
        catch (Exception ex)
        {
            return new ReleaseFundsResponse
            {
                Success = false,
                ErrorCode = GatewayExceptionCodePrefix + ex.GetType().Name,
                ErrorMessage = GatewayCallFailed
            };
        }
    }

    /// <summary>The gateway refund call, with the outcome rules of <see cref="CallReleaseAsync"/>.</summary>
    private async Task<RefundFundsResponse> CallRefundAsync(RefundFundsRequest request)
    {
        try
        {
            var response = await _telebirrGateway.RefundFundsAsync(request);
            return response is null
                ? new RefundFundsResponse { Success = false, ErrorCode = TelebirrGatewayErrors.Network, ErrorMessage = NoGatewayAnswer }
                : response;
        }
        catch (Exception ex)
        {
            return new RefundFundsResponse
            {
                Success = false,
                ErrorCode = GatewayExceptionCodePrefix + ex.GetType().Name,
                ErrorMessage = GatewayCallFailed
            };
        }
    }

    private const string NoGatewayAnswer = "Telebirr gave no answer; the payout may have been made.";
    private const string GatewayCallFailed = "The Telebirr call failed; the payout may have been made.";

    /// <summary>
    /// The status write after the gateway paid (<c>released</c> or <c>refunded</c>). The money has
    /// moved, so a transient failure is retried (4 attempts, 150 ms times the attempt apart). A
    /// failed condition is not retried: an earlier attempt (ours, or one the SDK retried after a
    /// lost answer) may have landed although the client saw an error, so a strongly consistent
    /// re-read that shows the new status counts as written (ESC-05). If the write never lands, the
    /// references go to the log and the exception escapes with the lock held; the recovery step
    /// parks the escrow.
    /// </summary>
    private Task WriteSettledStatusAsync(
        string escrowId, string createdAt, string newStatus, string expectedStatus,
        Dictionary<string, object> fields, string paidRefs)
        => WriteAfterPayoutAsync(
            escrowId,
            () => _escrowRepo.UpdateStatusAsync(escrowId, createdAt, newStatus, expectedStatus, fields),
            () => HasSettledStatusAsync(escrowId, newStatus),
            paidRefs);

    /// <summary>
    /// The retry rules of <see cref="WriteSettledStatusAsync"/> for any write that must land after
    /// the gateway paid (ESC-04: the trucker leg's record too). <paramref name="write"/> throws
    /// <see cref="ConditionalCheckFailedException"/> on a failed condition, and
    /// <paramref name="landed"/> tells from a strongly consistent read whether it landed anyway.
    /// </summary>
    private async Task WriteAfterPayoutAsync(
        string escrowId, Func<Task> write, Func<Task<bool>> landed, string paidRefs)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await write();
                return;
            }
            catch (ConditionalCheckFailedException)
            {
                // The SDK retries a write whose answer was lost, so even a first attempt can fail
                // its condition on our own commit: a strongly consistent re-read decides.
                if (await landed())
                {
                    return;
                }

                LogPaidUnrecorded(escrowId, paidRefs);
                throw;
            }
            catch (Exception) when (attempt < MaxSettlementWriteAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt));
            }
            catch (Exception)
            {
                LogPaidUnrecorded(escrowId, paidRefs);
                throw;
            }
        }
    }

    /// <summary>
    /// Whether the row already carries the settled status (a <c>released</c> row may already have
    /// been completed by the crash-recovery sweep). A failed read counts as no.
    /// </summary>
    private async Task<bool> HasSettledStatusAsync(string escrowId, string newStatus)
    {
        try
        {
            var latest = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
            var status = latest != null ? GetString(latest, "status") : "";
            return status == newStatus || (newStatus == "released" && status == "completed");
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The gateway paid but the escrow could not record it. Not an alarm token (the exception
    /// that follows is logged as an unknown outcome); it gives the operator the references.
    /// </summary>
    private static void LogPaidUnrecorded(string escrowId, string paidRefs)
        => Console.WriteLine($"ESCROW_SETTLEMENT_PAID_UNRECORDED escrowId={MoneyAlarmLog.LogId(escrowId)} {paidRefs}");

    /// <summary>
    /// Queue a settlement, retrying the write up to <paramref name="attempts"/> times (150 ms times
    /// the attempt apart). See <see cref="TryQueueManualPayoutAsync"/>.
    /// </summary>
    private async Task<bool> QueueManualPayoutAsync(
        string escrowId, string createdAt, string op, string reason, string fromStatus,
        List<ManualPayoutLegSeed> legs, string actor, int attempts, Func<string, Task<bool>>? write = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (await TryQueueManualPayoutAsync(escrowId, createdAt, op, reason, fromStatus, legs, actor, write))
            {
                return true;
            }

            if (attempt >= attempts)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt));
        }
    }

    /// <summary>
    /// The gateway may have moved money and the manual payout queue write kept failing. The lock
    /// and the idempotency record stay, nothing retries the gateway, and the recovery step parks
    /// the escrow once its lock is 15 minutes old (ESC-05, FUP-03).
    /// </summary>
    private async Task<EscrowResult<T>> OutcomeUnknownNotQueuedAsync<T>(
        string escrowId, string auditAction, string status, string actor, string reason, string? paidRefs)
    {
        await AppendFailureAuditBestEffortAsync(escrowId, auditAction, status, actor,
            ManualPayoutFields.QueueFailedCodePrefix + reason);
        if (paidRefs != null)
        {
            LogPaidUnrecorded(escrowId, paidRefs);
        }

        return EscrowResult<T>.Failure(
            502, "ESCROW_013",
            "The gateway may already have paid, and the payout could not be queued for an admin. "
            + "It is not retried: the escrow stays locked and the recovery sweep queues it.");
    }

    /// <summary>
    /// A failure audit row on a path where the money may have moved (ESC-05). Its alarm line is
    /// logged before the write; a failed write must not stop the queue write that follows.
    /// </summary>
    private async Task AppendFailureAuditBestEffortAsync(
        string escrowId, string action, string status, string actor, string? code)
    {
        try
        {
            await AppendAuditAsync(escrowId, action, status, status, actor, metadata: code);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"ESCROW_SETTLEMENT_AUDIT_DEFERRED escrowId={MoneyAlarmLog.LogId(escrowId)} action={action} error={ex.GetType().Name}");
        }
    }

    /// <summary>
    /// The idempotency record of a settlement that holds the lock. Best-effort: it only serves the
    /// cached answer of a same-key replay, and a failed write must not turn a settled or queued
    /// escrow into an unknown outcome.
    /// </summary>
    private async Task StoreIdempotencyBestEffortAsync(string idempotencyKey, string escrowId)
    {
        try
        {
            await _idempotencyRepo.StoreAsync(idempotencyKey, escrowId, "processing");
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"ESCROW_IDEMPOTENCY_WRITE_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} status=processing error={ex.GetType().Name}");
        }
    }

    private async Task UpdateIdempotencyBestEffortAsync(string idempotencyKey, string escrowId, string status)
    {
        try
        {
            await _idempotencyRepo.UpdateAsync(idempotencyKey, status);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"ESCROW_IDEMPOTENCY_WRITE_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} status={status} error={ex.GetType().Name}");
        }
    }

    /// <summary>What the recovery step did with a row (ESC-05).</summary>
    public enum StaleSettlementOutcome
    {
        /// <summary>Not stale, not settleable, already queued, or no lock: left alone.</summary>
        Skipped,

        /// <summary>Parked in the manual payout queue as <c>GATEWAY_OUTCOME_UNKNOWN</c>.</summary>
        Parked,

        /// <summary>The row changed before the write (another recovery parked it, or a new lock): left alone, silently.</summary>
        Lost,

        /// <summary>The park write threw and the row still holds the same stale lock: alarmed, the next run tries again.</summary>
        Failed
    }

    /// <summary>
    /// ESC-05 recovery step, for one row from <see cref="IEscrowRepository.QueryStaleSettlementLocksAsync"/>:
    /// a settlement lock older than 15 minutes on a settleable row that is not queued has no living
    /// owner, and the gateway may have been called under it. Park it in the manual payout queue
    /// (reason <c>GATEWAY_OUTCOME_UNKNOWN</c>, one pending leg per payee of the locked operation),
    /// conditional on the same lock and status. Never calls the gateway and never releases a lock.
    /// A lock from before ESC-05 has no lock time and is aged by <c>updatedAt</c>.
    /// </summary>
    public async Task<StaleSettlementOutcome> ParkStaleSettlementAsync(Dictionary<string, object> escrow, DateTime now)
    {
        var escrowId = GetString(escrow, "escrowId");
        var createdAt = GetString(escrow, "createdAt");
        var status = GetString(escrow, "status");
        var op = GetStringOrNull(escrow, SettlementLockFields.Lock);
        var lockAt = GetStringOrNull(escrow, SettlementLockFields.LockAt);
        var lockId = GetStringOrNull(escrow, SettlementLockFields.LockId);
        var lockedSince = lockAt ?? GetStringOrNull(escrow, "updatedAt");
        if (op is not (ManualPayoutFields.OpRelease or ManualPayoutFields.OpRefund)
            || IsManualPayoutOpen(escrow)
            || !StaleLockStatuses.Contains(status)
            || !DateTime.TryParse(lockedSince, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var locked)
            || now.ToUniversalTime() - locked.ToUniversalTime() < SettlementLockFields.StaleAfter)
        {
            return StaleSettlementOutcome.Skipped;
        }

        var legs = new List<ManualPayoutLegSeed>();
        if (op == ManualPayoutFields.OpRelease)
        {
            legs.Add(new(ManualPayoutFields.LegFarmer, ManualPayoutFields.LegPending, null, null, null));
            if ((GetDecimalOrNull(escrow, "truckerPayout") ?? 0m) > 0)
            {
                legs.Add(new(ManualPayoutFields.LegTrucker, ManualPayoutFields.LegPending, null, null, null));
            }
        }
        else
        {
            legs.Add(new(ManualPayoutFields.LegBuyer, ManualPayoutFields.LegPending, null, null, null));
        }

        var queuedAt = DateTime.UtcNow.ToString("o");
        var parkId = Guid.NewGuid().ToString("N");
        bool queued;
        Exception? writeError = null;
        try
        {
            queued = await _escrowRepo.TryQueueStaleSettlementAsync(
                escrowId, createdAt, op, status, legs, queuedAt, lockId, parkId);
        }
        catch (Exception ex)
        {
            writeError = ex;
            queued = false;
        }

        if (!queued)
        {
            // Ours only if the row carries this very write (its park id) on the same lock: a write
            // whose answer was lost. Anything else (another recovery parked it first, a new lock)
            // is left alone without a log line, audit row or alarm.
            var latest = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
            if (latest == null
                || !IsManualPayoutOpen(latest)
                || GetStringOrNull(latest, ManualPayoutFields.Op) != op
                || GetStringOrNull(latest, SettlementLockFields.LockId) != lockId
                || GetStringOrNull(latest, ManualPayoutFields.ParkId) != parkId)
            {
                // A write that threw on a row still holding the same stale lock, unqueued: the
                // unknown outcome is still unparked, so it alarms and the next run tries again.
                if (writeError != null && latest != null && !IsManualPayoutOpen(latest)
                    && GetStringOrNull(latest, SettlementLockFields.Lock) == op
                    && GetStringOrNull(latest, SettlementLockFields.LockId) == lockId)
                {
                    AlarmLog(MoneyAlarmLog.SettlementFailed(
                        escrowId, "stale_lock_recovery_failed", MoneyAlarmLog.OutcomeUnknown, writeError.GetType().Name));
                    return StaleSettlementOutcome.Failed;
                }

                return StaleSettlementOutcome.Lost;
            }
        }

        // The alarm line first, so a failed audit write still alarms.
        var ageMinutes = (int)(now.ToUniversalTime() - locked.ToUniversalTime()).TotalMinutes;
        AlarmLog(MoneyAlarmLog.SettlementFailed(escrowId, "stale_lock_parked", MoneyAlarmLog.OutcomeUnknown, op));
        var legNames = string.Join(",", legs.Select(l => l.Leg));
        Console.WriteLine(
            $"ESCROW_MANUAL_PAYOUT_QUEUED escrowId={MoneyAlarmLog.LogId(escrowId)} op={op} legs={legNames} reason={ManualPayoutFields.ReasonGatewayOutcomeUnknown}");

        // Deterministic keys: the park row one tick before the queued row, so it never shares
        // the queued row's timestamp (the audit table's sort key).
        // A legacy lock can contradict the verdict of a dispute_resolved row (a release locked
        // before a buyer_wins verdict); the row and the admin page say so.
        await EnsureAuditAsync(escrowId, AddTicks(queuedAt, -1), "stale_lock_parked", status, status, SystemActor,
            metadata: $"op={op};lockedAt={lockedSince};ageMinutes={ageMinutes};legacyLock={(lockId == null ? "true" : "false")}"
                      + $";disputeResolution={GetStringOrNull(escrow, "disputeResolution") ?? "-"}");
        await EnsureAuditAsync(escrowId, queuedAt, "manual_payout_queued", status, status, SystemActor,
            metadata: $"op={op};legs={legNames};reason={ManualPayoutFields.ReasonGatewayOutcomeUnknown}");
        // ESC-09: a parked release is committed (the queue owns it), so its order is disbursing.
        await FollowMoneyAsync(escrowId, "stale_lock");
        return StaleSettlementOutcome.Parked;
    }

    // ──────────────────────────────────────────────
    // Settlement blocks (ESC-10)
    // ──────────────────────────────────────────────

    /// <summary>
    /// ESC-10: a settlement the hourly sweeps cannot make on this run, audited once per reason. The
    /// reason (<see cref="SettlementBlockFields"/>, <c>&lt;op&gt;:&lt;gate&gt;:&lt;reason&gt;</c>) is stored on the
    /// escrow by compare-and-set with the time first seen, and the audit row
    /// (<paramref name="auditAction"/>) sits at that time, so later runs that see the same reason write
    /// nothing but re-ensure a row a crash lost. With <paramref name="alarmAction"/> the run whose write
    /// creates that row logs one <c>ESCROW_SETTLEMENT_FAILED</c> line (the block needs a person): once by
    /// construction, also when the reason's write landed but its answer was lost, so the row came only on a
    /// later run. A lost-answer write that the SDK retried is recognised by its time. Best-effort: a failed
    /// write logs <c>SETTLEMENT_BLOCK_AUDIT_FAILED</c> and the next run repairs it. Returns true when this
    /// call stored the reason.
    /// </summary>
    public async Task<bool> NoteSettlementBlockedAsync(
        Dictionary<string, object> escrow, string expectedStatus, string reason, string auditAction,
        string metadata, string? alarmAction = null)
    {
        var escrowId = GetString(escrow, "escrowId");
        try
        {
            var storedAt = GetStringOrNull(escrow, SettlementBlockFields.ReasonAt);
            if (GetStringOrNull(escrow, SettlementBlockFields.Reason) == reason && storedAt != null)
            {
                await AuditBlockOnceAsync(escrowId, storedAt);
                return false;
            }

            var reasonAt = DateTime.UtcNow.ToString("o");
            if (!await _escrowRepo.TrySetSettlementBlockReasonAsync(
                    escrowId, GetString(escrow, "createdAt"), expectedStatus, reason, reasonAt))
            {
                var latest = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
                var latestAt = latest == null ? null : GetStringOrNull(latest, SettlementBlockFields.ReasonAt);
                if (latest == null || GetStringOrNull(latest, SettlementBlockFields.Reason) != reason || latestAt == null)
                {
                    return false; // the escrow moved on (status, lock, queue): nothing to record
                }

                if (latestAt != reasonAt)
                {
                    await AuditBlockOnceAsync(escrowId, latestAt);
                    return false;
                }
            }

            await AuditBlockOnceAsync(escrowId, reasonAt);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SETTLEMENT_BLOCK_AUDIT_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} reason=[{ex.GetType().Name}]");
            return false;
        }

        // The block's one audit row at its stored time, and the alarm line only from the write that created it.
        async Task AuditBlockOnceAsync(string id, string at)
        {
            if (await EnsureAuditAsync(id, at, auditAction, expectedStatus, expectedStatus, SystemActor, metadata: metadata)
                && alarmAction != null)
            {
                AlarmLog(MoneyAlarmLog.SettlementFailed(id, alarmAction, MoneyAlarmLog.OutcomeFailed, reason));
            }
        }
    }

    /// <summary>
    /// ESC-10: the gate (<paramref name="gate"/> for <paramref name="op"/>) passed, so a reason it stored
    /// earlier no longer holds: remove it, only when the row still carries that very reason. Writes nothing
    /// when the row carries no reason of this gate. Best-effort.
    /// </summary>
    public async Task ClearSettlementBlockAsync(Dictionary<string, object> escrow, string op, string gate)
    {
        var stored = GetStringOrNull(escrow, SettlementBlockFields.Reason);
        if (stored == null || !stored.StartsWith(SettlementBlockFields.Prefix(op, gate), StringComparison.Ordinal))
        {
            return;
        }

        var escrowId = GetString(escrow, "escrowId");
        try
        {
            await _escrowRepo.TryClearSettlementBlockReasonAsync(escrowId, GetString(escrow, "createdAt"), stored);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SETTLEMENT_BLOCK_CLEAR_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} reason=[{ex.GetType().Name}]");
        }
    }

    // ──────────────────────────────────────────────
    // Trucker leg retry (ESC-04)
    // ──────────────────────────────────────────────

    /// <summary>The audit action of a trucker leg the payout gate blocks (no money alarm, like <c>release_blocked_kyc</c>).</summary>
    private const string TruckerBlockedAuditAction = "trucker_release_blocked_kyc";

    /// <summary>Audit metadata of a payout the identity gate blocks: the farmer leg (ESC-10) and the trucker leg (ESC-04).</summary>
    private static string PayoutBlockedMetadata(string reason) => $"code=ESCROW_029;reason={reason}";

    /// <summary>What the trucker retry did with one row (ESC-04).</summary>
    public enum TruckerRetryOutcome
    {
        /// <summary>Not released, no pending trucker leg, or queued: nothing to do.</summary>
        Skipped,

        /// <summary>The gateway paid the trucker; the escrow is completed.</summary>
        Paid,

        /// <summary>The leg waits in the manual payout queue (the gateway cannot pay out, did not say, or a stale trucker lock was parked).</summary>
        Queued,

        /// <summary>The payout gate blocks the trucker; the gateway was not called.</summary>
        Blocked,

        /// <summary>A definite refusal (the lock is released, the next run retries), or a queue write that did not land.</summary>
        Failed,

        /// <summary>Another run holds the trucker lock or took it first.</summary>
        InFlight,

        /// <summary>No gateway call this run: out of time, over the per-run cap, or the row changed.</summary>
        Deferred
    }

    /// <summary>The outcome and, for a leg still waiting, why (the overdue line's reason).</summary>
    public readonly record struct TruckerRetryResult(TruckerRetryOutcome Outcome, string? Reason);

    /// <summary>
    /// ESC-04, one row from <see cref="IEscrowRepository.QueryPendingTruckerLegsAsync"/>: a released
    /// escrow whose trucker leg is owed. The flag only exists where nothing moved for the trucker
    /// (a definite refusal or a blocked trucker, ESC-05), so the leg may be paid again with the
    /// same stable reference once the trucker passes the payout gate. Fences: a strongly consistent
    /// read first, the trucker leg's own lock (the row keeps the release's settlement lock), no
    /// retry of a queued row, and every answer that may have paid queued with the lock kept. A
    /// lock older than 15 minutes is parked, never released. With <paramref name="mayCallGateway"/>
    /// false (the run is out of time or over its cap) no lock is taken.
    /// </summary>
    public async Task<TruckerRetryResult> RetryTruckerLegAsync(Dictionary<string, object> row, DateTime now, bool mayCallGateway)
    {
        var escrowId = GetString(row, "escrowId");
        var escrow = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
        if (escrow == null || GetString(escrow, "status") != "released"
            || !GetBool(escrow, TruckerPayoutFields.Pending) || IsManualPayoutOpen(escrow))
        {
            return new(TruckerRetryOutcome.Skipped, null);
        }

        var heldLock = GetStringOrNull(escrow, TruckerPayoutFields.LockId);
        if (heldLock != null)
        {
            var lockAt = GetStringOrNull(escrow, TruckerPayoutFields.LockAt);
            var fresh = DateTime.TryParse(lockAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var locked)
                        && now.ToUniversalTime() - locked.ToUniversalTime() < SettlementLockFields.StaleAfter;
            if (fresh)
            {
                return new(TruckerRetryOutcome.InFlight, "in_flight");
            }

            try
            {
                return await ParkStaleTruckerLegAsync(escrow, heldLock, lockAt, now);
            }
            catch (Exception ex)
            {
                // The stale lock's gateway call may have paid: a failure here is an unknown outcome.
                MoneyAlarmLog.MarkSettlementInFlight(ex);
                throw;
            }
        }

        // A pending leg without a pending time was left by the code before ESC-04 (a crash between
        // the released and completed writes), when the runbook said to pay it by hand: parked for
        // the admin, never retried.
        if (GetStringOrNull(escrow, TruckerPayoutFields.PendingSince) == null)
        {
            return await ParkLegacyAsync(escrow, "released")
                ? new(TruckerRetryOutcome.Queued, "legacy")
                : new(TruckerRetryOutcome.Skipped, null);
        }

        // O2: never pay without the identity gate; a missing profile repository is a misconfiguration.
        if (_userRepo == null)
        {
            Console.WriteLine($"ESCROW_TRUCKER_PAYOUT_GATE_UNAVAILABLE escrowId={MoneyAlarmLog.LogId(escrowId)}");
            return new(TruckerRetryOutcome.Blocked, "gate_unavailable");
        }

        var (truckerId, fromOrder, block) = await ResolveTruckerIdAsync(escrow);
        if (truckerId != null && fromOrder && !await BackfillTruckerIdAsync(escrow, truckerId, SystemActor))
        {
            // A truckerId landed in between: the next run decides on it.
            return new(TruckerRetryOutcome.Deferred, "trucker_changed");
        }

        var amount = GetDecimalOrNull(escrow, "truckerPayout") ?? 0m;
        UserDto? trucker = null;
        if (truckerId != null)
        {
            trucker = await _userRepo.GetUserByIdAsync(truckerId);
            block = PayoutBlockReason(trucker, amount);
        }

        if (block != null)
        {
            await NoteTruckerBlockedAsync(escrow, block);
            return new(TruckerRetryOutcome.Blocked, block);
        }

        if (!mayCallGateway)
        {
            return new(TruckerRetryOutcome.Deferred, "deferred");
        }

        var lockId = await _escrowRepo.TryAcquireTruckerPayoutLockAsync(escrowId, GetString(escrow, "createdAt"));
        if (lockId == null)
        {
            return new(TruckerRetryOutcome.InFlight, "in_flight");
        }

        // From here on the gateway may have been called: an exception that escapes before the
        // payout is recorded is an unknown outcome, and the lock stays for the recovery to park.
        var progress = new SettlementProgress { Locked = true };
        try
        {
            return await PayTruckerLegAsync(escrow, lockId, trucker!.PhoneNumber, amount, progress);
        }
        catch (Exception ex) when (progress.Locked && !progress.Recorded)
        {
            MoneyAlarmLog.MarkSettlementInFlight(ex);
            throw;
        }
    }

    /// <summary>The gateway call under the trucker lock and what follows from its answer.</summary>
    private async Task<TruckerRetryResult> PayTruckerLegAsync(
        Dictionary<string, object> escrow, string lockId, string truckerPhone, decimal amount, SettlementProgress progress)
    {
        var escrowId = GetString(escrow, "escrowId");
        var createdAt = GetString(escrow, "createdAt");
        var response = await CallReleaseAsync(new ReleaseFundsRequest
        {
            TransactionRef = TruckerReleaseTransactionRef(escrowId),
            HoldRef = GetStringOrNull(escrow, "telebirrHoldRef") ?? "",
            FarmerPhone = truckerPhone,
            Amount = amount,
            PlatformCommission = 0m
        });

        var manualReason = ManualPayoutReason(response.Success, response.ErrorCode, response.TelebirrRef);
        if (manualReason == null && IsOutcomeUnknownAnswer(response.Success, response.ErrorCode))
        {
            await AppendFailureAuditBestEffortAsync(escrowId, "trucker_release_failed", "released", SystemActor, response.ErrorCode);
            manualReason = ManualPayoutFields.ReasonGatewayOutcomeUnknown;
        }

        if (manualReason != null)
        {
            return await QueueTruckerLegAsync(escrow, lockId, manualReason, progress);
        }

        if (!response.Success)
        {
            // A definite refusal: nothing moved. The lock goes first, so a failed audit write
            // cannot strand it; the next run retries with the same reference.
            await _escrowRepo.ReleaseTruckerPayoutLockAsync(escrowId, createdAt, lockId);
            progress.Locked = false;
            await AppendAuditAsync(escrowId, "trucker_release_failed", "released", "released", SystemActor,
                metadata: response.ErrorCode);
            return new(TruckerRetryOutcome.Failed, "gateway_refused");
        }

        var truckerRef = response.TelebirrRef!;
        var paidAt = DateTime.UtcNow.ToString("o");
        var completedAt = AddTicks(paidAt, 1);
        await WriteAfterPayoutAsync(
            escrowId,
            async () =>
            {
                if (!await _escrowRepo.TryRecordTruckerPayoutAsync(escrowId, createdAt, lockId, truckerRef, paidAt, completedAt))
                {
                    throw new ConditionalCheckFailedException("The trucker leg's lock or status changed.");
                }
            },
            () => HasRecordedTruckerPayoutAsync(escrowId, truckerRef),
            $"op=release leg=trucker truckerRef={MoneyAlarmLog.LogId(truckerRef)}");
        progress.Recorded = true;

        // O1: deterministic keys, so these rows never duplicate (the complete row at completedAt,
        // the key every completing writer uses).
        await EnsureAuditAsync(escrowId, paidAt, "trucker_release", "released", "released", SystemActor,
            truckerRef, "source=retry");
        await EnsureAuditAsync(escrowId, completedAt, "complete", "released", "completed", SystemActor,
            metadata: "trucker_retry");
        Console.WriteLine($"ESCROW_TRUCKER_PAYOUT_PAID escrowId={MoneyAlarmLog.LogId(escrowId)} source=retry");
        // ESC-09: the last leg is paid, so the order is settled.
        await FollowMoneyAsync(escrowId, "trucker_retry");
        return new(TruckerRetryOutcome.Paid, null);
    }

    /// <summary>
    /// The gateway cannot pay the trucker out, paid without a reference, or did not say: the leg
    /// goes to the manual payout queue with the farmer leg already paid, and the trucker lock stays.
    /// If the queue write never lands, a <c>PAYOUT_NOT_SUPPORTED</c> leg (nothing moved) releases the
    /// lock for the next run; any other reason keeps it, so the recovery parks it once stale.
    /// </summary>
    private async Task<TruckerRetryResult> QueueTruckerLegAsync(
        Dictionary<string, object> escrow, string lockId, string reason, SettlementProgress progress)
    {
        var escrowId = GetString(escrow, "escrowId");
        var createdAt = GetString(escrow, "createdAt");
        var legs = TruckerQueueLegs(escrow);
        if (await QueueManualPayoutAsync(escrowId, createdAt, ManualPayoutFields.OpRelease, reason, "released", legs,
                SystemActor, MaxSettlementWriteAttempts,
                queuedAt => _escrowRepo.TryQueueTruckerLegAsync(escrowId, createdAt, reason, legs, queuedAt, lockId, parkId: null)))
        {
            return new(TruckerRetryOutcome.Queued, reason);
        }

        if (reason == ManualPayoutFields.ReasonPayoutNotSupported)
        {
            await _escrowRepo.ReleaseTruckerPayoutLockAsync(escrowId, createdAt, lockId);
            progress.Locked = false;
            await AppendAuditAsync(escrowId, "trucker_release_failed", "released", "released", SystemActor,
                metadata: QueueFailedNotSupportedCode);
            return new(TruckerRetryOutcome.Failed, "queue_failed");
        }

        await AppendFailureAuditBestEffortAsync(escrowId, "trucker_release_failed", "released", SystemActor,
            ManualPayoutFields.QueueFailedCodePrefix + reason);
        return new(TruckerRetryOutcome.Failed, "queue_failed");
    }

    /// <summary>
    /// A trucker lock older than 15 minutes on a released, pending, unqueued row has no living owner
    /// and its gateway call may have paid: park the leg as <c>GATEWAY_OUTCOME_UNKNOWN</c>, fenced on
    /// the same lock, like the ESC-05 recovery step. Never calls the gateway, never releases the lock.
    /// </summary>
    private async Task<TruckerRetryResult> ParkStaleTruckerLegAsync(
        Dictionary<string, object> escrow, string lockId, string? lockAt, DateTime now)
    {
        var escrowId = GetString(escrow, "escrowId");
        var createdAt = GetString(escrow, "createdAt");
        var legs = TruckerQueueLegs(escrow);
        var queuedAt = DateTime.UtcNow.ToString("o");
        var parkId = Guid.NewGuid().ToString("N");
        bool queued;
        Exception? writeError = null;
        try
        {
            queued = await _escrowRepo.TryQueueTruckerLegAsync(
                escrowId, createdAt, ManualPayoutFields.ReasonGatewayOutcomeUnknown, legs, queuedAt, lockId, parkId);
        }
        catch (Exception ex)
        {
            writeError = ex;
            queued = false;
        }

        if (!queued)
        {
            // Ours only if the row carries this very write (its park id) on the same lock.
            var latest = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
            if (latest == null
                || !IsManualPayoutOpen(latest)
                || GetStringOrNull(latest, TruckerPayoutFields.LockId) != lockId
                || GetStringOrNull(latest, ManualPayoutFields.ParkId) != parkId)
            {
                if (writeError != null && latest != null && !IsManualPayoutOpen(latest)
                    && GetStringOrNull(latest, TruckerPayoutFields.LockId) == lockId)
                {
                    AlarmLog(MoneyAlarmLog.SettlementFailed(
                        escrowId, "stale_lock_recovery_failed", MoneyAlarmLog.OutcomeUnknown, writeError.GetType().Name));
                    return new(TruckerRetryOutcome.Failed, "stale_lock");
                }

                return new(TruckerRetryOutcome.Skipped, null);
            }
        }

        var ageMinutes = DateTime.TryParse(lockAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var locked)
            ? (int)(now.ToUniversalTime() - locked.ToUniversalTime()).TotalMinutes
            : -1;
        AlarmLog(MoneyAlarmLog.SettlementFailed(escrowId, "stale_lock_parked", MoneyAlarmLog.OutcomeUnknown, "release:trucker"));
        Console.WriteLine(
            $"ESCROW_MANUAL_PAYOUT_QUEUED escrowId={MoneyAlarmLog.LogId(escrowId)} op=release legs=farmer,trucker reason={ManualPayoutFields.ReasonGatewayOutcomeUnknown}");
        await EnsureAuditAsync(escrowId, AddTicks(queuedAt, -1), "stale_lock_parked", "released", "released", SystemActor,
            metadata: $"op=release;leg=trucker;lockedAt={lockAt ?? "-"};ageMinutes={ageMinutes}");
        await EnsureAuditAsync(escrowId, queuedAt, "manual_payout_queued", "released", "released", SystemActor,
            metadata: $"op=release;legs=farmer,trucker;reason={ManualPayoutFields.ReasonGatewayOutcomeUnknown}");
        return new(TruckerRetryOutcome.Queued, "stale_lock");
    }

    /// <summary>
    /// ESC-04 legacy, one row from <see cref="IEscrowRepository.QueryLegacyTruckerLegsAsync"/>: a
    /// <c>completed</c> escrow whose trucker leg was never paid (before ESC-04 a release completed
    /// without it). See <see cref="ParkLegacyAsync"/>. Returns true when this call parked it.
    /// </summary>
    public async Task<bool> ParkLegacyTruckerLegAsync(Dictionary<string, object> row)
    {
        var escrow = await _escrowRepo.GetByIdAsync(GetString(row, "escrowId"), consistentRead: true);
        return escrow != null && await ParkLegacyAsync(escrow, "completed");
    }

    /// <summary>
    /// ESC-04 legacy: a trucker leg left pending by the code before ESC-04 (no pending time),
    /// <c>completed</c> or (a crash between the two writes) <c>released</c>. Under the old runbook it
    /// may have been paid by hand, so it is never retried: the escrow becomes <c>released</c> and the
    /// leg goes into the manual payout queue as <c>GATEWAY_OUTCOME_UNKNOWN</c> (the admin checks the
    /// portal first), in one conditional write. Returns true when this call parked it. Never calls
    /// the gateway.
    /// </summary>
    private async Task<bool> ParkLegacyAsync(Dictionary<string, object> escrow, string status)
    {
        var escrowId = GetString(escrow, "escrowId");
        if (GetString(escrow, "status") != status || !GetBool(escrow, TruckerPayoutFields.Pending)
            || IsManualPayoutOpen(escrow) || GetStringOrNull(escrow, TruckerPayoutFields.PendingSince) != null)
        {
            return false;
        }

        var createdAt = GetString(escrow, "createdAt");
        var (truckerId, fromOrder, _) = await ResolveTruckerIdAsync(escrow);
        if (truckerId != null && fromOrder)
        {
            await BackfillTruckerIdAsync(escrow, truckerId, SystemActor);
        }

        var legs = TruckerQueueLegs(escrow);
        var queuedAt = DateTime.UtcNow.ToString("o");
        var completedAt = GetStringOrNull(escrow, "completedAt");
        var pendingSince = completedAt ?? GetStringOrNull(escrow, "releasedAt") ?? queuedAt;
        bool reopened;
        try
        {
            reopened = await _escrowRepo.TryParkLegacyTruckerLegAsync(escrowId, createdAt, status, legs, queuedAt, pendingSince);
        }
        catch (Exception)
        {
            // The write may have landed although the answer was lost: the row decides.
            if (!await IsQueuedAtAsync(escrowId, queuedAt))
            {
                throw;
            }

            reopened = true;
        }

        // A failed condition is ours too when the SDK retried our own committed write.
        if (!reopened && !await IsQueuedAtAsync(escrowId, queuedAt))
        {
            return false;
        }

        AlarmLog(MoneyAlarmLog.SettlementFailed(escrowId, "legacy_trucker_parked", MoneyAlarmLog.OutcomeUnknown, "release:trucker"));
        Console.WriteLine(
            $"ESCROW_MANUAL_PAYOUT_QUEUED escrowId={MoneyAlarmLog.LogId(escrowId)} op=release legs=farmer,trucker reason={ManualPayoutFields.ReasonGatewayOutcomeUnknown}");
        await EnsureAuditAsync(escrowId, AddTicks(queuedAt, -1), "legacy_trucker_parked", status, "released", SystemActor,
            metadata: $"legacy=true;fromStatus={status};completedAt={completedAt ?? "-"}");
        await EnsureAuditAsync(escrowId, queuedAt, "manual_payout_queued", "released", "released", SystemActor,
            metadata: $"op=release;legs=farmer,trucker;reason={ManualPayoutFields.ReasonGatewayOutcomeUnknown}");
        await FollowMoneyAsync(escrowId, "legacy_park");
        return true;
    }

    /// <summary>Whether the row is queued under exactly this <paramref name="queuedAt"/> (our write). A failed read counts as no.</summary>
    private async Task<bool> IsQueuedAtAsync(string escrowId, string queuedAt)
    {
        try
        {
            var latest = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
            return latest != null && IsManualPayoutOpen(latest)
                   && GetStringOrNull(latest, ManualPayoutFields.QueuedAt) == queuedAt;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The legs of a trucker leg queued on a released escrow: the farmer paid by the release, the trucker pending.</summary>
    private static List<ManualPayoutLegSeed> TruckerQueueLegs(Dictionary<string, object> escrow) => new()
    {
        new(ManualPayoutFields.LegFarmer, ManualPayoutFields.LegPaid, GetStringOrNull(escrow, "telebirrReleaseRef"),
            ManualPayoutFields.PaidByGateway, GetStringOrNull(escrow, "releasedAt") ?? GetStringOrNull(escrow, "updatedAt")),
        new(ManualPayoutFields.LegTrucker, ManualPayoutFields.LegPending, null, null, null)
    };

    /// <summary>A queue entry for the trucker leg of a released escrow (ESC-04): it closes to completed.</summary>
    private static bool IsReleasedTruckerQueue(Dictionary<string, object> escrow)
        => GetString(escrow, "status") == "released"
           && GetStringOrNull(escrow, ManualPayoutFields.FromStatus) == "released"
           && GetStringOrNull(escrow, ManualPayoutFields.Op) == ManualPayoutFields.OpRelease;

    /// <summary>
    /// R10: the reason is stored as a compare-and-set, so it is audited once per value however many
    /// runs (or concurrent ones) see it. The audit row sits at the time stored with the reason, so a
    /// row whose write failed after the reason won is re-ensured by the next run that sees the same
    /// reason (a conditional put that writes nothing when the row is there).
    /// </summary>
    private async Task NoteTruckerBlockedAsync(Dictionary<string, object> escrow, string reason)
    {
        var escrowId = GetString(escrow, "escrowId");
        var reasonAt = DateTime.UtcNow.ToString("o");
        if (!await _escrowRepo.TrySetTruckerBlockReasonAsync(escrowId, GetString(escrow, "createdAt"), reason, reasonAt))
        {
            var storedAt = GetStringOrNull(escrow, TruckerPayoutFields.BlockReasonAt);
            if (GetStringOrNull(escrow, TruckerPayoutFields.BlockReason) == reason && storedAt != null)
            {
                await EnsureAuditAsync(escrowId, storedAt, TruckerBlockedAuditAction, "released", "released", SystemActor,
                    metadata: PayoutBlockedMetadata(reason));
            }

            return;
        }

        await EnsureAuditAsync(escrowId, reasonAt, TruckerBlockedAuditAction, "released", "released", SystemActor,
            metadata: PayoutBlockedMetadata(reason));
        Console.WriteLine($"ESCROW_TRUCKER_PAYOUT_BLOCKED escrowId={MoneyAlarmLog.LogId(escrowId)} reason={reason}");
    }

    /// <summary>
    /// The trucker an escrow pays: its own truckerId, or (an escrow minted before its order had a
    /// truck, ESC-04 owns that backfill) the order's, which must match the delivery's immutable
    /// truckerId when the escrow is linked to a delivery (O3). Reads only; the caller keeps a
    /// truckerId taken from the order (<c>FromOrder</c>) with <see cref="BackfillTruckerIdAsync"/>.
    /// </summary>
    private async Task<(string? TruckerId, bool FromOrder, string? BlockReason)> ResolveTruckerIdAsync(
        Dictionary<string, object> escrow)
    {
        var own = GetStringOrNull(escrow, "truckerId");
        if (!string.IsNullOrEmpty(own))
        {
            return (own, false, null);
        }

        var orderId = GetStringOrNull(escrow, "orderId");
        var order = _orderRepo != null && !string.IsNullOrEmpty(orderId) ? await _orderRepo.GetByIdAsync(orderId) : null;
        var orderTruckerId = order != null ? GetStringOrNull(order, "truckerId") : null;
        if (string.IsNullOrEmpty(orderTruckerId))
        {
            return (null, false, TruckerPayoutFields.BlockNoTrucker);
        }

        var deliveryId = GetStringOrNull(escrow, "deliveryId");
        if (!string.IsNullOrEmpty(deliveryId))
        {
            // Fail closed: a delivery that cannot be read or names another trucker blocks the leg.
            var delivery = _deliveryRepo != null ? await _deliveryRepo.GetByIdAsync(deliveryId) : null;
            var deliveryTruckerId = delivery != null ? GetStringOrNull(delivery, "truckerId") : null;
            if (!string.Equals(deliveryTruckerId, orderTruckerId, StringComparison.Ordinal))
            {
                return (null, false, TruckerPayoutFields.BlockTruckerMismatch);
            }
        }

        return (orderTruckerId, true, null);
    }

    /// <summary>Keep a truckerId taken from the order on the escrow, only when it has none. Returns false when one landed first.</summary>
    private async Task<bool> BackfillTruckerIdAsync(Dictionary<string, object> escrow, string truckerId, string actor)
    {
        var escrowId = GetString(escrow, "escrowId");
        if (!await _escrowRepo.TryBackfillTruckerIdAsync(escrowId, GetString(escrow, "createdAt"), truckerId))
        {
            return false;
        }

        // Best effort: the row already carries the truckerId, and a later run never writes it again.
        try
        {
            var status = GetString(escrow, "status");
            await AppendAuditAsync(escrowId, "trucker_backfilled", status, status, actor,
                metadata: $"source=order;truckerId={truckerId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ESCROW_TRUCKER_BACKFILL_AUDIT_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} error={ex.GetType().Name}");
        }

        return true;
    }

    /// <summary>The refusal text for a trucker leg without a payable trucker.</summary>
    private static string TruckerBlockMessage(string? reason) => reason switch
    {
        TruckerPayoutFields.BlockTruckerMismatch => "Payout blocked: the order's trucker does not match the delivery's trucker.",
        TruckerPayoutFields.BlockNoTrucker => "Payout blocked: this escrow and its order name no trucker.",
        _ => "Payout blocked: trucker profile not found."
    };

    /// <summary>Whether the trucker leg is recorded with <paramref name="truckerRef"/> (a write whose answer was lost). A failed read counts as no.</summary>
    private async Task<bool> HasRecordedTruckerPayoutAsync(string escrowId, string truckerRef)
    {
        try
        {
            var latest = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
            return latest != null && GetString(latest, "status") == "completed"
                   && GetStringOrNull(latest, TruckerPayoutFields.Ref) == truckerRef;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ──────────────────────────────────────────────
    // The order follows the money (ESC-09)
    // ──────────────────────────────────────────────

    /// <summary>
    /// ESC-09: an order whose money moved more than this long ago and that still cannot be written
    /// alarms (<c>write_failed</c>): the inline call and at least one earlier sweep had their chance.
    /// </summary>
    internal static readonly TimeSpan OrderMoneyWriteFailedAlarmAfter = TimeSpan.FromHours(2);

    /// <summary>
    /// ESC-09: after a money write, move the escrow's order to where the money says
    /// (<see cref="OrderMoneyMirror"/>), from a strongly consistent read of the escrow. Best-effort and
    /// never throws: the settlement's answer never depends on it. A follow that did not get there
    /// logs <c>ORDER_MONEY_FOLLOW_DEFERRED</c> (not an alarm token); the hourly order-money sweep
    /// repairs it and alarms when it cannot.
    /// </summary>
    internal async Task<OrderMoneyResult?> FollowMoneyAsync(string escrowId, string source, ILambdaContext? context = null)
    {
        if (_orderRepo == null)
        {
            return null;
        }

        try
        {
            var escrow = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
            if (escrow == null)
            {
                return null;
            }

            var result = await _orderMoneyMirror.FollowAsync(escrow, source, context: context);
            if (result.Outcome is OrderMoneyOutcome.Failed or OrderMoneyOutcome.Mismatch)
            {
                Console.WriteLine(
                    $"ORDER_MONEY_FOLLOW_DEFERRED escrowId={MoneyAlarmLog.LogId(escrowId)} orderId={MoneyAlarmLog.LogId(result.OrderId)} "
                    + $"source={source} reason={MoneyAlarmLog.LogText(result.Reason)}");
            }

            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"ORDER_MONEY_FOLLOW_DEFERRED escrowId={MoneyAlarmLog.LogId(escrowId)} source={source} reason=[{ex.GetType().Name}]");
            return null;
        }
    }

    /// <summary>
    /// ESC-09, one order from the hourly order-money sweep (<c>EscrowCronFunction</c>): find the escrow
    /// that holds or held its money and move the order where that money says.
    /// <list type="bullet">
    /// <item>Moved, or still waiting for its money (held, in a dispute, a verdict not yet settled): no mark.</item>
    /// <item>No escrow at all, a flagged override or another consistent end: the order is marked as decided
    /// at its status (<see cref="OrderMoneyFields.SweepSkip"/>), so later runs skip it until it moves.</item>
    /// <item>Mismatch (the order cannot follow money that moved, or is ahead of money that did not): an
    /// escrow-audit row at a deterministic key decides "first time"; only then the
    /// <c>ORDER_MONEY_MISMATCH</c> line and a transaction-audit row; then the mark.</item>
    /// <item>Failed: the alarm line (<c>write_failed</c>) once the money moved more than
    /// <see cref="OrderMoneyWriteFailedAlarmAfter"/> ago, an info line before; no mark.</item>
    /// </list>
    /// </summary>
    internal async Task<OrderMoneySweepOutcome> ReconcileOrderMoneyAsync(
        Dictionary<string, object> order, DateTime now, ILambdaContext? context = null)
    {
        var orderId = GetString(order, "orderId");
        var indexStatus = GetString(order, "status");
        var escrow = await FindMoneyEscrowForOrderAsync(orderId, GetStringOrNull(order, "activeDeliveryId"));
        if (escrow == null)
        {
            // An order still before delivery may get its escrow later (linkage self-heals a record the
            // funding could not write), so only an order past delivery is marked.
            if (!LogisticsOrderStatuses.Contains(indexStatus))
            {
                await MarkMoneySweepSkipAsync(orderId, indexStatus, now);
            }

            return OrderMoneySweepOutcome.NoEscrow;
        }

        var escrowId = GetString(escrow, "escrowId");
        var result = await _orderMoneyMirror.FollowAsync(escrow, "sweep", indexStatus, context);
        if (result.Outcome == OrderMoneyOutcome.Mismatch
            && await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true) is { } fresh)
        {
            // The escrow was read before the order: a payout closed in between (and its own follow moved
            // the order) would look like an order ahead of the money. Decide again on a fresh read.
            escrow = fresh;
            result = await _orderMoneyMirror.FollowAsync(escrow, "sweep", indexStatus, context);
        }

        var escrowStatus = GetString(escrow, "status");
        switch (result.Outcome)
        {
            case OrderMoneyOutcome.Moved:
                return result.OrderStatus == "settled" ? OrderMoneySweepOutcome.Settled : OrderMoneySweepOutcome.Moved;

            case OrderMoneyOutcome.Consistent:
                await MarkMoneySweepSkipAsync(orderId, result.OrderStatus ?? indexStatus, now);
                return OrderMoneySweepOutcome.Consistent;

            case OrderMoneyOutcome.Mismatch:
                await ReportOrderMoneyMismatchOnceAsync(escrow, orderId, result, context);
                await MarkMoneySweepSkipAsync(orderId, result.OrderStatus ?? indexStatus, now);
                return OrderMoneySweepOutcome.Mismatch;

            case OrderMoneyOutcome.Failed:
                var movedAt = ParseUtcTimeOrNull(GetStringOrNull(escrow, "completedAt"))
                              ?? ParseUtcTimeOrNull(GetStringOrNull(escrow, "releasedAt"))
                              ?? ParseUtcTimeOrNull(GetStringOrNull(escrow, "refundedAt"))
                              ?? ParseUtcTimeOrNull(GetStringOrNull(escrow, ManualPayoutFields.QueuedAt))
                              ?? ParseUtcTimeOrNull(GetStringOrNull(escrow, "updatedAt"));
                if (movedAt == null || now - movedAt.Value >= OrderMoneyWriteFailedAlarmAfter)
                {
                    AlarmLog(MoneyAlarmLog.OrderMoneyMismatch(
                        orderId, escrowId, result.OrderStatus ?? indexStatus, escrowStatus, "write_failed"));
                }
                else
                {
                    Console.WriteLine(
                        $"ORDER_MONEY_FOLLOW_DEFERRED escrowId={MoneyAlarmLog.LogId(escrowId)} orderId={MoneyAlarmLog.LogId(orderId)} "
                        + $"source=sweep reason={MoneyAlarmLog.LogText(result.Reason)}");
                }

                return OrderMoneySweepOutcome.Failed;

            default:
                return OrderMoneySweepOutcome.Waiting;
        }
    }

    /// <summary>
    /// The escrow that holds or held the order's money, strongly consistent: the order escrow
    /// (<c>escrow-order-{orderId}</c>), and when it is missing or inert, any other escrow the order index
    /// or the active delivery names (a legacy <c>/escrow/initiate</c> escrow). The one furthest along wins.
    /// </summary>
    private async Task<Dictionary<string, object>?> FindMoneyEscrowForOrderAsync(string orderId, string? activeDeliveryId)
    {
        var orderEscrowId = $"escrow-order-{orderId}";
        var candidates = new List<Dictionary<string, object>>();
        var own = await _escrowRepo.GetByIdAsync(orderEscrowId, consistentRead: true);
        if (own != null)
        {
            candidates.Add(own);
        }

        if (own == null || GetString(own, "status") is "refunded" or "cancelled")
        {
            var ids = new List<string>();
            foreach (var row in await _escrowRepo.QueryByOrderIdAsync(orderId) ?? new List<Dictionary<string, object>>())
            {
                var id = GetString(row, "escrowId");
                if (id.Length > 0 && id != orderEscrowId && !ids.Contains(id))
                {
                    ids.Add(id);
                }
            }

            if (ids.Count == 0 && own == null && !string.IsNullOrEmpty(activeDeliveryId))
            {
                var byDelivery = await _escrowRepo.GetByDeliveryIdAsync(activeDeliveryId);
                var id = byDelivery != null ? GetString(byDelivery, "escrowId") : "";
                if (id.Length > 0)
                {
                    ids.Add(id);
                }
            }

            foreach (var id in ids)
            {
                if (await _escrowRepo.GetByIdAsync(id, consistentRead: true) is { } current)
                {
                    candidates.Add(current);
                }
            }
        }

        return candidates.OrderByDescending(MoneyProgress).FirstOrDefault();
    }

    /// <summary>How far an escrow's money got, to pick between an order's escrows.</summary>
    private static int MoneyProgress(Dictionary<string, object> escrow) => GetString(escrow, "status") switch
    {
        "completed" => 6,
        "released" => 5,
        _ when IsManualPayoutOpen(escrow) && GetStringOrNull(escrow, ManualPayoutFields.Op) == ManualPayoutFields.OpRelease => 4,
        "refunded" => 3,
        "dispute_resolved" => 2,
        "escrowed" or "disputed" or "held" => 1,
        _ => 0
    };

    /// <summary>
    /// Once per escrow (an escrow-audit row at its <c>createdAt</c> plus 7 ticks, or 8 for an order
    /// ahead of the money; see the tick registry on <see cref="OrderMoneyMirror"/>): the alarm line and a
    /// transaction-audit row on the order. A failed audit write still alarms (fail loud).
    /// </summary>
    private async Task ReportOrderMoneyMismatchOnceAsync(
        Dictionary<string, object> escrow, string orderId, OrderMoneyResult result, ILambdaContext? context)
    {
        var escrowId = GetString(escrow, "escrowId");
        var escrowStatus = GetString(escrow, "status");
        var ahead = result.Reason == "order_ahead";
        var action = ahead ? OrderMoneyMirror.AheadAuditAction : OrderMoneyMirror.MismatchAuditAction;
        var reason = ahead ? "order_ahead" : "cannot_follow";
        var orderStatus = result.OrderStatus ?? "";
        bool first;
        try
        {
            first = await _auditRepo.AppendIfAbsentAsync(BuildAuditEntry(
                EscrowAuditRepository.DeterministicAuditId(action, OrderMoneyAuditTimestamp(escrow, ahead)),
                OrderMoneyAuditTimestamp(escrow, ahead), escrowId, action, escrowStatus, escrowStatus, SystemActor,
                null, $"orderId={orderId};orderStatus={orderStatus};reason={result.Reason}"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ORDER_MONEY_AUDIT_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} error={ex.GetType().Name}");
            first = true;
        }

        if (!first)
        {
            return;
        }

        AlarmLog(MoneyAlarmLog.OrderMoneyMismatch(orderId, escrowId, orderStatus, escrowStatus, reason));
        await TransactionAuditWriter.EmitAsync(_transactionAuditRepo, context, orderId, "order", orderId,
            action, orderStatus, orderStatus, AuditContext.ForSystem(),
            metadata: $"escrowId={escrowId};escrowStatus={escrowStatus};reason={result.Reason}");
    }

    /// <summary>The escrow's <c>createdAt</c> plus 7 ticks (mismatch) or 8 (ahead); the start of today when it cannot be read.</summary>
    internal static string OrderMoneyAuditTimestamp(Dictionary<string, object> escrow, bool ahead)
    {
        var at = ParseUtcTimeOrNull(GetStringOrNull(escrow, "createdAt")) ?? DateTime.UtcNow.Date;
        return at.AddTicks(ahead ? 8 : 7).ToString("o");
    }

    private async Task MarkMoneySweepSkipAsync(string orderId, string status, DateTime now)
    {
        try
        {
            await _orderRepo!.TryMarkMoneySweepSkipAsync(orderId, status, now.ToString("o"));
        }
        catch (Exception ex)
        {
            // The next run decides again (the mark only saves reads).
            Console.WriteLine($"ORDER_MONEY_SKIP_MARK_FAILED orderId={MoneyAlarmLog.LogId(orderId)} error={ex.GetType().Name}");
        }
    }

    /// <summary>
    /// ESC-09: order statuses whose escrow may be released (shared with ESC-10's auto-release trade
    /// check). <c>accepted</c>, <c>pickup_confirmed</c> and <c>in_transit</c> only behind a delivered
    /// delivery (the order lags it); <c>disbursing</c> while a release waits in the manual payout queue.
    /// </summary>
    internal static readonly HashSet<string> ReleasableOrderStatuses = new(StringComparer.Ordinal)
    {
        "accepted", "pickup_confirmed", "in_transit", "delivered", "disbursing"
    };

    /// <summary>
    /// ESC-09 Part B: before an admin (not the system sweep) releases a plain <c>escrowed</c> escrow, its
    /// order must be <c>delivered</c> or <c>disbursing</c>; an order at <c>accepted</c>,
    /// <c>pickup_confirmed</c> or <c>in_transit</c> behind a delivered delivery is advanced to
    /// <c>delivered</c> first. Anything else is refused (422, no override): the farmer is never paid for
    /// goods the order does not show delivered. A read or a lost race refuses with 503 (nothing moved).
    /// No order behind the escrow: no check.
    /// </summary>
    private async Task<SettlementBlock?> CheckAdminReleaseOrderAsync(Dictionary<string, object> escrow, string actor)
    {
        if (_orderRepo == null)
        {
            return null;
        }

        var escrowId = GetString(escrow, "escrowId");
        try
        {
            var orderId = await ResolveEscrowOrderIdAsync(escrow);
            if (string.IsNullOrEmpty(orderId))
            {
                return null;
            }

            var order = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
            if (order == null)
            {
                return await RefuseAdminSettlementAsync(escrowId, actor, "release", orderId, "missing", overridable: false);
            }

            var status = GetString(order, "status");
            if (status is "delivered" or "disbursing")
            {
                return null;
            }

            if (ReleasableOrderStatuses.Contains(status) && await ActiveDeliveryStatusAsync(order, escrow) == "delivered")
            {
                if (await _orderRepo.TryAdvanceStatusAsync(orderId, status, "delivered") != null)
                {
                    await TransactionAuditWriter.EmitAsync(_transactionAuditRepo, null, orderId, "order", orderId,
                        "order_updated", status, "delivered", AuditContext.ForSystem(),
                        metadata: $"escrowId={escrowId};source=admin_release");
                    return null;
                }

                var latest = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
                return latest != null && GetString(latest, "status") is "delivered" or "disbursing"
                    ? null
                    : new SettlementBlock(503, "ESCROW_012", "The order changed while the release was checked. Nothing moved; try again.");
            }

            return await RefuseAdminSettlementAsync(escrowId, actor, "release", orderId, status, overridable: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ESCROW_ADMIN_SETTLEMENT_ORDER_UNREAD escrowId={MoneyAlarmLog.LogId(escrowId)} op=release error={ex.GetType().Name}");
            return new SettlementBlock(503, "ESCROW_012", "The order behind this escrow could not be read. Nothing moved; try again.");
        }
    }

    /// <summary>
    /// ESC-09 Part B: before an admin (not the system sweep) refunds a plain <c>escrowed</c> escrow:
    /// <list type="bullet">
    /// <item><c>rejected</c>: refund as is. <c>cancelled</c>: as is, after the stock is given back first
    /// when the cancel was the refund's own or a transport failure (fail closed, 503).</item>
    /// <item><c>funded</c>, or <c>accepted</c> with no delivery or a pending one: cancelled first through the
    /// ESC-03 cancel-before-pickup path (pending delivery, order <c>cancelReason: escrow_refunded</c>, slot,
    /// stock), and the refund scheduled on the escrow (<c>expiresAt = now</c>, <c>refundReason:
    /// admin_refund</c>) so a gateway refusal leaves it armed for the hourly sweep. A cancel or restock
    /// that does not land refuses with 503.</item>
    /// <item>goods moving (<c>pickup_confirmed</c>, <c>in_transit</c>, <c>accepted</c> with a picked-up
    /// delivery) or <c>delivered</c>: 422 unless <c>overrideOrderMismatch</c> (admin group, a reason):
    /// then refunded, the order flagged (<c>refundOverride*</c>) and its status left.</item>
    /// <item>anything else (<c>disputed</c>, <c>disbursing</c>, <c>settled</c>, <c>dispute_resolved</c>,
    /// a payment status, a missing order): 422, no override.</item>
    /// </list>
    /// </summary>
    private async Task<SettlementBlock?> PrepareAdminRefundOrderAsync(
        Dictionary<string, object> escrow, string actor, string? overrideReason)
    {
        if (_orderRepo == null)
        {
            return null;
        }

        var escrowId = GetString(escrow, "escrowId");
        string? orderId;
        Dictionary<string, object>? order;
        try
        {
            orderId = await ResolveEscrowOrderIdAsync(escrow);
            if (string.IsNullOrEmpty(orderId))
            {
                return null;
            }

            order = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ESCROW_ADMIN_SETTLEMENT_ORDER_UNREAD escrowId={MoneyAlarmLog.LogId(escrowId)} op=refund error={ex.GetType().Name}");
            return new SettlementBlock(503, "ESCROW_012", "The order behind this escrow could not be read. Nothing moved; try again.");
        }

        if (order == null)
        {
            return await RefuseAdminSettlementAsync(escrowId, actor, "refund", orderId, "missing", overridable: false);
        }

        var status = GetString(order, "status");
        switch (status)
        {
            case "rejected":
                return null;

            case "cancelled":
                return await EnsureRefundRestockAsync(order, escrowId);

            case "funded" or "accepted":
                var cancel = await _verdictMirror.CancelBeforeRefundAsync(escrow);
                if (cancel.Order is VerdictLegOutcome.Cancelled or VerdictLegOutcome.Closed)
                {
                    await ScheduleAdminRefundAsync(escrow, actor);
                    return null;
                }

                if (cancel.Order is VerdictLegOutcome.Blocked or VerdictLegOutcome.Unknown
                    || cancel.Delivery == VerdictLegOutcome.Unknown)
                {
                    return new SettlementBlock(503, "ESCROW_012",
                        $"The order could not be cancelled or restocked before the refund ({cancel.FailedLegs}). Nothing moved; try again.");
                }

                // The goods left the farm (or the order moved on) since it was read: decide on the latest status.
                var latestStatus = cancel.OrderItem != null ? GetString(cancel.OrderItem, "status") : "missing";
                return latestStatus switch
                {
                    "rejected" => null,
                    "cancelled" => await EnsureRefundRestockAsync(cancel.OrderItem!, escrowId),
                    "accepted" or "pickup_confirmed" or "in_transit" or "delivered"
                        => await GoodsMovingRefundAsync(escrowId, actor, orderId, latestStatus, overrideReason),
                    _ => await RefuseAdminSettlementAsync(escrowId, actor, "refund", orderId, latestStatus, overridable: false)
                };

            case "pickup_confirmed" or "in_transit" or "delivered":
                return await GoodsMovingRefundAsync(escrowId, actor, orderId, status, overrideReason);

            default:
                return await RefuseAdminSettlementAsync(escrowId, actor, "refund", orderId, status, overridable: false);
        }
    }

    /// <summary>A cancelled order whose own cancel promised the stock back gets it back before the refund (fail closed).</summary>
    private async Task<SettlementBlock?> EnsureRefundRestockAsync(Dictionary<string, object> order, string escrowId)
    {
        if (GetStringOrNull(order, "cancelReason") is not ("escrow_refunded" or PickupDeadline.CancelReason))
        {
            return null;
        }

        var restock = await _verdictMirror.EnsureStockRestoredAsync(order);
        return restock == null
            ? null
            : new SettlementBlock(503, "ESCROW_012",
                $"The cancelled order's stock could not be given back first ({restock}). Nothing was refunded; try again.");
    }

    /// <summary>The goods are moving: refused unless the admin overrides with a reason (audited before the money).</summary>
    private async Task<SettlementBlock?> GoodsMovingRefundAsync(
        string escrowId, string actor, string orderId, string orderStatus, string? overrideReason)
    {
        if (overrideReason == null)
        {
            return await RefuseAdminSettlementAsync(escrowId, actor, "refund", orderId, orderStatus, overridable: true);
        }

        var metadata = $"orderId={orderId};orderStatus={orderStatus};reason={overrideReason}";
        try
        {
            await AppendAuditAsync(escrowId, "refund_order_mismatch_overridden", "escrowed", "escrowed", actor, metadata: metadata);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ESCROW_REFUND_OVERRIDE_AUDIT_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} reason=[{ex.GetType().Name}]");
            return new SettlementBlock(503, "ESCROW_012", "The override could not be recorded. Nothing was refunded; try again.");
        }

        Console.WriteLine(
            $"ESCROW_REFUND_ORDER_MISMATCH_OVERRIDDEN escrowId={MoneyAlarmLog.LogId(escrowId)} admin={MoneyAlarmLog.LogId(actor)} orderStatus={orderStatus}");
        await TransactionAuditWriter.EmitAsync(_transactionAuditRepo, null, orderId, "escrow", escrowId,
            "refund_mismatch_override", "escrowed", "escrowed",
            _auditContext ?? new AuditContext { Actor = actor, ActorRole = "admin" }, metadata: metadata);

        if (!await _verdictMirror.FlagOrderAsync(orderId, new Dictionary<string, object>
            {
                { OrderMoneyFields.RefundOverrideAt, DateTime.UtcNow.ToString("o") },
                { OrderMoneyFields.RefundOverrideBy, actor },
                { OrderMoneyFields.RefundOverrideReason, overrideReason },
                { OrderMoneyFields.RefundOverrideEscrowId, escrowId }
            }))
        {
            Console.WriteLine(
                $"ESCROW_REFUND_OVERRIDE_FLAG_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} orderId={MoneyAlarmLog.LogId(orderId)}");
        }

        return null;
    }

    /// <summary>
    /// The refund is armed on the escrow before the lock (as <see cref="ScheduleRefundForOrderAsync"/> does),
    /// so a gateway refusal leaves it for the hourly sweep. Best-effort: the order is already cancelled and
    /// the refund this call makes goes ahead either way.
    /// </summary>
    private async Task ScheduleAdminRefundAsync(Dictionary<string, object> escrow, string actor)
    {
        var escrowId = GetString(escrow, "escrowId");
        try
        {
            await _escrowRepo.UpdateStatusAsync(escrowId, GetString(escrow, "createdAt"), "escrowed", "escrowed",
                new Dictionary<string, object>
                {
                    { "expiresAt", DateTime.UtcNow.ToString("o") },
                    { "refundReason", "admin_refund" }
                });
            await AppendAuditAsync(escrowId, "refund_scheduled", "escrowed", "escrowed", actor, metadata: "admin_refund");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ESCROW_ADMIN_REFUND_SCHEDULE_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} error={ex.GetType().Name}");
        }
    }

    /// <summary>An admin release or refund the order cannot follow: audited per attempt, answered 422.</summary>
    private async Task<SettlementBlock> RefuseAdminSettlementAsync(
        string escrowId, string actor, string op, string orderId, string orderStatus, bool overridable)
    {
        try
        {
            await AppendAuditAsync(escrowId, "admin_settlement_order_refused", "escrowed", "escrowed", actor,
                metadata: $"op={op};orderId={orderId};orderStatus={orderStatus}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ESCROW_ADMIN_SETTLEMENT_REFUSAL_AUDIT_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} error={ex.GetType().Name}");
        }

        var message = op == "release"
            ? $"The order behind this escrow is '{orderStatus}', not delivered. Nothing was paid: confirm the delivery first, or use the dispute path."
            : $"The order behind this escrow is '{orderStatus}' and cannot follow a refund. Nothing was refunded"
              + (overridable ? ": overrideOrderMismatch with a reason refunds it anyway, leaving the order flagged." : ".");
        return new SettlementBlock(422, "ESCROW_INVALID_TRANSITION", message);
    }

    /// <summary>The escrow's order: its own <c>orderId</c>, else its delivery's.</summary>
    private async Task<string?> ResolveEscrowOrderIdAsync(Dictionary<string, object> escrow)
    {
        var orderId = GetStringOrNull(escrow, "orderId");
        if (!string.IsNullOrEmpty(orderId))
        {
            return orderId;
        }

        var deliveryId = GetStringOrNull(escrow, "deliveryId");
        if (_deliveryRepo == null || string.IsNullOrEmpty(deliveryId))
        {
            return null;
        }

        var delivery = await _deliveryRepo.GetByIdAsync(deliveryId, consistentRead: true);
        return delivery != null ? GetStringOrNull(delivery, "orderId") : null;
    }

    /// <summary>The status of the order's active delivery (else the escrow's), null when there is none.</summary>
    private async Task<string?> ActiveDeliveryStatusAsync(Dictionary<string, object> order, Dictionary<string, object> escrow)
    {
        var deliveryId = GetStringOrNull(order, "activeDeliveryId") ?? GetStringOrNull(escrow, "deliveryId");
        if (_deliveryRepo == null || string.IsNullOrEmpty(deliveryId))
        {
            return null;
        }

        var delivery = await _deliveryRepo.GetByIdAsync(deliveryId, consistentRead: true);
        return delivery != null ? GetStringOrNull(delivery, "status") : null;
    }

    private static DateTime? ParseUtcTimeOrNull(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    // ──────────────────────────────────────────────
    // Manual payout queue (ESC-02)
    // ──────────────────────────────────────────────

    /// <summary>Escrow statuses from which a queued settlement may still close.</summary>
    private static readonly HashSet<string> ManualPayoutSettleableStatuses = new()
    {
        "escrowed", "disputed", "dispute_resolved"
    };

    /// <summary>
    /// GET /admin/payouts/manual: escrows whose release or refund the gateway could not make,
    /// oldest first, with one row per payee leg (amount, registered phone, identity gate).
    /// </summary>
    public async Task<EscrowResult<ManualPayoutListResponse>> ListManualPayoutsAsync(int limit, string? nextToken)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 20;
        }

        Dictionary<string, AttributeValue>? exclusiveStartKey = null;
        if (!string.IsNullOrEmpty(nextToken))
        {
            exclusiveStartKey = PaginationTokenHelper.DecodeNextToken(nextToken);
            if (exclusiveStartKey == null)
            {
                return EscrowResult<ManualPayoutListResponse>.Failure(
                    400, "ESCROW_023", "Invalid pagination token.");
            }
        }

        var (items, lastEvaluatedKey) = await _escrowRepo.QueryOpenManualPayoutsAsync(limit, exclusiveStartKey);
        var userCache = new Dictionary<string, UserDto?>(StringComparer.Ordinal);
        var payouts = new List<ManualPayoutDto>(items.Count);
        foreach (var item in items)
        {
            payouts.Add(await MapManualPayoutAsync(item, userCache));
        }

        return EscrowResult<ManualPayoutListResponse>.Success(new ManualPayoutListResponse
        {
            Payouts = payouts,
            NextToken = lastEvaluatedKey != null ? PaginationTokenHelper.EncodeNextToken(lastEvaluatedKey) : null,
            Total = payouts.Count
        });
    }

    /// <summary>
    /// POST /admin/payouts/{escrowId}/{leg}/complete: an admin paid one leg in the Telebirr
    /// merchant portal and records its transaction id. The last leg closes the queue and
    /// moves the escrow to <c>released</c> then <c>completed</c>, or to <c>refunded</c>,
    /// with the admin-entered references as the settlement references. This is the only
    /// way a queued escrow ever settles; the gateway is never called here. ESC-04: a trucker leg
    /// queued on a released escrow (the farmer already paid) closes straight to <c>completed</c>.
    /// </summary>
    public async Task<EscrowResult<CompleteManualPayoutResponse>> CompleteManualPayoutAsync(
        string escrowId, string leg, string? externalRef, string actorUserId,
        bool adminGroupVerified, AuditContext? auditContext = null)
    {
        _auditContext = auditContext;

        // The admin pool's admin group only: no users-table role fallback for this money action.
        if (!adminGroupVerified)
        {
            return EscrowResult<CompleteManualPayoutResponse>.Failure(
                403, "ESCROW_015", "Only an admin can record a manual payout.");
        }

        var reference = (externalRef ?? "").Trim();
        if (!IsValidExternalRef(reference))
        {
            return EscrowResult<CompleteManualPayoutResponse>.Failure(
                400, "ESCROW_037",
                "externalRef must be the Telebirr transaction id: 4 to 64 letters, digits or . _ : / -",
                "externalRef");
        }

        if (!ManualPayoutFields.IsLeg(leg))
        {
            return EscrowResult<CompleteManualPayoutResponse>.Failure(
                404, "ESCROW_035", "No such payout leg.");
        }

        // A refusal on an existing escrow is audited too, so a second admin who tried to record
        // another reference (or a blocked payee) leaves a trace next to the leg.
        async Task<EscrowResult<CompleteManualPayoutResponse>> RefuseAsync(
            string status, int httpStatus, string code, string message)
        {
            try
            {
                await AppendAuditAsync(escrowId, "manual_payout_refused", status, status, actorUserId,
                    metadata: $"leg={leg};code={code};attemptedRef={reference}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ESCROW_MANUAL_PAYOUT_REFUSAL_AUDIT_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} error={ex.GetType().Name}");
            }

            return EscrowResult<CompleteManualPayoutResponse>.Failure(httpStatus, code, message);
        }

        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            // Strongly consistent: a stale copy would burn the retries below on a row that
            // already moved.
            var escrow = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
            if (escrow == null)
            {
                return EscrowResult<CompleteManualPayoutResponse>.Failure(
                    404, "ESCROW_016", "Escrow not found.");
            }

            var op = GetString(escrow, ManualPayoutFields.Op);
            var currentStatus = GetString(escrow, "status");
            var createdAt = GetString(escrow, "createdAt");
            var legStatus = GetStringOrNull(escrow, ManualPayoutFields.StatusAttr(leg));
            var legRef = GetStringOrNull(escrow, ManualPayoutFields.RefAttr(leg));

            // Replay of a recorded leg, before or after the queue closed: same reference is
            // idempotent, a different one is refused (ESCROW_036).
            if (legStatus == ManualPayoutFields.LegPaid)
            {
                if (!string.Equals(legRef, reference, StringComparison.Ordinal))
                {
                    return await RefuseAsync(currentStatus,
                        409, "ESCROW_036", "This leg was already recorded with a different reference.");
                }

                // A crash after the money write may have left the audit rows or the final
                // status behind: re-ensure them (deterministic keys, never written twice).
                currentStatus = await EnsureManualPayoutRecordedAsync(escrow, op, leg, currentStatus);
                // ESC-09: a replay also repairs an order the first close could not move.
                await FollowMoneyAsync(escrowId, "manual_payout");

                return EscrowResult<CompleteManualPayoutResponse>.Success(BuildCompleteResponse(
                    escrow, leg, reference, GetStringOrNull(escrow, ManualPayoutFields.PaidAtAttr(leg)) ?? "",
                    currentStatus, PendingLegs(escrow, op, except: leg), "Already recorded.", alreadyRecorded: true));
            }

            if (!IsManualPayoutOpen(escrow) || legStatus == null || !ManualPayoutFields.LegBelongsToOp(leg, op))
            {
                return await RefuseAsync(currentStatus,
                    404, "ESCROW_035", "This escrow has no open manual payout for that leg.");
            }

            foreach (var other in ManualPayoutFields.AllLegs)
            {
                if (other != leg && string.Equals(
                        GetStringOrNull(escrow, ManualPayoutFields.RefAttr(other)), reference, StringComparison.Ordinal))
                {
                    return await RefuseAsync(currentStatus,
                        409, "ESCROW_036", "This reference was already used for another leg of this escrow.");
                }
            }

            // ESC-04: a trucker leg queued on a released escrow (the farmer already paid) closes too.
            var releasedTruckerQueue = IsReleasedTruckerQueue(escrow);
            if (!ManualPayoutSettleableStatuses.Contains(currentStatus) && !releasedTruckerQueue)
            {
                return await RefuseAsync(currentStatus,
                    409, "ESCROW_INVALID_TRANSITION",
                    $"Escrow is '{currentStatus}' and can no longer be settled from the queue.");
            }

            // The payee must still pass the identity gate the gateway release applies.
            var payeeId = GetString(escrow, ManualPayoutPayeeField(leg));
            if (leg == ManualPayoutFields.LegTrucker && string.IsNullOrEmpty(payeeId))
            {
                // ESC-04: an escrow minted before its order had a truck has no truckerId; take the
                // order's, checked against the delivery's, and keep it on the escrow first.
                var (truckerId, fromOrder, truckerBlock) = await ResolveTruckerIdAsync(escrow);
                if (truckerId == null)
                {
                    return await RefuseAsync(currentStatus, 409, "ESCROW_029", TruckerBlockMessage(truckerBlock));
                }

                if (fromOrder && !await BackfillTruckerIdAsync(escrow, truckerId, actorUserId))
                {
                    if (attempt < maxAttempts)
                    {
                        continue;
                    }

                    return await RefuseAsync(currentStatus,
                        409, "ESCROW_036", "The payout changed while this leg was being recorded. Reload and try again.");
                }

                payeeId = truckerId;
            }

            var amount = ManualPayoutLegAmount(escrow, leg);
            var payee = await GetUserCachedAsync(payeeId, new Dictionary<string, UserDto?>(StringComparer.Ordinal));
            var (payeeVerified, blockReason) = EvaluateManualPayee(escrow, leg, payee, amount);
            if (!payeeVerified)
            {
                return await RefuseAsync(currentStatus,
                    409, "ESCROW_029", blockReason ?? "Payout blocked.");
            }

            var pendingOthers = PendingLegs(escrow, op, except: leg);
            var paidOthers = ManualPayoutFields.AllLegs
                .Where(l => l != leg && ManualPayoutFields.LegBelongsToOp(l, op)
                            && GetStringOrNull(escrow, ManualPayoutFields.StatusAttr(l)) == ManualPayoutFields.LegPaid)
                .ToList();
            var now = DateTime.UtcNow.ToString("o");
            var newStatus = currentStatus;
            bool written;
            if (pendingOthers.Count > 0)
            {
                written = await _escrowRepo.TryMarkManualLegPaidAsync(
                    escrowId, createdAt, op, leg, reference, actorUserId, now, pendingOthers[0]);
            }
            else if (releasedTruckerQueue)
            {
                // ESC-04: the release already happened (releasedAt and telebirrReleaseRef stay as the
                // release wrote them); the last leg completes the escrow in the closing write.
                newStatus = "completed";
                var fields = new Dictionary<string, object>
                {
                    { TruckerPayoutFields.Ref, leg == ManualPayoutFields.LegTrucker
                        ? reference
                        : GetStringOrNull(escrow, ManualPayoutFields.RefAttr(ManualPayoutFields.LegTrucker)) ?? "" },
                    { TruckerPayoutFields.PaidAt, now },
                    { "completedAt", AddTicks(now, 1) }
                };
                written = await _escrowRepo.TryCloseManualPayoutAsync(
                    escrowId, createdAt, op, leg, reference, actorUserId, now, paidOthers, newStatus, fields);
            }
            else
            {
                newStatus = op == ManualPayoutFields.OpRelease ? "released" : "refunded";
                var fields = new Dictionary<string, object>();
                if (op == ManualPayoutFields.OpRelease)
                {
                    var farmerRef = leg == ManualPayoutFields.LegFarmer
                        ? reference
                        : GetStringOrNull(escrow, ManualPayoutFields.RefAttr(ManualPayoutFields.LegFarmer)) ?? "";
                    fields["telebirrReleaseRef"] = farmerRef;
                    fields["releasedAt"] = now;
                    if (GetStringOrNull(escrow, ManualPayoutFields.StatusAttr(ManualPayoutFields.LegTrucker)) != null)
                    {
                        fields["truckerTelebirrRef"] = leg == ManualPayoutFields.LegTrucker
                            ? reference
                            : GetStringOrNull(escrow, ManualPayoutFields.RefAttr(ManualPayoutFields.LegTrucker)) ?? "";
                    }
                }
                else
                {
                    fields["telebirrRefundRef"] = reference;
                    fields["refundedAt"] = now;
                }

                written = await _escrowRepo.TryCloseManualPayoutAsync(
                    escrowId, createdAt, op, leg, reference, actorUserId, now, paidOthers, newStatus, fields);
            }

            if (!written)
            {
                // Another admin recorded a leg in between (or the escrow moved): re-read and decide again.
                if (attempt < maxAttempts)
                {
                    continue;
                }

                return await RefuseAsync(currentStatus,
                    409, "ESCROW_036", "The payout changed while this leg was being recorded. Reload and try again.");
            }

            // Audit rows at deterministic keys (paidAt, paidAt + 1 tick): if one of these
            // writes fails, a replay of the same reference fills it in without a duplicate.
            await EnsureQueuedAuditAsync(escrow, op);
            await EnsureAuditAsync(escrowId, now, "manual_payout_paid", currentStatus, currentStatus, actorUserId,
                reference, ManualPaidMetadata(leg, amount, payeeId));
            Console.WriteLine($"ESCROW_MANUAL_PAYOUT_PAID escrowId={MoneyAlarmLog.LogId(escrowId)} op={op} leg={leg} remaining={pendingOthers.Count}");

            var finalStatus = currentStatus;
            if (releasedTruckerQueue && newStatus == "completed")
            {
                // No release row: the release was audited when it paid the farmer. The complete row
                // sits at completedAt, the key every completing writer uses.
                await EnsureManualCompleteAuditAsync(escrowId, AddTicks(now, 1), actorUserId);
                finalStatus = newStatus;
            }
            else if (newStatus != currentStatus)
            {
                await EnsureAuditAsync(escrowId, AddTicks(now, 1), op == ManualPayoutFields.OpRelease ? "release" : "refund",
                    currentStatus, newStatus, actorUserId, reference, "manual");
                finalStatus = newStatus == "released"
                    ? await FinishManualReleaseAsync(escrowId, createdAt, actorUserId)
                    : newStatus;
            }

            // ESC-09: the order follows the recorded money (disbursing while a leg waits, settled once
            // the escrow is completed; a refund only checks that the order is closed).
            await FollowMoneyAsync(escrowId, "manual_payout");

            return EscrowResult<CompleteManualPayoutResponse>.Success(BuildCompleteResponse(
                escrow, leg, reference, now, finalStatus, pendingOthers,
                pendingOthers.Count > 0
                    ? $"Leg recorded. {pendingOthers.Count} leg(s) still waiting."
                    : finalStatus == "refunded"
                        ? "Refund recorded. The escrow is refunded."
                        : "Payout recorded. The escrow is completed.",
                alreadyRecorded: false));
        }
    }

    /// <summary>
    /// Replay of an already recorded leg: make sure its queued and paid audit rows exist and,
    /// when the queue already closed, the release or refund row (closing leg's paidAt + 1 tick)
    /// and the move to completed. Returns the escrow status after that.
    /// </summary>
    private async Task<string> EnsureManualPayoutRecordedAsync(
        Dictionary<string, object> escrow, string op, string leg, string currentStatus)
    {
        var escrowId = GetString(escrow, "escrowId");
        var fromStatus = GetStringOrNull(escrow, ManualPayoutFields.FromStatus) ?? currentStatus;
        var legFromStatus = ManualPayoutSettleableStatuses.Contains(currentStatus) ? currentStatus : fromStatus;
        await EnsureQueuedAuditAsync(escrow, op);

        var paidAt = GetStringOrNull(escrow, ManualPayoutFields.PaidAtAttr(leg));
        var paidBy = GetStringOrNull(escrow, ManualPayoutFields.PaidByAttr(leg));
        if (paidAt != null && paidBy != null && paidBy != ManualPayoutFields.PaidByGateway)
        {
            var payeeId = GetStringOrNull(escrow, ManualPayoutPayeeField(leg)) ?? "";
            await EnsureAuditAsync(escrowId, paidAt, "manual_payout_paid", legFromStatus, legFromStatus, paidBy,
                GetStringOrNull(escrow, ManualPayoutFields.RefAttr(leg)),
                ManualPaidMetadata(leg, ManualPayoutLegAmount(escrow, leg), payeeId));
        }

        if (IsManualPayoutOpen(escrow) || currentStatus is not ("released" or "completed" or "refunded"))
        {
            return currentStatus;
        }

        // The queue closed on the leg recorded last.
        var closingLeg = ManualPayoutFields.AllLegs
            .Where(l => ManualPayoutFields.LegBelongsToOp(l, op)
                        && GetStringOrNull(escrow, ManualPayoutFields.PaidAtAttr(l)) != null)
            .OrderByDescending(l => GetString(escrow, ManualPayoutFields.PaidAtAttr(l)), StringComparer.Ordinal)
            .FirstOrDefault();
        // ESC-04: a trucker leg queued on a released escrow has no release row of its own (the
        // release paid the farmer and was audited then); its close only completes the escrow.
        if (closingLeg != null && fromStatus != "released")
        {
            var closedAt = GetString(escrow, ManualPayoutFields.PaidAtAttr(closingLeg));
            var settled = op == ManualPayoutFields.OpRelease ? "released" : "refunded";
            await EnsureAuditAsync(escrowId, AddTicks(closedAt, 1), op == ManualPayoutFields.OpRelease ? "release" : "refund",
                fromStatus, settled, GetStringOrNull(escrow, ManualPayoutFields.PaidByAttr(closingLeg)) ?? "system",
                GetStringOrNull(escrow, ManualPayoutFields.RefAttr(closingLeg)), "manual");
        }

        if (currentStatus == "released")
        {
            return await FinishManualReleaseAsync(escrowId, GetString(escrow, "createdAt"), "system");
        }

        // Completed already: its complete row sits at completedAt, the key both this service and
        // the cron's crash recovery write it under, so re-ensuring it never duplicates it.
        var completedAt = GetStringOrNull(escrow, "completedAt");
        if (currentStatus == "completed" && completedAt != null)
        {
            await EnsureAuditAsync(escrowId, completedAt, "complete", "released", "completed", "system", metadata: "manual");
        }

        return currentStatus;
    }

    private static string ManualPaidMetadata(string leg, decimal amount, string payeeId)
        => $"leg={leg};amount={amount.ToString("F2", CultureInfo.InvariantCulture)};payee={payeeId}";

    /// <summary>An ISO "o" time plus <paramref name="ticks"/> 100ns ticks, for adjacent deterministic audit keys.</summary>
    private static string AddTicks(string isoTime, long ticks)
        => DateTime.Parse(isoTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .ToUniversalTime().AddTicks(ticks).ToString("o");

    /// <summary>
    /// released to completed after a manual release close. A transient failure here is
    /// recovered by the cron's CompleteReleasedAsync sweep, so it is logged, not thrown.
    /// </summary>
    private async Task<string> FinishManualReleaseAsync(string escrowId, string createdAt, string actor)
    {
        var completedAt = DateTime.UtcNow.ToString("o");
        try
        {
            await _escrowRepo.UpdateStatusAsync(escrowId, createdAt, "completed", "released",
                new Dictionary<string, object> { { "completedAt", completedAt } });
        }
        catch (ConditionalCheckFailedException)
        {
            // Already completed by a parallel sweep or replay, which writes its own complete row,
            // or (ESC-04) a leg is still owed and the escrow stays released: the row decides.
            return await ReadStatusOrAsync(escrowId, "released");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ESCROW_MANUAL_PAYOUT_COMPLETE_DEFERRED escrowId={MoneyAlarmLog.LogId(escrowId)} error={ex.GetType().Name}");
            return "released";
        }

        // The status is completed now, whatever happens to its audit row; a replay re-ensures
        // the row at the same key (completedAt).
        await EnsureManualCompleteAuditAsync(escrowId, completedAt, actor);

        return "completed";
    }

    /// <summary>
    /// The complete row of a manual close at its completedAt key. A failed write never changes the
    /// answer (the status is completed already); a replay re-ensures it at the same key.
    /// </summary>
    private async Task EnsureManualCompleteAuditAsync(string escrowId, string completedAt, string actor)
    {
        try
        {
            await EnsureAuditAsync(escrowId, completedAt, "complete", "released", "completed", actor, metadata: "manual");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ESCROW_MANUAL_PAYOUT_COMPLETE_AUDIT_DEFERRED escrowId={MoneyAlarmLog.LogId(escrowId)} error={ex.GetType().Name}");
        }
    }

    /// <summary>The escrow's status from a strongly consistent read, or <paramref name="fallback"/> when it cannot be read.</summary>
    private async Task<string> ReadStatusOrAsync(string escrowId, string fallback)
    {
        try
        {
            var latest = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
            return latest != null ? GetString(latest, "status") : fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static CompleteManualPayoutResponse BuildCompleteResponse(
        Dictionary<string, object> escrow, string leg, string reference, string paidAt, string escrowStatus,
        List<string> remainingLegs, string message, bool alreadyRecorded) => new()
        {
            EscrowId = GetString(escrow, "escrowId"),
            OrderId = GetStringOrNull(escrow, "orderId"),
            Ref = leg,
            LegStatus = ManualPayoutFields.LegPaid,
            EscrowStatus = escrowStatus,
            ExternalRef = reference,
            PaidAt = paidAt,
            RemainingLegs = remainingLegs,
            Message = message,
            AlreadyRecorded = alreadyRecorded
        };

    private static List<string> PendingLegs(Dictionary<string, object> escrow, string op, string except)
        => ManualPayoutFields.AllLegs
            .Where(l => l != except && ManualPayoutFields.LegBelongsToOp(l, op)
                        && GetStringOrNull(escrow, ManualPayoutFields.StatusAttr(l)) == ManualPayoutFields.LegPending)
            .ToList();

    private async Task<ManualPayoutDto> MapManualPayoutAsync(
        Dictionary<string, object> item, Dictionary<string, UserDto?> userCache)
    {
        var op = GetString(item, ManualPayoutFields.Op);
        var legs = new List<ManualPayoutLegDto>();
        foreach (var leg in ManualPayoutFields.AllLegs)
        {
            var status = GetStringOrNull(item, ManualPayoutFields.StatusAttr(leg));
            if (status == null)
            {
                continue;
            }

            var payeeId = GetString(item, ManualPayoutPayeeField(leg));
            string? truckerBlock = null;
            if (leg == ManualPayoutFields.LegTrucker && string.IsNullOrEmpty(payeeId)
                && status == ManualPayoutFields.LegPending)
            {
                // ESC-04: show the trucker the record would take from the order (nothing is written
                // here; recording the leg keeps it on the escrow).
                var resolved = await ResolveTruckerIdAsync(item);
                payeeId = resolved.TruckerId ?? "";
                truckerBlock = resolved.BlockReason;
            }

            var amount = ManualPayoutLegAmount(item, leg);
            var payee = await GetUserCachedAsync(payeeId, userCache);
            var (verified, blockReason) = truckerBlock != null
                ? (false, TruckerBlockMessage(truckerBlock))
                : EvaluateManualPayee(item, leg, payee, amount);
            legs.Add(new ManualPayoutLegDto
            {
                Ref = leg,
                PayeeRole = leg,
                PayeeId = payeeId,
                PayeeName = payee?.DisplayName ?? payee?.FullName,
                PayeePhone = payee?.PhoneNumber,
                PayeeVerified = verified,
                BlockReason = blockReason,
                Amount = amount,
                Status = status,
                ExternalRef = GetStringOrNull(item, ManualPayoutFields.RefAttr(leg)),
                PaidBy = GetStringOrNull(item, ManualPayoutFields.PaidByAttr(leg)),
                PaidAt = GetStringOrNull(item, ManualPayoutFields.PaidAtAttr(leg)),
                GatewayRef = GatewayTransactionRef(GetString(item, "escrowId"), leg)
            });
        }

        return new ManualPayoutDto
        {
            EscrowId = GetString(item, "escrowId"),
            OrderId = GetStringOrNull(item, "orderId"),
            DeliveryId = GetStringOrNull(item, "deliveryId"),
            Op = op,
            Reason = GetString(item, ManualPayoutFields.Reason),
            EscrowStatus = GetString(item, "status"),
            FromStatus = GetString(item, ManualPayoutFields.FromStatus),
            DeliveryConfirmedAt = GetStringOrNull(item, "deliveryConfirmedAt"),
            // ESC-04: for a trucker leg queued on a released escrow the gateway call that may have
            // paid is the trucker retry's, under its own lock.
            SettlementLockedAt = IsReleasedTruckerQueue(item)
                ? GetStringOrNull(item, TruckerPayoutFields.LockAt)
                : GetStringOrNull(item, SettlementLockFields.LockAt),
            DisputeResolution = GetStringOrNull(item, "disputeResolution"),
            QueuedAt = GetString(item, ManualPayoutFields.QueuedAt),
            Currency = GetStringOrNull(item, "currency") ?? "ETB",
            Legs = legs,
            PendingAmount = legs.Where(l => l.Status == ManualPayoutFields.LegPending).Sum(l => l.Amount)
        };
    }

    /// <summary>The user the leg pays: farmerId, truckerId or buyerId.</summary>
    private static string ManualPayoutPayeeField(string leg) => leg switch
    {
        ManualPayoutFields.LegFarmer => "farmerId",
        ManualPayoutFields.LegTrucker => "truckerId",
        _ => "buyerId"
    };

    /// <summary>What the admin pays for the leg: the crop leg, the transport leg, or the whole hold.</summary>
    private static decimal ManualPayoutLegAmount(Dictionary<string, object> escrow, string leg) => leg switch
    {
        ManualPayoutFields.LegFarmer => GetDecimal(escrow, "farmerPayout"),
        ManualPayoutFields.LegTrucker => GetDecimalOrNull(escrow, "truckerPayout") ?? 0m,
        _ => GetDecimal(escrow, "amount")
    };

    /// <summary>
    /// The same gate the gateway settlement applies: farmer and trucker through the payout
    /// identity rules (the farmer's Fayda tier is judged on the whole held amount, as in
    /// ReleaseAsync), the buyer only needs a profile with a phone (it is their own money).
    /// Without a user repository the gate passes, as ResolvePayoutAsync does.
    /// </summary>
    private (bool Verified, string? BlockReason) EvaluateManualPayee(
        Dictionary<string, object> escrow, string leg, UserDto? payee, decimal amount)
    {
        if (_userRepo == null)
        {
            return (true, null);
        }

        if (leg == ManualPayoutFields.LegBuyer)
        {
            return payee != null && !string.IsNullOrEmpty(payee.PhoneNumber)
                ? (true, null)
                : (false, "Refund blocked: buyer profile or phone not found.");
        }

        var thresholdAmount = leg == ManualPayoutFields.LegFarmer ? GetDecimal(escrow, "amount") : amount;
        var check = CheckPayoutEligibility(payee, thresholdAmount, leg);
        return check.IsSuccess ? (true, null) : (false, check.Error!.Message);
    }

    private async Task<UserDto?> GetUserCachedAsync(string userId, Dictionary<string, UserDto?> cache)
    {
        if (_userRepo == null || string.IsNullOrEmpty(userId))
        {
            return null;
        }

        if (!cache.TryGetValue(userId, out var user))
        {
            user = await _userRepo.GetUserByIdAsync(userId);
            cache[userId] = user;
        }

        return user;
    }

    /// <summary>
    /// Queue a settlement the gateway could not make. Returns false (and writes nothing
    /// else) when the conditional write fails or throws and a strongly consistent re-read
    /// shows no queue entry for <paramref name="op"/>, so the caller decides what to do
    /// with the lock. A write that committed although the client saw an error (timeout)
    /// counts as queued. On success writes the audit row and the log token. <paramref name="write"/>
    /// replaces the settlement's queue write (ESC-04: the trucker leg's, fenced on its own lock).
    /// </summary>
    private async Task<bool> TryQueueManualPayoutAsync(
        string escrowId, string createdAt, string op, string reason, string fromStatus,
        List<ManualPayoutLegSeed> legs, string actor, Func<string, Task<bool>>? write = null)
    {
        var queuedAt = DateTime.UtcNow.ToString("o");
        bool queued;
        try
        {
            queued = write != null
                ? await write(queuedAt)
                : await _escrowRepo.TryQueueManualPayoutAsync(escrowId, createdAt, op, reason, fromStatus, legs, queuedAt);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ESCROW_MANUAL_PAYOUT_QUEUE_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} op={op} error={ex.GetType().Name}");
            queued = false;
        }

        if (!queued)
        {
            Dictionary<string, object>? latest;
            try
            {
                latest = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ESCROW_MANUAL_PAYOUT_QUEUE_RECHECK_FAILED escrowId={MoneyAlarmLog.LogId(escrowId)} op={op} error={ex.GetType().Name}");
                return false;
            }

            if (latest == null || !IsManualPayoutOpen(latest)
                || !string.Equals(GetStringOrNull(latest, ManualPayoutFields.Op), op, StringComparison.Ordinal))
            {
                return false;
            }

            queuedAt = GetStringOrNull(latest, ManualPayoutFields.QueuedAt) ?? queuedAt;
        }

        var legNames = string.Join(",", legs.Select(l => l.Leg));
        await EnsureAuditAsync(escrowId, queuedAt, "manual_payout_queued", fromStatus, fromStatus, actor,
            metadata: $"op={op};legs={legNames};reason={reason}");
        Console.WriteLine($"ESCROW_MANUAL_PAYOUT_QUEUED escrowId={MoneyAlarmLog.LogId(escrowId)} op={op} legs={legNames} reason={reason}");
        return true;
    }

    /// <summary>
    /// The queued audit row of an open or closed queue entry, re-ensured from the row itself
    /// (a crash right after the queue write leaves it missing).
    /// </summary>
    private Task EnsureQueuedAuditAsync(Dictionary<string, object> escrow, string op)
    {
        var queuedAt = GetStringOrNull(escrow, ManualPayoutFields.QueuedAt);
        if (queuedAt == null)
        {
            return Task.CompletedTask;
        }

        var fromStatus = GetStringOrNull(escrow, ManualPayoutFields.FromStatus) ?? GetString(escrow, "status");
        var legNames = string.Join(",", ManualPayoutFields.AllLegs
            .Where(l => GetStringOrNull(escrow, ManualPayoutFields.StatusAttr(l)) != null));
        return EnsureAuditAsync(GetString(escrow, "escrowId"), queuedAt, "manual_payout_queued", fromStatus, fromStatus,
            "system", metadata: $"op={op};legs={legNames};reason={GetStringOrNull(escrow, ManualPayoutFields.Reason)}");
    }

    /// <summary>
    /// Why a gateway answer needs a manual payout: the gateway said it cannot pay out, or it
    /// claimed success without a reference (fail closed rather than complete with ""). Any
    /// other failure keeps the retryable failure path.
    /// </summary>
    private static string? ManualPayoutReason(bool success, string? errorCode, string? telebirrRef)
    {
        if (!success && string.Equals(errorCode, TelebirrGatewayErrors.PayoutNotSupported, StringComparison.Ordinal))
        {
            return ManualPayoutFields.ReasonPayoutNotSupported;
        }

        if (success && string.IsNullOrWhiteSpace(telebirrRef))
        {
            return ManualPayoutFields.ReasonGatewayRefMissing;
        }

        return null;
    }

    internal static bool IsManualPayoutOpen(Dictionary<string, object> escrow)
        => string.Equals(GetStringOrNull(escrow, ManualPayoutFields.Marker), ManualPayoutFields.MarkerOpen,
            StringComparison.Ordinal);

    /// <summary>409 ESCROW_038: no dispute on an escrow whose settlement waits in the manual payout queue.</summary>
    private static EscrowResult<DisputeEscrowResponse> DisputeRefusedManualPayout() => EscrowResult<DisputeEscrowResponse>.Failure(
        409, "ESCROW_038",
        "This payment is already being paid out by an admin, so it can no longer be disputed. Contact support.");

    /// <summary>
    /// 202 ESCROW_034: the settlement is waiting in the manual payout queue and this call made no
    /// payout. Whether an earlier gateway call did depends on the queue reason (ESC-05:
    /// <c>GATEWAY_OUTCOME_UNKNOWN</c> may have paid). Returned to the cron and the admin
    /// settlement routes only.
    /// </summary>
    private static EscrowResult<T> ManualPayoutWaiting<T>() => EscrowResult<T>.Failure(
        202, "ESCROW_034",
        "This payout is waiting in the manual payout queue. An admin pays it in the Telebirr portal and records the reference.");

    /// <summary>
    /// 202 ESCROW_034 (ESC-05): the gateway did not say whether it paid, so the settlement was
    /// parked in the manual payout queue as <c>GATEWAY_OUTCOME_UNKNOWN</c>. The money may have moved.
    /// </summary>
    private static EscrowResult<T> OutcomeUnknownWaiting<T>() => EscrowResult<T>.Failure(
        202, "ESCROW_034",
        "The gateway did not confirm this payout, so it may already have been made. It waits in the manual "
        + "payout queue: an admin checks the Telebirr portal before paying anything.");

    /// <summary>4 to 64 characters: a letter or digit first, then letters, digits, '.', '_', ':', '/' or '-'.</summary>
    internal static bool IsValidExternalRef(string reference)
    {
        if (reference.Length is < 4 or > 64 || !char.IsAsciiLetterOrDigit(reference[0]))
        {
            return false;
        }

        foreach (var c in reference)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or ':' or '/' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Get a single escrow by ID. Only accessible by buyer, farmer, or admin.
    /// </summary>
    public async Task<EscrowResult<EscrowDto>> GetByIdAsync(
        string escrowId, string authenticatedUserId, string? role = null)
    {
        // 1. Get escrow → ESCROW_016
        var escrowItem = await _escrowRepo.GetByIdAsync(escrowId);
        if (escrowItem == null)
        {
            return EscrowResult<EscrowDto>.Failure(404, "ESCROW_016", "Escrow not found.");
        }

        // 2. Validate user is buyer, farmer, trucker, or admin → ESCROW_022
        if (!await IsEscrowPartyOrAdminAsync(escrowItem, authenticatedUserId, role))
        {
            return EscrowResult<EscrowDto>.Failure(
                403, "ESCROW_022", "You are not a party to this escrow.");
        }

        // 3. Map to EscrowDto
        return EscrowResult<EscrowDto>.Success(MapToEscrowDto(escrowItem));
    }

    /// <summary>
    /// Get the escrow associated with a delivery (1 escrow per delivery, via the
    /// DeliveryIndex GSI). Enforces the same party-or-admin visibility as
    /// <see cref="GetByIdAsync"/>. F-33.
    /// </summary>
    public async Task<EscrowResult<EscrowDto>> GetByDeliveryAsync(
        string deliveryId, string authenticatedUserId, string? role = null)
    {
        // 1. Get escrow by deliveryId → ESCROW_016
        var escrowItem = await _escrowRepo.GetByDeliveryIdAsync(deliveryId);
        if (escrowItem == null)
        {
            return EscrowResult<EscrowDto>.Failure(404, "ESCROW_016", "Escrow not found.");
        }

        // 2. Validate user is buyer, farmer, trucker, or admin → ESCROW_022
        if (!await IsEscrowPartyOrAdminAsync(escrowItem, authenticatedUserId, role))
        {
            return EscrowResult<EscrowDto>.Failure(
                403, "ESCROW_022", "You are not a party to this escrow.");
        }

        // 3. Map to EscrowDto
        return EscrowResult<EscrowDto>.Success(MapToEscrowDto(escrowItem));
    }

    /// <summary>
    /// Who may read an escrow: its buyer, its farmer, the trucker the order bound to
    /// it (paid the transport leg out of the held amount), or an admin/system caller.
    /// SIM-15.
    /// </summary>
    private async Task<bool> IsEscrowPartyOrAdminAsync(
        Dictionary<string, object> escrowItem, string authenticatedUserId, string? role)
    {
        if (IsParty(escrowItem, "buyerId", authenticatedUserId)
            || IsParty(escrowItem, "farmerId", authenticatedUserId)
            || IsParty(escrowItem, "truckerId", authenticatedUserId))
        {
            return true;
        }

        return await IsSystemOrAdminAsync(authenticatedUserId, role);
    }

    private static bool IsParty(Dictionary<string, object> item, string field, string userId)
    {
        var value = GetStringOrNull(item, field);
        return !string.IsNullOrEmpty(value) && string.Equals(value, userId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Get the authenticated user's payment history with optional filters and pagination.
    /// </summary>
    public async Task<EscrowResult<GetEscrowHistoryResponse>> GetHistoryAsync(
        string userId, string? status, string? role, string? fromDate, string? toDate,
        int limit, string? nextToken)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 20;
        }

        // Decode pagination token
        Dictionary<string, AttributeValue>? exclusiveStartKey = null;
        if (!string.IsNullOrEmpty(nextToken))
        {
            exclusiveStartKey = PaginationTokenHelper.DecodeNextToken(nextToken);
            if (exclusiveStartKey == null)
            {
                return EscrowResult<GetEscrowHistoryResponse>.Failure(
                    400, "ESCROW_023", "Invalid pagination token.");
            }
        }

        // 1. Query by buyer, farmer or trucker GSI based on role parameter
        List<Dictionary<string, object>> items;
        Dictionary<string, AttributeValue>? lastEvaluatedKey;

        if (string.Equals(role, "farmer", StringComparison.OrdinalIgnoreCase))
        {
            var result = await _escrowRepo.QueryByFarmerAsync(userId, status, limit, exclusiveStartKey);
            items = result.Items;
            lastEvaluatedKey = result.LastEvaluatedKey;
        }
        else if (string.Equals(role, "trucker", StringComparison.OrdinalIgnoreCase))
        {
            // SIM-15: escrows carrying this trucker's transport leg (TruckerStatusIndex).
            var result = await _escrowRepo.QueryByTruckerAsync(userId, status, limit, exclusiveStartKey);
            items = result.Items;
            lastEvaluatedKey = result.LastEvaluatedKey;
        }
        else
        {
            // Default to buyer
            var result = await _escrowRepo.QueryByBuyerAsync(userId, status, limit, exclusiveStartKey);
            items = result.Items;
            lastEvaluatedKey = result.LastEvaluatedKey;
        }

        // 2. Apply date filters (post-query)
        if (!string.IsNullOrEmpty(fromDate))
        {
            items = items
                .Where(i => string.Compare(GetString(i, "createdAt"), fromDate, StringComparison.Ordinal) >= 0)
                .ToList();
        }

        if (!string.IsNullOrEmpty(toDate))
        {
            items = items
                .Where(i => string.Compare(GetString(i, "createdAt"), toDate, StringComparison.Ordinal) <= 0)
                .ToList();
        }

        // 3. Map to EscrowSummaryDto list
        // Farmer and trucker both face the buyer as counterparty.
        var isBuyer = !string.Equals(role, "farmer", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(role, "trucker", StringComparison.OrdinalIgnoreCase);
        var escrows = items.Select(i => MapToEscrowSummaryDto(i, isBuyer)).ToList();

        // 4. Return with pagination
        string? nextTokenResponse = null;
        var hasMore = lastEvaluatedKey != null;
        if (hasMore)
        {
            nextTokenResponse = PaginationTokenHelper.EncodeNextToken(lastEvaluatedKey!);
        }

        return EscrowResult<GetEscrowHistoryResponse>.Success(new GetEscrowHistoryResponse
        {
            Escrows = escrows,
            NextToken = nextTokenResponse,
            HasMore = hasMore,
            Total = escrows.Count
        });
    }

    // ──────────────────────────────────────────────
    // DTO mapping helpers
    // ──────────────────────────────────────────────

    internal static EscrowDto MapToEscrowDto(Dictionary<string, object> item)
    {
        DisputeDto? dispute = null;
        var disputeId = GetStringOrNull(item, "disputeId");
        if (disputeId != null)
        {
            var evidenceRefsRaw = GetStringOrNull(item, "disputeEvidenceRefs");
            List<string>? evidenceRefs = null;
            if (!string.IsNullOrEmpty(evidenceRefsRaw))
            {
                evidenceRefs = evidenceRefsRaw
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .ToList();
            }

            dispute = new DisputeDto
            {
                DisputeId = disputeId,
                EscrowId = GetString(item, "escrowId"),
                DisputedBy = GetString(item, "disputedBy"),
                Reason = GetString(item, "disputeReason"),
                Description = GetStringOrNull(item, "disputeDescription"),
                EvidenceRefs = evidenceRefs,
                Resolution = GetStringOrNull(item, "disputeResolution"),
                ResolvedBy = GetStringOrNull(item, "disputeResolvedBy"),
                ResolvedAt = GetStringOrNull(item, "disputeResolvedAt"),
                CreatedAt = GetString(item, "disputeCreatedAt")
            };
        }

        return new EscrowDto
        {
            EscrowId = GetString(item, "escrowId"),
            DeliveryId = GetString(item, "deliveryId"),
            OrderId = GetStringOrNull(item, "orderId"),
            BuyerId = GetString(item, "buyerId"),
            FarmerId = GetString(item, "farmerId"),
            Amount = GetDecimal(item, "amount"),
            Commission = GetDecimal(item, "commission"),
            CommissionRate = GetDecimal(item, "commissionRate"),
            FarmerPayout = GetDecimal(item, "farmerPayout"),
            TruckerPayout = GetDecimalOrNull(item, "truckerPayout"),
            TruckerId = GetStringOrNull(item, "truckerId"),
            Currency = GetString(item, "currency"),
            Status = GetString(item, "status"),
            TelebirrRef = GetStringOrNull(item, "telebirrRef"),
            TelebirrHoldRef = GetStringOrNull(item, "telebirrHoldRef"),
            TelebirrReleaseRef = GetStringOrNull(item, "telebirrReleaseRef"),
            TelebirrRefundRef = GetStringOrNull(item, "telebirrRefundRef"),
            Dispute = dispute,
            BuyerFaydaVerified = GetBool(item, "buyerFaydaVerified"),
            FarmerFaydaVerified = GetBool(item, "farmerFaydaVerified"),
            CreatedAt = GetString(item, "createdAt"),
            UpdatedAt = GetString(item, "updatedAt"),
            ExpiresAt = GetString(item, "expiresAt"),
            DeliveryConfirmedAt = GetStringOrNull(item, "deliveryConfirmedAt"),
            DisputeWindowEndsAt = GetStringOrNull(item, "disputeWindowEndsAt"),
            ReleasedAt = GetStringOrNull(item, "releasedAt"),
            RefundedAt = GetStringOrNull(item, "refundedAt"),
            CompletedAt = GetStringOrNull(item, "completedAt"),
            RefundPending = IsRefundPending(item),
            RefundReason = GetStringOrNull(item, "refundReason"),
            Version = GetInt(item, "version")
        };
    }

    /// <summary>
    /// SIM-20: held money that is on its way back to the buyer. A cancelled or
    /// rejected order schedules its refund by stamping refundReason and expiring
    /// the hold (ScheduleRefundForOrderAsync); a 7-day timeout expires it too.
    /// Either way the hourly sweep refunds it, so it must not read as "held
    /// safely" in the meantime.
    /// </summary>
    internal static bool IsRefundPending(Dictionary<string, object> item)
    {
        if (!string.Equals(GetString(item, "status"), "escrowed", StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(GetStringOrNull(item, "refundReason")))
        {
            return true;
        }

        // ESC-06: a hold at its 21-day cap keeps its past expiry while an admin decides; it is not
        // on its way back to the buyer.
        if (!string.IsNullOrEmpty(GetStringOrNull(item, AdminQueueFields.HoldCapReachedAt)))
        {
            return false;
        }

        return DateTime.TryParse(
                   GetStringOrNull(item, "expiresAt"), CultureInfo.InvariantCulture,
                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var expiresAt)
               && expiresAt <= DateTime.UtcNow;
    }

    internal static EscrowSummaryDto MapToEscrowSummaryDto(
        Dictionary<string, object> item, bool isBuyer)
    {
        var counterpartyName = isBuyer
            ? GetStringOrNull(item, "farmerName") ?? GetString(item, "farmerId")
            : GetStringOrNull(item, "buyerName") ?? GetString(item, "buyerId");

        return new EscrowSummaryDto
        {
            EscrowId = GetString(item, "escrowId"),
            // Empty until an order-funded escrow is linked to its delivery.
            DeliveryId = GetString(item, "deliveryId"),
            OrderId = GetStringOrNull(item, "orderId"),
            BuyerId = GetString(item, "buyerId"),
            FarmerId = GetString(item, "farmerId"),
            CounterpartyName = counterpartyName,
            Amount = GetDecimal(item, "amount"),
            Commission = GetDecimal(item, "commission"),
            // Each party's own leg; amount = farmerPayout + truckerPayout + commission.
            FarmerPayout = GetDecimal(item, "farmerPayout"),
            TruckerPayout = GetDecimalOrNull(item, "truckerPayout"),
            TruckerId = GetStringOrNull(item, "truckerId"),
            Currency = GetString(item, "currency"),
            Status = GetString(item, "status"),
            CreatedAt = GetString(item, "createdAt"),
            CompletedAt = GetStringOrNull(item, "completedAt"),
            ExpiresAt = GetStringOrNull(item, "expiresAt"),
            RefundPending = IsRefundPending(item),
            RefundReason = GetStringOrNull(item, "refundReason")
        };
    }

    // ──────────────────────────────────────────────
    // Response builders (for idempotent duplicate returns)
    // ──────────────────────────────────────────────

    private static InitiateEscrowResponse BuildInitiateResponse(
        Dictionary<string, object> escrow, bool isDuplicate = false)
    {
        return new InitiateEscrowResponse
        {
            EscrowId = GetString(escrow, "escrowId"),
            DeliveryId = GetString(escrow, "deliveryId"),
            OrderId = GetStringOrNull(escrow, "orderId"),
            BuyerId = GetString(escrow, "buyerId"),
            FarmerId = GetString(escrow, "farmerId"),
            Amount = GetDecimal(escrow, "amount"),
            Commission = GetDecimal(escrow, "commission"),
            FarmerPayout = GetDecimal(escrow, "farmerPayout"),
            Currency = GetString(escrow, "currency"),
            Status = GetString(escrow, "status"),
            TelebirrRef = GetStringOrNull(escrow, "telebirrRef"),
            CreatedAt = GetString(escrow, "createdAt"),
            ExpiresAt = GetString(escrow, "expiresAt"),
            Message = isDuplicate ? "Duplicate request. Returning cached result." : null,
            IsDuplicate = isDuplicate
        };
    }

    private static ReleaseEscrowResponse BuildReleaseResponse(Dictionary<string, object> escrow)
    {
        return new ReleaseEscrowResponse
        {
            EscrowId = GetString(escrow, "escrowId"),
            Status = GetString(escrow, "status"),
            FarmerPayout = GetDecimal(escrow, "farmerPayout"),
            TruckerPayout = GetDecimalOrNull(escrow, "truckerPayout"),
            Commission = GetDecimal(escrow, "commission"),
            TelebirrRef = GetStringOrNull(escrow, "telebirrReleaseRef"),
            TruckerTelebirrRef = GetStringOrNull(escrow, "truckerTelebirrRef"),
            ReleasedAt = GetStringOrNull(escrow, "releasedAt") ?? GetString(escrow, "updatedAt"),
            Message = "Duplicate request. Returning cached result."
        };
    }

    private static DisputeEscrowResponse BuildDisputeResponse(Dictionary<string, object> escrow)
    {
        return new DisputeEscrowResponse
        {
            EscrowId = GetString(escrow, "escrowId"),
            Status = GetString(escrow, "status"),
            DisputeId = GetString(escrow, "disputeId"),
            DisputedBy = GetString(escrow, "disputedBy"),
            Reason = GetString(escrow, "disputeReason"),
            DisputeWindowEndsAt = GetString(escrow, "disputeWindowEndsAt"),
            CreatedAt = GetString(escrow, "disputeCreatedAt"),
            Message = "Duplicate request. Returning cached result."
        };
    }

    private static RefundEscrowResponse BuildRefundResponse(Dictionary<string, object> escrow)
    {
        return new RefundEscrowResponse
        {
            EscrowId = GetString(escrow, "escrowId"),
            Status = GetString(escrow, "status"),
            RefundAmount = GetDecimal(escrow, "amount"),
            TelebirrRef = GetStringOrNull(escrow, "telebirrRefundRef"),
            RefundedAt = GetStringOrNull(escrow, "refundedAt") ?? GetString(escrow, "updatedAt"),
            Message = "Duplicate request. Returning cached result."
        };
    }

    // ──────────────────────────────────────────────
    // Audit helper
    // ──────────────────────────────────────────────

    private async Task AppendAuditAsync(
        string escrowId, string action, string fromState, string toState,
        string actor, string? telebirrRef = null, string? metadata = null)
    {
        // ESC-13: a failed settlement leg is logged before the audit write, so the money
        // alarm fires even when the audit write itself fails. The code decides the outcome
        // (IsOutcomeUnknownCode): after a timeout, a lost or unreadable answer or a gateway call
        // that threw, Telebirr may still have paid, and so it may after any manual payout queue
        // write that failed except one for PAYOUT_NOT_SUPPORTED.
        if (MoneyAlarmLog.IsSettlementFailureAuditAction(action))
        {
            var outcome = IsOutcomeUnknownCode(metadata)
                ? MoneyAlarmLog.OutcomeUnknown
                : MoneyAlarmLog.OutcomeFailed;
            AlarmLog(MoneyAlarmLog.SettlementFailed(escrowId, action, outcome, metadata));
        }

        await _auditRepo.AppendAsync(BuildAuditEntry(
            $"audit-{Guid.NewGuid():N}", DateTime.UtcNow.ToString("o"),
            escrowId, action, fromState, toState, actor, telebirrRef, metadata));
    }

    /// <summary>
    /// ESC-02: write an audit row at a deterministic key (<paramref name="timestamp"/> comes
    /// from the escrow row, for example a leg's paidAt), only if it is not there yet. A replay
    /// after a crash between the money write and its audit row fills the gap without a duplicate.
    /// Returns true when this call wrote the row (ESC-10).
    /// </summary>
    private async Task<bool> EnsureAuditAsync(
        string escrowId, string timestamp, string action, string fromState, string toState,
        string actor, string? telebirrRef = null, string? metadata = null)
    {
        return await _auditRepo.AppendIfAbsentAsync(BuildAuditEntry(
            EscrowAuditRepository.DeterministicAuditId(action, timestamp), timestamp,
            escrowId, action, fromState, toState, actor, telebirrRef, metadata));
    }

    private Dictionary<string, object> BuildAuditEntry(
        string auditId, string timestamp, string escrowId, string action, string fromState,
        string toState, string actor, string? telebirrRef, string? metadata)
    {
        var auditEntry = new Dictionary<string, object>
        {
            { "auditId", auditId },
            { "escrowId", escrowId },
            { "action", action },
            { "fromState", fromState },
            { "toState", toState },
            { "actor", actor },
            { "timestamp", timestamp }
        };

        if (telebirrRef != null)
        {
            auditEntry["telebirrRef"] = telebirrRef;
        }

        if (metadata != null)
        {
            auditEntry["metadata"] = metadata;
        }

        // Record the "where" (source IP) + correlation id when the caller supplied
        // request context. Previously the escrow-audit ipAddress field was declared
        // and read by the admin API but never written by any writer.
        if (!string.IsNullOrEmpty(_auditContext?.IpAddress))
        {
            auditEntry["ipAddress"] = _auditContext!.IpAddress!;
        }

        if (!string.IsNullOrEmpty(_auditContext?.CorrelationId))
        {
            auditEntry["correlationId"] = _auditContext!.CorrelationId!;
        }

        return auditEntry;
    }

    // ──────────────────────────────────────────────
    // Authorization helpers
    // ──────────────────────────────────────────────

    /// <summary>
    /// Payout destination for a party: the registered phone from the profile.
    /// Without a user repository (unit tests, misconfiguration) the user id is
    /// passed through as before. With one, the party must exist and be
    /// phone-verified, and Fayda-verified when the escrow amount exceeds the NBE
    /// threshold; otherwise ESCROW_029 blocks the payout, with the rule that blocked it
    /// (ESC-10 audits it once per rule).
    /// </summary>
    private async Task<(EscrowResult<string> Destination, string? BlockReason)> ResolvePayoutAsync(
        string? userId, decimal amount, string legLabel)
    {
        if (_userRepo == null)
        {
            return (EscrowResult<string>.Success(userId ?? ""), null);
        }

        var user = string.IsNullOrEmpty(userId) ? null : await _userRepo.GetUserByIdAsync(userId);
        var destination = CheckPayoutEligibility(user, amount, legLabel);
        return (destination, destination.IsSuccess ? null : PayoutBlockReason(user, amount));
    }

    /// <summary>The identity rules behind <see cref="ResolvePayoutAsync"/>, also applied to manual payouts (ESC-02).</summary>
    private static EscrowResult<string> CheckPayoutEligibility(UserDto? user, decimal amount, string legLabel)
        => PayoutBlockReason(user, amount) switch
        {
            TruckerPayoutFields.BlockProfileMissing => EscrowResult<string>.Failure(422, "ESCROW_029",
                $"Payout blocked: {legLabel} profile not found."),
            TruckerPayoutFields.BlockPhoneUnverified => EscrowResult<string>.Failure(403, "ESCROW_029",
                $"Payout blocked: the {legLabel}'s phone number is not verified."),
            TruckerPayoutFields.BlockFaydaRequired => EscrowResult<string>.Failure(403, "ESCROW_029",
                $"Payout blocked: the {legLabel} must verify their Fayda ID for amounts above 10,000 ETB."),
            _ => EscrowResult<string>.Success(user!.PhoneNumber)
        };

    /// <summary>
    /// Which identity rule blocks a payout to <paramref name="user"/>, or null when none does:
    /// a profile, a verified phone and, above the NBE threshold, a verified Fayda. ESC-04 keeps the
    /// value on a pending trucker leg and in its audit row.
    /// </summary>
    private static string? PayoutBlockReason(UserDto? user, decimal amount)
    {
        if (user == null)
        {
            return TruckerPayoutFields.BlockProfileMissing;
        }

        if (!user.IsPhoneVerified)
        {
            return TruckerPayoutFields.BlockPhoneUnverified;
        }

        return amount > FaydaThreshold && !user.FaydaVerified ? TruckerPayoutFields.BlockFaydaRequired : null;
    }

    /// <summary>
    /// ESC-04: the trucker's payout destination with the reason it is blocked (null when it is
    /// not). Without a user repository the id passes through, as <see cref="ResolvePayoutAsync"/>
    /// does for the release.
    /// </summary>
    private async Task<(EscrowResult<string> Destination, string? BlockReason)> ResolveTruckerPayeeAsync(
        string truckerId, decimal amount)
    {
        if (_userRepo == null)
        {
            return (EscrowResult<string>.Success(truckerId), null);
        }

        var trucker = string.IsNullOrEmpty(truckerId) ? null : await _userRepo.GetUserByIdAsync(truckerId);
        var destination = CheckPayoutEligibility(trucker, amount, "trucker");
        var reason = destination.IsSuccess
            ? null
            : string.IsNullOrEmpty(truckerId) ? TruckerPayoutFields.BlockNoTrucker : PayoutBlockReason(trucker, amount);
        return (destination, reason);
    }

    /// <summary>
    /// Refunds go back to the buyer's registered phone (no KYC gate: it is their own money).
    /// The order checkout uses the same phone for the Telebirr payment (ESC-01).
    /// </summary>
    private async Task<string> ResolveRefundPhoneAsync(string buyerId)
    {
        if (_userRepo == null || string.IsNullOrEmpty(buyerId))
        {
            return buyerId;
        }

        var buyer = await _userRepo.GetUserByIdAsync(buyerId);
        return buyer?.PhoneNumber ?? buyerId;
    }

    /// <summary>
    /// The system actor (cron), or an admin. Admin-pool users are not in the users
    /// table: the caller verifies their cognito:groups membership at the API layer
    /// and passes adminGroupVerified. A marketplace user claiming "admin" must also
    /// hold the admin role on their profile row (fail closed without a repository).
    /// </summary>
    private async Task<bool> IsSystemOrAdminAsync(string userId, string? role, bool adminGroupVerified = false)
    {
        if (string.Equals(userId, SystemActor, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (adminGroupVerified)
        {
            return true;
        }

        if (_userRepo == null)
        {
            return false; // fail-closed
        }

        var user = await _userRepo.GetUserByIdAsync(userId);
        return user != null
            && string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────
    // Validation helpers
    // ──────────────────────────────────────────────

    /// <summary>Round a monetary amount to 2 decimal places (santim), away from zero.</summary>
    private static decimal RoundMoney(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static bool IsValidIdempotencyKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var parts = key.Split(':');
        return parts.Length == 4
            && !string.IsNullOrWhiteSpace(parts[0])
            && !string.IsNullOrWhiteSpace(parts[1])
            && !string.IsNullOrWhiteSpace(parts[2])
            && long.TryParse(parts[3], out _);
    }

    // ──────────────────────────────────────────────
    // Dictionary accessor helpers (DynamoDB item → typed value)
    // ──────────────────────────────────────────────

    /// <summary>
    /// True for the record created at order funding (source=order, deterministic
    /// escrow-order-{orderId} id), as opposed to an escrow initiated for a delivery.
    /// </summary>
    internal static bool IsOrderFundedEscrow(Dictionary<string, object> escrow)
    {
        if (string.Equals(GetStringOrNull(escrow, "source"), "order", StringComparison.Ordinal))
        {
            return true;
        }

        return GetStringOrNull(escrow, "escrowId")?.StartsWith("escrow-order-", StringComparison.Ordinal) == true;
    }

    private static string GetString(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value.ToString() ?? "" : "";

    private static string? GetStringOrNull(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value.ToString() : null;

    private static int GetInt(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var value))
        {
            return 0;
        }

        return value switch
        {
            int i => i,
            long l => (int)l,
            string s => int.TryParse(s, out var result) ? result : 0,
            _ => 0
        };
    }

    private static decimal GetDecimal(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var value))
        {
            return 0m;
        }

        return value switch
        {
            decimal d => d,
            double dbl => (decimal)dbl,
            float f => (decimal)f,
            int i => i,
            long l => l,
            string s => decimal.TryParse(s, NumberStyles.Number,
                CultureInfo.InvariantCulture, out var result)
                ? result
                : 0m,
            _ => 0m
        };
    }

    private static decimal? GetDecimalOrNull(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            decimal d => d,
            double dbl => (decimal)dbl,
            float f => (decimal)f,
            int i => i,
            long l => l,
            string s => decimal.TryParse(s, NumberStyles.Number,
                CultureInfo.InvariantCulture, out var result)
                ? result
                : null,
            _ => null
        };
    }

    private static bool GetBool(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var value))
        {
            return false;
        }

        return value switch
        {
            bool b => b,
            string s => string.Equals(s, "true", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}

/// <summary>What <see cref="EscrowService.FreezeForDeliveryDisputeAsync"/> did with the escrow behind a disputed delivery.</summary>
public enum DeliveryDisputeFreeze
{
    /// <summary>The escrow moved to <c>disputed</c>.</summary>
    Frozen,

    /// <summary>ESC-07: the escrow was <c>disputed</c> already (no manual payout, no lock); the delivery and order may follow.</summary>
    AlreadyDisputed,

    /// <summary>ESC-07: no escrow for the delivery or its order.</summary>
    NoEscrow,

    /// <summary>ESC-02: the settlement waits in the manual payout queue; nothing was frozen and the delivery and order must not move either.</summary>
    RefusedManualPayout,

    /// <summary>ESC-03: a release or refund holds the settlement lock; nothing was frozen and the delivery and order must not move either.</summary>
    RefusedSettlementInFlight,

    /// <summary>ESC-07: the escrow holds no money a dispute can freeze (any status but <c>escrowed</c> and <c>disputed</c>).</summary>
    RefusedNotHeld,

    /// <summary>ESC-07: the order escrow is linked to another delivery.</summary>
    RefusedOtherDelivery
}

/// <summary>ESC-07: the outcome of a delivery dispute's freeze, with the escrow it was decided on.</summary>
public sealed record DeliveryDisputeFreezeResult(DeliveryDisputeFreeze Outcome, string? EscrowId = null, string? EscrowStatus = null)
{
    /// <summary>True when the delivery and order may follow: the escrow is frozen now or was already.</summary>
    public bool MayMirror => Outcome is DeliveryDisputeFreeze.Frozen or DeliveryDisputeFreeze.AlreadyDisputed;
}

/// <summary>What <see cref="EscrowService.PrepareOrderEscrowForCancelAsync"/> found for an order's money (ESC-06).</summary>
public enum OrderEscrowCancelReadiness
{
    /// <summary>The money is held in a plain <c>escrowed</c> record that a scheduled refund can pay back.</summary>
    Ready,

    /// <summary>A settlement, a manual payout, a dispute or a confirmed delivery owns the money: refuse the cancel.</summary>
    Busy,

    /// <summary>No record could be found or created right now: nothing may be written; the client retries.</summary>
    Unavailable
}

/// <summary>What <see cref="EscrowService.ReconcileOrderMoneyAsync"/> did with one order (ESC-09).</summary>
public enum OrderMoneySweepOutcome
{
    /// <summary>Nothing to do now: the money has not moved yet, or the order already shows it.</summary>
    Waiting,

    /// <summary>The order was moved (to delivered or disbursing).</summary>
    Moved,

    /// <summary>The order was moved to settled.</summary>
    Settled,

    /// <summary>The order agrees with the money without showing it (a flagged override, a closed order); marked.</summary>
    Consistent,

    /// <summary>The order cannot follow the money, or is ahead of it; alarmed once, marked.</summary>
    Mismatch,

    /// <summary>No escrow for the order; marked.</summary>
    NoEscrow,

    /// <summary>The order could follow but a read or write failed; retried next run.</summary>
    Failed
}
