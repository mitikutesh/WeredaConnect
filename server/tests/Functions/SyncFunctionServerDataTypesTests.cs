using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// SIM-25: success and conflict rows in /sync carry numbers and matchedListings with the JSON
/// types GET returns (sync-protocol.md 1.37.0), not the strings DynamoDB hands back.
/// </summary>
public partial class SyncFunctionTests
{
    private static JsonElement FirstServerData(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("results")[0].GetProperty("serverData");

    [Fact]
    public async Task CropUpdateConflict_ServerData_HasNumbersAsNumbers()
    {
        _mockCropRepo.GetByIdAsync("crop-001").Returns(new Dictionary<string, object>
        {
            { "cropId", "crop-001" },
            { "farmerId", "farmer-001" },
            { "woreda", "105" },
            { "pricePerKg", "18" },
            { "quantity", "12.5" },
            { "version", "2" }
        });
        _mockCropRepo.UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "Version mismatch."));
        var op = new SyncOperationRequest
        {
            OperationId = "op-typed-conflict",
            IdempotencyKey = "user:crop_listing:crop-001:update:12345",
            EntityType = "crop_listing",
            EntityId = "crop-001",
            Action = "update",
            Data = new Dictionary<string, object> { { "quantity", 100 }, { "version", 1 } }
        };

        var response = await _function.FunctionHandler(MakeAuthenticatedRequest(MakeSyncRequest(op), "farmer-001"), _context);

        FirstStatus(response.Body).Should().Be("conflict");
        var row = FirstServerData(response.Body);
        row.GetProperty("version").ValueKind.Should().Be(JsonValueKind.Number);
        row.GetProperty("version").GetInt32().Should().Be(2);
        row.GetProperty("pricePerKg").GetDecimal().Should().Be(18m);
        row.GetProperty("quantity").GetDecimal().Should().Be(12.5m);
        row.GetProperty("woreda").GetString().Should().Be("105");
    }

    [Fact]
    public async Task TripUpdateConflict_ServerData_HasNumbersAndMatchedListingsTyped()
    {
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>()).Returns(new QueryResponse
        {
            Items = new List<Dictionary<string, AttributeValue>>
            {
                new()
                {
                    ["tripId"] = new() { S = "trip-001" },
                    ["truckerId"] = new() { S = "trucker-001" },
                    ["createdAt"] = new() { S = "2026-02-16T10:00:00Z" },
                    ["status"] = new() { S = "open" },
                    ["capacity"] = new() { N = "50" },
                    ["pricePerQuintal"] = new() { N = "90.5" },
                    ["matchedListings"] = new() { S = "[\"listing-1\"]" },
                    ["version"] = new() { N = "3" }
                }
            }
        });
        _mockDynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>())
            .ThrowsAsync(new ConditionalCheckFailedException("Version mismatch"));
        var op = new SyncOperationRequest
        {
            OperationId = "op-trip-typed",
            IdempotencyKey = "user:trip:trip-001:update:12345",
            EntityType = "trip",
            EntityId = "trip-001",
            Action = "update",
            Data = new Dictionary<string, object> { { "capacity", 60.0 }, { "version", 1 } }
        };

        var response = await _function.FunctionHandler(MakeAuthenticatedRequest(MakeSyncRequest(op)), _context);

        FirstStatus(response.Body).Should().Be("conflict");
        var row = FirstServerData(response.Body);
        row.GetProperty("version").GetInt32().Should().Be(3);
        row.GetProperty("capacity").GetDouble().Should().Be(50);
        row.GetProperty("pricePerQuintal").GetDouble().Should().Be(90.5);
        row.GetProperty("matchedListings").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("listing-1");
    }

    [Fact]
    public async Task CropCreateSuccess_ServerData_HasNumbersAsNumbers()
    {
        _mockCropRepo.CreateAsync("crop-001", Arg.Any<Dictionary<string, object>>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "cropId", "crop-001" },
                { "pricePerKg", "45.5" },
                { "quantity", "50" },
                { "version", "1" }
            }));

        var response = await _function.FunctionHandler(
            MakeAuthenticatedRequest(MakeSyncRequest(MakeCropCreateOp()), "farmer-001", "farmer"), _context);

        FirstStatus(response.Body).Should().Be("success");
        var row = FirstServerData(response.Body);
        row.GetProperty("version").GetInt32().Should().Be(1);
        row.GetProperty("pricePerKg").GetDecimal().Should().Be(45.5m);
        row.GetProperty("quantity").GetInt32().Should().Be(50);
    }
}
