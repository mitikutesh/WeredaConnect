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
    private readonly IEscrowIdempotencyRepository _idempotencyRepo;
    private readonly ITelebirrGateway _telebirrGateway;
    private readonly IUserRepository? _userRepo;
    private readonly IOrderRepository? _orderRepo;
    private readonly IDeliveryRepository? _deliveryRepo;

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

    public EscrowService(
        IEscrowRepository escrowRepo,
        IEscrowAuditRepository auditRepo,
        IEscrowIdempotencyRepository idempotencyRepo,
        ITelebirrGateway telebirrGateway,
        IUserRepository? userRepo = null,
        IOrderRepository? orderRepo = null,
        IDeliveryRepository? deliveryRepo = null)
    {
        _escrowRepo = escrowRepo ?? throw new ArgumentNullException(nameof(escrowRepo));
        _auditRepo = auditRepo ?? throw new ArgumentNullException(nameof(auditRepo));
        _idempotencyRepo = idempotencyRepo ?? throw new ArgumentNullException(nameof(idempotencyRepo));
        _telebirrGateway = telebirrGateway ?? throw new ArgumentNullException(nameof(telebirrGateway));
        _userRepo = userRepo;
        _orderRepo = orderRepo;
        _deliveryRepo = deliveryRepo;
    }

    // ──────────────────────────────────────────────
    // Public API
    // ──────────────────────────────────────────────

    /// <summary>
    /// Initiate escrow payment for a delivery. Holds funds in buyer's Telebirr account.
    /// </summary>
    public async Task<EscrowResult<InitiateEscrowResponse>> InitiateAsync(
        InitiateEscrowRequest request, string authenticatedUserId)
    {
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

        // 6b. Check for existing active escrow on this delivery → ESCROW_009
        var existingEscrowForDelivery = await _escrowRepo.GetByDeliveryIdAsync(request.DeliveryId);
        if (existingEscrowForDelivery != null)
        {
            var existingStatus = existingEscrowForDelivery.TryGetValue("status", out var statusObj)
                ? statusObj?.ToString() : null;
            // Only block if escrow is in an active state (not refunded/completed/expired)
            if (existingStatus is "initiated" or "escrowed" or "released" or "disputed")
            {
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
        if (_deliveryRepo != null)
        {
            var delivery = await _deliveryRepo.GetByIdAsync(request.DeliveryId);
            if (delivery == null)
            {
                return EscrowResult<InitiateEscrowResponse>.Failure(
                    404, "ESCROW_008", "Delivery not found.", "deliveryId");
            }

            var deliveryStatus = delivery.TryGetValue("status", out var ds) ? ds?.ToString() : null;
            if (deliveryStatus == null || !EscrowableDeliveryStates.Contains(deliveryStatus))
            {
                return EscrowResult<InitiateEscrowResponse>.Failure(
                    422, "ESCROW_010",
                    $"Delivery is not in a valid state for payment (was '{deliveryStatus}').",
                    "deliveryId");
            }
        }

        // 7. User validation + Fayda ID verification for high-value transactions → ESCROW_007
        var buyerFaydaVerified = false;
        var farmerFaydaVerified = false;
        var requiresFayda = request.Amount > FaydaThreshold;

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

        // If orderId provided, read order record for 3-way split amounts
        decimal? orderCropCost = null;
        decimal? orderTransportCost = null;
        decimal? orderCommissionAmount = null;
        string? truckerId = null;
        if (!string.IsNullOrEmpty(request.OrderId) && _orderRepo != null)
        {
            var order = await _orderRepo.GetByIdAsync(request.OrderId);
            if (order != null)
            {
                // Ownership binding: the referenced order must belong to this buyer and delivery.
                if (order.TryGetValue("buyerId", out var ob) && ob?.ToString() is string obs
                    && !string.Equals(obs, request.BuyerId, StringComparison.Ordinal))
                {
                    return EscrowResult<InitiateEscrowResponse>.Failure(
                        403, "ESCROW_VALIDATION", "Referenced order does not belong to this buyer.", "orderId");
                }
                if (order.TryGetValue("deliveryId", out var od) && od?.ToString() is string ods
                    && !string.Equals(ods, request.DeliveryId, StringComparison.Ordinal))
                {
                    return EscrowResult<InitiateEscrowResponse>.Failure(
                        400, "ESCROW_VALIDATION", "Referenced order is for a different delivery.", "orderId");
                }

                if (order.TryGetValue("cropCost", out var cc))
                    orderCropCost = decimal.TryParse(cc?.ToString(), out var ccVal) ? ccVal : null;
                if (order.TryGetValue("transportCost", out var tc))
                    orderTransportCost = decimal.TryParse(tc?.ToString(), out var tcVal) ? tcVal : null;
                if (order.TryGetValue("commissionAmount", out var ca))
                    orderCommissionAmount = decimal.TryParse(ca?.ToString(), out var caVal) ? caVal : null;
                if (order.TryGetValue("truckerId", out var tid))
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

        if (!string.IsNullOrEmpty(request.OrderId))
            escrowRecord["orderId"] = request.OrderId;
        if (truckerPayout > 0)
            escrowRecord["truckerPayout"] = truckerPayout;
        if (!string.IsNullOrEmpty(truckerId))
            escrowRecord["truckerId"] = truckerId;

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
            BuyerPhone = request.BuyerId, // Real impl resolves phone from user profile
            Amount = request.Amount,
            Description = $"WeredaConnect escrow for delivery {request.DeliveryId}"
        });

        // 13. If hold succeeds: transition to "escrowed", audit log, update idempotency
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
    /// Release escrowed funds to the farmer. Transitions: escrowed/disputed → released → completed.
    /// Only callable by system (cron) or admin. Regular users cannot release.
    /// </summary>
    /// <param name="role">The caller's JWT role claim (e.g. "admin"). Pass null for system/cron calls.</param>
    public async Task<EscrowResult<ReleaseEscrowResponse>> ReleaseAsync(
        ReleaseEscrowRequest request, string authenticatedUserId, string? role = null)
    {
        // 1. Authorization: only system or admin can release funds → ESCROW_015
        if (!await IsSystemOrAdminAsync(authenticatedUserId, role))
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

        // 4. Validate state transition: must be "escrowed" or "disputed"
        var currentStatus = GetString(escrowItem, "status");
        if (currentStatus is not ("escrowed" or "disputed"))
        {
            return EscrowResult<ReleaseEscrowResponse>.Failure(
                422, "ESCROW_INVALID_TRANSITION",
                $"Cannot release escrow in '{currentStatus}' state. Must be 'escrowed' or 'disputed'.");
        }

        var farmerPayout = GetDecimal(escrowItem, "farmerPayout");
        var commission = GetDecimal(escrowItem, "commission");
        var holdRef = GetStringOrNull(escrowItem, "telebirrHoldRef") ?? "";
        var createdAt = GetString(escrowItem, "createdAt");
        var currentVersion = GetInt(escrowItem, "version");

        // 5. Acquire the settlement lock BEFORE the irreversible Telebirr call. This is the
        //    atomic mutual-exclusion guard: at most ONE settlement (release OR refund, across
        //    any idempotency keys) may move funds for this escrow. A caller that loses the race
        //    NEVER calls Telebirr, so a release-vs-refund or release-vs-release race cannot
        //    double-settle. (Fixes the earlier design where funds moved before the state CAS.)
        var lockAcquired = await _escrowRepo.TryAcquireSettlementLockAsync(
            request.EscrowId, createdAt, "release");
        if (!lockAcquired)
        {
            var latest = await _escrowRepo.GetByIdAsync(request.EscrowId);
            var latestStatus = latest != null ? GetString(latest, "status") : "";
            if (latest != null && latestStatus is "released" or "completed")
            {
                // A prior release already settled this escrow — safe to return the result.
                return EscrowResult<ReleaseEscrowResponse>.Duplicate(BuildReleaseResponse(latest));
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
            FarmerPhone = GetString(escrowItem, "farmerId"), // Real impl resolves phone
            Amount = farmerPayout,
            PlatformCommission = commission
        });

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
        var truckerPayout = GetDecimalOrNull(escrowItem, "truckerPayout");
        string? truckerReleaseRef = null;
        var truckerReleasePending = false;
        if (truckerPayout.HasValue && truckerPayout.Value > 0)
        {
            var truckerIdValue = GetStringOrNull(escrowItem, "truckerId") ?? "";
            var truckerRelease = await _telebirrGateway.ReleaseFundsAsync(new ReleaseFundsRequest
            {
                TransactionRef = $"release:{request.EscrowId}:trucker",
                HoldRef = holdRef,
                FarmerPhone = truckerIdValue, // Real impl resolves phone from truckerId
                Amount = truckerPayout.Value,
                PlatformCommission = 0m
            });

            if (truckerRelease.Success)
            {
                truckerReleaseRef = truckerRelease.TelebirrRef;
                await AppendAuditAsync(request.EscrowId, "trucker_release", currentStatus, currentStatus,
                    "system", truckerRelease.TelebirrRef);
            }
            else
            {
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
            releaseFields["truckerTelebirrRef"] = truckerReleaseRef;
        if (truckerReleasePending)
            releaseFields["truckerPayoutPending"] = true;

        // We hold the settlement lock, so the status is still `currentStatus`; this CAS
        // therefore succeeds. A genuine failure here is an anomaly and is allowed to surface
        // (500) rather than being masked as success — funds were NOT double-moved because the
        // lock guaranteed exclusivity before Telebirr.
        await _escrowRepo.UpdateStatusAsync(request.EscrowId, createdAt, "released", currentStatus,
            releaseFields);

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
        DisputeEscrowRequest request, string authenticatedUserId)
    {
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

        await _escrowRepo.UpdateStatusAsync(request.EscrowId, createdAt, "disputed", "escrowed", additionalFields);

        // 10. Audit log
        await AppendAuditAsync(request.EscrowId, "dispute", "escrowed", "disputed", authenticatedUserId);

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
    /// Refund escrowed funds to the buyer (full amount, no commission deducted).
    /// Only callable by system (cron) or admin. Regular users cannot refund.
    /// </summary>
    /// <param name="role">The caller's JWT role claim (e.g. "admin"). Pass null for system/cron calls.</param>
    public async Task<EscrowResult<RefundEscrowResponse>> RefundAsync(
        RefundEscrowRequest request, string authenticatedUserId, string? role = null)
    {
        // 1. Authorization: only system or admin can refund → ESCROW_021
        if (!await IsSystemOrAdminAsync(authenticatedUserId, role))
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

        // 4. Validate state: must be "escrowed" or "disputed" → ESCROW_INVALID_TRANSITION
        var currentStatus = GetString(escrowItem, "status");
        if (currentStatus is not ("escrowed" or "disputed"))
        {
            return EscrowResult<RefundEscrowResponse>.Failure(
                422, "ESCROW_INVALID_TRANSITION",
                $"Cannot refund escrow in '{currentStatus}' state. Must be 'escrowed' or 'disputed'.");
        }

        var amount = GetDecimal(escrowItem, "amount");
        var holdRef = GetStringOrNull(escrowItem, "telebirrHoldRef") ?? "";
        var createdAt = GetString(escrowItem, "createdAt");
        var currentVersion = GetInt(escrowItem, "version");

        // Acquire the settlement lock BEFORE the irreversible Telebirr call. Mutual exclusion
        // across release/refund and across idempotency keys: a refund that loses the race to a
        // release (or another refund) NEVER calls Telebirr, so funds cannot be double-moved.
        var lockAcquired = await _escrowRepo.TryAcquireSettlementLockAsync(
            request.EscrowId, createdAt, "refund");
        if (!lockAcquired)
        {
            var latest = await _escrowRepo.GetByIdAsync(request.EscrowId);
            var latestStatus = latest != null ? GetString(latest, "status") : "";
            if (latest != null && latestStatus == "refunded")
            {
                // A prior refund already settled this escrow — safe to return the result.
                return EscrowResult<RefundEscrowResponse>.Duplicate(BuildRefundResponse(latest));
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
            BuyerPhone = GetString(escrowItem, "buyerId"), // Real impl resolves phone
            Amount = amount
        });

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

        // 2. Validate user is buyer, farmer, or admin → ESCROW_022
        var buyerId = GetString(escrowItem, "buyerId");
        var farmerId = GetString(escrowItem, "farmerId");
        if (!string.Equals(authenticatedUserId, buyerId, StringComparison.Ordinal)
            && !string.Equals(authenticatedUserId, farmerId, StringComparison.Ordinal)
            && !await IsSystemOrAdminAsync(authenticatedUserId, role))
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

        // 2. Validate user is buyer, farmer, or admin → ESCROW_022
        var buyerId = GetString(escrowItem, "buyerId");
        var farmerId = GetString(escrowItem, "farmerId");
        if (!string.Equals(authenticatedUserId, buyerId, StringComparison.Ordinal)
            && !string.Equals(authenticatedUserId, farmerId, StringComparison.Ordinal)
            && !await IsSystemOrAdminAsync(authenticatedUserId, role))
        {
            return EscrowResult<EscrowDto>.Failure(
                403, "ESCROW_022", "You are not a party to this escrow.");
        }

        // 3. Map to EscrowDto
        return EscrowResult<EscrowDto>.Success(MapToEscrowDto(escrowItem));
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

        // 1. Query by buyer or farmer GSI based on role parameter
        List<Dictionary<string, object>> items;
        Dictionary<string, AttributeValue>? lastEvaluatedKey;

        if (string.Equals(role, "farmer", StringComparison.OrdinalIgnoreCase))
        {
            var result = await _escrowRepo.QueryByFarmerAsync(userId, status, limit, exclusiveStartKey);
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
        var isBuyer = !string.Equals(role, "farmer", StringComparison.OrdinalIgnoreCase);
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
            Version = GetInt(item, "version")
        };
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
            DeliveryId = GetString(item, "deliveryId"),
            CounterpartyName = counterpartyName,
            Amount = GetDecimal(item, "amount"),
            Commission = GetDecimal(item, "commission"),
            Currency = GetString(item, "currency"),
            Status = GetString(item, "status"),
            CreatedAt = GetString(item, "createdAt"),
            CompletedAt = GetStringOrNull(item, "completedAt")
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
        var auditEntry = new Dictionary<string, object>
        {
            { "auditId", $"audit-{Guid.NewGuid():N}" },
            { "escrowId", escrowId },
            { "action", action },
            { "fromState", fromState },
            { "toState", toState },
            { "actor", actor },
            { "timestamp", DateTime.UtcNow.ToString("o") }
        };

        if (telebirrRef != null)
        {
            auditEntry["telebirrRef"] = telebirrRef;
        }

        if (metadata != null)
        {
            auditEntry["metadata"] = metadata;
        }

        await _auditRepo.AppendAsync(auditEntry);
    }

    // ──────────────────────────────────────────────
    // Authorization helpers
    // ──────────────────────────────────────────────

    /// <summary>
    /// Check if the actor is the system cron or a verified admin user.
    /// Release and refund operations are restricted to system/admin.
    /// System actor ("system") is used by cron jobs and short-circuits (no DB lookup).
    /// Admin users must have the "admin" JWT role claim AND a matching Role in DynamoDB.
    /// Fails closed: if _userRepo is null or user is not found, returns false.
    /// </summary>
    private async Task<bool> IsSystemOrAdminAsync(string userId, string? role)
    {
        if (string.Equals(userId, SystemActor, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
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
