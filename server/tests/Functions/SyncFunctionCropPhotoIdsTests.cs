using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// crop-listing-api 1.6.0 rule 9 on POST /sync: photoIds is required on create (2 to 4 distinct
/// UUID strings), optional on update with the same rule, refused inside CROP_VALIDATION, and
/// stored lower-cased as a DynamoDB L of S.
/// </summary>
public partial class SyncFunctionTests
{
    private const string PhotoIdsRefusal = "CROP_VALIDATION: photoIds must list 2 to 4 distinct photo ids.";

    private static string FirstMessage(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("results")[0].GetProperty("message").GetString() ?? "";

    private static string FirstStatus(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("results")[0].GetProperty("status").GetString() ?? "";

    public static IEnumerable<object[]> BadPhotoIds() => new[]
    {
        new object[] { "missing" },
        new object[] { "not an array" },
        new object[] { 42 },
        new object[] { new { a = 1 } },
        new object[] { Array.Empty<string>() },
        new object[] { new[] { CropPhotoFixtures.Photo1 } },
        new object[] { new[] { CropPhotoFixtures.Photo1, CropPhotoFixtures.Photo2, CropPhotoFixtures.Photo3, CropPhotoFixtures.Photo4, CropPhotoFixtures.Photo5 } },
        new object[] { new[] { CropPhotoFixtures.Photo1, CropPhotoFixtures.Photo1 } },
        new object[] { new[] { CropPhotoFixtures.Photo1, CropPhotoFixtures.Photo1.ToUpperInvariant() } },
        new object[] { new[] { CropPhotoFixtures.Photo1, "not-a-uuid" } },
        new object[] { new[] { CropPhotoFixtures.Photo1, "7d1c0a523f0e4c4b9a8e0f3b6c2d9e11" } },
        new object[] { new[] { CropPhotoFixtures.Photo1, "" } },
        new object[] { new object[] { CropPhotoFixtures.Photo1, 7 } },
        new object?[] { new object?[] { CropPhotoFixtures.Photo1, null } },
    };

    [Theory]
    [MemberData(nameof(BadPhotoIds))]
    public async Task CropCreate_BadPhotoIds_IsRefused_AndNothingWritten(object photoIds)
    {
        var op = MakeCropCreateOp();
        if (photoIds is "missing")
        {
            op.Data!.Remove("photoIds");
        }
        else
        {
            op.Data!["photoIds"] = photoIds;
        }

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer"), _context);

        response.StatusCode.Should().Be(200);
        FirstStatus(response.Body).Should().Be("error");
        FirstMessage(response.Body).Should().Be(PhotoIdsRefusal);
        await _mockCropRepo.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task CropCreate_NullPhotoIds_IsRefused()
    {
        var op = MakeCropCreateOp();
        op.Data!["photoIds"] = null!;

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer"), _context);

        FirstMessage(response.Body).Should().Be(PhotoIdsRefusal);
        await _mockCropRepo.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task CropCreate_PhotoIdsRefusal_JoinsTheOtherFieldErrors()
    {
        var op = MakeCropCreateOp();
        op.Data!.Remove("photoIds");
        op.Data["pricePerKg"] = 0;

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer"), _context);

        FirstMessage(response.Body).Should()
            .Be("CROP_VALIDATION: pricePerKg must be between 0.01 and 1000. photoIds must list 2 to 4 distinct photo ids.");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task CropCreate_TwoToFourPhotoIds_AreWrittenLowerCased_InOrder(int count)
    {
        var ids = new[] { CropPhotoFixtures.Photo3, CropPhotoFixtures.Photo1, CropPhotoFixtures.Photo4, CropPhotoFixtures.Photo2 }
            .Take(count).ToArray();
        var op = MakeCropCreateOp();
        op.Data!["photoIds"] = ids.Select(id => id.ToUpperInvariant()).ToArray();
        Dictionary<string, object>? written = null;
        _mockCropRepo.CreateAsync("crop-001", Arg.Do<Dictionary<string, object>>(d => written = d))
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "cropId", "crop-001" }, { "version", "1" }
            }));

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer"), _context);

        FirstStatus(response.Body).Should().Be("success");
        written.Should().NotBeNull();
        written!["photoIds"].Should().BeOfType<List<string>>().Which.Should().Equal(ids);
    }

    [Fact]
    public async Task CropUpdate_WithoutPhotoIds_IsAccepted()
    {
        _mockCropRepo.GetByIdAsync("crop-001").Returns(OwnCropRow());
        _mockCropRepo.UpdateAsync("crop-001", Arg.Any<Dictionary<string, object>>(), 1)
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "cropId", "crop-001" }, { "version", "2" }
            }));

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(CropUpdateOp(new Dictionary<string, object>
            {
                { "quantity", 40 }, { "version", 1 }
            })), "farmer-001", "farmer"), _context);

        FirstStatus(response.Body).Should().Be("success");
    }

    [Theory]
    [MemberData(nameof(BadPhotoIds))]
    public async Task CropUpdate_BadPhotoIds_IsRefused_AndNothingWritten(object photoIds)
    {
        if (photoIds is "missing")
        {
            return; // absent is fine on update (CropUpdate_WithoutPhotoIds_IsAccepted)
        }

        _mockCropRepo.GetByIdAsync("crop-001").Returns(OwnCropRow());

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(CropUpdateOp(new Dictionary<string, object>
            {
                { "photoIds", photoIds }, { "version", 1 }
            })), "farmer-001", "farmer"), _context);

        FirstStatus(response.Body).Should().Be("error");
        FirstMessage(response.Body).Should().Be(PhotoIdsRefusal);
        await _mockCropRepo.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task CropUpdate_ValidPhotoIds_AreWrittenLowerCased()
    {
        _mockCropRepo.GetByIdAsync("crop-001").Returns(OwnCropRow());
        Dictionary<string, object>? written = null;
        _mockCropRepo.UpdateAsync("crop-001", Arg.Do<Dictionary<string, object>>(d => written = d), 1)
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "cropId", "crop-001" }, { "version", "2" }
            }));

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(CropUpdateOp(new Dictionary<string, object>
            {
                { "photoIds", new[] { CropPhotoFixtures.Photo2.ToUpperInvariant(), CropPhotoFixtures.Photo1, CropPhotoFixtures.Photo3 } },
                { "version", 1 }
            })), "farmer-001", "farmer"), _context);

        FirstStatus(response.Body).Should().Be("success");
        written!["photoIds"].Should().BeOfType<List<string>>().Which.Should()
            .Equal(CropPhotoFixtures.Photo2, CropPhotoFixtures.Photo1, CropPhotoFixtures.Photo3);
    }

    /// <summary>
    /// End to end through the real repository: the wire array becomes an L of S in the put, and
    /// the create result (read back as a List of object) serializes under the source-gen context.
    /// </summary>
    [Fact]
    public async Task CropCreate_ThroughTheRepository_StoresAnLOfS_AndEchoesTheListInServerData()
    {
        PutItemRequest? put = null;
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });
        _mockDynamoDb.PutItemAsync(Arg.Do<PutItemRequest>(r => put = r), Arg.Any<CancellationToken>())
            .Returns(new PutItemResponse());
        var function = new SyncFunction(_mockSyncMeta, new CropRepository(_mockDynamoDb, "test-crops-table"),
            _mockDynamoDb, "test-trips-table", "test-deliveries-table",
            userRepo: _mockUserRepo, orderRepo: _mockOrderRepo);
        var op = MakeCropCreateOp();
        op.Data!["photoIds"] = new[] { CropPhotoFixtures.Photo2.ToUpperInvariant(), CropPhotoFixtures.Photo1 };

        var response = await function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001", "farmer"), _context);

        response.StatusCode.Should().Be(200);
        FirstStatus(response.Body).Should().Be("success");
        put.Should().NotBeNull();
        var stored = put!.Item["photoIds"];
        stored.S.Should().BeNull();
        stored.L.Select(a => a.S).Should().Equal(CropPhotoFixtures.Photo2, CropPhotoFixtures.Photo1);

        var serverData = JsonDocument.Parse(response.Body).RootElement
            .GetProperty("results")[0].GetProperty("serverData");
        serverData.GetProperty("photoIds").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(CropPhotoFixtures.Photo2, CropPhotoFixtures.Photo1);
    }

    [Fact]
    public void SyncResponse_WithAListInServerData_SerializesUnderTheSourceGenContext()
    {
        var response = new SyncResponse
        {
            SyncBatchId = "batch-001",
            Results = new List<SyncOperationResult>
            {
                new()
                {
                    OperationId = "op-001",
                    Status = "success",
                    ServerVersion = 1,
                    ServerData = DynamoDbHelper.FromItem(new Dictionary<string, AttributeValue>
                    {
                        ["cropId"] = new() { S = "crop-001" },
                        ["photoIds"] = DynamoDbHelper.ToAttributeValue(new List<string>
                        {
                            CropPhotoFixtures.Photo1, CropPhotoFixtures.Photo2
                        })
                    })
                }
            }
        };

        var json = JsonSerializer.Serialize(response, AppJsonContext.Default.SyncResponse);

        json.Should().Contain($"\"photoIds\":[\"{CropPhotoFixtures.Photo1}\",\"{CropPhotoFixtures.Photo2}\"]");
    }

    private static Dictionary<string, object> OwnCropRow() => new()
    {
        { "cropId", "crop-001" },
        { "farmerId", "farmer-001" },
        { "status", "available" },
        { "version", "1" }
    };

    private static SyncOperationRequest CropUpdateOp(Dictionary<string, object> data) => new()
    {
        OperationId = "op-update-photos",
        IdempotencyKey = "user:crop_listing:crop-001:update:12345",
        EntityType = "crop_listing",
        EntityId = "crop-001",
        Action = "update",
        Data = data
    };
}
