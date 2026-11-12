using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// POST /sync Lambda handler.
/// Processes a batch of offline-first sync operations from the mobile client.
///
/// Flow per operation:
/// 1. Check the idempotency key, scoped to the authenticated caller
///    (SyncIdempotencyKey): if the caller already sent it, return duplicate
/// 2. Route to entity handler based on entityType (crop_listing, etc.)
/// 3. Apply operation (create/update/delete) with version checking
/// 4. Store the caller-scoped idempotency key with 7-day TTL
/// 5. Return per-operation result (success/conflict/error)
///
/// Error codes:
/// - SYNC_SUCCESS: Operation applied successfully
/// - SYNC_CONFLICT: Client version behind server version
/// - SYNC_DUPLICATE: Idempotency key already processed
/// - SYNC_VALIDATION: Invalid operation data
/// - SYNC_SERVER_ERROR: Unexpected server failure
///
/// See: specs/contracts/sync-protocol.md
/// </summary>
public partial class SyncFunction
{
    private readonly ISyncMetadataRepository _syncMetadataRepo;
    private readonly ICropRepository _cropRepo;
    private readonly IOrderRepository _orderRepo;
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tripsTable;
    private readonly string _deliveriesTable;
    private readonly IRateLimitService? _rateLimitService;
    private readonly IFraudDetectionService? _fraudService;
    private readonly IUserRepository? _userRepo;

    // ESC-15: trip bookings (reserve, verify, release capacity) and the departure read by the
    // commitment deadlines. Raw trip reads elsewhere keep using GetTripItemAsync.
    private readonly ITripRepository _tripRepo;

    // auth_time of the current request's token (Lambda handles one request at a
    // time per instance, so a field is safe); consumed by the payment step-up gate.
    private long? _requestAuthTime;

    // FRD-04: the caller's risk score, read at most once per batch (null without a fraud
    // service). Set at the top of FunctionHandler, like the auth_time above.
    private RiskScoreMemo? _batchRiskScores;

    // FRD-08: the (user, signal type) pairs this batch already tried to write, so a batch of 50
    // refusals is one write, and the update and delete version conflicts it met so far.
    private readonly HashSet<string> _batchSignals = new(StringComparer.Ordinal);
    private int _batchStaleConflicts;

    /// <summary>FRD-08: update and delete version conflicts in one batch that make a <c>STALE_VERSION_BURST</c>.</summary>
    internal const int StaleVersionBurstThreshold = 10;

    // FRD-13: the payees' payout-delay classes, read at most once per batch (with _batchRiskScores).
    private PayoutDelayBatch? _batchPayoutDelays;
    private readonly IIntentRepository? _intentRepo;
    private readonly IMatchEventRepository? _matchEventRepo;
    // Optional: when the escrows table is provisioned, confirming a delivery
    // stamps the linked escrow so the auto-release cron can pay the farmer.
    private readonly IEscrowRepository? _escrowRepo;

    /// <summary>
    /// FRD-03: <c>FRAUD_HOLD_ENABLED</c>, as on escrow-cron. On, the farmer's accept of an order with an
    /// escrow is one transaction that checks the escrow is not held (<see cref="EscrowsTableName"/>).
    /// </summary>
    internal bool FraudHoldEnabled { get; init; }

    /// <summary>FRD-03: the escrows table the accept's transaction checks (<c>ESCROWS_TABLE</c>).</summary>
    internal string? EscrowsTableName { get; init; }

    /// <summary>
    /// FRD-13 (fraud-detection-architecture.md 17.5): classes the farmer and the trucker when a delivery is
    /// confirmed, so the stamp gives the escrow its <c>releaseNotBefore</c>. The Lambda wires it; without one (the
    /// test constructor) the stamp writes only the two fields it wrote before.
    /// </summary>
    internal PayoutDelayPolicy? PayoutDelay { get; init; }

    /// <summary>
    /// FRD-09 (fraud-detection-architecture.md 15.1): the transaction pattern rules, run after a payment, a
    /// committed cancel, a delivery dispute or a delivery confirm succeeded. The Lambda wires it when the
    /// signals table is set; without one (the test constructor) no rule runs and nothing is read.
    /// </summary>
    internal TransactionPatternService? PatternRules { get; init; }

    /// <summary>FRD-03: the farmer's accept of an order whose escrow is held for review (order-lifecycle 1.22.0).</summary>
    private const string OrderUnderReviewMessage = "This order is under review and cannot be accepted. Contact support.";
    // Optional: the order-funded escrow leg (escrow record at order funding,
    // delivery linkage, refund scheduling). Constructed WITHOUT Telebirr wiring —
    // these paths never move money themselves (escrow-protocol.md §Order-funded
    // escrow). Needs both ESCROWS_TABLE and ESCROW_AUDIT_TABLE; absent → null
    // (orders still fund; the escrow record simply isn't materialized here).
    private readonly EscrowService? _orderEscrowService;
    // ESC-01: the funding side effects (stock + order escrow) shared with the live
    // Telebirr webhook, so mock and live funding can never drift apart. Built on first use so
    // it sees CommitmentsEnabled, which an object initializer may set after the constructor.
    private OrderFundingService? _orderFundingInstance;
    private OrderFundingService _orderFunding
        => _orderFundingInstance ??= new OrderFundingService(
            _orderRepo, _cropRepo, _orderEscrowService, _tripRepo, CommitmentsEnabled, _transactionAuditRepo);

    /// <summary>
    /// ESC-15: sequential farmer → trucker commitments, the awaiting_transport status and the trip
    /// bookings. Defaults to the env flag (<see cref="OrderCommitment.IsEnabled"/>); tests set it.
    /// Off, the farmer's accept moves a funded order straight to accepted as before.
    /// </summary>
    public bool CommitmentsEnabled { get; init; } = OrderCommitment.IsEnabled;

    /// <summary>
    /// ESC-25: the minimum app build for order operations (<c>MIN_ORDER_APP_BUILD</c>, 0 = off), read
    /// on its own: the gate does not depend on <see cref="CommitmentsEnabled"/>. Tests set it.
    /// </summary>
    public int MinOrderAppBuild { get; init; } = OrderAppBuildGate.FromEnvironment();

    // ESC-25: the caller's X-App-Build (null: none readable), read once per batch at the top of
    // FunctionHandler, like _batchAudit below.
    private int? _batchAppBuild;
    // Optional: when the ratings table is provisioned, buyers can rate
    // counterparties after a delivered order. Absent env → null (rating sync
    // simply returns RATING_UNAVAILABLE).
    private readonly IRatingRepository? _ratingRepo;
    // Optional: when the messages table is provisioned, participants of a
    // marketplace context (crop/order/trip/delivery) can chat. Absent env → null
    // (message sync simply returns MESSAGE_UNAVAILABLE).
    private readonly IMessageRepository? _messageRepo;
    // Optional: unified transaction-audit trail for order/delivery lifecycle
    // transitions. Absent env → null (transitions still process; simply unlogged).
    private readonly ITransactionAuditRepository? _transactionAuditRepo;
    // Optional: per-delivery QR handoff secret store, kept SEPARATE from the delivery
    // record so the pickup nonce can never leak via any sync-down/GSI/DTO/conflict path.
    // Absent env → null (falls back to the legacy on-record qrNonce for in-flight deliveries).
    private readonly IDeliveryQrSecretRepository? _qrSecretRepo;
    // ESC-06: the system cancel of a pending delivery (status-conditional, like the dispute
    // verdict's) when its buyer cancels an order after a transport failure.
    private readonly IDeliveryRepository _deliveryRepo;

    // Who/where for the current sync batch (same user/IP/correlation across all
    // operations in one request). Set at the top of FunctionHandler. Lambda runs
    // one invocation per instance at a time, so a per-batch field is safe.
    private AuditContext? _batchAudit;

    // SSM commission rate cache (15-min TTL)
    private static double _cachedCommissionRate;
    private static DateTime _commissionRateFetchedAt = DateTime.MinValue;
    private static readonly TimeSpan CommissionRateCacheTtl = TimeSpan.FromMinutes(15);
    private const double DefaultCommissionRate = 0.05;

    /// <summary>
    /// SIM-05/SIM-07 units: order.quantity is KILOGRAMS on the wire; listing stock and
    /// trip capacity are quintals and trip prices are per quintal (order-lifecycle.md).
    /// </summary>
    private const double KgPerQuintal = 100.0;

    /// <summary>ESC-01: what one live Telebirr payment can take (escrow-protocol.md §Amount Validation).</summary>
    private const decimal LiveMinPaymentEtb = 100m;
    private const decimal LiveMaxPaymentEtb = 500_000m;

    /// <summary>FUP-20: the only payment method the live gateway funds (the ESC-01 checkout); bank transfers wait for ESC-46.</summary>
    private const string LivePaymentMethod = "telebirr";

    /// <summary>Client-echoed money must match the server's computation to the santim.</summary>
    private const double MoneyTolerance = 0.01;

    /// <summary>Slack for floating-point kg comparisons against quintal stock.</summary>
    private const double QuantityTolerance = 0.001;

    /// <summary>Largest crop listing stock, in quintals (crop-listing-api.md).</summary>
    private const decimal MaxListingQuintals = 10000m;

    /// <summary>
    /// Largest order, in kilograms: a whole listing at its maximum stock (MaxListingQuintals x
    /// KgPerQuintal). The real bound is the listing's stock, checked in BindOrderToListing.
    /// </summary>
    private const decimal MaxOrderKg = MaxListingQuintals * 100m;

    private static readonly HashSet<string> AllowedCropFields = new()
    {
        "farmerId", "farmerName", "farmerPhone", "cropType", "quantity", "pricePerKg",
        "latitude", "longitude", "locationName", "description",
        "status", "woreda", "listingId", "audioNoteRef", "createdAt",
        // crop-listing-api 1.6.0 rule 9: 2 to 4 photo ids, stored as an L of S.
        "photoIds"
    };

    private static readonly HashSet<string> AllowedIntentFields = new()
    {
        "intentId", "buyerId", "cropType", "quantityNeeded", "maxPricePerKg",
        "deliveryWoreda", "deliveryLatitude", "deliveryLongitude", "neededByDate",
        "status", "createdAt"
    };

    private static readonly HashSet<string> AllowedRatingFields = new()
    {
        "ratingId", "orderId", "raterId", "rateeId", "rateeRole", "stars",
        "comment", "createdAt"
    };

    private static readonly HashSet<string> AllowedRateeRoles = new(StringComparer.Ordinal)
    {
        "farmer", "trucker"
    };

    // Client-supplied message fields. Sender/recipient names are NOT here: the
    // server denormalizes them authoritatively from the users table, so a client
    // cannot spoof the counterpart's identity. Phones are stripped, never stored.
    private static readonly HashSet<string> AllowedMessageFields = new()
    {
        "messageId", "conversationId", "contextType", "contextId",
        "senderId", "recipientId", "body", "createdAt"
    };

    // The four context kinds a conversation may be scoped to. Crop and trip threads
    // are kept for old rows and clients but take no new message (ESC-48).
    private static readonly HashSet<string> AllowedMessageContextTypes = new(StringComparer.Ordinal)
    {
        "crop", "order", "trip", "delivery"
    };

    private static readonly HashSet<string> AllowedIntentStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "active", "fulfilled", "expired"
    };

    private static readonly HashSet<string> AllowedMatchEventFields = new()
    {
        "eventId", "intentId", "buyerId", "eventType", "listingId",
        "score", "rank", "isSubstitute", "matchCount", "impressions",
        "occurredAt", "policyTag"
    };

    private static readonly HashSet<string> AllowedMatchEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "shown", "opened", "order_created"
    };

    /// <summary>Upper bound for the JSON-string `impressions` payload (bytes-ish).</summary>
    private const int MaxImpressionsLength = 8192;

    /// <summary>
    /// SIM-02: identity fields a crop update may echo unchanged (web clients resend
    /// them) but may never CHANGE. Rewriting farmerId moved listings into other
    /// users' accounts; farmerName/farmerPhone spoofed the contact shown to buyers.
    /// </summary>
    private static readonly HashSet<string> ImmutableCropIdentityFields = new()
    {
        "farmerId", "farmerName", "farmerPhone"
    };

    /// <summary>SIM-03: same guard for trips (truckerId/Name/Phone).</summary>
    private static readonly HashSet<string> ImmutableTripIdentityFields = new()
    {
        "truckerId", "truckerName", "truckerPhone"
    };

    /// <summary>
    /// SIM-09: delivery fields fixed at create (parties, order, listing, trip, type). An
    /// update may echo them unchanged; re-pointing any of them is rejected.
    /// </summary>
    private static readonly HashSet<string> ImmutableDeliveryIdentityFields = new()
    {
        "orderId", "tripId", "listingId", "deliveryType",
        "buyerId", "buyerName", "buyerPhone",
        "farmerId", "farmerName", "farmerPhone",
        "truckerId", "truckerName", "truckerPhone"
    };

    private static readonly HashSet<string> AllowedTripFields = new()
    {
        "truckerId", "truckerName", "truckerPhone", "vehiclePlate", "vehicleDescription",
        "fromWoreda", "toWoreda", "departureDate", "capacity", "pricePerQuintal", "status", "matchedListings",
        // trip-listing-api 2.1.0: the destination's point, the live track's drop-off (TripTrack.PlannedDropoff).
        "destinationLatitude", "destinationLongitude"
    };

    /// <summary>
    /// FUP-49: listing text that buyers read before any order. ESC-48 keeps contact closed until
    /// the money is held, so none of it may carry a phone number (crop-listing-api.md 1.4.0,
    /// trip-listing-api.md 1.5.0). The crop `woreda` joined in crop-listing-api.md 1.8.0 (FUP-98),
    /// with the profile woreda exempt (<see cref="IsProfileWoreda"/>).
    /// </summary>
    private static readonly string[] CropContactScreenedFields = { "description", "locationName", "woreda" };

    private static readonly string[] TripContactScreenedFields =
    {
        "vehicleDescription", "vehiclePlate", "fromWoreda", "toWoreda"
    };

    /// <summary>FUP-49: the refusal for a phone number in listing text; deterministic for a payload.</summary>
    private static string ContactInTextRefusal(string code, List<string> fields) =>
        $"{code}: Remove the phone number from {string.Join(", ", fields)}. Contact opens after payment is held in escrow.";

    /// <summary>
    /// FUP-98: a crop <c>woreda</c> equal to the farmer's profile woreda is not refused. Clients
    /// send that value from the registration profile, so an account registered before the FUP-52
    /// screen would otherwise have nothing to fix in the form. Trimmed and ordinal, like the other
    /// profile woreda read (<see cref="StampOrderBuyerNameAsync"/>). Never
    /// <see cref="WoredaNames.Normalize"/>: it is a matching key that drops everything after a
    /// comma or bracket, so "Adama, 0911..." would pass as "Adama".
    /// </summary>
    private static bool IsProfileWoreda(Dictionary<string, object> data, string? profileWoreda) =>
        !string.IsNullOrWhiteSpace(profileWoreda)
        && data.TryGetValue("woreda", out var woreda)
        && string.Equals(woreda?.ToString()?.Trim(), profileWoreda.Trim(), StringComparison.Ordinal);

    private static readonly HashSet<string> AllowedDeliveryFields = new()
    {
        "tripId", "listingId", "orderId", "farmerId", "farmerName", "farmerPhone",
        "truckerId", "truckerName", "truckerPhone", "buyerId", "buyerName", "buyerPhone",
        "status", "gpsPickup", "gpsDropoff",
        "pickupPhotoRef", "dropoffPhotoRef", "disputeReason",
        "qrToken", "gpsAnomalyFlag", "qrNonce", "deliveryType"
    };

    private static readonly HashSet<string> AllowedOrderFields = new()
    {
        "listingId", "buyerId", "farmerId", "buyerName", "farmerName",
        "cropType", "quantity", "pricePerUnit",
        "tripId", "truckerId", "truckerName", "transportCost",
        "paymentMethod", "cbeTransactionId",
        "status", "rejectionReason", "cancelReason",
        // ESC-15: the commitment commands (consumed by ProcessCommitmentCommandAsync, never written raw).
        OrderCommitment.FarmerResponseField, OrderCommitment.TruckerResponseField, OrderCommitment.DeclineReasonField,
        // LT-12: the buyer's drop-off landmark (CheckDropoffNoteChangeAsync guards an update).
        OrderDropoffNote.Field,
        // LT-14: the order's own drop-off point (CheckDeliveryLocationChangeAsync guards an update).
        OrderDeliveryLocation.LatitudeField, OrderDeliveryLocation.LongitudeField
    };

    // Server-calculated fields that clients must not set directly
    private static readonly HashSet<string> OrderServerOnlyFields = new()
    {
        "cropCost", "commissionRate", "commissionAmount", "orderTotal",
        "paymentVerifiedAt", "respondedAt", "cancelledAt",
        // ESC-15: who was asked, when they answered, their deadlines and the failure bookkeeping.
        OrderCommitment.FulfillmentField, OrderCommitment.PickupDateField,
        OrderCommitment.FarmerRespondedAtField, OrderCommitment.FarmerDeadlineAtField,
        OrderCommitment.TruckerAskedAtField, OrderCommitment.TruckerRespondedAtField, OrderCommitment.TruckerDeadlineAtField,
        OrderCommitment.BuyerChoiceDeadlineAtField, OrderCommitment.TransportFailureField,
        OrderCommitment.CancelledByField, OrderCommitment.FaultPartyField,
        OrderCommitment.FarmerReminderAtField, OrderCommitment.TruckerReminderAtField,
        OrderCommitment.BuyerChoiceReminderAtField, OrderCommitment.PickupOverdueNotifiedAtField,
        // ESC-01: the live Telebirr payment behind a funding; only the webhook writes them.
        // paymentSubmittedAt starts the payment clock and is stamped by the server.
        OrderFundingService.MerchOrderIdField, OrderFundingService.PaymentOrderIdField,
        OrderFundingService.TransIdField, "paymentSubmittedAt",
        // ESC-06: the pickup deadline is stamped at the farmer's accept; cancelNote keeps the
        // buyer's text of a transport-failure cancel; the admin queue is written by escrow-cron.
        PickupDeadline.Field, "cancelNote",
        AdminQueueFields.Marker, AdminQueueFields.QueuedAt, AdminQueueFields.Reason, AdminQueueFields.EscrowId,
        // ESC-45: the webhook holds an order whose verified payment is still pending in its inbox.
        PaymentHoldFields.HoldUntil,
        // ESC-47: when the order last entered pending_payment; the auto-cancel counts from it.
        "pendingPaymentAt",
        // Fieldway trip bookings (order-lifecycle 2.2.0): the farm and the drop-off place of the
        // order, stamped at create from the listing and the buyer's profile.
        "pickupPlace", "dropoffPlace",
        // ESC-34: the pickup judgement, the check-ins (written only by handoff_check_in) and the
        // pickup point stamped when the order became judgeable.
        PickupJudgement.Field, PickupJudgement.JudgedAtField, PickupJudgement.CheckInsField,
        PickupJudgement.PickupLatitudeField, PickupJudgement.PickupLongitudeField,
        // ESC-16: the order's next deadline (DeadlineIndex); every order write derives it itself.
        OrderDeadlines.AtField, OrderDeadlines.KindField, OrderDeadlines.ShardField
    };

    /// <summary>
    /// SIM-06: the only order fields a client may change after create. Everything else in
    /// AllowedOrderFields identifies or prices the order and is immutable; the money
    /// fields are server-owned (OrderServerOnlyFields) so orderTotal can never drift.
    /// </summary>
    private static readonly HashSet<string> UpdatableOrderFields = new()
    {
        "status", "rejectionReason", "cancelReason", "paymentMethod", "cbeTransactionId",
        // LT-12: by the buyer only, until pickup (CheckDropoffNoteChangeAsync).
        OrderDropoffNote.Field,
        // LT-14: by the buyer only, until pickup (CheckDeliveryLocationChangeAsync).
        OrderDeliveryLocation.LatitudeField, OrderDeliveryLocation.LongitudeField
    };

    /// <summary>
    /// ESC-15: the commitment commands. Allowed on an update only through
    /// <c>ProcessCommitmentCommandAsync</c> (which validates and never writes them raw); refused on
    /// create, and treated as immutable by the generic update so no party can rewrite a recorded
    /// response or decline reason.
    /// </summary>
    private static readonly string[] OrderCommandFields =
    {
        OrderCommitment.FarmerResponseField, OrderCommitment.TruckerResponseField, OrderCommitment.DeclineReasonField
    };

    /// <summary>
    /// Bookkeeping keys the shipped clients echo on every order update. They are checked
    /// (orderId) or dropped (updatedAt is stamped by the repository); version is consumed
    /// by the optimistic-concurrency check.
    /// </summary>
    private static readonly HashSet<string> OrderUpdateEchoFields = new()
    {
        "orderId", "updatedAt", "version"
    };

    /// <summary>
    /// The order status map clients are held to. Internal so tests can check that every order status
    /// has a class in the escrow sweeps (ESC-10) and that the system writes (ESC-09's money hops) stay
    /// edges of it.
    /// </summary>
    internal static readonly Dictionary<string, HashSet<string>> OrderTransitions = new()
    {
        // Buyers pay immediately after placing an order, so allow the direct
        // created -> payment_submitted hop as well as the pending_payment step.
        { "created", new HashSet<string> { "pending_payment", "payment_submitted", "cancelled" } },
        { "pending_payment", new HashSet<string> { "payment_submitted", "cancelled" } },
        { "payment_submitted", new HashSet<string> { "funded", "payment_failed" } },
        // Farmer responds to a funded order with accept or reject; reject is a
        // terminal state (authorization + respondedAt/audit side-effects already
        // handle "rejected" — only this transition entry was missing).
        // ESC-15: a funded order whose transport failed (trucker declined, expired, trip cancelled
        // or full) waits for the buyer in awaiting_transport; so does an accepted one whose trucker
        // dropped out before pickup. Both edges are system-written (OrderCommitment).
        { "funded", new HashSet<string> { "accepted", "rejected", "cancelled", OrderCommitment.AwaitingTransportStatus } },
        // ESC-06: the buyer may cancel an accepted order only after a transport failure (no
        // pickup by pickupDeadlineAt); ProcessTransportFailureCancelAsync enforces it.
        { "accepted", new HashSet<string> { "pickup_confirmed", "cancelled", OrderCommitment.AwaitingTransportStatus } },
        // ESC-15: the buyer cancels for a full refund, the farmer may cancel without penalty, or
        // the order cron cancels after the buyer's 48 hours (transport_not_arranged).
        { OrderCommitment.AwaitingTransportStatus, new HashSet<string> { "cancelled" } },
        { "pickup_confirmed", new HashSet<string> { "in_transit" } },
        { "in_transit", new HashSet<string> { "delivered" } },
        { "delivered", new HashSet<string> { "disbursing", "disputed" } },
        { "disbursing", new HashSet<string> { "settled" } },
        { "disputed", new HashSet<string> { "dispute_resolved" } },
        // ESC-47 (1), done in ESC-44: a retry after payment_failed may go straight to
        // payment_submitted. The payment gates run on the new status as for any payment,
        // paymentSubmittedAt is stamped again (a new payment attempt), and the pending_payment
        // auto-cancel never sees the order. A retry through pending_payment gets 24 hours from
        // that step (pendingPaymentAt), not from createdAt.
        { "payment_failed", new HashSet<string> { "pending_payment", "payment_submitted", "cancelled" } },
    };

    // Server-controlled fields that must not be set by clients.
    // Stripped from client data early in processing; server injects them after fraud checks.
    private static readonly HashSet<string> ServerOnlyFields = new()
    {
        "isFlagged", "flagReason", "gpsAnomalyFlag",
        // ESC-29 (reputation-api.md section 7): the owner's copied reputation, written by the server only.
        OwnerReputationFields.TierField, OwnerReputationFields.EffectiveRatingField, OwnerReputationFields.RatingCountField,
        OwnerReputationFields.ReliabilityPctField, OwnerReputationFields.AtField,
        // ESC-30: the old listing-conversion reliability is no longer computed or scored; a
        // client value is never stored.
        "farmerReliability"
    };

    // FIX-007: Identity fields are immutable after delivery creation
    private static readonly HashSet<string> ImmutableDeliveryFields = new()
    {
        "farmerId", "farmerName", "farmerPhone",
        "buyerId", "buyerName", "buyerPhone",
        "truckerId", "truckerName", "truckerPhone",
        "tripId", "listingId", "deliveryType"
    };

    private static readonly Dictionary<string, HashSet<string>> ValidTransitions = new()
    {
        { "open", new HashSet<string> { "matched", "cancelled" } },
        { "matched", new HashSet<string> { "inTransit", "cancelled" } },
        { "inTransit", new HashSet<string> { "delivered" } },
    };

    private static readonly Dictionary<string, HashSet<string>> DeliveryTransitions = new()
    {
        // ESC-08: cancel (an update or the delete action) only before pickup. After pickup the
        // goods are moving and no party can cancel; an admin settles it through a dispute verdict.
        { "pending", new HashSet<string> { "pickedUp", "cancelled" } },
        // "delivered" allowed directly from pickedUp: the buyer confirms handoff
        // right after pickup in the capture flow; marking in-transit is optional.
        { "pickedUp", new HashSet<string> { "inTransit", "delivered" } },
        { "inTransit", new HashSet<string> { "delivered" } },
        { "delivered", new HashSet<string> { "disputed" } },
    };

    // Self-pickup deliveries have no trucker/transit leg: the buyer collects at the farm,
    // so a single scan of the farmer's QR takes pending → delivered in one hop. This
    // transition is permitted ONLY for deliveryType == self_pickup (see the transition
    // authorization below), never for a normal trucker delivery.
    private const string SelfPickupDeliveryType = "self_pickup";

    // QR handoff freshness / offline-defer windows (days). Generous, because rural
    // handoffs sync late; the point is to stop indefinitely-stale codes and to give up
    // deferring a QR-gated handoff (every pickup since FRD-11, a paid drop-off) whose
    // nonce is clearly never going to arrive. The defer window counts from the later of
    // the delivery's creation and the scan (AwaitHandoffNonce).
    private const int QrNonceFreshnessDays = 14;
    private const int QrNonceMaxDeferDays = 14;

    // ESC-35: the trucker's drop-off nonce starts with this tag (a farmer's nonce is plain
    // base64url, which never holds a dot), so a buyer's scan of a drop-off QR whose nonce has not
    // synced yet waits for it instead of failing as a wrong code. Shared with the apps.
    private const string DropoffNoncePrefix = "d.";
    private const int DropoffNonceMaxLength = 100;

    /// <summary>
    /// Scrub secret fields from ANY entity item before it is echoed to a client as sync
    /// <c>ServerData</c> (conflict/success). Currently the only such field is the delivery
    /// QR handoff nonce: new deliveries never store it on the record (it lives in the separate
    /// secret store), but this also cleans legacy in-flight items so the pickup nonce can never
    /// ride back down to a scanner. A no-op for entities that have no secret field.
    /// </summary>
    private static Dictionary<string, object> ScrubSyncSecrets(Dictionary<string, object> item)
    {
        item.Remove("qrNonce");
        return item;
    }

    /// <summary>
    /// Copies the order's crop type and quantity (kg), the listing's place name and the
    /// trip's departure date onto a new delivery row (delivery-verification-api 1.21.0), so
    /// list cards need no order read. A missing source leaves its field off the row.
    /// </summary>
    private static void StampDeliveryCargo(
        Dictionary<string, AttributeValue> item,
        Dictionary<string, object> order,
        Dictionary<string, object>? listing,
        Dictionary<string, AttributeValue>? trip)
    {
        var cropType = order.GetValueOrDefault("cropType")?.ToString();
        if (!string.IsNullOrWhiteSpace(cropType))
        {
            item["cropType"] = new AttributeValue { S = cropType };
        }

        var quantity = ParseDouble(order, "quantity");
        if (quantity > 0)
        {
            item["quantity"] = new AttributeValue { N = quantity.ToString(CultureInfo.InvariantCulture) };
        }

        var place = listing?.GetValueOrDefault("locationName")?.ToString();
        if (!string.IsNullOrWhiteSpace(place))
        {
            item["pickupPlace"] = new AttributeValue { S = place };
        }

        if (trip != null && trip.TryGetValue("departureDate", out var departure)
            && !string.IsNullOrWhiteSpace(departure.S))
        {
            item["pickupPlannedAt"] = new AttributeValue { S = departure.S };
        }
    }

    /// <summary>
    /// A delivery row as a party gets it back from /sync: no QR secret (<see cref="ScrubSyncSecrets"/>)
    /// and no phone numbers (ESC-48: the parties reach each other from the order, only while its
    /// contact is open). Delivery rows only: owner trip rows keep their own <c>truckerPhone</c>,
    /// which shipped clients read as a required field.
    /// </summary>
    private static Dictionary<string, object> ScrubDeliveryForClient(Dictionary<string, object> item)
    {
        ScrubSyncSecrets(item);
        item.Remove("farmerPhone");
        item.Remove("truckerPhone");
        item.Remove("buyerPhone");
        // FRD-12: the S3 version ids the photo reads sign are server-only (delivery-verification-api 1.25.0).
        item.Remove("pickupPhotoVersionId");
        item.Remove("dropoffPhotoVersionId");
        return item;
    }

    /// <summary>
    /// Constant-time comparison of a scanned QR nonce against the stored one. The nonce is a
    /// high-entropy bearer secret, so compare in fixed time to avoid leaking a match prefix
    /// via timing. Differing lengths short-circuit to false (length is not sensitive here).
    /// </summary>
    private static bool NonceEquals(string scanned, string expected)
    {
        var a = Encoding.UTF8.GetBytes(scanned);
        var b = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static readonly HashSet<string> AllowedCropTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "teff", "wheat", "barley", "maize", "chickpea", "lentils", "sorghum", "coffee", "sesame"
    };

    private static readonly Dictionary<string, HashSet<string>> CropTransitions = new()
    {
        { "available", new HashSet<string> { "reserved", "sold", "deleted" } },
        { "reserved", new HashSet<string> { "available", "sold", "deleted" } },
        // sold and deleted are terminal states
    };

    [GeneratedRegex(@"^\+251\d{9}$")]
    private static partial Regex EthiopianPhoneRegex();

    // SIM-60: departureDate is ISO 8601 date and time with Z, an offset or no zone, as clients
    // send it (Dart toIso8601String of a UTC time; builds before 2026-06-18 sent a local time
    // without a zone, and trip creates they queued still replay). It is the sort key of
    // StatusDepartureDateIndex, so a value only a lenient parse accepts ("10/15/2026")
    // would sort ahead of every real date in buyer browse.
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]{1,7})?)?(Z|[+-][0-9]{2}:[0-9]{2})?$")]
    private static partial Regex IsoDepartureDateRegex();

    /// <summary>
    /// SIM-60: read a departureDate as a UTC instant; an offset is converted and a value
    /// without a zone is taken as UTC. <paramref name="requireIsoShape"/> also requires
    /// <see cref="IsoDepartureDateRegex"/> (client input; stored values are read as they are).
    /// </summary>
    private static bool TryParseDepartureDateUtc(string? raw, bool requireIsoShape, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(raw) || (requireIsoShape && !IsoDepartureDateRegex().IsMatch(raw)))
        {
            return false;
        }

        return DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out utc);
    }

    /// <summary>
    /// SIM-60: store departureDate in one form, UTC with milliseconds, the shape mobile and
    /// web send (toUtc().toIso8601String()), so an offset cannot move a trip within the
    /// StatusDepartureDateIndex order. Call after <see cref="ValidateTripFields"/> passed.
    /// </summary>
    private static void NormalizeDepartureDate(Dictionary<string, object> data)
    {
        if (data.TryGetValue("departureDate", out var raw)
            && TryParseDepartureDateUtc(raw?.ToString(), requireIsoShape: true, out var utc))
        {
            data["departureDate"] = utc.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// Reads table names from environment variables.
    /// </summary>
    public SyncFunction()
    {
        var syncMetadataTable = Environment.GetEnvironmentVariable("SYNC_METADATA_TABLE")
                                ?? throw new InvalidOperationException("SYNC_METADATA_TABLE is not set.");
        var cropsTable = Environment.GetEnvironmentVariable("CROPS_TABLE")
                         ?? throw new InvalidOperationException("CROPS_TABLE is not set.");
        _tripsTable = Environment.GetEnvironmentVariable("TRIPS_TABLE")
                      ?? throw new InvalidOperationException("TRIPS_TABLE is not set.");
        _deliveriesTable = Environment.GetEnvironmentVariable("DELIVERIES_TABLE")
                           ?? throw new InvalidOperationException("DELIVERIES_TABLE is not set.");
        var rateLimitTable = Environment.GetEnvironmentVariable("RATE_LIMITS_TABLE")
                             ?? throw new InvalidOperationException("RATE_LIMITS_TABLE is not set.");

        var usersTable = Environment.GetEnvironmentVariable("USERS_TABLE")
                         ?? throw new InvalidOperationException("USERS_TABLE is not set.");
        var ordersTable = Environment.GetEnvironmentVariable("ORDERS_TABLE")
                          ?? throw new InvalidOperationException("ORDERS_TABLE is not set.");

        _dynamoDb = new AmazonDynamoDBClient();

        _syncMetadataRepo = new SyncMetadataRepository(_dynamoDb, syncMetadataTable);
        _cropRepo = new CropRepository(_dynamoDb, cropsTable);
        _orderRepo = new OrderRepository(_dynamoDb, ordersTable);
        _deliveryRepo = new DeliveryRepository(_dynamoDb, _deliveriesTable);
        _rateLimitService = new RateLimitService(new RateLimitRepository(_dynamoDb, rateLimitTable));
        // Fraud signals feed the admin review queue; absent env → writes no-op.
        var fraudSignalsTable = Environment.GetEnvironmentVariable("FRAUD_SIGNALS_TABLE") ?? "";
        _fraudService = new FraudDetectionService(
            _dynamoDb,
            cropsTable,
            fraudSignalsTable,
            new RateLimitRepository(_dynamoDb, rateLimitTable));
        _userRepo = new UserRepository(_dynamoDb, usersTable);

        // Intents are optional: the buyer_intent sync handler is only reachable once
        // the intents table is provisioned (push-alerts feature). Absent env → null.
        var intentsTable = Environment.GetEnvironmentVariable("INTENTS_TABLE");
        _intentRepo = intentsTable != null
            ? new IntentRepository(_dynamoDb, intentsTable)
            : null;

        // Match-event logging is optional in the same way: the match_event sync
        // handler is only reachable once the match-events table is provisioned.
        var matchEventsTable = Environment.GetEnvironmentVariable("MATCH_EVENTS_TABLE");
        _matchEventRepo = matchEventsTable != null
            ? new MatchEventRepository(_dynamoDb, matchEventsTable)
            : null;

        // Escrow bridge is optional: confirming a delivery stamps deliveryConfirmedAt
        // on the linked escrow so the auto-release cron can settle. Absent env → null
        // (delivery still transitions; escrow simply isn't stamped from here).
        var escrowsTable = Environment.GetEnvironmentVariable("ESCROWS_TABLE");
        _escrowRepo = escrowsTable != null
            ? new EscrowRepository(_dynamoDb, escrowsTable)
            : null;
        EscrowsTableName = escrowsTable;
        FraudHoldEnabled = string.Equals(
            Environment.GetEnvironmentVariable("FRAUD_HOLD_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);
        PayoutDelay = new PayoutDelayPolicy(_fraudService, _userRepo, _escrowRepo);

        // FRD-09: the pattern rules read history only when their signals can be written.
        PatternRules = string.IsNullOrEmpty(fraudSignalsTable)
            ? null
            : new TransactionPatternService(_orderRepo, _escrowRepo);

        // Optional: unified transaction-audit trail (order/delivery transitions).
        var txAuditTable = Environment.GetEnvironmentVariable("TRANSACTION_AUDIT_TABLE");
        _transactionAuditRepo = txAuditTable != null
            ? new TransactionAuditRepository(_dynamoDb, txAuditTable)
            : null;

        // Order-funded escrow leg. Reuses _escrowRepo; the audit table is required
        // because every escrow record mutation must land in the immutable ledger.
        // ESC-07: built after the transaction-audit repository, which the dispute mirror
        // writes one row per order or delivery hop to.
        var escrowAuditTable = Environment.GetEnvironmentVariable("ESCROW_AUDIT_TABLE");
        _orderEscrowService = _escrowRepo != null && escrowAuditTable != null
            ? new EscrowService(
                _escrowRepo,
                new EscrowAuditRepository(_dynamoDb, escrowAuditTable),
                idempotencyRepo: null,
                telebirrGateway: null,
                userRepo: _userRepo,
                orderRepo: _orderRepo,
                deliveryRepo: _deliveryRepo,
                transactionAuditRepo: _transactionAuditRepo)
            {
                // ESC-20: a mock-funded order's escrow is born on the ledger behind ESCROW_LEDGER_ENABLED.
                LedgerRepo = EscrowLedgerConfig.FromEnvironment(_dynamoDb, escrowsTable, escrowAuditTable),
                LedgerBirthEnabled = EscrowLedgerConfig.BirthEnabled
            }
            : null;
        _tripRepo = new TripRepository(_dynamoDb, _tripsTable);

        // Ratings are optional in the same way: the rating sync handler is only
        // reachable once the ratings table is provisioned.
        // ESC-28: with the users table the create also keeps the ratee's stored rating sum and count
        // (rating-api.md 1.2.0); with the reputation-events table it upserts their heal marker.
        var ratingsTable = Environment.GetEnvironmentVariable("RATINGS_TABLE");
        var reputationEventsTable = Environment.GetEnvironmentVariable("REPUTATION_EVENTS_TABLE");
        _ratingRepo = ratingsTable != null
            ? new RatingRepository(_dynamoDb, ratingsTable, usersTable, reputationEventsTable)
            : null;
        // FUP-162: a rating for a ratee the backfill did not reach stamps their aggregate (not flagged).
        RatingHeal = ratingsTable != null ? new RatingAggregateHeal(_dynamoDb, usersTable, ratingsTable) : null;
        Reputation = BuildReputationService(_dynamoDb, reputationEventsTable, usersTable, _fraudService);

        // Messages are optional in the same way: the message sync handler is only
        // reachable once the messages table is provisioned.
        var messagesTable = Environment.GetEnvironmentVariable("MESSAGES_TABLE");
        _messageRepo = messagesTable != null
            ? new MessageRepository(_dynamoDb, messagesTable)
            : null;

        // Optional: per-delivery QR handoff secret store. Absent env → null (legacy
        // on-record qrNonce fallback keeps in-flight deliveries verifiable).
        var qrSecretsTable = Environment.GetEnvironmentVariable("DELIVERY_QR_SECRETS_TABLE");
        _qrSecretRepo = qrSecretsTable != null
            ? new DeliveryQrSecretRepository(_dynamoDb, qrSecretsTable)
            : null;

        // LT-05: the trip-locations table (location_ping check-ins). Absent env → null (a ping answers
        // an unprefixed error, which clients retry).
        var tripLocationsTable = Environment.GetEnvironmentVariable("TRIP_LOCATIONS_TABLE");
        _tripLocationRepo = tripLocationsTable != null
            ? new TripLocationRepository(_dynamoDb, tripLocationsTable)
            : null;

        // FRD-12: the uploads bucket a delivery_photo attach checks (its S3 client is made on first
        // use). Absent env → attaches are deferred.
        UploadsBucket = Environment.GetEnvironmentVariable("UPLOADS_BUCKET");
    }

    /// <summary>
    /// Constructor for testing with mocked dependencies.
    /// </summary>
    public SyncFunction(ISyncMetadataRepository syncMetadataRepo, ICropRepository cropRepo,
        IAmazonDynamoDB dynamoDb, string tripsTable, string deliveriesTable,
        IRateLimitService? rateLimitService = null,
        IFraudDetectionService? fraudService = null,
        IUserRepository? userRepo = null,
        IOrderRepository? orderRepo = null,
        IIntentRepository? intentRepo = null,
        IMatchEventRepository? matchEventRepo = null,
        IEscrowRepository? escrowRepo = null,
        IRatingRepository? ratingRepo = null,
        ITransactionAuditRepository? transactionAuditRepo = null,
        IDeliveryQrSecretRepository? qrSecretRepo = null,
        IMessageRepository? messageRepo = null,
        EscrowService? orderEscrowService = null,
        IDeliveryRepository? deliveryRepo = null,
        ITripRepository? tripRepo = null,
        ITripLocationRepository? tripLocationRepo = null)
    {
        _syncMetadataRepo = syncMetadataRepo ?? throw new ArgumentNullException(nameof(syncMetadataRepo));
        _cropRepo = cropRepo ?? throw new ArgumentNullException(nameof(cropRepo));
        _orderRepo = orderRepo ?? new OrderRepository(dynamoDb, "orders-placeholder");
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tripsTable = tripsTable ?? throw new ArgumentNullException(nameof(tripsTable));
        _deliveriesTable = deliveriesTable ?? throw new ArgumentNullException(nameof(deliveriesTable));
        _rateLimitService = rateLimitService;
        _fraudService = fraudService;
        _userRepo = userRepo;
        _intentRepo = intentRepo;
        _matchEventRepo = matchEventRepo;
        _escrowRepo = escrowRepo;
        _ratingRepo = ratingRepo;
        _transactionAuditRepo = transactionAuditRepo;
        _qrSecretRepo = qrSecretRepo;
        _messageRepo = messageRepo;
        _orderEscrowService = orderEscrowService;
        _deliveryRepo = deliveryRepo ?? new DeliveryRepository(_dynamoDb, _deliveriesTable);
        _tripRepo = tripRepo ?? new TripRepository(_dynamoDb, _tripsTable);
        _tripLocationRepo = tripLocationRepo;
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation("Sync invoked");

        // Parse request
        SyncRequest? syncRequest;
        try
        {
            syncRequest = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.SyncRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "SYNC_VALIDATION", "Invalid JSON in request body.");
        }

        if (syncRequest == null || syncRequest.Operations == null || syncRequest.Operations.Count == 0)
        {
            return LambdaResponse.Error(400, "SYNC_VALIDATION", "Request must contain at least one operation.");
        }

        // Validate batch size limit (prevent abuse)
        if (syncRequest.Operations.Count > 50)
        {
            return LambdaResponse.Error(400, "SYNC_VALIDATION", "Maximum 50 operations per batch.");
        }

        // FUP-127: a JSON null in the operations array is read as a null element (the reader does
        // not refuse it); the batch is refused here instead of failing on the first dereference.
        if (syncRequest.Operations.Contains(null!))
        {
            return LambdaResponse.Error(400, "SYNC_VALIDATION", "Operations must not contain null.");
        }

        // Extract authenticated user ID from API Gateway authorizer context.
        // Uses shared AuthHelper to handle both HTTP API v2 JWT authorizer
        // (claims nested under "claims" key) and flat test dictionaries.
        var authenticatedUserId = AuthHelper.GetUserId(request);
        context.Logger.LogInformation($"Authenticated user: {authenticatedUserId ?? "NULL"}");
        if (string.IsNullOrEmpty(authenticatedUserId))
        {
            return LambdaResponse.Error(401, "UNAUTHORIZED", "Authentication required.");
        }

        var authenticatedRole = AuthHelper.GetClaim(request, "custom:role");
        _requestAuthTime = AuthHelper.GetAuthTime(request);
        _batchRiskScores = _fraudService == null ? null : new RiskScoreMemo(_fraudService);
        _batchSignals.Clear();
        _batchStaleConflicts = 0;
        _batchDeliveryWritten = false;
        _batchTripRows.Clear();
        _batchListingPoints.Clear();
        _batchOrders.Clear();
        _batchPayoutDelays = new PayoutDelayBatch(_batchRiskScores);
        _batchAppBuild = ClientAppBuild.Read(request);

        // Per-user rate limiting (F-11 Finding 3)
        // Record unconditionally to prevent request flooding via invalid operations
        if (_rateLimitService != null)
        {
            var rateLimitCheck = await _rateLimitService.CheckSyncAllowedAsync(authenticatedUserId);
            if (!rateLimitCheck.IsSuccess)
            {
                var rlError = rateLimitCheck.Error!;
                return LambdaResponse.RateLimited(rlError.Code, rlError.Message, 60);
            }
            await _rateLimitService.RecordSyncAsync(authenticatedUserId);
        }

        // ACC-02: nothing in the batch is applied once the caller's account is being or has been
        // deleted (410 ACCOUNT_004, terminal for the client). ESC-29 (reputation-api.md 8.4): the
        // same strongly consistent read refuses a suspended caller (403 AUTH_025, no operation
        // applied, no idempotency key recorded) and gives the reputation gates and the owner stamps
        // the caller's row, at no extra read.
        _batchCaller = null;
        if (_userRepo != null)
        {
            try
            {
                _batchCaller = await _userRepo.GetUserByIdAsync(authenticatedUserId, consistentRead: true);
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Sync refused: the caller's account could not be read [{ex.GetType().Name}].");
                return LambdaResponse.Error(503, "SYNC_SERVER_ERROR", "Sync is temporarily unavailable. Please retry.");
            }

            if (AccountDeletionGuard.RefusalFor(_batchCaller) is { } refused)
            {
                return refused;
            }
        }

        var correlationId = AuditLogger.GetCorrelationId(request);

        // Who/where for every audit event emitted while processing this batch.
        _batchAudit = AuditContext.From(request, authenticatedUserId, authenticatedRole);

        AuditLogger.LogAudit("SYNC", "BATCH_START",
            userId: authenticatedUserId, correlationId: correlationId,
            detail: $"{syncRequest.Operations.Count} operations");

        // Process each operation independently
        var results = new List<SyncOperationResult>();

        foreach (var operation in syncRequest.Operations)
        {
            var result = await ProcessOperationAsync(operation, authenticatedUserId, authenticatedRole, context);
            context.Logger.LogInformation(
                $"Op {MoneyAlarmLog.LogId(result.OperationId)}: status={result.Status}, msg={MoneyAlarmLog.LogText(result.Message ?? "none")}");
            // SIM-25: numbers and matchedListings in serverData go out typed, as GET returns them.
            results.Add(SyncServerData.Typed(result, ReputationPublicTags));
        }

        var successCount = results.Count(r => r.Status == "SYNC_SUCCESS" || r.Status == "SYNC_DUPLICATE");
        var failCount = results.Count - successCount;

        AuditLogger.LogAudit("SYNC", "BATCH_COMPLETE",
            userId: authenticatedUserId, correlationId: correlationId,
            detail: $"{successCount} ok, {failCount} failed");

        var response = new SyncResponse
        {
            SyncBatchId = syncRequest.SyncBatchId,
            Results = results
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.SyncResponse);
    }

    /// <summary>
    /// SIM-28: the answer to an idempotency key already stored for another payload. Nothing is
    /// processed or stored. <c>SYNC_VALIDATION</c>, so clients reject it for good (it can never
    /// pass with this key) and the contract's action applies: send the change with a new key.
    /// </summary>
    internal const string IdempotencyKeyReusedMessage =
        "SYNC_VALIDATION: idempotency key already used for a different operation payload; send this change with a new idempotency key.";

    /// <summary>
    /// Process a single sync operation with idempotency checking and entity routing.
    /// </summary>
    private async Task<SyncOperationResult> ProcessOperationAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        string? authenticatedRole,
        ILambdaContext context)
    {
        // Validate required fields
        if (string.IsNullOrEmpty(operation.OperationId) ||
            string.IsNullOrEmpty(operation.IdempotencyKey) ||
            string.IsNullOrEmpty(operation.EntityType) ||
            string.IsNullOrEmpty(operation.EntityId) ||
            string.IsNullOrEmpty(operation.Action))
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId ?? "unknown",
                Status = "error",
                Message = "Missing required fields: operationId, idempotencyKey, entityType, entityId, action."
            };
        }

        // Validate action
        if (operation.Action is not ("create" or "update" or "delete"))
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = $"Invalid action: '{operation.Action}'. Must be create, update, or delete."
            };
        }

        // ESC-25 (sync-protocol.md 1.41.0): an order operation from a build below the minimum is
        // refused before any read, field check or write, so every payload from that build gets the
        // same answer. No idempotency key is read or stored and no fraud signal is written; the rest
        // of the batch goes on. Other entities are never gated (a client would only hold them).
        if (operation.EntityType == "order"
            && OrderAppBuildGate.Refusal(MinOrderAppBuild, _batchAppBuild) is { } tooOld)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = tooOld
            };
        }

        // Idempotency keys are scoped to the caller (SIM-48, ESC-11): another user's
        // key must never mark this caller's operation as already processed. Fail
        // closed when the caller id cannot scope a key (unreachable with Cognito subs).
        var scopedIdempotencyKey = SyncIdempotencyKey.ForCaller(authenticatedUserId, operation.IdempotencyKey);
        if (scopedIdempotencyKey == null)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "SYNC_VALIDATION: caller identity cannot scope the idempotency key."
            };
        }

        // Strip server-controlled fields from client data to prevent spoofing
        if (operation.Data != null)
        {
            foreach (var field in ServerOnlyFields)
            {
                operation.Data.Remove(field);
            }
        }

        try
        {
            // SIM-28: what this key is sent with, taken before any handler changes the data.
            var payloadFingerprint = SyncPayloadFingerprint.Of(operation);

            // 1. Check idempotency: did this caller already send this key?
            var isDuplicate = await _syncMetadataRepo.IdempotencyKeyExistsAsync(scopedIdempotencyKey);
            if (isDuplicate)
            {
                // SIM-28: the same key with another payload is not a retry; answering duplicate
                // would drop the change as if it had been applied. A record from before the
                // fingerprint was stored has none and still answers duplicate.
                var storedFingerprint = await _syncMetadataRepo.GetPayloadFingerprintAsync(scopedIdempotencyKey);
                if (!string.IsNullOrEmpty(storedFingerprint)
                    && !string.Equals(storedFingerprint, payloadFingerprint, StringComparison.Ordinal))
                {
                    context.Logger.LogWarning(
                        $"Idempotency key {MoneyAlarmLog.LogId(operation.IdempotencyKey)} reused with another payload for operation {MoneyAlarmLog.LogId(operation.OperationId)}");

                    return new SyncOperationResult
                    {
                        OperationId = operation.OperationId,
                        Status = "error",
                        Message = IdempotencyKeyReusedMessage
                    };
                }

                context.Logger.LogInformation(
                    $"Duplicate idempotency key: {MoneyAlarmLog.LogId(operation.IdempotencyKey)} for operation {MoneyAlarmLog.LogId(operation.OperationId)}");

                return new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "duplicate",
                    Message = "Operation already processed."
                };
            }

            // 2. Route to entity handler
            var result = operation.EntityType switch
            {
                "crop_listing" => await ProcessCropListingAsync(operation, authenticatedUserId, authenticatedRole, context),
                "buyer_intent" => await ProcessBuyerIntentAsync(operation, authenticatedUserId, authenticatedRole, context),
                "match_event" => await ProcessMatchEventAsync(operation, authenticatedUserId, authenticatedRole),
                "trip" => await ProcessTripAsync(operation, authenticatedUserId, authenticatedRole, context),
                "delivery" => await ProcessDeliveryAsync(operation, authenticatedUserId, context),
                "order" => await ProcessOrderAsync(operation, authenticatedUserId, authenticatedRole, context),
                "rating" => await ProcessRatingAsync(operation, authenticatedUserId, authenticatedRole, context),
                "message" => await ProcessMessageAsync(operation, authenticatedUserId, authenticatedRole, context),
                "user" => await ProcessUserAsync(operation, authenticatedUserId, context),
                LocationPingRules.EntityType => await ProcessLocationPingAsync(operation, authenticatedUserId, context),
                DeliveryPhotoKeys.EntityType => await ProcessDeliveryPhotoAsync(operation, authenticatedUserId, context),
                ListingReportRules.EntityType => await ProcessListingReportAsync(operation, authenticatedUserId, authenticatedRole, context),
                // ESC-34: with the flag off the entity does not exist (the unsupported answer below).
                PickupJudgement.EntityType when PickupJudgementOn => await ProcessHandoffCheckInAsync(operation, authenticatedUserId, context),
                _ => new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "error",
                    Message = $"Unsupported entity type: '{operation.EntityType}'."
                }
            };

            // LT-05: a later location_ping in this batch may not see this write in the deliveries-by-trip index.
            if (operation.EntityType == "delivery" && result.Status == "success")
            {
                _batchDeliveryWritten = true;
            }

            // 3. Store the caller-scoped idempotency key (for successful or conflict results)
            if (result.Status is "success" or "conflict" or "duplicate")
            {
                await _syncMetadataRepo.StoreIdempotencyKeyAsync(
                    scopedIdempotencyKey,
                    operation.OperationId,
                    operation.EntityType,
                    result.Status,
                    payloadFingerprint);
            }

            // FRD-08: an offline client meets version conflicts in normal use, so one never writes a
            // signal; a batch with many is recorded once, at weight 0, for review and FRD-15.
            if (result.Status == "conflict" && operation.Action is ("update" or "delete")
                && ++_batchStaleConflicts == StaleVersionBurstThreshold)
            {
                await TryRecordFraudSignalAsync(
                    authenticatedUserId ?? "", FraudDetectionService.StaleVersionBurstSignal, operation.EntityId,
                    operation.EntityType, $"{StaleVersionBurstThreshold} or more update or delete version conflicts in one sync batch.",
                    context);
            }

            return result;
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"Error processing operation {MoneyAlarmLog.LogId(operation.OperationId)}: [{ex.GetType().Name}] {MoneyAlarmLog.LogText(ex.Message)}");

            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "Internal server error."
            };
        }
    }

    /// <summary>
    /// Reject an update that changes an immutable identity field; strip identity
    /// fields that merely echo the stored value so they are not rewritten.
    /// Phone fields are compared in normalized (+251...) form because a client may
    /// echo the local 09... spelling of a number the server stored normalized, and
    /// numeric fields are compared as numbers (an echoed 50 must match a stored 50.0).
    /// Returns an error message (without the code prefix) or null when clean.
    /// </summary>
    private static string? RejectImmutableFieldChanges(
        Dictionary<string, object> data,
        HashSet<string> immutableFields,
        Func<string, string?> storedValue)
    {
        foreach (var field in immutableFields)
        {
            if (!data.TryGetValue(field, out var incoming))
            {
                continue;
            }

            var incomingText = (incoming?.ToString() ?? "").Trim();
            var stored = (storedValue(field) ?? "").Trim();
            if (field.EndsWith("Phone", StringComparison.Ordinal))
            {
                incomingText = Validation.RequestValidator.NormalizePhoneNumber(incomingText) ?? incomingText;
                stored = Validation.RequestValidator.NormalizePhoneNumber(stored) ?? stored;
            }

            var unchanged = string.Equals(incomingText, stored, StringComparison.Ordinal)
                || (double.TryParse(incomingText, NumberStyles.Float, CultureInfo.InvariantCulture, out var incomingNumber)
                    && double.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out var storedNumber)
                    && Math.Abs(incomingNumber - storedNumber) <= QuantityTolerance);
            if (!unchanged)
            {
                return $"{field} is immutable and cannot be changed on update.";
            }

            data.Remove(field);
        }

        return null;
    }

    private static List<string> ValidateCropFields(Dictionary<string, object> data, bool isCreate)
    {
        var errors = new List<string>();

        // Validate pricePerKg
        if (data.TryGetValue("pricePerKg", out var ppkObj))
        {
            if (!decimal.TryParse(ppkObj?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var price)
                || price < 0.01m || price > 1000)
            {
                errors.Add("pricePerKg must be between 0.01 and 1000.");
            }
        }
        else if (isCreate)
        {
            errors.Add("pricePerKg is required.");
        }

        // Validate quantity
        if (data.TryGetValue("quantity", out var qObj))
        {
            if (!decimal.TryParse(qObj?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var qty)
                || qty < 0.01m || qty > MaxListingQuintals)
            {
                errors.Add("quantity must be between 0.01 and 10000.");
            }
        }
        else if (isCreate)
        {
            errors.Add("quantity is required.");
        }

        // Validate cropType against allowed enum
        if (data.TryGetValue("cropType", out var ctObj))
        {
            var cropType = ctObj?.ToString() ?? "";
            if (!AllowedCropTypes.Contains(cropType))
            {
                errors.Add("Invalid cropType.");
            }
        }
        else if (isCreate)
        {
            errors.Add("cropType is required.");
        }

        // Validate description length
        if (data.TryGetValue("description", out var descObj))
        {
            var desc = descObj?.ToString() ?? "";
            if (desc.Length > 500)
            {
                errors.Add("description must not exceed 500 characters.");
            }
        }

        // Validate woreda
        if (data.TryGetValue("woreda", out var wObj))
        {
            var woreda = wObj?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(woreda) || woreda.Length > 100)
            {
                errors.Add("woreda must be 1-100 characters.");
            }
        }
        else if (isCreate)
        {
            errors.Add("woreda is required.");
        }

        // SIM-17: geo and date rules run on create AND update (the contract's
        // server-side rules; they used to be enforced nowhere on the server).
        // Coordinates are optional (GPS may not work in remote areas): absent,
        // null or empty is fine, a present value must be a number inside the
        // Ethiopia bounding box.
        if (TryReadOptionalDouble(data, "latitude", out var latitude, out var latitudeInvalid))
        {
            if (latitudeInvalid || latitude < EthiopiaMinLatitude || latitude > EthiopiaMaxLatitude)
            {
                errors.Add($"latitude must be between {EthiopiaMinLatitude} and {EthiopiaMaxLatitude} (Ethiopia).");
            }
        }

        if (TryReadOptionalDouble(data, "longitude", out var longitude, out var longitudeInvalid))
        {
            if (longitudeInvalid || longitude < EthiopiaMinLongitude || longitude > EthiopiaMaxLongitude)
            {
                errors.Add($"longitude must be between {EthiopiaMinLongitude} and {EthiopiaMaxLongitude} (Ethiopia).");
            }
        }

        // createdAt is server-stamped (the repository ignores the client value),
        // but a nonsense value must not be accepted silently. Clients send local
        // time without an offset and device clocks drift, so allow a day of skew.
        if (data.TryGetValue("createdAt", out var createdAtObj))
        {
            var createdAtText = createdAtObj?.ToString();
            if (!string.IsNullOrWhiteSpace(createdAtText))
            {
                if (!DateTime.TryParse(createdAtText, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var createdAtUtc))
                {
                    errors.Add("createdAt must be an ISO 8601 timestamp.");
                }
                else if (createdAtUtc > DateTime.UtcNow.Add(CreatedAtFutureSkew))
                {
                    errors.Add("createdAt cannot be in the future.");
                }
            }
        }

        // crop-listing-api 1.6.0 rule 9: required on create, optional on update, same rule when
        // present. NormalizeCropPhotoIds writes the parsed list back before the repository write.
        if (data.TryGetValue("photoIds", out var photoIdsObj))
        {
            if (!CropPhotoKeys.TryNormalizePhotoIds(photoIdsObj, out _))
            {
                errors.Add(CropPhotoKeys.PhotoIdsRule);
            }
        }
        else if (isCreate)
        {
            errors.Add(CropPhotoKeys.PhotoIdsRule);
        }

        return errors;
    }

    /// <summary>
    /// Rule 9: replace a validated <c>photoIds</c> (a JSON array on the wire) with its lower-cased
    /// string list, so the repository stores an L of S rather than the array's raw text.
    /// </summary>
    private static void NormalizeCropPhotoIds(Dictionary<string, object> data)
    {
        if (data.TryGetValue("photoIds", out var raw)
            && CropPhotoKeys.TryNormalizePhotoIds(raw, out var photoIds))
        {
            data["photoIds"] = photoIds;
        }
    }

    /// <summary>Ethiopia bounding box (contract: 3-15°N, 33-48°E).</summary>
    private const double EthiopiaMinLatitude = 3.0;
    private const double EthiopiaMaxLatitude = 15.0;
    private const double EthiopiaMinLongitude = 33.0;
    private const double EthiopiaMaxLongitude = 48.0;

    /// <summary>
    /// How far ahead of server time a client createdAt may sit before it is a
    /// future date. Covers local-time strings sent without an offset (UTC+3)
    /// plus device clock drift, without accepting a listing dated next year.
    /// </summary>
    private static readonly TimeSpan CreatedAtFutureSkew = TimeSpan.FromHours(24);

    /// <summary>
    /// Read an optional numeric field. Returns false when the key is absent or
    /// its value is null/empty (treated as not provided). When present,
    /// <paramref name="invalid"/> is true if the value is not a finite number.
    /// </summary>
    private static bool TryReadOptionalDouble(
        Dictionary<string, object> data, string key, out double value, out bool invalid)
    {
        value = 0;
        invalid = false;
        if (!data.TryGetValue(key, out var obj))
        {
            return false;
        }

        var text = obj?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        invalid = !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || double.IsNaN(value) || double.IsInfinity(value);
        return true;
    }

    /// <summary>
    /// Validate buyer-intent field values. Mirrors the crop validator's style.
    /// </summary>
    private static List<string> ValidateIntentFields(Dictionary<string, object> data, bool isCreate)
    {
        var errors = new List<string>();

        if (isCreate)
        {
            var cropType = data.GetValueOrDefault("cropType")?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(cropType))
            {
                errors.Add("cropType is required.");
            }
            else if (!AllowedCropTypes.Contains(cropType))
            {
                errors.Add("Invalid cropType.");
            }
        }

        // Parse invariantly: client numbers are JSON (period decimal), so a
        // fractional quantity/price must not be rejected on a comma-decimal
        // server locale.
        if (data.TryGetValue("quantityNeeded", out var qObj)
            && (!double.TryParse(qObj?.ToString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var q) || q <= 0))
        {
            errors.Add("quantityNeeded must be a positive number.");
        }

        // 0 is a valid value: it means "no budget set" (the max-price field is
        // optional on the client) and scores as unconstrained in MatchScoring.
        if (data.TryGetValue("maxPricePerKg", out var pObj)
            && (!double.TryParse(pObj?.ToString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var p) || p < 0))
        {
            errors.Add("maxPricePerKg must be a non-negative number.");
        }

        if (data.TryGetValue("status", out var sObj))
        {
            var status = sObj?.ToString() ?? "";
            if (!AllowedIntentStatuses.Contains(status))
            {
                errors.Add("Invalid status.");
            }
        }

        // Optional delivery deadline — must be a parseable ISO 8601 date when present.
        if (data.TryGetValue("neededByDate", out var nObj)
            && !string.IsNullOrWhiteSpace(nObj?.ToString())
            && !DateTime.TryParse(nObj!.ToString(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out _))
        {
            errors.Add("neededByDate must be an ISO 8601 date.");
        }

        return errors;
    }

    /// <summary>
    /// Process a buyer_intent sync operation (create, update, delete).
    ///
    /// Buyer intents are owned by the authenticated buyer; only role "buyer" may
    /// create/modify them. The data's buyerId must name the caller on create; an update
    /// may omit it (the row is keyed by the caller, so ownership is enforced by the
    /// repository) but may not name anyone else (SIM-60). New crops are matched against
    /// these intents for push alerts (see
    /// specs/architecture/push-notifications-architecture.md).
    /// </summary>
    private async Task<SyncOperationResult> ProcessBuyerIntentAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        string? authenticatedRole,
        ILambdaContext context)
    {
        if (_intentRepo == null)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "INTENT_UNAVAILABLE: Intent sync is not enabled on this environment."
            };
        }

        if (!string.Equals(authenticatedRole, "buyer", StringComparison.OrdinalIgnoreCase))
        {
            return await RefuseWithSignalAsync(new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "INTENT_UNAUTHORIZED: Only buyers can create purchase intents."
            }, RoleGateSignal(authenticatedRole), operation, "buyer_intent", authenticatedUserId, context);
        }

        switch (operation.Action)
        {
            case "create":
            case "update":
                {
                    if (operation.Data == null || operation.Data.Count == 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "Operation requires data payload."
                        };
                    }

                    // buyerId in the payload must match the authenticated user. It is
                    // required on create; a partial update may leave it out (SIM-60).
                    var hasBuyerId = operation.Data.TryGetValue("buyerId", out var buyerIdObj);
                    if ((operation.Action == "create" && !hasBuyerId)
                        || (hasBuyerId && buyerIdObj?.ToString() != authenticatedUserId))
                    {
                        return await RefuseWithSignalAsync(new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "INTENT_UNAUTHORIZED: buyerId must match authenticated user."
                        }, FraudDetectionService.RoleForbiddenSignal, operation, "buyer_intent", authenticatedUserId, context);
                    }

                    var unknownFields = operation.Data.Keys
                        .Where(k => !AllowedIntentFields.Contains(k) && k != "version")
                        .ToList();
                    if (unknownFields.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"INTENT_VALIDATION: Unknown fields: {string.Join(", ", unknownFields)}"
                        };
                    }

                    var validationErrors = ValidateIntentFields(operation.Data, operation.Action == "create");
                    if (validationErrors.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"INTENT_VALIDATION: {string.Join(" ", validationErrors)}"
                        };
                    }

                    if (operation.Action == "create")
                    {
                        operation.Data["status"] = "active";
                        var createResult = await _intentRepo.CreateAsync(
                            operation.EntityId, authenticatedUserId!, operation.Data);
                        return createResult.IsSuccess
                            ? new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "success",
                                ServerVersion = 1,
                                ServerData = createResult.Value
                            }
                            : new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "conflict",
                                Message = createResult.Error?.Message ?? "Intent already exists."
                            };
                    }

                    // SIM-08: op-level clientVersion (contract) or data.version (shipped clients).
                    TryGetClientVersion(operation, out var clientVersion);

                    var updateResult = await _intentRepo.UpdateAsync(
                        operation.EntityId, authenticatedUserId!, operation.Data, clientVersion);
                    return updateResult.IsSuccess
                        ? new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "success",
                            ServerVersion = clientVersion + 1,
                            ServerData = updateResult.Value
                        }
                        : new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "conflict",
                            Message = updateResult.Error?.Message ?? "Intent conflict."
                        };
                }

            case "delete":
                {
                    await _intentRepo.DeleteAsync(operation.EntityId, authenticatedUserId!);
                    return new SyncOperationResult
                    {
                        OperationId = operation.OperationId,
                        Status = "success",
                        Message = "Intent deleted."
                    };
                }

            default:
                return new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "error",
                    Message = $"Unsupported action: '{operation.Action}'."
                };
        }
    }

    /// <summary>
    /// Process a rating sync operation. Create-only and immutable: a buyer rates
    /// a counterparty (farmer or trucker) for a delivered/settled order they own.
    /// The deterministic ratingId ("{orderId}:{rateeId}") + attribute_not_exists
    /// create condition guarantee one rating per (order, ratee).
    /// </summary>
    private async Task<SyncOperationResult> ProcessRatingAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        string? authenticatedRole,
        ILambdaContext context)
    {
        if (_ratingRepo == null)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "RATING_UNAVAILABLE: Rating sync is not enabled on this environment."
            };
        }

        if (!string.Equals(authenticatedRole, "buyer", StringComparison.OrdinalIgnoreCase))
        {
            return await RefuseWithSignalAsync(new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "RATING_UNAUTHORIZED: Only buyers can rate counterparties."
            }, RoleGateSignal(authenticatedRole), operation, "rating", authenticatedUserId, context);
        }

        // Ratings are immutable: only `create` is supported.
        if (!string.Equals(operation.Action, "create", StringComparison.OrdinalIgnoreCase))
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = $"RATING_VALIDATION: Unsupported action '{operation.Action}'. Ratings are immutable."
            };
        }

        if (operation.Data == null || operation.Data.Count == 0)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "Operation requires data payload."
            };
        }

        // raterId in the payload must match the authenticated user.
        if (!operation.Data.TryGetValue("raterId", out var raterIdObj)
            || raterIdObj?.ToString() != authenticatedUserId)
        {
            return await RefuseWithSignalAsync(new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "RATING_UNAUTHORIZED: raterId must match authenticated user."
            }, FraudDetectionService.RoleForbiddenSignal, operation, "rating", authenticatedUserId, context);
        }

        var unknownFields = operation.Data.Keys
            .Where(k => !AllowedRatingFields.Contains(k) && k != "version")
            .ToList();
        if (unknownFields.Count > 0)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = $"RATING_VALIDATION: Unknown fields: {string.Join(", ", unknownFields)}"
            };
        }

        var validationErrors = ValidateRatingFields(operation.Data);
        if (validationErrors.Count > 0)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = $"RATING_VALIDATION: {string.Join(" ", validationErrors)}"
            };
        }

        var orderId = operation.Data["orderId"].ToString()!;
        var rateeId = operation.Data["rateeId"].ToString()!;
        var rateeRole = operation.Data["rateeRole"].ToString()!;

        // The rating must be about a delivered/settled order the buyer owns, and
        // the ratee must be that order's farmer or trucker.
        var order = await _orderRepo.GetByIdAsync(orderId);
        if (order == null)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "RATING_VALIDATION: Order not found."
            };
        }

        var orderBuyerId = order.TryGetValue("buyerId", out var bObj) ? bObj?.ToString() : null;
        if (orderBuyerId != authenticatedUserId)
        {
            return await RefuseWithSignalAsync(new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "RATING_FORBIDDEN: You can only rate your own orders."
            }, FraudDetectionService.RoleForbiddenSignal, operation, "rating", authenticatedUserId, context);
        }

        // ESC-09: a delivered order is rateable while its money is paid out (disbursing, which with the
        // live gateway can last days in the manual payout queue) and after (settled).
        var orderStatus = order.TryGetValue("status", out var sObj) ? sObj?.ToString() : null;
        if (orderStatus is not ("delivered" or "disbursing" or "settled"))
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "RATING_FORBIDDEN: You can only rate a delivered order (delivered, disbursing or settled)."
            };
        }

        var orderFarmerId = order.TryGetValue("farmerId", out var fObj) ? fObj?.ToString() : null;
        var orderTruckerId = order.TryGetValue("truckerId", out var tObj) ? tObj?.ToString() : null;
        var validRatee = (rateeRole == "farmer" && rateeId == orderFarmerId)
            || (rateeRole == "trucker" && rateeId == orderTruckerId
                && !string.IsNullOrEmpty(orderTruckerId));
        if (!validRatee)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "RATING_VALIDATION: ratee must be the order's farmer or trucker."
            };
        }

        // Deterministic id enforces one rating per (order, ratee).
        if (operation.EntityId != $"{orderId}:{rateeId}")
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "RATING_VALIDATION: ratingId must equal '{orderId}:{rateeId}'."
            };
        }

        // ACC-02: no rating for an account that is being or has been deleted (the deletion erased
        // the comments about it).
        var ratee = _userRepo != null ? await _userRepo.GetUserByIdAsync(rateeId) : null;
        if (!string.IsNullOrEmpty(ratee?.DeletionState))
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "SYNC_VALIDATION: The rated user has deleted their account."
            };
        }

        var createResult = await _ratingRepo.CreateAsync(operation.EntityId, operation.Data);
        if (createResult.IsSuccess)
        {
            // FUP-162: an unstamped ratee gets their full aggregate first, so the refresh reads it.
            await HealRateeAggregateAsync(rateeId, ratee, rateeRole, context);
            await RefreshRateeReputationAsync(rateeId, context);
        }

        return createResult.IsSuccess
            ? new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "success",
                ServerVersion = 1,
                ServerData = createResult.Value
            }
            : new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "conflict",
                Message = createResult.Error?.Message ?? "Rating already exists."
            };
    }

    /// <summary>
    /// Validate rating fields: orderId/rateeId required, rateeRole in
    /// {farmer, trucker}, stars an integer 1-5, comment ≤ 500 chars.
    /// </summary>
    private static List<string> ValidateRatingFields(Dictionary<string, object> data)
    {
        var errors = new List<string>();

        if (!data.TryGetValue("orderId", out var orderIdObj)
            || string.IsNullOrWhiteSpace(orderIdObj?.ToString()))
        {
            errors.Add("orderId is required.");
        }

        if (!data.TryGetValue("rateeId", out var rateeIdObj)
            || string.IsNullOrWhiteSpace(rateeIdObj?.ToString()))
        {
            errors.Add("rateeId is required.");
        }

        if (!data.TryGetValue("rateeRole", out var roleObj)
            || !AllowedRateeRoles.Contains(roleObj?.ToString() ?? ""))
        {
            errors.Add("rateeRole must be 'farmer' or 'trucker'.");
        }

        if (!data.TryGetValue("stars", out var starsObj)
            || !int.TryParse(starsObj?.ToString(), out var stars) || stars < 1 || stars > 5)
        {
            errors.Add("stars must be an integer between 1 and 5.");
        }

        if (data.TryGetValue("comment", out var commentObj) && commentObj != null
            && commentObj.ToString()!.Length > 500)
        {
            errors.Add("comment must be 500 characters or fewer.");
        }

        return errors;
    }

    /// <summary>
    /// Process a message sync operation (context-scoped in-app chat). Messages
    /// are create-only and immutable. The CRITICAL role/visibility gate lives
    /// here: the sender must be the authenticated user, and both the sender and
    /// recipient must be legitimate participants of the referenced context
    /// (order/delivery), and the order's contact must be open (ESC-48; crop and
    /// trip threads are closed). Sender/recipient names are denormalized
    /// authoritatively from the users table so neither side can spoof them.
    /// </summary>
    private async Task<SyncOperationResult> ProcessMessageAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        string? authenticatedRole,
        ILambdaContext context)
    {
        if (_messageRepo == null || _userRepo == null)
        {
            return MessageError(operation, "error",
                "MESSAGE_UNAVAILABLE: Messaging is not enabled on this environment.");
        }

        // Messages are immutable: only `create` is supported.
        if (!string.Equals(operation.Action, "create", StringComparison.OrdinalIgnoreCase))
        {
            return MessageError(operation, "error",
                $"MESSAGE_VALIDATION: Unsupported action '{operation.Action}'. Messages are immutable.");
        }

        if (operation.Data == null || operation.Data.Count == 0)
        {
            return MessageError(operation, "error", "Operation requires data payload.");
        }

        // Strip server-denormalized identity fields so a client can neither spoof
        // them nor be rejected for echoing them back on a resend.
        operation.Data.Remove("senderName");
        operation.Data.Remove("senderPhone");
        operation.Data.Remove("recipientName");
        operation.Data.Remove("recipientPhone");

        var unknownFields = operation.Data.Keys
            .Where(k => !AllowedMessageFields.Contains(k) && k != "version")
            .ToList();
        if (unknownFields.Count > 0)
        {
            return MessageError(operation, "error",
                $"MESSAGE_VALIDATION: Unknown fields: {string.Join(", ", unknownFields)}");
        }

        var validationErrors = ValidateMessageFields(operation.Data);
        if (validationErrors.Count > 0)
        {
            return MessageError(operation, "error",
                $"MESSAGE_VALIDATION: {string.Join(" ", validationErrors)}");
        }

        var messageId = operation.Data["messageId"].ToString()!;
        var conversationId = operation.Data["conversationId"].ToString()!;
        var contextType = operation.Data["contextType"].ToString()!;
        var contextId = operation.Data["contextId"].ToString()!;
        var senderId = operation.Data["senderId"].ToString()!;
        var recipientId = operation.Data["recipientId"].ToString()!;

        // The message id (entityId) must equal the client-generated messageId.
        if (operation.EntityId != messageId)
        {
            return MessageError(operation, "error",
                "MESSAGE_VALIDATION: messageId must equal the operation entityId.");
        }

        // The sender must be the authenticated user (the hard rule).
        if (senderId != authenticatedUserId)
        {
            return await RefuseWithSignalAsync(
                MessageError(operation, "error", "MESSAGE_UNAUTHORIZED: senderId must match authenticated user."),
                FraudDetectionService.RoleForbiddenSignal, operation, "message", authenticatedUserId, context);
        }

        if (senderId == recipientId)
        {
            return MessageError(operation, "error",
                "MESSAGE_VALIDATION: sender and recipient must differ.");
        }

        // conversationId must be the deterministic "{contextType}:{contextId}:{lo}:{hi}"
        // over the sorted participant ids — ties the grouping key to this exact
        // context + pair so a client cannot mis-group or hijack a thread.
        var sortedIds = new[] { senderId, recipientId };
        Array.Sort(sortedIds, StringComparer.Ordinal);
        var expectedConversationId = $"{contextType}:{contextId}:{sortedIds[0]}:{sortedIds[1]}";
        if (conversationId != expectedConversationId)
        {
            return MessageError(operation, "error",
                "MESSAGE_VALIDATION: conversationId does not match the context and participants.");
        }

        // Verify BOTH parties are legitimate participants of the context. Never
        // trust a client-supplied role — derive legitimacy from the record.
        var contextError = await VerifyMessageContextAsync(
            contextType, contextId, senderId, recipientId);
        if (contextError != null)
        {
            // ESC-48: a resend of a message this sender already stored (its answer was lost, and
            // the context closed since, e.g. a crop thread from before ESC-48) is a duplicate, not
            // a refusal: the client would otherwise delete a message the server keeps.
            var stored = await _messageRepo.GetByIdAsync(messageId);
            if (stored != null && ItemString(stored, "senderId") == senderId)
            {
                return MessageError(operation, "duplicate", "Message already exists.");
            }

            return MessageError(operation, "error", contextError);
        }

        // Denormalize both parties' names authoritatively from the users table
        // (overwrites anything the client might have sent). Phone numbers are not
        // stored on messages (ESC-48): the parties take them from the order.
        var sender = await _userRepo.GetUserByIdAsync(senderId);
        var recipient = await _userRepo.GetUserByIdAsync(recipientId);
        if (sender == null || recipient == null)
        {
            return MessageError(operation, "error",
                "MESSAGE_VALIDATION: sender or recipient user not found.");
        }

        // ACC-02: no message to an account that is being or has been deleted.
        if (!string.IsNullOrEmpty(recipient.DeletionState))
        {
            return MessageError(operation, "error", "SYNC_VALIDATION: The recipient has deleted their account.");
        }

        operation.Data["senderName"] = sender.DisplayName ?? sender.FullName ?? "";
        operation.Data["recipientName"] = recipient.DisplayName ?? recipient.FullName ?? "";

        var createResult = await _messageRepo.CreateAsync(operation.EntityId, operation.Data);
        return createResult.IsSuccess
            ? new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "success",
                ServerVersion = 1,
                ServerData = createResult.Value
            }
            : new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "duplicate",
                Message = createResult.Error?.Message ?? "Message already exists."
            };
    }

    /// <summary>
    /// Verify sender and recipient are the two legitimate parties for the
    /// referenced context, and that the context is open for contact.
    /// Returns null when valid, or an error message string.
    /// ESC-48: crop and trip threads (before any order) take no new messages from anyone,
    /// and order and delivery threads only while the order's contact is open
    /// (<see cref="ContactReveal"/>), so no pre-order channel is left for a buyer and a
    /// farmer or trucker to agree to trade off the platform.
    /// </summary>
    private async Task<string?> VerifyMessageContextAsync(
        string contextType, string contextId, string senderId, string recipientId)
    {
        switch (contextType)
        {
            case "crop":
            case "trip":
                return ContactReveal.LockedMessage;

            case "order":
                {
                    var order = await _orderRepo.GetByIdAsync(contextId);
                    if (order == null)
                    {
                        return "MESSAGE_VALIDATION: Order not found.";
                    }

                    var participants = new HashSet<string>(StringComparer.Ordinal);
                    AddIfPresent(participants, ItemString(order, "buyerId"));
                    AddIfPresent(participants, ItemString(order, "farmerId"));
                    AddIfPresent(participants, ItemString(order, "truckerId"));
                    // ESC-15: contact is per party; a trucker who declined, expired or dropped out
                    // stays on the row but can no longer be reached (and cannot reach anyone).
                    return VerifyDistinctParticipants(participants, senderId, recipientId)
                           ?? (ContactReveal.IsOpenFor(order, senderId) && ContactReveal.IsOpenFor(order, recipientId)
                               ? null
                               : ContactReveal.LockedMessage);
                }

            case "delivery":
                {
                    var delivery = await GetDeliveryItemAsync(contextId);
                    if (delivery == null)
                    {
                        return "MESSAGE_VALIDATION: Delivery not found.";
                    }

                    var participants = new HashSet<string>(StringComparer.Ordinal);
                    AddIfPresent(participants, AttrString(delivery, "buyerId"));
                    AddIfPresent(participants, AttrString(delivery, "farmerId"));
                    AddIfPresent(participants, AttrString(delivery, "truckerId"));
                    var participantError = VerifyDistinctParticipants(participants, senderId, recipientId);
                    if (participantError != null)
                    {
                        return participantError;
                    }

                    // The delivery's order decides; a delivery without one stays closed.
                    var orderId = AttrString(delivery, "orderId");
                    var order = string.IsNullOrEmpty(orderId) ? null : await _orderRepo.GetByIdAsync(orderId);
                    return order != null
                           && ContactReveal.IsOpenFor(order, senderId) && ContactReveal.IsOpenFor(order, recipientId)
                        ? null
                        : ContactReveal.LockedMessage;
                }

            default:
                return "MESSAGE_VALIDATION: Unknown contextType.";
        }
    }

    /// <summary>Both parties must be distinct members of the participant set.</summary>
    private static string? VerifyDistinctParticipants(
        HashSet<string> participants, string senderId, string recipientId)
    {
        if (!participants.Contains(senderId) || !participants.Contains(recipientId))
        {
            return "MESSAGE_FORBIDDEN: You are not a participant in this conversation.";
        }

        return null;
    }

    private static void AddIfPresent(HashSet<string> set, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            set.Add(value);
        }
    }

    private static string ItemString(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    private static string AttrString(Dictionary<string, AttributeValue> item, string key)
        => item.TryGetValue(key, out var v) ? v.S ?? "" : "";

    private static SyncOperationResult MessageError(
        SyncOperationRequest operation, string status, string message)
        => new()
        {
            OperationId = operation.OperationId,
            Status = status,
            Message = message
        };

    /// <summary>
    /// Validate message fields: ids required, contextType in the allowed set,
    /// body non-empty and ≤ 1000 chars, createdAt (if present) a valid timestamp.
    /// </summary>
    private static List<string> ValidateMessageFields(Dictionary<string, object> data)
    {
        var errors = new List<string>();

        foreach (var required in new[] { "messageId", "conversationId", "contextId", "senderId", "recipientId" })
        {
            if (!data.TryGetValue(required, out var v) || string.IsNullOrWhiteSpace(v?.ToString()))
            {
                errors.Add($"{required} is required.");
            }
        }

        if (!data.TryGetValue("contextType", out var ctObj)
            || !AllowedMessageContextTypes.Contains(ctObj?.ToString() ?? ""))
        {
            errors.Add("contextType must be one of crop, order, trip, delivery.");
        }

        if (!data.TryGetValue("body", out var bodyObj) || string.IsNullOrWhiteSpace(bodyObj?.ToString()))
        {
            errors.Add("body is required.");
        }
        else if (bodyObj!.ToString()!.Length > 1000)
        {
            errors.Add("body must be 1000 characters or fewer.");
        }

        if (data.TryGetValue("createdAt", out var caObj) && caObj != null
            && !DateTime.TryParse(caObj.ToString(), out _))
        {
            errors.Add("createdAt must be a valid ISO-8601 timestamp.");
        }

        return errors;
    }

    /// <summary>
    /// SIM-37: update the caller's own user profile from a sync `update`. Only
    /// `preferredLanguage` is allow-listed (en, am, om); every other field in the
    /// payload is ignored, so a client cannot change its role, woreda or phone
    /// through this path. Lets the language chosen in Settings follow the user to
    /// other devices and gives ESC-21 the right language for order notifications.
    /// </summary>
    private async Task<SyncOperationResult> ProcessUserAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        ILambdaContext context)
    {
        if (_userRepo == null)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "USER_PROFILE_UNAVAILABLE: Profile sync is not enabled on this environment."
            };
        }

        if (operation.Action != "update")
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "USER_PROFILE_VALIDATION: Only profile updates are supported for the user entity."
            };
        }

        // A user may only update their own profile.
        if (string.IsNullOrEmpty(authenticatedUserId) || operation.EntityId != authenticatedUserId)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "USER_PROFILE_UNAUTHORIZED: A user can only update their own profile."
            };
        }

        var language = operation.Data?.GetValueOrDefault("preferredLanguage")?.ToString();
        if (string.IsNullOrEmpty(language))
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "USER_PROFILE_VALIDATION: preferredLanguage is required."
            };
        }

        if (!Validation.RequestValidator.ValidLanguages.Contains(language))
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "USER_PROFILE_VALIDATION: preferredLanguage must be en, am or om."
            };
        }

        var user = await _userRepo.GetUserByIdAsync(authenticatedUserId, consistentRead: true);
        if (user == null)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "USER_PROFILE_NOT_FOUND: No profile exists for the authenticated user."
            };
        }

        // Store the canonical lowercase code and no-op when unchanged, so a repeated
        // choice does not churn the version.
        var canonical = language.ToLowerInvariant();
        if (string.Equals(user.PreferredLanguage, canonical, StringComparison.OrdinalIgnoreCase))
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "success",
                ServerVersion = user.Version
            };
        }

        var updated = await _userRepo.UpdateUserAsync(user with { PreferredLanguage = canonical }, user.Version);
        if (!updated.IsSuccess)
        {
            // A rare concurrent write bumped the version; the client retries the op.
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = $"USER_PROFILE_CONFLICT: {updated.Error?.Message ?? "Could not save the language."}"
            };
        }

        context.Logger.LogInformation(
            $"SIM-37: preferredLanguage updated for {MoneyAlarmLog.LogId(authenticatedUserId)}.");

        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "success",
            ServerVersion = updated.Value!.Version
        };
    }

    /// <summary>
    /// Process a match_event sync operation. Append-only telemetry: only
    /// `create` is supported, only by the buyer whose intent the event belongs
    /// to. Events feed the future learned ranker (specs/contracts/match-events.md);
    /// a failure to log must never block the marketplace flow, so clients treat
    /// errors here as non-fatal.
    /// </summary>
    private async Task<SyncOperationResult> ProcessMatchEventAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        string? authenticatedRole)
    {
        if (_matchEventRepo == null)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "MATCH_EVENT_UNAVAILABLE: Match-event logging is not enabled on this environment."
            };
        }

        if (!string.Equals(authenticatedRole, "buyer", StringComparison.OrdinalIgnoreCase))
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "MATCH_EVENT_UNAUTHORIZED: Only buyers can log match events."
            };
        }

        if (operation.Action != "create")
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "MATCH_EVENT_VALIDATION: Match events are append-only (create only)."
            };
        }

        if (operation.Data == null || operation.Data.Count == 0)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "Operation requires data payload."
            };
        }

        if (!operation.Data.TryGetValue("buyerId", out var buyerIdObj)
            || buyerIdObj?.ToString() != authenticatedUserId)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "MATCH_EVENT_UNAUTHORIZED: buyerId must match authenticated user."
            };
        }

        var unknownFields = operation.Data.Keys
            .Where(k => !AllowedMatchEventFields.Contains(k))
            .ToList();
        if (unknownFields.Count > 0)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = $"MATCH_EVENT_VALIDATION: Unknown fields: {string.Join(", ", unknownFields)}"
            };
        }

        var errors = ValidateMatchEventFields(operation.Data);
        if (errors.Count > 0)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = $"MATCH_EVENT_VALIDATION: {string.Join(" ", errors)}"
            };
        }

        var intentId = operation.Data.GetValueOrDefault("intentId")?.ToString() ?? "";
        await _matchEventRepo.CreateAsync(
            operation.EntityId, intentId, authenticatedUserId!, operation.Data);

        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "success",
            ServerVersion = 1,
        };
    }

    /// <summary>Validate match-event field values (see AllowedMatchEventFields).</summary>
    private static List<string> ValidateMatchEventFields(Dictionary<string, object> data)
    {
        var errors = new List<string>();

        var eventType = data.GetValueOrDefault("eventType")?.ToString() ?? "";
        if (!AllowedMatchEventTypes.Contains(eventType))
        {
            errors.Add("eventType must be one of: shown, opened, order_created.");
        }

        if (string.IsNullOrWhiteSpace(data.GetValueOrDefault("intentId")?.ToString()))
        {
            errors.Add("intentId is required.");
        }

        // Interaction events must say WHICH listing was interacted with.
        if (eventType is "opened" or "order_created"
            && string.IsNullOrWhiteSpace(data.GetValueOrDefault("listingId")?.ToString()))
        {
            errors.Add("listingId is required for opened/order_created events.");
        }

        // Parse invariantly: client numbers are JSON (period decimal), so a
        // comma-decimal server locale must not reject a fractional score.
        if (data.TryGetValue("score", out var sObj)
            && (!double.TryParse(sObj?.ToString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var s)
                || s < 0 || s > 1))
        {
            errors.Add("score must be a number in [0, 1].");
        }

        if (data.TryGetValue("rank", out var rObj)
            && (!int.TryParse(rObj?.ToString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var r)
                || r < 0))
        {
            errors.Add("rank must be a non-negative integer.");
        }

        if (data.TryGetValue("impressions", out var iObj))
        {
            var impressions = iObj?.ToString() ?? "";
            if (impressions.Length > MaxImpressionsLength)
            {
                errors.Add($"impressions must not exceed {MaxImpressionsLength} characters.");
            }
            else if (impressions.Length > 0 && !IsValidJson(impressions))
            {
                // Contract: impressions is a JSON string. Guard it so the
                // training corpus stays parseable.
                errors.Add("impressions must be a valid JSON string.");
            }
        }

        if (data.TryGetValue("matchCount", out var mcObj)
            && (!int.TryParse(mcObj?.ToString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var mc)
                || mc < 0))
        {
            errors.Add("matchCount must be a non-negative integer.");
        }

        if (data.TryGetValue("occurredAt", out var oObj)
            && !string.IsNullOrWhiteSpace(oObj?.ToString())
            && !DateTime.TryParse(oObj!.ToString(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out _))
        {
            errors.Add("occurredAt must be an ISO 8601 date.");
        }

        return errors;
    }

    /// <summary>Whether <paramref name="value"/> parses as a JSON document.</summary>
    private static bool IsValidJson(string value)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Persist a fraud signal for admin review. Best-effort: signals feed the
    /// admin review queue, so a write failure must never block or fail the
    /// underlying sync operation. Skips silently when the user id is unknown
    /// (the table's partition key cannot be empty).
    /// </summary>
    private async Task TryRecordFraudSignalAsync(
        string userId, string signalType, string? entityId, string entityType,
        string? description, ILambdaContext context)
    {
        if (_fraudService == null || string.IsNullOrEmpty(userId))
        {
            return;
        }

        // FRD-08: the service keeps one row per user, type and hour; one attempt per batch is enough.
        if (!_batchSignals.Add($"{userId}|{signalType}"))
        {
            return;
        }

        try
        {
            await _fraudService.RecordFraudSignalAsync(
                userId, signalType, entityId, entityType, description);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Failed to record fraud signal {signalType} for user {userId}: [{ex.GetType().Name}]");
        }
    }

    /// <summary>
    /// FRD-09: run one transaction pattern check and write each hit as a signal on
    /// <paramref name="userId"/> with the observed entity. Called only after the business write it
    /// observes succeeded, and never changes the answer: a failed history read is logged (exception type
    /// only) and writes nothing, a failed signal write is logged by <see cref="TryRecordFraudSignalAsync"/>.
    /// No rules wired, no fraud service or no subject: nothing runs.
    /// </summary>
    private async Task TryRecordPatternSignalsAsync(
        string? userId, string entityId, string entityType, string rule,
        Func<TransactionPatternService, Task<IReadOnlyList<PatternHit>>> check, ILambdaContext context)
    {
        if (PatternRules == null || _fraudService == null || string.IsNullOrEmpty(userId))
        {
            return;
        }

        IReadOnlyList<PatternHit> hits;
        try
        {
            hits = await check(PatternRules);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Pattern check {rule} skipped for {entityType} {MoneyAlarmLog.LogId(entityId)}: [{ex.GetType().Name}]");
            return;
        }

        foreach (var hit in hits)
        {
            await TryRecordFraudSignalAsync(userId, hit.SignalType, entityId, entityType, hit.Description, context);
        }
    }

    /// <summary>
    /// FRD-09: the pattern rules a written delivery update feeds. A dispute this update froze the money for,
    /// sent by the delivery's buyer: the buyer's dispute rate (the escrow as the entity). A confirmed delivery
    /// with a trucker: the impossible delivery time, written on the delivery's trucker (not the caller, who is
    /// usually the buyer). The pickup side (point, <c>pickedUpAt</c>, <c>pickupCapturedAt</c>) comes from the row
    /// as it was before this write, the drop-off side from the written row (FUP-246), so the confirm cannot move
    /// the pickup. A self-pickup has no trucker and no trip between the scans.
    /// </summary>
    private async Task TryRecordDeliveryPatternSignalsAsync(
        SyncOperationRequest operation, Dictionary<string, AttributeValue> existing,
        Dictionary<string, AttributeValue> written, DeliveryDisputeFreezeResult? freeze,
        string? authenticatedUserId, ILambdaContext context)
    {
        if (PatternRules == null)
        {
            return;
        }

        if (freeze is { Outcome: DeliveryDisputeFreeze.Frozen, EscrowId: { Length: > 0 } escrowId }
            && !string.IsNullOrEmpty(authenticatedUserId)
            && AttrString(existing, "buyerId") == authenticatedUserId)
        {
            await TryRecordPatternSignalsAsync(authenticatedUserId, escrowId, "escrow", "dispute_rate",
                rules => rules.CheckDisputeRateAsync(authenticatedUserId, escrowId, DateTime.UtcNow), context);
        }

        if (AttrString(written, "status") == "delivered"
            && AttrString(written, "deliveryType") != SelfPickupDeliveryType
            && AttrString(written, "truckerId") is { Length: > 0 } truckerId)
        {
            await TryRecordPatternSignalsAsync(truckerId, operation.EntityId, "delivery", "delivery_time",
                rules => Task.FromResult(rules.CheckDeliveryTime(TransactionPatternRules.DeliveryTimeFactsFrom(existing, written))),
                context);
        }
    }

    /// <summary>
    /// ESC-33 and FUP-203: whether a confirmed delivery's trucker can get a <c>GPS_ANOMALY</c> row, judged
    /// from the trucker's own check-ins (<see cref="TryRecordTruckerGpsAnomalyAsync"/>). It follows
    /// <c>reputationPenalties</c> (off in every environment): with the evidence the trucker's own phone, the
    /// row needs no flag of its own (fraud-detection-architecture.md 15.3).
    /// </summary>
    internal bool GpsAnomalyOnTrucker { get; init; } = ReputationFlags.PenaltiesEnabled;

    /// <summary>
    /// The <c>GPS_ANOMALY</c> signal of the confirming buyer (fraud-detection-architecture.md Rule 4 and section
    /// 15.3), written after the delivery write succeeded: the caller, whose phone sent the drop-off coordinates,
    /// exactly as before ESC-33, so a buyer and a trucker who fake deliveries together keep adding to the
    /// buyer's score. The buyer's coordinates feed this row only; the trucker's row is judged from the trucker's
    /// own check-ins (FUP-203). Best effort like every signal.
    /// </summary>
    private async Task TryRecordGpsAnomalySignalAsync(
        SyncOperationRequest operation, FraudCheckResult gpsCheck, string? authenticatedUserId, ILambdaContext context)
    {
        if (_fraudService == null || string.IsNullOrEmpty(authenticatedUserId))
        {
            return;
        }

        var signalType = gpsCheck.FlagReason ?? FraudDetectionService.GpsAnomalySignal;
        await TryRecordFraudSignalAsync(
            authenticatedUserId, signalType, operation.EntityId, "delivery", gpsCheck.Message, context);
    }

    /// <summary>
    /// FRD-08: record a typed fraud signal on the caller for a refusal, then return the refusal as
    /// built. Best-effort like every signal: the answer is the same whether the write works or not.
    /// A null <paramref name="signalType"/> records nothing.
    /// </summary>
    private async Task<SyncOperationResult> RefuseWithSignalAsync(
        SyncOperationResult refusal, string? signalType, SyncOperationRequest operation,
        string entityType, string? callerId, ILambdaContext context)
    {
        if (signalType != null)
        {
            await TryRecordFraudSignalAsync(
                callerId ?? "", signalType, operation.EntityId, entityType, refusal.Message, context);
        }

        return refusal;
    }

    /// <summary>
    /// FRD-08: the signal of a role gate. A token without the role claim (an access token instead
    /// of the ID token) fails the gate too, but that is a client set-up problem, not a probe.
    /// </summary>
    private static string? RoleGateSignal(string? authenticatedRole) =>
        string.IsNullOrWhiteSpace(authenticatedRole) ? null : FraudDetectionService.RoleForbiddenSignal;

    /// <summary>
    /// FRD-04 (fraud-detection-architecture.md 17.1): the caller's risk tier at a user gate, read once per
    /// batch. LOW without a fraud service, and for anything but a computed score (section 14.4).
    /// </summary>
    private async Task<RiskTier> CallerRiskTierAsync(string userId)
        => _batchRiskScores == null
            ? RiskTier.Low
            : RiskGatePolicy.TierAtUserGate(await _batchRiskScores.GetAsync(userId));

    /// <summary>FRD-04: a create or a payment refused because the caller's risk tier is CRITICAL; nothing written.</summary>
    private static SyncOperationResult AccountHeldResult(SyncOperationRequest operation) => new()
    {
        OperationId = operation.OperationId,
        Status = "error",
        Message = RiskGatePolicy.SyncMessage
    };

    /// <summary>
    /// Process a crop_listing sync operation (create, update, delete).
    /// Uses version-based conflict detection per sync-protocol.md.
    /// </summary>
    private async Task<SyncOperationResult> ProcessCropListingAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        string? authenticatedRole,
        ILambdaContext context)
    {
        switch (operation.Action)
        {
            case "create":
                {
                    if (operation.Data == null || operation.Data.Count == 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "Create operation requires data payload."
                        };
                    }

                    // Role check: only farmers can create crop listings
                    if (!string.Equals(authenticatedRole, "farmer", StringComparison.OrdinalIgnoreCase))
                    {
                        return await RefuseWithSignalAsync(new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "CROP_UNAUTHORIZED: Only farmers can create crop listings."
                        }, RoleGateSignal(authenticatedRole), operation, "crop", authenticatedUserId, context);
                    }

                    // Validate farmerId matches authenticated user
                    if (!operation.Data.TryGetValue("farmerId", out var farmerIdObj)
                        || farmerIdObj?.ToString() != authenticatedUserId)
                    {
                        return await RefuseWithSignalAsync(new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "CROP_UNAUTHORIZED: farmerId must match authenticated user."
                        }, FraudDetectionService.RoleForbiddenSignal, operation, "crop", authenticatedUserId, context);
                    }

                    // ESC-29 (reputation-api.md 8.1): a restricted farmer lists nothing new.
                    if (RefuseIfRestricted(operation) is { } cropRestricted)
                    {
                        return cropRestricted;
                    }

                    // FUP-98: the same profile read gives the woreda the screen below exempts.
                    string? farmerProfileWoreda = null;
                    var cropContactError = await StampOwnerContactAsync(
                        operation.Data, authenticatedUserId!, "farmerName", "farmerPhone", context,
                        owner => farmerProfileWoreda = owner.Woreda);
                    if (cropContactError != null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"CROP_VALIDATION: {cropContactError}"
                        };
                    }

                    // Field whitelist — reject unknown fields
                    var unknownCreateFields = operation.Data.Keys
                        .Where(k => !AllowedCropFields.Contains(k) && k != "version")
                        .ToList();
                    if (unknownCreateFields.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"CROP_VALIDATION: Unknown fields: {string.Join(", ", unknownCreateFields)}"
                        };
                    }

                    // FIX-009: Validate crop field values
                    var createValidationErrors = ValidateCropFields(operation.Data, isCreate: true);
                    if (createValidationErrors.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"CROP_VALIDATION: {string.Join(" ", createValidationErrors)}"
                        };
                    }

                    // FUP-49: no phone number in text every buyer reads (FUP-98: the woreda too,
                    // unless it is the profile woreda).
                    var createContactFields = Validation.ContactInText.FieldsWithPhoneNumber(
                        operation.Data, CropContactScreenedFields);
                    if (IsProfileWoreda(operation.Data, farmerProfileWoreda))
                    {
                        createContactFields.Remove("woreda");
                    }

                    if (createContactFields.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = ContactInTextRefusal("CROP_CONTACT_IN_TEXT", createContactFields)
                        };
                    }

                    // FRD-04 (fraud-detection-architecture.md 17.1): after the field rules and before the
                    // listing checks below, so an account under review writes no velocity signal either.
                    if (await CallerRiskTierAsync(authenticatedUserId!) == RiskTier.Critical)
                    {
                        return AccountHeldResult(operation);
                    }

                    // ── Fraud detection (F-14) ──
                    if (_fraudService != null)
                    {
                        var cropType = operation.Data.GetValueOrDefault("cropType")?.ToString() ?? "";
                        var pricePerKg = 0m;
                        if (operation.Data.TryGetValue("pricePerKg", out var ppkObj))
                        {
                            _ = decimal.TryParse(ppkObj?.ToString(), out pricePerKg);
                        }

                        var fraudCheck = await _fraudService.CheckListingCreationAsync(
                            authenticatedUserId!, cropType, pricePerKg);

                        if (!fraudCheck.IsAllowed)
                        {
                            await TryRecordFraudSignalAsync(
                                authenticatedUserId!, fraudCheck.ErrorCode ?? "FRAUD_REJECTED",
                                operation.EntityId, "crop", fraudCheck.Message, context);

                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = $"{fraudCheck.ErrorCode}: {fraudCheck.Message}"
                            };
                        }

                        if (fraudCheck.IsFlagged)
                        {
                            operation.Data["isFlagged"] = true;
                            operation.Data["flagReason"] = fraudCheck.FlagReason ?? "UNKNOWN";
                            context.Logger.LogWarning(
                                $"Fraud flag on listing {MoneyAlarmLog.LogId(operation.EntityId)}: {fraudCheck.FlagReason}: {MoneyAlarmLog.LogText(fraudCheck.Message)}");
                            await TryRecordFraudSignalAsync(
                                authenticatedUserId!, fraudCheck.FlagReason ?? "UNKNOWN",
                                operation.EntityId, "crop", fraudCheck.Message, context);
                        }
                    }

                    // FIX-010: Force initial status to "available" on create
                    operation.Data["status"] = "available";

                    NormalizeCropPhotoIds(operation.Data);
                    StampOwnerReputation(operation.Data);

                    var createResult = await _cropRepo.CreateAsync(operation.EntityId, operation.Data);

                    if (createResult.IsSuccess)
                    {
                        // Record for velocity tracking (F-14)
                        if (_fraudService != null)
                        {
                            await _fraudService.RecordListingCreatedAsync(authenticatedUserId!);
                        }

                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "success",
                            ServerVersion = 1,
                            ServerData = createResult.Value
                        };
                    }

                    // Already exists: return conflict with server data, but only to the
                    // owner: a create that names another farmer's id must not read that
                    // farmer's listing (role-visibility rule).
                    var existing = await _cropRepo.GetByIdAsync(operation.EntityId);
                    var serverVersion = existing != null && existing.TryGetValue("version", out var v)
                        ? int.TryParse(v.ToString(), out var sv) ? sv : 1
                        : 1;
                    var isOwner = existing != null && IsParty(existing, authenticatedUserId, "farmerId");

                    // FRD-08: create ids are UUIDs made on the device, so naming another farmer's
                    // listing is a probe. The owner's own retry is not.
                    if (existing != null && !isOwner)
                    {
                        await TryRecordFraudSignalAsync(
                            authenticatedUserId!, FraudDetectionService.IdorCreateConflictSignal, operation.EntityId,
                            "crop", "Create named a crop listing id that belongs to another user.", context);
                    }

                    return new SyncOperationResult
                    {
                        OperationId = operation.OperationId,
                        Status = "conflict",
                        ServerVersion = serverVersion,
                        ServerData = isOwner ? existing : null,
                        Message = "Crop listing already exists on server."
                    };
                }

            case "update":
                {
                    if (operation.Data == null || operation.Data.Count == 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "Update operation requires data payload."
                        };
                    }

                    // Validate ownership — fetch existing item
                    var existingForUpdate = await _cropRepo.GetByIdAsync(operation.EntityId);
                    if (existingForUpdate == null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "Crop listing not found."
                        };
                    }

                    if (!existingForUpdate.TryGetValue("farmerId", out var updateFarmerId)
                        || updateFarmerId?.ToString() != authenticatedUserId)
                    {
                        return await RefuseWithSignalAsync(new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "CROP_UNAUTHORIZED: You can only update your own crop listings."
                        }, FraudDetectionService.RoleForbiddenSignal, operation, "crop", authenticatedUserId, context);
                    }

                    // Field whitelist — reject unknown fields
                    var unknownUpdateFields = operation.Data.Keys
                        .Where(k => !AllowedCropFields.Contains(k) && k != "version")
                        .ToList();
                    if (unknownUpdateFields.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"CROP_VALIDATION: Unknown fields: {string.Join(", ", unknownUpdateFields)}"
                        };
                    }

                    // SIM-02: ownership/contact fields are immutable after create.
                    var cropIdentityError = RejectImmutableFieldChanges(
                        operation.Data,
                        ImmutableCropIdentityFields,
                        key => existingForUpdate.TryGetValue(key, out var stored) ? stored?.ToString() : null);
                    if (cropIdentityError != null)
                    {
                        return await RefuseWithSignalAsync(new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"CROP_VALIDATION: {cropIdentityError}"
                        }, FraudDetectionService.ImmutableFieldChangeSignal, operation, "crop", authenticatedUserId, context);
                    }

                    // FIX-009: Validate crop field values
                    var updateValidationErrors = ValidateCropFields(operation.Data, isCreate: false);
                    if (updateValidationErrors.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"CROP_VALIDATION: {string.Join(" ", updateValidationErrors)}"
                        };
                    }

                    // FUP-49: a changed text must not carry a phone number; an unchanged echo
                    // of the stored value is not checked.
                    var updateContactFields = Validation.ContactInText.FieldsWithPhoneNumber(
                        operation.Data, CropContactScreenedFields,
                        key => existingForUpdate.TryGetValue(key, out var stored) ? stored?.ToString() : null);

                    // FUP-98: the update path has not read the profile, so it is read only for a
                    // woreda that would otherwise be refused (one read, rare).
                    if (updateContactFields.Contains("woreda") && _userRepo != null
                        && IsProfileWoreda(operation.Data, (await _userRepo.GetUserByIdAsync(authenticatedUserId!))?.Woreda))
                    {
                        updateContactFields.Remove("woreda");
                    }

                    if (updateContactFields.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = ContactInTextRefusal("CROP_CONTACT_IN_TEXT", updateContactFields)
                        };
                    }

                    // FIX-010: Validate crop status transitions
                    if (operation.Data.TryGetValue("status", out var newCropStatusObj))
                    {
                        var newCropStatus = newCropStatusObj?.ToString();
                        var currentCropStatus = existingForUpdate.TryGetValue("status", out var curStatusObj)
                            ? curStatusObj?.ToString() ?? "available"
                            : "available";

                        if (!CropTransitions.TryGetValue(currentCropStatus, out var allowedCropStatuses)
                            || newCropStatus == null || !allowedCropStatuses.Contains(newCropStatus))
                        {
                            // SIM-28: the loser of a status race is behind, not wrong.
                            var storedCropVersion = existingForUpdate.TryGetValue("version", out var scv)
                                && int.TryParse(scv?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var scvn)
                                ? scvn : 0;
                            if (StaleClientVersion(operation, storedCropVersion) is int cropRaceVersion)
                            {
                                return StaleVersionConflict(operation, storedCropVersion, cropRaceVersion, existingForUpdate);
                            }

                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = $"CROP_INVALID_TRANSITION: Cannot transition from '{currentCropStatus}' to '{newCropStatus}'."
                            };
                        }

                        // ESC-18: an update to 'deleted' is a delete, with the same guard.
                        if (newCropStatus == "deleted")
                        {
                            var removalRefusal = await CheckListingRemovalAllowedAsync(operation, context);
                            if (removalRefusal != null)
                            {
                                return removalRefusal;
                            }
                        }
                    }

                    // SIM-08: op-level clientVersion (contract) or data.version (shipped clients).
                    TryGetClientVersion(operation, out var clientVersion);

                    NormalizeCropPhotoIds(operation.Data);

                    var updateResult = await _cropRepo.UpdateAsync(
                        operation.EntityId, operation.Data, clientVersion);

                    if (updateResult.IsSuccess)
                    {
                        var newVersion = updateResult.Value != null &&
                                         updateResult.Value.TryGetValue("version", out var nv)
                            ? int.TryParse(nv.ToString(), out var snv) ? snv : clientVersion + 1
                            : clientVersion + 1;

                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "success",
                            ServerVersion = newVersion,
                            ServerData = updateResult.Value
                        };
                    }

                    // Conflict — return server's current data
                    var serverData = await _cropRepo.GetByIdAsync(operation.EntityId);
                    var serverVer = serverData != null && serverData.TryGetValue("version", out var sv2)
                        ? int.TryParse(sv2.ToString(), out var svn) ? svn : 0
                        : 0;

                    return new SyncOperationResult
                    {
                        OperationId = operation.OperationId,
                        Status = "conflict",
                        ServerVersion = serverVer,
                        ServerData = serverData,
                        Message = updateResult.Error?.Message ?? "Version conflict."
                    };
                }

            case "delete":
                {
                    // SIM-08: op-level clientVersion (contract) or data.version (shipped clients).
                    TryGetClientVersion(operation, out var clientVersion);

                    // Validate ownership
                    var existingForDelete = await _cropRepo.GetByIdAsync(operation.EntityId);
                    if (existingForDelete == null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "Crop listing not found."
                        };
                    }

                    if (!existingForDelete.TryGetValue("farmerId", out var deleteFarmerId)
                        || deleteFarmerId?.ToString() != authenticatedUserId)
                    {
                        return await RefuseWithSignalAsync(new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "CROP_UNAUTHORIZED: You can only delete your own crop listings."
                        }, FraudDetectionService.RoleForbiddenSignal, operation, "crop", authenticatedUserId, context);
                    }

                    // ESC-18: not while orders still depend on the listing.
                    var removalRefusal = await CheckListingRemovalAllowedAsync(operation, context);
                    if (removalRefusal != null)
                    {
                        return removalRefusal;
                    }

                    var deleteResult = await _cropRepo.DeleteAsync(operation.EntityId, clientVersion);

                    if (deleteResult.IsSuccess)
                    {
                        var newVersion = deleteResult.Value != null &&
                                         deleteResult.Value.TryGetValue("version", out var nv)
                            ? int.TryParse(nv.ToString(), out var snv) ? snv : clientVersion + 1
                            : clientVersion + 1;

                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "success",
                            ServerVersion = newVersion
                        };
                    }

                    if (deleteResult.Error?.HttpStatus == 404)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "Crop listing not found."
                        };
                    }

                    // Conflict
                    var serverData = await _cropRepo.GetByIdAsync(operation.EntityId);
                    var serverVer = serverData != null && serverData.TryGetValue("version", out var sv3)
                        ? int.TryParse(sv3.ToString(), out var svn) ? svn : 0
                        : 0;

                    return new SyncOperationResult
                    {
                        OperationId = operation.OperationId,
                        Status = "conflict",
                        ServerVersion = serverVer,
                        ServerData = serverData,
                        Message = deleteResult.Error?.Message ?? "Version conflict on delete."
                    };
                }

            default:
                return new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "error",
                    Message = $"Invalid action: '{operation.Action}'."
                };
        }
    }

    /// <summary>
    /// Process a trip sync operation (create, update, delete).
    /// Uses version-based conflict detection per trip-listing-api.md.
    /// Operates directly on DynamoDB (trips don't use the full CRUD repo pattern).
    /// </summary>
    private async Task<SyncOperationResult> ProcessTripAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        string? authenticatedRole,
        ILambdaContext context)
    {
        switch (operation.Action)
        {
            case "create":
                {
                    if (operation.Data == null || operation.Data.Count == 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "Create operation requires data payload."
                        };
                    }

                    // Role check: only truckers can create trip listings
                    if (!string.Equals(authenticatedRole, "trucker", StringComparison.OrdinalIgnoreCase))
                    {
                        return await RefuseWithSignalAsync(new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "TRIP_UNAUTHORIZED: Only truckers can create trip listings."
                        }, RoleGateSignal(authenticatedRole), operation, "trip", authenticatedUserId, context);
                    }

                    // C2: Validate truckerId matches authenticated user
                    if (!operation.Data.TryGetValue("truckerId", out var truckerIdObj)
                        || truckerIdObj?.ToString() != authenticatedUserId)
                    {
                        return await RefuseWithSignalAsync(new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "TRIP_UNAUTHORIZED: truckerId must match authenticated user."
                        }, FraudDetectionService.RoleForbiddenSignal, operation, "trip", authenticatedUserId, context);
                    }

                    // ESC-29 (reputation-api.md 8.1): a restricted trucker posts no new trip.
                    if (RefuseIfRestricted(operation) is { } tripRestricted)
                    {
                        return tripRestricted;
                    }

                    var tripContactError = await StampOwnerContactAsync(
                        operation.Data, authenticatedUserId!, "truckerName", "truckerPhone", context);
                    if (tripContactError != null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"TRIP_VALIDATION: {tripContactError}"
                        };
                    }

                    // C3: Field whitelist — reject unknown fields
                    var unknownFields = operation.Data.Keys
                        .Where(k => !AllowedTripFields.Contains(k) && k != "version")
                        .ToList();
                    if (unknownFields.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"TRIP_VALIDATION: Unknown fields: {string.Join(", ", unknownFields)}"
                        };
                    }

                    // C3: Validate required fields
                    var validationErrors = ValidateTripFields(operation.Data, isCreate: true);
                    if (validationErrors.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"TRIP_VALIDATION: {string.Join(" ", validationErrors)}"
                        };
                    }

                    // FUP-49: no phone number in text every buyer reads.
                    var createContactFields = Validation.ContactInText.FieldsWithPhoneNumber(
                        operation.Data, TripContactScreenedFields);
                    if (createContactFields.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = ContactInTextRefusal("TRIP_CONTACT_IN_TEXT", createContactFields)
                        };
                    }

                    // FRD-04 (fraud-detection-architecture.md 17.1): after the field rules, before the write.
                    if (await CallerRiskTierAsync(authenticatedUserId!) == RiskTier.Critical)
                    {
                        return AccountHeldResult(operation);
                    }

                    NormalizeDepartureDate(operation.Data);

                    var now = DateTime.UtcNow.ToString("o");

                    var item = new Dictionary<string, AttributeValue>
                {
                    { "tripId", new AttributeValue { S = operation.EntityId } },
                    { "createdAt", new AttributeValue { S = now } },
                    { "updatedAt", new AttributeValue { S = now } },
                    { "version", new AttributeValue { N = "1" } }
                };

                    // Copy only whitelisted data fields (C3)
                    foreach (var kvp in operation.Data)
                    {
                        if (kvp.Key is "tripId" or "version" or "createdAt" or "updatedAt")
                        {
                            continue;
                        }

                        if (!AllowedTripFields.Contains(kvp.Key))
                        {
                            continue;
                        }

                        if (kvp.Key == "matchedListings")
                        {
                            continue; // handled separately
                        }

                        item[kvp.Key] = CropRepository.ToAttributeValue(kvp.Value);
                    }

                    // Force status to "open" for create (C3)
                    item["status"] = new AttributeValue { S = "open" };

                    // ESC-29 (reputation-api.md section 7): the owner's reputation at create.
                    var tripOwnerFields = new Dictionary<string, object>();
                    StampOwnerReputation(tripOwnerFields);
                    foreach (var (ownerField, ownerValue) in tripOwnerFields)
                    {
                        item[ownerField] = CropRepository.ToAttributeValue(ownerValue);
                    }

                    // Handle matchedListings
                    if (operation.Data.TryGetValue("matchedListings", out var ml))
                    {
                        var mlAttr = CropRepository.ToAttributeValue(ml);
                        item["matchedListings"] = mlAttr.S != null
                            ? mlAttr
                            : new AttributeValue { S = "[]" };
                    }
                    else
                    {
                        item["matchedListings"] = new AttributeValue { S = "[]" };
                    }

                    var request = new PutItemRequest
                    {
                        TableName = _tripsTable,
                        Item = item,
                        ConditionExpression = "attribute_not_exists(tripId)"
                    };

                    try
                    {
                        // The key is (tripId, createdAt), so the condition alone lets a
                        // replayed create add a second row (SIM-39).
                        if (await DynamoDbHelper.PartitionExistsAsync(
                                _dynamoDb, _tripsTable, "tripId", operation.EntityId))
                        {
                            throw new ConditionalCheckFailedException("Trip already exists.");
                        }

                        await _dynamoDb.PutItemAsync(request);

                        var result = CropRepository.FromItem(item);
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "success",
                            ServerVersion = 1,
                            ServerData = result
                        };
                    }
                    catch (ConditionalCheckFailedException)
                    {
                        // Already exists: return conflict with server data, only to the
                        // owning trucker (role-visibility rule).
                        var existing = await GetTripItemAsync(operation.EntityId);
                        var serverVersion = 1;
                        if (existing != null && existing.TryGetValue("version", out var v))
                        {
                            _ = int.TryParse(v.N, out serverVersion);
                        }
                        var isOwner = existing != null && IsParty(existing, authenticatedUserId, "truckerId");

                        // FRD-08: as for crops, a trip id of another trucker is a probe.
                        if (existing != null && !isOwner)
                        {
                            await TryRecordFraudSignalAsync(
                                authenticatedUserId!, FraudDetectionService.IdorCreateConflictSignal, operation.EntityId,
                                "trip", "Create named a trip id that belongs to another user.", context);
                        }

                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "conflict",
                            ServerVersion = serverVersion,
                            ServerData = isOwner ? CropRepository.FromItem(existing!) : null,
                            Message = "Trip already exists on server."
                        };
                    }
                }

            case "update":
                {
                    if (operation.Data == null || operation.Data.Count == 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "Update operation requires data payload."
                        };
                    }

                    // Get existing item to find sort key
                    var existing = await GetTripItemAsync(operation.EntityId);
                    if (existing == null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "Trip not found."
                        };
                    }

                    // C2: Validate authenticated user owns this trip
                    if (!existing.TryGetValue("truckerId", out var existingTruckerId)
                        || existingTruckerId.S != authenticatedUserId)
                    {
                        return await RefuseWithSignalAsync(new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "TRIP_UNAUTHORIZED: You can only update your own trips."
                        }, FraudDetectionService.RoleForbiddenSignal, operation, "trip", authenticatedUserId, context);
                    }

                    // H3: Block updates to terminal-state or deleted trips. ESC-17: a client that
                    // asks for what already holds, or is behind the stored version (the server
                    // closed or completed the trip meanwhile), gets the stored row as a conflict.
                    var currentStatus = existing.TryGetValue("status", out var existingStatusAttr)
                        ? existingStatusAttr.S : "open";
                    var requestedTripStatus = operation.Data.TryGetValue("status", out var requestedStatusObj)
                        ? requestedStatusObj?.ToString() : null;
                    if (existing.TryGetValue("deletedAt", out var deletedAtAttr)
                        && !string.IsNullOrEmpty(deletedAtAttr.S))
                    {
                        return TerminalTripConflict(operation, existing, requestedTripStatus)
                            ?? new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "TRIP_ALREADY_CANCELLED: Cannot update a deleted trip."
                            };
                    }
                    // F-SEC-06: Block updates to both cancelled and delivered (terminal states)
                    if (currentStatus is "cancelled" or "delivered")
                    {
                        return TerminalTripConflict(operation, existing, requestedTripStatus)
                            ?? new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "TRIP_TERMINAL_STATE: Cannot update a terminal-state trip."
                            };
                    }

                    // C3: Field whitelist — reject unknown fields
                    var unknownFields = operation.Data.Keys
                        .Where(k => !AllowedTripFields.Contains(k) && k != "version")
                        .ToList();
                    if (unknownFields.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"TRIP_VALIDATION: Unknown fields: {string.Join(", ", unknownFields)}"
                        };
                    }

                    // SIM-03: ownership/contact fields are immutable after create.
                    var tripIdentityError = RejectImmutableFieldChanges(
                        operation.Data,
                        ImmutableTripIdentityFields,
                        key => existing.TryGetValue(key, out var stored) ? stored.S : null);
                    if (tripIdentityError != null)
                    {
                        return await RefuseWithSignalAsync(new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"TRIP_VALIDATION: {tripIdentityError}"
                        }, FraudDetectionService.ImmutableFieldChangeSignal, operation, "trip", authenticatedUserId, context);
                    }

                    // C3: Validate present fields (SIM-60: a changed departureDate must
                    // satisfy the create window; an unchanged echo is accepted).
                    var validationErrors = ValidateTripFields(
                        operation.Data, isCreate: false,
                        storedDepartureDate: existing.TryGetValue("departureDate", out var storedDep) ? storedDep.S : null);
                    if (validationErrors.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"TRIP_VALIDATION: {string.Join(" ", validationErrors)}"
                        };
                    }

                    // FUP-49: a changed text must not carry a phone number; an unchanged echo
                    // of the stored value is not checked.
                    var updateContactFields = Validation.ContactInText.FieldsWithPhoneNumber(
                        operation.Data, TripContactScreenedFields,
                        key => existing.TryGetValue(key, out var stored) ? stored.S : null);
                    if (updateContactFields.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = ContactInTextRefusal("TRIP_CONTACT_IN_TEXT", updateContactFields)
                        };
                    }

                    NormalizeDepartureDate(operation.Data);

                    // ESC-17: a booked trip is frozen (date, price, route; capacity never below
                    // the bookings), `matched` is written by the server when the trip fills, and
                    // a trip with bookings may start from `open`. FUP-241: an app without
                    // X-App-Build gets TRIP_HAS_BOOKINGS as SYNC_VALIDATION; the signal keeps the code.
                    var bookingError = CheckBookedTripUpdate(operation.Data, existing);
                    if (bookingError != null)
                    {
                        return bookingError.Value.Code == "TRIP_HAS_BOOKINGS"
                            ? (await RefuseWithSignalAsync(new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = bookingError.Value.Message
                            }, FraudDetectionService.ImmutableFieldChangeSignal, operation, "trip", authenticatedUserId, context)) with
                            {
                                Message = OldClientRefusal.ForClient(
                                    bookingError.Value.Message, _batchAppBuild, "TRIP_HAS_BOOKINGS", operation.EntityType)
                            }
                            : new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = bookingError.Value.Message
                            };
                    }

                    // H1: Validate status transition if status is being changed
                    var tripIsCancelling = false;
                    if (operation.Data.TryGetValue("status", out var newStatusObj))
                    {
                        var newStatus = newStatusObj?.ToString();
                        var startsWithBookings = CommitmentsEnabled
                            && currentStatus == "open" && newStatus == "inTransit" && BookedQuintalsOf(existing) > 0;
                        if (!startsWithBookings
                            && (!ValidTransitions.TryGetValue(currentStatus, out var allowed)
                                || newStatus == null || !allowed.Contains(newStatus)))
                        {
                            // SIM-28: the loser of a status race is behind, not wrong.
                            var storedTripVersion = ItemVersion(existing);
                            if (StaleClientVersion(operation, storedTripVersion) is int tripRaceVersion)
                            {
                                return StaleVersionConflict(operation, storedTripVersion, tripRaceVersion,
                                    ScrubSyncSecrets(CropRepository.FromItem(existing)));
                            }

                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = $"TRIP_INVALID_TRANSITION: Cannot transition from '{currentStatus}' to '{newStatus}'."
                            };
                        }

                        tripIsCancelling = newStatus == "cancelled";
                    }

                    // ESC-17: no cancelling a trip whose crops are already on the road; the other
                    // orders on it are moved after the write (CascadeTripCancelAsync).
                    if (tripIsCancelling)
                    {
                        var cascadeStop = await CheckTripCancelAllowedAsync(operation, existing, context);
                        if (cascadeStop != null)
                        {
                            return cascadeStop;
                        }
                    }

                    // SIM-08: op-level clientVersion (contract) or data.version (shipped clients).
                    TryGetClientVersion(operation, out var clientVersion);

                    var createdAt = existing["createdAt"].S;
                    var now = DateTime.UtcNow.ToString("o");
                    var newVersion = clientVersion + 1;

                    // Build update expression
                    var updateParts = new List<string> { "#updated = :updated", "#v = :newVersion" };
                    var attrNames = new Dictionary<string, string>
                {
                    { "#updated", "updatedAt" },
                    { "#v", "version" }
                };
                    var attrValues = new Dictionary<string, AttributeValue>
                {
                    { ":updated", new AttributeValue { S = now } },
                    { ":newVersion", new AttributeValue { N = newVersion.ToString() } },
                    { ":expectedVersion", new AttributeValue { N = clientVersion.ToString() } }
                };

                    var i = 0;
                    foreach (var kvp in operation.Data)
                    {
                        if (kvp.Key is "tripId" or "version" or "createdAt" or "updatedAt")
                        {
                            continue;
                        }

                        if (!AllowedTripFields.Contains(kvp.Key))
                        {
                            continue;
                        }

                        var nameAlias = $"#f{i}";
                        var valueAlias = $":f{i}";
                        attrNames[nameAlias] = kvp.Key;
                        attrValues[valueAlias] = CropRepository.ToAttributeValue(kvp.Value);
                        updateParts.Add($"{nameAlias} = {valueAlias}");
                        i++;
                    }

                    // ESC-17: a capacity change keeps remainingQuintals = capacity - bookedQuintals. DynamoDB
                    // does the subtraction, so a reservation landing between this read and the write (the
                    // booking writes never bump the trip version) is still counted.
                    if (operation.Data.TryGetValue("capacity", out var newCapacityObj)
                        && double.TryParse(newCapacityObj?.ToString(), System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var newCapacity))
                    {
                        attrNames["#remaining"] = "remainingQuintals";
                        attrNames["#booked"] = "bookedQuintals";
                        attrValues[":newCapacity"] = new AttributeValue
                        {
                            N = newCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        };
                        attrValues[":zeroBooked"] = new AttributeValue { N = "0" };
                        updateParts.Add("#remaining = :newCapacity - if_not_exists(#booked, :zeroBooked)");
                    }

                    var updateRequest = new UpdateItemRequest
                    {
                        TableName = _tripsTable,
                        Key = new Dictionary<string, AttributeValue>
                    {
                        { "tripId", new AttributeValue { S = operation.EntityId } },
                        { "createdAt", new AttributeValue { S = createdAt } }
                    },
                        UpdateExpression = "SET " + string.Join(", ", updateParts),
                        ConditionExpression = "#v = :expectedVersion",
                        ExpressionAttributeNames = attrNames,
                        ExpressionAttributeValues = attrValues,
                        ReturnValues = ReturnValue.ALL_NEW
                    };

                    try
                    {
                        var response = await _dynamoDb.UpdateItemAsync(updateRequest);
                        var result = ScrubSyncSecrets(CropRepository.FromItem(response.Attributes));
                        if (tripIsCancelling)
                        {
                            await CascadeTripCancelAsync(existing, context);
                        }

                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "success",
                            ServerVersion = newVersion,
                            ServerData = result
                        };
                    }
                    catch (ConditionalCheckFailedException)
                    {
                        var serverData = ScrubSyncSecrets(CropRepository.FromItem(existing));
                        var serverVer = existing.TryGetValue("version", out var sv)
                            ? int.TryParse(sv.N, out var svn) ? svn : 0
                            : 0;

                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "conflict",
                            ServerVersion = serverVer,
                            ServerData = serverData,
                            Message = $"Version conflict: server has version {serverVer}, client expected {clientVersion}."
                        };
                    }
                }

            case "delete":
                {
                    // SIM-08: op-level clientVersion (contract) or data.version (shipped clients).
                    TryGetClientVersion(operation, out var clientVersion);

                    var existing = await GetTripItemAsync(operation.EntityId);
                    if (existing == null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "Trip not found."
                        };
                    }

                    // C2: Validate authenticated user owns this trip
                    if (!existing.TryGetValue("truckerId", out var existingTruckerId)
                        || existingTruckerId.S != authenticatedUserId)
                    {
                        return await RefuseWithSignalAsync(new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "TRIP_UNAUTHORIZED: You can only delete your own trips."
                        }, FraudDetectionService.RoleForbiddenSignal, operation, "trip", authenticatedUserId, context);
                    }

                    // H3: Block deletion of terminal-state or deleted trips. ESC-17: the delete of
                    // builds before the single cancel path is a cancel, so a trip already cancelled
                    // (or a stale version) answers the stored row as a conflict, like the update.
                    if (existing.TryGetValue("deletedAt", out var deletedAtAttr)
                        && !string.IsNullOrEmpty(deletedAtAttr.S))
                    {
                        return TerminalTripConflict(operation, existing, "cancelled")
                            ?? new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "TRIP_ALREADY_CANCELLED: Trip is already deleted."
                            };
                    }
                    if (existing.TryGetValue("status", out var existingStatusAttr)
                        && existingStatusAttr.S is "cancelled" or "delivered")
                    {
                        return TerminalTripConflict(operation, existing, "cancelled")
                            ?? new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "TRIP_TERMINAL_STATE: Cannot delete a terminal-state trip."
                            };
                    }

                    // ESC-17: a delete is a cancel; crops already on the road block it, and the
                    // other orders on the trip are moved after the write.
                    var deleteCascadeStop = await CheckTripCancelAllowedAsync(operation, existing, context);
                    if (deleteCascadeStop != null)
                    {
                        return deleteCascadeStop;
                    }

                    var createdAt = existing["createdAt"].S;
                    var now = DateTime.UtcNow.ToString("o");
                    var newVersion = clientVersion + 1;

                    // Soft delete: set deletedAt and status=cancelled
                    var deleteRequest = new UpdateItemRequest
                    {
                        TableName = _tripsTable,
                        Key = new Dictionary<string, AttributeValue>
                    {
                        { "tripId", new AttributeValue { S = operation.EntityId } },
                        { "createdAt", new AttributeValue { S = createdAt } }
                    },
                        UpdateExpression = "SET #deleted = :deleted, #status = :status, #updated = :updated, #v = :newVersion",
                        ConditionExpression = "#v = :expectedVersion",
                        ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        { "#deleted", "deletedAt" },
                        { "#status", "status" },
                        { "#updated", "updatedAt" },
                        { "#v", "version" }
                    },
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        { ":deleted", new AttributeValue { S = now } },
                        { ":status", new AttributeValue { S = "cancelled" } },
                        { ":updated", new AttributeValue { S = now } },
                        { ":newVersion", new AttributeValue { N = newVersion.ToString() } },
                        { ":expectedVersion", new AttributeValue { N = clientVersion.ToString() } }
                    },
                        ReturnValues = ReturnValue.ALL_NEW
                    };

                    try
                    {
                        var response = await _dynamoDb.UpdateItemAsync(deleteRequest);
                        await CascadeTripCancelAsync(existing, context);
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "success",
                            ServerVersion = newVersion
                        };
                    }
                    catch (ConditionalCheckFailedException)
                    {
                        var serverData = ScrubSyncSecrets(CropRepository.FromItem(existing));
                        var serverVer = existing.TryGetValue("version", out var sv)
                            ? int.TryParse(sv.N, out var svn) ? svn : 0
                            : 0;

                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "conflict",
                            ServerVersion = serverVer,
                            ServerData = serverData,
                            Message = "Version conflict on delete."
                        };
                    }
                }

            default:
                return new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "error",
                    Message = $"Invalid action: '{operation.Action}'."
                };
        }
    }

    /// <summary>
    /// True when an update's departureDate echoes the stored one (same text, or the same
    /// UTC instant; <paramref name="parsed"/> is UTC), so no date rule needs to run.
    /// </summary>
    private static bool IsUnchangedDepartureDate(string raw, DateTime parsed, string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return false;
        }

        if (string.Equals(raw, stored, StringComparison.Ordinal))
        {
            return true;
        }

        return TryParseDepartureDateUtc(stored, requireIsoShape: false, out var storedUtc)
               && storedUtc == parsed;
    }

    /// <summary>
    /// Validate trip data fields. For create: all required fields checked.
    /// For update: only validates fields that are present in the data. A departureDate
    /// that differs from <paramref name="storedDepartureDate"/> must be in the future and
    /// within 30 days, like on create (SIM-60); an echo of the stored value is accepted
    /// so a departed trip can still move matched -> inTransit -> delivered.
    /// </summary>
    private static List<string> ValidateTripFields(
        Dictionary<string, object> data, bool isCreate, string? storedDepartureDate = null)
    {
        var errors = new List<string>();

        // capacity: required on create, 0.1-1000
        if (isCreate || data.ContainsKey("capacity"))
        {
            if (!data.TryGetValue("capacity", out var capObj)
                || !double.TryParse(capObj?.ToString(), out var capacity))
            {
                if (isCreate)
                {
                    errors.Add("capacity is required and must be a number.");
                }
            }
            else if (capacity < 0.1 || capacity > 1000)
            {
                errors.Add("capacity must be between 0.1 and 1000.");
            }
        }

        // pricePerQuintal: required on create, 0.01-10000
        if (isCreate || data.ContainsKey("pricePerQuintal"))
        {
            if (!data.TryGetValue("pricePerQuintal", out var priceObj)
                || !double.TryParse(priceObj?.ToString(), out var price))
            {
                if (isCreate)
                {
                    errors.Add("pricePerQuintal is required and must be a number.");
                }
            }
            else if (price < 0.01 || price > 10000)
            {
                errors.Add("pricePerQuintal must be between 0.01 and 10000.");
            }
        }

        // fromWoreda: required on create, 1-100 chars
        if (isCreate || data.ContainsKey("fromWoreda"))
        {
            if (!data.TryGetValue("fromWoreda", out var fromObj)
                || string.IsNullOrWhiteSpace(fromObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("fromWoreda is required.");
                }
            }
            else if (fromObj.ToString()!.Length > 100)
            {
                errors.Add("fromWoreda must be 1-100 characters.");
            }
        }

        // toWoreda: required on create, 1-100 chars
        if (isCreate || data.ContainsKey("toWoreda"))
        {
            if (!data.TryGetValue("toWoreda", out var toObj)
                || string.IsNullOrWhiteSpace(toObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("toWoreda is required.");
                }
            }
            else if (toObj.ToString()!.Length > 100)
            {
                errors.Add("toWoreda must be 1-100 characters.");
            }
        }

        // fromWoreda and toWoreda must differ
        if (data.TryGetValue("fromWoreda", out var from) && data.TryGetValue("toWoreda", out var to)
            && from?.ToString() == to?.ToString())
        {
            errors.Add("fromWoreda and toWoreda must be different.");
        }

        // trip-listing-api 2.1.0: the destination point, optional, both or neither, inside Ethiopia's
        // box (the same bounds as a location_ping).
        var hasDestLat = data.TryGetValue("destinationLatitude", out var destLatObj);
        var hasDestLng = data.TryGetValue("destinationLongitude", out var destLngObj);
        if (hasDestLat != hasDestLng)
        {
            errors.Add("destinationLatitude and destinationLongitude must be sent together.");
        }
        else if (hasDestLat)
        {
            if (!double.TryParse(destLatObj?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var destLat)
                || destLat < 3 || destLat > 15)
            {
                errors.Add("destinationLatitude must be a number between 3 and 15.");
            }

            if (!double.TryParse(destLngObj?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var destLng)
                || destLng < 32.5 || destLng > 48.5)
            {
                errors.Add("destinationLongitude must be a number between 32.5 and 48.5.");
            }
        }

        // departureDate: required on create, valid ISO 8601, future and max 30 days ahead
        // on create and whenever an update changes it
        if (isCreate || data.ContainsKey("departureDate"))
        {
            if (!data.TryGetValue("departureDate", out var dateObj)
                || string.IsNullOrWhiteSpace(dateObj?.ToString()))
            {
                // SIM-60: a blank value sent on update used to be written as the sort key.
                errors.Add(isCreate
                    ? "departureDate is required."
                    : "departureDate must be a valid ISO 8601 date.");
            }
            else if (!TryParseDepartureDateUtc(dateObj.ToString(), requireIsoShape: true, out var parsedDate))
            {
                errors.Add("departureDate must be a valid ISO 8601 date.");
            }
            else if (isCreate)
            {
                // F-SEC-01: Enforce future date and 30-day max for create operations
                if (parsedDate <= DateTime.UtcNow)
                {
                    errors.Add("departureDate must be in the future for create.");
                }
                else if (parsedDate > DateTime.UtcNow.AddDays(30))
                {
                    errors.Add("departureDate must be within 30 days.");
                }
            }
            else if (!IsUnchangedDepartureDate(dateObj.ToString()!, parsedDate, storedDepartureDate))
            {
                // SIM-60: an update used to accept any parseable date, so an open trip
                // could be moved into the past or beyond the planning horizon.
                if (parsedDate <= DateTime.UtcNow)
                {
                    errors.Add("departureDate must be in the future when changed.");
                }
                else if (parsedDate > DateTime.UtcNow.AddDays(30))
                {
                    errors.Add("departureDate must be within 30 days.");
                }
            }
        }

        // status: must be "open" for create
        if (isCreate && data.TryGetValue("status", out var statusObj)
            && statusObj?.ToString() != "open")
        {
            errors.Add("status must be 'open' for create.");
        }

        // truckerPhone: required on create, must be valid Ethiopian phone
        if (isCreate || data.ContainsKey("truckerPhone"))
        {
            if (!data.TryGetValue("truckerPhone", out var phoneObj)
                || string.IsNullOrWhiteSpace(phoneObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("truckerPhone is required.");
                }
            }
            else
            {
                // F-SEC-02: Normalize then validate (accepts +251, 00251, 0 prefix)
                var normalized = Validation.RequestValidator.NormalizePhoneNumber(phoneObj.ToString()!);
                if (normalized == null)
                {
                    errors.Add("truckerPhone must be valid Ethiopian format (+251XXXXXXXXX, 00251XXXXXXXXX, or 0XXXXXXXXX).");
                }
                else
                {
                    data["truckerPhone"] = normalized;
                }
            }
        }

        // truckerName: required on create, 1-100 chars
        if (isCreate || data.ContainsKey("truckerName"))
        {
            if (!data.TryGetValue("truckerName", out var nameObj)
                || string.IsNullOrWhiteSpace(nameObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("truckerName is required.");
                }
            }
            else if (nameObj.ToString()!.Length > 100)
            {
                errors.Add("truckerName must be 1-100 characters.");
            }
        }

        // truckerId: required on create (auth check done separately)
        if (isCreate && !data.ContainsKey("truckerId"))
        {
            errors.Add("truckerId is required.");
        }

        return errors;
    }

    /// <summary>
    /// Validate delivery fields for create/update operations.
    /// Follows the same pattern as ValidateTripFields.
    /// </summary>
    private static List<string> ValidateDeliveryFields(
        Dictionary<string, object> data, bool isCreate, bool isSelfPickup = false)
    {
        var errors = new List<string>();

        // Self-pickup deliveries have no trucker and the farmer's QR is generated later,
        // so truckerId/truckerName/qrToken are NOT required at create for them.
        var requireTruckerAndQr = isCreate && !isSelfPickup;

        // tripId: required on create, must be valid UUID format
        if (isCreate || data.ContainsKey("tripId"))
        {
            if (!data.TryGetValue("tripId", out var tripIdObj)
                || string.IsNullOrWhiteSpace(tripIdObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("tripId is required.");
                }
            }
            else if (!Guid.TryParse(tripIdObj.ToString(), out _))
            {
                errors.Add("tripId must be a valid UUID.");
            }
        }

        // listingId: required on create, must be valid UUID format
        if (isCreate || data.ContainsKey("listingId"))
        {
            if (!data.TryGetValue("listingId", out var listingIdObj)
                || string.IsNullOrWhiteSpace(listingIdObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("listingId is required.");
                }
            }
            else if (!Guid.TryParse(listingIdObj.ToString(), out _))
            {
                errors.Add("listingId must be a valid UUID.");
            }
        }

        // farmerId: required on create, must be valid UUID format
        if (isCreate || data.ContainsKey("farmerId"))
        {
            if (!data.TryGetValue("farmerId", out var farmerIdObj)
                || string.IsNullOrWhiteSpace(farmerIdObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("farmerId is required.");
                }
            }
            else if (!Guid.TryParse(farmerIdObj.ToString(), out _))
            {
                errors.Add("farmerId must be a valid UUID.");
            }
        }

        // buyerId: required on create, must be valid UUID format
        if (isCreate || data.ContainsKey("buyerId"))
        {
            if (!data.TryGetValue("buyerId", out var buyerIdObj)
                || string.IsNullOrWhiteSpace(buyerIdObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("buyerId is required.");
                }
            }
            else if (!Guid.TryParse(buyerIdObj.ToString(), out _))
            {
                errors.Add("buyerId must be a valid UUID.");
            }
        }

        // qrToken: required on create, 1-500 chars
        if (isCreate || data.ContainsKey("qrToken"))
        {
            if (!data.TryGetValue("qrToken", out var qrTokenObj)
                || string.IsNullOrWhiteSpace(qrTokenObj?.ToString()))
            {
                if (requireTruckerAndQr)
                {
                    errors.Add("qrToken is required.");
                }
            }
            else if (qrTokenObj.ToString()!.Length > 500)
            {
                errors.Add("qrToken must be 1-500 characters.");
            }
        }

        // farmerName: required on create, 1-200 chars
        if (isCreate || data.ContainsKey("farmerName"))
        {
            if (!data.TryGetValue("farmerName", out var farmerNameObj)
                || string.IsNullOrWhiteSpace(farmerNameObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("farmerName is required.");
                }
            }
            else if (farmerNameObj.ToString()!.Length > 200)
            {
                errors.Add("farmerName must be 1-200 characters.");
            }
        }

        // truckerName: required on create, 1-200 chars
        if (isCreate || data.ContainsKey("truckerName"))
        {
            if (!data.TryGetValue("truckerName", out var truckerNameObj)
                || string.IsNullOrWhiteSpace(truckerNameObj?.ToString()))
            {
                if (requireTruckerAndQr)
                {
                    errors.Add("truckerName is required.");
                }
            }
            else if (truckerNameObj.ToString()!.Length > 200)
            {
                errors.Add("truckerName must be 1-200 characters.");
            }
        }

        // buyerName: required on create, 1-200 chars
        if (isCreate || data.ContainsKey("buyerName"))
        {
            if (!data.TryGetValue("buyerName", out var buyerNameObj)
                || string.IsNullOrWhiteSpace(buyerNameObj?.ToString()))
            {
                if (isCreate)
                {
                    errors.Add("buyerName is required.");
                }
            }
            else if (buyerNameObj.ToString()!.Length > 200)
            {
                errors.Add("buyerName must be 1-200 characters.");
            }
        }

        // F12-SEC-04: Phone format validation — normalize then validate
        foreach (var phoneField in new[] { "farmerPhone", "truckerPhone", "buyerPhone" })
        {
            if (data.TryGetValue(phoneField, out var phoneObj)
                && phoneObj != null && !string.IsNullOrWhiteSpace(phoneObj.ToString()))
            {
                var normalized = Validation.RequestValidator.NormalizePhoneNumber(phoneObj.ToString()!);
                if (normalized == null)
                {
                    errors.Add($"{phoneField} must be valid Ethiopian format (+251XXXXXXXXX, 00251XXXXXXXXX, or 0XXXXXXXXX).");
                }
                else
                {
                    data[phoneField] = normalized;
                }
            }
        }

        // F12-SEC-06: disputeReason — 1-500 chars (aligned with contract)
        if (data.TryGetValue("disputeReason", out var disputeObj) && disputeObj != null)
        {
            var disputeText = disputeObj.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(disputeText) || disputeText.Length > 500)
            {
                errors.Add("disputeReason must be 1-500 characters.");
            }
        }

        // truckerId: required on create for trucker deliveries only (auth check done
        // separately). Self-pickup deliveries legitimately have no trucker.
        if (requireTruckerAndQr && !data.ContainsKey("truckerId"))
        {
            errors.Add("truckerId is required.");
        }

        return errors;
    }

    /// <summary>
    /// SIM-46: stamp one delivery party's name and phone from the users table, as
    /// SIM-40 does for listings. The name falls back to the order row (its farmerName
    /// and truckerName are themselves server-stamped) because the creator cannot fix
    /// another party's profile; the phone has no fallback since orders carry none.
    /// Returns an error message when the profile cannot be read or no name is known.
    /// </summary>
    private async Task<string?> StampDeliveryPartyContactAsync(
        Dictionary<string, object> data,
        string partyLabel,
        string? partyId,
        string nameKey,
        string phoneKey,
        Dictionary<string, object> order,
        UserDto? preloaded,
        ILambdaContext context)
    {
        if (string.IsNullOrWhiteSpace(partyId))
        {
            return $"{partyLabel}Id is required.";
        }

        if (_userRepo == null)
        {
            context.Logger.LogError($"Cannot stamp {phoneKey}: user repository not configured.");
            return $"The {partyLabel} profile could not be read. Please try again later.";
        }

        var party = preloaded ?? await _userRepo.GetUserByIdAsync(partyId);
        if (party == null)
        {
            return $"The {partyLabel} profile could not be read. Please try again later.";
        }

        // FUP-130: an order written before the fix may hold the party's id where the name
        // belongs; that is no name.
        var orderName = PartyName.OrEmpty(order.TryGetValue(nameKey, out var on) ? on?.ToString() : null, partyId);
        var name = !string.IsNullOrWhiteSpace(party.DisplayName) ? party.DisplayName
            : !string.IsNullOrWhiteSpace(party.FullName) ? party.FullName
            : orderName.Length > 0 ? orderName
            : null;
        if (name == null)
        {
            return $"The {partyLabel} has no name on their profile. Ask them to add one before creating a delivery.";
        }

        data[nameKey] = name.Trim();
        data[phoneKey] = party.PhoneNumber;
        return null;
    }

    /// <summary>
    /// Process a delivery sync operation (create, update, delete).
    /// Uses version-based conflict detection.
    /// Operates directly on DynamoDB (deliveries don't use the full CRUD repo pattern).
    /// </summary>
    private async Task<SyncOperationResult> ProcessDeliveryAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        ILambdaContext context)
    {
        switch (operation.Action)
        {
            case "create":
                {
                    if (operation.Data == null || operation.Data.Count == 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "Create operation requires data payload."
                        };
                    }

                    var createEchoError = StripDeliveryEchoFields(operation);
                    if (createEchoError != null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"DELIVERY_VALIDATION: {createEchoError}"
                        };
                    }

                    // Self-pickup deliveries are created by the BUYER (there is no trucker);
                    // normal deliveries are created by the trucker. Gate authorization on the
                    // party that owns each kind.
                    var createDeliveryType = operation.Data.TryGetValue("deliveryType", out var cdtObj)
                        ? cdtObj?.ToString() : null;
                    var isSelfPickupCreate = string.Equals(
                        createDeliveryType, SelfPickupDeliveryType, StringComparison.Ordinal);

                    if (isSelfPickupCreate)
                    {
                        var spBuyerId = operation.Data.TryGetValue("buyerId", out var spB)
                            ? spB?.ToString() : null;
                        if (spBuyerId != authenticatedUserId)
                        {
                            return await RefuseWithSignalAsync(new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_UNAUTHORIZED: buyerId must match authenticated user for self-pickup."
                            }, FraudDetectionService.RoleForbiddenSignal, operation, "delivery", authenticatedUserId, context);
                        }

                        var spTruckerId = operation.Data.TryGetValue("truckerId", out var spT)
                            ? spT?.ToString() : null;
                        if (!string.IsNullOrWhiteSpace(spTruckerId))
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_VALIDATION: A self-pickup delivery must not have a trucker."
                            };
                        }

                        // Synthesize the required non-empty tripId sort key from the deliveryId
                        // (self-pickup has no trip). GetByIdAsync queries the PK only, so this
                        // synthetic value is inert for retrieval and the TripIdIndex.
                        operation.Data["tripId"] = operation.EntityId;
                    }
                    else if (!operation.Data.TryGetValue("truckerId", out var truckerIdObj)
                        || truckerIdObj?.ToString() != authenticatedUserId)
                    {
                        return await RefuseWithSignalAsync(new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "DELIVERY_UNAUTHORIZED: truckerId must match authenticated user."
                        }, FraudDetectionService.RoleForbiddenSignal, operation, "delivery", authenticatedUserId, context);
                    }

                    // The listing and (trucker flow) the trip read below are kept: the row
                    // takes its pickup place and planned pickup from them.
                    Dictionary<string, object>? createListing = null;
                    Dictionary<string, AttributeValue>? createTrip = null;

                    // SEC-VULN-002: Validate farmerId matches listing owner
                    if (operation.Data.TryGetValue("listingId", out var lidVal))
                    {
                        var listingId = lidVal?.ToString();
                        if (!string.IsNullOrEmpty(listingId))
                        {
                            var listing = await _cropRepo.GetByIdAsync(listingId);
                            if (listing == null)
                            {
                                return new SyncOperationResult
                                {
                                    OperationId = operation.OperationId,
                                    Status = "error",
                                    Message = "DELIVERY_VALIDATION: Referenced listing not found."
                                };
                            }

                            // Check if listing is deleted (FUP-132: by the stamp or by the status)
                            if (CropRepository.IsDeleted(listing))
                            {
                                return new SyncOperationResult
                                {
                                    OperationId = operation.OperationId,
                                    Status = "error",
                                    Message = "DELIVERY_VALIDATION: Referenced listing has been deleted."
                                };
                            }

                            // Check farmerId matches listing owner
                            var requestedFarmerId = operation.Data.TryGetValue("farmerId", out var fid)
                                ? fid?.ToString() : null;
                            var listingFarmerId = listing.TryGetValue("farmerId", out var lfid)
                                ? lfid?.ToString() : null;
                            if (requestedFarmerId != listingFarmerId)
                            {
                                return new SyncOperationResult
                                {
                                    OperationId = operation.OperationId,
                                    Status = "error",
                                    Message = "DELIVERY_PARTICIPANT_MISMATCH: farmerId does not match listing owner."
                                };
                            }

                            createListing = listing;
                        }
                    }

                    // SEC-VULN-002: Validate buyerId references a real buyer. The row is
                    // kept for the contact stamping below (SIM-46).
                    UserDto? createBuyer = null;
                    if (operation.Data.TryGetValue("buyerId", out var bidVal))
                    {
                        var buyerId = bidVal?.ToString();
                        if (!string.IsNullOrEmpty(buyerId) && _userRepo != null)
                        {
                            var buyer = await _userRepo.GetUserByIdAsync(buyerId);
                            if (buyer == null)
                            {
                                return new SyncOperationResult
                                {
                                    OperationId = operation.OperationId,
                                    Status = "error",
                                    Message = "DELIVERY_VALIDATION: buyerId does not reference a valid user."
                                };
                            }

                            if (!string.Equals(buyer.Role, "buyer", StringComparison.OrdinalIgnoreCase))
                            {
                                return new SyncOperationResult
                                {
                                    OperationId = operation.OperationId,
                                    Status = "error",
                                    Message = "DELIVERY_PARTICIPANT_MISMATCH: buyerId must reference a buyer."
                                };
                            }

                            createBuyer = buyer;
                        }
                    }

                    // The order backing this delivery, loaded once and reused for the
                    // escrow-linkage step after the write.
                    Dictionary<string, object>? createOrder = null;

                    // Self-pickup integrity: no self-trade, and the linked order must carry no
                    // transport cost / trucker — otherwise escrow would try to settle a trucker
                    // leg with no trucker, or block on a split that can't reconcile 2-way.
                    if (isSelfPickupCreate)
                    {
                        var spFarmerId = operation.Data.TryGetValue("farmerId", out var spF)
                            ? spF?.ToString() : null;
                        var spBuyerId2 = operation.Data.TryGetValue("buyerId", out var spB2)
                            ? spB2?.ToString() : null;
                        if (!string.IsNullOrEmpty(spFarmerId) && spFarmerId == spBuyerId2)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_VALIDATION: Buyer and farmer must be different users."
                            };
                        }

                        var spOrderId = operation.Data.TryGetValue("orderId", out var spO)
                            ? spO?.ToString() : null;
                        if (string.IsNullOrWhiteSpace(spOrderId))
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_VALIDATION: A self-pickup delivery requires an orderId."
                            };
                        }

                        var spOrder = await _orderRepo.GetByIdAsync(spOrderId);
                        if (spOrder == null)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_VALIDATION: Referenced order not found."
                            };
                        }

                        if (spOrder.TryGetValue("buyerId", out var soB)
                            && soB?.ToString() != spBuyerId2)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_PARTICIPANT_MISMATCH: The order does not belong to this buyer."
                            };
                        }

                        // Bind the delivery's listing/farmer to the order's, so the buyer
                        // can't point the GPS geofence at an attacker-chosen listing while
                        // paying against a different order (anti-fraud: "physically at the farm").
                        var spListingId = operation.Data.TryGetValue("listingId", out var spL)
                            ? spL?.ToString() : null;
                        if (spOrder.TryGetValue("listingId", out var soL)
                            && soL?.ToString() != spListingId)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_PARTICIPANT_MISMATCH: listingId does not match the order."
                            };
                        }
                        if (spOrder.TryGetValue("farmerId", out var soF)
                            && soF?.ToString() != spFarmerId)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_PARTICIPANT_MISMATCH: farmerId does not match the order."
                            };
                        }

                        var soTransport = spOrder.TryGetValue("transportCost", out var soT)
                            && decimal.TryParse(soT?.ToString(), NumberStyles.Any,
                                CultureInfo.InvariantCulture, out var soTv)
                            ? soTv : 0m;
                        var soTrucker = spOrder.TryGetValue("truckerId", out var soTr)
                            ? soTr?.ToString() : null;
                        if (soTransport > 0m || !string.IsNullOrWhiteSpace(soTrucker))
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_VALIDATION: Self-pickup is not allowed for an order that already has transport or a trucker."
                            };
                        }

                        createOrder = spOrder;
                    }

                    // Every delivery must reference its order so the order-funded escrow
                    // can be linked to it (previously only self-pickup required orderId —
                    // an orderId-less trucker delivery would silently skip escrow linkage
                    // and the QR gate would fail open as "cash").
                    var createOrderId = operation.Data.TryGetValue("orderId", out var coObj)
                        ? coObj?.ToString() : null;
                    if (string.IsNullOrWhiteSpace(createOrderId))
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "DELIVERY_ORDER_REQUIRED: A delivery must reference its orderId."
                        };
                    }

                    createOrder ??= await _orderRepo.GetByIdAsync(createOrderId);
                    if (createOrder == null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "DELIVERY_VALIDATION: Referenced order not found."
                        };
                    }

                    // SIM-09: a delivery moves goods the farmer has agreed to sell, so the
                    // order must be accepted (or already in a pre-delivery logistics state).
                    // Earlier statuses carry no farmer consent (created, funded), and a dead
                    // order would re-point escrow money at a finished trade.
                    var createOrderStatus = createOrder.TryGetValue("status", out var cosObj)
                        ? cosObj?.ToString() : null;
                    if (createOrderStatus is not ("accepted" or "pickup_confirmed" or "in_transit"))
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"DELIVERY_VALIDATION: Order is {createOrderStatus ?? "unknown"}; "
                                + "no delivery can be created until the farmer has accepted it "
                                + "(accepted, pickup_confirmed or in_transit)."
                        };
                    }

                    // Participant binding for the trucker flow (self-pickup already binds
                    // buyer/farmer/listing to the order above).
                    if (!isSelfPickupCreate)
                    {
                        var dBuyerId = operation.Data.TryGetValue("buyerId", out var dB)
                            ? dB?.ToString() : null;
                        var dFarmerId = operation.Data.TryGetValue("farmerId", out var dF)
                            ? dF?.ToString() : null;
                        if (createOrder.TryGetValue("buyerId", out var coB)
                            && coB?.ToString() != dBuyerId)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_PARTICIPANT_MISMATCH: buyerId does not match the order."
                            };
                        }

                        if (createOrder.TryGetValue("farmerId", out var coF)
                            && coF?.ToString() != dFarmerId)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_PARTICIPANT_MISMATCH: farmerId does not match the order."
                            };
                        }

                        // When the order was matched to a trucker, only THAT trucker
                        // may create its delivery — otherwise any authenticated
                        // trucker who learns an orderId could attach a bogus delivery
                        // and hijack the escrow linkage.
                        var coTrucker = createOrder.TryGetValue("truckerId", out var coT)
                            ? coT?.ToString() : null;
                        if (string.IsNullOrWhiteSpace(coTrucker))
                        {
                            // SIM-09: an order without a trucker can only be fulfilled by the
                            // buyer as a self-pickup; any trucker could otherwise attach itself.
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_PARTICIPANT_MISMATCH: The order has no trucker; only the buyer can create a self-pickup delivery for it."
                            };
                        }

                        if (coTrucker != authenticatedUserId)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_PARTICIPANT_MISMATCH: Only the order's matched trucker can create this delivery."
                            };
                        }

                        // ESC-08: the delivery moves this order's goods on this order's trip, so
                        // both ids must be the order's, and the trip must be the caller's. The
                        // trip status is not checked: that rule belongs to ESC-17 (bookings).
                        var dTripId = operation.Data.TryGetValue("tripId", out var dT)
                            ? dT?.ToString() : null;
                        var coTripId = createOrder.TryGetValue("tripId", out var coTr)
                            ? coTr?.ToString() : null;
                        if (string.IsNullOrWhiteSpace(coTripId) || coTripId != dTripId)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_PARTICIPANT_MISMATCH: tripId does not match the order."
                            };
                        }

                        var dListingId = operation.Data.TryGetValue("listingId", out var dL)
                            ? dL?.ToString() : null;
                        var coListingId = createOrder.TryGetValue("listingId", out var coL)
                            ? coL?.ToString() : null;
                        if (string.IsNullOrWhiteSpace(coListingId) || coListingId != dListingId)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_PARTICIPANT_MISMATCH: listingId does not match the order."
                            };
                        }

                        createTrip = await GetTripItemAsync(coTripId);
                        if (createTrip == null)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_VALIDATION: Referenced trip not found."
                            };
                        }

                        var tripTruckerId = createTrip.TryGetValue("truckerId", out var ctT) ? ctT.S : null;
                        if (tripTruckerId != authenticatedUserId)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_PARTICIPANT_MISMATCH: The trip belongs to another trucker."
                            };
                        }
                    }

                    // SIM-46: each party sees the others' name and phone on the delivery, so
                    // they come from the users table (name falling back to the order row),
                    // never from the client. The ids were bound to the order above.
                    var createBuyerId = operation.Data.TryGetValue("buyerId", out var cbObj)
                        ? cbObj?.ToString() : null;
                    var createFarmerId = operation.Data.TryGetValue("farmerId", out var cfObj)
                        ? cfObj?.ToString() : null;
                    var partyContactError = await StampDeliveryPartyContactAsync(
                        operation.Data, "buyer", createBuyerId, "buyerName", "buyerPhone",
                        createOrder, createBuyer, context);
                    partyContactError ??= await StampDeliveryPartyContactAsync(
                        operation.Data, "farmer", createFarmerId, "farmerName", "farmerPhone",
                        createOrder, null, context);
                    if (partyContactError == null && !isSelfPickupCreate)
                    {
                        partyContactError = await StampDeliveryPartyContactAsync(
                            operation.Data, "trucker", authenticatedUserId, "truckerName", "truckerPhone",
                            createOrder, null, context);
                    }

                    if (partyContactError != null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"DELIVERY_VALIDATION: {partyContactError}"
                        };
                    }

                    if (isSelfPickupCreate)
                    {
                        // No trucker: nothing client-authored may pose as one.
                        operation.Data.Remove("truckerName");
                        operation.Data.Remove("truckerPhone");
                    }

                    // Field whitelist — reject unknown fields
                    var unknownFields = operation.Data.Keys
                        .Where(k => !AllowedDeliveryFields.Contains(k) && k != "version")
                        .ToList();
                    if (unknownFields.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"DELIVERY_VALIDATION: Unknown fields: {string.Join(", ", unknownFields)}"
                        };
                    }

                    // Validate required fields
                    var validationErrors = ValidateDeliveryFields(
                        operation.Data, isCreate: true, isSelfPickup: isSelfPickupCreate);
                    if (validationErrors.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"DELIVERY_VALIDATION: {string.Join(" ", validationErrors)}"
                        };
                    }

                    // FIX-011 (path traversal) and FRD-12: a photo ref on a delivery op is checked,
                    // then dropped; photos are attached only through delivery_photo.
                    var photoRefError = DropDeliveryOpPhotoRefs(operation);
                    if (photoRefError != null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = photoRefError
                        };
                    }

                    // FUP-246: a create is always pending, so it carries no handoff field.
                    DropOtherHandoffFields(operation, "pending", context);

                    var now = DateTime.UtcNow.ToString("o");

                    var item = new Dictionary<string, AttributeValue>
                {
                    { "deliveryId", new AttributeValue { S = operation.EntityId } },
                    { "createdAt", new AttributeValue { S = now } },
                    { "updatedAt", new AttributeValue { S = now } },
                    { "version", new AttributeValue { N = "1" } }
                };

                    // Copy only whitelisted data fields. qrNonce is never taken at create:
                    // only the farmer may author it, later (SIM-41).
                    foreach (var kvp in operation.Data)
                    {
                        if (kvp.Key is "deliveryId" or "version" or "createdAt" or "updatedAt" or "qrNonce")
                        {
                            continue;
                        }

                        if (!AllowedDeliveryFields.Contains(kvp.Key))
                        {
                            continue;
                        }

                        // Handle GPS fields as nested DynamoDB Map attributes
                        if (kvp.Key is "gpsPickup" or "gpsDropoff")
                        {
                            if (kvp.Value is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                // Guard property presence/type: GetProperty throws on a present-but-
                                // malformed GPS object (missing keys), which the outer catch would mask
                                // as a generic 500 and burn a sync retry. Fail with a clean validation error.
                                if (!je.TryGetProperty("latitude", out var latEl) || latEl.ValueKind != System.Text.Json.JsonValueKind.Number
                                    || !je.TryGetProperty("longitude", out var lngEl) || lngEl.ValueKind != System.Text.Json.JsonValueKind.Number)
                                {
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = $"DELIVERY_VALIDATION: {kvp.Key} requires numeric latitude and longitude."
                                    };
                                }
                                var lat = latEl.GetDouble();
                                var lng = lngEl.GetDouble();

                                if (double.IsNaN(lat) || double.IsInfinity(lat) || lat < -90 || lat > 90
                                    || double.IsNaN(lng) || double.IsInfinity(lng) || lng < -180 || lng > 180)
                                {
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = $"DELIVERY_VALIDATION: Invalid GPS coordinates for {kvp.Key}. Latitude must be -90 to 90, longitude -180 to 180."
                                    };
                                }

                                var mapAttr = new AttributeValue
                                {
                                    M = new Dictionary<string, AttributeValue>
                                {
                                    { "latitude", new AttributeValue { N = lat.ToString(CultureInfo.InvariantCulture) } },
                                    { "longitude", new AttributeValue { N = lng.ToString(CultureInfo.InvariantCulture) } }
                                }
                                };
                                item[kvp.Key] = mapAttr;
                            }
                            continue;
                        }

                        item[kvp.Key] = CropRepository.ToAttributeValue(kvp.Value);
                    }

                    // Force status to "pending" for create
                    item["status"] = new AttributeValue { S = "pending" };

                    // The deliveries list titles each card by crop and quantity and names
                    // the pickup place and day without one order read per row (3G): the
                    // row carries them from the order, listing and trip read above. They
                    // are not client fields (the whitelist refuses them).
                    StampDeliveryCargo(item, createOrder, createListing, createTrip);

                    // Participant index key: the buyer is the party that must receive
                    // status updates (pull-based), so ParticipantIndex is keyed on buyerId.
                    if (item.TryGetValue("buyerId", out var buyerIdForIndex)
                        && !string.IsNullOrEmpty(buyerIdForIndex.S))
                    {
                        item["participantId"] = new AttributeValue { S = buyerIdForIndex.S };
                    }

                    // QR nonce is NOT set at create. The delivery generator (farmer)
                    // persists a nonce via an update after generating the QR the trucker
                    // scans; that farmer-authored nonce is authoritative and is what the
                    // scanner must echo back. Until it is persisted, qrNonce is empty and
                    // the pickup/delivery nonce check is skipped (offline-first).

                    // The key is (deliveryId, tripId), so a replayed create with another
                    // tripId would pass the put's condition and add a second row (SIM-39).
                    // Checked before the slot claim so a reused deliveryId never claims
                    // (and strands) another order's slot.
                    var deliveryExists = await DynamoDbHelper.PartitionExistsAsync(
                        _dynamoDb, _deliveriesTable, "deliveryId", operation.EntityId);

                    // SIM-09, ESC-08: at most one active delivery per order. The slot claim on
                    // the order row (deliveries have no order index) and the delivery row are
                    // one transaction, so an interrupted create never leaves the order naming
                    // a delivery that does not exist. A retry of the same deliveryId re-claims
                    // its own slot, and a cancelled delivery releases it.
                    var slotWrite = deliveryExists
                        ? DeliverySlotWriteResult.DeliveryExists
                        : await CreateDeliveryWithSlotAsync(createOrderId!, item, context);
                    switch (slotWrite)
                    {
                        case DeliverySlotWriteResult.SlotTaken:
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_VALIDATION: The order already has an active delivery."
                            };
                        case DeliverySlotWriteResult.OrderNotFound:
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_VALIDATION: Referenced order not found."
                            };
                        case DeliverySlotWriteResult.OrderNotActive:
                            // ESC-06: the order was cancelled (or moved on) after it was read above.
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_VALIDATION: The order no longer takes a delivery "
                                    + "(it must be accepted, pickup_confirmed or in_transit)."
                            };
                    }

                    try
                    {
                        if (slotWrite == DeliverySlotWriteResult.DeliveryExists)
                        {
                            throw new ConditionalCheckFailedException("Delivery already exists.");
                        }

                        var deliveryTxId = item.TryGetValue("orderId", out var dCreateOid)
                            && !string.IsNullOrEmpty(dCreateOid.S)
                            ? dCreateOid.S : operation.EntityId;
                        await EmitTxAuditAsync(context, deliveryTxId, "delivery", operation.EntityId,
                            "delivery_created", null, "pending");

                        // Attach the order's escrow to this delivery so confirm-stamping,
                        // auto-release, and the self-pickup QR gate all see the money.
                        await TryLinkOrderEscrowAsync(
                            createOrderId!, operation.EntityId, createOrder, context);

                        var result = ScrubDeliveryForClient(CropRepository.FromItem(item));
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "success",
                            ServerVersion = 1,
                            ServerData = result
                        };
                    }
                    catch (ConditionalCheckFailedException)
                    {
                        // Echo the existing delivery only to one of its parties. Consistent: a
                        // concurrent create of this id may have committed a moment ago (ESC-08).
                        var existing = await GetDeliveryItemAsync(operation.EntityId, consistentRead: true);
                        var serverVersion = 1;
                        if (existing != null && existing.TryGetValue("version", out var v))
                        {
                            _ = int.TryParse(v.N, out serverVersion);
                        }
                        var isParty = existing != null
                            && IsParty(existing, authenticatedUserId, "buyerId", "farmerId", "truckerId");

                        // FRD-08: a delivery id of a delivery the caller is no party to is a probe.
                        if (existing != null && !isParty)
                        {
                            await TryRecordFraudSignalAsync(
                                authenticatedUserId ?? "", FraudDetectionService.IdorCreateConflictSignal, operation.EntityId,
                                "delivery", "Create named a delivery id that belongs to other users.", context);
                        }

                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "conflict",
                            ServerVersion = serverVersion,
                            ServerData = isParty ? ScrubDeliveryForClient(CropRepository.FromItem(existing!)) : null,
                            Message = "Delivery already exists on server."
                        };
                    }
                }

            case "update":
                {
                    if (operation.Data == null || operation.Data.Count == 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "Update operation requires data payload."
                        };
                    }

                    var updateEchoError = StripDeliveryEchoFields(operation);
                    if (updateEchoError != null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"DELIVERY_VALIDATION: {updateEchoError}"
                        };
                    }

                    // Get existing item to find sort key
                    var existing = await GetDeliveryItemAsync(operation.EntityId);
                    if (existing == null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "Delivery not found."
                        };
                    }

                    // ESC-08: a cancel has its own rules (any party, before pickup only).
                    if (operation.Data.TryGetValue("status", out var cancelStatusObj)
                        && cancelStatusObj?.ToString() == "cancelled")
                    {
                        return await CancelDeliveryAsync(operation, existing, authenticatedUserId, "update", context);
                    }

                    // Block updates to cancelled/deleted deliveries. FUP-128: a party who is behind
                    // the stored version (the delivery was cancelled under it) gets the stored row
                    // as a conflict, like the loser of any status race (SIM-28).
                    var currentStatus = existing.TryGetValue("status", out var existingStatusAttr)
                        ? existingStatusAttr.S : "pending";

                    // ESC-34: a pickup scan after the order's pickup was judged is late evidence,
                    // whether the judgement cancelled the delivery or left it pending.
                    if (await RefuseLatePickupAsync(operation, existing, currentStatus, authenticatedUserId, context) is { } latePickup)
                    {
                        return latePickup;
                    }

                    if (existing.TryGetValue("deletedAt", out var deletedAtAttr)
                        && !string.IsNullOrEmpty(deletedAtAttr.S))
                    {
                        return CancelledDeliveryConflict(operation, existing, authenticatedUserId)
                            ?? new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_ALREADY_CANCELLED: Cannot update a deleted delivery."
                            };
                    }
                    if (currentStatus == "cancelled")
                    {
                        await AlarmPickupOnTransportCancelledDeliveryAsync(operation, existing, authenticatedUserId, context);
                        return CancelledDeliveryConflict(operation, existing, authenticatedUserId)
                            ?? new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_ALREADY_CANCELLED: Cannot update a cancelled delivery."
                            };
                    }

                    // SIM-41: the pickup nonce proves the farmer handed over the goods, so
                    // only the farmer may author it. A trucker-sent nonce let the trucker
                    // confirm pickup with a QR code he made himself.
                    var deliveryFarmerId = existing.TryGetValue("farmerId", out var nonceFarmerAttr)
                        ? nonceFarmerAttr.S : null;
                    if (authenticatedUserId != deliveryFarmerId)
                    {
                        operation.Data.Remove("qrNonce");
                    }

                    // ESC-35: the drop-off nonce proves the trucker handed over the goods, so only
                    // the delivery's trucker may author it, on an update without a status. Anyone
                    // else's, or one riding on a status change, is dropped as qrNonce is above.
                    string? dropoffNonce = null;
                    if (operation.Data.Remove("dropoffQrNonce", out var dropoffNonceObj)
                        && !operation.Data.ContainsKey("status")
                        && authenticatedUserId == (existing.TryGetValue("truckerId", out var dropoffTruckerAttr)
                            ? dropoffTruckerAttr.S : null))
                    {
                        dropoffNonce = dropoffNonceObj?.ToString() ?? string.Empty;
                    }

                    // Role-based authorization for status transitions
                    if (operation.Data.TryGetValue("status", out var newStatusObj))
                    {
                        var newStatus = newStatusObj?.ToString();

                        var existingDeliveryType = existing.TryGetValue("deliveryType", out var edt)
                            ? edt.S : null;
                        var isSelfPickupDelivery = string.Equals(
                            existingDeliveryType, SelfPickupDeliveryType, StringComparison.Ordinal);

                        // Self-pickup: the buyer collects at the farm, so a single scan takes
                        // pending → delivered directly (there is no trucker pickup/transit leg).
                        // Permitted ONLY for self-pickup; a normal delivery must still go through
                        // the trucker's pickedUp step. The "delivered" auth below keeps it buyer-only.
                        var selfPickupDirectDeliver = isSelfPickupDelivery
                            && currentStatus == "pending" && newStatus == "delivered";

                        // ESC-07: a dispute sent again for a delivery that is already disputed (the
                        // retry of a request that stopped after the delivery write, or the other
                        // party's dispute) finishes the freeze and the order mirror and answers with
                        // the row; the delivery is not written again.
                        if (newStatus == "disputed" && currentStatus == "disputed")
                        {
                            return await RepeatDeliveryDisputeAsync(operation, existing, authenticatedUserId, context);
                        }

                        // LT-07: a check-in may have moved the delivery to inTransit before the
                        // trucker's own tap reached the server. The trucker is answered with the row
                        // (server wins, nothing written), not a failed change that retries.
                        if (newStatus == "inTransit" && currentStatus == "inTransit"
                            && authenticatedUserId == (existing.TryGetValue("truckerId", out var transitTruckerAttr) ? transitTruckerAttr.S : null))
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "conflict",
                                ServerVersion = existing.TryGetValue("version", out var transitVersionAttr)
                                    && int.TryParse(transitVersionAttr.N, out var transitVersion) ? transitVersion : 0,
                                ServerData = ScrubDeliveryForClient(CropRepository.FromItem(existing)),
                                Message = "The delivery is already in transit."
                            };
                        }

                        // Validate status transition
                        if (!selfPickupDirectDeliver
                            && (!DeliveryTransitions.TryGetValue(currentStatus, out var allowed)
                                || newStatus == null || !allowed.Contains(newStatus)))
                        {
                            // SIM-28: the loser of a status race is behind, not wrong: a party who may
                            // ask for this status gets the row. Anyone else keeps the error.
                            var storedDeliveryVersion = ItemVersion(existing);
                            if (StaleClientVersion(operation, storedDeliveryVersion) is int deliveryRaceVersion
                                && MayRequestDeliveryStatus(existing, authenticatedUserId, newStatus))
                            {
                                return StaleVersionConflict(operation, storedDeliveryVersion, deliveryRaceVersion,
                                    ScrubDeliveryForClient(CropRepository.FromItem(existing)));
                            }

                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = $"DELIVERY_INVALID_TRANSITION: Cannot transition from '{currentStatus}' to '{newStatus}'."
                            };
                        }

                        // Role-based access for specific transitions
                        var existingTruckerId = existing.TryGetValue("truckerId", out var tid) ? tid.S : null;
                        var existingBuyerId = existing.TryGetValue("buyerId", out var bid) ? bid.S : null;
                        var existingFarmerId = existing.TryGetValue("farmerId", out var fid) ? fid.S : null;

                        switch (newStatus)
                        {
                            case "pickedUp":
                            case "inTransit":
                                if (authenticatedUserId != existingTruckerId)
                                {
                                    return await RefuseWithSignalAsync(new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = $"DELIVERY_UNAUTHORIZED: Only the trucker can mark as {newStatus}."
                                    }, FraudDetectionService.RoleForbiddenSignal, operation, "delivery", authenticatedUserId, context);
                                }
                                break;
                            case "delivered":
                                if (authenticatedUserId != existingBuyerId)
                                {
                                    return await RefuseWithSignalAsync(new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = "DELIVERY_UNAUTHORIZED: Only the buyer can confirm delivery."
                                    }, FraudDetectionService.RoleForbiddenSignal, operation, "delivery", authenticatedUserId, context);
                                }
                                break;
                            case "disputed":
                                // F12-SEC-02: Enforce 48-hour dispute window (server-side)
                                if (existing.TryGetValue("confirmedAt", out var confirmedAtAttr2)
                                    && !string.IsNullOrEmpty(confirmedAtAttr2.S)
                                    && DateTime.TryParse(confirmedAtAttr2.S, null,
                                        DateTimeStyles.RoundtripKind, out var confirmedDate))
                                {
                                    if (DateTime.UtcNow - confirmedDate > TimeSpan.FromHours(48))
                                    {
                                        return new SyncOperationResult
                                        {
                                            OperationId = operation.OperationId,
                                            Status = "error",
                                            Message = "DELIVERY_DISPUTE_EXPIRED: Dispute window (48 hours) has expired."
                                        };
                                    }
                                }
                                else if (currentStatus == "delivered")
                                {
                                    // confirmedAt is missing for a delivered delivery — data integrity issue
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = "DELIVERY_VALIDATION: confirmedAt is missing for delivered delivery."
                                    };
                                }
                                if (authenticatedUserId != existingFarmerId
                                    && authenticatedUserId != existingTruckerId
                                    && authenticatedUserId != existingBuyerId)
                                {
                                    return await RefuseWithSignalAsync(new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = "DELIVERY_UNAUTHORIZED: Only participants can dispute a delivery."
                                    }, FraudDetectionService.RoleForbiddenSignal, operation, "delivery", authenticatedUserId, context);
                                }

                                // A dispute freezes the order's money: above the step-up
                                // threshold it needs a fresh password (AUTH_040), like the
                                // escrow dispute endpoint. Decided before the freeze, so a
                                // read failure (FUP-26) freezes and writes nothing.
                                var disputeStepUp = await CheckDeliveryDisputeStepUpAsync(
                                    existing, operation.EntityId, context);
                                if (disputeStepUp != null)
                                {
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = disputeStepUp
                                    };
                                }
                                break;
                            default:
                                return new SyncOperationResult
                                {
                                    OperationId = operation.OperationId,
                                    Status = "error",
                                    Message = $"DELIVERY_INVALID_TRANSITION: Unknown status '{newStatus}'."
                                };
                        }
                    }
                    else
                    {
                        // Non-status updates: the trucker owns generic updates.
                        // Additionally the FARMER — who generates the pickup QR —
                        // may persist the qrNonce so the scanner's nonce can be
                        // verified against it. (The farmer cannot drive any status
                        // transition, so this grants no extra transition power.)
                        var existingTruckerId = existing.TryGetValue("truckerId", out var tid2) ? tid2.S : null;
                        var existingFarmerIdForNonce = existing.TryGetValue("farmerId", out var fid2) ? fid2.S : null;
                        var isFarmerNoncePersist = operation.Data.ContainsKey("qrNonce")
                            && authenticatedUserId == existingFarmerIdForNonce;
                        if (authenticatedUserId != existingTruckerId && !isFarmerNoncePersist)
                        {
                            return await RefuseWithSignalAsync(new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_UNAUTHORIZED: You can only update your own deliveries."
                            }, FraudDetectionService.RoleForbiddenSignal, operation, "delivery", authenticatedUserId, context);
                        }

                        // T-1 review: once the delivery left pending, its nonce was used for the
                        // pickup and is the drop-off's secret too (the trucker's phone shows it
                        // to the buyer). A farmer's later nonce (a QR reopened on a phone whose
                        // copy still said pending) must not replace it, nor clear the typed-code
                        // misses: nothing is written, success at the current version.
                        var farmerNonceOnly = isFarmerNoncePersist && authenticatedUserId != existingTruckerId;
                        if (farmerNonceOnly && currentStatus != "pending")
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "success",
                                ServerVersion = existing.TryGetValue("version", out var lateNonceVer)
                                    && int.TryParse(lateNonceVer.N, out var lateNonceVerN) ? lateNonceVerN : 0,
                                ServerData = ScrubDeliveryForClient(CropRepository.FromItem(existing))
                            };
                        }

                        // Route the farmer-authored pickup nonce to the SEPARATE secret
                        // store (never onto the delivery item, so it can't leak via any
                        // sync-down/GSI/conflict-echo path). When no table is provisioned
                        // (_qrSecretRepo == null) we leave the legacy on-record qrNonce so
                        // in-flight deliveries stay verifiable.
                        if (isFarmerNoncePersist && _qrSecretRepo != null)
                        {
                            var nonceValue = operation.Data.TryGetValue("qrNonce", out var qnPersist)
                                ? qnPersist?.ToString()
                                : null;
                            if (!string.IsNullOrEmpty(nonceValue))
                            {
                                await _qrSecretRepo.PutNonceAsync(operation.EntityId, nonceValue);
                            }

                            operation.Data.Remove("qrNonce");

                            // Nonce only: nothing on the delivery item changes, so skip the
                            // write and keep the version. Bumping it made the scanner's
                            // queued confirm (sent at the version it last pulled) conflict,
                            // and server-wins silently undid the pickup or collection.
                            // The farmer may store the nonce and nothing else: other fields
                            // riding along are ignored, never written by the farmer.
                            if (farmerNonceOnly || operation.Data.Keys.All(k => k is "version" or "updatedAt"))
                            {
                                return new SyncOperationResult
                                {
                                    OperationId = operation.OperationId,
                                    Status = "success",
                                    ServerVersion = existing.TryGetValue("version", out var nonceVer)
                                        && int.TryParse(nonceVer.N, out var nonceVerN) ? nonceVerN : 0,
                                    ServerData = ScrubDeliveryForClient(CropRepository.FromItem(existing))
                                };
                            }
                        }

                        // ESC-35: the trucker's drop-off nonce goes to the secret store too. The
                        // operation stores it and nothing else, never moving the delivery's version.
                        if (dropoffNonce != null)
                        {
                            return await StoreDropoffNonceAsync(operation, existing, currentStatus, dropoffNonce, context);
                        }
                    }

                    // QR nonce cross-validation for pickedUp/delivered transitions.
                    // The nonce lives in a SEPARATE secret store (never on the delivery item);
                    // in-flight deliveries created before that store fall back to the legacy
                    // on-record qrNonce. When set after a successful CAS, this holds the status
                    // whose single-use marker should be recorded (consume-after-commit).
                    string? nonceToConsumeStatus = null;
                    // T-1: how the handoff was proven ("qr" scanned, "code" typed), recorded with
                    // the transition's audit entry as dispute and fraud evidence.
                    string? handoffMethod = null;
                    if (operation.Data.TryGetValue("status", out var statusForNonce))
                    {
                        var nStatus = statusForNonce?.ToString();
                        if (nStatus is "pickedUp" or "delivered")
                        {
                            var secret = _qrSecretRepo != null
                                ? await _qrSecretRepo.GetAsync(operation.EntityId)
                                : null;
                            // The on-record nonce is trusted only without a secret store: rows
                            // written before SIM-41 may carry a trucker-authored value.
                            var legacyNonce = _qrSecretRepo == null
                                && existing.TryGetValue("qrNonce", out var qnAttr)
                                ? qnAttr.S : null;
                            // ESC-35: a trucker delivery's drop-off is checked against the trucker's own
                            // nonce once one is stored, else against the farmer's (1.22.0: the farmer's
                            // token kept on the trucker's phone, for apps that issue none).
                            var truckerDropoff = nStatus == "delivered" && !string.Equals(
                                existing.TryGetValue("deliveryType", out var dropoffTypeAttr) ? dropoffTypeAttr.S : null,
                                SelfPickupDeliveryType, StringComparison.Ordinal);
                            var useDropoffNonce = truckerDropoff && !string.IsNullOrEmpty(secret?.DropoffNonce);
                            var effectiveNonce = useDropoffNonce ? secret!.DropoffNonce : secret?.Nonce ?? legacyNonce;
                            var effectiveIssuedAt = useDropoffNonce ? secret!.DropoffIssuedAt : secret?.IssuedAt;

                            if (!string.IsNullOrEmpty(effectiveNonce))
                            {
                                var scannedNonce = operation.Data.TryGetValue("scannedQrNonce", out var snObj)
                                    ? snObj?.ToString() : null;
                                // T-1: the typed fallback. A scanned nonce wins when both are sent.
                                var typedCode = string.IsNullOrEmpty(scannedNonce)
                                    && operation.Data.TryGetValue("scannedQrCode", out var scObj)
                                    ? scObj?.ToString() : null;

                                // FRD-08: QR failures are soft. A code regenerated offline and scanned
                                // before it synced fails too; the score counts it once per delivery
                                // however often the queue retries it.
                                if (string.IsNullOrEmpty(scannedNonce) && string.IsNullOrEmpty(typedCode))
                                {
                                    return await RefuseWithSignalAsync(new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = "DELIVERY_NONCE_MISSING: QR nonce verification required for this transition."
                                    }, FraudDetectionService.QrHandoffFailureSignal, operation, "delivery", authenticatedUserId, context);
                                }

                                // ESC-35: a scan of the trucker's drop-off QR whose nonce has not reached
                                // the server yet (the trucker's phone was offline) waits for it, as a
                                // pickup waits for the farmer's nonce, and is refused once it is clearly
                                // never coming. Its tag says whose it is; a typed code carries none.
                                if (truckerDropoff && !useDropoffNonce
                                    && scannedNonce != null
                                    && scannedNonce.StartsWith(DropoffNoncePrefix, StringComparison.Ordinal))
                                {
                                    return AwaitHandoffNonce(operation, existing,
                                        "DELIVERY_NONCE_MISSING: The trucker's drop-off QR code never reached the server. Ask the trucker for a new drop-off code, or contact support.",
                                        "DELIVERY_NONCE_PENDING: Waiting for the trucker's drop-off QR code to sync. Will retry automatically.");
                                }

                                if (!string.IsNullOrEmpty(typedCode))
                                {
                                    var codeRefusal = await CheckTypedHandoffCodeAsync(
                                        operation, existing, secret, effectiveNonce, typedCode, nStatus,
                                        authenticatedUserId, context, useDropoffNonce);
                                    if (codeRefusal != null)
                                    {
                                        return codeRefusal;
                                    }

                                    handoffMethod = "code";
                                }
                                else if (!NonceEquals(scannedNonce!, effectiveNonce))
                                {
                                    return await RefuseWithSignalAsync(new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = "DELIVERY_NONCE_MISMATCH: Scanned QR nonce does not match generator's nonce."
                                    }, FraudDetectionService.QrHandoffFailureSignal, operation, "delivery", authenticatedUserId, context);
                                }
                                else
                                {
                                    handoffMethod = "qr";
                                }

                                // Secret-store path adds freshness + single-use replay protection
                                // (legacy on-record nonces lack issuance/consumption metadata).
                                if (secret != null)
                                {
                                    if (!string.IsNullOrEmpty(effectiveIssuedAt)
                                        && DateTime.TryParse(effectiveIssuedAt, CultureInfo.InvariantCulture,
                                            DateTimeStyles.RoundtripKind, out var issuedAt)
                                        && DateTime.UtcNow - issuedAt.ToUniversalTime()
                                            > TimeSpan.FromDays(QrNonceFreshnessDays))
                                    {
                                        return new SyncOperationResult
                                        {
                                            OperationId = operation.OperationId,
                                            Status = "error",
                                            Message = useDropoffNonce
                                                ? "DELIVERY_NONCE_EXPIRED: QR code has expired. Ask the trucker for a new drop-off code."
                                                : "DELIVERY_NONCE_EXPIRED: QR code has expired. Ask the farmer to show a fresh code."
                                        };
                                    }

                                    var consumedOp = secret.ConsumedOpFor(nStatus);
                                    if (consumedOp != null && consumedOp != operation.OperationId)
                                    {
                                        return await RefuseWithSignalAsync(new SyncOperationResult
                                        {
                                            OperationId = operation.OperationId,
                                            Status = "error",
                                            Message = "DELIVERY_NONCE_REPLAY: This QR code was already used for this step."
                                        }, FraudDetectionService.QrHandoffFailureSignal, operation, "delivery", authenticatedUserId, context);
                                    }

                                    // Consume only AFTER the version CAS succeeds (below), so a
                                    // rejected transition never burns the nonce. A same-op retry
                                    // (consumedOp == this op) is idempotent — do not re-consume.
                                    if (consumedOp == null)
                                    {
                                        nonceToConsumeStatus = nStatus;
                                    }
                                }
                            }
                            else if (nStatus == "pickedUp" || string.Equals(
                                existing.TryGetValue("deliveryType", out var gateTypeAttr) ? gateTypeAttr.S : null,
                                SelfPickupDeliveryType, StringComparison.Ordinal))
                            {
                                // FRD-11: a pickup at the farm (the trucker's pickedUp, a self-pickup
                                // buyer's delivered) is proven only by the farmer's QR, whatever the
                                // money, so no escrow is read. Without the farmer's nonce it waits for
                                // it to sync (offline first: the op stays queued) and is refused once
                                // it is clearly never coming.
                                return AwaitHandoffNonce(operation, existing,
                                    "DELIVERY_NONCE_MISSING: The farmer's QR code never reached the server for this pickup. Contact support.");
                            }
                            else
                            {
                                // No nonce on either source for a trucker's drop-off. Money-bearing
                                // deliveries must NOT fail open: DEFER (offline-first, the op stays
                                // queued and retries) until the farmer's nonce syncs, then hard-fail
                                // once it's clearly never coming. A cash (no-escrow) delivery picked
                                // up without a QR before FRD-11 keeps the allow-through.
                                bool hasEscrow;
                                try
                                {
                                    hasEscrow = await HasDeliveryEscrowAsync(existing, operation.EntityId);
                                }
                                catch (Exception ex)
                                {
                                    // FUP-42: an unknown answer is never cash. Nothing is written and the
                                    // op stays queued without using a retry. No 14-day hard fail here:
                                    // the delivery may carry no money at all. A read that keeps failing
                                    // would defer for ever, so the token is alarmed (order escrow problems).
                                    var gateOrderId = existing.TryGetValue("orderId", out var gateOrderAttr)
                                        ? gateOrderAttr.S : null;
                                    context.Logger.LogError(
                                        $"QR_GATE_UNDECIDED deliveryId={MoneyAlarmLog.LogId(operation.EntityId)} "
                                        + $"orderId={MoneyAlarmLog.LogId(gateOrderId)} reason={ex.GetType().Name} "
                                        + $"detail={MoneyAlarmLog.LogText(ex.Message)}");
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "deferred",
                                        Message = "DELIVERY_NONCE_PENDING: The payment check could not be completed. Will retry automatically."
                                    };
                                }

                                if (hasEscrow)
                                {
                                    return AwaitHandoffNonce(operation, existing,
                                        "DELIVERY_NONCE_MISSING: QR verification could not be completed for this payment. Contact support.");
                                }
                            }
                        }
                    }

                    // Field whitelist — reject unknown fields
                    // FIX-005: scannedQrNonce is allowed in data for validation but not persisted;
                    // so is its typed fallback scannedQrCode (T-1).
                    var unknownFields = operation.Data.Keys
                        .Where(k => !AllowedDeliveryFields.Contains(k) && k != "version"
                            && k != "scannedQrNonce" && k != "scannedQrCode")
                        .ToList();
                    if (unknownFields.Count > 0)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"DELIVERY_VALIDATION: Unknown fields: {string.Join(", ", unknownFields)}"
                        };
                    }

                    // SIM-09: the parties, order, listing, trip and type of a delivery are fixed
                    // at create; an update may only echo them unchanged.
                    var deliveryIdentityError = RejectImmutableFieldChanges(
                        operation.Data,
                        ImmutableDeliveryIdentityFields,
                        key => existing.TryGetValue(key, out var storedAttr) ? storedAttr.S : null);
                    if (deliveryIdentityError != null)
                    {
                        return await RefuseWithSignalAsync(new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = $"DELIVERY_VALIDATION: {deliveryIdentityError}"
                        }, FraudDetectionService.ImmutableFieldChangeSignal, operation, "delivery", authenticatedUserId, context);
                    }

                    // F12-SEC-06: Validate disputeReason length (aligned with contract: 500 chars)
                    if (operation.Data.TryGetValue("disputeReason", out var drObj) && drObj != null)
                    {
                        var drText = drObj.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(drText) || drText.Length > 500)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_INVALID_FIELD: disputeReason must be 1-500 characters."
                            };
                        }
                    }

                    // FIX-011 (path traversal) and FRD-12: a photo ref on a delivery op is checked,
                    // then dropped; photos are attached only through delivery_photo.
                    var photoRefError = DropDeliveryOpPhotoRefs(operation);
                    if (photoRefError != null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = photoRefError
                        };
                    }

                    // F12-SEC-05: GPS proximity validation for pickup/delivery
                    var resolvedNewStatus = operation.Data.TryGetValue("status", out var statusForGps)
                        ? statusForGps?.ToString() : null;

                    // FUP-246: each handoff field only with its own status, so no later step rewrites it.
                    DropOtherHandoffFields(operation, resolvedNewStatus, context);

                    if (resolvedNewStatus == "pickedUp"
                        && operation.Data.TryGetValue("gpsPickup", out var gpsPickupObj)
                        && gpsPickupObj is System.Text.Json.JsonElement pickupJe
                        && pickupJe.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        // Guard presence/type — a present-but-malformed gpsPickup would
                        // otherwise throw KeyNotFoundException → masked as a generic 500.
                        if (!pickupJe.TryGetProperty("latitude", out var pickupLatEl) || pickupLatEl.ValueKind != System.Text.Json.JsonValueKind.Number
                            || !pickupJe.TryGetProperty("longitude", out var pickupLngEl) || pickupLngEl.ValueKind != System.Text.Json.JsonValueKind.Number)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_VALIDATION: gpsPickup requires numeric latitude and longitude."
                            };
                        }
                        var pickupLat = pickupLatEl.GetDouble();
                        var pickupLng = pickupLngEl.GetDouble();

                        var listingId = existing.TryGetValue("listingId", out var lid) ? lid.S : null;
                        if (listingId != null)
                        {
                            var listing = await _cropRepo.GetByIdAsync(listingId);
                            if (listing != null
                                && listing.TryGetValue("latitude", out var listingLatObj)
                                && listing.TryGetValue("longitude", out var listingLonObj)
                                && double.TryParse(listingLatObj.ToString(), out var listingLat)
                                && double.TryParse(listingLonObj.ToString(), out var listingLon))
                            {
                                var distance = GpsValidator.HaversineDistance(
                                    pickupLat, pickupLng, listingLat, listingLon);
                                if (distance > 5.0)
                                {
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = $"DELIVERY_GPS_PROXIMITY: Pickup GPS is {distance:F1}km from listing location (max 5km)."
                                    };
                                }
                            }
                        }
                    }

                    // Self-pickup confirmation releases money, so GPS at the farm is MANDATORY
                    // (a normal trucker delivery's GPS is best-effort). Require gpsDropoff up front
                    // for self-pickup so the check below cannot be skipped by simply omitting it.
                    if (resolvedNewStatus == "delivered"
                        && string.Equals(
                            existing.TryGetValue("deliveryType", out var spDt) ? spDt.S : null,
                            SelfPickupDeliveryType, StringComparison.Ordinal)
                        && !(operation.Data.TryGetValue("gpsDropoff", out var spGpsObj)
                            && spGpsObj is System.Text.Json.JsonElement spGpsJe
                            && spGpsJe.ValueKind == System.Text.Json.JsonValueKind.Object))
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "DELIVERY_GPS_PROXIMITY: Location is required to confirm self-pickup at the farm."
                        };
                    }

                    if (resolvedNewStatus == "delivered"
                        && operation.Data.TryGetValue("gpsDropoff", out var gpsDropoffObj)
                        && gpsDropoffObj is System.Text.Json.JsonElement dropoffJe
                        && dropoffJe.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        // Guard presence/type — a present-but-malformed gpsDropoff would
                        // otherwise throw KeyNotFoundException → masked as a generic 500.
                        if (!dropoffJe.TryGetProperty("latitude", out var dropoffLatEl) || dropoffLatEl.ValueKind != System.Text.Json.JsonValueKind.Number
                            || !dropoffJe.TryGetProperty("longitude", out var dropoffLngEl) || dropoffLngEl.ValueKind != System.Text.Json.JsonValueKind.Number)
                        {
                            return new SyncOperationResult
                            {
                                OperationId = operation.OperationId,
                                Status = "error",
                                Message = "DELIVERY_VALIDATION: gpsDropoff requires numeric latitude and longitude."
                            };
                        }
                        var dropoffLat = dropoffLatEl.GetDouble();
                        var dropoffLng = dropoffLngEl.GetDouble();

                        var deliveredDeliveryType = existing.TryGetValue("deliveryType", out var ddt)
                            ? ddt.S : null;
                        if (string.Equals(deliveredDeliveryType, SelfPickupDeliveryType, StringComparison.Ordinal))
                        {
                            // Self-pickup: the handoff happens AT THE FARM, so validate the buyer's
                            // GPS against the LISTING location (there is no trip destination). Listing
                            // GPS is MANDATORY — otherwise this money-releasing confirmation could be
                            // made from anywhere.
                            var listingId = existing.TryGetValue("listingId", out var lidD) ? lidD.S : null;
                            var listing = listingId != null ? await _cropRepo.GetByIdAsync(listingId) : null;
                            if (listing == null
                                || !listing.TryGetValue("latitude", out var slLatObj)
                                || !listing.TryGetValue("longitude", out var slLonObj)
                                || !double.TryParse(slLatObj?.ToString(), NumberStyles.Any,
                                    CultureInfo.InvariantCulture, out var listingLat)
                                || !double.TryParse(slLonObj?.ToString(), NumberStyles.Any,
                                    CultureInfo.InvariantCulture, out var listingLon))
                            {
                                return new SyncOperationResult
                                {
                                    OperationId = operation.OperationId,
                                    Status = "error",
                                    Message = "DELIVERY_GPS_PROXIMITY: This listing has no registered location, so self-pickup cannot be verified."
                                };
                            }

                            var spDistance = GpsValidator.HaversineDistance(
                                dropoffLat, dropoffLng, listingLat, listingLon);
                            if (spDistance > 5.0)
                            {
                                return new SyncOperationResult
                                {
                                    OperationId = operation.OperationId,
                                    Status = "error",
                                    Message = $"DELIVERY_GPS_PROXIMITY: You are {spDistance:F1}km from the farm location (max 5km)."
                                };
                            }
                        }
                        else
                        {
                            // LT-14: within 5 km of EITHER the order's own drop-off point OR the trip's
                            // destination point. The order's point defaults to the buyer's registered
                            // or intent place, so a buyer who meets the truck at its destination still
                            // passes; a failed order read throws and answers the retryable internal
                            // error, never a refusal judged without it.
                            var distance = await DropoffDistanceForConfirmAsync(existing, dropoffLat, dropoffLng);
                            if (distance is { } km && km > 5.0)
                            {
                                return new SyncOperationResult
                                {
                                    OperationId = operation.OperationId,
                                    Status = "error",
                                    Message = $"DELIVERY_GPS_PROXIMITY: Dropoff GPS is {km.ToString("F1", CultureInfo.InvariantCulture)}km from the drop-off point (max 5km)."
                                };
                            }
                        }
                    }

                    // ── GPS fraud detection (F-14) ──
                    // ESC-33: the flag is set here, the signal is written after the delivery write succeeded.
                    FraudCheckResult? gpsAnomaly = null;
                    if (_fraudService != null && resolvedNewStatus == "delivered" && operation.Data != null)
                    {
                        double? fraudPickupLat = null, fraudPickupLng = null, fraudDropoffLat = null, fraudDropoffLng = null;
                        string fraudFromWoreda = "", fraudToWoreda = "";

                        // Extract pickup GPS from existing delivery (recorded at pickedUp transition)
                        if (existing.TryGetValue("gpsPickup", out var gpAttr) && gpAttr.M != null)
                        {
                            if (gpAttr.M.TryGetValue("latitude", out var lat) && double.TryParse(lat.N, out var pLat))
                            {
                                fraudPickupLat = pLat;
                            }

                            if (gpAttr.M.TryGetValue("longitude", out var lng) && double.TryParse(lng.N, out var pLng))
                            {
                                fraudPickupLng = pLng;
                            }
                        }

                        // Extract dropoff GPS from incoming data
                        if (operation.Data.TryGetValue("gpsDropoff", out var fraudGdObj) && fraudGdObj is System.Text.Json.JsonElement fraudGdJe
                            && fraudGdJe.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            if (fraudGdJe.TryGetProperty("latitude", out var lat))
                            {
                                fraudDropoffLat = lat.GetDouble();
                            }

                            if (fraudGdJe.TryGetProperty("longitude", out var lng))
                            {
                                fraudDropoffLng = lng.GetDouble();
                            }
                        }

                        // Get woredas from the associated trip
                        var fraudTripId = existing.TryGetValue("tripId", out var ftAttr) ? ftAttr.S : null;
                        if (fraudTripId != null)
                        {
                            var fraudTrip = await GetTripItemAsync(fraudTripId);
                            if (fraudTrip != null)
                            {
                                fraudFromWoreda = fraudTrip.TryGetValue("fromWoreda", out var fw) ? fw.S ?? "" : "";
                                fraudToWoreda = fraudTrip.TryGetValue("toWoreda", out var tw) ? tw.S ?? "" : "";
                            }
                        }

                        var gpsCheck = _fraudService.CheckGpsConsistency(
                            fraudPickupLat, fraudPickupLng, fraudDropoffLat, fraudDropoffLng, fraudFromWoreda, fraudToWoreda);

                        if (gpsCheck.IsFlagged)
                        {
                            operation.Data["gpsAnomalyFlag"] = true;
                            context.Logger.LogWarning(
                                $"GPS anomaly on delivery {MoneyAlarmLog.LogId(operation.EntityId)}: {MoneyAlarmLog.LogText(gpsCheck.Message)}");
                            gpsAnomaly = gpsCheck;
                        }
                    }

                    // SIM-08: op-level clientVersion (contract) or data.version (shipped clients).
                    TryGetClientVersion(operation, out var clientVersion);

                    var createdAt = existing["createdAt"].S;
                    var now = DateTime.UtcNow.ToString("o");
                    var newVersion = clientVersion + 1;

                    // Build update expression
                    var updateParts = new List<string> { "#updated = :updated", "#v = :newVersion" };
                    var attrNames = new Dictionary<string, string>
                {
                    { "#updated", "updatedAt" },
                    { "#v", "version" }
                };
                    var attrValues = new Dictionary<string, AttributeValue>
                {
                    { ":updated", new AttributeValue { S = now } },
                    { ":newVersion", new AttributeValue { N = newVersion.ToString() } },
                    { ":expectedVersion", new AttributeValue { N = clientVersion.ToString() } }
                };

                    var i = 0;
                    foreach (var kvp in operation.Data ?? new Dictionary<string, object>())
                    {
                        if (kvp.Key is "deliveryId" or "version" or "createdAt" or "updatedAt" or "qrToken")
                        {
                            continue;
                        }

                        if (!AllowedDeliveryFields.Contains(kvp.Key))
                        {
                            continue;
                        }

                        // FIX-007: Identity fields are immutable after creation
                        if (ImmutableDeliveryFields.Contains(kvp.Key))
                        {
                            continue;
                        }

                        // Handle GPS fields as nested DynamoDB Map attributes
                        if (kvp.Key is "gpsPickup" or "gpsDropoff")
                        {
                            if (kvp.Value is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                // Guard property presence/type: GetProperty throws on a present-but-
                                // malformed GPS object (missing keys), which the outer catch would mask
                                // as a generic 500 and burn a sync retry. Fail with a clean validation error.
                                if (!je.TryGetProperty("latitude", out var latEl) || latEl.ValueKind != System.Text.Json.JsonValueKind.Number
                                    || !je.TryGetProperty("longitude", out var lngEl) || lngEl.ValueKind != System.Text.Json.JsonValueKind.Number)
                                {
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = $"DELIVERY_VALIDATION: {kvp.Key} requires numeric latitude and longitude."
                                    };
                                }
                                var lat = latEl.GetDouble();
                                var lng = lngEl.GetDouble();

                                if (double.IsNaN(lat) || double.IsInfinity(lat) || lat < -90 || lat > 90
                                    || double.IsNaN(lng) || double.IsInfinity(lng) || lng < -180 || lng > 180)
                                {
                                    return new SyncOperationResult
                                    {
                                        OperationId = operation.OperationId,
                                        Status = "error",
                                        Message = $"DELIVERY_VALIDATION: Invalid GPS coordinates for {kvp.Key}. Latitude must be -90 to 90, longitude -180 to 180."
                                    };
                                }

                                var nameAlias = $"#f{i}";
                                var valueAlias = $":f{i}";
                                attrNames[nameAlias] = kvp.Key;
                                attrValues[valueAlias] = new AttributeValue
                                {
                                    M = new Dictionary<string, AttributeValue>
                                {
                                    { "latitude", new AttributeValue { N = lat.ToString(CultureInfo.InvariantCulture) } },
                                    { "longitude", new AttributeValue { N = lng.ToString(CultureInfo.InvariantCulture) } }
                                }
                                };
                                updateParts.Add($"{nameAlias} = {valueAlias}");
                                i++;
                            }
                            continue;
                        }

                        var na = $"#f{i}";
                        var va = $":f{i}";
                        attrNames[na] = kvp.Key;
                        attrValues[va] = CropRepository.ToAttributeValue(kvp.Value);
                        updateParts.Add($"{na} = {va}");
                        i++;
                    }

                    // FIX-006: Server-side timestamps for status transitions
                    if (operation.Data != null && operation.Data.TryGetValue("status", out var tsStatusObj))
                    {
                        var tsStatus = tsStatusObj?.ToString();
                        if (tsStatus == "pickedUp")
                        {
                            var tsAlias = $"#f{i}";
                            var tsValAlias = $":f{i}";
                            attrNames[tsAlias] = "pickedUpAt";
                            attrValues[tsValAlias] = new AttributeValue { S = now };
                            updateParts.Add($"{tsAlias} = {tsValAlias}");
                            i++;

                            // FRD-09: when the phone made the pickup scan (the operation's timestamp), which
                            // differs from pickedUpAt when it synced late; the impossible delivery time rule
                            // reads it at the confirm. Server-only; an implausible value is not stored.
                            if (TransactionPatternRules.PlausibleCaptureTime(
                                    operation.Timestamp, TransactionPatternRules.ParseUtc(now) ?? DateTime.UtcNow) is { } pickupCaptured)
                            {
                                attrNames[$"#f{i}"] = TransactionPatternRules.PickupCapturedAtField;
                                attrValues[$":f{i}"] = new AttributeValue
                                {
                                    S = pickupCaptured.ToString("o", CultureInfo.InvariantCulture)
                                };
                                updateParts.Add($"#f{i} = :f{i}");
                                i++;
                            }
                        }
                        else if (tsStatus == "delivered")
                        {
                            var tsAlias = $"#f{i}";
                            var tsValAlias = $":f{i}";
                            attrNames[tsAlias] = "confirmedAt";
                            attrValues[tsValAlias] = new AttributeValue { S = now };
                            updateParts.Add($"{tsAlias} = {tsValAlias}");
                            i++;
                        }
                    }

                    // Sort key is tripId (not createdAt) per database-stack.ts
                    var existingTripIdForKey = existing.TryGetValue("tripId", out var tripIdForKey)
                        ? tripIdForKey.S : "";
                    var updateRequest = new UpdateItemRequest
                    {
                        TableName = _deliveriesTable,
                        Key = new Dictionary<string, AttributeValue>
                    {
                        { "deliveryId", new AttributeValue { S = operation.EntityId } },
                        { "tripId", new AttributeValue { S = existingTripIdForKey } }
                    },
                        UpdateExpression = "SET " + string.Join(", ", updateParts),
                        ConditionExpression = "#v = :expectedVersion",
                        ExpressionAttributeNames = attrNames,
                        ExpressionAttributeValues = attrValues,
                        ReturnValues = ReturnValue.ALL_NEW
                    };

                    // ESC-07: a dispute freezes the escrow before anything is written. Only a
                    // frozen (or already disputed) escrow lets the delivery and order move; a
                    // refused freeze writes nothing and the client is told why.
                    var deliveryOrderId = existing.TryGetValue("orderId", out var deliveryOrderAttr)
                        && !string.IsNullOrEmpty(deliveryOrderAttr.S) ? deliveryOrderAttr.S : null;
                    var isDispute = operation.Data != null
                        && operation.Data.TryGetValue("status", out var disputeStatusObj)
                        && disputeStatusObj?.ToString() == "disputed";
                    var disputeText = isDispute && operation.Data!.TryGetValue("disputeReason", out var disputeTextObj)
                        ? disputeTextObj?.ToString() : null;
                    DeliveryDisputeFreezeResult? freeze = null;
                    if (isDispute)
                    {
                        var (stop, frozen) = await FreezeBeforeDeliveryDisputeAsync(
                            operation, deliveryOrderId, clientVersion, authenticatedUserId, disputeText, context);
                        if (stop != null)
                        {
                            return stop;
                        }

                        freeze = frozen;
                    }

                    try
                    {
                        var response = await _dynamoDb.UpdateItemAsync(updateRequest);
                        var result = ScrubDeliveryForClient(CropRepository.FromItem(response.Attributes));

                        // Right after the write, before the steps below that can fail: main wrote this
                        // signal before the delivery write, so a confirmed delivery must not lose it.
                        if (gpsAnomaly != null)
                        {
                            await TryRecordGpsAnomalySignalAsync(operation, gpsAnomaly, authenticatedUserId, context);
                        }

                        // FUP-203: the trucker's row, judged from the trucker's own check-ins, whatever the
                        // buyer's coordinates said. `now` is the confirmedAt just written.
                        if (resolvedNewStatus == "delivered")
                        {
                            await TryRecordTruckerGpsAnomalyAsync(operation, existing, now, authenticatedUserId, context);
                        }

                        // Audit the delivery transition (who/when; GPS is joined at
                        // read time from the delivery record) for the monitor.
                        if (operation.Data != null
                            && operation.Data.TryGetValue("status", out var newDelivStatusObj))
                        {
                            var toStatus = newDelivStatusObj?.ToString() ?? "";
                            var fromStatus = existing.TryGetValue("status", out var exStatus)
                                ? exStatus.S : null;
                            var deliveryTxId = existing.TryGetValue("orderId", out var exOid)
                                && !string.IsNullOrEmpty(exOid.S)
                                ? exOid.S : operation.EntityId;
                            await EmitTxAuditAsync(context, deliveryTxId, "delivery", operation.EntityId,
                                DeliveryActionFor(toStatus), fromStatus, toStatus,
                                metadata: handoffMethod != null ? $"handoff={handoffMethod}" : null);
                        }

                        // SIM-11: the order follows its delivery (pickedUp -> pickup_confirmed,
                        // inTransit -> in_transit, delivered -> delivered; a self-pickup jumps
                        // from accepted straight to delivered). Best-effort, system actor.
                        // ESC-07: the order of a disputed delivery follows through the escrow
                        // service's dispute mirror (consistent reads, retries, audited).
                        if (freeze is { MayMirror: true })
                        {
                            await TryMirrorDeliveryDisputeAsync(
                                freeze, deliveryOrderId, operation.EntityId, authenticatedUserId, disputeText,
                                markDelivery: false, context);
                        }
                        else if (!isDispute
                            && deliveryOrderId != null
                            && operation.Data != null
                            && operation.Data.TryGetValue("status", out var followStatusObj)
                            && followStatusObj?.ToString() is string followStatus)
                        {
                            await TryAdvanceOrderForDeliveryAsync(deliveryOrderId, followStatus, context);
                        }

                        // Bridge delivery confirmation -> escrow release. When the buyer
                        // confirms delivery, stamp the linked escrow's deliveryConfirmedAt +
                        // disputeWindowEndsAt so the auto-release cron settles to the farmer
                        // (and the auto-refund cron skips it). Without this, a delivered order
                        // was never paid out and the buyer was refunded after 7 days.
                        // Non-fatal: a confirmed delivery must still succeed even if the
                        // escrow can't be stamped here (reconciliation cron is the backstop).
                        // Single-use: burn the nonce for this transition now that the CAS
                        // committed. Ordered after the write so a failed CAS never consumes it.
                        if (nonceToConsumeStatus != null && _qrSecretRepo != null)
                        {
                            await _qrSecretRepo.MarkConsumedAsync(
                                operation.EntityId, nonceToConsumeStatus, operation.OperationId);
                        }

                        if (_escrowRepo != null
                            && operation.Data != null
                            && operation.Data.TryGetValue("status", out var confirmedStatusObj)
                            && confirmedStatusObj?.ToString() == "delivered")
                        {
                            await StampEscrowDeliveryConfirmedAsync(operation.EntityId, now,
                                existing.TryGetValue("truckerId", out var stampTruckerAttr) ? stampTruckerAttr.S : null,
                                context, deliveryOrderId);
                        }

                        await TryRecordDeliveryPatternSignalsAsync(
                            operation, existing, response.Attributes, freeze, authenticatedUserId, context);

                        // ESC-17: the last delivery done (delivered or disputed) finishes its trip.
                        if (operation.Data != null
                            && operation.Data.TryGetValue("status", out var tripFollowStatusObj)
                            && tripFollowStatusObj?.ToString() is string tripFollowStatus)
                        {
                            await TryCompleteTripAsync(existing, operation.EntityId, tripFollowStatus, context);
                        }

                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "success",
                            ServerVersion = newVersion,
                            ServerData = result
                        };
                    }
                    catch (ConditionalCheckFailedException)
                    {
                        var conflictRow = existing;
                        if (freeze is { MayMirror: true })
                        {
                            // ESC-07: the delivery changed after the version check, but the money is
                            // frozen, so the delivery and order still follow (status-conditioned
                            // writes). The conflict then carries the row as it is now.
                            await TryMirrorDeliveryDisputeAsync(
                                freeze, deliveryOrderId, operation.EntityId, authenticatedUserId, disputeText,
                                markDelivery: true, context);
                            conflictRow = await GetDeliveryItemAsync(operation.EntityId, consistentRead: true) ?? existing;
                        }

                        var serverData = ScrubDeliveryForClient(CropRepository.FromItem(conflictRow));
                        var serverVer = conflictRow.TryGetValue("version", out var sv)
                            ? int.TryParse(sv.N, out var svn) ? svn : 0
                            : 0;

                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "conflict",
                            ServerVersion = serverVer,
                            ServerData = serverData,
                            Message = $"Version conflict: server has version {serverVer}, client expected {clientVersion}."
                        };
                    }
                }

            case "delete":
                {
                    var existing = await GetDeliveryItemAsync(operation.EntityId);
                    if (existing == null)
                    {
                        return new SyncOperationResult
                        {
                            OperationId = operation.OperationId,
                            Status = "error",
                            Message = "Delivery not found."
                        };
                    }

                    // ESC-08: delete is the cancel that app versions before ESC-08 send (and
                    // still sit in offline queues); it follows the same rules as an update to
                    // cancelled.
                    return await CancelDeliveryAsync(operation, existing, authenticatedUserId, "delete", context);
                }

            default:
                return new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "error",
                    Message = $"Invalid action: '{operation.Action}'."
                };
        }
    }

    /// <summary>
    /// ESC-08: cancel a delivery, through an update to <c>cancelled</c> or the delete action.
    /// Any of its parties may cancel while it is <c>pending</c>; after pickup nobody can (an admin
    /// settles it through a dispute verdict). The row keeps no <c>deletedAt</c> so every party's
    /// delivery list still returns it, the cancel is audited, and the order's slot is freed.
    /// </summary>
    /// <param name="via">
    /// <c>update</c> or <c>delete</c>, recorded in the audit metadata. An update may carry nothing
    /// but the status and the version (the echo keys are already stripped); a delete's data is
    /// ignored apart from the version.
    /// </param>
    private async Task<SyncOperationResult> CancelDeliveryAsync(
        SyncOperationRequest operation,
        Dictionary<string, AttributeValue> existing,
        string? authenticatedUserId,
        string via,
        ILambdaContext context)
    {
        // Parties first, so nobody else learns anything about the delivery.
        if (!IsParty(existing, authenticatedUserId, "buyerId", "farmerId", "truckerId"))
        {
            return await RefuseWithSignalAsync(new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "DELIVERY_UNAUTHORIZED: Only participants can cancel a delivery."
            }, FraudDetectionService.RoleForbiddenSignal, operation, "delivery", authenticatedUserId, context);
        }

        if (via == "update")
        {
            var extraFields = operation.Data!.Keys.Where(k => k is not ("status" or "version")).ToList();
            if (extraFields.Count > 0)
            {
                return new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "error",
                    Message = $"DELIVERY_VALIDATION: A cancel may only send status and version, not: {string.Join(", ", extraFields)}"
                };
            }
        }

        var currentStatus = StatusOf(existing);
        var refusal = Refusal(existing);
        if (refusal != null)
        {
            return refusal;
        }

        // SIM-08: op-level clientVersion (contract) or data.version (shipped clients).
        TryGetClientVersion(operation, out var clientVersion);
        var now = DateTime.UtcNow.ToString("o");
        var newVersion = clientVersion + 1;

        var request = new UpdateItemRequest
        {
            TableName = _deliveriesTable,
            Key = new Dictionary<string, AttributeValue>
            {
                { "deliveryId", new AttributeValue { S = operation.EntityId } },
                { "tripId", new AttributeValue { S = existing.TryGetValue("tripId", out var tripAttr) ? tripAttr.S : "" } }
            },
            UpdateExpression = "SET #status = :cancelled, #updated = :updated, #v = :newVersion",
            ConditionExpression = "#v = :expectedVersion AND #status = :pending",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" },
                { "#updated", "updatedAt" },
                { "#v", "version" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":cancelled", new AttributeValue { S = "cancelled" } },
                { ":pending", new AttributeValue { S = "pending" } },
                { ":updated", new AttributeValue { S = now } },
                { ":newVersion", new AttributeValue { N = newVersion.ToString() } },
                { ":expectedVersion", new AttributeValue { N = clientVersion.ToString() } }
            },
            ReturnValues = ReturnValue.ALL_NEW
        };

        UpdateItemResponse response;
        try
        {
            response = await _dynamoDb.UpdateItemAsync(request);
        }
        catch (ConditionalCheckFailedException)
        {
            // Something landed since the read (a pickup, another party's cancel, any change):
            // judge the fresh row, so a pickup answers the refusal instead of a conflict whose
            // stale row the client would take as the server's view.
            var fresh = await GetDeliveryItemAsync(operation.EntityId, consistentRead: true) ?? existing;
            return Refusal(fresh) ?? new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "conflict",
                ServerVersion = VersionOf(fresh),
                ServerData = ScrubDeliveryForClient(CropRepository.FromItem(fresh)),
                Message = $"Version conflict: server has version {VersionOf(fresh)}, client expected {clientVersion}."
            };
        }

        var orderId = existing.TryGetValue("orderId", out var orderAttr) && !string.IsNullOrEmpty(orderAttr.S)
            ? orderAttr.S : null;
        await EmitTxAuditAsync(context, orderId ?? operation.EntityId, "delivery", operation.EntityId,
            "delivery_cancelled", currentStatus, "cancelled", metadata: $"via={via}");

        // SIM-09: a cancelled delivery frees the order's single delivery slot so a replacement
        // can be created. The cancel has landed either way; a release that fails here is taken
        // over by the next delivery create (the holder is cancelled).
        if (orderId != null)
        {
            try
            {
                await _orderRepo.ReleaseDeliverySlotAsync(orderId, operation.EntityId);
            }
            catch (Exception ex)
            {
                context.Logger.LogError(
                    $"DELIVERY_SLOT_RELEASE_FAILED orderId={MoneyAlarmLog.LogId(orderId)} "
                    + $"deliveryId={MoneyAlarmLog.LogId(operation.EntityId)}: [{ex.GetType().Name}]");
            }
        }

        // ESC-17: the cancel may close the last open delivery of a trip already on the road.
        await TryCompleteTripAsync(existing, operation.EntityId, "cancelled", context);

        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "success",
            ServerVersion = newVersion,
            ServerData = ScrubDeliveryForClient(CropRepository.FromItem(response.Attributes))
        };

        static string StatusOf(Dictionary<string, AttributeValue> row)
            => (row.TryGetValue("status", out var statusAttr) ? statusAttr.S : null) ?? "pending";

        static int VersionOf(Dictionary<string, AttributeValue> row)
            => row.TryGetValue("version", out var versionAttr) && int.TryParse(versionAttr.N, out var version)
                ? version : 0;

        // Why the row cannot be cancelled, or null when it is still pending.
        SyncOperationResult? Refusal(Dictionary<string, AttributeValue> row)
        {
            var status = StatusOf(row);
            if (status == "cancelled"
                || (row.TryGetValue("deletedAt", out var deletedAtAttr) && !string.IsNullOrEmpty(deletedAtAttr.S)))
            {
                // Another party cancelled first: the goal state already holds, so answer with the
                // server's row (the client settles it, server wins) instead of an error it would retry.
                return new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "conflict",
                    ServerVersion = VersionOf(row),
                    ServerData = ScrubDeliveryForClient(CropRepository.FromItem(row)),
                    Message = "DELIVERY_ALREADY_CANCELLED: The delivery is already cancelled."
                };
            }

            if (!DeliveryTransitions.TryGetValue(status, out var allowed) || !allowed.Contains("cancelled"))
            {
                return new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "error",
                    Message = $"DELIVERY_CANCEL_NOT_ALLOWED: A delivery can be cancelled only before pickup (status: {status}). "
                        + "After pickup, open a dispute or contact support."
                };
            }

            return null;
        }
    }

    /// <summary>
    /// LT-14: how far a trucker delivery's <c>gpsDropoff</c> is from its drop-off
    /// (delivery-verification-api.md 2.1.0, GPS Validation): the great circle km to the nearer of the
    /// trip's destination point and the order's own drop-off point (read strongly consistent). The
    /// trip is read first, and a fix within 5 km of it answers without the order read. Null when
    /// neither point exists: not checked.
    /// </summary>
    private async Task<double?> DropoffDistanceForConfirmAsync(
        Dictionary<string, AttributeValue> delivery, double latitude, double longitude)
    {
        double? nearest = null;
        var tripId = delivery.TryGetValue("tripId", out var tripIdAttr) ? tripIdAttr.S : null;
        var trip = string.IsNullOrEmpty(tripId) ? null : await GetTripItemAsync(tripId);
        if (TripTrack.PlannedDropoff(trip == null ? null : CropRepository.FromItem(trip), order: null) is { } tripPoint)
        {
            nearest = GpsValidator.HaversineDistance(latitude, longitude, tripPoint.Latitude, tripPoint.Longitude);
            if (nearest <= 5.0)
            {
                return nearest;
            }
        }

        var orderId = delivery.TryGetValue("orderId", out var orderIdAttr) ? orderIdAttr.S : null;
        var order = string.IsNullOrEmpty(orderId) ? null : await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
        if (OrderDeliveryLocation.Of(order) is { } orderPoint)
        {
            var toOrder = GpsValidator.HaversineDistance(latitude, longitude, orderPoint.Latitude, orderPoint.Longitude);
            nearest = nearest is { } toTrip ? Math.Min(toTrip, toOrder) : toOrder;
        }

        return nearest;
    }

    /// <summary>
    /// Query for a trip item by tripId (partition key).
    /// </summary>
    private async Task<Dictionary<string, AttributeValue>?> GetTripItemAsync(string tripId)
    {
        var request = new QueryRequest
        {
            TableName = _tripsTable,
            KeyConditionExpression = "tripId = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":id", new AttributeValue { S = tripId } }
            },
            Limit = 1
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items.Count > 0 ? response.Items[0] : null;
    }

    /// <summary>
    /// Query for a delivery item by deliveryId (partition key).
    /// </summary>
    private async Task<Dictionary<string, AttributeValue>?> GetDeliveryItemAsync(
        string deliveryId, bool consistentRead = false)
    {
        var request = new QueryRequest
        {
            TableName = _deliveriesTable,
            KeyConditionExpression = "deliveryId = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":id", new AttributeValue { S = deliveryId } }
            },
            Limit = 1,
            ConsistentRead = consistentRead
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items.Count > 0 ? response.Items[0] : null;
    }

    /// <summary>
    /// ESC-08: write a new delivery together with its order's slot claim. When another delivery
    /// holds the slot and that holder is dead, take the slot over from exactly the claim that was
    /// read; a live holder, or a holder that changed since it was read, keeps it (no retry loop).
    /// </summary>
    private async Task<DeliverySlotWriteResult> CreateDeliveryWithSlotAsync(
        string orderId, Dictionary<string, AttributeValue> item, ILambdaContext context)
    {
        var write = await _orderRepo.TryCreateDeliveryWithSlotAsync(orderId, _deliveriesTable, item);
        if (write != DeliverySlotWriteResult.SlotTaken)
        {
            return write;
        }

        var stale = await FindStaleDeliverySlotAsync(orderId);
        if (stale == null)
        {
            return write;
        }

        write = await _orderRepo.TryCreateDeliveryWithSlotAsync(orderId, _deliveriesTable, item, stale);
        if (write == DeliverySlotWriteResult.Written)
        {
            context.Logger.LogWarning(
                $"DELIVERY_SLOT_RECLAIMED orderId={MoneyAlarmLog.LogId(orderId)} "
                + $"from={MoneyAlarmLog.LogId(stale.DeliveryId)} to={MoneyAlarmLog.LogId(item["deliveryId"].S)}");
        }

        return write;
    }

    /// <summary>
    /// ESC-08: the order's slot holder when it can no longer become a handoff: its delivery is
    /// cancelled (a slot release after the cancel did not land), or it has no row and its claim
    /// carries no time (a claim left by a create from before ESC-08, which wrote the claim and the
    /// row separately). Since ESC-08 a claim is always timed and written with its row, so a timed
    /// claim without a row is never taken over.
    /// </summary>
    private async Task<DeliverySlotHolder?> FindStaleDeliverySlotAsync(string orderId)
    {
        var order = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
        var holderId = order != null && order.TryGetValue("activeDeliveryId", out var holderObj)
            ? holderObj?.ToString() : null;
        if (string.IsNullOrEmpty(holderId))
        {
            return null;
        }

        var claimedAt = order!.TryGetValue("activeDeliveryClaimedAt", out var claimedObj)
            ? claimedObj?.ToString() : null;
        var holder = await GetDeliveryItemAsync(holderId, consistentRead: true);
        var dead = holder == null
            ? claimedAt == null
            : holder.TryGetValue("status", out var holderStatus) && holderStatus.S == "cancelled";
        return dead ? new DeliverySlotHolder(holderId, claimedAt) : null;
    }

    /// <summary>
    /// Stamp the escrow linked to a confirmed delivery with deliveryConfirmedAt +
    /// disputeWindowEndsAt (now + 48h) so the auto-release cron settles to the
    /// farmer and the auto-refund cron skips it. FRD-13: with the payout delay's
    /// releaseNotBefore in the same write (the trucker named by the delivery when the
    /// escrow has none yet). Idempotent: only stamps an
    /// "escrowed" escrow that has not already been confirmed. Best-effort — errors
    /// are logged, never thrown, so a confirmed delivery is never rolled back.
    /// FUP-66: when the <c>DeliveryIndex</c> names no escrow, the order's own escrow
    /// (<c>escrow-order-{orderId}</c>) is stamped if its link to the delivery never landed
    /// (<see cref="FindUnlinkedOrderEscrowAsync"/>).
    /// </summary>
    private async Task StampEscrowDeliveryConfirmedAsync(
        string deliveryId, string confirmedAt, string? deliveryTruckerId, ILambdaContext context,
        string? deliveryOrderId = null)
    {
        if (_escrowRepo == null)
        {
            return;
        }

        try
        {
            var escrow = await _escrowRepo.GetByDeliveryIdAsync(deliveryId)
                ?? await FindUnlinkedOrderEscrowAsync(deliveryOrderId, deliveryId);
            if (escrow == null)
            {
                return; // no escrow pays for this delivery (e.g. cash order)
            }

            var status = escrow.TryGetValue("status", out var st) ? st?.ToString() : null;
            var alreadyConfirmed = escrow.TryGetValue("deliveryConfirmedAt", out var dc)
                && !string.IsNullOrEmpty(dc?.ToString());
            if (status != "escrowed" || alreadyConfirmed)
            {
                return; // not settleable, or already stamped (idempotent replay)
            }

            var escrowId = escrow.TryGetValue("escrowId", out var eid) ? eid?.ToString() ?? "" : "";
            var createdAt = escrow.TryGetValue("createdAt", out var ca) ? ca?.ToString() ?? "" : "";
            if (string.IsNullOrEmpty(escrowId) || string.IsNullOrEmpty(createdAt))
            {
                return;
            }

            // Base the dispute window on the same confirmation timestamp so the two
            // fields are consistent (not skewed by a fresh UtcNow read).
            var windowBase = DateTime.TryParse(confirmedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var confirmedDt)
                ? confirmedDt
                : DateTime.UtcNow;
            var disputeWindowEndsAt = windowBase.AddHours(48).ToString("o");
            var fields = new Dictionary<string, object>
            {
                { "deliveryConfirmedAt", confirmedAt },
                { "disputeWindowEndsAt", disputeWindowEndsAt }
            };

            // FRD-13: the payees' payout delay, never before the window. The policy never throws, and a
            // payee it cannot read gets the longest delay.
            PayoutDelayDecision? delay = null;
            if (PayoutDelay != null)
            {
                delay = await PayoutDelay.DecideAsync(escrow, deliveryTruckerId, windowBase, windowBase.AddHours(48),
                    DateTime.UtcNow, _batchPayoutDelays);
                fields[PayoutDelayPolicy.ReleaseNotBeforeField] = delay.Value.ReleaseNotBeforeIso;
            }

            await _escrowRepo.UpdateStatusAsync(
                escrowId,
                createdAt,
                newStatus: "escrowed",
                expectedCurrentStatus: "escrowed",
                additionalFields: fields);

            if (delay is { } decided)
            {
                context.Logger.LogInformation(decided.LogLine(escrowId));
            }

            context.Logger.LogInformation(
                $"Escrow {MoneyAlarmLog.LogId(escrowId)} stamped for delivery {MoneyAlarmLog.LogId(deliveryId)}; auto-release scheduled after the 48h dispute window and any payout delay.");
        }
        catch (Exception ex)
        {
            // A confirmed delivery must not be rolled back. Until the escrow is stamped the
            // auto-release never pays it: alarmed (ESC-16, escrow-delivery-stamp-failures), and the
            // escrow cron's hourly order-money sweep stamps it for a delivered order.
            context.Logger.LogError(
                $"{MoneyAlarmLog.EscrowDeliveryStampFailedToken} deliveryId={MoneyAlarmLog.LogId(deliveryId)} source=sync "
                + $"reason=[{ex.GetType().Name}]: delivery confirmed but its escrow is not marked delivery-confirmed; "
                + "the hourly order-money sweep retries the stamp.");
        }
    }

    /// <summary>
    /// FUP-66: the order's own escrow (<c>escrow-order-{orderId}</c>, strongly consistent) for a
    /// delivery the <c>DeliveryIndex</c> does not know: the link write failed
    /// (<c>ORDER_ESCROW_LINK_FAILED</c>) or the index lags. Only an escrow that names this order and
    /// no other delivery: one linked to a different delivery is left to that delivery. Null when the
    /// delivery has no order or the order has no such escrow. One read, only on the unlinked path.
    /// </summary>
    private async Task<Dictionary<string, object>?> FindUnlinkedOrderEscrowAsync(string? orderId, string deliveryId)
    {
        if (_escrowRepo == null || string.IsNullOrEmpty(orderId))
        {
            return null;
        }

        var escrow = await _escrowRepo.GetByIdAsync($"escrow-order-{orderId}", consistentRead: true);
        if (escrow == null)
        {
            return null;
        }

        var escrowOrderId = escrow.TryGetValue("orderId", out var eo) ? eo?.ToString() : null;
        var linkedDeliveryId = escrow.TryGetValue("deliveryId", out var ld) ? ld?.ToString() : null;
        var ownsDelivery = string.IsNullOrEmpty(linkedDeliveryId)
            || string.Equals(linkedDeliveryId, deliveryId, StringComparison.Ordinal);
        return string.Equals(escrowOrderId, orderId, StringComparison.Ordinal) && ownsDelivery ? escrow : null;
    }

    // ─── Order Processing ─────────────────────────────────────────────────

    /// <summary>
    /// Process an order sync operation (create, update).
    /// Uses server-authoritative state machine and commission calculation. LT-12: the order row a
    /// result carries back leaves out the drop-off note when the caller may not read it
    /// (<see cref="OrderDropoffNote.ForCaller"/>); LT-14: the same for the order's drop-off point, by
    /// its own rule (<see cref="OrderDeliveryLocation.ForCaller"/>).
    /// </summary>
    private async Task<SyncOperationResult> ProcessOrderAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        string? authenticatedRole,
        ILambdaContext context)
    {
        var result = operation.Action switch
        {
            "create" => await ProcessOrderCreateAsync(operation, authenticatedUserId, authenticatedRole, context),
            "update" => await ProcessOrderUpdateAsync(operation, authenticatedUserId, context),
            _ => new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = $"ORDER_VALIDATION: Invalid action '{operation.Action}' for orders. Allowed: create, update."
            }
        };
        // ESC-34: the check-ins as the parties read them, without the stamped pickup point.
        return PickupJudgement.ForClient(
            OrderDeliveryLocation.ForCaller(OrderDropoffNote.ForCaller(result, authenticatedUserId), authenticatedUserId));
    }

    /// <summary>
    /// Best-effort emit to the unified transaction-audit trail using the current
    /// batch's who/where context (or a system override for follow-on transitions).
    /// No-op when audit is unconfigured or no batch context is set.
    /// </summary>
    private Task EmitTxAuditAsync(
        ILambdaContext context,
        string transactionId,
        string entityType,
        string entityId,
        string action,
        string? fromState,
        string? toState,
        AuditContext? ctxOverride = null,
        string? metadata = null)
    {
        var ctx = ctxOverride ?? _batchAudit;
        if (ctx == null)
        {
            return Task.CompletedTask;
        }
        return TransactionAuditWriter.EmitAsync(
            _transactionAuditRepo, context, transactionId, entityType, entityId,
            action, fromState, toState, ctx, metadata);
    }

    /// <summary>
    /// Map an order status to a timeline action key. Reuses the vocabulary the
    /// admin app already localises (payment_verified / farmer_responded /
    /// order_cancelled / order_created); everything else is a generic
    /// order_updated with the from→to states carrying the detail.
    /// </summary>
    private static string OrderActionFor(string toStatus) => toStatus switch
    {
        "funded" => "payment_verified",
        "accepted" or "rejected" => "farmer_responded",
        "cancelled" => "order_cancelled",
        _ => "order_updated",
    };

    /// <summary>
    /// Delivery status → timeline action key (reuses picked_up / delivered, and since ESC-07
    /// dispute, the action every dispute hop is audited with).
    /// </summary>
    private static string DeliveryActionFor(string toStatus) => toStatus switch
    {
        "pickedUp" => "picked_up",
        "delivered" => "delivered",
        "disputed" => "dispute",
        _ => "delivery_updated",
    };

    private async Task<SyncOperationResult> ProcessOrderCreateAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        string? authenticatedRole,
        ILambdaContext context)
    {
        if (operation.Data == null || operation.Data.Count == 0)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "Create operation requires data payload."
            };
        }

        // Role check: only buyers can create orders
        if (!string.Equals(authenticatedRole, "buyer", StringComparison.OrdinalIgnoreCase))
        {
            return await RefuseWithSignalAsync(new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "ORDER_UNAUTHORIZED: Only buyers can create orders."
            }, RoleGateSignal(authenticatedRole), operation, "order", authenticatedUserId, context);
        }

        // Validate buyerId matches authenticated user
        if (!operation.Data.TryGetValue("buyerId", out var buyerIdObj)
            || buyerIdObj?.ToString() != authenticatedUserId)
        {
            return await RefuseWithSignalAsync(new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "ORDER_UNAUTHORIZED: buyerId must match authenticated user."
            }, FraudDetectionService.RoleForbiddenSignal, operation, "order", authenticatedUserId, context);
        }

        // Strip server-controlled fields
        foreach (var field in OrderServerOnlyFields)
        {
            operation.Data.Remove(field);
        }

        // ESC-15: the commitment commands are update-only. A create carrying a response would
        // plant "accepted" on the row and open the parties' phones before anyone agreed.
        var plantedCommands = OrderCommandFields.Where(operation.Data.ContainsKey).ToList();
        if (plantedCommands.Count > 0)
        {
            return await RefuseWithSignalAsync(
                OrderValidationError(operation, $"{string.Join(", ", plantedCommands)} cannot be set when creating an order."),
                FraudDetectionService.ImmutableFieldChangeSignal, operation, "order", authenticatedUserId, context);
        }

        // Field whitelist
        var unknownFields = operation.Data.Keys
            .Where(k => !AllowedOrderFields.Contains(k) && k != "version")
            .ToList();
        if (unknownFields.Count > 0)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = $"ORDER_VALIDATION: Unknown fields: {string.Join(", ", unknownFields)}"
            };
        }

        // LT-14: an order without a trip (self-pickup) has no drop-off; its point is ignored, never refused.
        IgnoreCreateDeliveryLocationWithoutTrip(operation);

        // Validate required fields
        var validationErrors = ValidateOrderFields(operation.Data, isCreate: true);
        if (validationErrors.Count > 0)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = $"ORDER_VALIDATION: {string.Join(" ", validationErrors)}"
            };
        }

        // LT-12: the drop-off note is stored trimmed, and not at all when empty. Like listing text it
        // may not carry a phone number (the farmer reads it before contact opens); checked after the
        // field rules, so one payload always gets the same answer.
        var dropoffNoteRefusal = ApplyCreateDropoffNote(operation);
        if (dropoffNoteRefusal != null)
        {
            return dropoffNoteRefusal;
        }

        // LT-14: the order's own drop-off point, checked with the field rules above; a pair of
        // nulls is no point and is not stored.
        ApplyCreateDeliveryLocation(operation);

        KeepServerCancelCodesReserved(operation.Data);

        // FRD-04 (fraud-detection-architecture.md 17.1): after the field rules, before the listing and
        // trip reads and the write. Only the buyer's tier: the counterparty's is never revealed (section 17).
        if (await CallerRiskTierAsync(authenticatedUserId!) == RiskTier.Critical)
        {
            return AccountHeldResult(operation);
        }

        // FUP-99: buyerName is shown to the farmer before escrow, so it comes from the
        // buyer's profile, never the client, which inherits the FUP-52 phone screen on the
        // profile fields. farmerName is stamped from the listing (BindOrderToListing) and
        // truckerName from the trip (BindOrderToTrip).
        var buyerNameError = await StampOrderBuyerNameAsync(operation.Data, authenticatedUserId!, context);
        if (buyerNameError != null)
        {
            return OrderValidationError(operation, buyerNameError);
        }

        // SIM-05: the client's copy of the listing and trip is not trusted. Load both,
        // bind every party, price and quantity to the server's records, and compute the
        // money fields from those records only.
        var listingId = operation.Data["listingId"]?.ToString() ?? "";
        var listing = await _cropRepo.GetByIdAsync(listingId);
        if (listing == null)
        {
            return OrderValidationError(operation, "Listing not found.");
        }

        var listingError = BindOrderToListing(operation.Data, listing, authenticatedUserId);
        if (listingError != null)
        {
            return OrderValidationError(operation, listingError);
        }

        var quantityKg = ParseDouble(operation.Data, "quantity");
        var pricePerUnit = ParseDouble(operation.Data, "pricePerUnit");
        var commissionRate = GetCommissionRate(context);

        var tripId = operation.Data.TryGetValue("tripId", out var tripIdObj) ? tripIdObj?.ToString() : null;
        var truckerId = operation.Data.TryGetValue("truckerId", out var truckerIdObj) ? truckerIdObj?.ToString() : null;
        if (string.IsNullOrEmpty(tripId) != string.IsNullOrEmpty(truckerId))
        {
            return OrderValidationError(operation, "tripId and truckerId must be provided together.");
        }

        var clientTransportCost = ParseDoubleOrNull(operation.Data, "transportCost");
        var transportCost = 0.0;
        if (!string.IsNullOrEmpty(tripId))
        {
            var trip = await GetTripItemAsync(tripId);
            if (trip == null)
            {
                return OrderValidationError(operation, "Trip not found.");
            }

            var tripError = BindOrderToTrip(
                operation.Data, trip, truckerId!, quantityKg, clientTransportCost, DateTime.UtcNow,
                enforceBookings: CommitmentsEnabled, out transportCost);
            if (tripError != null)
            {
                return tripError.StartsWith("TRIP_CAPACITY_EXCEEDED", StringComparison.Ordinal)
                    || tripError.StartsWith(OrderCommitment.TripNotEligibleCode, StringComparison.Ordinal)
                    ? new SyncOperationResult { OperationId = operation.OperationId, Status = "error", Message = tripError }
                    : OrderValidationError(operation, tripError);
            }
        }
        else if (clientTransportCost is double sentWithoutTrip && sentWithoutTrip != 0.0)
        {
            return OrderValidationError(operation, "transportCost requires a tripId.");
        }

        // SIM-30: each money field in ETB to the santim (2 dp, halves away from zero, as the escrow
        // rounds), computed in decimals so float noise never moves a santim (170 kg x 18.99 ETB x 0.05
        // is 161.41499... in doubles, 161.415 exactly, so 161.42). The commission is 5 % of the rounded
        // crop cost, and crop + transport + commission is exactly the orderTotal the buyer pays and the
        // order escrow holds (farmerPayout + truckerPayout + commission).
        var cropCost = RoundEtb((decimal)quantityKg * (decimal)pricePerUnit);
        var transportCostEtb = RoundEtb((decimal)transportCost);
        var commissionAmount = RoundEtb(cropCost * (decimal)commissionRate);
        var orderTotal = cropCost + transportCostEtb + commissionAmount;

        operation.Data["transportCost"] = (double)transportCostEtb;
        operation.Data["cropCost"] = (double)cropCost;
        operation.Data["commissionRate"] = commissionRate;
        operation.Data["commissionAmount"] = (double)commissionAmount;
        operation.Data["orderTotal"] = (double)orderTotal;

        // Force initial status
        operation.Data["status"] = "created";

        // ESC-29 (reputation-api.md 8.2): no new order with a restricted or suspended farmer or trucker.
        if (await RefuseRestrictedCounterpartyAsync(
                operation, context, ItemString(listing, "farmerId"), truckerId) is { } counterpartyRestricted)
        {
            return counterpartyRestricted;
        }

        var createResult = await _orderRepo.CreateAsync(operation.EntityId, operation.Data);

        if (createResult.IsSuccess)
        {
            await EmitTxAuditAsync(context, operation.EntityId, "order", operation.EntityId,
                "order_created", null, "created");
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "success",
                ServerVersion = 1,
                ServerData = createResult.Value
            };
        }

        // Already exists: return conflict; the order (phones, amounts) is echoed only
        // to one of its parties.
        var existing = await _orderRepo.GetByIdAsync(operation.EntityId);
        var serverVersion = existing != null && existing.TryGetValue("version", out var v)
            ? int.TryParse(v.ToString(), out var sv) ? sv : 1
            : 1;
        var isParty = existing != null
            && IsParty(existing, authenticatedUserId, "buyerId", "farmerId", "truckerId");

        // FRD-08: an order id of an order the caller is no party to is a probe.
        if (existing != null && !isParty)
        {
            await TryRecordFraudSignalAsync(
                authenticatedUserId ?? "", FraudDetectionService.IdorCreateConflictSignal, operation.EntityId,
                "order", "Create named an order id that belongs to other users.", context);
        }

        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "conflict",
            ServerVersion = serverVersion,
            ServerData = isParty ? existing : null,
            Message = "Order already exists on server."
        };
    }

    private async Task<SyncOperationResult> ProcessOrderUpdateAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        ILambdaContext context)
    {
        if (operation.Data == null || operation.Data.Count == 0)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "Update operation requires data payload."
            };
        }

        // FUP-21: strongly consistent. Every check below runs on this row, but the write's
        // version check uses the client's version, so a stale row can pass a transition the
        // stored order forbids. Two updates of one order in one batch (pay then cancel)
        // would otherwise validate the second against the row from before the first.
        var existingOrder = await _orderRepo.GetByIdAsync(operation.EntityId, consistentRead: true);
        if (existingOrder == null)
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "Order not found."
            };
        }

        // Authorization: the buyer or farmer of this order can update it; the order's
        // matched trucker may only drive the logistics statuses (SIM-11; the contract
        // says "either participant" for pickup_confirmed / in_transit / delivered).
        var orderBuyerId = existingOrder.TryGetValue("buyerId", out var bid) ? bid?.ToString() : null;
        var orderFarmerId = existingOrder.TryGetValue("farmerId", out var fid) ? fid?.ToString() : null;
        var orderTruckerId = existingOrder.TryGetValue("truckerId", out var otid) ? otid?.ToString() : null;
        var isBuyerOrFarmer = authenticatedUserId == orderBuyerId || authenticatedUserId == orderFarmerId;
        var isTruckerOnly = !isBuyerOrFarmer
            && !string.IsNullOrEmpty(orderTruckerId)
            && authenticatedUserId == orderTruckerId;

        if (!isBuyerOrFarmer && !isTruckerOnly)
        {
            return await RefuseWithSignalAsync(new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "ORDER_UNAUTHORIZED: You can only update orders you are a participant in."
            }, FraudDetectionService.RoleForbiddenSignal, operation, "order", authenticatedUserId, context);
        }

        // LT-12: the drop-off note before any other rule, so a command or a status write never
        // carries a note change past the buyer-only, phone and pickup checks.
        if (operation.Data.ContainsKey(OrderDropoffNote.Field))
        {
            var noteRefusal = await CheckDropoffNoteChangeAsync(
                operation, existingOrder, authenticatedUserId, orderBuyerId, context);
            if (noteRefusal != null)
            {
                return noteRefusal;
            }
        }

        // LT-14: the order's own drop-off point, by the same rules and at the same place.
        if (OrderDeliveryLocation.Sent(operation.Data))
        {
            var locationRefusal = await CheckDeliveryLocationChangeAsync(
                operation, existingOrder, authenticatedUserId, orderBuyerId, context);
            if (locationRefusal != null)
            {
                return locationRefusal;
            }
        }

        if (isTruckerOnly)
        {
            // ESC-15: the trucker answers the transport request (accept or decline) once the
            // farmer accepted; before that, and for anything but the logistics catch-ups, the
            // trucker may not touch the order.
            var truckerCommand = CommitmentCommandOf(operation.Data, OrderCommitment.TruckerResponseField);
            if (truckerCommand != null)
            {
                if (!CommitmentsEnabled)
                {
                    return OrderValidationError(operation, "Transport requests are not enabled on this server yet.");
                }

                return await ProcessCommitmentCommandAsync(
                    operation, existingOrder, CommitmentActor.Trucker, truckerCommand, authenticatedUserId!, context);
            }

            var truckerStatus = operation.Data.TryGetValue("status", out var tsObj) ? tsObj?.ToString() : null;
            if (truckerStatus is not ("pickup_confirmed" or "in_transit" or "delivered"))
            {
                return await RefuseWithSignalAsync(new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "error",
                    Message = "ORDER_UNAUTHORIZED: The trucker may only set pickup_confirmed, in_transit or delivered."
                }, FraudDetectionService.RoleForbiddenSignal, operation, "order", authenticatedUserId, context);
            }
        }
        else if (operation.Data.ContainsKey(OrderCommitment.TruckerResponseField))
        {
            return await RefuseWithSignalAsync(new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "ORDER_UNAUTHORIZED: Only the order's trucker can answer a transport request."
            }, FraudDetectionService.RoleForbiddenSignal, operation, "order", authenticatedUserId, context);
        }

        // ESC-15: the farmer's answer. The command shape (farmerResponse: accept|decline) and the
        // legacy status write (accepted|rejected) mean the same thing; with the feature on both go
        // through the commitment rules, with it off the command is translated to the old write.
        var farmerCommand = CommitmentCommandOf(operation.Data, OrderCommitment.FarmerResponseField);
        if (farmerCommand != null && authenticatedUserId != orderFarmerId)
        {
            return await RefuseWithSignalAsync(new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "ORDER_UNAUTHORIZED: Only the farmer can accept or decline the order."
            }, FraudDetectionService.RoleForbiddenSignal, operation, "order", authenticatedUserId, context);
        }

        if (CommitmentsEnabled && authenticatedUserId == orderFarmerId)
        {
            if (farmerCommand == null
                && operation.Data.TryGetValue("status", out var legacyFarmerStatus)
                && legacyFarmerStatus?.ToString() is "accepted" or "rejected"
                && ItemString(existingOrder, "status") == "funded")
            {
                farmerCommand = legacyFarmerStatus.ToString() == "accepted"
                    ? OrderCommitment.AcceptCommand
                    : OrderCommitment.DeclineCommand;
            }

            if (farmerCommand != null)
            {
                return await ProcessCommitmentCommandAsync(
                    operation, existingOrder, CommitmentActor.Farmer, farmerCommand, authenticatedUserId!, context);
            }
        }
        else if (farmerCommand != null)
        {
            operation.Data.Remove(OrderCommitment.FarmerResponseField);
            operation.Data.Remove(OrderCommitment.DeclineReasonField);
            operation.Data["status"] = farmerCommand == OrderCommitment.AcceptCommand ? "accepted" : "rejected";
        }

        // Strip server-controlled fields
        foreach (var field in OrderServerOnlyFields)
        {
            operation.Data.Remove(field);
        }

        // SIM-06: an update may only carry status and its companions. The echoed orderId
        // must be this order; updatedAt is server-stamped; unknown keys are rejected; every
        // identity or pricing field is immutable (an unchanged echo is dropped, a changed
        // value is rejected) so the stored money fields always match their inputs.
        if (operation.Data.TryGetValue("orderId", out var echoedOrderId))
        {
            if (!string.Equals(echoedOrderId?.ToString(), operation.EntityId, StringComparison.Ordinal))
            {
                return await RefuseWithSignalAsync(
                    OrderValidationError(operation, "orderId does not match the operation entityId."),
                    FraudDetectionService.ImmutableFieldChangeSignal, operation, "order", authenticatedUserId, context);
            }

            operation.Data.Remove("orderId");
        }

        operation.Data.Remove("updatedAt");

        var unknownUpdateFields = operation.Data.Keys
            .Where(k => !AllowedOrderFields.Contains(k) && !OrderUpdateEchoFields.Contains(k))
            .ToList();
        if (unknownUpdateFields.Count > 0)
        {
            return OrderValidationError(operation, $"Unknown fields: {string.Join(", ", unknownUpdateFields)}");
        }

        var immutableOrderFields = AllowedOrderFields.Where(f => !UpdatableOrderFields.Contains(f)).ToHashSet();
        var immutableError = RejectImmutableFieldChanges(
            operation.Data,
            immutableOrderFields,
            key => existingOrder.TryGetValue(key, out var stored) ? stored?.ToString() : null);
        if (immutableError != null)
        {
            return await RefuseWithSignalAsync(
                OrderValidationError(operation, immutableError),
                FraudDetectionService.ImmutableFieldChangeSignal, operation, "order", authenticatedUserId, context);
        }

        var updateValidationErrors = ValidateOrderFields(operation.Data, isCreate: false);
        if (updateValidationErrors.Count > 0)
        {
            return OrderValidationError(operation, string.Join(" ", updateValidationErrors));
        }

        // FRD-03: set by the accept below when its write must check the escrow in the same transaction.
        EscrowRowKey? acceptFence = null;

        // FRD-09: the payment the pattern rules observe once the payment_submitted write succeeded (the
        // amount and the buyer's row the KYC gate read).
        (decimal Total, UserDto? Buyer)? submittedPayment = null;

        // Validate status transition
        if (operation.Data.TryGetValue("status", out var newStatusObj))
        {
            var newStatus = newStatusObj?.ToString() ?? "";
            var currentStatus = existingOrder.TryGetValue("status", out var cs) ? cs?.ToString() ?? "" : "";

            if (!OrderTransitions.TryGetValue(currentStatus, out var allowedTransitions)
                || !allowedTransitions.Contains(newStatus))
            {
                // SIM-28: the loser of a status race is behind, not wrong, when it may ask for this
                // status at all (a system-managed one, or another party's, keeps the error).
                if (StaleClientVersion(operation, OrderVersionOf(existingOrder)) is int orderRaceVersion
                    && ValidateOrderTransitionAuth(currentStatus, newStatus, authenticatedUserId, orderBuyerId, orderFarmerId) == null)
                {
                    return OrderConflict(operation, existingOrder, orderRaceVersion);
                }

                return new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "error",
                    Message = $"ORDER_VALIDATION: Invalid status transition from '{currentStatus}' to '{newStatus}'."
                };
            }

            // ESC-15: cancel rules of a paid order. While the farmer and the trucker decide
            // (funded) nobody cancels; in awaiting_transport the buyer (full refund) or the
            // farmer (no penalty) may; an accepted order may be cancelled by its farmer before
            // pickup with a fresh sign-in. Each runs its own transactional path.
            if (newStatus == "cancelled")
            {
                var committedCancel = await RouteCommittedCancelAsync(
                    operation, existingOrder, currentStatus, authenticatedUserId, orderBuyerId, orderFarmerId, context);
                if (committedCancel != null)
                {
                    return committedCancel;
                }
            }

            // Authorization for specific transitions
            var transitionError = ValidateOrderTransitionAuth(
                currentStatus, newStatus, authenticatedUserId, orderBuyerId, orderFarmerId);
            if (transitionError != null)
            {
                return await RefuseWithSignalAsync(new SyncOperationResult
                {
                    OperationId = operation.OperationId,
                    Status = "error",
                    Message = transitionError
                }, FraudDetectionService.RoleForbiddenSignal, operation, "order", authenticatedUserId, context);
            }

            // ESC-06: an accepted order is cancelled only after a transport failure, with its own
            // checks and writes (the pending delivery first, then the order, then the refund).
            if (currentStatus == "accepted" && newStatus == "cancelled")
            {
                return await ProcessTransportFailureCancelAsync(operation, context);
            }

            // KYC and step-up gates on the buyer's payment. The order-funded escrow leg is
            // the real money path, so the checks /escrow/initiate makes (both parties
            // phone-verified, Fayda above 10,000 ETB) and the payment step-up (a fresh
            // password up to 10,000 ETB, AUTH_040; a fresh Fayda login above, AUTH_041)
            // run here, before any money moves. FRD-04: the buyer's risk tier comes first
            // (fraud-detection-architecture.md 17.1): CRITICAL pays nothing, MEDIUM and HIGH
            // add proofs to the step-up.
            if (newStatus == "payment_submitted")
            {
                // ESC-15: no money for a trip that can no longer carry the order (cancelled,
                // full, or leaving within 12 hours): the buyer places a new order instead.
                var tripGate = await CheckTripBookableForPaymentAsync(existingOrder, context);
                if (tripGate != null)
                {
                    return OrderValidationError(operation, tripGate);
                }

                var riskTier = await CallerRiskTierAsync(authenticatedUserId!);
                if (riskTier == RiskTier.Critical)
                {
                    return AccountHeldResult(operation);
                }

                // ACC-02 (account-deletion.md, counterparty money entry points): no money for an
                // order whose buyer, farmer or trucker is being or has been deleted. Part of the KYC
                // gate, read strongly consistent, and before the KYC rows so a deleted farmer is not
                // reported as an unverified phone.
                if (_userRepo != null
                    && await AccountDeletionGuard.AnyPartyDeletingAsync(_userRepo, orderBuyerId, orderFarmerId, orderTruckerId))
                {
                    return OrderValidationError(operation, AccountDeletionGuard.DeletedPartyMessage);
                }

                var orderTotal = ReadDecimal(existingOrder, "orderTotal");
                var (kycError, buyer) = await CheckPaymentKycAsync(authenticatedUserId!, orderFarmerId, orderTotal, context);
                if (kycError != null)
                {
                    return new SyncOperationResult
                    {
                        OperationId = operation.OperationId,
                        Status = "error",
                        Message = kycError
                    };
                }

                submittedPayment = (orderTotal, buyer);
                var stepUpError = StepUpPolicy.CheckPayment(
                    _requestAuthTime, buyer?.FaydaAuthenticatedAt, orderTotal, tier: riskTier);
                if (stepUpError != null)
                {
                    return new SyncOperationResult
                    {
                        OperationId = operation.OperationId,
                        Status = "error",
                        Message = stepUpError
                    };
                }

                // ESC-01: with the live gateway an order is paid by one Telebirr checkout, which
                // takes 100 to 500,000 ETB. Refuse up front rather than leave the order waiting
                // for a payment that can never be made (bank transfers are ESC-46).
                var roundedTotal = Math.Round(orderTotal, 2, MidpointRounding.AwayFromZero);
                if (!IsMockPaymentsEnabled()
                    && (roundedTotal < LiveMinPaymentEtb || roundedTotal > LiveMaxPaymentEtb))
                {
                    return PaymentTotalOutOfRange(operation,
                        $"This order total ({roundedTotal} ETB) is outside what one Telebirr payment can take "
                        + $"({LiveMinPaymentEtb} to {LiveMaxPaymentEtb} ETB).");
                }

                // FUP-20: with the live gateway only the Telebirr checkout funds an order. A bank
                // transfer (cbe_transfer, the demo rail) has no funding path until ESC-46 builds
                // CBE verification, so it is refused here, before the payment clock starts and
                // before anything records a submitted payment. Only the update's own value counts:
                // the stored one is a create-time default (the mobile create writes cbe_transfer),
                // so an update without a method is left to the checkout.
                if (!IsMockPaymentsEnabled()
                    && operation.Data.TryGetValue("paymentMethod", out var methodObj)
                    && methodObj?.ToString() is string paymentMethod
                    && !string.Equals(paymentMethod, LivePaymentMethod, StringComparison.Ordinal))
                {
                    var methodLabel = paymentMethod == "cbe_transfer"
                        ? "Bank transfer (cbe_transfer)"
                        : $"Payment method '{paymentMethod}'";
                    return PaymentMethodUnavailable(operation,
                        $"{methodLabel} is not available yet. Pay with Telebirr.");
                }
            }

            // SIM-60: pickup_confirmed and in_transit follow the delivery as well. The
            // pickup scan advances the order server-side; a client request is a catch-up
            // honoured only when the active delivery is already that far. There is no
            // legacy fallback here: an order without an active delivery cannot have been
            // picked up, so it is refused for every participant (buyer, farmer, trucker).
            if (newStatus is "pickup_confirmed" or "in_transit")
            {
                var followError = await CheckOrderFollowsDeliveryAsync(existingOrder, newStatus);
                if (followError != null)
                {
                    return OrderValidationError(operation, followError);
                }
            }

            // SIM-11: the order becomes "delivered" only once its delivery is confirmed.
            // The confirmation itself advances the order server-side, so a client request
            // is honoured only when the active delivery already says so. Orders with no
            // active delivery on record (created before 1.4.0) keep the old behaviour.
            if (newStatus == "delivered"
                && existingOrder.TryGetValue("activeDeliveryId", out var adObj)
                && adObj?.ToString() is string activeDeliveryId
                && !string.IsNullOrEmpty(activeDeliveryId))
            {
                // Strongly consistent (FUP-09): the catch-up usually comes right after the
                // delivery's confirm, which a lagging read may not show yet; a lagging read
                // could also still say delivered after the delivery was disputed.
                var activeDelivery = await GetDeliveryItemAsync(activeDeliveryId, consistentRead: true);
                var activeDeliveryStatus = activeDelivery != null
                    && activeDelivery.TryGetValue("status", out var adsAttr) ? adsAttr.S : null;
                if (activeDeliveryStatus != "delivered")
                {
                    return OrderValidationError(operation,
                        $"The delivery has not been confirmed yet (delivery status: {activeDeliveryStatus ?? "unknown"}); "
                        + "confirm the delivery instead of marking the order delivered.");
                }
            }

            // Add server-side timestamps for specific transitions
            var nowUtc = DateTime.UtcNow;
            var now = nowUtc.ToString("o");

            // FRD-03: a funded order whose escrow is held for review cannot be accepted: a delivery
            // created after it could never link to the held escrow. Reject and cancel stay allowed. An
            // escrow that cannot be read refuses the accept for now (transient, the client retries).
            if (newStatus == "accepted")
            {
                var (escrowStop, fence) = await CheckAcceptEscrowAsync(operation, context);
                if (escrowStop != null)
                {
                    return escrowStop;
                }

                acceptFence = fence;
            }

            // ESC-06: the farmer's accept starts the pickup clock in the same write. A trip that
            // cannot be read refuses the accept for now (transient, so the client retries): no
            // accepted order is left without its deadline.
            if (newStatus == "accepted")
            {
                var pickupDeadline = await ComputePickupDeadlineAsync(existingOrder, nowUtc, context);
                if (pickupDeadline == null)
                {
                    return TransientOrderError(operation, "The order's trip could not be read. Please try again later.");
                }

                operation.Data[PickupDeadline.Field] = PickupDeadline.Format(pickupDeadline.Value);
            }

            switch (newStatus)
            {
                case "cancelled":
                    operation.Data["cancelledAt"] = now;
                    break;
                case "accepted":
                case "rejected":
                    operation.Data["respondedAt"] = now;
                    break;
                case "funded":
                    operation.Data["paymentVerifiedAt"] = now;
                    break;
                case "payment_submitted":
                    // ESC-01: starts the payment clock. The checkout refuses new attempts
                    // after 20 hours and the order cron marks the order payment_failed after 24.
                    operation.Data["paymentSubmittedAt"] = now;
                    break;
                case "pending_payment":
                    // ESC-47: the order cron cancels an unpaid pending_payment order 24 hours after
                    // this, so a retry after payment_failed on an old order is not cancelled at once.
                    operation.Data["pendingPaymentAt"] = now;
                    break;
            }
        }

        KeepServerCancelCodesReserved(operation.Data);

        // SIM-08: an order update is never applied without the client's version. The old
        // fallback to the stored version skipped optimistic concurrency entirely, so a
        // stale client could overwrite a newer state (even a cancelled order).
        if (!TryGetClientVersion(operation, out var version))
        {
            return OrderValidationError(operation,
                "version is required for order updates (send clientVersion or data.version).");
        }

        operation.Data.Remove("version"); // consumed here; the repository handles the check

        // FUP-53: a funded order's reject or cancel and its refund arm are one transaction.
        var storedStatus = existingOrder.TryGetValue("status", out var storedStatusObj) ? storedStatusObj?.ToString() : null;
        var closingStatus = operation.Data.TryGetValue("status", out var closingStatusObj) ? closingStatusObj?.ToString() : null;
        Dictionary<string, object>? closedWithRefund = null;
        if (storedStatus == "funded" && closingStatus is "rejected" or "cancelled")
        {
            var close = await TryCloseOrderWithRefundAsync(
                operation, operation.Data, version, "funded", $"order_{closingStatus}", context);
            if (close.Stop != null)
            {
                return close.Stop;
            }

            closedWithRefund = close.Written;
        }

        var updateResult = closedWithRefund != null
            ? AuthResult<Dictionary<string, object>>.Success(closedWithRefund)
            : acceptFence != null
                ? await _orderRepo.UpdateUnlessEscrowHeldAsync(operation.EntityId, operation.Data, version, acceptFence)
                : await _orderRepo.UpdateAsync(operation.EntityId, operation.Data, version);

        // FRD-03: a sweep held the escrow between the check and the write; nothing was written.
        if (!updateResult.IsSuccess && updateResult.Error?.Code == OrderRepository.EscrowHeldCode)
        {
            return OrderValidationError(operation, OrderUnderReviewMessage);
        }

        if (updateResult.IsSuccess)
        {
            var appliedStatus = operation.Data.TryGetValue("status", out var statusVal)
                ? statusVal?.ToString()
                : null;

            // Audit the order transition (who/when/where) for the monitor.
            if (!string.IsNullOrEmpty(appliedStatus))
            {
                var priorStatus = existingOrder.TryGetValue("status", out var psObj)
                    ? psObj?.ToString()
                    : null;
                await EmitTxAuditAsync(context, operation.EntityId, "order", operation.EntityId,
                    OrderActionFor(appliedStatus), priorStatus, appliedStatus);

                // A funded order that gets rejected/cancelled holds the buyer's money
                // in escrow — schedule the refund (expiresAt=now) so the hourly
                // auto-refund cron pays it back through the standard gateway path
                // (order-lifecycle.md invariant 2). Only the funded→terminal hop
                // carries unreleased money; later statuses are governed by the
                // delivery-confirm/dispute machinery.
                if (priorStatus == "funded" && appliedStatus is "rejected" or "cancelled")
                {
                    // FUP-53: already armed with the order write, unless no record held the money then.
                    if (closedWithRefund == null)
                    {
                        await TryScheduleOrderEscrowRefundAsync(
                            operation.EntityId, $"order_{appliedStatus}", context);
                    }

                    // SIM-13: the goods go back on the market with the money going back
                    // to the buyer.
                    await TryRestoreListingStockAsync(existingOrder, context);
                }
            }

            // 6D: an order that becomes "funded" takes its stock. Clients cannot set the
            // system-managed status, so this is a backstop on the shared funding path: a funding
            // that lost the listing's stock is ended there with a full refund (ESC-18).
            if (appliedStatus == "funded")
            {
                await _orderFunding.EnsureFundedSideEffectsAsync(updateResult.Value ?? existingOrder, "system", _batchAudit, context);
            }

            var newVersion = version + 1;

            // FRD-09 (fraud-detection-architecture.md 15.1): the buyer payment rules, on the payment the
            // gates above let through. Flag-only and best effort: the answer below is the same either way.
            if (appliedStatus == "payment_submitted" && submittedPayment is { } payment)
            {
                await TryRecordPatternSignalsAsync(authenticatedUserId, operation.EntityId, "order", "payment",
                    rules => rules.CheckPaymentAsync(
                        authenticatedUserId!, operation.EntityId, payment.Total, payment.Buyer?.CreatedAt,
                        payment.Buyer?.FaydaVerified == true, DateTime.UtcNow),
                    context);
            }

            // Mock/demo payments have no real gateway to verify the transfer, so a
            // submitted payment is auto-verified server-side: advance
            // payment_submitted -> funded (clients are blocked from the
            // system-managed 'funded' state) so the farmer can act on the order.
            // Live/prod payments (TELEBIRR_MOCK unset) are funded by the real
            // gateway callback, never here.
            if (appliedStatus == "payment_submitted" && IsMockPaymentsEnabled())
            {
                var fundedData = await TryAutoFundMockOrderAsync(
                    operation.EntityId, newVersion, existingOrder, context);
                if (fundedData != null)
                {
                    // The funding is audited inside; a row ended for lack of stock (ESC-18) is one
                    // version further on.
                    var fundedVersion = OrderVersionOf(fundedData);
                    return new SyncOperationResult
                    {
                        OperationId = operation.OperationId,
                        Status = "success",
                        ServerVersion = fundedVersion > 0 ? fundedVersion : newVersion + 1,
                        ServerData = fundedData
                    };
                }
            }

            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "success",
                ServerVersion = newVersion,
                ServerData = updateResult.Value
            };
        }

        // Conflict. Read strongly consistent (FUP-21): the client adopts this row and retries
        // from its version, so it must be the row that beat the write, not an older one.
        var serverData = await _orderRepo.GetByIdAsync(operation.EntityId, consistentRead: true);
        var srvVersion = serverData != null && serverData.TryGetValue("version", out var svObj)
            ? int.TryParse(svObj.ToString(), out var parsed) ? parsed : version
            : version;

        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "conflict",
            ServerVersion = srvVersion,
            ServerData = serverData,
            Message = updateResult.Error?.Message ?? "Version conflict."
        };
    }

    /// <summary>Sync error prefix for a delivery dispute that was refused for good (ESC-07): nothing was written.</summary>
    internal const string DeliveryDisputeRefusedCode = "DELIVERY_DISPUTE_REFUSED";

    /// <summary>Sync error prefix for a delivery dispute whose freeze could not be decided (ESC-07): nothing was written, send it again.</summary>
    internal const string DeliveryDisputeRetryCode = "DELIVERY_DISPUTE_RETRY";

    /// <summary>The whole retry answer: the freeze (ESC-07) or the step-up's reads (FUP-26) could not be decided.</summary>
    private const string DeliveryDisputeRetryMessage =
        $"{DeliveryDisputeRetryCode}: The dispute could not be recorded right now. It will be sent again.";

    /// <summary>
    /// ESC-07: the freeze a delivery dispute needs before its delivery write. Returns the result to
    /// answer with instead of writing (a stale version, a refused or undecided freeze), or the
    /// freeze to mirror after the write. In order:
    /// <list type="bullet">
    /// <item>a strongly consistent read: a delivery already <c>disputed</c> goes to
    /// <see cref="RepeatDeliveryDisputeAsync"/>; anything else but <c>delivered</c> at the client's
    /// version is a <c>conflict</c> and nothing is frozen;</item>
    /// <item>the freeze (<see cref="EscrowService.FreezeForDeliveryDisputeAsync"/>): frozen or
    /// already disputed goes ahead; a manual payout, a settlement in flight, an escrow that holds
    /// no money, one linked to another delivery, and a delivery with an order but no escrow are
    /// refused (<c>DELIVERY_DISPUTE_REFUSED</c>, permanent);</item>
    /// <item>an exception, or no escrow service for a delivery with an order:
    /// <c>DELIVERY_DISPUTE_RETRY</c> (transient, nothing written).</item>
    /// </list>
    /// A legacy delivery without an order and without an escrow is disputed as before, with
    /// nothing to freeze.
    /// </summary>
    private async Task<(SyncOperationResult? Stop, DeliveryDisputeFreezeResult? Freeze)> FreezeBeforeDeliveryDisputeAsync(
        SyncOperationRequest operation, string? orderId, int clientVersion, string? disputedBy, string? reason,
        ILambdaContext context)
    {
        var deliveryId = operation.EntityId;
        var current = await GetDeliveryItemAsync(deliveryId, consistentRead: true);
        if (current == null)
        {
            return (DeliveryDisputeError(operation, "Delivery not found."), null);
        }

        // The row the checks above read may lag: one already disputed is finished, not written again
        // (that would overwrite disputeReason and audit a second dispute), and anything but
        // delivered at the client's version is a conflict.
        var currentStatus = current.TryGetValue("status", out var cs) ? cs.S : null;
        if (currentStatus == "disputed")
        {
            return (await RepeatDeliveryDisputeAsync(operation, current, disputedBy, context), null);
        }

        var currentVersion = current.TryGetValue("version", out var cv) && int.TryParse(cv.N, out var cvn) ? cvn : 0;
        if (currentStatus != "delivered" || currentVersion != clientVersion)
        {
            return (new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "conflict",
                ServerVersion = currentVersion,
                ServerData = ScrubDeliveryForClient(CropRepository.FromItem(current)),
                Message = $"Version conflict: server has version {currentVersion}, client expected {clientVersion}."
            }, null);
        }

        if (_orderEscrowService == null || string.IsNullOrEmpty(disputedBy))
        {
            if (orderId == null)
            {
                return (null, null);
            }

            context.Logger.LogError(
                $"Dispute on delivery {MoneyAlarmLog.LogId(deliveryId)} not recorded: no escrow service is configured.");
            return (DeliveryDisputeError(operation, DeliveryDisputeRetryMessage), null);
        }

        DeliveryDisputeFreezeResult freeze;
        try
        {
            freeze = await _orderEscrowService.FreezeForDeliveryDisputeAsync(deliveryId, orderId, disputedBy, reason);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Freeze for disputed delivery {MoneyAlarmLog.LogId(deliveryId)} undecided, nothing written: [{ex.GetType().Name}] {MoneyAlarmLog.LogText(ex.Message)}");
            return (DeliveryDisputeError(operation, DeliveryDisputeRetryMessage), null);
        }

        context.Logger.LogInformation(
            $"Dispute on delivery {MoneyAlarmLog.LogId(deliveryId)} by {MoneyAlarmLog.LogId(disputedBy)}: escrow {MoneyAlarmLog.LogId(freeze.EscrowId)} {freeze.Outcome}");

        // FUP-27: the escrow dispute-rate alarm counts this dispute with the escrow Lambda's. Only a
        // fresh freeze: an escrow already disputed was counted when it froze, and a refused dispute
        // is resent by older clients on every reconnect. Warning, so a raised log level keeps it.
        if (freeze.Outcome == DeliveryDisputeFreeze.Frozen)
        {
            context.Logger.LogWarning(MoneyAlarmLog.DeliveryDisputeFrozen(freeze.EscrowId, orderId, deliveryId));
        }

        var refusal = freeze.Outcome switch
        {
            DeliveryDisputeFreeze.Frozen or DeliveryDisputeFreeze.AlreadyDisputed => null,
            DeliveryDisputeFreeze.NoEscrow when orderId == null => null,
            DeliveryDisputeFreeze.NoEscrow =>
                "No payment is on record for this delivery's order, so the dispute was not recorded.",
            DeliveryDisputeFreeze.RefusedManualPayout =>
                "The payment for this delivery is already being paid out by an admin, so it can no longer be disputed.",
            DeliveryDisputeFreeze.RefusedSettlementInFlight =>
                "The payment for this delivery is being settled, so it can no longer be disputed.",
            DeliveryDisputeFreeze.RefusedOtherDelivery =>
                "The payment for this order belongs to another delivery, so this one cannot be disputed.",
            DeliveryDisputeFreeze.RefusedRefundScheduled =>
                "The payment for this delivery is being refunded to the buyer, so it can no longer be disputed.",
            _ => $"The payment for this delivery is not held in escrow (status: {freeze.EscrowStatus}), so it can no longer be disputed."
        };
        if (refusal == null)
        {
            return (null, freeze);
        }

        if (freeze.Outcome == DeliveryDisputeFreeze.NoEscrow)
        {
            context.Logger.LogError(MoneyAlarmLog.DisputeNoEscrow(orderId, deliveryId));
        }

        return (DeliveryDisputeError(operation, $"{DeliveryDisputeRefusedCode}: {refusal} Contact support."), null);
    }

    /// <summary>
    /// ESC-07: a dispute for a delivery that is already <c>disputed</c>. A participant's request
    /// finishes what an earlier dispute may have left undone (a Lambda that stopped after the
    /// delivery write never mirrored the order; a delivery disputed before ESC-07 never froze its
    /// escrow): the freeze, then the order mirror when the escrow is frozen. Nothing is written to
    /// the delivery; the answer is <c>conflict</c> with the row, which the client takes as is.
    /// </summary>
    private async Task<SyncOperationResult> RepeatDeliveryDisputeAsync(
        SyncOperationRequest operation, Dictionary<string, AttributeValue> delivery, string? userId,
        ILambdaContext context)
    {
        string? Field(string name) => delivery.TryGetValue(name, out var attr) && !string.IsNullOrEmpty(attr.S) ? attr.S : null;
        if (string.IsNullOrEmpty(userId)
            || (userId != Field("buyerId") && userId != Field("farmerId") && userId != Field("truckerId")))
        {
            return await RefuseWithSignalAsync(
                DeliveryDisputeError(operation, "DELIVERY_UNAUTHORIZED: Only participants can dispute a delivery."),
                FraudDetectionService.RoleForbiddenSignal, operation, "delivery", userId, context);
        }

        var deliveryId = operation.EntityId;
        var orderId = Field("orderId");
        if (_orderEscrowService != null)
        {
            DeliveryDisputeFreezeResult freeze;
            try
            {
                freeze = await _orderEscrowService.FreezeForDeliveryDisputeAsync(
                    deliveryId, orderId, userId, Field("disputeReason"));
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning(
                    $"Freeze for disputed delivery {MoneyAlarmLog.LogId(deliveryId)} undecided on a repeat: [{ex.GetType().Name}] {MoneyAlarmLog.LogText(ex.Message)}");
                return DeliveryDisputeError(operation, DeliveryDisputeRetryMessage);
            }

            context.Logger.LogInformation(
                $"Repeated dispute on delivery {MoneyAlarmLog.LogId(deliveryId)}: escrow {MoneyAlarmLog.LogId(freeze.EscrowId)} {freeze.Outcome}");
            if (freeze.MayMirror)
            {
                await TryMirrorDeliveryDisputeAsync(freeze, orderId, deliveryId, userId, null, markDelivery: false, context);
            }
        }

        var row = await GetDeliveryItemAsync(deliveryId, consistentRead: true) ?? delivery;
        var version = row.TryGetValue("version", out var v) && int.TryParse(v.N, out var vn) ? vn : 0;
        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "conflict",
            ServerVersion = version,
            ServerData = ScrubDeliveryForClient(CropRepository.FromItem(row)),
            Message = "The delivery is already disputed."
        };
    }

    private static SyncOperationResult DeliveryDisputeError(SyncOperationRequest operation, string message) => new()
    {
        OperationId = operation.OperationId,
        Status = "error",
        Message = message
    };

    /// <summary>
    /// ESC-07: move the order (and with <paramref name="markDelivery"/> the delivery) behind a
    /// frozen delivery dispute, through <see cref="EscrowService.MirrorDisputeAsync"/>. The money
    /// is already frozen, so a failure is logged and never fails the delivery update.
    /// </summary>
    private async Task TryMirrorDeliveryDisputeAsync(
        DeliveryDisputeFreezeResult freeze, string? orderId, string deliveryId, string? disputedBy, string? reason,
        bool markDelivery, ILambdaContext context)
    {
        if (_orderEscrowService == null || string.IsNullOrEmpty(freeze.EscrowId))
        {
            return;
        }

        try
        {
            await _orderEscrowService.MirrorDisputeAsync(
                freeze.EscrowId, orderId, deliveryId, disputedBy ?? EscrowService.SystemActor, reason, markDelivery);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Dispute mirror for delivery {MoneyAlarmLog.LogId(deliveryId)} failed: [{ex.GetType().Name}] {MoneyAlarmLog.LogText(ex.Message)}");
        }
    }

    /// <summary>
    /// Delivery statuses that already imply each client-settable logistics order status
    /// (SIM-60): pickup_confirmed needs a picked-up delivery, in_transit one in transit.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> DeliveryStatusesImplyingOrderStatus = new()
    {
        { "pickup_confirmed", new HashSet<string> { "pickedUp", "inTransit", "delivered" } },
        { "in_transit", new HashSet<string> { "inTransit", "delivered" } },
    };

    /// <summary>
    /// SIM-60: a client may set pickup_confirmed or in_transit only once the order's active
    /// delivery is already at least that far. Returns the error text without the code
    /// prefix, or null when the transition is allowed.
    /// </summary>
    private async Task<string?> CheckOrderFollowsDeliveryAsync(
        Dictionary<string, object> existingOrder, string newStatus)
    {
        var activeDeliveryId = existingOrder.TryGetValue("activeDeliveryId", out var adObj)
            ? adObj?.ToString()
            : null;
        if (string.IsNullOrEmpty(activeDeliveryId))
        {
            return $"The order has no active delivery; '{newStatus}' is set when the delivery is picked up.";
        }

        // Strongly consistent: the catch-up usually comes right after the delivery's own
        // transition, which an eventually consistent read may not show yet.
        var delivery = await GetDeliveryItemAsync(activeDeliveryId, consistentRead: true);
        var deliveryStatus = delivery != null && delivery.TryGetValue("status", out var dsAttr) ? dsAttr.S : null;
        if (deliveryStatus != null && DeliveryStatusesImplyingOrderStatus[newStatus].Contains(deliveryStatus))
        {
            return null;
        }

        var step = newStatus == "pickup_confirmed" ? "picked up" : "in transit";
        return $"The delivery is not {step} yet (delivery status: {deliveryStatus ?? "unknown"}); "
               + "the order follows its delivery.";
    }

    /// <summary>Order logistics statuses in progression order (SIM-11).</summary>
    private static readonly string[] OrderLogisticsProgression =
    {
        "accepted", "pickup_confirmed", "in_transit", "delivered"
    };

    /// <summary>
    /// SIM-11: advance the linked order to mirror a delivery transition. The order only
    /// ever moves forward along accepted -> pickup_confirmed -> in_transit -> delivered;
    /// a self-pickup handoff or a pickedUp -> delivered confirmation jumps straight to
    /// delivered. Best-effort: a failure is logged and never fails the delivery update.
    /// Audited as a system actor so the monitor shows who really moved the order. A disputed
    /// delivery's order follows the escrow's dispute mirror instead (ESC-07).
    /// </summary>
    private async Task TryAdvanceOrderForDeliveryAsync(
        string orderId, string deliveryStatus, ILambdaContext context)
    {
        var target = deliveryStatus switch
        {
            "pickedUp" => "pickup_confirmed",
            "inTransit" => "in_transit",
            "delivered" => "delivered",
            _ => null
        };
        if (target == null)
        {
            return;
        }

        try
        {
            var order = await _orderRepo.GetByIdAsync(orderId);
            if (order == null)
            {
                return;
            }

            var current = order.TryGetValue("status", out var cs) ? cs?.ToString() ?? "" : "";
            var currentRank = Array.IndexOf(OrderLogisticsProgression, current);
            var targetRank = Array.IndexOf(OrderLogisticsProgression, target);
            if (currentRank < 0 || currentRank >= targetRank)
            {
                // Outside the logistics run (disputed, cancelled, ...) or already at/after
                // the target: never move an order backwards or out of a terminal state.
                return;
            }

            var version = 1;
            if (order.TryGetValue("version", out var v))
            {
                int.TryParse(v?.ToString(), out version);
            }

            var result = await _orderRepo.UpdateAsync(
                orderId, new Dictionary<string, object> { { "status", target } }, version);
            if (!result.IsSuccess)
            {
                context.Logger.LogWarning(
                    $"Order {MoneyAlarmLog.LogId(orderId)} not advanced to {target} from delivery {MoneyAlarmLog.LogId(deliveryStatus)}: {MoneyAlarmLog.LogText(result.Error?.Message)}");
                return;
            }

            await EmitTxAuditAsync(context, orderId, "order", orderId,
                OrderActionFor(target), current, target, ctxOverride: AuditContext.ForSystem());
            context.Logger.LogInformation($"Order {MoneyAlarmLog.LogId(orderId)}: {current} -> {target} (delivery {MoneyAlarmLog.LogId(deliveryStatus)})");
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Failed to advance order {MoneyAlarmLog.LogId(orderId)} for delivery {MoneyAlarmLog.LogId(deliveryStatus)}: [{ex.GetType().Name}] {MoneyAlarmLog.LogText(ex.Message)}");
        }
    }

    /// <summary>
    /// SIM-13: give the listing its stock back when a funded order dies (rejected or
    /// cancelled). Idempotency per order and the race fence live in the repository
    /// (version CAS plus the listing's set of restored order ids). Best-effort, like
    /// the decrement: a failure is logged and never fails the order update.
    /// </summary>
    private async Task TryRestoreListingStockAsync(Dictionary<string, object> order, ILambdaContext context)
    {
        try
        {
            var listingId = order.TryGetValue("listingId", out var lid) ? lid?.ToString() : null;
            var orderId = order.TryGetValue("orderId", out var oid) ? oid?.ToString() : null;
            var quintals = ParseDouble(order, "quantity") / KgPerQuintal;
            if (string.IsNullOrEmpty(listingId) || string.IsNullOrEmpty(orderId) || quintals <= 0)
            {
                return;
            }

            var restored = await _cropRepo.TryRestoreStockAsync(listingId, orderId, quintals);
            context.Logger.LogInformation(restored
                ? $"Listing {MoneyAlarmLog.LogId(listingId)}: +{quintals} quintals restored (order {MoneyAlarmLog.LogId(orderId)} died)"
                : $"Listing {MoneyAlarmLog.LogId(listingId)}: nothing to restore for order {MoneyAlarmLog.LogId(orderId)} (already restored or listing gone)");
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Failed to restore listing stock for order: [{ex.GetType().Name}] {MoneyAlarmLog.LogText(ex.Message)}");
        }
    }

    /// <summary>
    /// Whether the environment runs mocked payments (no real Telebirr gateway).
    /// Set by infra for dev/staging (TELEBIRR_MOCK=true); never set in prod or
    /// when the live gateway is enabled.
    /// </summary>
    private static bool IsMockPaymentsEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable("TELEBIRR_MOCK"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Auto-verify a mocked payment by advancing the order payment_submitted ->
    /// funded server-side (the client cannot set the system-managed 'funded'
    /// state). Mirrors the real funded path: stamps paymentVerifiedAt and
    /// decreases the listing quantity. Best-effort — on failure the caller keeps
    /// the successful payment_submitted result rather than erroring the payment.
    /// </summary>
    private async Task<Dictionary<string, object>?> TryAutoFundMockOrderAsync(
        string orderId, int expectedVersion,
        Dictionary<string, object> existingOrder, ILambdaContext context)
    {
        try
        {
            var fundedAt = DateTime.UtcNow;
            var fundData = new Dictionary<string, object>
            {
                { "status", "funded" },
                { "paymentVerifiedAt", fundedAt.ToString("o") }
            };

            // ESC-15: the farmer's request and its deadline ride on the funding write (empty
            // when the feature is off), exactly as on the live webhook path.
            foreach (var (key, value) in await _orderFunding.FundingStampsAsync(existingOrder, fundedAt))
            {
                fundData[key] = value;
            }

            var result = await _orderRepo.UpdateAsync(orderId, fundData, expectedVersion);
            if (!result.IsSuccess)
            {
                context.Logger.LogWarning(
                    $"Mock auto-fund not applied for order {MoneyAlarmLog.LogId(orderId)}: {MoneyAlarmLog.LogText(result.Error?.Message)}");
                return null;
            }

            // System-driven follow-on transition, recorded as a system actor before its side
            // effects, so an out-of-stock end (ESC-18) follows it in the order's trail.
            await EmitTxAuditAsync(context, orderId, "order", orderId,
                "payment_verified", "payment_submitted", "funded",
                ctxOverride: AuditContext.ForSystem());

            // Same side effects as a live Telebirr payment (ESC-01): stock off the listing
            // and the order escrow, both idempotent per order. ESC-18: a funding that lost the
            // listing's stock ends the order with a full refund; the buyer gets that row back.
            var effects = await _orderFunding.EnsureFundedSideEffectsAsync(result.Value!, "system", _batchAudit, context);
            context.Logger.LogInformation($"Order {MoneyAlarmLog.LogId(orderId)} auto-funded (mock payments).");
            return effects.OutOfStockOrder ?? result.Value;
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Mock auto-fund failed for order {MoneyAlarmLog.LogId(orderId)}: [{ex.GetType().Name}]");
            return null;
        }
    }

    /// <summary>
    /// Best-effort refund scheduling for a rejected/cancelled funded order: expires
    /// the order's escrow so the hourly auto-refund cron pays the buyer back. A
    /// failure here is money stuck in escrow — alarmable token, needs reconciliation.
    /// </summary>
    private async Task TryScheduleOrderEscrowRefundAsync(
        string orderId, string reason, ILambdaContext context)
    {
        if (_orderEscrowService == null)
        {
            return;
        }

        try
        {
            var scheduled = await _orderEscrowService.ScheduleRefundForOrderAsync(
                orderId, reason, "system", _batchAudit);
            context.Logger.LogInformation(scheduled
                ? $"Order escrow refund scheduled: orderId={MoneyAlarmLog.LogId(orderId)} reason={reason}"
                : $"No refundable order escrow for orderId={MoneyAlarmLog.LogId(orderId)} ({reason}): "
                  + "either none exists or delivery already confirmed.");
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"ORDER_ESCROW_REFUND_SCHEDULE_FAILED orderId={MoneyAlarmLog.LogId(orderId)} reason=[{ex.GetType().Name}]");
        }
    }

    /// <summary>FUP-53: plans per funded order close (an escrow can change between the plan and the write).</summary>
    private const int FundedCloseAttempts = 3;

    /// <summary>
    /// FUP-53: the reject or cancel of a funded order, written in one transaction with the refund arm
    /// on its escrow (<see cref="EscrowService.PlanOrderRefundAsync"/>,
    /// <see cref="EscrowService.CloseOrderWithRefundAsync"/>), so no dispute can freeze the money
    /// between the order write and the arm. Returns the order as written, or the answer to stop with:
    /// <c>ORDER_CANCEL_NOT_ALLOWED: The payment is being settled or reviewed.</c> when a dispute froze
    /// the money or a settlement owns it (nothing written), a conflict when the order moved, a
    /// transient error when a read or the write failed or the escrow kept changing. Both null when no
    /// record holds the money (or no escrow service is wired): the caller writes the order as before
    /// and schedules the refund after, best-effort.
    /// </summary>
    private async Task<(SyncOperationResult? Stop, Dictionary<string, object>? Written)> TryCloseOrderWithRefundAsync(
        SyncOperationRequest operation, Dictionary<string, object> fields, int version, string expectedStatus,
        string reason, ILambdaContext context)
    {
        if (_orderEscrowService == null)
        {
            return (null, null);
        }

        var orderId = operation.EntityId;
        for (var attempt = 1; attempt <= FundedCloseAttempts; attempt++)
        {
            OrderRefundPlan plan;
            try
            {
                plan = await _orderEscrowService.PlanOrderRefundAsync(orderId);
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning(
                    $"Close of funded order {MoneyAlarmLog.LogId(orderId)}: its escrow could not be read: [{ex.GetType().Name}]");
                return (TransientOrderError(operation, "The order's payment could not be read. Please try again later."), null);
            }

            if (plan.Readiness == OrderRefundReadiness.Busy)
            {
                context.Logger.LogInformation(
                    $"Close of funded order {MoneyAlarmLog.LogId(orderId)} refused ({reason}): its escrow is disputed or being settled.");
                return (CancelNotAllowed(operation, "The payment is being settled or reviewed."), null);
            }

            if (plan.Readiness == OrderRefundReadiness.Nothing)
            {
                return (null, null);
            }

            OrderLinkedWriteResult write;
            try
            {
                write = await _orderEscrowService.CloseOrderWithRefundAsync(
                    orderId, fields, version, expectedStatus, plan, reason, "system", _batchAudit);
            }
            catch (Exception ex)
            {
                // Nothing was written (a conflicting transaction, throttling): the client sends it again.
                context.Logger.LogWarning(
                    $"Close of funded order {MoneyAlarmLog.LogId(orderId)}: the write failed: [{ex.GetType().Name}]");
                return (TransientOrderError(operation, "The order could not be updated right now. Please try again later."), null);
            }

            switch (write.Outcome)
            {
                case OrderLinkedWriteOutcome.Written:
                    context.Logger.LogInformation(plan.Held.Count > 0
                        ? $"Order escrow refund scheduled: orderId={MoneyAlarmLog.LogId(orderId)} reason={reason}"
                        : $"No refundable order escrow for orderId={MoneyAlarmLog.LogId(orderId)} ({reason}): held for review.");
                    return (null, write.Order);

                case OrderLinkedWriteOutcome.OrderNotFound:
                    return (TransientOrderError(operation, "Order not found."), null);

                case OrderLinkedWriteOutcome.OrderChanged:
                    {
                        var latest = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
                        return (latest == null
                            ? TransientOrderError(operation, "Order not found.")
                            : OrderConflict(operation, latest, version), null);
                    }
            }

            // LinkedItemRefused: an escrow changed after the plan (a dispute, a hold, a hold released). Plan again.
        }

        return (TransientOrderError(operation, "The order's payment changed while the order was updated. Please try again later."), null);
    }

    /// <summary>
    /// ESC-06: moves a client <c>cancelReason</c> that spells a server code
    /// (<see cref="OrderCommitment.ServerCancelReasons"/>) or the unpaid cron's sentence to
    /// <c>cancelNote</c>, so no cancel can pass for a server decision.
    /// </summary>
    private static void KeepServerCancelCodesReserved(Dictionary<string, object> data)
    {
        if (data.TryGetValue("cancelReason", out var raw)
            && raw?.ToString()?.Trim() is { Length: > 0 } text
            && (OrderCommitment.ServerCancelReasons.Contains(text)
                || text.StartsWith(OrderCommitment.LegacyAutoCancelPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            data.Remove("cancelReason");
            data["cancelNote"] = text;
        }
    }

    /// <summary>ESC-06: order cancel writes per transport-failure cancel (a concurrent write can move the version).</summary>
    private const int TransportCancelOrderAttempts = 3;

    /// <summary>ESC-06: the refund reason stamped on the escrow of a transport-failure cancel.</summary>
    private const string TransportCancelRefundReason = "order_transport_failed";

    /// <summary>Delivery statuses that mean nothing left the farm yet (no row counts as none).</summary>
    private static bool IsBeforePickup(string? deliveryStatus) => deliveryStatus is null or "pending" or "cancelled";

    /// <summary>
    /// ESC-06: the buyer cancels an <c>accepted</c> order nobody picked up by its
    /// <c>pickupDeadlineAt</c>, for a full refund (order-lifecycle.md 1.15.0). Decided before any
    /// write: the client's version against a strongly consistent read (a stale command only gets the
    /// server's row back), the deadline on the server clock, the active delivery (nothing picked up)
    /// and the order escrow (plain <c>escrowed</c>; a missing one is created first, as delivery
    /// linkage does). Each refusal is <c>ORDER_CANCEL_NOT_ALLOWED</c>; a read or write that fails
    /// answers an unprefixed error, so the client retries. Then, in the dispute verdict's order so a
    /// pickup racing in leaves the order alone: the pending delivery is cancelled, the order is
    /// cancelled (<c>cancelReason: transport_failed_buyer_cancelled</c>, the buyer's text in
    /// <c>cancelNote</c>; a version conflict re-reads and retries while the order is still
    /// <c>accepted</c>) in one transaction with the escrow refund armed for the hourly sweep (FUP-32:
    /// a dispute that froze the money since the check refuses the cancel), then the delivery slot is
    /// released and the listing stock restored.
    /// </summary>
    private async Task<SyncOperationResult> ProcessTransportFailureCancelAsync(
        SyncOperationRequest operation, ILambdaContext context, CommittedCancelSpec? spec = null)
    {
        // ESC-15: the same transactional path serves the buyer's or farmer's cancel in
        // awaiting_transport and the farmer's cancel of a committed order; only the expected
        // status, the reason codes and the deadline check differ.
        spec ??= CommittedCancelSpec.BuyerAfterPickupDeadline;

        if (!TryGetClientVersion(operation, out var clientVersion))
        {
            return OrderValidationError(operation,
                "version is required for order updates (send clientVersion or data.version).");
        }

        var orderId = operation.EntityId;
        Dictionary<string, object>? order;
        try
        {
            order = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
        }
        catch (Exception ex)
        {
            return TransportCancelUnavailable(operation, context, orderId, "order", ex);
        }

        if (order == null)
        {
            return TransientOrderError(operation, "Order not found.");
        }

        if (OrderVersionOf(order) != clientVersion || ItemString(order, "status") != spec.ExpectedStatus)
        {
            return OrderConflict(operation, order, clientVersion);
        }

        var nowUtc = DateTime.UtcNow;
        if (spec.CheckPickupDeadline)
        {
            DateTime? deadline = PickupDeadline.TryParse(ItemString(order, PickupDeadline.Field), out var stamped)
                ? stamped
                : null;
            if (deadline == null)
            {
                // Accepted before ESC-06: the same rule, from the live trip and the acceptance time.
                var acceptedAt = PickupDeadline.AcceptedAt(order);
                deadline = acceptedAt == null ? null : await ComputePickupDeadlineAsync(order, acceptedAt.Value, context);
                if (deadline == null)
                {
                    return TransientOrderError(operation, "The order's trip could not be read. Please try again later.");
                }
            }

            if (nowUtc < deadline.Value)
            {
                return CancelNotAllowed(operation,
                    $"The crops can still be collected until {PickupDeadline.Format(deadline.Value)}.");
            }

            // ESC-34: with pickupClassification on, the pickup judgement decides an order accepted under
            // it (a trucker no-show reaches awaiting_transport, where the buyer cancels; a farmer no-show
            // is refunded; the admin queue holds the money), so the buyer's deadline cancel is not taken.
            if (PickupJudgementOn && PickupJudgement.IsStamped(order))
            {
                return CancelNotAllowed(operation,
                    "The pickup is judged from the arrival check-ins; you will be told the outcome.");
            }
        }

        // The order's deliveries decide whether anything was picked up (the order can lag them).
        var activeDeliveryId = ItemString(order, "activeDeliveryId");
        List<string> pendingDeliveryIds;
        try
        {
            var deliveries = await ReadOrderDeliveriesAsync(order, activeDeliveryId);
            if (deliveries.PickedUp)
            {
                return CancelNotAllowed(operation, "The crops were already picked up.");
            }

            pendingDeliveryIds = deliveries.Pending;
        }
        catch (Exception ex)
        {
            return TransportCancelUnavailable(operation, context, orderId, "delivery", ex);
        }

        if (_orderEscrowService == null)
        {
            return TransientOrderError(operation, "Refunds are unavailable right now. Please try again later.");
        }

        OrderEscrowCancelPlan plan;
        try
        {
            plan = await _orderEscrowService.PrepareOrderEscrowForCancelAsync(order, "system", _batchAudit);
        }
        catch (Exception ex)
        {
            return TransportCancelUnavailable(operation, context, orderId, "escrow", ex);
        }

        switch (plan.Readiness)
        {
            case OrderEscrowCancelReadiness.Busy:
                return CancelNotAllowed(operation, "The payment is being settled or reviewed.");
            case OrderEscrowCancelReadiness.Unavailable:
                return TransientOrderError(operation,
                    "The payment could not be prepared for a refund. Please try again later.");
        }

        // The pending deliveries first: once one is cancelled no pickup can land on it.
        string? cancelledDeliveryId = null;
        foreach (var pendingId in pendingDeliveryIds)
        {
            try
            {
                if (await _deliveryRepo.TryCancelPendingAsync(pendingId))
                {
                    await EmitTxAuditAsync(context, orderId, "delivery", pendingId,
                        "delivery_cancelled", "pending", "cancelled", metadata: $"reason={spec.CancelReason}");
                }
                else
                {
                    var latest = await GetDeliveryItemAsync(pendingId, consistentRead: true);
                    var latestStatus = latest == null ? null : DeliveryStatusOf(latest);
                    if (latestStatus == "pending")
                    {
                        return TransientOrderError(operation,
                            "The delivery could not be cancelled right now. Please try again later.");
                    }

                    if (!IsBeforePickup(latestStatus))
                    {
                        return CancelNotAllowed(operation, "The crops were already picked up.");
                    }
                }
            }
            catch (Exception ex)
            {
                return TransportCancelUnavailable(operation, context, orderId, "delivery", ex);
            }

            if (pendingId == activeDeliveryId)
            {
                cancelledDeliveryId = pendingId;
            }
        }

        var cancelFields = new Dictionary<string, object>
        {
            { "status", "cancelled" },
            { "cancelledAt", nowUtc.ToString("o") },
            { "cancelReason", spec.CancelReason },
            { OrderCommitment.CancelledByField, spec.CancelledBy },
            // A null spec fault keeps what the transport failure recorded (trucker, or none for a full trip).
            {
                OrderCommitment.FaultPartyField,
                spec.FaultParty ?? (ItemString(order, OrderCommitment.FaultPartyField) is { Length: > 0 } recorded
                    ? recorded
                    : OrderCommitment.Party.None)
            }
        };
        var note = operation.Data!.TryGetValue("cancelReason", out var noteObj) ? noteObj?.ToString()?.Trim() : null;
        if (!string.IsNullOrEmpty(note))
        {
            cancelFields["cancelNote"] = note;
        }

        var version = clientVersion;
        Dictionary<string, object>? cancelled;
        for (var attempt = 1; ; attempt++)
        {
            Dictionary<string, object>? latest;
            try
            {
                // FUP-32: the order cancel and the refund arm are one transaction, so a dispute that
                // froze the money after the check above can never leave a cancelled order over it.
                var write = await _orderEscrowService.CancelOrderWithRefundAsync(
                    orderId, cancelFields, version, spec.ExpectedStatus, plan.Held, spec.RefundReason, "system", _batchAudit);
                if (write.Outcome == OrderLinkedWriteOutcome.Written)
                {
                    cancelled = write.Order;
                    break;
                }

                if (write.Outcome == OrderLinkedWriteOutcome.LinkedItemRefused)
                {
                    // Nothing was written: the order stays accepted and the money where the dispute
                    // or settlement put it. The pending delivery stays cancelled, as on every refusal
                    // after it, and a replacement can be created.
                    context.Logger.LogInformation(
                        $"Transport cancel of order {MoneyAlarmLog.LogId(orderId)} refused: its escrow changed after the check.");
                    return CancelNotAllowed(operation, "The payment is being settled or reviewed.");
                }

                if (write.Outcome != OrderLinkedWriteOutcome.OrderChanged || attempt >= TransportCancelOrderAttempts)
                {
                    return TransientOrderError(operation, "The order could not be cancelled right now. Please try again later.");
                }

                // A system write moved the version after the delivery was cancelled: the buyer's
                // cancel still stands while the order is accepted.
                latest = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
            }
            catch (Exception ex)
            {
                return TransportCancelUnavailable(operation, context, orderId, "order", ex);
            }

            if (latest == null)
            {
                return TransientOrderError(operation, "Order not found.");
            }

            if (ItemString(latest, "status") != spec.ExpectedStatus)
            {
                return OrderConflict(operation, latest, clientVersion);
            }

            // A delivery that took the slot meanwhile must not have been picked up; a pending one is
            // cancelled with the slot below.
            var latestHolder = ItemString(latest, "activeDeliveryId");
            if (latestHolder.Length > 0 && latestHolder != activeDeliveryId)
            {
                Dictionary<string, AttributeValue>? holder;
                try
                {
                    holder = await GetDeliveryItemAsync(latestHolder, consistentRead: true);
                }
                catch (Exception ex)
                {
                    return TransportCancelUnavailable(operation, context, orderId, "delivery", ex);
                }

                if (!IsBeforePickup(holder == null ? null : DeliveryStatusOf(holder)))
                {
                    return CancelNotAllowed(operation, "The crops were already picked up.");
                }

                activeDeliveryId = latestHolder;
            }

            version = OrderVersionOf(latest);
        }

        await EmitTxAuditAsync(context, orderId, "order", orderId, "order_cancelled", spec.ExpectedStatus, "cancelled",
            metadata: $"cancelReason={spec.CancelReason};cancelledBy={spec.CancelledBy}");
        context.Logger.LogInformation(
            $"Order escrow refund scheduled: orderId={MoneyAlarmLog.LogId(orderId)} reason={spec.RefundReason}");
        await ReleaseTransportCancelSlotAsync(orderId, cancelledDeliveryId, context);
        await TryRestoreListingStockAsync(order, context);
        await TryReleaseBookingAsync(order, context);

        // ESC-48: the parties could reach each other since the farmer accepted, and this cancel
        // refunds the buyer in full, which is also how a trade agreed off the platform would end.
        // Best-effort signal for the admin fraud review; never a phone number in it. Only the
        // buyer's cancel is a signal: a farmer backing out costs the farmer, not the buyer.
        if (spec.CancelledBy == OrderCommitment.Party.Buyer && ContactReveal.IsOpen(order))
        {
            await TryRecordFraudSignalAsync(
                ItemString(order, "buyerId"), ContactReveal.TransportCancelSignal, orderId, "order",
                $"Buyer cancelled after a transport failure while contact with farmer {ItemString(order, "farmerId")} was open.",
                context);
        }

        // FRD-09: the farmer backed out of a funded order they had accepted (at fault). The other committed
        // cancels follow a transport failure the canceller did not cause, so they are not this rule's.
        if (spec.CancelledBy == OrderCommitment.Party.Farmer && spec.FaultParty == OrderCommitment.Party.Farmer)
        {
            var farmerId = ItemString(order, "farmerId");
            await TryRecordPatternSignalsAsync(farmerId, orderId, "order", "cancel_after_fund",
                rules => rules.CheckCancelAfterFundAsync(
                    farmerId, TransactionPatternService.Farmer, new[] { orderId }, DateTime.UtcNow),
                context);
        }

        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "success",
            ServerVersion = version + 1,
            ServerData = cancelled
        };
    }

    /// <summary>
    /// ESC-06: free the cancelled order's delivery slot, then read the slot again. A pending delivery
    /// created between the delivery cancel and the order cancel is cancelled too. Any other live
    /// holder (picked up in that window, or unreadable) means goods may have left the farm on a
    /// cancelled order and is logged with the alarmed <c>ORDER_TRANSPORT_CANCEL_LATE_DELIVERY</c>
    /// token. Never throws: the order is cancelled either way.
    /// </summary>
    private async Task ReleaseTransportCancelSlotAsync(string orderId, string? cancelledDeliveryId, ILambdaContext context)
    {
        string? holderId = null;
        try
        {
            if (!string.IsNullOrEmpty(cancelledDeliveryId))
            {
                await _orderRepo.ReleaseDeliverySlotAsync(orderId, cancelledDeliveryId);
            }

            var order = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
            holderId = order == null ? null : ItemString(order, "activeDeliveryId");
            if (string.IsNullOrEmpty(holderId) || holderId == cancelledDeliveryId)
            {
                return;
            }

            if (await _deliveryRepo.TryCancelPendingAsync(holderId))
            {
                await EmitTxAuditAsync(context, orderId, "delivery", holderId,
                    "delivery_cancelled", "pending", "cancelled", metadata: $"reason={PickupDeadline.CancelReason}");
                await _orderRepo.ReleaseDeliverySlotAsync(orderId, holderId);
                context.Logger.LogWarning(
                    $"Transport cancel of order {MoneyAlarmLog.LogId(orderId)} also cancelled pending delivery "
                    + $"{MoneyAlarmLog.LogId(holderId)}, created while the order was being cancelled.");
                return;
            }

            var holder = await GetDeliveryItemAsync(holderId, consistentRead: true);
            var holderStatus = holder == null ? null : DeliveryStatusOf(holder);
            if (holderStatus is null or "cancelled")
            {
                // A dead holder whose own slot release was lost: free it.
                await _orderRepo.ReleaseDeliverySlotAsync(orderId, holderId);
                return;
            }

            context.Logger.LogError(
                $"ORDER_TRANSPORT_CANCEL_LATE_DELIVERY orderId={MoneyAlarmLog.LogId(orderId)} "
                + $"deliveryId={MoneyAlarmLog.LogId(holderId)} status={MoneyAlarmLog.LogId(holderStatus)}");
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"ORDER_TRANSPORT_CANCEL_LATE_DELIVERY orderId={MoneyAlarmLog.LogId(orderId)} "
                + $"deliveryId={MoneyAlarmLog.LogId(holderId ?? cancelledDeliveryId)} status=unknown [{ex.GetType().Name}]");
        }
    }

    /// <summary>
    /// ESC-06, the reverse offline race: a pickup (or a self-pickup scan) that reaches a delivery the
    /// buyer's transport-failure cancel already cancelled means the goods may have left the farm on an
    /// order being refunded. Logs the alarmed <c>ORDER_TRANSPORT_CANCEL_PICKUP_REFUSED</c> token (ids
    /// defanged); the refusal itself is unchanged. Only the party who could have made that step counts
    /// (the delivery's trucker for <c>pickedUp</c> and <c>inTransit</c>, its buyer for <c>delivered</c>),
    /// so nobody else can page the alarm. Best-effort: never throws.
    /// </summary>
    private async Task AlarmPickupOnTransportCancelledDeliveryAsync(
        SyncOperationRequest operation, Dictionary<string, AttributeValue> delivery, string? authenticatedUserId,
        ILambdaContext context)
    {
        var requested = operation.Data != null && operation.Data.TryGetValue("status", out var statusObj)
            ? statusObj?.ToString()
            : null;
        var party = requested switch
        {
            "pickedUp" or "inTransit" => AttrString(delivery, "truckerId"),
            "delivered" => AttrString(delivery, "buyerId"),
            _ => ""
        };
        var orderId = AttrString(delivery, "orderId");
        if (party.Length == 0 || authenticatedUserId != party || orderId.Length == 0)
        {
            return;
        }

        try
        {
            var order = await _orderRepo.GetByIdAsync(orderId);
            if (order == null || ItemString(order, "cancelReason") != PickupDeadline.CancelReason)
            {
                return;
            }

            context.Logger.LogError(
                $"ORDER_TRANSPORT_CANCEL_PICKUP_REFUSED orderId={MoneyAlarmLog.LogId(orderId)} "
                + $"deliveryId={MoneyAlarmLog.LogId(operation.EntityId)} status={MoneyAlarmLog.LogId(requested)}");
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Pickup on a cancelled delivery {MoneyAlarmLog.LogId(operation.EntityId)}: its order could not be read: [{ex.GetType().Name}]");
        }
    }

    /// <summary>
    /// ESC-06: the pickup deadline of <paramref name="order"/> accepted at
    /// <paramref name="acceptedAtUtc"/> (<see cref="PickupDeadline.Compute"/>): from its trip's
    /// departure, or the self-pickup window when it has no trip or the trip is gone (missing or
    /// soft-deleted). Null when the trip could not be read.
    /// </summary>
    private async Task<DateTime?> ComputePickupDeadlineAsync(
        Dictionary<string, object> order, DateTime acceptedAtUtc, ILambdaContext context)
    {
        var tripId = ItemString(order, "tripId");
        if (tripId.Length == 0)
        {
            return PickupDeadline.Compute(null, acceptedAtUtc);
        }

        Dictionary<string, AttributeValue>? trip;
        try
        {
            trip = await GetTripItemAsync(tripId);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Pickup deadline: trip {MoneyAlarmLog.LogId(tripId)} of order {MoneyAlarmLog.LogId(ItemString(order, "orderId"))} "
                + $"could not be read: [{ex.GetType().Name}]");
            return null;
        }

        DateTime? departure = null;
        if (trip != null
            && AttrString(trip, "deletedAt").Length == 0
            && TryParseDepartureDateUtc(AttrString(trip, "departureDate"), requireIsoShape: false, out var departureUtc))
        {
            departure = departureUtc;
        }

        return PickupDeadline.Compute(departure, acceptedAtUtc);
    }

    /// <summary>
    /// ESC-06: the order's deliveries, read strongly consistent: the active one (the slot holder), or
    /// for an order whose delivery predates the slot claim, every delivery of its listing that names
    /// the order. <c>PickedUp</c> when any of them left <c>pending</c> for a pickup status; otherwise the
    /// pending ones to cancel. Throws when a read fails (the caller answers a transient error).
    /// </summary>
    private async Task<(bool PickedUp, List<string> Pending)> ReadOrderDeliveriesAsync(
        Dictionary<string, object> order, string activeDeliveryId)
    {
        var ids = activeDeliveryId.Length > 0
            ? new List<string> { activeDeliveryId }
            : await QueryOrderDeliveryIdsByListingAsync(ItemString(order, "listingId"), ItemString(order, "orderId"));

        var pending = new List<string>();
        foreach (var id in ids)
        {
            var delivery = await GetDeliveryItemAsync(id, consistentRead: true);
            var status = delivery == null ? null : DeliveryStatusOf(delivery);
            if (!IsBeforePickup(status))
            {
                return (true, pending);
            }

            if (status == "pending")
            {
                pending.Add(id);
            }
        }

        return (false, pending);
    }

    /// <summary>ESC-06: pages of the listing's deliveries read for an order without a slot claim.</summary>
    private const int LegacyDeliveryLookupMaxPages = 10;

    /// <summary>
    /// ESC-06: the deliveries of the order's listing (<c>ListingIndex</c>) that name the order. Throws when
    /// the listing has more pages than <see cref="LegacyDeliveryLookupMaxPages"/>, so an unread delivery
    /// never counts as none.
    /// </summary>
    private async Task<List<string>> QueryOrderDeliveryIdsByListingAsync(string listingId, string orderId)
    {
        var ids = new List<string>();
        if (listingId.Length == 0)
        {
            return ids;
        }

        var request = new QueryRequest
        {
            TableName = _deliveriesTable,
            IndexName = "ListingIndex",
            KeyConditionExpression = "listingId = :listingId",
            FilterExpression = "orderId = :orderId",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":listingId", new AttributeValue { S = listingId } },
                { ":orderId", new AttributeValue { S = orderId } }
            }
        };

        for (var page = 0; page < LegacyDeliveryLookupMaxPages; page++)
        {
            var response = await _dynamoDb.QueryAsync(request);
            ids.AddRange(response.Items.Select(item => AttrString(item, "deliveryId")).Where(id => id.Length > 0));
            if (response.LastEvaluatedKey == null || response.LastEvaluatedKey.Count == 0)
            {
                return ids;
            }

            request.ExclusiveStartKey = response.LastEvaluatedKey;
        }

        // Not every delivery of the listing was read: the order's own may be among the rest.
        throw new InvalidOperationException("The listing has more deliveries than the lookup reads.");
    }

    private static string DeliveryStatusOf(Dictionary<string, AttributeValue> delivery)
    {
        var status = AttrString(delivery, "status");
        return status.Length == 0 ? "pending" : status;
    }

    private static int OrderVersionOf(Dictionary<string, object> order)
        => order.TryGetValue("version", out var raw)
           && int.TryParse(raw?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
            ? version
            : 0;

    private static SyncOperationResult OrderConflict(
        SyncOperationRequest operation, Dictionary<string, object> serverOrder, int clientVersion) => new()
        {
            OperationId = operation.OperationId,
            Status = "conflict",
            ServerVersion = OrderVersionOf(serverOrder),
            ServerData = serverOrder,
            Message = $"Version conflict: server has version {OrderVersionOf(serverOrder)}, client expected {clientVersion}."
        };

    private static SyncOperationResult CancelNotAllowed(SyncOperationRequest operation, string message) => new()
    {
        OperationId = operation.OperationId,
        Status = "error",
        Message = $"ORDER_CANCEL_NOT_ALLOWED: {message}"
    };

    /// <summary>
    /// FUP-20: a live payment with a method the gateway cannot fund. Deterministic, so permanent
    /// under the <c>ORDER_*</c> rule (sync-protocol.md 1.16.0); nothing was written.
    /// </summary>
    private static SyncOperationResult PaymentMethodUnavailable(SyncOperationRequest operation, string message) => new()
    {
        OperationId = operation.OperationId,
        Status = "error",
        Message = $"ORDER_PAYMENT_METHOD_UNAVAILABLE: {message}"
    };

    /// <summary>A transient order failure: no <c>ORDER_</c> prefix, so the client keeps the change and retries.</summary>
    private static SyncOperationResult TransientOrderError(SyncOperationRequest operation, string message) => new()
    {
        OperationId = operation.OperationId,
        Status = "error",
        Message = message
    };

    private static SyncOperationResult TransportCancelUnavailable(
        SyncOperationRequest operation, ILambdaContext context, string orderId, string what, Exception ex)
    {
        context.Logger.LogWarning(
            $"Transport cancel of order {MoneyAlarmLog.LogId(orderId)}: the {what} could not be read or written: [{ex.GetType().Name}]");
        return TransientOrderError(operation, $"The order's {what} could not be read. Please try again later.");
    }

    /// <summary>
    /// Best-effort escrow↔delivery linkage after a delivery create. Self-heals a
    /// missing order escrow while the order still holds money; "not linked" is a
    /// normal outcome for pre-escrow legacy orders.
    /// </summary>
    private async Task TryLinkOrderEscrowAsync(
        string orderId, string deliveryId, Dictionary<string, object> order, ILambdaContext context)
    {
        if (_orderEscrowService == null)
        {
            return;
        }

        try
        {
            var linked = await _orderEscrowService.LinkDeliveryToOrderEscrowAsync(
                orderId, deliveryId, order, "system", _batchAudit);
            if (!linked)
            {
                context.Logger.LogInformation(
                    $"No order escrow linked for delivery {MoneyAlarmLog.LogId(deliveryId)} (orderId={MoneyAlarmLog.LogId(orderId)}).");
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                $"ORDER_ESCROW_LINK_FAILED orderId={MoneyAlarmLog.LogId(orderId)} deliveryId={MoneyAlarmLog.LogId(deliveryId)} "
                + $"reason=[{ex.GetType().Name}]");
        }
    }

    /// <summary>
    /// SIM-40: a new listing shows buyers the owner's name and phone, so they come
    /// from the users table, never from the client (a spoofed phone redirected buyers).
    /// Returns an error message when the profile cannot be read or has no name.
    /// <paramref name="onProfileRead"/> gets the profile once it is read, so a caller that needs
    /// more of it (FUP-98: the crop create's profile woreda) does not read it again.
    /// </summary>
    private async Task<string?> StampOwnerContactAsync(
        Dictionary<string, object> data, string ownerId, string nameKey, string phoneKey,
        ILambdaContext context, Action<UserDto>? onProfileRead = null)
    {
        if (_userRepo == null)
        {
            context.Logger.LogError($"Cannot stamp {phoneKey}: user repository not configured.");
            return "Your profile could not be read. Please try again later.";
        }

        var owner = await _userRepo.GetUserByIdAsync(ownerId);
        if (owner == null)
        {
            return "Your profile could not be read. Please try again later.";
        }

        onProfileRead?.Invoke(owner);

        // Display name first, else the legal name received from Fayda. The client
        // value is never kept: with no name on the profile the create is refused.
        var name = !string.IsNullOrWhiteSpace(owner.DisplayName) ? owner.DisplayName
            : !string.IsNullOrWhiteSpace(owner.FullName) ? owner.FullName
            : null;
        if (name == null)
        {
            return "Add your name to your profile before creating a listing.";
        }

        data[nameKey] = name.Trim();
        data[phoneKey] = owner.PhoneNumber;
        return null;
    }

    /// <summary>
    /// FUP-99: the order's buyerName is shown to the farmer before escrow, so it is stamped
    /// from the buyer's profile (display name, else the Fayda legal name), never kept from
    /// the client. This inherits the FUP-52 phone screen applied to the profile fields.
    /// Orders carry no buyerPhone, so only the name is set, plus the buyer's registered
    /// woreda as the order's drop-off place (order-lifecycle 2.2.0) from the same read.
    /// Returns null when stamped, else a validation message. Mirrors
    /// <see cref="StampOwnerContactAsync"/> for listings.
    /// </summary>
    private async Task<string?> StampOrderBuyerNameAsync(
        Dictionary<string, object> data, string buyerId, ILambdaContext context)
    {
        if (_userRepo == null)
        {
            context.Logger.LogError("Cannot stamp buyerName: user repository not configured.");
            return "Your profile could not be read. Please try again later.";
        }

        var buyer = await _userRepo.GetUserByIdAsync(buyerId);
        if (buyer == null)
        {
            return "Your profile could not be read. Please try again later.";
        }

        var name = !string.IsNullOrWhiteSpace(buyer.DisplayName) ? buyer.DisplayName
            : !string.IsNullOrWhiteSpace(buyer.FullName) ? buyer.FullName
            : null;
        if (name == null)
        {
            return "Add your name to your profile before placing an order.";
        }

        data["buyerName"] = name.Trim();
        if (!string.IsNullOrWhiteSpace(buyer.Woreda))
        {
            data["dropoffPlace"] = buyer.Woreda.Trim();
        }

        return null;
    }

    /// <summary>
    /// Both parties must be phone-verified, and Fayda-verified above the NBE
    /// threshold, before the buyer pays. Fails closed when the profiles cannot be read.
    /// Also returns the buyer's row so the step-up check can read the last Fayda login.
    /// </summary>
    private async Task<(string? Error, UserDto? Buyer)> CheckPaymentKycAsync(
        string buyerId, string? farmerId, decimal orderTotal, ILambdaContext context)
    {
        if (_userRepo == null)
        {
            context.Logger.LogError("Payment KYC gate unavailable: user repository not configured.");
            return (KycPolicy.Message(KycPolicy.ReasonUnavailable, "Identity verification is unavailable right now. Please try again later."), null);
        }

        var buyer = await _userRepo.GetUserByIdAsync(buyerId);
        var farmer = string.IsNullOrEmpty(farmerId) ? null : await _userRepo.GetUserByIdAsync(farmerId);
        return (KycPolicy.CheckPayment(buyer, farmer, orderTotal), buyer);
    }

    /// <summary>
    /// Step-up check for a delivery dispute, sized by the linked order's total (rounded as the
    /// escrow amount is). Null when the dispute may go on, else the sync error message. Fails
    /// closed (FUP-26): an order or escrow that cannot be read answers <c>DELIVERY_DISPUTE_RETRY</c>
    /// before anything is frozen or written, and a missing order or one without a readable total is
    /// sized at the threshold. A legacy delivery without an order is sized by the escrow the freeze
    /// would take from the <c>DeliveryIndex</c>; without one there is nothing to freeze and no step-up.
    /// </summary>
    private async Task<string?> CheckDeliveryDisputeStepUpAsync(
        Dictionary<string, AttributeValue> delivery, string deliveryId, ILambdaContext context)
    {
        var orderId = delivery.TryGetValue("orderId", out var oid) ? oid.S : null;
        decimal amount;
        try
        {
            if (!string.IsNullOrEmpty(orderId))
            {
                var order = await _orderRepo.GetByIdAsync(orderId);
                if (order == null)
                {
                    context.Logger.LogWarning(
                        $"Dispute step-up for delivery {MoneyAlarmLog.LogId(deliveryId)}: order {MoneyAlarmLog.LogId(orderId)} not found, sized at the threshold.");
                }

                amount = order == null ? 0m : ReadDecimal(order, "orderTotal");
            }
            else
            {
                var escrow = _escrowRepo == null ? null : await _escrowRepo.GetByDeliveryIdAsync(deliveryId);
                if (escrow == null)
                {
                    return null;
                }

                amount = ReadDecimal(escrow, "amount");
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Dispute step-up for delivery {MoneyAlarmLog.LogId(deliveryId)} undecided, nothing written: [{ex.GetType().Name}] {MoneyAlarmLog.LogText(ex.Message)}");
            return DeliveryDisputeRetryMessage;
        }

        // An amount that cannot be read (missing, unparseable, zero or negative) needs the fresh password.
        var rounded = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        return StepUpPolicy.Check(_requestAuthTime, rounded > 0m ? rounded : StepUpPolicy.ThresholdEtb);
    }

    /// <summary>
    /// A QR-gated handoff with no farmer's nonce on record (ESC-35: also a scanned drop-off QR whose
    /// trucker's nonce is not on record yet, and the trucker's drop-off nonce sent before the pickup
    /// synced): <c>deferred</c> with <paramref name="pending"/> while it may still sync (nothing
    /// written, the op stays queued without using a retry), <paramref name="refusal"/> once
    /// <see cref="QrNonceMaxDeferDays"/> have passed since the later of the delivery's creation and the
    /// scan (the operation's <c>timestamp</c>, when plausible). From creation alone, a delivery made
    /// weeks before its trip was refused at the first sync of an honest offline scan.
    /// </summary>
    private static SyncOperationResult AwaitHandoffNonce(
        SyncOperationRequest operation, Dictionary<string, AttributeValue> delivery, string refusal,
        string pending = "DELIVERY_NONCE_PENDING: Waiting for the farmer's QR code to sync. Will retry automatically.")
    {
        var now = DateTime.UtcNow;
        var createdAt = TransactionPatternRules.ParseUtc(
            delivery.TryGetValue("createdAt", out var caAttr) ? caAttr.S : null);
        var scannedAt = TransactionPatternRules.PlausibleCaptureTime(operation.Timestamp, now);
        var windowStart = createdAt is { } created && scannedAt is { } scanned
            ? (created > scanned ? created : scanned)
            : createdAt ?? scannedAt;
        var tooOld = windowStart is { } start && now - start > TimeSpan.FromDays(QrNonceMaxDeferDays);
        return tooOld
            ? new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = refusal
            }
            : new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "deferred",
                Message = pending
            };
    }

    /// <summary>
    /// ESC-35: the trucker's update storing its drop-off nonce (<c>dropoffQrNonce</c>) in the secret
    /// store. It stores the nonce and nothing else: the delivery is not written and its version does
    /// not move, so the trucker's queued operations behind it never conflict (as the farmer's nonce,
    /// 1.6.0). Stored only while the delivery is <c>pickedUp</c> or <c>inTransit</c>; before the pickup
    /// reached the server it waits for it (the same batch can carry a pickup that is deferred for the
    /// farmer's nonce); once delivered or disputed nothing is stored. The nonce is never logged.
    /// </summary>
    private async Task<SyncOperationResult> StoreDropoffNonceAsync(
        SyncOperationRequest operation,
        Dictionary<string, AttributeValue> existing,
        string currentStatus,
        string nonce,
        ILambdaContext context)
    {
        if (nonce.Length > DropoffNonceMaxLength
            || nonce.Length <= DropoffNoncePrefix.Length
            || !nonce.StartsWith(DropoffNoncePrefix, StringComparison.Ordinal))
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = $"DELIVERY_VALIDATION: dropoffQrNonce must start with '{DropoffNoncePrefix}' and be at most {DropoffNonceMaxLength} characters."
            };
        }

        if (currentStatus == "pending")
        {
            return AwaitHandoffNonce(operation, existing,
                "DELIVERY_VALIDATION: The drop-off QR code can only be registered after the pickup.",
                "DELIVERY_NONCE_PENDING: Waiting for the pickup to sync. Will retry automatically.");
        }

        if (currentStatus is "pickedUp" or "inTransit")
        {
            var stored = _qrSecretRepo != null
                && await _qrSecretRepo.PutDropoffNonceAsync(operation.EntityId, nonce);
            if (!stored)
            {
                // No farmer's nonce to stand next to (a delivery picked up before FRD-11, or no secret
                // store): the drop-off keeps its old rule.
                context.Logger.LogWarning(
                    $"DROPOFF_NONCE_NOT_STORED deliveryId={MoneyAlarmLog.LogId(operation.EntityId)}");
            }
        }

        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "success",
            ServerVersion = ItemVersion(existing),
            ServerData = ScrubDeliveryForClient(CropRepository.FromItem(existing))
        };
    }

    /// <summary>
    /// FUP-42: whether a delivery carries money, so a trucker's drop-off <c>delivered</c> needs the QR
    /// (a pickup needs it whatever the money since FRD-11) and a typed code needs the leg's GPS. The
    /// order's own escrow (<c>escrow-order-{orderId}</c>, strongly consistent, any status) decides
    /// first, found as the dispute freeze finds it (ESC-07): its link to the delivery can fail
    /// (<c>ORDER_ESCROW_LINK_FAILED</c>) or lag, and the <c>DeliveryIndex</c> alone then called the
    /// delivery cash. Otherwise the <c>DeliveryIndex</c> finds an escrow paid through
    /// <c>POST /escrow/initiate</c>, or one of a legacy delivery without an order. FUP-66: when no
    /// escrow row exists at all, the order's funding decides (<c>paymentVerifiedAt</c>, stamped by every
    /// funding write and never by a client): a funded order whose escrow could not be created (the
    /// create and its self-heal both failed) still holds the buyer's money, so it is never cash. One
    /// more read, only on the path that used to answer cash. Throws when a read fails; the caller defers.
    /// </summary>
    private async Task<bool> HasDeliveryEscrowAsync(Dictionary<string, AttributeValue> delivery, string deliveryId)
    {
        if (_escrowRepo == null)
        {
            return false;
        }

        var orderId = delivery.TryGetValue("orderId", out var oid) ? oid.S : null;
        if (!string.IsNullOrEmpty(orderId)
            && await _escrowRepo.GetByIdAsync($"escrow-order-{orderId}", consistentRead: true) != null)
        {
            return true;
        }

        if (await _escrowRepo.GetByDeliveryIdAsync(deliveryId) != null)
        {
            return true;
        }

        if (string.IsNullOrEmpty(orderId))
        {
            return false;
        }

        var order = await _orderRepo.GetByIdAsync(orderId, consistentRead: true);
        return order != null
            && order.TryGetValue("paymentVerifiedAt", out var verifiedAt)
            && !string.IsNullOrEmpty(verifiedAt?.ToString());
    }

    /// <summary>
    /// T-1: a typed handoff code (<c>scannedQrCode</c>) in place of the scanned nonce for a
    /// <c>pickedUp</c> or <c>delivered</c>. Returns the refusal, or null when the code stands in for
    /// the scan; the caller then applies the same freshness and single-use rules as for a scan.
    /// A code that is not nine digits with a valid Damm digit can never match, so it is a validation
    /// error and no miss. Each attempt of a leg is taken in the secret store before the comparison
    /// (once per operation and code, atomic, so parallel guesses cannot pass the cap and a reused
    /// operation id with a new code still counts) and a right code gives it back; at <see cref="HandoffCode.MaxMisses"/> wrong codes typed codes are locked for that
    /// leg until the farmer's Regenerate stores a new nonce, while a scanned QR still works.
    /// Without the secret store (legacy rows) nothing can be counted, so typed codes are not taken.
    /// On a delivery that carries money a typed code needs the leg's GPS, as the scan screen always
    /// captures it. ESC-35: with <paramref name="dropoffNonce"/> the code is the trucker's drop-off
    /// code, whose lock the trucker's new code clears.
    /// </summary>
    private async Task<SyncOperationResult?> CheckTypedHandoffCodeAsync(
        SyncOperationRequest operation,
        Dictionary<string, AttributeValue> existing,
        DeliveryQrSecret? secret,
        string nonce,
        string code,
        string targetStatus,
        string? callerId,
        ILambdaContext context,
        bool dropoffNonce = false)
    {
        SyncOperationResult Error(string message) => new()
        {
            OperationId = operation.OperationId,
            Status = "error",
            Message = message
        };

        if (!HandoffCode.IsWellFormed(code))
        {
            return Error("DELIVERY_VALIDATION: scannedQrCode must be 9 digits with a valid check digit.");
        }

        if (secret == null || _qrSecretRepo == null)
        {
            return Error("DELIVERY_CODE_LOCKED: Typed codes cannot be checked for this delivery. Scan the QR code.");
        }

        // The farmer's code cannot be renewed after pickup (the farmer's Regenerate only works before
        // it), so a locked drop-off of a trucker delivery checked against it has its own code and way
        // out. ESC-35: the trucker's own drop-off code is renewed by the trucker's new code.
        var lockedDropoff = targetStatus == "delivered"
            && !string.Equals(
                existing.TryGetValue("deliveryType", out var lockTypeAttr) ? lockTypeAttr.S : null,
                SelfPickupDeliveryType, StringComparison.Ordinal);
        var locked = dropoffNonce
            ? "DELIVERY_CODE_LOCKED: Too many wrong codes for this drop-off. Scan the trucker's drop-off QR, or ask the trucker for a new code."
            : lockedDropoff
                ? "DELIVERY_CODE_LOCKED_DROPOFF: Too many wrong codes for this drop-off. Scan the trucker's drop-off QR, or contact support."
                : "DELIVERY_CODE_LOCKED: Too many wrong codes for this delivery. Scan the QR code, or ask the farmer for a new code.";
        const string mismatch = "DELIVERY_CODE_MISMATCH: The typed code does not match this delivery.";
        // One attempt per operation AND code: a true retry is free, a new code under the same
        // operation id (which errors never store) always counts.
        var attemptId = HandoffCode.AttemptId(operation.OperationId, code);
        Task<SyncOperationResult> Refuse(string message) => RefuseWithSignalAsync(
            Error(message), FraudDetectionService.QrHandoffFailureSignal, operation, "delivery", callerId, context);

        if (secret.CodeMissesFor(targetStatus) >= HandoffCode.MaxMisses)
        {
            return await Refuse(locked);
        }

        // The attempt is taken before the comparison, in one conditional write, so guesses sent in
        // parallel cannot get past the cap; a right code gives it back below.
        var attempt = await _qrSecretRepo.TryTakeCodeAttemptAsync(
            operation.EntityId, targetStatus, attemptId, nonce, HandoffCode.MaxMisses, dropoffNonce);
        if (attempt.Outcome == CodeAttemptOutcome.Locked)
        {
            return await Refuse(locked);
        }

        if (attempt.Outcome == CodeAttemptOutcome.NonceChanged)
        {
            // Regenerated meanwhile: the code was typed for a nonce that is gone.
            return await Refuse(mismatch);
        }

        if (!HandoffCode.Matches(code, nonce, operation.EntityId))
        {
            return await Refuse(attempt.Count >= HandoffCode.MaxMisses ? locked : mismatch);
        }

        try
        {
            await _qrSecretRepo.ReturnCodeAttemptAsync(operation.EntityId, targetStatus, attemptId);
        }
        catch (Exception ex)
        {
            // Best effort: a right code left counted costs its leg one try, nothing more.
            context.Logger.LogWarning(
                $"Typed code attempt not given back for delivery {MoneyAlarmLog.LogId(operation.EntityId)}: [{ex.GetType().Name}]");
        }

        var gpsField = targetStatus == "pickedUp" ? "gpsPickup" : "gpsDropoff";
        var hasGps = operation.Data != null
            && operation.Data.TryGetValue(gpsField, out var gpsObj)
            && gpsObj is System.Text.Json.JsonElement gpsJe
            && gpsJe.ValueKind == System.Text.Json.JsonValueKind.Object;
        if (hasGps)
        {
            return null;
        }

        bool carriesMoney;
        try
        {
            carriesMoney = await HasDeliveryEscrowAsync(existing, operation.EntityId);
        }
        catch (Exception ex)
        {
            // As the QR gate (FUP-42): an unknown answer is never cash, nothing is written and the
            // op stays queued; the same alarmed token.
            var gateOrderId = existing.TryGetValue("orderId", out var gateOrderAttr) ? gateOrderAttr.S : null;
            context.Logger.LogError(
                $"QR_GATE_UNDECIDED deliveryId={MoneyAlarmLog.LogId(operation.EntityId)} "
                + $"orderId={MoneyAlarmLog.LogId(gateOrderId)} reason={ex.GetType().Name} "
                + $"detail={MoneyAlarmLog.LogText(ex.Message)}");
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "deferred",
                Message = "DELIVERY_NONCE_PENDING: The payment check could not be completed. Will retry automatically."
            };
        }

        return carriesMoney
            ? Error("DELIVERY_CODE_GPS_REQUIRED: Location is required to confirm a paid delivery with a typed code.")
            : null;
    }

    /// <summary>True when the caller's id equals any of the named id fields.</summary>
    private static bool IsParty(Dictionary<string, object> item, string? userId, params string[] idFields)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return false;
        }
        foreach (var field in idFields)
        {
            if (item.TryGetValue(field, out var raw) && string.Equals(raw?.ToString(), userId, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsParty(Dictionary<string, AttributeValue> item, string? userId, params string[] idFields)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return false;
        }
        foreach (var field in idFields)
        {
            if (item.TryGetValue(field, out var attr) && string.Equals(attr.S, userId, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static decimal ReadDecimal(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var raw) || raw == null)
        {
            return 0m;
        }

        return raw switch
        {
            decimal d => d,
            double dbl => (decimal)dbl,
            int i => i,
            long l => l,
            _ => decimal.TryParse(raw.ToString(), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m
        };
    }

    /// <summary>
    /// Validate authorization for specific order state transitions.
    /// Returns null if authorized, or an error message string if not.
    /// </summary>
    private static string? ValidateOrderTransitionAuth(
        string currentStatus, string newStatus,
        string? authenticatedUserId, string? buyerId, string? farmerId)
    {
        // System-only transitions: block from client sync. payment_failed joined them in
        // ESC-01: a participant setting it while a Telebirr payment was in flight let the
        // buyer resubmit and pay twice (order-lifecycle.md says only the system sets it).
        // ESC-07: disputed too. A client order dispute froze no money (the 48-hour release
        // still paid out); disputes come from the delivery or POST /escrow/dispute, which
        // freeze the escrow first and move the order after.
        // ESC-15: awaiting_transport too; only a failed transport (OrderCommitment) parks an order there.
        if (newStatus is "funded" or "disbursing" or "settled" or "dispute_resolved" or "payment_failed" or "disputed"
            or OrderCommitment.AwaitingTransportStatus)
        {
            return $"ORDER_UNAUTHORIZED: '{newStatus}' is a system-managed transition.";
        }

        // Buyer actions: cancel, submit payment
        if (newStatus is "cancelled" or "pending_payment" or "payment_submitted")
        {
            if (authenticatedUserId != buyerId)
            {
                return $"ORDER_UNAUTHORIZED: Only the buyer can transition to '{newStatus}'.";
            }
        }

        // Farmer actions: accept, reject
        if (newStatus is "accepted" or "rejected")
        {
            if (authenticatedUserId != farmerId)
            {
                return $"ORDER_UNAUTHORIZED: Only the farmer can transition to '{newStatus}'.";
            }
        }

        // Logistics transitions (pickup_confirmed, in_transit, delivered): either
        // participant (and the order's trucker) may request them, gated by the
        // participant check above and, in ProcessOrderUpdateAsync, by the state of the
        // order's active delivery (SIM-11 for delivered, SIM-60 for the other two).

        return null;
    }

    /// <summary>
    /// SIM-10: the contract's example payloads and the mobile dispute call echo
    /// bookkeeping keys the server owns. deliveryId may be sent but must name this
    /// operation's entity; updatedAt, pickedUpAt and confirmedAt are always stamped by
    /// the server (confirmedAt anchors the 48-hour dispute window, so a client value
    /// must never be trusted) and are dropped here. Returns an error message without
    /// the code prefix, or null when clean.
    /// </summary>
    private static string? StripDeliveryEchoFields(SyncOperationRequest operation)
    {
        var data = operation.Data!;
        if (data.TryGetValue("deliveryId", out var echoedDeliveryId))
        {
            if (!string.Equals(echoedDeliveryId?.ToString(), operation.EntityId, StringComparison.Ordinal))
            {
                return "deliveryId does not match the operation entityId.";
            }

            data.Remove("deliveryId");
        }

        data.Remove("updatedAt");
        data.Remove("pickedUpAt");
        data.Remove("confirmedAt");
        return null;
    }

    /// <summary>
    /// FUP-246: the delivery fields that belong to one handoff, with the status that handoff writes
    /// (sync-protocol.md 1.54.0). The role check pins each status to its party: <c>pickedUp</c> to the
    /// trucker, <c>delivered</c> to the buyer, <c>disputed</c> to the party who disputes. Before, a
    /// buyer's confirm or any party's dispute could rewrite the other party's GPS point, which feeds
    /// <c>IMPOSSIBLE_DELIVERY_TIME</c> on the trucker and the admin dispute evidence.
    /// </summary>
    private static readonly (string Field, string Status)[] DeliveryHandoffFields =
    {
        ("gpsPickup", "pickedUp"),
        ("gpsDropoff", "delivered"),
        ("disputeReason", "disputed")
    };

    /// <summary>
    /// FUP-246: drops, not refuses, each handoff field the operation's status does not write (a create
    /// is <c>pending</c>, an update without a status is null), so every shipped client, which sends each
    /// field only with its own status, is answered as before. Each drop is logged with no personal data.
    /// </summary>
    private static void DropOtherHandoffFields(SyncOperationRequest operation, string? status, ILambdaContext context)
    {
        foreach (var (field, handoffStatus) in DeliveryHandoffFields)
        {
            if (status != handoffStatus && operation.Data!.Remove(field))
            {
                context.Logger.LogInformation(
                    $"DELIVERY_FIELD_DROPPED field={field} status={MoneyAlarmLog.LogId(status ?? "none")} "
                    + $"deliveryId={MoneyAlarmLog.LogId(operation.EntityId)}");
            }
        }
    }

    /// <summary>
    /// SIM-08: the client's optimistic-concurrency version. The contract carries it as
    /// the op-level clientVersion; the shipped clients send data.version. Both are
    /// honoured, op-level first. Returns false (version 0) when neither is present.
    /// </summary>
    private static bool TryGetClientVersion(SyncOperationRequest operation, out int version)
    {
        if (operation.ClientVersion is int opLevel)
        {
            version = opLevel;
            return true;
        }

        if (operation.Data != null
            && operation.Data.TryGetValue("version", out var raw)
            && raw != null
            && int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out version))
        {
            return true;
        }

        version = 0;
        return false;
    }

    /// <summary>
    /// SIM-28: the version the client sent when the stored row has moved past it (another device
    /// or party changed the entity first), else null. A status change that is invalid against the
    /// stored row then comes from the loser of a race: it is answered <c>conflict</c> with the row,
    /// like any version conflict. With a current, newer or missing version the invalid-transition
    /// error stands.
    /// </summary>
    private static int? StaleClientVersion(SyncOperationRequest operation, int storedVersion)
        => TryGetClientVersion(operation, out var sent) && sent < storedVersion ? sent : null;

    /// <summary>A version conflict answered with the stored row (server wins).</summary>
    private static SyncOperationResult StaleVersionConflict(
        SyncOperationRequest operation, int serverVersion, int clientVersion, Dictionary<string, object> serverData) => new()
        {
            OperationId = operation.OperationId,
            Status = "conflict",
            ServerVersion = serverVersion,
            ServerData = serverData,
            Message = $"Version conflict: server has version {serverVersion}, client expected {clientVersion}."
        };

    /// <summary>
    /// SIM-28: whether <paramref name="userId"/> may ask for <paramref name="status"/> on this delivery
    /// (the role rules of the delivery update): the trucker picks up and drives, the buyer confirms,
    /// any party disputes.
    /// </summary>
    private static bool MayRequestDeliveryStatus(Dictionary<string, AttributeValue> delivery, string? userId, string? status)
        => status switch
        {
            "pickedUp" or "inTransit" => IsParty(delivery, userId, "truckerId"),
            "delivered" => IsParty(delivery, userId, "buyerId"),
            "disputed" => IsParty(delivery, userId, "buyerId", "farmerId", "truckerId"),
            _ => false
        };

    /// <summary>
    /// FUP-128: an update that reaches a delivery already cancelled (or soft-deleted before 1.9.0).
    /// When the client is behind the stored version (another party or the system cancelled the
    /// delivery while it was offline) and may make this change (the SIM-28 rule for a status; the
    /// trucker, or the farmer storing the pickup nonce, for any other field), the answer is a
    /// conflict with the stored row, so the client takes the cancelled delivery (server wins)
    /// instead of retrying an error. Null otherwise: the caller keeps its refusal, so anyone else
    /// never gets the row, and a current, newer or missing version is still an error.
    /// </summary>
    private static SyncOperationResult? CancelledDeliveryConflict(
        SyncOperationRequest operation, Dictionary<string, AttributeValue> delivery, string? userId)
    {
        var storedVersion = ItemVersion(delivery);
        if (StaleClientVersion(operation, storedVersion) is null)
        {
            return null;
        }

        var data = operation.Data!;
        var mayAsk = data.TryGetValue("status", out var requestedStatus)
            ? MayRequestDeliveryStatus(delivery, userId, requestedStatus?.ToString())
            : IsParty(delivery, userId, "truckerId")
                || (data.ContainsKey("qrNonce") && IsParty(delivery, userId, "farmerId"));
        if (!mayAsk)
        {
            return null;
        }

        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "conflict",
            ServerVersion = storedVersion,
            ServerData = ScrubDeliveryForClient(CropRepository.FromItem(delivery)),
            Message = "DELIVERY_ALREADY_CANCELLED: The delivery is already cancelled."
        };
    }

    /// <summary>The <c>version</c> of a raw DynamoDB row, 0 when it has none.</summary>
    private static int ItemVersion(Dictionary<string, AttributeValue> row)
        => row.TryGetValue("version", out var versionAttr)
           && int.TryParse(versionAttr.N, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
            ? version
            : 0;

    private static SyncOperationResult OrderValidationError(SyncOperationRequest operation, string message) => new()
    {
        OperationId = operation.OperationId,
        Status = "error",
        Message = $"ORDER_VALIDATION: {message}"
    };

    /// <summary>
    /// FUP-22: the live gateway's refusal of a payment whose total one Telebirr payment cannot take.
    /// Its own code so clients match it without reading the English text (sync-protocol.md 1.24.0);
    /// deterministic, so permanent under the <c>ORDER_*</c> rule. Nothing was written.
    /// </summary>
    private static SyncOperationResult PaymentTotalOutOfRange(SyncOperationRequest operation, string message) => new()
    {
        OperationId = operation.OperationId,
        Status = "error",
        Message = $"ORDER_PAYMENT_TOTAL_OUT_OF_RANGE: {message}"
    };

    /// <summary>
    /// SIM-05: bind an order create to the listing it references (existence is checked
    /// by the caller). Returns an error message without the code prefix, or null when
    /// clean. Overwrites farmerName from the listing so the client cannot spoof it
    /// (empty when the listing has no real name, FUP-130).
    /// </summary>
    private static string? BindOrderToListing(
        Dictionary<string, object> data, Dictionary<string, object> listing, string? buyerId)
    {
        var listingStatus = listing.TryGetValue("status", out var ls)
            ? ls?.ToString() ?? "available"
            : "available";
        if (!string.Equals(listingStatus, "available", StringComparison.OrdinalIgnoreCase))
        {
            return $"Listing is not available for orders (status: {listingStatus}).";
        }

        var listingFarmerId = listing.TryGetValue("farmerId", out var lf) ? lf?.ToString() : null;
        if (string.IsNullOrEmpty(listingFarmerId)
            || !string.Equals(data["farmerId"]?.ToString(), listingFarmerId, StringComparison.Ordinal))
        {
            return "farmerId must be the farmer on the listing.";
        }

        if (string.Equals(buyerId, listingFarmerId, StringComparison.Ordinal))
        {
            return "Self-trading not allowed. Buyer and farmer must be different users.";
        }

        var listingCropType = listing.TryGetValue("cropType", out var lc) ? lc?.ToString() : null;
        if (!string.Equals(data["cropType"]?.ToString(), listingCropType, StringComparison.OrdinalIgnoreCase))
        {
            return "cropType must match the listing.";
        }

        var listingPricePerKg = ParseDouble(listing, "pricePerKg");
        var pricePerUnit = ParseDouble(data, "pricePerUnit");
        if (listingPricePerKg <= 0 || Math.Abs(pricePerUnit - listingPricePerKg) > MoneyTolerance)
        {
            return "pricePerUnit must equal the listing price "
                + $"({listingPricePerKg.ToString("0.00", CultureInfo.InvariantCulture)} ETB/kg).";
        }

        var stockKg = ParseDouble(listing, "quantity") * KgPerQuintal;
        var quantityKg = ParseDouble(data, "quantity");
        if (quantityKg > stockKg + QuantityTolerance)
        {
            return $"quantity ({quantityKg.ToString("0.##", CultureInfo.InvariantCulture)} kg) exceeds "
                + $"the listing stock ({stockKg.ToString("0.##", CultureInfo.InvariantCulture)} kg).";
        }

        // FUP-130: always the listing's name, never the client's copy (apps used to send the
        // farmer's id when the listing had no name). A listing without a real name leaves the
        // field empty and the apps show their own label; the field stays on the row because
        // released apps read it as a string.
        data["farmerName"] = PartyName.OrEmpty(listing.GetValueOrDefault("farmerName")?.ToString(), listingFarmerId);

        // The farm's place for the trip bookings row (order-lifecycle 2.2.0), from the read this
        // bind already makes; a listing without one leaves the field off the row.
        var pickupPlace = listing.GetValueOrDefault("locationName")?.ToString();
        if (!string.IsNullOrWhiteSpace(pickupPlace))
        {
            data["pickupPlace"] = pickupPlace.Trim();
        }

        return null;
    }

    /// <summary>
    /// SIM-05: bind an order create to the trip it references and derive the transport
    /// cost (kg / 100 x pricePerQuintal). A client-sent transportCost may only echo it.
    /// Overwrites truckerName from the trip.
    /// </summary>
    private static string? BindOrderToTrip(
        Dictionary<string, object> data,
        Dictionary<string, AttributeValue> trip,
        string truckerId,
        double quantityKg,
        double? clientTransportCost,
        DateTime nowUtc,
        bool enforceBookings,
        out double transportCost)
    {
        transportCost = 0.0;

        var tripStatus = trip.TryGetValue("status", out var ts) ? ts.S ?? "open" : "open";
        if (tripStatus is not ("open" or "matched"))
        {
            return $"Trip is not open for orders (status: {tripStatus}).";
        }

        var tripTruckerId = trip.TryGetValue("truckerId", out var tt) ? tt.S : null;
        if (string.IsNullOrEmpty(tripTruckerId)
            || !string.Equals(tripTruckerId, truckerId, StringComparison.Ordinal))
        {
            return "truckerId must be the trucker on the trip.";
        }

        // L-7: a trip whose Ethiopia day is over has left, whatever the flag. A buyer's phone
        // may still list it from an old cache, offline, and queue an order on it. The trip's
        // own code, which clients treat as permanent and explain ("choose again").
        if (TryParseDepartureDateUtc(AttrString(trip, "departureDate"), requireIsoShape: false, out var leftUtc)
            && TripDeparture.HasDeparted(leftUtc, nowUtc))
        {
            return $"{OrderCommitment.TripNotEligibleCode}: This trip has already left; choose another trip.";
        }

        // ESC-15/17: the farmer and the trucker each get a window to answer before the truck
        // leaves, so a trip departing within 24 hours takes no new order; and the order must
        // fit in what the trip still has free (the atomic hold happens at funding). GET /trips
        // tells the apps this lead (OrderCommitment.OrderLeadHours, FUP-122) so their match
        // suggestions drop the same trips with the same comparison.
        if (enforceBookings)
        {
            if (TryParseDepartureDateUtc(AttrString(trip, "departureDate"), requireIsoShape: false, out var departureUtc)
                && departureUtc < nowUtc + OrderCommitment.ResponseWindow)
            {
                // ESC-16: the lead is the response window read from SSM (24 hours by default).
                return $"This trip leaves within {OrderTimings.HoursText(OrderCommitment.ResponseWindow)} hours; choose a later trip.";
            }

            var remaining = RemainingQuintalsOf(trip);
            if (remaining < quantityKg / KgPerQuintal - MoneyTolerance)
            {
                return "TRIP_CAPACITY_EXCEEDED: The trip has only "
                    + $"{remaining.ToString("0.##", CultureInfo.InvariantCulture)} quintals free.";
            }
        }

        var pricePerQuintal = trip.TryGetValue("pricePerQuintal", out var pq)
            && double.TryParse(pq.N ?? pq.S, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0.0;
        if (pricePerQuintal <= 0)
        {
            return "Trip has no valid pricePerQuintal.";
        }

        transportCost = quantityKg / KgPerQuintal * pricePerQuintal;
        if (clientTransportCost is double sent && Math.Abs(sent - transportCost) > MoneyTolerance)
        {
            return "transportCost must equal quantity/100 x the trip pricePerQuintal "
                + $"({transportCost.ToString("0.00", CultureInfo.InvariantCulture)} ETB).";
        }

        // FUP-130: always the trip's name, never the client's copy. truckerName is optional on
        // an order, so a trip without a real name leaves it off the row.
        var tripTruckerName = PartyName.OrEmpty(AttrString(trip, "truckerName"), tripTruckerId);
        if (tripTruckerName.Length > 0)
        {
            data["truckerName"] = tripTruckerName;
        }
        else
        {
            data.Remove("truckerName");
        }

        return null;
    }

    private static List<string> ValidateOrderFields(Dictionary<string, object> data, bool isCreate)
    {
        var errors = new List<string>();

        if (isCreate)
        {
            if (!data.ContainsKey("listingId"))
            {
                errors.Add("listingId is required.");
            }

            if (!data.ContainsKey("buyerId"))
            {
                errors.Add("buyerId is required.");
            }

            if (!data.ContainsKey("farmerId"))
            {
                errors.Add("farmerId is required.");
            }

            if (!data.ContainsKey("buyerName"))
            {
                errors.Add("buyerName is required.");
            }

            if (!data.ContainsKey("farmerName"))
            {
                errors.Add("farmerName is required.");
            }

            if (!data.ContainsKey("cropType"))
            {
                errors.Add("cropType is required.");
            }
        }

        // Validate quantity
        if (data.TryGetValue("quantity", out var qObj))
        {
            if (!decimal.TryParse(qObj?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var qty)
                || qty <= 0 || qty > MaxOrderKg)
            {
                errors.Add("quantity must be between 0.01 and 1000000 kg.");
            }
        }
        else if (isCreate)
        {
            errors.Add("quantity is required.");
        }

        // Validate pricePerUnit
        if (data.TryGetValue("pricePerUnit", out var ppuObj))
        {
            if (!decimal.TryParse(ppuObj?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ppu)
                || ppu <= 0 || ppu > 10000)
            {
                errors.Add("pricePerUnit must be between 0.01 and 10000.");
            }
        }
        else if (isCreate)
        {
            errors.Add("pricePerUnit is required.");
        }

        // Validate cropType against allowed enum
        if (data.TryGetValue("cropType", out var ctObj))
        {
            var cropType = ctObj?.ToString() ?? "";
            if (!AllowedCropTypes.Contains(cropType))
            {
                errors.Add("Invalid cropType.");
            }
        }

        // Validate string lengths
        if (data.TryGetValue("buyerName", out var bnObj))
        {
            var name = bnObj?.ToString() ?? "";
            if (name.Length > 100)
            {
                errors.Add("buyerName must not exceed 100 characters.");
            }
        }

        if (data.TryGetValue("farmerName", out var fnObj))
        {
            var name = fnObj?.ToString() ?? "";
            if (name.Length > 100)
            {
                errors.Add("farmerName must not exceed 100 characters.");
            }
        }

        if (data.TryGetValue("rejectionReason", out var rrObj))
        {
            var reason = rrObj?.ToString() ?? "";
            if (reason.Length > 500)
            {
                errors.Add("rejectionReason must not exceed 500 characters.");
            }
        }

        if (data.TryGetValue("cancelReason", out var crObj))
        {
            var reason = crObj?.ToString() ?? "";
            if (reason.Length > 500)
            {
                errors.Add("cancelReason must not exceed 500 characters.");
            }
        }

        if (data.TryGetValue("cbeTransactionId", out var cbeObj))
        {
            var txId = cbeObj?.ToString() ?? "";
            if (txId.Length > 100)
            {
                errors.Add("cbeTransactionId must not exceed 100 characters.");
            }
        }

        // LT-12: one line of at most 80 user-visible characters (OrderDropoffNote.Length).
        if (data.TryGetValue(OrderDropoffNote.Field, out var noteObj)
            && OrderDropoffNote.ValidationError(noteObj) is { } noteError)
        {
            errors.Add(noteError);
        }

        // LT-14: the drop-off point, both or neither, inside Ethiopia's box.
        if (OrderDeliveryLocation.ValidationError(data) is { } locationError)
        {
            errors.Add(locationError);
        }

        return errors;
    }

    /// <summary>
    /// SIM-30: an ETB amount to the santim (2 dp), halves away from zero, the rounding of the escrow
    /// money (<c>EscrowService.RoundMoney</c>) and of the apps' order preview (<c>roundEtb</c>).
    /// </summary>
    private static decimal RoundEtb(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Get the platform commission rate from SSM with 15-minute cache.
    /// Falls back to DefaultCommissionRate if SSM is unavailable.
    /// </summary>
    private static double GetCommissionRate(ILambdaContext context)
    {
        if (DateTime.UtcNow - _commissionRateFetchedAt < CommissionRateCacheTtl)
        {
            return _cachedCommissionRate;
        }

        try
        {
            var paramName = Environment.GetEnvironmentVariable("COMMISSION_RATE_PARAM");
            if (string.IsNullOrEmpty(paramName))
            {
                _cachedCommissionRate = DefaultCommissionRate;
                _commissionRateFetchedAt = DateTime.UtcNow;
                return _cachedCommissionRate;
            }

            var value = AwsParameterResolver.GetSsmParameterValue(paramName);
            if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var rate) && rate > 0 && rate < 1)
            {
                _cachedCommissionRate = rate;
            }
            else
            {
                context.Logger.LogWarning($"Invalid commission rate from SSM: '{value}'. Using default {DefaultCommissionRate}.");
                _cachedCommissionRate = DefaultCommissionRate;
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"Failed to fetch commission rate from SSM: [{ex.GetType().Name}]. Using default.");
            _cachedCommissionRate = DefaultCommissionRate;
        }

        _commissionRateFetchedAt = DateTime.UtcNow;
        return _cachedCommissionRate;
    }

    private static double ParseDouble(Dictionary<string, object> data, string key)
    {
        if (!data.TryGetValue(key, out var value))
        {
            return 0.0;
        }

        return value switch
        {
            double d => d,
            float f => f,
            decimal m => (double)m,
            string s => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : 0.0,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number => je.GetDouble(),
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String =>
                double.TryParse(je.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var r2) ? r2 : 0.0,
            _ => 0.0
        };
    }

    private static double? ParseDoubleOrNull(Dictionary<string, object> data, string key)
    {
        if (!data.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            double d => d,
            float f => f,
            decimal m => (double)m,
            string s => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : null,
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number => je.GetDouble(),
            System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String =>
                double.TryParse(je.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var r2) ? r2 : null,
            _ => null
        };
    }
}
