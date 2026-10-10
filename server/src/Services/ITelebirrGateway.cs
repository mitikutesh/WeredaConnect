using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// Abstraction over Telebirr's mobile money API for hold/release/refund operations.
/// Enables mock implementations during development and testability.
/// All methods are idempotent via transactionRef.
/// </summary>
public interface ITelebirrGateway
{
    /// <summary>
    /// Hold funds in the buyer's Telebirr account for escrow.
    /// </summary>
    Task<HoldFundsResponse> HoldFundsAsync(HoldFundsRequest request);

    /// <summary>
    /// Release held funds to the farmer's Telebirr account (minus platform commission).
    /// A gateway that cannot pay out returns <c>Success = false</c> with
    /// <see cref="TelebirrGatewayErrors.PayoutNotSupported"/> (ESC-02); it never reports a
    /// payout it did not make. A success always carries a non-empty <c>TelebirrRef</c>.
    /// </summary>
    Task<ReleaseFundsResponse> ReleaseFundsAsync(ReleaseFundsRequest request);

    /// <summary>
    /// Refund held funds to the buyer's Telebirr account (full amount, no commission).
    /// Same fail-closed rule as <see cref="ReleaseFundsAsync"/>.
    /// </summary>
    Task<RefundFundsResponse> RefundFundsAsync(RefundFundsRequest request);

    /// <summary>
    /// Query Telebirr for the current status of a transaction (used by reconciliation).
    /// </summary>
    Task<TransactionStatusResponse> GetTransactionStatusAsync(string transactionRef);

    /// <summary>
    /// ESC-01: true when <see cref="HoldFundsAsync"/> opens a hosted checkout that the buyer
    /// completes in Telebirr (the webhook confirms the payment later); false for the
    /// synchronous mock gateway.
    /// </summary>
    bool SupportsHostedCheckout { get; }

    /// <summary>
    /// ESC-01: a freshly signed checkout URL for an open <paramref name="prepayId"/> (the same
    /// payment, new nonce and timestamp), so a buyer whose response was lost gets the same
    /// checkout back instead of a second payable one. Null when the gateway has no hosted
    /// checkout.
    /// </summary>
    string? BuildCheckoutUrl(string prepayId);
}

/// <summary>
/// Gateway error codes the escrow service reacts to by name.
/// </summary>
/// <remarks>
/// ESC-05 rule for every gateway: a failure that may have moved money (no answer, a lost,
/// unreadable or incomplete answer) returns <see cref="Timeout"/> or <see cref="Network"/>, or
/// throws. The escrow service then keeps the settlement lock and parks the settlement for an
/// admin; it never calls the gateway again for it. Any other failure code means Telebirr
/// definitely did not move the money, and the next sweep retries.
/// </remarks>
public static class TelebirrGatewayErrors
{
    /// <summary>
    /// The gateway has no payout (B2B disbursement) API yet. The settlement is not
    /// made and goes to the manual payout queue (ESC-02).
    /// </summary>
    public const string PayoutNotSupported = "PAYOUT_NOT_SUPPORTED";

    /// <summary>The request timed out: Telebirr may have acted on it.</summary>
    public const string Timeout = "TELEBIRR_TIMEOUT";

    /// <summary>No answer, or an answer that could not be read: Telebirr may have acted on it.</summary>
    public const string Network = "TELEBIRR_NETWORK";

    /// <summary>
    /// ESC-45: the fabric token could not be obtained, so the payment request was never sent. A
    /// definite failure: nothing was created at Telebirr.
    /// </summary>
    public const string TokenFailed = "TELEBIRR_TOKEN";
}
