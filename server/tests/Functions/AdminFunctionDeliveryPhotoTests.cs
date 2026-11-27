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
using WeredaConnect.Server.Functions;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FRD-12: the admin transaction view (<c>GET /admin/transactions/{orderId}</c>) carries the
/// delivery's handoff photos as presigned GETs, the dispute evidence an admin weighs.
/// </summary>
public sealed class AdminFunctionDeliveryPhotoTests
{
    private const string OrdersTable = "orders-test";
    private const string EscrowsTable = "escrows-test";
    private const string DeliveriesTable = "deliveries-test";
    private const string Bucket = "uploads-test";
    private const string PickupKey = "uploads/photos/delivery-1/pickup-1700000000000.webp";
    private const string DropoffKey = "uploads/photos/delivery-1/dropoff-1700000000000.jpg";

    private readonly IAmazonDynamoDB _db = Substitute.For<IAmazonDynamoDB>();
    private readonly IAmazonS3 _s3 = Substitute.For<IAmazonS3>();
    private readonly TestLambdaContext _context = new();
    private readonly Dictionary<string, AttributeValue> _delivery;

    public AdminFunctionDeliveryPhotoTests()
    {
        var order = new Dictionary<string, AttributeValue>
        {
            ["orderId"] = S("order-1"),
            ["buyerId"] = S("buyer-1"),
            ["farmerId"] = S("farmer-1"),
            ["status"] = S("disputed"),
            ["createdAt"] = S("2026-07-01T08:00:00Z"),
            ["updatedAt"] = S("2026-07-05T00:00:00Z"),
        };
        var escrow = new Dictionary<string, AttributeValue>
        {
            ["escrowId"] = S("escrow-1"),
            ["deliveryId"] = S("delivery-1"),
            ["orderId"] = S("order-1"),
            ["status"] = S("disputed"),
            ["createdAt"] = S("2026-07-01T09:05:00Z"),
            ["updatedAt"] = S("2026-07-05T00:00:00Z"),
        };
        var delivery = _delivery = new Dictionary<string, AttributeValue>
        {
            ["deliveryId"] = S("delivery-1"),
            ["tripId"] = S("trip-1"),
            ["farmerId"] = S("farmer-1"),
            ["truckerId"] = S("trucker-1"),
            ["buyerId"] = S("buyer-1"),
            ["status"] = S("disputed"),
            ["pickupPhotoRef"] = S(PickupKey),
            ["dropoffPhotoRef"] = S(DropoffKey),
            ["dropoffPhotoVersionId"] = S("v-dropoff"),
            ["createdAt"] = S("2026-07-01T09:10:00Z"),
            ["updatedAt"] = S("2026-07-03T14:00:00Z"),
            ["version"] = new AttributeValue { N = "4" },
        };

        _db.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var req = ci.Arg<QueryRequest>();
                List<Dictionary<string, AttributeValue>> items =
                    req.IndexName == "OrderIndex" ? new() { escrow }
                    : req.TableName == OrdersTable ? new() { order }
                    : req.TableName == DeliveriesTable ? new() { delivery }
                    : new();
                return new QueryResponse { Items = items, LastEvaluatedKey = new Dictionary<string, AttributeValue>() };
            });
        _db.BatchGetItemAsync(Arg.Any<BatchGetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BatchGetItemResponse
            {
                Responses = new Dictionary<string, List<Dictionary<string, AttributeValue>>>(),
                UnprocessedKeys = new Dictionary<string, KeysAndAttributes>(),
            });
    }

    [Fact]
    public async Task TheTransactionView_CarriesBothPhotos()
    {
        _s3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>())
            .Returns(c => "https://signed/" + c.Arg<GetPreSignedUrlRequest>().Key);

        var delivery = await GetDeliveryAsync(Function(withBucket: true));

        delivery.GetProperty("pickupPhotoUrl").GetString().Should().Be("https://signed/" + PickupKey);
        delivery.GetProperty("dropoffPhotoUrl").GetString().Should().Be("https://signed/" + DropoffKey);
        _s3.Received(2).GetPreSignedURL(Arg.Is<GetPreSignedUrlRequest>(r =>
            r.Verb == HttpVerb.GET && r.BucketName == Bucket && r.Expires > DateTime.UtcNow));
        // The attached version: a later PUT to the key cannot swap the evidence.
        _s3.Received(1).GetPreSignedURL(Arg.Is<GetPreSignedUrlRequest>(r =>
            r.Key == DropoffKey && r.VersionId == "v-dropoff"));
    }

    [Fact]
    public async Task FUP140_TheTransactionView_SignsTheKeptCopy()
    {
        // The upload key may have expired under uploads/photos/: the admin signs the kept copy the attach made.
        _delivery["dropoffPhotoKeptRef"] = S("evidence/photos/delivery-1/dropoff-1700000000000.jpg");
        _delivery["dropoffPhotoKeptVersionId"] = S("v-kept");
        _s3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>())
            .Returns(c => "https://signed/" + c.Arg<GetPreSignedUrlRequest>().Key);

        var delivery = await GetDeliveryAsync(Function(withBucket: true));

        delivery.GetProperty("dropoffPhotoUrl").GetString().Should()
            .Be("https://signed/evidence/photos/delivery-1/dropoff-1700000000000.jpg");
        delivery.GetProperty("dropoffPhotoRef").GetString().Should().Be(DropoffKey);
        _s3.Received(1).GetPreSignedURL(Arg.Is<GetPreSignedUrlRequest>(r =>
            r.Key == "evidence/photos/delivery-1/dropoff-1700000000000.jpg" && r.VersionId == "v-kept"));
        // A photo attached before the copies (the backfill has not reached it) still signs its upload key.
        delivery.GetProperty("pickupPhotoUrl").GetString().Should().Be("https://signed/" + PickupKey);
    }

    [Fact]
    public async Task WithoutTheBucket_TheViewHasNoPhotoUrl()
    {
        var delivery = await GetDeliveryAsync(Function(withBucket: false));

        delivery.TryGetProperty("pickupPhotoUrl", out _).Should().BeFalse();
        delivery.GetProperty("pickupPhotoRef").GetString().Should().Be(PickupKey);
    }

    [Fact]
    public async Task ASigningFailure_StillAnswersTheTransaction()
    {
        _s3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>()).Throws(new AmazonS3Exception("boom"));

        var delivery = await GetDeliveryAsync(Function(withBucket: true));

        delivery.TryGetProperty("dropoffPhotoUrl", out _).Should().BeFalse();
    }

    private AdminFunction Function(bool withBucket) =>
        new(_db, "users-test", OrdersTable, EscrowsTable, DeliveriesTable, "escrow-audit-test")
        {
            UploadsBucket = withBucket ? Bucket : null,
            DeliveryPhotoS3 = withBucket ? _s3 : null,
        };

    private async Task<JsonElement> GetDeliveryAsync(AdminFunction function)
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = "/admin/transactions/order-1",
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } },
            },
        };

        var response = await function.FunctionHandler(request, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("delivery").Clone();
    }

    private static AttributeValue S(string v) => new() { S = v };
}
