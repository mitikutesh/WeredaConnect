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
