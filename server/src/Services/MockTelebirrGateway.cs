using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Services;

/// <summary>
/// Mock Telebirr gateway for development and testing.
/// Returns deterministic reference IDs with a simulated 500ms delay.
/// Supports SIMULATE_FAILURE and SIMULATE_TIMEOUT flags via transactionRef.
/// </summary>
public sealed class MockTelebirrGateway : ITelebirrGateway
{
    private const int SimulatedDelayMs = 500;
    private const int SimulatedTimeoutMs = 35_000;

    public async Task<HoldFundsResponse> HoldFundsAsync(HoldFundsRequest request)
    {
        if (request.TransactionRef.Contains("TIMEOUT", StringComparison.Ordinal))
        {
            await Task.Delay(SimulatedTimeoutMs);
        }

        await Task.Delay(SimulatedDelayMs);

        if (request.TransactionRef.Contains("FAIL", StringComparison.Ordinal))
        {
            return new HoldFundsResponse
            {
                Success = false,
                ErrorCode = "TELEBIRR_HOLD_FAILED",
                ErrorMessage = "Simulated hold failure for testing."
            };
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new HoldFundsResponse
        {
            Success = true,
            TelebirrRef = $"MOCK-HOLD-{request.TransactionRef}-{timestamp}"
        };
    }

    public async Task<ReleaseFundsResponse> ReleaseFundsAsync(ReleaseFundsRequest request)
    {
        if (request.TransactionRef.Contains("TIMEOUT", StringComparison.Ordinal))
        {
            await Task.Delay(SimulatedTimeoutMs);
        }

        await Task.Delay(SimulatedDelayMs);

        if (request.TransactionRef.Contains("FAIL", StringComparison.Ordinal))
        {
            return new ReleaseFundsResponse
            {
                Success = false,
                ErrorCode = "TELEBIRR_RELEASE_FAILED",
                ErrorMessage = "Simulated release failure for testing."
            };
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new ReleaseFundsResponse
        {
            Success = true,
            TelebirrRef = $"MOCK-RELEASE-{request.TransactionRef}-{timestamp}"
        };
    }

    public async Task<RefundFundsResponse> RefundFundsAsync(RefundFundsRequest request)
    {
        if (request.TransactionRef.Contains("TIMEOUT", StringComparison.Ordinal))
        {
            await Task.Delay(SimulatedTimeoutMs);
        }

        await Task.Delay(SimulatedDelayMs);

        if (request.TransactionRef.Contains("FAIL", StringComparison.Ordinal))
        {
            return new RefundFundsResponse
            {
                Success = false,
                ErrorCode = "TELEBIRR_REFUND_FAILED",
                ErrorMessage = "Simulated refund failure for testing."
            };
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new RefundFundsResponse
        {
            Success = true,
            TelebirrRef = $"MOCK-REFUND-{request.TransactionRef}-{timestamp}"
        };
    }

    public async Task<TransactionStatusResponse> GetTransactionStatusAsync(string transactionRef)
    {
        await Task.Delay(SimulatedDelayMs);

        if (transactionRef.Contains("FAIL", StringComparison.Ordinal))
        {
            return new TransactionStatusResponse
            {
                TransactionRef = transactionRef,
                Status = "failed",
                Amount = 0,
                UpdatedAt = DateTime.UtcNow.ToString("o")
            };
        }

        return new TransactionStatusResponse
        {
            TransactionRef = transactionRef,
            Status = "completed",
            TelebirrRef = $"MOCK-STATUS-{transactionRef}",
            Amount = 0,
            UpdatedAt = DateTime.UtcNow.ToString("o")
        };
    }

    /// <summary>The mock holds synchronously; mock orders are funded at payment submit.</summary>
    public bool SupportsHostedCheckout => false;

    /// <summary>The mock never reaches Telebirr (FUP-36).</summary>
    public bool IsLiveTelebirr => false;

    /// <summary>No hosted checkout, so no URL to rebuild.</summary>
    public string? BuildCheckoutUrl(string prepayId) => null;
}
