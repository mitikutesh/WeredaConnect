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
    private readonly ITransactionAuditRepository _audit = Substitute.For<ITransactionAuditRepository>();
    private readonly List<Dictionary<string, object>> _auditRows = new();
    private readonly TestLambdaContext _context = new();

    public SyncFunctionDeliveryPhotoTests()
    {
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());
        _audit.AppendAsync(Arg.Do<Dictionary<string, object>>(e => _auditRows.Add(e))).Returns(Task.CompletedTask);
    }

    // ── Who may attach (role rules) ─────────────────────────────────────

    [Theory]
    [InlineData("pickup", PickupKey, TruckerId, "pickupPhotoRef", "pickedUp")]
    [InlineData("dropoff", DropoffKey, BuyerId, "dropoffPhotoRef", "delivered")]
    public async Task ThePhotosTaker_AttachesIt_WithoutMovingTheVersion(
        string type, string key, string caller, string field, string status)
    {
        SetupDelivery(Delivery(status));
        SetupObject(key, 150 * 1024);

        var result = await SendAsync(caller, type, key);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        result.GetProperty("serverVersion").GetInt32().Should().Be(3);
        await _dynamoDb.Received(1).UpdateItemAsync(Arg.Is<UpdateItemRequest>(r =>
            r.TableName == DeliveriesTable
            && r.Key["deliveryId"].S == DeliveryId
            && r.Key["tripId"].S == "trip-photo"
            && r.UpdateExpression == "SET #photo = :ref, #kept = :kept, #keptVersion = :keptVersion, #photoVersion = :version"
            && r.ExpressionAttributeNames["#photo"] == field
            && r.ExpressionAttributeValues[":ref"].S == key
            && r.ExpressionAttributeNames["#photoVersion"] == field.Replace("Ref", "VersionId")
            && r.ExpressionAttributeValues[":version"].S == "v-attached"
            // FUP-140: the kept copy, recorded in the same write.
            && r.ExpressionAttributeNames["#kept"] == field.Replace("Ref", "KeptRef")
            && r.ExpressionAttributeValues[":kept"].S == DeliveryPhotoKeys.KeptKeyOf(key)
            && r.ExpressionAttributeNames["#keptVersion"] == field.Replace("Ref", "KeptVersionId")
            && r.ExpressionAttributeValues[":keptVersion"].S == "v-kept"
            && r.ConditionExpression.Contains("attribute_not_exists(#photo)")
            && !r.ExpressionAttributeNames.Values.Contains("version")
            && !r.ExpressionAttributeNames.Values.Contains("updatedAt")), Arg.Any<CancellationToken>());
        await NoSignalAsync();
    }

    // ── FUP-140: the kept copy, made before the write ───────────────────

    [Fact]
    public async Task FUP140_TheVersionTheHeadSaw_IsCopiedToTheKeptKey_BeforeTheWrite()
    {
        SetupDelivery(Delivery("pickedUp"));
        SetupObject(PickupKey, 1024);

        var result = await SendAsync(TruckerId, "pickup", PickupKey);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        DeliveryPhotoKeys.KeptKeyOf(PickupKey).Should().Be("evidence/photos/del-photo/pickup-1700000000000.webp");
        Received.InOrder(() =>
        {
            _s3.CopyObjectAsync(Arg.Is<CopyObjectRequest>(r =>
                r.SourceBucket == Bucket && r.SourceKey == PickupKey && r.SourceVersionId == "v-attached"
                && r.DestinationBucket == Bucket && r.DestinationKey == "evidence/photos/del-photo/pickup-1700000000000.webp"),
                Arg.Any<CancellationToken>());
            _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task FUP140_ACopyThatFails_IsDeferred_AndNothingIsWritten()
    {
        // Without its kept copy the photo would be lost when uploads/photos/ expires: the attach waits instead.
        SetupDelivery(Delivery("pickedUp"));
        SetupObject(PickupKey, 1024);
        _s3.CopyObjectAsync(Arg.Any<CopyObjectRequest>(), Arg.Any<CancellationToken>())
            .Throws(new AmazonS3Exception("Access Denied") { StatusCode = HttpStatusCode.Forbidden });

        var result = await SendAsync(TruckerId, "pickup", PickupKey);

        result.GetProperty("status").GetString().Should().Be("deferred");
        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_PENDING: ");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task FUP140_ARetryAfterTheAttach_CopiesNothingAgain()
    {
        SetupDelivery(Delivery("pickedUp", pickupPhotoRef: PickupKey));

        var result = await SendAsync(TruckerId, "pickup", PickupKey);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        await _s3.DidNotReceive().CopyObjectAsync(Arg.Any<CopyObjectRequest>(), Arg.Any<CancellationToken>());
        await AssertNothingWrittenAsync();
    }

    // ── FUP-140: a handoff photo waits for its handoff ──────────────────

    [Theory]
    [InlineData("pickup", PickupKey, TruckerId, "pending", null, null)]
    [InlineData("dropoff", DropoffKey, BuyerId, "pickedUp", "2026-11-12T08:00:00Z", null)]
    [InlineData("dropoff", DropoffKey, BuyerId, "inTransit", "2026-11-12T08:00:00Z", null)]
    public async Task FUP140_APhotoWhoseHandoffIsNotOnTheRow_Waits(
        string type, string key, string caller, string status, string? pickedUpAt, string? confirmedAt)
    {
        // The handoff may still be on its way (deferred itself), or the server never took it (refused, or
        // reverted by a conflict): either way the photo must not attach yet. The phone drops it when it learns.
        SetupDelivery(Delivery(status, pickedUpAt: pickedUpAt, confirmedAt: confirmedAt));
        SetupObject(key, 1024);

        var result = await SendAsync(caller, type, key);

        result.GetProperty("status").GetString().Should().Be("deferred", result.ToString());
        MessageOf(result).Should().Be(
            "DELIVERY_PHOTO_PENDING: The handoff has not reached the server yet. Will retry automatically.");
        await _s3.DidNotReceive().GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _s3.DidNotReceive().CopyObjectAsync(Arg.Any<CopyObjectRequest>(), Arg.Any<CancellationToken>());
        await AssertNothingWrittenAsync();
    }

    [Theory]
    [InlineData("pickup", PickupKey, TruckerId, "inTransit", null, null)]
    [InlineData("pickup", PickupKey, TruckerId, "delivered", null, null)]
    // A dispute after the pickup: the pickup happened (pickedUpAt).
    [InlineData("pickup", PickupKey, TruckerId, "disputed", "2026-11-12T08:00:00Z", null)]
    // A dispute after the confirm: the confirm happened (confirmedAt).
    [InlineData("dropoff", DropoffKey, BuyerId, "disputed", "2026-11-12T08:00:00Z", "2026-11-12T14:00:00Z")]
    public async Task FUP140_APhotoWhoseHandoffIsOnTheRow_Attaches(
        string type, string key, string caller, string status, string? pickedUpAt, string? confirmedAt)
    {
        SetupDelivery(Delivery(status, pickedUpAt: pickedUpAt, confirmedAt: confirmedAt));
        SetupObject(key, 1024);

        var result = await SendAsync(caller, type, key);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
    }

    [Fact]
    public async Task FUP140_ACancelledDelivery_IsStillRefused_NotLeftWaiting()
    {
        SetupDelivery(Delivery("cancelled"));
        SetupObject(PickupKey, 1024);

        var result = await SendAsync(TruckerId, "pickup", PickupKey);

        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_REFUSED: ");
    }

    [Fact]
    public async Task FUP140_AStaleRead_IsReadAgainConsistently_BeforeThePhotoWaits()
    {
        // The pickup landed a moment ago (earlier in this batch, or the web's handoff call): the eventually
        // consistent read may still show it pending. The consistent read decides.
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable && !r.ConsistentRead), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { Delivery("pending") } });
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable && r.ConsistentRead), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>> { Delivery("pickedUp", pickedUpAt: "2026-11-12T08:00:00Z") }
            });
        SetupObject(PickupKey, 1024);

        var result = await SendAsync(TruckerId, "pickup", PickupKey);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        await _dynamoDb.Received(1).QueryAsync(Arg.Is<QueryRequest>(r => r.ConsistentRead), Arg.Any<CancellationToken>());
        await _dynamoDb.Received(1).UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FUP140_APhotoThatWaits_WasJudgedOnAConsistentRead()
    {
        SetupDelivery(Delivery("pending"));
        SetupObject(PickupKey, 1024);

        var result = await SendAsync(TruckerId, "pickup", PickupKey);

        result.GetProperty("status").GetString().Should().Be("deferred");
        await _dynamoDb.Received(1).QueryAsync(Arg.Is<QueryRequest>(r => r.ConsistentRead), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FUP140_TheConsistentReadFailing_IsDeferred()
    {
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable && !r.ConsistentRead), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { Delivery("pending") } });
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable && r.ConsistentRead), Arg.Any<CancellationToken>())
            .Throws(new ProvisionedThroughputExceededException("throttled"));
        SetupObject(PickupKey, 1024);

        var result = await SendAsync(TruckerId, "pickup", PickupKey);

        result.GetProperty("status").GetString().Should().Be("deferred");
        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_PENDING: The delivery could not be read.");
        await AssertNothingWrittenAsync();
    }

    // ── FUP-140: the buyer's drop-off photo on a system dispute (late evidence) ──

    /// <summary>
    /// The buyer confirmed offline before the order cron's T8 (or T9) run and synced after: the confirm is refused,
    /// but the photo of what arrived is kept as late evidence on the dispute no admin has decided, marked late like
    /// FUP-373's arrival photo. Only the photo is written, never the status.
    /// </summary>
    [Theory]
    [InlineData(DropoffJudgement.OverdueReason)]
    [InlineData(DropoffJudgement.OutOfRangeReason)]
    [InlineData(DropoffJudgement.SilentBuyerReason)]
    public async Task FUP140_ASystemDispute_TakesTheBuyersDropoffPhoto_AsLateEvidence(string reason)
    {
        SetupDelivery(SystemDisputed(reason));
        SetupObject(DropoffKey, 120 * 1024);
        var before = DateTime.UtcNow.AddSeconds(-1);

        var result = await SendAsync(BuyerId, "dropoff", DropoffKey);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        await _dynamoDb.Received(1).UpdateItemAsync(Arg.Is<UpdateItemRequest>(r =>
            r.UpdateExpression == "SET #photo = :ref, #kept = :kept, #keptVersion = :keptVersion, #photoVersion = :version, #lateAt = :lateAt"
            && r.ExpressionAttributeNames["#photo"] == "dropoffPhotoRef"
            && r.ExpressionAttributeNames["#kept"] == "dropoffPhotoKeptRef"
            && r.ExpressionAttributeValues[":kept"].S == DeliveryPhotoKeys.KeptKeyOf(DropoffKey)
            && r.ExpressionAttributeNames["#lateAt"] == DropoffJudgement.DropoffPhotoLateAtField
            && r.ExpressionAttributeNames["#resolution"] == "disputeResolution"
            && r.ConditionExpression.EndsWith(" AND #status = :disputed AND attribute_not_exists(#resolution)")
            && !r.UpdateExpression.Contains("#status")
            && !r.ExpressionAttributeNames.Values.Contains("version")), Arg.Any<CancellationToken>());
        var write = _dynamoDb.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<UpdateItemRequest>().Single();
        OrderCommitment.TryParseUtc(write.ExpressionAttributeValues[":lateAt"].S, out var lateAt).Should().BeTrue();
        lateAt.Should().BeOnOrAfter(before);
        // The admin's timeline row, as for the late arrival photo.
        var audit = _auditRows.Should().ContainSingle().Subject;
        audit["entityType"].Should().Be("delivery");
        audit["entityId"].Should().Be(DeliveryId);
        audit["action"].Should().Be("delivery_photo");
        audit["metadata"].Should().Be("type=dropoff;late=true");
    }

    [Theory]
    // An admin decided the dispute: nothing more is taken.
    [InlineData("system", DropoffJudgement.OverdueReason, "buyer_wins")]
    // A dispute nobody's cron opened, without a confirm (an admin's disposition before the drop-off).
    [InlineData("admin-1", "not_delivered", "refund_buyer")]
    [InlineData(TruckerId, DropoffJudgement.OverdueReason, null)]
    public async Task FUP140_AnyOtherDisputeWithoutAConfirm_RefusesTheDropoffPhotoForGood(
        string disputedBy, string reason, string? resolution)
    {
        // The confirm can no longer come, so the photo is refused (final, silent) instead of waiting forever.
        var item = SystemDisputed(reason);
        item["disputedBy"] = new AttributeValue { S = disputedBy };
        if (resolution != null)
        {
            item["disputeResolution"] = new AttributeValue { S = resolution };
        }

        SetupDelivery(item);
        SetupObject(DropoffKey, 1024);

        var result = await SendAsync(BuyerId, "dropoff", DropoffKey);

        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_REFUSED: ");
        await _s3.DidNotReceive().CopyObjectAsync(Arg.Any<CopyObjectRequest>(), Arg.Any<CancellationToken>());
        await AssertNothingWrittenAsync();
        _auditRows.Should().BeEmpty();
    }

    [Fact]
    public async Task FUP140_APickupPhotoOnADisputeBeforeThePickup_IsRefusedForGood()
    {
        var item = SystemDisputed(DropoffJudgement.OverdueReason);
        item["disputedBy"] = new AttributeValue { S = "admin-1" };
        SetupDelivery(item);
        SetupObject(PickupKey, 1024);

        var result = await SendAsync(TruckerId, "pickup", PickupKey);

        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_REFUSED: ");
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task FUP140_AVerdictLandingBeforeTheWrite_RefusesTheLateDropoffPhoto()
    {
        var decided = SystemDisputed(DropoffJudgement.OverdueReason);
        decided["disputeResolution"] = new AttributeValue { S = "buyer_wins" };
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable), Arg.Any<CancellationToken>())
            .Returns(
                new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { SystemDisputed(DropoffJudgement.OverdueReason) } },
                new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { decided } });
        SetupObject(DropoffKey, 1024);
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns<UpdateItemResponse>(_ => throw new ConditionalCheckFailedException("verdict"));

        var result = await SendAsync(BuyerId, "dropoff", DropoffKey);

        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_REFUSED: ");
        _auditRows.Should().BeEmpty();
    }

    [Fact]
    public async Task FUP140_TheDropoffPhotoOfAConfirmedThenDisputedDelivery_IsNotLate()
    {
        // The buyer confirmed, then disputed within the window: the confirm is on the row, the photo is on time.
        var item = SystemDisputed(DropoffJudgement.OverdueReason);
        item["disputedBy"] = new AttributeValue { S = BuyerId };
        item["confirmedAt"] = new AttributeValue { S = "2026-11-12T14:00:00Z" };
        SetupDelivery(item);
        SetupObject(DropoffKey, 1024);

        var result = await SendAsync(BuyerId, "dropoff", DropoffKey);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        await _dynamoDb.Received(1).UpdateItemAsync(Arg.Is<UpdateItemRequest>(r =>
            !r.ExpressionAttributeNames.ContainsKey("#lateAt")), Arg.Any<CancellationToken>());
        _auditRows.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public async Task WithoutAnS3Version_OnlyTheKeyIsWritten(string? versionId)
    {
        SetupDelivery(Delivery("pickedUp"));
        SetupObject(PickupKey, 1024, versionId: versionId, keptVersionId: versionId);

        var result = await SendAsync(TruckerId, "pickup", PickupKey);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        // FUP-140: the copy is of the latest and is still recorded, without a version.
        await _s3.Received(1).CopyObjectAsync(Arg.Is<CopyObjectRequest>(r => r.SourceVersionId == null),
            Arg.Any<CancellationToken>());
        await _dynamoDb.Received(1).UpdateItemAsync(Arg.Is<UpdateItemRequest>(r =>
            r.UpdateExpression == "SET #photo = :ref, #kept = :kept"
            && !r.ExpressionAttributeValues.ContainsKey(":version")
            && !r.ExpressionAttributeValues.ContainsKey(":keptVersion")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASelfPickupBuyer_AttachesTheDropoffPhoto()
    {
        SetupDelivery(Delivery("delivered", truckerId: "", deliveryType: "self_pickup", confirmedAt: "2026-11-12T14:00:00Z"));
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
    public async Task FUP140_APendingDelivery_KeepsThePickupPhotoWaiting_UntilItsPickupSynced()
    {
        // Before 2.14.0 this attached the photo to a delivery that was never picked up when the pickup was then
        // reverted by a conflict; now the photo waits, and attaches once the pickup is on the row.
        SetupDelivery(Delivery("pending"));
        SetupObject(PickupKey, 1024);

        var waiting = await SendAsync(TruckerId, "pickup", PickupKey);

        waiting.GetProperty("status").GetString().Should().Be("deferred");
        await AssertNothingWrittenAsync();

        SetupDelivery(Delivery("pickedUp", pickedUpAt: "2026-11-12T08:00:00Z"));

        var attached = await SendAsync(TruckerId, "pickup", PickupKey);

        attached.GetProperty("status").GetString().Should().Be("success", attached.ToString());
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
            && r.ExpressionAttributeValues[":ref"].S == ArrivalKey
            // FUP-373: on the road it is no late evidence: no marker, no verdict condition, no audit row.
            && !r.ExpressionAttributeNames.ContainsKey("#lateAt")
            && !r.ConditionExpression.Contains("#resolution")), Arg.Any<CancellationToken>());
        _auditRows.Should().BeEmpty();
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

        var before = DateTime.UtcNow.AddSeconds(-1);

        var result = await SendAsync(TruckerId, "arrival", ArrivalKey, dropoff: true);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        // Marked late with the server's time, and only while no verdict is stamped (the status stays disputed).
        // FUP-140: the late photo records its kept copy in the same write, like any other.
        await _dynamoDb.Received(1).UpdateItemAsync(Arg.Is<UpdateItemRequest>(r =>
            r.UpdateExpression == "SET #photo = :ref, #kept = :kept, #keptVersion = :keptVersion, #photoVersion = :version, #lateAt = :lateAt"
            && r.ExpressionAttributeNames["#photo"] == "arrivalPhotoRef"
            && r.ExpressionAttributeNames["#kept"] == "arrivalPhotoKeptRef"
            && r.ExpressionAttributeValues[":kept"].S == DeliveryPhotoKeys.KeptKeyOf(ArrivalKey)
            && r.ExpressionAttributeNames["#keptVersion"] == "arrivalPhotoKeptVersionId"
            && r.ExpressionAttributeValues[":keptVersion"].S == "v-kept"
            && r.ExpressionAttributeNames["#lateAt"] == DropoffJudgement.ArrivalPhotoLateAtField
            && r.ExpressionAttributeNames["#resolution"] == "disputeResolution"
            && r.ConditionExpression.EndsWith(" AND #status = :disputed AND attribute_not_exists(#resolution)")
            && r.ExpressionAttributeValues[":disputed"].S == "disputed"
            && r.ExpressionAttributeValues[":ref"].S == ArrivalKey
            && !r.ExpressionAttributeNames.Values.Contains("version")), Arg.Any<CancellationToken>());
        var write = _dynamoDb.ReceivedCalls().Select(c => c.GetArguments()[0]).OfType<UpdateItemRequest>().Single();
        OrderCommitment.TryParseUtc(write.ExpressionAttributeValues[":lateAt"].S, out var lateAt).Should().BeTrue();
        lateAt.Should().BeOnOrAfter(before);
        // The admin's timeline row for it.
        var audit = _auditRows.Should().ContainSingle().Subject;
        audit["entityType"].Should().Be("delivery");
        audit["entityId"].Should().Be(DeliveryId);
        audit["action"].Should().Be("delivery_photo");
        audit["metadata"].Should().Be("type=arrival;late=true");
    }

    [Fact]
    public async Task FUP373_AVerdictLandingBeforeTheWrite_RefusesTheLatePhoto()
    {
        // The verdict lands between the read and the write: the condition fails, the fresh row carries it.
        var decided = SystemDisputed(DropoffJudgement.OverdueReason);
        decided["disputeResolution"] = new AttributeValue { S = "buyer_wins" };
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable), Arg.Any<CancellationToken>())
            .Returns(
                new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { SystemDisputed(DropoffJudgement.OverdueReason) } },
                new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { decided } });
        SetupObject(ArrivalKey, 120 * 1024);
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns<UpdateItemResponse>(_ => throw new ConditionalCheckFailedException("verdict"));

        var result = await SendAsync(TruckerId, "arrival", ArrivalKey, dropoff: true);

        MessageOf(result).Should().StartWith("DELIVERY_PHOTO_REFUSED: ");
        _auditRows.Should().BeEmpty();
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

    private void SetupObject(
        string key, long size, string? contentType = null, string? versionId = "v-attached", string? keptVersionId = "v-kept")
    {
        var head = new GetObjectMetadataResponse { ContentLength = size, VersionId = versionId };
        head.Headers.ContentType = contentType ?? DeliveryPhotoKeys.ContentTypeOf(key);
        _s3.GetObjectMetadataAsync(Bucket, key, Arg.Any<CancellationToken>()).Returns(head);
        _s3.CopyObjectAsync(Arg.Is<CopyObjectRequest>(r => r.SourceKey == key), Arg.Any<CancellationToken>())
            .Returns(new CopyObjectResponse { VersionId = keptVersionId });
    }

    private static Dictionary<string, AttributeValue> Delivery(
        string status, string truckerId = TruckerId, string? deliveryType = null, string? pickupPhotoRef = null,
        string? pickedUpAt = null, string? confirmedAt = null)
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

        if (pickedUpAt != null)
        {
            item["pickedUpAt"] = new AttributeValue { S = pickedUpAt };
        }

        if (confirmedAt != null)
        {
            item["confirmedAt"] = new AttributeValue { S = confirmedAt };
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
            "test-trips-table", DeliveriesTable, fraudService: _fraud, transactionAuditRepo: _audit)
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
