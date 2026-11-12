using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// SIM-37: the user entity handler in SyncFunction, which syncs the language
/// chosen in Settings to the caller's own profile. SIM-56: the same entity edits
/// displayName, woreda and a company buyer's companyName (FUP-52 phone screen),
/// and GET /user/profile reads the profile.
/// </summary>
public class SyncFunctionUserProfileTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ISyncMetadataRepository _meta = Substitute.For<ISyncMetadataRepository>();
    private readonly ICropRepository _crop = Substitute.For<ICropRepository>();
    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly SyncFunction _fn;
    private readonly TestLambdaContext _ctx = new();

    public SyncFunctionUserProfileTests()
    {
        _meta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        // UpdateUserAsync echoes the saved user with the version bumped, like the real repo.
        _users.UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>())
            .Returns(ci => AuthResult<UserDto>.Success(ci.Arg<UserDto>() with { Version = ci.ArgAt<int>(1) + 1 }));
        _fn = new SyncFunction(_meta, _crop, _ddb, "trips", "deliveries", userRepo: _users);
    }

    private static UserDto User(string id, string language, int version = 3) => new()
    {
        UserId = id,
        PhoneNumber = "+251900100001",
        Role = "farmer",
        Woreda = "Test Woreda",
        PreferredLanguage = language,
        IsPhoneVerified = true,
        CreatedAt = "2026-01-01T00:00:00Z",
        UpdatedAt = "2026-01-01T00:00:00Z",
        Version = version,
    };

    private void StubUser(UserDto user) =>
        _users.GetUserByIdAsync(Arg.Any<string>(), Arg.Any<bool>()).Returns(user);

    private static APIGatewayProxyRequest Req(SyncOperationRequest op, string userId, string role = "farmer")
    {
        var authorizer = new APIGatewayCustomAuthorizerContext
        {
            { "userId", userId },
            { "custom:role", role },
        };
        var body = new SyncRequest
        {
            SyncBatchId = "b1",
            ClientId = "d1",
            Operations = new List<SyncOperationRequest> { op },
        };
        return new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(body, Json),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer },
        };
    }

    private static SyncOperationRequest UserOp(
        string userId,
        string action = "update",
        Dictionary<string, object>? data = null)
    {
        return new SyncOperationRequest
        {
            OperationId = $"op-{action}",
            IdempotencyKey = $"{userId}:user:{userId}:{action}:1",
            EntityType = "user",
            EntityId = userId,
            Action = action,
            Data = data ?? new Dictionary<string, object> { { "preferredLanguage", "om" } },
            Timestamp = "2026-07-07T10:00:00Z",
        };
    }

    [Fact]
    public async Task ChangingLanguage_UpdatesProfile_AndReturnsSuccess()
    {
        StubUser(User("farmer-1", "am", version: 3));

        var response = await _fn.FunctionHandler(Req(UserOp("farmer-1"), "farmer-1"), _ctx);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("success", response.Body);
        await _users.Received(1).UpdateUserAsync(
            Arg.Is<UserDto>(u => u.PreferredLanguage == "om"), 3);
    }

    [Fact]
    public async Task UnknownLanguageCode_IsRejected()
    {
        StubUser(User("farmer-1", "am"));

        var response = await _fn.FunctionHandler(
            Req(UserOp("farmer-1", data: new Dictionary<string, object> { { "preferredLanguage", "fr" } }),
                "farmer-1"),
            _ctx);

        Assert.Contains("USER_PROFILE_VALIDATION", response.Body);
        await _users.DidNotReceive().UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>());
    }

    [Fact]
    public async Task MissingPreferredLanguage_IsRejected()
    {
        StubUser(User("farmer-1", "am"));

        var response = await _fn.FunctionHandler(
            Req(UserOp("farmer-1", data: new Dictionary<string, object>()), "farmer-1"),
            _ctx);

        Assert.Contains("USER_PROFILE_VALIDATION", response.Body);
        await _users.DidNotReceive().UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>());
    }

    [Fact]
    public async Task UpdatingAnotherUsersProfile_IsRejected()
    {
        StubUser(User("farmer-1", "am"));

        // entityId is someone else's id; the caller is farmer-1.
        var response = await _fn.FunctionHandler(Req(UserOp("farmer-2"), "farmer-1"), _ctx);

        Assert.Contains("USER_PROFILE_UNAUTHORIZED", response.Body);
        await _users.DidNotReceive().UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>());
    }

    [Fact]
    public async Task CreateAction_IsRejected()
    {
        StubUser(User("farmer-1", "am"));

        var response = await _fn.FunctionHandler(
            Req(UserOp("farmer-1", action: "create"), "farmer-1"), _ctx);

        Assert.Contains("USER_PROFILE_VALIDATION", response.Body);
        await _users.DidNotReceive().UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>());
    }

    [Fact]
    public async Task SameLanguage_IsNoOp_Success()
    {
        StubUser(User("farmer-1", "om", version: 5));

        var response = await _fn.FunctionHandler(Req(UserOp("farmer-1"), "farmer-1"), _ctx);

        Assert.Contains("success", response.Body);
        await _users.DidNotReceive().UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>());
    }

    [Fact]
    public async Task WithoutUserRepo_ReportsUnavailable()
    {
        var fn = new SyncFunction(_meta, _crop, _ddb, "trips", "deliveries");

        var response = await fn.FunctionHandler(Req(UserOp("farmer-1"), "farmer-1"), _ctx);

        Assert.Contains("USER_PROFILE_UNAVAILABLE", response.Body);
    }

    // ── SIM-56: profile edit ──────────────────────────────────────────

    private const string PhoneRefusalTail = "Contact opens after payment is held in escrow.";

    private static UserDto Buyer(string id, string? buyerType, string? companyName) =>
        User(id, "am") with { Role = "buyer", BuyerType = buyerType, CompanyName = companyName };

    private static Dictionary<string, object> Data(params (string Key, object Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value);

    private async Task<string> EditAsync(string userId, Dictionary<string, object> data, string role = "farmer")
        => (await _fn.FunctionHandler(Req(UserOp(userId, data: data), userId, role), _ctx)).Body;

    [Fact]
    public async Task NameAndWoreda_AreSavedTrimmed_AndTheLanguageStays()
    {
        StubUser(User("farmer-1", "am", version: 4));

        var body = await EditAsync("farmer-1", Data(("displayName", "  Abebe K. "), ("woreda", " Meki ")));

        Assert.Contains("\"status\":\"success\"", body);
        await _users.Received(1).UpdateUserAsync(
            Arg.Is<UserDto>(u => u.DisplayName == "Abebe K." && u.Woreda == "Meki" && u.PreferredLanguage == "am"), 4);
    }

    [Fact]
    public async Task ProtectedFields_InThePayload_AreIgnored()
    {
        StubUser(User("farmer-1", "am") with { FaydaId = "F-1", FaydaVerified = true });

        var body = await EditAsync("farmer-1", Data(
            ("displayName", "Abebe K."),
            ("role", "buyer"),
            ("phoneNumber", "+251911000000"),
            ("faydaId", "F-2"),
            ("faydaVerified", false),
            ("isPhoneVerified", false),
            ("suspended", true),
            ("buyerType", "company"),
            ("version", 99)));

        Assert.Contains("\"status\":\"success\"", body);
        await _users.Received(1).UpdateUserAsync(
            Arg.Is<UserDto>(u => u.DisplayName == "Abebe K."
                && u.Role == "farmer"
                && u.PhoneNumber == "+251900100001"
                && u.FaydaId == "F-1"
                && u.FaydaVerified
                && u.IsPhoneVerified
                && !u.Suspended
                && u.BuyerType == null),
            3);
    }

    [Fact]
    public async Task CaseVariantKeys_AreNotFieldsEither()
    {
        // Keys match exactly: "Role", "DisplayName" and the like are unknown keys, so ignored.
        StubUser(User("farmer-1", "am") with { FaydaId = "F-1" });

        var body = await EditAsync("farmer-1", Data(
            ("woreda", "Meki"),
            ("Role", "buyer"),
            ("ROLE", "admin"),
            ("PhoneNumber", "+251911000000"),
            ("FaydaId", "F-2"),
            ("Suspended", true),
            ("BuyerType", "company"),
            ("CompanyName", "Acme"),
            ("DisplayName", "Call 0911223344")));

        Assert.Contains("\"status\":\"success\"", body);
        await _users.Received(1).UpdateUserAsync(
            Arg.Is<UserDto>(u => u.Woreda == "Meki"
                && u.DisplayName == null
                && u.Role == "farmer"
                && u.PhoneNumber == "+251900100001"
                && u.FaydaId == "F-1"
                && !u.Suspended
                && u.BuyerType == null
                && u.CompanyName == null),
            3);
    }

    [Fact]
    public async Task PhoneNumberInTheName_IsRefused_WithTheRegistrationCodeAndText()
    {
        StubUser(User("farmer-1", "am"));

        var body = await EditAsync("farmer-1", Data(("displayName", "Abebe 0911 22 33 44"), ("preferredLanguage", "om")));

        Assert.Contains($"AUTH_027: Remove the phone number from displayName. {PhoneRefusalTail}", body);
        await _users.DidNotReceive().UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>());
    }

    [Fact]
    public async Task PhoneNumbersInWoredaAndCompanyName_NameBothFields()
    {
        StubUser(Buyer("buyer-1", "company", "Old Traders"));

        var body = await EditAsync("buyer-1",
            Data(("woreda", "Adama +251 911 223344"), ("companyName", "Traders 0711223344")), role: "buyer");

        Assert.Contains($"AUTH_027: Remove the phone number from woreda, companyName. {PhoneRefusalTail}", body);
        await _users.DidNotReceive().UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>());
    }

    [Fact]
    public async Task StoredValueWithAPhoneNumber_SentBackUnchanged_IsAnEcho()
    {
        // Stored before FUP-52: not re-validated. Sending it back is no change.
        StubUser(User("farmer-1", "am") with { DisplayName = "Abebe 0911223344" });

        var body = await EditAsync("farmer-1", Data(("displayName", "Abebe 0911223344"), ("woreda", "Meki")));

        Assert.Contains("\"status\":\"success\"", body);
        await _users.Received(1).UpdateUserAsync(
            Arg.Is<UserDto>(u => u.DisplayName == "Abebe 0911223344" && u.Woreda == "Meki"), 3);
    }

    [Fact]
    public async Task NothingChanged_IsANoOpSuccess()
    {
        StubUser(User("farmer-1", "am", version: 7) with { DisplayName = "Abebe" });

        var body = await EditAsync("farmer-1", Data(("displayName", " Abebe "), ("woreda", "Test Woreda")));

        Assert.Contains("\"status\":\"success\"", body);
        Assert.Contains("\"serverVersion\":7", body);
        await _users.DidNotReceive().UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>());
    }

    [Theory]
    [InlineData("displayName", "   ", "displayName must not be empty.")]
    [InlineData("woreda", "", "woreda must not be empty.")]
    [InlineData("displayName", "Deleted user", "This display name is not available.")]
    public async Task FieldRules_RefuseTheEdit(string field, string value, string expected)
    {
        StubUser(User("farmer-1", "am"));

        var body = await EditAsync("farmer-1", Data((field, value)));

        Assert.Contains($"USER_PROFILE_VALIDATION: {expected}", body);
        await _users.DidNotReceive().UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>());
    }

    [Fact]
    public async Task OverLongWoreda_IsRefused()
    {
        StubUser(User("farmer-1", "am"));

        var body = await EditAsync("farmer-1", Data(("woreda", new string('a', 101))));

        Assert.Contains("USER_PROFILE_VALIDATION: woreda must not exceed 100 characters.", body);
        await _users.DidNotReceive().UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>());
    }

    [Fact]
    public async Task ANameThatIsNotAString_IsRefused()
    {
        StubUser(User("farmer-1", "am"));

        var body = await EditAsync("farmer-1", Data(("displayName", 42)));

        Assert.Contains("USER_PROFILE_VALIDATION: displayName must be a string.", body);
        await _users.DidNotReceive().UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>());
    }

    [Fact]
    public async Task CompanyBuyer_ChangesTheCompanyName()
    {
        StubUser(Buyer("buyer-1", "company", "Old Traders"));

        var body = await EditAsync("buyer-1", Data(("companyName", "Meki Grain Traders")), role: "buyer");

        Assert.Contains("\"status\":\"success\"", body);
        await _users.Received(1).UpdateUserAsync(
            Arg.Is<UserDto>(u => u.CompanyName == "Meki Grain Traders" && u.BuyerType == "company"), 3);
    }

    [Theory]
    [InlineData("farmer", null)]
    [InlineData("buyer", "private")]
    public async Task CompanyName_FromAnyoneButACompanyBuyer_IsRefused(string role, string? buyerType)
    {
        StubUser(User("user-1", "am") with { Role = role, BuyerType = buyerType });

        var body = await EditAsync("user-1", Data(("companyName", "Acme")), role: role);

        Assert.Contains("USER_PROFILE_VALIDATION: companyName can only be changed by a company buyer.", body);
        await _users.DidNotReceive().UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>());
    }

    [Fact]
    public async Task AConcurrentWrite_AnswersConflict()
    {
        StubUser(User("farmer-1", "am"));
        _users.UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>())
            .Returns(AuthResult<UserDto>.Failure(409, "AUTH_019", "User was modified by another request. Please retry.", "version"));

        var body = await EditAsync("farmer-1", Data(("displayName", "Abebe K.")));

        Assert.Contains("USER_PROFILE_CONFLICT", body);
    }

    // ── SIM-56: GET /user/profile ─────────────────────────────────────

    private static APIGatewayProxyRequest ProfileRead(string? userId)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext();
        if (userId != null)
        {
            authorizer["userId"] = userId;
        }

        return new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = "/user/profile",
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer },
        };
    }

    [Fact]
    public async Task ProfileRead_ReturnsOnlyTheProfileView()
    {
        _users.GetUserByIdAsync("buyer-1", true).Returns(Buyer("buyer-1", "company", "Meki Traders") with
        {
            DisplayName = "Almaz",
            FullName = "Almaz Tesfaye Bekele",
            FaydaId = "FAYDA-SUB-1",
            FaydaVerified = true,
            FaydaVerifiedAt = "2026-10-01T00:00:00Z",
            FaydaAuthenticatedAt = "2026-11-01T00:00:00Z",
            KnownDevices = ["install-1", "install-2"],
            ProfilePhotoRef = "photo-1",
            SuspendReason = "x",
        });

        var response = await _fn.FunctionHandler(ProfileRead("buyer-1"), _ctx);

        Assert.Equal(200, response.StatusCode);
        using var doc = JsonDocument.Parse(response.Body);
        var user = doc.RootElement.GetProperty("user");
        var keys = user.EnumerateObject().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(
            new[]
            {
                "buyerType", "companyName", "displayName", "fullName", "preferredLanguage", "role",
                "userId", "version", "woreda",
            },
            keys);
        Assert.Equal("buyer-1", user.GetProperty("userId").GetString());
        Assert.Equal("Almaz", user.GetProperty("displayName").GetString());
        Assert.Equal("Meki Traders", user.GetProperty("companyName").GetString());
        Assert.Equal("buyer", user.GetProperty("role").GetString());
        Assert.Equal(3, user.GetProperty("version").GetInt32());
        Assert.DoesNotContain("FAYDA-SUB-1", response.Body);
        Assert.DoesNotContain("install-1", response.Body);
        Assert.DoesNotContain("+251900100001", response.Body);
        await _users.DidNotReceive().UpdateUserAsync(Arg.Any<UserDto>(), Arg.Any<int>());
    }

    [Fact]
    public async Task ProfileRead_WithoutACaller_Is401()
    {
        var response = await _fn.FunctionHandler(ProfileRead(null), _ctx);

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task ProfileRead_WithoutARow_Is404()
    {
        _users.GetUserByIdAsync("farmer-1", true).Returns((UserDto?)null);

        var response = await _fn.FunctionHandler(ProfileRead("farmer-1"), _ctx);

        Assert.Equal(404, response.StatusCode);
        Assert.Contains("AUTH_013", response.Body);
    }

    [Fact]
    public async Task ProfileRead_DeletingAccount_Is410()
    {
        _users.GetUserByIdAsync("farmer-1", true).Returns(User("farmer-1", "am") with { DeletionState = "in_progress" });

        var response = await _fn.FunctionHandler(ProfileRead("farmer-1"), _ctx);

        Assert.Equal(410, response.StatusCode);
        Assert.Contains("ACCOUNT_004", response.Body);
    }

    [Fact]
    public async Task ProfileRead_SuspendedAccount_Is403()
    {
        _users.GetUserByIdAsync("farmer-1", true).Returns(User("farmer-1", "am") with { Suspended = true });

        var response = await _fn.FunctionHandler(ProfileRead("farmer-1"), _ctx);

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("AUTH_025", response.Body);
    }

    [Fact]
    public async Task ProfileRead_ThatFails_Is503()
    {
        _users.GetUserByIdAsync("farmer-1", true).Returns<UserDto?>(_ => throw new InvalidOperationException("boom"));

        var response = await _fn.FunctionHandler(ProfileRead("farmer-1"), _ctx);

        Assert.Equal(503, response.StatusCode);
        Assert.Contains("USER_PROFILE_UNAVAILABLE", response.Body);
    }

    [Fact]
    public async Task ProfileRead_WithoutUserRepo_Is503()
    {
        var fn = new SyncFunction(_meta, _crop, _ddb, "trips", "deliveries");

        var response = await fn.FunctionHandler(ProfileRead("farmer-1"), _ctx);

        Assert.Equal(503, response.StatusCode);
    }

    [Fact]
    public void OnlyGetOfTheProfilePath_IsAProfileRead()
    {
        Assert.True(SyncFunction.IsProfileRead(new APIGatewayProxyRequest { HttpMethod = "GET", Path = "/user/profile/" }));
        Assert.False(SyncFunction.IsProfileRead(new APIGatewayProxyRequest { HttpMethod = "POST", Path = "/user/profile" }));
        Assert.False(SyncFunction.IsProfileRead(new APIGatewayProxyRequest { HttpMethod = "POST", Path = "/sync" }));
        Assert.False(SyncFunction.IsProfileRead(new APIGatewayProxyRequest { HttpMethod = "GET", Path = "/sync" }));
    }
}
