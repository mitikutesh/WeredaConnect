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
}
