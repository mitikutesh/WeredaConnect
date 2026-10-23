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
/// chosen in Settings to the caller's own profile (only preferredLanguage).
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
}
