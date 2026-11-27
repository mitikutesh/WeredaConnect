using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-67: the <c>listing_report</c> sync entity (listing-report-api.md 1.0.0 section 1, sync-protocol.md
/// 1.44.0). Buyers only, one report per buyer and listing, a daily limit, every refusal permanent
/// (<c>SYNC_VALIDATION</c>), and nothing written anywhere but the reports table.
/// </summary>
public class SyncFunctionListingReportTests
{
    private const string BuyerId = "00000000-0000-0000-0000-000000000003";
    private const string FarmerId = "00000000-0000-0000-0000-000000000001";
    private const string TruckerId = "00000000-0000-0000-0000-000000000002";
    private const string CropId = "11111111-1111-1111-1111-111111111111";
    private const string TripId = "22222222-2222-2222-2222-222222222222";
    private const string TripsTable = "test-trips";
    private const string DeliveriesTable = "test-deliveries";

    private static readonly DateTime Now = new(2026, 11, 7, 9, 30, 15, 123, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly ISyncMetadataRepository _syncMeta = Substitute.For<ISyncMetadataRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly IListingReportRepository _reports = Substitute.For<IListingReportRepository>();
    private readonly IRateLimitRepository _limits = Substitute.For<IRateLimitRepository>();
    private readonly List<ListingReportRow> _written = new();
    private readonly TestLambdaContext _context = new();

    public SyncFunctionListingReportTests()
    {
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });
        _crops.GetByIdAsync(CropId).Returns(new Dictionary<string, object>
        {
            ["cropId"] = CropId,
            ["farmerId"] = FarmerId,
            ["status"] = "available"
        });
        TripOwnedBy(TruckerId);
        _reports.ExistsAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        _reports.TryCreateAsync(Arg.Do<ListingReportRow>(row => _written.Add(row))).Returns(true);
        _limits.IncrementAsync(Arg.Any<string>(), Arg.Any<int>()).Returns(1);
    }

    // ===== Accepted =====

    [Fact]
    public async Task Buyer_ReportsACrop_TheOpenRowIsWrittenWithTheOwnerAndTtl()
    {
        var result = await SendAsync(Report("crop", CropId, "contact_info"));

        result.GetProperty("status").GetString().Should().Be("success");
        result.TryGetProperty("serverData", out _).Should().BeFalse();
        var row = _written.Should().ContainSingle().Subject;
        row.ListingId.Should().Be(CropId);
        row.ReporterId.Should().Be(BuyerId);
        row.ListingType.Should().Be("crop");
        row.OwnerId.Should().Be(FarmerId);
        row.Reason.Should().Be("contact_info");
        row.Status.Should().Be("open");
        row.CreatedAt.Should().Be("2026-11-07T09:30:15.123Z");
        row.ExpiresAt.Should().Be(new DateTimeOffset(Now).AddDays(180).ToUnixTimeSeconds());
        await _limits.Received(1).IncrementAsync($"LISTING_REPORT#{BuyerId}#2026-11-07", 2 * 24 * 60 * 60);
        await _syncMeta.Received(1).StoreIdempotencyKeyAsync(
            Arg.Any<string>(), Arg.Is("op-report"), Arg.Is("listing_report"), Arg.Is("success"), Arg.Any<string?>());
    }

    [Fact]
    public async Task Buyer_ReportsATrip_TheTruckerIsTheOwner()
    {
        var result = await SendAsync(Report("trip", TripId, "scam"));

        result.GetProperty("status").GetString().Should().Be("success");
        var row = _written.Should().ContainSingle().Subject;
        row.ListingType.Should().Be("trip");
        row.ListingId.Should().Be(TripId);
        row.OwnerId.Should().Be(TruckerId);
        row.Reason.Should().Be("scam");
    }

    [Theory]
    [InlineData("contact_info")]
    [InlineData("misleading")]
    [InlineData("scam")]
    [InlineData("offensive")]
    [InlineData("other")]
    public async Task EveryReasonCode_IsAccepted(string reason)
    {
        var result = await SendAsync(Report("crop", CropId, reason));

        result.GetProperty("status").GetString().Should().Be("success");
        _written.Should().ContainSingle().Which.Reason.Should().Be(reason);
    }

    [Fact]
    public async Task ADeletedListing_CanStillBeReported()
    {
        _crops.GetByIdAsync(CropId).Returns(new Dictionary<string, object>
        {
            ["cropId"] = CropId,
            ["farmerId"] = FarmerId,
            ["status"] = "deleted"
        });

        var result = await SendAsync(Report("crop", CropId, "scam"));

        result.GetProperty("status").GetString().Should().Be("success");
    }

    [Fact]
    public async Task TheLastReportOfTheDay_IsAccepted()
    {
        _limits.IncrementAsync(Arg.Any<string>(), Arg.Any<int>()).Returns(10);

        var result = await SendAsync(Report("crop", CropId, "other"));

        result.GetProperty("status").GetString().Should().Be("success");
    }

    // ===== Duplicates =====

    [Fact]
    public async Task ASecondReportOfTheSameListing_IsDuplicate_AndCostsNoQuota()
    {
        _reports.ExistsAsync(CropId, BuyerId).Returns(true);

        var result = await SendAsync(Report("crop", CropId, "misleading"));

        result.GetProperty("status").GetString().Should().Be("duplicate");
        _written.Should().BeEmpty();
        await _limits.DidNotReceive().IncrementAsync(Arg.Any<string>(), Arg.Any<int>());
    }

    [Fact]
    public async Task ARaceLostOnTheConditionalPut_IsDuplicate()
    {
        _reports.TryCreateAsync(Arg.Any<ListingReportRow>()).Returns(false);

        var result = await SendAsync(Report("crop", CropId, "misleading"));

        result.GetProperty("status").GetString().Should().Be("duplicate");
    }

    // ===== Refused =====

    [Theory]
    [InlineData("farmer", FarmerId)]
    [InlineData("trucker", TruckerId)]
    public async Task AFarmerOrATrucker_IsRefused_WithARoleSignal(string role, string userId)
    {
        var result = await SendAsync(Report("trip", TripId, "scam"), userId, role);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().Be("SYNC_VALIDATION: Only buyers can report a listing.");
        _written.Should().BeEmpty();
        await _fraud.Received(1).RecordFraudSignalAsync(
            userId, FraudDetectionService.RoleForbiddenSignal, $"trip#{TripId}", "listing_report", Arg.Any<string?>());
    }

    [Fact]
    public async Task ATokenWithoutARole_IsRefused_WithoutASignal()
    {
        var result = await SendAsync(Report("crop", CropId, "scam"), BuyerId, role: null);

        result.GetProperty("message").GetString().Should().StartWith("SYNC_VALIDATION:");
        await _fraud.DidNotReceive().RecordFraudSignalAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task AnythingButCreate_IsRefused(string action)
    {
        var result = await SendAsync(Report("crop", CropId, "scam", action: action));

        result.GetProperty("message").GetString().Should().StartWith("SYNC_VALIDATION: Unsupported action");
        _written.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnknownReason_IsRefused()
    {
        var result = await SendAsync(Report("crop", CropId, "spam"));

        result.GetProperty("message").GetString().Should().StartWith("SYNC_VALIDATION: reason must be one of");
        _written.Should().BeEmpty();
        await _limits.DidNotReceive().IncrementAsync(Arg.Any<string>(), Arg.Any<int>());
    }

    [Fact]
    public async Task FreeText_IsRefused_AsAnUnknownField()
    {
        var result = await SendAsync(Report("crop", CropId, "other", data: d => d["note"] = "call 0911223344"));

        result.GetProperty("message").GetString().Should().StartWith("SYNC_VALIDATION: Unknown fields: note.");
        _written.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEntityIdThatDoesNotNameTheListing_IsRefused()
    {
        var result = await SendAsync(Report("crop", CropId, "scam", entityId: $"trip#{CropId}"));

        result.GetProperty("message").GetString().Should().Contain("entityId must be '{listingType}#{listingId}'.");
        _written.Should().BeEmpty();
    }

    [Fact]
    public async Task AMissingListing_IsRefused()
    {
        _crops.GetByIdAsync(CropId).Returns((Dictionary<string, object>?)null);

        var result = await SendAsync(Report("crop", CropId, "scam"));

        result.GetProperty("message").GetString().Should().Be("SYNC_VALIDATION: The reported listing does not exist.");
        _written.Should().BeEmpty();
    }

    [Fact]
    public async Task AMissingTrip_IsRefused()
    {
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == TripsTable))
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

        var result = await SendAsync(Report("trip", TripId, "scam"));

        result.GetProperty("message").GetString().Should().Be("SYNC_VALIDATION: The reported listing does not exist.");
    }

    [Fact]
    public async Task OwnListing_IsRefused()
    {
        _crops.GetByIdAsync(CropId).Returns(new Dictionary<string, object> { ["cropId"] = CropId, ["farmerId"] = BuyerId });

        var result = await SendAsync(Report("crop", CropId, "scam"));

        result.GetProperty("message").GetString().Should().Be("SYNC_VALIDATION: You cannot report your own listing.");
        _written.Should().BeEmpty();
    }

    [Fact]
    public async Task OverTheDailyLimit_IsRefused_AndNothingIsWritten()
    {
        _limits.IncrementAsync(Arg.Any<string>(), Arg.Any<int>()).Returns(11);

        var result = await SendAsync(Report("crop", CropId, "scam"));

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("SYNC_VALIDATION: report limit reached.");
        _written.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutTheReportsTable_TheErrorIsUnprefixed_SoClientsRetry()
    {
        var function = new SyncFunction(_syncMeta, _crops, _dynamoDb, TripsTable, DeliveriesTable,
            fraudService: _fraud, orderRepo: _orders, deliveryRepo: _deliveries)
        {
            ReportRateLimits = _limits
        };

        var result = await SendAsync(Report("crop", CropId, "scam"), function: function);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().Be("Listing reports are not enabled on this server yet.");
    }

    // ===== Helpers =====

    private SyncFunction Build() =>
        new(_syncMeta, _crops, _dynamoDb, TripsTable, DeliveriesTable,
            fraudService: _fraud, orderRepo: _orders, deliveryRepo: _deliveries)
        {
            ListingReportRepo = _reports,
            ReportRateLimits = _limits,
            ReportClock = () => Now
        };

    private void TripOwnedBy(string truckerId) =>
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == TripsTable))
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new()
                    {
                        ["tripId"] = new AttributeValue { S = TripId },
                        ["truckerId"] = new AttributeValue { S = truckerId },
                        ["status"] = new AttributeValue { S = "open" }
                    }
                }
            });

    private static SyncOperationRequest Report(
        string listingType, string listingId, string reason, string action = "create", string? entityId = null,
        Action<Dictionary<string, object>>? data = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["listingType"] = listingType,
            ["listingId"] = listingId,
            ["reason"] = reason
        };
        data?.Invoke(payload);
        var id = entityId ?? $"{listingType}#{listingId}";
        return new SyncOperationRequest
        {
            OperationId = "op-report",
            IdempotencyKey = $"{BuyerId}:listing_report:{id}:{action}:1762507815123",
            EntityType = "listing_report",
            EntityId = id,
            Action = action,
            Data = payload,
            Timestamp = "2026-11-07T09:30:15Z"
        };
    }

    private async Task<JsonElement> SendAsync(
        SyncOperationRequest operation, string userId = BuyerId, string? role = "buyer", SyncFunction? function = null)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId } };
        if (role != null)
        {
            authorizer["custom:role"] = role;
        }

        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-fup-67",
                ClientId = "device-001",
                Operations = new List<SyncOperationRequest> { operation }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer }
        };

        var response = await (function ?? Build()).FunctionHandler(request, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }
}
