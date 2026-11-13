using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// FUP-140: its own set of tables for the photo copy backfill, which reads the whole deliveries table: rows the
/// other classes seed never reach it.
/// </summary>
[CollectionDefinition(Name)]
public sealed class DeliveryPhotoEvidenceCollection : ICollectionFixture<LocalDynamoDbFixture>
{
    public const string Name = "DeliveryPhotoEvidence";
}

/// <summary>
/// FUP-140 against real S3 and DynamoDB (LocalStack in CI, <c>SERVICES=dynamodb,s3</c>), what the mocked tests cannot
/// show: a server-side copy of a pinned version in a versioned bucket (not the latest one), the attach that records
/// the copy in its conditional write, the evidence still readable once every version of the upload key is gone (what
/// the <c>uploads/photos/</c> expiry does), and the backfill's conditional update on a real row.
/// </summary>
[Collection(DeliveryPhotoEvidenceCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DeliveryPhotoEvidenceLocalStackTests : IAsyncLifetime
{
    private readonly LocalDynamoDbFixture _db;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private readonly TestLambdaContext _context = new();
    private IAmazonS3 _s3 = null!;

    public DeliveryPhotoEvidenceLocalStackTests(LocalDynamoDbFixture db) => _db = db;

    private IAmazonDynamoDB Ddb => _db.Client;

    private string Bucket => $"uploads-fup140-{_run}";

    private string DeliveryId => $"delivery-fup140-{_run}";

    private string TripId => $"trip-fup140-{_run}";

    private string UploadKey => $"uploads/photos/{DeliveryId}/pickup-1700000000000.webp";

    public async Task InitializeAsync()
    {
        var endpoint = LocalDynamoDbEnvironment.Endpoint;
        if (endpoint == null)
        {
            return; // skipped
        }

        _s3 = new AmazonS3Client(new BasicAWSCredentials("test", "test"), new AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION") ?? "us-east-1"
        });
        await _s3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });
        // Versioned, as the uploads bucket is (database-stack.ts).
        await _s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = Bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled }
        });
    }

    public async Task DisposeAsync()
    {
        if (_s3 == null)
        {
            return;
        }

        try
        {
            var versions = await _s3.ListVersionsAsync(new ListVersionsRequest { BucketName = Bucket });
            foreach (var version in versions.Versions ?? new List<S3ObjectVersion>())
            {
                await _s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = Bucket, Key = version.Key, VersionId = version.VersionId });
            }

            await _s3.DeleteBucketAsync(Bucket);
        }
        catch (AmazonServiceException)
        {
            // Best-effort cleanup of a throwaway bucket.
        }

        _s3.Dispose();
    }

    private async Task<string> PutPhotoAsync(string key, string text)
    {
        var response = await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = key,
            ContentType = "image/webp",
            InputStream = new MemoryStream(Encoding.UTF8.GetBytes(text))
        });
        return response.VersionId;
    }

    private async Task<(string Text, string? ContentType)> ReadAsync(string key, string? versionId)
    {
        using var response = await _s3.GetObjectAsync(new GetObjectRequest { BucketName = Bucket, Key = key, VersionId = versionId });
        using var reader = new StreamReader(response.ResponseStream);
        return (await reader.ReadToEndAsync(), response.Headers.ContentType);
    }

    /// <summary>Every version and delete marker of <paramref name="key"/> removed: what the expiry rule ends with.</summary>
    private async Task ExpireAsync(string key)
    {
        var versions = await _s3.ListVersionsAsync(new ListVersionsRequest { BucketName = Bucket, Prefix = key });
        foreach (var version in (versions.Versions ?? new List<S3ObjectVersion>()).Where(v => v.Key == key))
        {
            await _s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = Bucket, Key = key, VersionId = version.VersionId });
        }
    }

    private async Task SeedDeliveryAsync(string status, Dictionary<string, AttributeValue>? extra = null)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["deliveryId"] = new() { S = DeliveryId },
            ["tripId"] = new() { S = TripId },
            ["farmerId"] = new() { S = $"farmer-{_run}" },
            ["truckerId"] = new() { S = $"trucker-{_run}" },
            ["buyerId"] = new() { S = $"buyer-{_run}" },
            ["status"] = new() { S = status },
            ["createdAt"] = new() { S = "2026-11-12T08:00:00.0000000Z" },
            ["updatedAt"] = new() { S = "2026-11-12T08:00:00.0000000Z" },
            ["version"] = new() { N = "3" }
        };
        foreach (var (name, value) in extra ?? new Dictionary<string, AttributeValue>())
        {
            item[name] = value;
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Deliveries, Item = item });
    }

    private async Task<Dictionary<string, AttributeValue>> RowAsync()
        => (await Ddb.GetItemAsync(new GetItemRequest
        {
            TableName = _db.Deliveries,
            Key = new Dictionary<string, AttributeValue> { ["deliveryId"] = new() { S = DeliveryId }, ["tripId"] = new() { S = TripId } },
            ConsistentRead = true
        })).Item;

    private async Task<JsonElement> AttachAsync(string photoRef)
    {
        var syncMeta = Substitute.For<ISyncMetadataRepository>();
        syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        var function = new SyncFunction(syncMeta, new CropRepository(Ddb, _db.Crops), Ddb, _db.Trips, _db.Deliveries,
            fraudService: Substitute.For<IFraudDetectionService>())
        {
            UploadsBucket = Bucket,
            DeliveryPhotoS3 = _s3,
        };
        var trucker = $"trucker-{_run}";
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = $"batch-{_run}",
                ClientId = "device-1",
                Operations = new List<SyncOperationRequest>
                {
                    new()
                    {
                        OperationId = $"op-{Guid.NewGuid():N}",
                        IdempotencyKey = $"{trucker}:delivery_photo:{DeliveryId}#pickup:create:{Guid.NewGuid():N}",
                        EntityType = DeliveryPhotoKeys.EntityType,
                        EntityId = $"{DeliveryId}#pickup",
                        Action = "create",
                        Data = new Dictionary<string, object> { ["photoRef"] = photoRef }
                    }
                }
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", trucker }, { "custom:role", "trucker" } }
            }
        };

        var response = await function.FunctionHandler(request, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }

    [LocalDynamoDbFact]
    public async Task TheCopy_IsOfThePinnedVersion_NotTheLatest()
    {
        var first = await PutPhotoAsync(UploadKey, "the photo the attach pinned");
        await PutPhotoAsync(UploadKey, "a second PUT within the presign window");

        var kept = await DeliveryPhotoEvidence.KeepAsync(_s3, Bucket, UploadKey, first);

        kept.Key.Should().Be($"evidence/photos/{DeliveryId}/pickup-1700000000000.webp");
        kept.VersionId.Should().NotBeNullOrEmpty();
        var (text, contentType) = await ReadAsync(kept.Key, kept.VersionId);
        text.Should().Be("the photo the attach pinned");
        contentType.Should().Be("image/webp");
    }

    [LocalDynamoDbFact]
    public async Task TheAttach_RecordsTheCopy_AndTheEvidenceOutlivesItsUpload()
    {
        await SeedDeliveryAsync("pickedUp", new Dictionary<string, AttributeValue>
        {
            ["pickedUpAt"] = new() { S = "2026-11-12T09:00:00.0000000Z" }
        });
        var uploaded = await PutPhotoAsync(UploadKey, "pickup photo bytes");

        var result = await AttachAsync(UploadKey);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        var row = await RowAsync();
        row["pickupPhotoRef"].S.Should().Be(UploadKey);
        row["pickupPhotoVersionId"].S.Should().Be(uploaded);
        row["pickupPhotoKeptRef"].S.Should().Be(DeliveryPhotoKeys.KeptKeyOf(UploadKey));
        row["pickupPhotoKeptVersionId"].S.Should().NotBeNullOrEmpty();
        // The attach moves neither the row's version nor updatedAt.
        row["version"].N.Should().Be("3");

        // The expiry takes every version of the upload key; the read signs the copy, which is still there.
        await ExpireAsync(UploadKey);
        var signed = DeliveryPhotoUrls.Sign(
            DeliveryFunction.MapToDeliveryDto(DynamoDbHelper.FromItem(row)), DynamoDbHelper.FromItem(row), _s3, Bucket, DateTime.UtcNow);
        signed.PickupPhotoUrl.Should().Contain("evidence/photos/");
        var (text, _) = await ReadAsync(row["pickupPhotoKeptRef"].S, row["pickupPhotoKeptVersionId"].S);
        text.Should().Be("pickup photo bytes");
    }

    [LocalDynamoDbFact]
    public async Task APickupPhotoBeforeItsPickup_Waits_AndCopiesNothing()
    {
        await SeedDeliveryAsync("pending");
        await PutPhotoAsync(UploadKey, "pickup photo bytes");

        var result = await AttachAsync(UploadKey);

        result.GetProperty("status").GetString().Should().Be("deferred");
        (await RowAsync()).Should().NotContainKey("pickupPhotoRef");
        var kept = await _s3.ListVersionsAsync(new ListVersionsRequest { BucketName = Bucket, Prefix = "evidence/" });
        (kept.Versions ?? new List<S3ObjectVersion>()).Should().BeEmpty();
    }

    [LocalDynamoDbFact]
    public async Task TheBackfill_CopiesAPhotoAttachedBeforeTheCopies_AtItsPinnedVersion_Once()
    {
        var pinned = await PutPhotoAsync(UploadKey, "attached before 2.14.0");
        await PutPhotoAsync(UploadKey, "a later PUT the read never signed");
        await SeedDeliveryAsync("delivered", new Dictionary<string, AttributeValue>
        {
            ["pickupPhotoRef"] = new() { S = UploadKey },
            ["pickupPhotoVersionId"] = new() { S = pinned },
            // A key no read signs (another delivery's): skipped, never copied.
            ["dropoffPhotoRef"] = new() { S = "uploads/photos/someone-else/dropoff-1700000000000.jpg" }
        });
        var backfill = new DeliveryPhotoCopyBackfill(Ddb, _db.Deliveries, _s3, Bucket);
        var slice = CronRunBudget.StartNew().Slice(TimeSpan.FromMinutes(4));

        var summary = await backfill.RunAsync(null, slice, _context);

        summary["outcome"].Should().Be("ok");
        summary["copied"].Should().Be("1");
        summary["skipped"].Should().Be("1");
        var row = await RowAsync();
        row["pickupPhotoKeptRef"].S.Should().Be(DeliveryPhotoKeys.KeptKeyOf(UploadKey));
        row.Should().NotContainKey("dropoffPhotoKeptRef");
        row["version"].N.Should().Be("3");
        var (text, _) = await ReadAsync(row["pickupPhotoKeptRef"].S, row["pickupPhotoKeptVersionId"].S);
        text.Should().Be("attached before 2.14.0");

        // A rerun finds nothing left to copy for this row.
        var again = await backfill.RunAsync(null, CronRunBudget.StartNew().Slice(TimeSpan.FromMinutes(4)), _context);

        again["outcome"].Should().Be("ok");
        again["copied"].Should().Be("0");
        var keptVersions = await _s3.ListVersionsAsync(new ListVersionsRequest { BucketName = Bucket, Prefix = "evidence/" });
        (keptVersions.Versions ?? new List<S3ObjectVersion>()).Should().ContainSingle();
    }
}
