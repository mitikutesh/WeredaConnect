using System.Globalization;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FRD-13 (escrow-protocol.md 1.33.0): confirming a delivery stamps the escrow's <c>releaseNotBefore</c> in the
/// same write as <c>deliveryConfirmedAt</c> and <c>disputeWindowEndsAt</c>. Trusted payees are paid when the
/// 48 hour window ends; a flagged trucker (named by the delivery when the escrow has none yet) makes the escrow
/// wait 72 hours from the confirm. Uses the delivery rig of <see cref="SyncFunctionTests"/>.
/// </summary>
public partial class SyncFunctionTests
{
    private readonly IFraudDetectionService _payeeScores = Substitute.For<IFraudDetectionService>();
    private readonly IUserRepository _payeeUsers = Substitute.For<IUserRepository>();
    private readonly IEscrowRepository _payeeEscrows = Substitute.For<IEscrowRepository>();

    /// <summary>Old, clean, experienced payees: trusted unless a test says otherwise.</summary>
    private PayoutDelayPolicy TrustingPayoutDelay()
    {
        _payeeScores.GetUserRiskScoreAsync(Arg.Any<string>())
            .Returns(new RiskScoreResult { Score = 0, EnforcementScore = 0, Tier = RiskTier.Low });
        _payeeUsers.GetUserByIdAsync(Arg.Any<string>()).Returns(ci => new UserDto
        {
            UserId = ci.ArgAt<string>(0),
            PhoneNumber = "+251900000001",
            Role = "farmer",
            Woreda = "w",
            IsPhoneVerified = true,
            CreatedAt = "2025-01-01T00:00:00Z",
            UpdatedAt = "2025-01-01T00:00:00Z",
            Version = 1
        });
        var history = (Enumerable.Range(0, 3)
                .Select(i => new Dictionary<string, object> { { "escrowId", $"done-{i}" }, { "status", "completed" } })
                .ToList(),
            (Dictionary<string, AttributeValue>?)null);
        _payeeEscrows.QueryByFarmerAsync(Arg.Any<string>(), "completed", 50, null).Returns(history);
        _payeeEscrows.QueryByTruckerAsync(Arg.Any<string>(), "completed", 50, null).Returns(history);
        return new PayoutDelayPolicy(_payeeScores, _payeeUsers, _payeeEscrows, PayoutDelaySettings.Default);
    }

    /// <summary>The buyer confirms del-001 (trucker-001 on the delivery); returns the escrow stamp's fields.</summary>
    private async Task<Dictionary<string, object>> ConfirmDeliveryWithEscrowAsync(
        PayoutDelayPolicy? policy, bool truckerOnEscrow)
    {
        var escrowRepo = Substitute.For<IEscrowRepository>();
        var escrow = new Dictionary<string, object>
        {
            { "escrowId", "esc-001" },
            { "createdAt", "2026-02-16T10:00:00Z" },
            { "status", "escrowed" },
            { "buyerId", "buyer-001" },
            { "farmerId", "farmer-001" },
            { "farmerPayout", 7_300m },
            { "truckerPayout", 500m }
        };
        if (truckerOnEscrow)
        {
            escrow["truckerId"] = "trucker-001";
        }

        escrowRepo.GetByDeliveryIdAsync("del-001").Returns(escrow);
        var function = new SyncFunction(
            _mockSyncMeta, _mockCropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table",
            userRepo: _mockUserRepo, escrowRepo: escrowRepo)
        {
            PayoutDelay = policy
        };
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pickedUp", 1, "nonce-delay");

        var response = await function.FunctionHandler(MakeAuthenticatedRequest(MakeSyncRequest(new SyncOperationRequest
        {
            OperationId = "op-deliver-delay",
            IdempotencyKey = "buyer-001:delivery:del-001:update:deliver-delay",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "delivered" },
                { "version", 1 },
                { "scannedQrNonce", "nonce-delay" }
            }
        }), "buyer-001", "buyer"), _context);

        Assert.Contains("success", response.Body);
        var stamp = escrowRepo.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IEscrowRepository.UpdateStatusAsync));
        Assert.Equal("esc-001", stamp.GetArguments()[0]);
        Assert.Equal("escrowed", stamp.GetArguments()[3]);
        return (Dictionary<string, object>)stamp.GetArguments()[4]!;
    }

    private static DateTime Utc(object iso)
        => DateTime.Parse((string)iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);

    [Fact]
    public async Task DeliveryConfirmed_TrustedPayees_ReleaseNotBeforeIsTheWindowEnd()
    {
        var fields = await ConfirmDeliveryWithEscrowAsync(TrustingPayoutDelay(), truckerOnEscrow: true);

        Assert.Equal(Utc(fields["disputeWindowEndsAt"]), Utc(fields[PayoutDelayPolicy.ReleaseNotBeforeField]));
        Assert.Equal(Utc(fields["deliveryConfirmedAt"]).AddHours(48), Utc(fields[PayoutDelayPolicy.ReleaseNotBeforeField]));
        Assert.Contains("PAYOUT_DELAY_SET escrowId=esc-001", ((TestLambdaLogger)_context.Logger).Buffer.ToString());
        Assert.Contains("hours=24 farmer=trusted trucker=trusted", ((TestLambdaLogger)_context.Logger).Buffer.ToString());
    }

    [Fact]
    public async Task DeliveryConfirmed_FlaggedTruckerNamedByTheDelivery_Waits72Hours()
    {
        var policy = TrustingPayoutDelay();
        _payeeScores.GetUserRiskScoreAsync("trucker-001")
            .Returns(new RiskScoreResult { Score = 65, EnforcementScore = 65, Tier = RiskTier.High });

        var fields = await ConfirmDeliveryWithEscrowAsync(policy, truckerOnEscrow: false);

        Assert.Equal(Utc(fields["deliveryConfirmedAt"]).AddHours(72), Utc(fields[PayoutDelayPolicy.ReleaseNotBeforeField]));
        await _payeeScores.Received(1).GetUserRiskScoreAsync("trucker-001");
        Assert.Contains("farmer=trusted trucker=flagged", ((TestLambdaLogger)_context.Logger).Buffer.ToString());
    }

    [Fact]
    public async Task DeliveryConfirmed_UnscoredFarmer_GetsTheLongestDelay_StillStamped()
    {
        var policy = TrustingPayoutDelay();
        _payeeScores.GetUserRiskScoreAsync("farmer-001").Returns(RiskScoreResult.Unscored(RiskScoreSource.Unavailable));

        var fields = await ConfirmDeliveryWithEscrowAsync(policy, truckerOnEscrow: true);

        Assert.True(fields.ContainsKey("deliveryConfirmedAt"));
        Assert.Equal(Utc(fields["deliveryConfirmedAt"]).AddHours(72), Utc(fields[PayoutDelayPolicy.ReleaseNotBeforeField]));
    }

    [Fact]
    public async Task DeliveryConfirmed_WithoutThePolicy_StampsAsBefore()
    {
        var fields = await ConfirmDeliveryWithEscrowAsync(policy: null, truckerOnEscrow: true);

        Assert.Equal(new[] { "deliveryConfirmedAt", "disputeWindowEndsAt" }, fields.Keys.OrderBy(k => k));
    }
}
