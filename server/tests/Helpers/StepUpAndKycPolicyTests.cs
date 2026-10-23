using Amazon.Lambda.APIGatewayEvents;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

public class StepUpPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BelowThreshold_NeverRequiresFreshAuth()
    {
        Assert.Null(StepUpPolicy.Check(authTime: null, amountEtb: 9_999.99m, now: Now));
        Assert.Null(StepUpPolicy.Check(authTime: Now.AddDays(-30).ToUnixTimeSeconds(), amountEtb: 500m, now: Now));
    }

    [Fact]
    public void AtThreshold_MissingAuthTime_IsRejected()
    {
        var error = StepUpPolicy.Check(authTime: null, amountEtb: 10_000m, now: Now);

        Assert.NotNull(error);
        Assert.StartsWith("AUTH_040:", error);
    }

    [Fact]
    public void AtThreshold_StaleAuthTime_IsRejected()
    {
        var stale = Now.AddSeconds(-StepUpPolicy.DefaultMaxAgeSeconds - 1).ToUnixTimeSeconds();

        Assert.StartsWith("AUTH_040:", StepUpPolicy.Check(stale, 25_000m, Now));
    }

    [Fact]
    public void AtThreshold_FreshAuthTime_IsAllowed()
    {
        var fresh = Now.AddSeconds(-StepUpPolicy.DefaultMaxAgeSeconds + 5).ToUnixTimeSeconds();

        Assert.Null(StepUpPolicy.Check(fresh, 25_000m, Now));
    }

    [Fact]
    public void RequestOverload_ReadsAuthTimeClaim()
    {
        var request = new APIGatewayProxyRequest
        {
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext
                {
                    { "claims", new Dictionary<string, object> { { "auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() } } }
                }
            }
        };

        Assert.True(StepUpPolicy.IsFresh(request));
        Assert.Null(StepUpPolicy.Check(request, 50_000m));
    }

    [Fact]
    public void AccessTokenWithoutAuthTime_IsNeverFresh()
    {
        var request = new APIGatewayProxyRequest
        {
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "user-1" } }
            }
        };

        Assert.False(StepUpPolicy.IsFresh(request));
        Assert.Null(AuthHelper.GetAuthTime(request));
    }

    // Payment tiers: password up to 10,000 ETB (AUTH_040), Fayda above (AUTH_041)

    private static string FaydaAt(int secondsAgo) => Now.AddSeconds(-secondsAgo).ToString("o");

    [Fact]
    public void Payment_SmallAmount_NeedsFreshPassword()
    {
        var fresh = Now.AddSeconds(-10).ToUnixTimeSeconds();
        var stale = Now.AddSeconds(-StepUpPolicy.DefaultMaxAgeSeconds - 1).ToUnixTimeSeconds();

        Assert.Null(StepUpPolicy.CheckPayment(fresh, null, 500m, Now));
        Assert.StartsWith("AUTH_040:", StepUpPolicy.CheckPayment(stale, null, 500m, Now));
        Assert.StartsWith("AUTH_040:", StepUpPolicy.CheckPayment(null, null, 500m, Now));
    }

    [Fact]
    public void Payment_SmallAmount_FaydaLoginDoesNotReplaceThePassword()
    {
        Assert.StartsWith("AUTH_040:", StepUpPolicy.CheckPayment(null, FaydaAt(10), 9_000m, Now));
    }

    [Fact]
    public void Payment_Exactly10K_IsThePasswordTier()
    {
        Assert.StartsWith("AUTH_040:", StepUpPolicy.CheckPayment(null, FaydaAt(10), 10_000m, Now));
        Assert.Null(StepUpPolicy.CheckPayment(Now.ToUnixTimeSeconds(), null, 10_000m, Now));
    }

    [Fact]
    public void Payment_Above10K_NeedsRecentFaydaLogin_NotAPassword()
    {
        var freshPassword = Now.ToUnixTimeSeconds();

        Assert.StartsWith("AUTH_041:", StepUpPolicy.CheckPayment(freshPassword, null, 10_000.01m, Now));
        Assert.StartsWith("AUTH_041:", StepUpPolicy.CheckPayment(freshPassword,
            FaydaAt(StepUpPolicy.DefaultMaxAgeSeconds + 1), 25_000m, Now));
        Assert.StartsWith("AUTH_041:", StepUpPolicy.CheckPayment(freshPassword, "not-a-date", 25_000m, Now));
        Assert.Null(StepUpPolicy.CheckPayment(null, FaydaAt(StepUpPolicy.DefaultMaxAgeSeconds - 5), 25_000m, Now));
    }

    [Fact]
    public void CodeOf_ReadsTheMessagePrefix()
    {
        Assert.Equal("AUTH_041", StepUpPolicy.CodeOf(StepUpPolicy.CheckPayment(null, null, 50_000m, Now)!));
        Assert.Equal("AUTH_040", StepUpPolicy.CodeOf(StepUpPolicy.CheckPayment(null, null, 50m, Now)!));
    }

    // FRD-04: the buyer's risk tier (fraud-detection-architecture.md section 17)

    private static long FreshPassword => Now.AddSeconds(-10).ToUnixTimeSeconds();
    private static long StalePassword => Now.AddSeconds(-StepUpPolicy.DefaultMaxAgeSeconds - 1).ToUnixTimeSeconds();

    [Theory]
    [InlineData(500)]
    [InlineData(10_000)]
    [InlineData(25_000)]
    public void Payment_LowTier_IsTheAmountTierAsBefore(int amount)
    {
        foreach (var (authTime, fayda) in new (long?, string?)[]
                 {
                     (null, null), (FreshPassword, null), (StalePassword, FaydaAt(10)), (FreshPassword, FaydaAt(10)),
                     (null, FaydaAt(StepUpPolicy.DefaultMaxAgeSeconds + 1))
                 })
        {
            Assert.Equal(
                StepUpPolicy.CheckPayment(authTime, fayda, amount, Now),
                StepUpPolicy.CheckPayment(authTime, fayda, amount, Now, RiskTier.Low));
        }
    }

    [Theory]
    [InlineData(500)]
    [InlineData(10_000)]
    public void Payment_MediumTier_UpTo10K_IsThePasswordAsForLow(int amount)
    {
        Assert.Null(StepUpPolicy.CheckPayment(FreshPassword, null, amount, Now, RiskTier.Medium));
        Assert.StartsWith("AUTH_040:", StepUpPolicy.CheckPayment(StalePassword, FaydaAt(10), amount, Now, RiskTier.Medium));
    }

    [Fact]
    public void Payment_MediumTier_Above10K_NeedsTheFaydaLoginAndThenThePassword()
    {
        Assert.StartsWith("AUTH_041:", StepUpPolicy.CheckPayment(FreshPassword, null, 25_000m, Now, RiskTier.Medium));
        // The Fayda login that alone pays a LOW buyer above 10,000 ETB is not enough at MEDIUM.
        Assert.Null(StepUpPolicy.CheckPayment(null, FaydaAt(10), 25_000m, Now));
        Assert.StartsWith("AUTH_040:", StepUpPolicy.CheckPayment(null, FaydaAt(10), 25_000m, Now, RiskTier.Medium));
        Assert.Null(StepUpPolicy.CheckPayment(FreshPassword, FaydaAt(10), 25_000m, Now, RiskTier.Medium));
    }

    [Theory]
    [InlineData(500, RiskTier.High)]
    [InlineData(10_000, RiskTier.High)]
    [InlineData(25_000, RiskTier.High)]
    [InlineData(500, RiskTier.Critical)]
    public void Payment_HighTier_AtAnyAmount_NeedsTheFaydaLoginFirst_ThenThePassword(int amount, RiskTier tier)
    {
        // Fayda first: a fresh password alone gets AUTH_041, a stale Fayda login too.
        Assert.StartsWith("AUTH_041:", StepUpPolicy.CheckPayment(FreshPassword, null, amount, Now, tier));
        Assert.StartsWith("AUTH_041:", StepUpPolicy.CheckPayment(FreshPassword,
            FaydaAt(StepUpPolicy.DefaultMaxAgeSeconds + 1), amount, Now, tier));
        // Then the password, on the retry after the Fayda login.
        Assert.StartsWith("AUTH_040:", StepUpPolicy.CheckPayment(StalePassword, FaydaAt(10), amount, Now, tier));
        Assert.StartsWith("AUTH_040:", StepUpPolicy.CheckPayment(null, FaydaAt(10), amount, Now, tier));
        Assert.Null(StepUpPolicy.CheckPayment(FreshPassword, FaydaAt(10), amount, Now, tier));
    }

    [Fact]
    public void PaymentNeedsFayda_AboveTheThreshold_OrFromHigh()
    {
        Assert.False(StepUpPolicy.PaymentNeedsFayda(10_000m));
        Assert.True(StepUpPolicy.PaymentNeedsFayda(10_000.01m));
        Assert.False(StepUpPolicy.PaymentNeedsFayda(500m, RiskTier.Medium));
        Assert.True(StepUpPolicy.PaymentNeedsFayda(500m, RiskTier.High));
        Assert.True(StepUpPolicy.PaymentNeedsFayda(500m, RiskTier.Critical));
    }

    [Fact]
    public void FaydaMessage_NamesNoAmount()
    {
        // Section 17: the server may ask for Fayda at any amount, so the text cannot name the threshold.
        Assert.DoesNotContain("10,000", StepUpPolicy.FaydaErrorMessage);
        Assert.DoesNotContain("ETB", StepUpPolicy.FaydaErrorMessage);
        Assert.StartsWith("Confirm it's you with Fayda", StepUpPolicy.FaydaErrorMessage);
    }
}

/// <summary>FRD-04: the tier a user gate acts on (fraud-detection-architecture.md 14.4, 17.1).</summary>
public class RiskGatePolicyTests
{
    [Theory]
    [InlineData(RiskTier.Low)]
    [InlineData(RiskTier.Medium)]
    [InlineData(RiskTier.High)]
    [InlineData(RiskTier.Critical)]
    public void AComputedScore_GivesItsTier(RiskTier tier)
    {
        Assert.Equal(tier, RiskGatePolicy.TierAtUserGate(
            new RiskScoreResult { Score = 1, EnforcementScore = 1, Tier = tier }));
    }

    [Fact]
    public void ATruncatedRead_IsComputedAndCritical()
    {
        Assert.Equal(RiskTier.Critical, RiskGatePolicy.TierAtUserGate(
            new RiskScoreResult { Score = 0, EnforcementScore = 0, Tier = RiskTier.Critical, Truncated = true }));
    }

    [Theory]
    [InlineData(RiskScoreSource.TableNotConfigured)]
    [InlineData(RiskScoreSource.Unavailable)]
    public void AnUnscoredResult_IsLowAtAUserGate_WhateverItsTier(RiskScoreSource source)
    {
        Assert.Equal(RiskTier.Low, RiskGatePolicy.TierAtUserGate(RiskScoreResult.Unscored(source)));
        Assert.Equal(RiskTier.Low, RiskGatePolicy.TierAtUserGate(
            new RiskScoreResult { Score = 200, EnforcementScore = 200, Tier = RiskTier.Critical, Source = source }));
    }

    [Fact]
    public void TheSyncMessage_IsTheCodeThenTheContractText()
    {
        Assert.Equal(
            "FRAUD_ACCOUNT_HELD: Your account is under review. Some actions are paused. Contact support.",
            RiskGatePolicy.SyncMessage);
    }
}

public class KycPolicyTests
{
    private static UserDto User(bool phone, bool fayda) => new()
    {
        UserId = "u",
        PhoneNumber = "+251900000000",
        Role = "buyer",
        Woreda = "Adama",
        IsPhoneVerified = phone,
        FaydaVerified = fayda,
        CreatedAt = "2026-09-25T10:00:00Z",
        UpdatedAt = "2026-09-25T10:00:00Z",
        Version = 1
    };

    [Fact]
    public void MissingBuyer_FailsClosed()
    {
        Assert.StartsWith("ORDER_KYC_REQUIRED:", KycPolicy.CheckPayment(null, User(true, true), 100m));
    }

    [Fact]
    public void MissingFarmer_FailsClosed()
    {
        Assert.StartsWith("ORDER_KYC_REQUIRED:", KycPolicy.CheckPayment(User(true, true), null, 100m));
    }

    [Fact]
    public void UnverifiedPhones_AreRejectedAtAnyAmount()
    {
        Assert.Contains("phone", KycPolicy.CheckPayment(User(false, true), User(true, true), 50m)!);
        Assert.Contains("farmer", KycPolicy.CheckPayment(User(true, true), User(false, true), 50m)!);
    }

    [Fact]
    public void SmallAmount_NeedsOnlyPhones()
    {
        Assert.Null(KycPolicy.CheckPayment(User(true, false), User(true, false), KycPolicy.FaydaThresholdEtb));
    }

    [Fact]
    public void AboveThreshold_NeedsBothFayda()
    {
        var amount = KycPolicy.FaydaThresholdEtb + 0.01m;

        Assert.Contains("Fayda", KycPolicy.CheckPayment(User(true, false), User(true, true), amount)!);
        Assert.Contains("farmer", KycPolicy.CheckPayment(User(true, true), User(true, false), amount)!);
        Assert.Null(KycPolicy.CheckPayment(User(true, true), User(true, true), amount));
    }

    [Fact]
    public void Rejection_NamesWhoseVerificationIsMissing()
    {
        var amount = KycPolicy.FaydaThresholdEtb + 1m;

        Assert.StartsWith("ORDER_KYC_REQUIRED: buyer_phone:", KycPolicy.CheckPayment(User(false, true), User(true, true), amount));
        Assert.StartsWith("ORDER_KYC_REQUIRED: farmer_phone:", KycPolicy.CheckPayment(User(true, true), User(false, true), amount));
        Assert.StartsWith("ORDER_KYC_REQUIRED: buyer_fayda:", KycPolicy.CheckPayment(User(true, false), User(true, true), amount));
        Assert.StartsWith("ORDER_KYC_REQUIRED: farmer_fayda:", KycPolicy.CheckPayment(User(true, true), User(true, false), amount));
    }
}
