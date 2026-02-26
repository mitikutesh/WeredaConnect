using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

public class MockTelebirrGatewayTests
{
    private readonly MockTelebirrGateway _gateway = new();

    [Fact]
    public async Task HoldFundsAsync_Success_ReturnsMockHoldRef()
    {
        var request = new HoldFundsRequest
        {
            TransactionRef = "buyer:delivery:initiate:12345",
            BuyerPhone = "+251912345678",
            Amount = 15_000m,
            Description = "Test hold"
        };

        var response = await _gateway.HoldFundsAsync(request);

        Assert.True(response.Success);
        Assert.NotNull(response.TelebirrRef);
        Assert.StartsWith("MOCK-HOLD-", response.TelebirrRef);
        Assert.Null(response.ErrorCode);
    }

    [Fact]
    public async Task HoldFundsAsync_WithFail_ReturnsFailure()
    {
        var request = new HoldFundsRequest
        {
            TransactionRef = "buyer:delivery:FAIL:12345",
            BuyerPhone = "+251912345678",
            Amount = 15_000m,
            Description = "Test hold failure"
        };

        var response = await _gateway.HoldFundsAsync(request);

        Assert.False(response.Success);
        Assert.Equal("TELEBIRR_HOLD_FAILED", response.ErrorCode);
        Assert.NotNull(response.ErrorMessage);
    }

    [Fact]
    public async Task ReleaseFundsAsync_Success_ReturnsMockReleaseRef()
    {
        var request = new ReleaseFundsRequest
        {
            TransactionRef = "system:escrow:release:12345",
            HoldRef = "MOCK-HOLD-REF",
            FarmerPhone = "+251922222222",
            Amount = 14_625m,
            PlatformCommission = 375m
        };

        var response = await _gateway.ReleaseFundsAsync(request);

        Assert.True(response.Success);
        Assert.NotNull(response.TelebirrRef);
        Assert.StartsWith("MOCK-RELEASE-", response.TelebirrRef);
    }

    [Fact]
    public async Task RefundFundsAsync_Success_ReturnsMockRefundRef()
    {
        var request = new RefundFundsRequest
        {
            TransactionRef = "system:escrow:refund:12345",
            HoldRef = "MOCK-HOLD-REF",
            BuyerPhone = "+251912345678",
            Amount = 15_000m
        };

        var response = await _gateway.RefundFundsAsync(request);

        Assert.True(response.Success);
        Assert.NotNull(response.TelebirrRef);
        Assert.StartsWith("MOCK-REFUND-", response.TelebirrRef);
    }

    [Fact]
    public async Task GetTransactionStatusAsync_Success_ReturnsCompleted()
    {
        var response = await _gateway.GetTransactionStatusAsync("ref-123");

        Assert.Equal("completed", response.Status);
        Assert.Equal("ref-123", response.TransactionRef);
        Assert.NotNull(response.TelebirrRef);
    }

    [Fact]
    public async Task GetTransactionStatusAsync_WithFail_ReturnsFailed()
    {
        var response = await _gateway.GetTransactionStatusAsync("FAIL-ref-123");

        Assert.Equal("failed", response.Status);
        Assert.Null(response.TelebirrRef);
    }

    // ──────────────────────────────────────────────
    // FAIL flag tests for Release and Refund
    // ──────────────────────────────────────────────

    [Fact]
    public async Task ReleaseFundsAsync_WithFail_ReturnsFailure()
    {
        var request = new ReleaseFundsRequest
        {
            TransactionRef = "system:escrow:FAIL:12345",
            HoldRef = "MOCK-HOLD-REF",
            FarmerPhone = "+251922222222",
            Amount = 14_625m,
            PlatformCommission = 375m
        };

        var response = await _gateway.ReleaseFundsAsync(request);

        Assert.False(response.Success);
        Assert.Equal("TELEBIRR_RELEASE_FAILED", response.ErrorCode);
        Assert.NotNull(response.ErrorMessage);
    }

    [Fact]
    public async Task RefundFundsAsync_WithFail_ReturnsFailure()
    {
        var request = new RefundFundsRequest
        {
            TransactionRef = "system:escrow:FAIL:12345",
            HoldRef = "MOCK-HOLD-REF",
            BuyerPhone = "+251912345678",
            Amount = 15_000m
        };

        var response = await _gateway.RefundFundsAsync(request);

        Assert.False(response.Success);
        Assert.Equal("TELEBIRR_REFUND_FAILED", response.ErrorCode);
        Assert.NotNull(response.ErrorMessage);
    }

    // ──────────────────────────────────────────────
    // Deterministic ref format tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task HoldFundsAsync_RefFormat_FollowsMockHoldPattern()
    {
        var request = new HoldFundsRequest
        {
            TransactionRef = "buyer:delivery:initiate:99999",
            BuyerPhone = "+251912345678",
            Amount = 1_000m,
            Description = "Test"
        };

        var response = await _gateway.HoldFundsAsync(request);

        Assert.True(response.Success);
        // Format: MOCK-HOLD-{transactionRef}-{timestamp}
        Assert.StartsWith("MOCK-HOLD-buyer:delivery:initiate:99999-", response.TelebirrRef);
    }

    [Fact]
    public async Task ReleaseFundsAsync_RefFormat_FollowsMockReleasePattern()
    {
        var request = new ReleaseFundsRequest
        {
            TransactionRef = "system:escrow:release:99999",
            HoldRef = "MOCK-HOLD-REF",
            FarmerPhone = "+251922222222",
            Amount = 14_625m,
            PlatformCommission = 375m
        };

        var response = await _gateway.ReleaseFundsAsync(request);

        Assert.True(response.Success);
        Assert.StartsWith("MOCK-RELEASE-system:escrow:release:99999-", response.TelebirrRef);
    }

    [Fact]
    public async Task RefundFundsAsync_RefFormat_FollowsMockRefundPattern()
    {
        var request = new RefundFundsRequest
        {
            TransactionRef = "system:escrow:refund:99999",
            HoldRef = "MOCK-HOLD-REF",
            BuyerPhone = "+251912345678",
            Amount = 15_000m
        };

        var response = await _gateway.RefundFundsAsync(request);

        Assert.True(response.Success);
        Assert.StartsWith("MOCK-REFUND-system:escrow:refund:99999-", response.TelebirrRef);
    }

    // ──────────────────────────────────────────────
    // GetTransactionStatus ref format
    // ──────────────────────────────────────────────

    [Fact]
    public async Task GetTransactionStatusAsync_Success_RefFormat()
    {
        var response = await _gateway.GetTransactionStatusAsync("ref-abc");

        Assert.Equal("completed", response.Status);
        Assert.Equal("MOCK-STATUS-ref-abc", response.TelebirrRef);
    }
}
