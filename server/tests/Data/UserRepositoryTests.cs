using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

public class UserRepositoryTests
{
    private readonly IAmazonDynamoDB _mockDynamoDb;
    private readonly UserRepository _repository;

    private const string TableName = "test-users";

    public UserRepositoryTests()
    {
        _mockDynamoDb = Substitute.For<IAmazonDynamoDB>();
        _repository = new UserRepository(_mockDynamoDb, TableName);
    }

    private static UserDto CreateTestUser(string userId = "user-123") => new()
    {
        UserId = userId,
        PhoneNumber = "+251912345678",
        DisplayName = "Abebe",
        Role = "farmer",
        Woreda = "Adama",
        PreferredLanguage = "am",
        IsPhoneVerified = false,
        CreatedAt = "2026-01-01T00:00:00Z",
        UpdatedAt = "2026-01-01T00:00:00Z",
        Version = 1
    };

    // --- CreateUserAsync ---

    [Fact]
    public async Task CreateUserAsync_Success_ReturnsUser()
    {
        _mockDynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PutItemResponse());

        var user = CreateTestUser();
        var result = await _repository.CreateUserAsync(user);

        Assert.True(result.IsSuccess);
        Assert.Equal("user-123", result.Value!.UserId);
    }

    [Fact]
    public async Task CreateUserAsync_DuplicateId_ReturnsFailure()
    {
        _mockDynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("exists"));

        var result = await _repository.CreateUserAsync(CreateTestUser());

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTH_005", result.Error!.Code);
        Assert.Equal(409, result.Error.HttpStatus);
    }

    // --- GetUserByIdAsync ---

    [Fact]
    public async Task GetUserByIdAsync_Found_ReturnsUser()
    {
        var item = UserRepository.ToItem(CreateTestUser());
        _mockDynamoDb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse { Item = item });

        var user = await _repository.GetUserByIdAsync("user-123");

        Assert.NotNull(user);
        Assert.Equal("user-123", user.UserId);
        Assert.Equal("+251912345678", user.PhoneNumber);
        Assert.Equal("Abebe", user.DisplayName);
    }

    [Fact]
    public async Task GetUserByIdAsync_NotFound_ReturnsNull()
    {
        _mockDynamoDb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse { Item = new Dictionary<string, AttributeValue>() });

        var user = await _repository.GetUserByIdAsync("nonexistent");

        Assert.Null(user);
    }

    // --- GetUserByPhoneAsync ---

    [Fact]
    public async Task GetUserByPhoneAsync_Found_ReturnsUser()
    {
        var item = UserRepository.ToItem(CreateTestUser());
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [item] });

        var user = await _repository.GetUserByPhoneAsync("+251912345678");

        Assert.NotNull(user);
        Assert.Equal("+251912345678", user.PhoneNumber);
    }

    [Fact]
    public async Task GetUserByPhoneAsync_NotFound_ReturnsNull()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = [] });

        var user = await _repository.GetUserByPhoneAsync("+251999999999");

        Assert.Null(user);
    }

    // --- UpdateUserAsync ---

    [Fact]
    public async Task UpdateUserAsync_Success_ReturnsUpdatedUser()
    {
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());

        var user = CreateTestUser();
        var result = await _repository.UpdateUserAsync(user with { IsPhoneVerified = true }, 1);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Version); // Version incremented
        Assert.True(result.Value.IsPhoneVerified);
    }

    [Fact]
    public async Task UpdateUserAsync_WritesTheFaydaLoginTime()
    {
        UpdateItemRequest? sent = null;
        _mockDynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());

        await _repository.UpdateUserAsync(CreateTestUser() with { FaydaAuthenticatedAt = "2026-09-29T10:00:00Z" }, 1);

        Assert.NotNull(sent);
        Assert.Contains("#faydaAuthAt = :faydaAuthAt", sent!.UpdateExpression);
        Assert.Equal("faydaAuthenticatedAt", sent.ExpressionAttributeNames["#faydaAuthAt"]);
        Assert.Equal("2026-09-29T10:00:00Z", sent.ExpressionAttributeValues[":faydaAuthAt"].S);
    }

    [Fact]
    public async Task UpdateUserAsync_VersionConflict_ReturnsFailure()
    {
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("version mismatch"));

        var result = await _repository.UpdateUserAsync(CreateTestUser(), 1);

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTH_019", result.Error!.Code);
        Assert.Equal(409, result.Error.HttpStatus);
    }

    // --- Marshalling ---

    [Fact]
    public void ToItem_FromItem_RoundTrips()
    {
        var user = CreateTestUser() with
        {
            FaydaId = "fayda-abc",
            ProfilePhotoRef = "s3://bucket/photo.jpg"
        };

        var item = UserRepository.ToItem(user);
        var restored = UserRepository.FromItem(item);

        Assert.Equal(user.UserId, restored.UserId);
        Assert.Equal(user.PhoneNumber, restored.PhoneNumber);
        Assert.Equal(user.DisplayName, restored.DisplayName);
        Assert.Equal(user.Role, restored.Role);
        Assert.Equal(user.Woreda, restored.Woreda);
        Assert.Equal(user.FaydaId, restored.FaydaId);
        Assert.Equal(user.PreferredLanguage, restored.PreferredLanguage);
        Assert.Equal(user.IsPhoneVerified, restored.IsPhoneVerified);
        Assert.Equal(user.ProfilePhotoRef, restored.ProfilePhotoRef);
        Assert.Equal(user.CreatedAt, restored.CreatedAt);
        Assert.Equal(user.UpdatedAt, restored.UpdatedAt);
        Assert.Equal(user.Version, restored.Version);
    }

    [Fact]
    public void ToItem_FromItem_RoundTripsFaydaLoginTime()
    {
        var user = CreateTestUser() with { FaydaAuthenticatedAt = "2026-09-29T10:00:00.0000000Z" };

        var item = UserRepository.ToItem(user);
        var restored = UserRepository.FromItem(item);

        Assert.Equal("2026-09-29T10:00:00.0000000Z", item["faydaAuthenticatedAt"].S);
        Assert.Equal(user.FaydaAuthenticatedAt, restored.FaydaAuthenticatedAt);
        Assert.False(UserRepository.ToItem(CreateTestUser()).ContainsKey("faydaAuthenticatedAt"));
        Assert.Null(UserRepository.FromItem(UserRepository.ToItem(CreateTestUser())).FaydaAuthenticatedAt);
    }

    [Fact]
    public void UserDto_Json_NeverCarriesTheFaydaLoginTime()
    {
        var user = CreateTestUser() with
        {
            FaydaVerifiedAt = "2026-09-01T00:00:00Z",
            FaydaAuthenticatedAt = "2026-09-29T10:00:00Z"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(user, AppJsonContext.Default.UserDto);

        Assert.Contains("faydaVerifiedAt", json);
        Assert.DoesNotContain("faydaAuthenticatedAt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("2026-09-29T10:00:00Z", json);
    }

    [Fact]
    public void ToItem_FromItem_RoundTripsBuyerFields()
    {
        var user = CreateTestUser() with
        {
            Role = "buyer",
            BuyerType = "company",
            CompanyName = "Abebe Trading PLC"
        };

        var item = UserRepository.ToItem(user);
        var restored = UserRepository.FromItem(item);

        Assert.Equal("company", restored.BuyerType);
        Assert.Equal("Abebe Trading PLC", restored.CompanyName);
    }

    [Fact]
    public void ToItem_OmitsBuyerFieldsWhenNull()
    {
        var user = CreateTestUser(); // farmer — no buyer fields

        var item = UserRepository.ToItem(user);
        var restored = UserRepository.FromItem(item);

        Assert.False(item.ContainsKey("buyerType"));
        Assert.False(item.ContainsKey("companyName"));
        Assert.Null(restored.BuyerType);
        Assert.Null(restored.CompanyName);
    }
}
