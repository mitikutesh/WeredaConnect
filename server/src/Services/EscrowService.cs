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
        "TELEBIRR_TIMEOUT", "TELEBIRR_NETWORK", "TELEBIRR_PREORDER"
    };

    /// <summary>
    /// ESC-13: the gateway reported a payout without a reference and the manual payout queue
    /// write failed. The money may have moved, so the alarm line says <c>outcome=unknown</c>.
    /// </summary>
    private const string QueueFailedRefMissingCode =
        ManualPayoutFields.QueueFailedCodePrefix + ManualPayoutFields.ReasonGatewayRefMissing;

    /// <summary>
    /// System actor ID used by cron jobs and automated operations.
    /// </summary>
    internal const string SystemActor = "system";

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
            if (_deliveryRepo == null)
            {
                return false;
            }

            var linkedDelivery = await _deliveryRepo.GetByIdAsync(linkedDeliveryId);
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
        var farmerId = GetString(escrowItem, "farmerId");
        var payout = await ResolvePayoutAsync(farmerId, GetDecimal(escrowItem, "amount"), "farmer");
        if (!payout.IsSuccess)
        {
            await AppendAuditAsync(request.EscrowId, "release_blocked_kyc", currentStatus, currentStatus,
                authenticatedUserId, metadata: payout.Error!.Code);
            return EscrowResult<ReleaseEscrowResponse>.Failure(
                payout.Error.HttpStatus, payout.Error.Code, payout.Error.Message);
        }
        var farmerPhone = payout.Value!;

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

        // Record idempotency for the cached-duplicate return on a same-key replay
        // (best-effort; the settlement lock — not this write — is the concurrency guard).
        await _idempotencyRepo.StoreAsync(request.IdempotencyKey, request.EscrowId, "processing");

        // 6. Call Telebirr ReleaseFunds with the farmer payout amount (only the lock winner reaches here).
        var releaseResponse = await _telebirrGateway.ReleaseFundsAsync(new ReleaseFundsRequest
        {
            TransactionRef = $"release:{request.EscrowId}",
            HoldRef = holdRef,
            FarmerPhone = farmerPhone,
            Amount = farmerPayout,
            PlatformCommission = commission
        });

        var truckerPayout = GetDecimalOrNull(escrowItem, "truckerPayout");
        var truckerPayoutAmount = truckerPayout ?? 0m;
        var hasTruckerLeg = truckerPayoutAmount > 0;
        const int maxReleaseWriteAttempts = 4;

        // 6a. ESC-02: a gateway that cannot pay out, or reports a payout without a reference,
        //     sends the whole settlement to the manual payout queue. Nothing has moved, the
        //     escrow keeps its status, and the settlement lock stays held on purpose so no
        //     other release or refund can run until an admin has paid every leg.
        var farmerManualReason = ManualPayoutReason(
            releaseResponse.Success, releaseResponse.ErrorCode, releaseResponse.TelebirrRef);
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

            if (await TryQueueManualPayoutAsync(request.EscrowId, createdAt, ManualPayoutFields.OpRelease,
                    farmerManualReason, currentStatus, legs, authenticatedUserId))
            {
                await _idempotencyRepo.UpdateAsync(request.IdempotencyKey, "manual_payout");
                return ManualPayoutWaiting<ReleaseEscrowResponse>();
            }

            // The queue write failed and nothing moved: today's retryable failure path.
            await AppendAuditAsync(request.EscrowId, "release_failed", currentStatus, currentStatus,
                authenticatedUserId, metadata: ManualPayoutFields.QueueFailedCodePrefix + farmerManualReason);
            await _escrowRepo.ReleaseSettlementLockAsync(request.EscrowId, createdAt);
            await _idempotencyRepo.DeleteAsync(request.IdempotencyKey);
            return EscrowResult<ReleaseEscrowResponse>.Failure(
                502, "ESCROW_013",
                "The payout could not be queued for manual settlement. It is retried by the next sweep.");
        }

        if (!releaseResponse.Success)
        {
            await AppendAuditAsync(request.EscrowId, "release_failed", currentStatus, currentStatus,
                authenticatedUserId, metadata: releaseResponse.ErrorCode);
            // Release the lock + idempotency record so a transient Telebirr failure is retryable.
            await _escrowRepo.ReleaseSettlementLockAsync(request.EscrowId, createdAt);
            await _idempotencyRepo.DeleteAsync(request.IdempotencyKey);
            return EscrowResult<ReleaseEscrowResponse>.Failure(
                502, "ESCROW_013",
                releaseResponse.ErrorMessage ?? "Telebirr gateway error during fund release.");
        }

        // 5b. 3-way split: If trucker payout exists, release transport cost to trucker.
        //     Stable per-escrow ref so Telebirr dedupes the trucker leg on retry.
        string? truckerReleaseRef = null;
        var truckerReleasePending = false;
        if (hasTruckerLeg)
        {
            var truckerIdValue = GetStringOrNull(escrowItem, "truckerId") ?? "";
            var truckerPayoutDest = await ResolvePayoutAsync(truckerIdValue, truckerPayoutAmount, "trucker");
            var truckerRelease = truckerPayoutDest.IsSuccess
                ? await _telebirrGateway.ReleaseFundsAsync(new ReleaseFundsRequest
                {
                    TransactionRef = $"release:{request.EscrowId}:trucker",
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
            if (truckerRelease.Success && truckerManualReason == null)
            {
                truckerReleaseRef = truckerRelease.TelebirrRef;
                await AppendAuditAsync(request.EscrowId, "trucker_release", currentStatus, currentStatus,
                    "system", truckerRelease.TelebirrRef);
            }
            else
            {
                // ESC-02: the farmer is paid but the gateway could not pay the trucker (or gave
                // no reference). Queue the trucker leg with the farmer leg already closed by the
                // gateway. Money has moved, so the queue write is retried like the released
                // write below; if it still fails, fall back to today's truckerPayoutPending
                // completion instead of stranding the escrow under a held lock. An unverified
                // trucker (no gateway call) keeps the truckerPayoutPending behaviour (ESC-04).
                if (truckerManualReason != null)
                {
                    var legs = new List<ManualPayoutLegSeed>
                    {
                        new(ManualPayoutFields.LegFarmer, ManualPayoutFields.LegPaid, releaseResponse.TelebirrRef,
                            ManualPayoutFields.PaidByGateway, DateTime.UtcNow.ToString("o")),
                        new(ManualPayoutFields.LegTrucker, ManualPayoutFields.LegPending, null, null, null)
                    };
                    for (var attempt = 1; attempt <= maxReleaseWriteAttempts; attempt++)
                    {
                        if (await TryQueueManualPayoutAsync(request.EscrowId, createdAt, ManualPayoutFields.OpRelease,
                                truckerManualReason, currentStatus, legs, authenticatedUserId))
                        {
                            await _idempotencyRepo.UpdateAsync(request.IdempotencyKey, "manual_payout");
                            return ManualPayoutWaiting<ReleaseEscrowResponse>();
                        }

                        if (attempt < maxReleaseWriteAttempts)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt));
                        }
                    }

                    truckerRelease = new ReleaseFundsResponse
                    {
                        Success = false,
                        ErrorCode = ManualPayoutFields.QueueFailedCodePrefix + truckerManualReason
                    };
                }

                // Do NOT silently swallow: audit the failure and mark the payout as owed so
                // reconciliation/admin can retry. The farmer leg still succeeds (funds already moved).
                truckerReleasePending = true;
                await AppendAuditAsync(request.EscrowId, "trucker_release_failed", currentStatus, currentStatus,
                    "system", metadata: truckerRelease.ErrorCode);
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
            releaseFields["truckerPayoutPending"] = true;
        }

        // We hold the settlement lock, so the status is still `currentStatus`; this CAS
        // therefore succeeds. Funds have ALREADY moved (Telebirr disbursed above), so the
        // escrow MUST advance to 'released' — a transient failure here would strand it in
        // 'escrowed'/'disputed' with the settlement lock held and no recovery path (a retry
        // would 409 on the lock). Retry transient failures; a CAS failure (state already
        // moved) is NOT retried and is allowed to surface. Re-disbursement is not a risk:
        // the stable Telebirr ref (release:{escrowId}) dedupes, and this write does not call
        // Telebirr.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _escrowRepo.UpdateStatusAsync(request.EscrowId, createdAt, "released", currentStatus,
                    releaseFields);
                break;
            }
            catch (ConditionalCheckFailedException)
            {
                throw;
            }
            catch (Exception) when (attempt < maxReleaseWriteAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt));
            }
        }

        // 7. Audit log
        await AppendAuditAsync(request.EscrowId, "release", currentStatus, "released",
            authenticatedUserId, releaseResponse.TelebirrRef);

        // 8. Transition to "completed" (optimistic concurrency on "released")
        await _escrowRepo.UpdateStatusAsync(request.EscrowId, createdAt, "completed", "released",
            new Dictionary<string, object>
            {
                { "completedAt", now },
                { "version", currentVersion + 2 }
            });

        // 9. Audit log
        await AppendAuditAsync(request.EscrowId, "complete", "released", "completed", "system");

        await _idempotencyRepo.UpdateAsync(request.IdempotencyKey, "completed");

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
        var currentVersion = GetInt(escrowItem, "version");

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
            { "disputeCreatedAt", now.ToString("o") },
            { "disputeWindowEndsAt", disputeWindowEndsAt },
            { "version", currentVersion + 1 }
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
            var latest = await _escrowRepo.GetByIdAsync(request.EscrowId, consistentRead: true);
            if (latest != null && IsManualPayoutOpen(latest))
            {
                await _idempotencyRepo.DeleteAsync(request.IdempotencyKey);
                return DisputeRefusedManualPayout();
            }

            // ESC-03: a settlement holds the lock; it wins, and the dispute is refused.
            if (latest != null && GetStringOrNull(latest, "settlementLock") != null)
            {
                await _idempotencyRepo.DeleteAsync(request.IdempotencyKey);
                return EscrowResult<DisputeEscrowResponse>.Failure(
                    409, "ESCROW_INVALID_TRANSITION", "A settlement is in progress for this escrow; it can no longer be disputed.");
            }

            throw;
        }

        // 10. Audit log
        await AppendAuditAsync(request.EscrowId, "dispute", "escrowed", "disputed", authenticatedUserId);

        // SIM-12: a dispute freezes the whole trade, not just the money. Mirror it onto
        // the order (delivered -> disputed, which blocks ratings) and the delivery.
        await TryPropagateDisputeAsync(escrowItem, request.EscrowId, request.DisputedBy, request.Reason);

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
    /// SIM-12: mirror an escrow dispute onto the linked delivery (delivered -> disputed)
    /// and order (delivered -> disputed). Best-effort: the dispute itself is already
    /// recorded and audited, so a propagation failure never surfaces as a failed dispute.
    /// The order id comes from the escrow record, or from the delivery when an older
    /// escrow carries none.
    /// SIM-47: the delivery and order legs run independently, so one failing leg never
    /// skips the other. The order write uses a consistent read and retries a version
    /// conflict (3 attempts in all); a missing order or one that is no longer
    /// <c>delivered</c> is nothing to do. A failed leg is logged as
    /// <c>DISPUTE_PROPAGATION_FAILED</c> and written to the escrow audit ledger as
    /// <c>dispute_propagation_failed</c> with <c>metadata = legs=&lt;leg&gt;:&lt;reason&gt;</c>
    /// so the admin timeline shows which leg failed and why.
    /// </summary>
    private async Task TryPropagateDisputeAsync(
        Dictionary<string, object> escrowItem, string escrowId, string disputedBy, string reason)
    {
        var deliveryId = GetStringOrNull(escrowItem, "deliveryId");
        var orderId = GetStringOrNull(escrowItem, "orderId");
        var failures = new List<string>();
        const int maxOrderAttempts = 3;

        if (_deliveryRepo != null && !string.IsNullOrEmpty(deliveryId))
        {
            try
            {
                if (string.IsNullOrEmpty(orderId))
                {
                    var delivery = await _deliveryRepo.GetByIdAsync(deliveryId);
                    orderId = delivery != null ? GetStringOrNull(delivery, "orderId") : null;
                }

                // False means the delivery is missing or not delivered: nothing to mirror.
                await _deliveryRepo.TryMarkDisputedAsync(deliveryId, disputedBy, reason);
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
                string? orderFailure = "conflict";
                for (var attempt = 0; attempt < maxOrderAttempts; attempt++)
                {
                    var order = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
                    if (order == null || GetString(order, "status") != "delivered")
                    {
                        // Missing, not delivered yet, or already moved on (for example a
                        // concurrent writer marked it disputed): nothing to mirror.
                        orderFailure = null;
                        break;
                    }

                    var update = await _orderRepo.UpdateAsync(
                        orderId, new Dictionary<string, object> { { "status", "disputed" } }, GetInt(order, "version"));
                    if (update.IsSuccess)
                    {
                        orderFailure = null;
                        break;
                    }

                    if (update.Error?.HttpStatus != 409)
                    {
                        orderFailure = update.Error?.Code ?? "unknown";
                        break;
                    }
                }

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
            return;
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
    /// SIM-12: freeze the escrow behind a delivery that was disputed through sync. Same
    /// status write and audit entry as <see cref="DisputeAsync"/>, but no idempotency
    /// record (the sync operation is already idempotent) and no window check (the
    /// delivery handler enforced the 48-hour window). The delivery's free-text reason is
    /// kept as the description under the <c>other</c> reason code. Returns
    /// <see cref="DeliveryDisputeFreeze.NothingToFreeze"/> when there is no escrow for the
    /// delivery or it is not <c>escrowed</c>, <see cref="DeliveryDisputeFreeze.RefusedManualPayout"/>
    /// when its settlement already waits in the manual payout queue (ESC-02), and
    /// <see cref="DeliveryDisputeFreeze.RefusedSettlementInFlight"/> when a release or refund holds
    /// the settlement lock (ESC-03): the settlement wins, and the caller leaves the order alone.
    /// </summary>
    public async Task<DeliveryDisputeFreeze> MarkDisputedFromDeliveryAsync(string deliveryId, string disputedBy, string? description)
    {
        var escrow = await _escrowRepo.GetByDeliveryIdAsync(deliveryId);
        if (escrow == null || GetString(escrow, "status") != "escrowed")
        {
            return DeliveryDisputeFreeze.NothingToFreeze;
        }

        var escrowId = GetString(escrow, "escrowId");
        var createdAt = GetString(escrow, "createdAt");

        // ESC-02: a queued manual payout is not frozen (the admin queue owns it). The refusal
        // is audited so the admin sees the delivery dispute on the escrow's timeline.
        if (IsManualPayoutOpen(escrow))
        {
            await AppendAuditAsync(escrowId, "dispute_refused_manual_payout", "escrowed", "escrowed", disputedBy,
                metadata: $"deliveryId={deliveryId};op={GetStringOrNull(escrow, ManualPayoutFields.Op)}");
            Console.WriteLine($"ESCROW_MANUAL_PAYOUT_DISPUTE_REFUSED escrowId={MoneyAlarmLog.LogId(escrowId)}");
            return DeliveryDisputeFreeze.RefusedManualPayout;
        }
        var now = DateTime.UtcNow;
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
            { "disputeCreatedAt", now.ToString("o") },
            { "disputeWindowEndsAt", disputeWindowEndsAt },
            { "version", GetInt(escrow, "version") + 1 }
        };
        if (!string.IsNullOrWhiteSpace(description))
        {
            fields["disputeDescription"] = description.Length > MaxDisputeDescriptionLength
                ? description[..MaxDisputeDescriptionLength]
                : description;
        }

        // The settlement lock is checked by the write itself (ESC-03), never on this
        // DeliveryIndex copy, which can be stale.
        try
        {
            await _escrowRepo.UpdateStatusAsync(escrowId, createdAt, "disputed", "escrowed", fields);
        }
        catch (ConditionalCheckFailedException)
        {
            // Lost a race: a settlement took the lock, or the payout was queued, meanwhile.
            var latest = await _escrowRepo.GetByIdAsync(escrowId, consistentRead: true);
            if (latest != null && IsManualPayoutOpen(latest))
            {
                await AppendAuditAsync(escrowId, "dispute_refused_manual_payout", "escrowed", "escrowed", disputedBy,
                    metadata: $"deliveryId={deliveryId};op={GetStringOrNull(latest, ManualPayoutFields.Op)}");
                return DeliveryDisputeFreeze.RefusedManualPayout;
            }

            if (latest != null && GetStringOrNull(latest, "settlementLock") != null)
            {
                return await RefuseFreezeSettlementInFlightAsync(escrowId, deliveryId, disputedBy, GetString(latest, "status"));
            }

            throw;
        }

        await AppendAuditAsync(escrowId, "dispute", "escrowed", "disputed", disputedBy);
        return DeliveryDisputeFreeze.Frozen;
    }

    /// <summary>
    /// ESC-03: a delivery dispute that lands while a release or refund holds the settlement lock
    /// is not frozen onto the escrow (the settlement wins). Audited so the admin sees the
    /// delivery dispute on the escrow's timeline.
    /// </summary>
    private async Task<DeliveryDisputeFreeze> RefuseFreezeSettlementInFlightAsync(
        string escrowId, string deliveryId, string disputedBy, string status)
    {
        await AppendAuditAsync(escrowId, "dispute_refused_settlement_in_flight", status, status, disputedBy,
            metadata: $"deliveryId={deliveryId}");
        Console.WriteLine($"ESCROW_DISPUTE_REFUSED_SETTLEMENT_IN_FLIGHT escrowId={MoneyAlarmLog.LogId(escrowId)}");
        return DeliveryDisputeFreeze.RefusedSettlementInFlight;
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

        var amount = GetDecimal(escrowItem, "amount");
        var holdRef = GetStringOrNull(escrowItem, "telebirrHoldRef") ?? "";
        var createdAt = GetString(escrowItem, "createdAt");
        var currentVersion = GetInt(escrowItem, "version");

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

        // Record idempotency for the cached-duplicate return (best-effort; the lock is the guard).
        await _idempotencyRepo.StoreAsync(request.IdempotencyKey, request.EscrowId, "processing");

        // 5. Call Telebirr RefundFunds with the full amount (only the lock winner reaches here).
        var refundResponse = await _telebirrGateway.RefundFundsAsync(new RefundFundsRequest
        {
            TransactionRef = $"refund:{request.EscrowId}",
            HoldRef = holdRef,
            BuyerPhone = await ResolveRefundPhoneAsync(GetString(escrowItem, "buyerId")),
            Amount = amount
        });

        // 5a. ESC-02: the gateway cannot refund (or gave no reference): queue the buyer leg for
        //     an admin, keep the lock, leave the status alone. See ReleaseAsync 6a.
        var refundManualReason = ManualPayoutReason(
            refundResponse.Success, refundResponse.ErrorCode, refundResponse.TelebirrRef);
        if (refundManualReason != null)
        {
            var legs = new List<ManualPayoutLegSeed>
            {
                new(ManualPayoutFields.LegBuyer, ManualPayoutFields.LegPending, null, null, null)
            };
            if (await TryQueueManualPayoutAsync(request.EscrowId, createdAt, ManualPayoutFields.OpRefund,
                    refundManualReason, currentStatus, legs, authenticatedUserId))
            {
                await _idempotencyRepo.UpdateAsync(request.IdempotencyKey, "manual_payout");
                return ManualPayoutWaiting<RefundEscrowResponse>();
            }

            await AppendAuditAsync(request.EscrowId, "refund_failed", currentStatus, currentStatus,
                authenticatedUserId, metadata: ManualPayoutFields.QueueFailedCodePrefix + refundManualReason);
            await _escrowRepo.ReleaseSettlementLockAsync(request.EscrowId, createdAt);
            await _idempotencyRepo.DeleteAsync(request.IdempotencyKey);
            return EscrowResult<RefundEscrowResponse>.Failure(
                502, "ESCROW_013",
                "The refund could not be queued for manual settlement. It is retried by the next sweep.");
        }

        if (!refundResponse.Success)
        {
            await AppendAuditAsync(request.EscrowId, "refund_failed", currentStatus, currentStatus,
                authenticatedUserId, metadata: refundResponse.ErrorCode);
            // Release the lock + idempotency record so a transient failure is retryable.
            await _escrowRepo.ReleaseSettlementLockAsync(request.EscrowId, createdAt);
            await _idempotencyRepo.DeleteAsync(request.IdempotencyKey);
            return EscrowResult<RefundEscrowResponse>.Failure(
                502, "ESCROW_013",
                refundResponse.ErrorMessage ?? "Telebirr gateway error during refund.");
        }

        var now = DateTime.UtcNow.ToString("o");

        // 6. Transition to "refunded". We hold the settlement lock so status is still
        //    `currentStatus`; a genuine CAS failure here is an anomaly and surfaces (500)
        //    rather than being masked — no funds were double-moved (lock guaranteed exclusivity).
        await _escrowRepo.UpdateStatusAsync(request.EscrowId, createdAt, "refunded", currentStatus,
            new Dictionary<string, object>
            {
                { "telebirrRefundRef", refundResponse.TelebirrRef ?? "" },
                { "refundedAt", now },
                { "version", currentVersion + 1 }
            });

        // 6. Audit log
        await AppendAuditAsync(request.EscrowId, "refund", currentStatus, "refunded",
            authenticatedUserId, refundResponse.TelebirrRef);

        await _idempotencyRepo.UpdateAsync(request.IdempotencyKey, "completed");

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
    /// way a queued escrow ever settles; the gateway is never called here.
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

            if (!ManualPayoutSettleableStatuses.Contains(currentStatus))
            {
                return await RefuseAsync(currentStatus,
                    409, "ESCROW_INVALID_TRANSITION",
                    $"Escrow is '{currentStatus}' and can no longer be settled from the queue.");
            }

            // The payee must still pass the identity gate the gateway release applies.
            var payeeId = GetString(escrow, ManualPayoutPayeeField(leg));
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
            if (newStatus != currentStatus)
            {
                await EnsureAuditAsync(escrowId, AddTicks(now, 1), op == ManualPayoutFields.OpRelease ? "release" : "refund",
                    currentStatus, newStatus, actorUserId, reference, "manual");
                finalStatus = newStatus == "released"
                    ? await FinishManualReleaseAsync(escrowId, createdAt, actorUserId)
                    : newStatus;
            }

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
        if (closingLeg != null)
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
            // Already completed by a parallel sweep or replay, which writes its own complete row.
            return "completed";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ESCROW_MANUAL_PAYOUT_COMPLETE_DEFERRED escrowId={MoneyAlarmLog.LogId(escrowId)} error={ex.GetType().Name}");
            return "released";
        }

        // The status is completed now, whatever happens to its audit row; a replay re-ensures
        // the row at the same key (completedAt).
        try
        {
            await EnsureAuditAsync(escrowId, completedAt, "complete", "released", "completed", actor, metadata: "manual");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ESCROW_MANUAL_PAYOUT_COMPLETE_AUDIT_DEFERRED escrowId={MoneyAlarmLog.LogId(escrowId)} error={ex.GetType().Name}");
        }

        return "completed";
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
            var amount = ManualPayoutLegAmount(item, leg);
            var payee = await GetUserCachedAsync(payeeId, userCache);
            var (verified, blockReason) = EvaluateManualPayee(item, leg, payee, amount);
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
                PaidAt = GetStringOrNull(item, ManualPayoutFields.PaidAtAttr(leg))
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
    /// counts as queued. On success writes the audit row and the log token.
    /// </summary>
    private async Task<bool> TryQueueManualPayoutAsync(
        string escrowId, string createdAt, string op, string reason, string fromStatus,
        List<ManualPayoutLegSeed> legs, string actor)
    {
        var queuedAt = DateTime.UtcNow.ToString("o");
        bool queued;
        try
        {
            queued = await _escrowRepo.TryQueueManualPayoutAsync(escrowId, createdAt, op, reason, fromStatus, legs, queuedAt);
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
    /// 202 ESCROW_034: the settlement is waiting in the manual payout queue; this call moved
    /// nothing. Returned to the cron and the admin settlement routes only.
    /// </summary>
    private static EscrowResult<T> ManualPayoutWaiting<T>() => EscrowResult<T>.Failure(
        202, "ESCROW_034",
        "This payout is waiting in the manual payout queue. An admin pays it in the Telebirr portal and records the reference.");

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
        // alarm fires even when the audit write itself fails. The gateway code decides the
        // outcome: after a timeout or a lost answer Telebirr may still have paid, and so may
        // it after a success without a reference whose manual payout queue write failed.
        if (MoneyAlarmLog.IsSettlementFailureAuditAction(action))
        {
            var outcome = metadata != null
                && (UncertainGatewayErrors.Contains(metadata) || metadata == QueueFailedRefMissingCode)
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
    /// </summary>
    private async Task EnsureAuditAsync(
        string escrowId, string timestamp, string action, string fromState, string toState,
        string actor, string? telebirrRef = null, string? metadata = null)
    {
        await _auditRepo.AppendIfAbsentAsync(BuildAuditEntry(
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
    /// threshold; otherwise ESCROW_029 blocks the payout.
    /// </summary>
    private async Task<EscrowResult<string>> ResolvePayoutAsync(string? userId, decimal amount, string legLabel)
    {
        if (_userRepo == null)
        {
            return EscrowResult<string>.Success(userId ?? "");
        }

        var user = string.IsNullOrEmpty(userId) ? null : await _userRepo.GetUserByIdAsync(userId);
        return CheckPayoutEligibility(user, amount, legLabel);
    }

    /// <summary>The identity rules behind <see cref="ResolvePayoutAsync"/>, also applied to manual payouts (ESC-02).</summary>
    private static EscrowResult<string> CheckPayoutEligibility(UserDto? user, decimal amount, string legLabel)
    {
        if (user == null)
        {
            return EscrowResult<string>.Failure(422, "ESCROW_029",
                $"Payout blocked: {legLabel} profile not found.");
        }

        if (!user.IsPhoneVerified)
        {
            return EscrowResult<string>.Failure(403, "ESCROW_029",
                $"Payout blocked: the {legLabel}'s phone number is not verified.");
        }

        if (amount > FaydaThreshold && !user.FaydaVerified)
        {
            return EscrowResult<string>.Failure(403, "ESCROW_029",
                $"Payout blocked: the {legLabel} must verify their Fayda ID for amounts above 10,000 ETB.");
        }

        return EscrowResult<string>.Success(user.PhoneNumber);
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

/// <summary>What <see cref="EscrowService.MarkDisputedFromDeliveryAsync"/> did with the escrow behind a disputed delivery.</summary>
public enum DeliveryDisputeFreeze
{
    /// <summary>The escrow moved to <c>disputed</c>.</summary>
    Frozen,

    /// <summary>No escrow for the delivery, or it is not <c>escrowed</c>.</summary>
    NothingToFreeze,

    /// <summary>ESC-02: the settlement waits in the manual payout queue; nothing was frozen and the order must not be mirrored either.</summary>
    RefusedManualPayout,

    /// <summary>ESC-03: a release or refund holds the settlement lock; nothing was frozen and the order must not be mirrored either.</summary>
    RefusedSettlementInFlight
}
