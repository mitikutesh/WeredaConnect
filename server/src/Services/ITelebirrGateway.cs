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
    /// </summary>
    Task<ReleaseFundsResponse> ReleaseFundsAsync(ReleaseFundsRequest request);

    /// <summary>
    /// Refund held funds to the buyer's Telebirr account (full amount, no commission).
    /// </summary>
    Task<RefundFundsResponse> RefundFundsAsync(RefundFundsRequest request);

    /// <summary>
    /// Query Telebirr for the current status of a transaction (used by reconciliation).
    /// </summary>
    Task<TransactionStatusResponse> GetTransactionStatusAsync(string transactionRef);
}
