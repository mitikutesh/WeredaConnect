using System.Net;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FRD-12: the <c>delivery_photo</c> sync entity attaches an uploaded handoff photo to its delivery
/// (delivery-verification-api.md 1.25.0). Only the photo's taker may attach it (the trucker the
/// pickup, the buyer the drop-off); the object must be in the bucket within the 200 KiB cap; the
/// first photo of a handoff stays; the write moves neither the version nor updatedAt.
/// </summary>
public sealed class SyncFunctionDeliveryPhotoTests
{
    private const string DeliveriesTable = "test-deliveries-table";
    private const string Bucket = "test-uploads";
    private const string DeliveryId = "del-photo";
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";
    private const string BuyerId = "buyer-001";
    private const string PickupKey = "uploads/photos/del-photo/pickup-1700000000000.webp";
    private const string DropoffKey = "uploads/photos/del-photo/dropoff-1700000000000.jpg";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly IAmazonS3 _s3 = Substitute.For<IAmazonS3>();
    private readonly ISyncMetadataRepository _syncMeta = Substitute.For<ISyncMetadataRepository>();
    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly TestLambdaContext _context = new();

    public SyncFunctionDeliveryPhotoTests()
    {
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());
    }

    // ── Who may attach (role rules) ─────────────────────────────────────

    [Theory]
    [InlineData("pickup", PickupKey, TruckerId, "pickupPhotoRef")]
    [InlineData("dropoff", DropoffKey, BuyerId, "dropoffPhotoRef")]
    public async Task ThePhotosTaker_AttachesIt_WithoutMovingTheVersion(string type, string key, string caller, string field)
    {
        SetupDelivery(Delivery("pickedUp"));
        SetupObject(key, 150 * 1024);

        var result = await SendAsync(caller, type, key);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        result.GetProperty("serverVersion").GetInt32().Should().Be(3);
        await _dynamoDb.Received(1).UpdateItemAsync(Arg.Is<UpdateItemRequest>(r =>
            r.TableName == DeliveriesTable
            && r.Key["deliveryId"].S == DeliveryId
            && r.Key["tripId"].S == "trip-photo"
            && r.UpdateExpression == "SET #photo = :ref, #photoVersion = :version"
            && r.ExpressionAttributeNames["#photo"] == field
            && r.ExpressionAttributeValues[":ref"].S == key
            && r.ExpressionAttributeNames["#photoVersion"] == field.Replace("Ref", "VersionId")
            && r.ExpressionAttributeValues[":version"].S == "v-attached"
            && r.ConditionExpression.Contains("attribute_not_exists(#photo)")
            && !r.ExpressionAttributeNames.Values.Contains("version")
            && !r.ExpressionAttributeNames.Values.Contains("updatedAt")), Arg.Any<CancellationToken>());
        await NoSignalAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public async Task WithoutAnS3Version_OnlyTheKeyIsWritten(string? versionId)
    {
        SetupDelivery(Delivery("pickedUp"));
        SetupObject(PickupKey, 1024, versionId: versionId);

        var result = await SendAsync(TruckerId, "pickup", PickupKey);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        await _dynamoDb.Received(1).UpdateItemAsync(Arg.Is<UpdateItemRequest>(r =>
            r.UpdateExpression == "SET #photo = :ref"
            && !r.ExpressionAttributeValues.ContainsKey(":version")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASelfPickupBuyer_AttachesTheDropoffPhoto()
    {
        SetupDelivery(Delivery("delivered", truckerId: "", deliveryType: "self_pickup"));
        SetupObject(DropoffKey, 80 * 1024);

        var result = await SendAsync(BuyerId, "dropoff", DropoffKey);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
    }

    [Fact]
    public async Task ASelfPickup_HasNoPickupPhoto()
    {
        SetupDelivery(Delivery("delivered", truckerId: "", deliveryType: "self_pickup"));
        SetupObject(PickupKey, 80 * 1024);

        var result = await SendAsync(BuyerId, "pickup", PickupKey);

        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_FORBIDDEN: ");
        await AssertNothingWrittenAsync();
    }

    [Theory]
    [InlineData(FarmerId, "pickup", PickupKey)]
    [InlineData(BuyerId, "pickup", PickupKey)]
    [InlineData(FarmerId, "dropoff", DropoffKey)]
    [InlineData(TruckerId, "dropoff", DropoffKey)]
    public async Task AnotherPartyOfTheDelivery_CannotAttachIt(string caller, string type, string key)
    {
        SetupDelivery(Delivery("pickedUp"));
        SetupObject(key, 1024);

        var result = await SendAsync(caller, type, key);

        result.GetProperty("status").GetString().Should().Be("error");
        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_FORBIDDEN: ");
        await AssertNothingWrittenAsync();
        // A party sending the wrong type is a client mistake, not a probe.
        await NoSignalAsync();
    }

    [Theory]
    [InlineData("pickup", PickupKey)]
    [InlineData("dropoff", DropoffKey)]
    public async Task AnOutsider_IsRefused_AndSignalled(string type, string key)
    {
        SetupDelivery(Delivery("pickedUp"));
        SetupObject(key, 1024);

        var result = await SendAsync("stranger-999", type, key);

        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_FORBIDDEN: ");
        await AssertNothingWrittenAsync();
        await _fraud.Received(1).RecordFraudSignalAsync("stranger-999", FraudDetectionService.RoleForbiddenSignal,
            $"{DeliveryId}#{type}", DeliveryPhotoKeys.EntityType, Arg.Any<string?>());
    }

    // ── The object (size cap, presence, type) ───────────────────────────

    [Fact]
    public async Task APhotoOverTheCap_IsRefused()
    {
        SetupDelivery(Delivery("pickedUp"));
        SetupObject(PickupKey, DeliveryPhotoKeys.MaxBytes + 1);

        var result = await SendAsync(TruckerId, "pickup", PickupKey);

        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_TOO_LARGE: ");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task APhotoOfExactlyTheCap_IsAttached()
    {
        DeliveryPhotoKeys.MaxBytes.Should().Be(204800);
        SetupDelivery(Delivery("pickedUp"));
        SetupObject(PickupKey, DeliveryPhotoKeys.MaxBytes);

        var result = await SendAsync(TruckerId, "pickup", PickupKey);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
    }

    [Fact]
    public async Task APhotoThatNeverWentUp_IsRefused()
    {
        SetupDelivery(Delivery("pickedUp"));
        _s3.GetObjectMetadataAsync(Bucket, PickupKey, Arg.Any<CancellationToken>())
            .Throws(new AmazonS3Exception("Not Found") { StatusCode = HttpStatusCode.NotFound });

        var result = await SendAsync(TruckerId, "pickup", PickupKey);

        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_MISSING: ");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task AStorageFailure_IsDeferred()
    {
        SetupDelivery(Delivery("pickedUp"));
        _s3.GetObjectMetadataAsync(Bucket, PickupKey, Arg.Any<CancellationToken>())
            .Throws(new AmazonS3Exception("Slow Down") { StatusCode = HttpStatusCode.ServiceUnavailable });

        var result = await SendAsync(TruckerId, "pickup", PickupKey);

        result.GetProperty("status").GetString().Should().Be("deferred");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task WithoutTheBucket_TheAttachIsDeferred()
    {
        SetupDelivery(Delivery("pickedUp"));

        var result = await SendAsync(TruckerId, "pickup", PickupKey, withBucket: false);

        result.GetProperty("status").GetString().Should().Be("deferred");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task AnObjectOfAnotherType_IsRefused()
    {
        SetupDelivery(Delivery("pickedUp"));
        SetupObject(PickupKey, 1024, contentType: "image/png");

        var result = await SendAsync(TruckerId, "pickup", PickupKey);

        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_INVALID: ");
        await AssertNothingWrittenAsync();
    }

    // ── The operation's shape ───────────────────────────────────────────

    [Theory]
    [InlineData("uploads/photos/del-other/pickup-1700000000000.webp")]
    [InlineData("uploads/photos/del-photo/dropoff-1700000000000.webp")]
    [InlineData("/data/user/0/app/files/photos/del-photo/pickup-1700000000000.webp")]
    [InlineData("uploads/photos/del-photo/pickup-1700000000000.png")]
    [InlineData("")]
    public async Task AKeyThatIsNotThisPhotos_IsRefused(string key)
    {
        SetupDelivery(Delivery("pickedUp"));

        var result = await SendAsync(TruckerId, "pickup", key);

        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_INVALID: ");
        await AssertNothingWrittenAsync();
    }

    [Theory]
    [InlineData("del-photo", "create")]
    [InlineData("del-photo#selfie", "create")]
    [InlineData("del-photo#pickup", "update")]
    public async Task AMalformedOperation_IsRefused(string entityId, string action)
    {
        SetupDelivery(Delivery("pickedUp"));

        var result = await SendRawAsync(TruckerId, entityId, action,
            new Dictionary<string, object> { { "photoRef", PickupKey } });

        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_INVALID: ");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task AnUnknownField_IsRefused()
    {
        SetupDelivery(Delivery("pickedUp"));

        var result = await SendRawAsync(TruckerId, $"{DeliveryId}#pickup", "create",
            new Dictionary<string, object> { { "photoRef", PickupKey }, { "version", 3 } });

        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_INVALID: Unknown fields: version");
        await AssertNothingWrittenAsync();
    }

    // ── The row's state ─────────────────────────────────────────────────

    [Fact]
    public async Task TheSameKeyAgain_IsASuccess_WithoutAWrite()
    {
        SetupDelivery(Delivery("pickedUp", pickupPhotoRef: PickupKey));

        var result = await SendAsync(TruckerId, "pickup", PickupKey);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        await AssertNothingWrittenAsync();
        await _s3.DidNotReceive().GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AHandoffKeepsItsFirstPhoto()
    {
        SetupDelivery(Delivery("pickedUp", pickupPhotoRef: "uploads/photos/del-photo/pickup-1600000000000.webp"));
        SetupObject(PickupKey, 1024);

        var result = await SendAsync(TruckerId, "pickup", PickupKey);

        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_EXISTS: ");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task ACancelledDelivery_TakesNoPhoto()
    {
        SetupDelivery(Delivery("cancelled"));
        SetupObject(PickupKey, 1024);

        var result = await SendAsync(TruckerId, "pickup", PickupKey);

        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_REFUSED: ");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task APendingDelivery_TakesThePickupPhoto_SentBeforeItsPickupSynced()
    {
        SetupDelivery(Delivery("pending"));
        SetupObject(PickupKey, 1024);

        var result = await SendAsync(TruckerId, "pickup", PickupKey);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
    }

    [Fact]
    public async Task AnotherPhotoLandingFirst_IsJudgedOnTheFreshRow()
    {
        var before = Delivery("pickedUp");
        var after = Delivery("pickedUp", pickupPhotoRef: "uploads/photos/del-photo/pickup-1600000000000.webp");
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable && !r.ConsistentRead), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { before } });
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable && r.ConsistentRead), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { after } });
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Throws(new ConditionalCheckFailedException("condition"));
        SetupObject(PickupKey, 1024);

        var result = await SendAsync(TruckerId, "pickup", PickupKey);

        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_EXISTS: ");
    }

    [Fact]
    public async Task AnUnknownDelivery_IsRetried_NotRefusedForGood()
    {
        _dynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });

        var result = await SendAsync(TruckerId, "pickup", PickupKey);

        result.GetProperty("status").GetString().Should().Be("error");
        // No DELIVERY_PHOTO_ prefix: the phone retries (the delivery's create may be on its way).
        MessageOf(result).Should().Be("Delivery not found.");
    }

    // ── ESC-36: the trucker's arrival photo at the drop-off ─────────────

    private const string ArrivalKey = "uploads/photos/del-photo/arrival-1700000000000.webp";

    [Fact]
    public async Task ESC36_WithTheFlagOn_TheTrucker_AttachesTheArrivalPhoto_OnTheRoad()
    {
        SetupDelivery(Delivery("inTransit"));
        SetupObject(ArrivalKey, 120 * 1024);

        var result = await SendAsync(TruckerId, "arrival", ArrivalKey, dropoff: true);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        await _dynamoDb.Received(1).UpdateItemAsync(Arg.Is<UpdateItemRequest>(r =>
            r.ExpressionAttributeNames["#photo"] == "arrivalPhotoRef"
            && r.ExpressionAttributeNames["#photoVersion"] == "arrivalPhotoVersionId"
            && r.ExpressionAttributeValues[":ref"].S == ArrivalKey), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ESC36_WithTheFlagOff_TheArrivalPhotoDoesNotExist()
    {
        SetupDelivery(Delivery("inTransit"));
        SetupObject(ArrivalKey, 120 * 1024);

        var result = await SendAsync(TruckerId, "arrival", ArrivalKey);

        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_INVALID: ");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task ESC36_TheBuyer_CannotAttachTheArrivalPhoto()
    {
        SetupDelivery(Delivery("inTransit"));
        SetupObject(ArrivalKey, 120 * 1024);

        var result = await SendAsync(BuyerId, "arrival", ArrivalKey, dropoff: true);

        MessageOf(result).Should().Be("DELIVERY_PHOTO_FORBIDDEN: Only the trucker takes the arrival photo.");
        await AssertNothingWrittenAsync();
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("delivered")]
    public async Task ESC36_OffTheRoad_TheArrivalPhotoIsRefused(string status)
    {
        SetupDelivery(Delivery(status));
        SetupObject(ArrivalKey, 120 * 1024);

        var result = await SendAsync(TruckerId, "arrival", ArrivalKey, dropoff: true);

        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_REFUSED: ");
        await AssertNothingWrittenAsync();
    }

    /// <summary>
    /// FUP-373: a check-in made offline at the drop-off before T8 syncs after the T8 run, its photo first. The
    /// order cron's own T8 dispute, not yet decided by an admin, takes the arrival photo as late evidence: only the
    /// photo is written, never the status.
    /// </summary>
    [Theory]
    [InlineData(DropoffJudgement.OverdueReason)]
    [InlineData(DropoffJudgement.OutOfRangeReason)]
    public async Task FUP373_TheT8Dispute_TakesTheArrivalPhoto_AsLateEvidence(string reason)
    {
        SetupDelivery(SystemDisputed(reason));
        SetupObject(ArrivalKey, 120 * 1024);

        var result = await SendAsync(TruckerId, "arrival", ArrivalKey, dropoff: true);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        await _dynamoDb.Received(1).UpdateItemAsync(Arg.Is<UpdateItemRequest>(r =>
            r.UpdateExpression == "SET #photo = :ref, #photoVersion = :version"
            && r.ExpressionAttributeNames["#photo"] == "arrivalPhotoRef"
            && r.ExpressionAttributeValues[":ref"].S == ArrivalKey), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("system", DropoffJudgement.SilentBuyerReason, null)]
    [InlineData("system", DropoffJudgement.OverdueReason, "buyer_wins")]
    [InlineData(BuyerId, "not_received", null)]
    [InlineData(TruckerId, DropoffJudgement.OverdueReason, null)]
    public async Task FUP373_AnyOtherDispute_RefusesTheArrivalPhoto(string disputedBy, string reason, string? resolution)
    {
        var item = SystemDisputed(reason);
        item["disputedBy"] = new AttributeValue { S = disputedBy };
        if (resolution != null)
        {
            item["disputeResolution"] = new AttributeValue { S = resolution };
        }

        SetupDelivery(item);
        SetupObject(ArrivalKey, 120 * 1024);

        var result = await SendAsync(TruckerId, "arrival", ArrivalKey, dropoff: true);

        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_REFUSED: ");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task FUP373_WithTheFlagOff_TheT8DisputeTakesNoArrivalPhoto()
    {
        SetupDelivery(SystemDisputed(DropoffJudgement.OverdueReason));
        SetupObject(ArrivalKey, 120 * 1024);

        var result = await SendAsync(TruckerId, "arrival", ArrivalKey);

        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_INVALID: ");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task FUP373_TheBuyer_CannotAttachTheArrivalPhoto_OnTheT8Dispute()
    {
        SetupDelivery(SystemDisputed(DropoffJudgement.OverdueReason));
        SetupObject(ArrivalKey, 120 * 1024);

        var result = await SendAsync(BuyerId, "arrival", ArrivalKey, dropoff: true);

        MessageOf(result).Should().Be("DELIVERY_PHOTO_FORBIDDEN: Only the trucker takes the arrival photo.");
        await AssertNothingWrittenAsync();
    }

    /// <summary>A delivery the order cron disputed at T8 (or T9) with <paramref name="reason"/>.</summary>
    private static Dictionary<string, AttributeValue> SystemDisputed(string reason)
    {
        var item = Delivery("disputed");
        item["disputedBy"] = new AttributeValue { S = DropoffJudgement.SystemActor };
        item["disputeReason"] = new AttributeValue { S = reason };
        item["disputedAt"] = new AttributeValue { S = DateTime.UtcNow.AddMinutes(-30).ToString("o") };
        return item;
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static string MessageOf(JsonElement result) => result.GetProperty("message").GetString() ?? "";

    private void SetupDelivery(Dictionary<string, AttributeValue> item)
        => _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { item } });

    private void SetupObject(string key, long size, string? contentType = null, string? versionId = "v-attached")
    {
        var head = new GetObjectMetadataResponse { ContentLength = size, VersionId = versionId };
        head.Headers.ContentType = contentType ?? DeliveryPhotoKeys.ContentTypeOf(key);
        _s3.GetObjectMetadataAsync(Bucket, key, Arg.Any<CancellationToken>()).Returns(head);
    }

    private static Dictionary<string, AttributeValue> Delivery(
        string status, string truckerId = TruckerId, string? deliveryType = null, string? pickupPhotoRef = null)
    {
        var created = DateTime.UtcNow.AddDays(-1).ToString("o");
        var item = new Dictionary<string, AttributeValue>
        {
            { "deliveryId", new AttributeValue { S = DeliveryId } },
            { "tripId", new AttributeValue { S = "trip-photo" } },
            { "listingId", new AttributeValue { S = "listing-photo" } },
            { "farmerId", new AttributeValue { S = FarmerId } },
            { "truckerId", new AttributeValue { S = truckerId } },
            { "buyerId", new AttributeValue { S = BuyerId } },
            { "status", new AttributeValue { S = status } },
            { "createdAt", new AttributeValue { S = created } },
            { "updatedAt", new AttributeValue { S = created } },
            { "version", new AttributeValue { N = "3" } }
        };
        if (deliveryType != null)
        {
            item["deliveryType"] = new AttributeValue { S = deliveryType };
        }

        if (pickupPhotoRef != null)
        {
            item["pickupPhotoRef"] = new AttributeValue { S = pickupPhotoRef };
        }

        return item;
    }

    private async Task AssertNothingWrittenAsync()
        => await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());

    private async Task NoSignalAsync()
        => await _fraud.DidNotReceive().RecordFraudSignalAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>());

    private Task<JsonElement> SendAsync(string userId, string type, string key, bool withBucket = true, bool dropoff = false)
        => SendRawAsync(userId, $"{DeliveryId}#{type}", "create",
            new Dictionary<string, object> { { "photoRef", key } }, withBucket, dropoff);

    private async Task<JsonElement> SendRawAsync(string userId, string entityId, string action,
        Dictionary<string, object> data, bool withBucket = true, bool dropoff = false)
    {
        var function = new SyncFunction(_syncMeta, Substitute.For<ICropRepository>(), _dynamoDb,
            "test-trips-table", DeliveriesTable, fraudService: _fraud)
        {
            UploadsBucket = withBucket ? Bucket : null,
            DeliveryPhotoS3 = withBucket ? _s3 : null,
            CommitmentsEnabled = true,
            DropoffClassificationEnabled = dropoff,
        };

        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-photo-1",
                ClientId = "device-001",
                Operations = new List<SyncOperationRequest>
                {
                    new()
                    {
                        OperationId = "op-photo-1",
                        IdempotencyKey = $"{userId}:delivery_photo:{entityId}:{action}:{Guid.NewGuid():N}",
                        EntityType = DeliveryPhotoKeys.EntityType,
                        EntityId = entityId,
                        Action = action,
                        Data = data
                    }
                }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext
                {
                    { "userId", userId },
                    { "custom:role", userId.StartsWith("buyer", StringComparison.Ordinal) ? "buyer" : "trucker" }
                }
            }
        };

        var response = await function.FunctionHandler(request, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }
}
