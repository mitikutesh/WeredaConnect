namespace WeredaConnect.Server.Models;

// ──────────────────────────────────────────────
// ESC-24: the admin's view of the legs, the escrow ledger and the stuck orders (read only)
// ──────────────────────────────────────────────

/// <summary>
/// ESC-24: where the transport leg of an escrow stands, on the admin escrow DTO. Read only: a trucker leg
/// is paid by the settlement sweeps or recorded in Manual Payouts
/// (<c>POST /admin/payouts/{escrowId}/{ref}/complete</c>), nowhere else (escrow-protocol.md 1.11.0).
/// </summary>
public sealed record AdminTruckerLegDto
{
    /// <summary>
    /// <c>manual_queue</c> (waits in Manual Payouts), <c>paid</c>, <c>blocked</c> (owed, the payout gate
    /// refuses the trucker: see <see cref="BlockReason"/>), <c>pending</c> (owed, retried every hour),
    /// <c>held</c>, <c>released</c> (a ledger escrow: decided for the trucker, its ledger row says whether it is
    /// paid), <c>refunded</c>, or <c>none</c> when nothing says.
    /// </summary>
    public required string State { get; init; }

    /// <summary>The transport leg is owed after the farmer was paid (<c>truckerPayoutPending</c>).</summary>
    public required bool Pending { get; init; }

    /// <summary>When the leg went pending (ISO 8601).</summary>
    public string? PendingSince { get; init; }

    /// <summary><c>no_trucker</c>, <c>profile_missing</c>, <c>phone_unverified</c>, <c>fayda_required</c> or <c>trucker_mismatch</c>.</summary>
    public string? BlockReason { get; init; }

    /// <summary>The leg waits in the manual payout queue.</summary>
    public required bool InManualQueue { get; init; }

    /// <summary>When the leg was recorded as paid (ISO 8601).</summary>
    public string? PaidAt { get; init; }

    /// <summary>The Telebirr reference of the trucker payout.</summary>
    public string? TelebirrRef { get; init; }
}

/// <summary>ESC-24: <c>GET /admin/escrows/{id}/ledger</c>, one page of an escrow's ledger rows (ESC-20), by ref.</summary>
public sealed record AdminEscrowLedgerResponse
{
    public required string EscrowId { get; init; }

    /// <summary><c>ledger</c>, or <c>legacy</c> for an escrow that settles on the 1.x path (it has no rows).</summary>
    public required string SettlementModel { get; init; }

    public required List<AdminLedgerRowDto> Rows { get; init; }
    public string? NextToken { get; init; }
}

/// <summary>ESC-24: one money movement of a ledger escrow (escrow-protocol.md 2.2.0, P.3), as the admin reads it.</summary>
public sealed record AdminLedgerRowDto
{
    /// <summary><c>release#farmer</c>, <c>release#trucker#{id}</c>, <c>refund#all</c>, <c>verdict#buyer</c>, ...</summary>
    public required string Ref { get; init; }

    /// <summary><c>release</c>, <c>refund</c>, <c>verdict</c>, <c>shift</c> or <c>topup</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The legs the row moves: <c>crop</c>, <c>transport</c>, <c>commission</c>.</summary>
    public required List<string> Legs { get; init; }

    /// <summary><c>farmer</c>, <c>trucker</c> or <c>buyer</c>.</summary>
    public string? PayeeRole { get; init; }
    public string? PayeeId { get; init; }
    public string? PayeeName { get; init; }
    public required decimal Amount { get; init; }

    /// <summary><c>pending</c>, <c>sent</c>, <c>done</c>, <c>failed</c> or <c>manual</c>.</summary>
    public required string Status { get; init; }

    /// <summary>The reference sent to the gateway for this row.</summary>
    public required string GatewayRef { get; init; }

    /// <summary>The gateway's (or the admin's recorded) transaction reference, once there is one.</summary>
    public string? TelebirrRef { get; init; }
    public required int Attempts { get; init; }
    public string? LastCode { get; init; }

    /// <summary>Why the row waits in Manual Payouts (<c>PAYOUT_NOT_SUPPORTED</c>, <c>PAYEE_NOT_VERIFIED</c>, ...).</summary>
    public string? ManualReason { get; init; }
    public required string Reason { get; init; }
    public required string Actor { get; init; }

    /// <summary><c>gateway</c>, or the admin who recorded the payout.</summary>
    public string? PaidBy { get; init; }
    public required string CreatedAt { get; init; }
    public required string UpdatedAt { get; init; }
    public string? SentAt { get; init; }
    public string? DoneAt { get; init; }
}

/// <summary>
/// ESC-24: <c>GET /admin/orders/queue?source=queue|deadline</c>, one page of stuck orders, oldest first.
/// <c>queue</c> is the admin stuck-order queue (ESC-06, <c>AdminQueueIndex</c>); <c>deadline</c> the orders whose
/// commitment deadline (ESC-16, <c>DeadlineIndex</c>) passed and nothing acted on it.
/// </summary>
public sealed record AdminStuckOrderListResponse
{
    public required string Source { get; init; }
    public required List<AdminStuckOrderDto> Orders { get; init; }

    /// <summary>More of the index is unread. A <c>deadline</c> page can be short or empty while this is set.</summary>
    public string? NextToken { get; init; }
    public required int Total { get; init; }

    /// <summary>The server time the ages were counted at (ISO 8601).</summary>
    public required string AsOf { get; init; }
}

/// <summary>ESC-24: one stuck order with why and for how long.</summary>
public sealed record AdminStuckOrderDto
{
    public required string OrderId { get; init; }
    public required string Status { get; init; }
    public required string Stage { get; init; }

    /// <summary>
    /// <c>hold_cap</c> (held 21 days, ESC-06), or the deadline that passed: <c>farmer_response</c>,
    /// <c>trucker_response</c>, <c>buyer_choice</c> or <c>pickup_overdue</c> (ESC-16).
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>When the order became stuck: its <c>adminQueuedAt</c>, or the deadline that passed (ISO 8601).</summary>
    public required string StuckSince { get; init; }

    /// <summary>Whole hours since <see cref="StuckSince"/>.</summary>
    public required int AgeHours { get; init; }
    public required string BuyerId { get; init; }
    public string? BuyerName { get; init; }
    public required string FarmerId { get; init; }
    public string? FarmerName { get; init; }
    public string? TruckerId { get; init; }
    public string? TruckerName { get; init; }
    public required string CropType { get; init; }
    public required double OrderTotal { get; init; }
    public required string Currency { get; init; }

    /// <summary>The escrow whose money the queue entry is about, when the entry names one.</summary>
    public string? EscrowId { get; init; }
    public required string CreatedAt { get; init; }
    public required string UpdatedAt { get; init; }
}
