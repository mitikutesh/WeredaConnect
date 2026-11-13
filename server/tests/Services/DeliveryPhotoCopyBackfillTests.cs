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
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FUP-140: the one-off <c>backfill_delivery_photo_copies</c> action over an in-memory deliveries table (Scan pages
/// with the filter applied, conditional updates) and a substitute S3, so each decision is visible: which photos are
/// copied and recorded, which are skipped, missing or left for the next run, and the cursor. The real S3 copy and
/// the real conditions run in <c>DeliveryPhotoEvidenceLocalStackTests</c>.
/// </summary>
public sealed class DeliveryPhotoCopyBackfillTests
{
    private const string DeliveriesTable = "deliveries-test";
    private const string Bucket = "uploads-test";

    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly IAmazonS3 _s3 = Substitute.For<IAmazonS3>();
    private readonly List<Dictionary<string, AttributeValue>> _rows = new();
    private readonly List<ScanRequest> _scans = new();
    private readonly List<UpdateItemRequest> _updates = new();
    private readonly TestLambdaContext _context = new();
    private TimeSpan _elapsed = TimeSpan.Zero;
    private TimeSpan _perRowCopy = TimeSpan.Zero;
    private Func<UpdateItemRequest, Exception?> _updateFails = _ => null;
    private Func<CopyObjectRequest, Exception?> _copyFails = _ => null;

    public DeliveryPhotoCopyBackfillTests()
    {
        _ddb.ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>()).Returns(call => Scan(call.Arg<ScanRequest>()));
        _ddb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>()).Returns(call => Update(call.Arg<UpdateItemRequest>()));
        _s3.CopyObjectAsync(Arg.Any<CopyObjectRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<CopyObjectRequest>();
            _elapsed += _perRowCopy;
            if (_copyFails(request) is { } failure)
            {
                throw failure;
            }

            return new CopyObjectResponse { VersionId = "kept-of-" + (request.SourceVersionId ?? "latest") };
        });
    }

    private static AttributeValue S(string value) => new() { S = value };

    private static string Key(string deliveryId, string type) => $"uploads/photos/{deliveryId}/{type}-1700000000000.webp";

    private DeliveryPhotoCopyBackfill Backfill(int pageSize = 2)
        => new(_ddb, DeliveriesTable, _s3, Bucket) { PageSize = pageSize };

    private CronRunSlice Slice() => new CronRunBudget(() => _elapsed).Slice(TimeSpan.FromMinutes(4));

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    private Dictionary<string, AttributeValue> Row(string deliveryId, params (string Type, string? Ref, string? Version, string? Kept)[] photos)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["deliveryId"] = S(deliveryId),
            ["tripId"] = S("trip-" + deliveryId),
            ["status"] = S("delivered"),
            ["version"] = new AttributeValue { N = "7" }
        };
        foreach (var (type, photoRef, version, kept) in photos)
        {
            if (photoRef != null)
            {
                item[DeliveryPhotoKeys.FieldOf(type)] = S(photoRef);
            }

            if (version != null)
            {
                item[DeliveryPhotoKeys.VersionFieldOf(type)] = S(version);
            }

            if (kept != null)
            {
                item[DeliveryPhotoKeys.KeptFieldOf(type)] = S(kept);
            }
        }

        _rows.Add(item);
        return item;
    }

    private Task<ScanResponse> Scan(ScanRequest request)
    {
        _scans.Add(new ScanRequest
        {
            FilterExpression = request.FilterExpression,
            ProjectionExpression = request.ProjectionExpression,
            ExpressionAttributeNames = new Dictionary<string, string>(request.ExpressionAttributeNames),
            ExclusiveStartKey = request.ExclusiveStartKey == null ? null : new Dictionary<string, AttributeValue>(request.ExclusiveStartKey),
            Limit = request.Limit
        });
        var start = 0;
        if (request.ExclusiveStartKey is { Count: > 0 } after)
        {
            start = _rows.FindIndex(r => r["deliveryId"].S == after["deliveryId"].S && r["tripId"].S == after["tripId"].S) + 1;
        }

        var page = _rows.Skip(start).Take(request.Limit > 0 ? request.Limit : 100).ToList();
        var last = start + page.Count < _rows.Count && page.Count > 0
            ? new Dictionary<string, AttributeValue> { ["deliveryId"] = page[^1]["deliveryId"], ["tripId"] = page[^1]["tripId"] }
            : new Dictionary<string, AttributeValue>();
        // The filter as DynamoDB applies it: some photo recorded without its kept copy.
        var types = new[] { DeliveryPhotoKeys.Pickup, DeliveryPhotoKeys.Dropoff, DeliveryPhotoKeys.Arrival };
        var matching = page
            .Where(r => types.Any(t => r.ContainsKey(DeliveryPhotoKeys.FieldOf(t)) && !r.ContainsKey(DeliveryPhotoKeys.KeptFieldOf(t))))
            .Select(r => new Dictionary<string, AttributeValue>(r))
            .ToList();
        return Task.FromResult(new ScanResponse { Items = matching, LastEvaluatedKey = last });
    }

    private Task<UpdateItemResponse> Update(UpdateItemRequest request)
    {
        _updates.Add(request);
        if (_updateFails(request) is { } failure)
        {
            throw failure;
        }

        var row = _rows.Single(r => r["deliveryId"].S == request.Key["deliveryId"].S && r["tripId"].S == request.Key["tripId"].S);
        var names = request.ExpressionAttributeNames;
        var values = request.ExpressionAttributeValues;
        // The condition as written: the photo unchanged, no copy yet, the version unchanged (or still none).
        var photoSame = row.TryGetValue(names["#photo"], out var photo) && photo.S == values[":ref"].S;
        var noCopy = !row.ContainsKey(names["#kept"]);
        var versionSame = values.TryGetValue(":version", out var version)
            ? row.TryGetValue(names["#version"], out var stored) && stored.S == version.S
            : !row.ContainsKey(names["#version"]);
        if (!(photoSame && noCopy && versionSame))
        {
            throw new ConditionalCheckFailedException("condition");
        }

        row[names["#kept"]] = values[":kept"];
        if (names.TryGetValue("#keptVersion", out var keptVersionField))
        {
            row[keptVersionField] = values[":keptVersion"];
        }

        return Task.FromResult(new UpdateItemResponse());
    }

    [Fact]
    public async Task EachRecordedPhoto_IsCopiedAtItsVersion_AndRecorded_WithoutMovingTheRow()
    {
        var row = Row("d-1",
            (DeliveryPhotoKeys.Pickup, Key("d-1", "pickup"), "v-pickup", null),
            (DeliveryPhotoKeys.Dropoff, Key("d-1", "dropoff"), null, null));

        var summary = await Backfill().RunAsync(null, Slice(), _context);

        summary["outcome"].Should().Be("ok");
        summary["copied"].Should().Be("2");
        summary["deliveries"].Should().Be("1");
        summary.Should().NotContainKey("scanCursor");
        await _s3.Received(1).CopyObjectAsync(Arg.Is<CopyObjectRequest>(r =>
            r.SourceBucket == Bucket && r.SourceKey == Key("d-1", "pickup") && r.SourceVersionId == "v-pickup"
            && r.DestinationBucket == Bucket && r.DestinationKey == "evidence/photos/d-1/pickup-1700000000000.webp"),
            Arg.Any<CancellationToken>());
        // No version recorded: the latest, which is what a read signed.
        await _s3.Received(1).CopyObjectAsync(Arg.Is<CopyObjectRequest>(r =>
            r.SourceKey == Key("d-1", "dropoff") && r.SourceVersionId == null), Arg.Any<CancellationToken>());
        row["pickupPhotoKeptRef"].S.Should().Be("evidence/photos/d-1/pickup-1700000000000.webp");
        row["pickupPhotoKeptVersionId"].S.Should().Be("kept-of-v-pickup");
        row["dropoffPhotoKeptRef"].S.Should().Be("evidence/photos/d-1/dropoff-1700000000000.webp");
        row["dropoffPhotoKeptVersionId"].S.Should().Be("kept-of-latest");
        // Only the copy fields: no version, no updatedAt, so no phone sees a conflict and no push goes out.
        _updates.Should().OnlyContain(u => !u.ExpressionAttributeNames.Values.Contains("version")
                                           && !u.ExpressionAttributeNames.Values.Contains("updatedAt"));
        _updates[0].ConditionExpression.Should().Be("#photo = :ref AND attribute_not_exists(#kept) AND #version = :version");
        _updates[1].ConditionExpression.Should().Be("#photo = :ref AND attribute_not_exists(#kept) AND attribute_not_exists(#version)");
    }

    [Fact]
    public async Task TheScan_AsksOnlyForPhotosWithoutTheirCopy_AndReadsTheArrivalToo()
    {
        Row("d-1", (DeliveryPhotoKeys.Arrival, Key("d-1", "arrival"), "v-arrival", null));

        var summary = await Backfill().RunAsync(null, Slice(), _context);

        summary["copied"].Should().Be("1");
        var scan = _scans.Single();
        scan.FilterExpression.Should().Be(
            "(attribute_exists(#r0) AND attribute_not_exists(#k0)) OR (attribute_exists(#r1) AND attribute_not_exists(#k1)) "
            + "OR (attribute_exists(#r2) AND attribute_not_exists(#k2))");
        scan.ExpressionAttributeNames["#r2"].Should().Be("arrivalPhotoRef");
        scan.ExpressionAttributeNames["#k2"].Should().Be("arrivalPhotoKeptRef");
    }

    [Fact]
    public async Task APhotoWithItsCopy_IsLeftAlone_ItsSiblingIsCopied()
    {
        Row("d-1",
            (DeliveryPhotoKeys.Pickup, Key("d-1", "pickup"), "v-pickup", "evidence/photos/d-1/pickup-1700000000000.webp"),
            (DeliveryPhotoKeys.Dropoff, Key("d-1", "dropoff"), "v-dropoff", null));
        Row("d-2", (DeliveryPhotoKeys.Pickup, Key("d-2", "pickup"), "v", "evidence/photos/d-2/pickup-1700000000000.webp"));

        var summary = await Backfill().RunAsync(null, Slice(), _context);

        summary["copied"].Should().Be("1");
        summary["deliveries"].Should().Be("1");
        await _s3.Received(1).CopyObjectAsync(Arg.Any<CopyObjectRequest>(), Arg.Any<CancellationToken>());
        await _s3.Received(1).CopyObjectAsync(Arg.Is<CopyObjectRequest>(r => r.SourceKey == Key("d-1", "dropoff")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AKeyOfAnotherShape_IsSkipped_NoReadSignsIt()
    {
        Row("d-1",
            (DeliveryPhotoKeys.Pickup, "uploads/photos/d-999/pickup-1700000000000.webp", null, null),
            (DeliveryPhotoKeys.Dropoff, "/data/user/0/app/files/photos/d-1/dropoff.webp", null, null));

        var summary = await Backfill().RunAsync(null, Slice(), _context);

        summary["outcome"].Should().Be("ok");
        summary["skipped"].Should().Be("2");
        summary["copied"].Should().Be("0");
        await _s3.DidNotReceive().CopyObjectAsync(Arg.Any<CopyObjectRequest>(), Arg.Any<CancellationToken>());
        _updates.Should().BeEmpty();
        Logs.Should().Contain($"{DeliveryPhotoCopyBackfill.SkippedToken} deliveryId=d-1 type=pickup reason=key_shape");
    }

    [Theory]
    [InlineData("NoSuchKey", HttpStatusCode.NotFound)]
    [InlineData("NoSuchVersion", HttpStatusCode.NotFound)]
    public async Task AnObjectThatIsGone_IsMissing_NotAFailure(string code, HttpStatusCode status)
    {
        Row("d-1", (DeliveryPhotoKeys.Pickup, Key("d-1", "pickup"), "v-gone", null));
        _copyFails = _ => new AmazonS3Exception("gone") { ErrorCode = code, StatusCode = status };

        var summary = await Backfill().RunAsync(null, Slice(), _context);

        summary["outcome"].Should().Be("ok");
        summary["missing"].Should().Be("1");
        _updates.Should().BeEmpty();
        Logs.Should().Contain($"{DeliveryPhotoCopyBackfill.MissingToken} deliveryId=d-1 type=pickup");
    }

    [Fact]
    public async Task ACopyThatFails_IsCountedAndLeftForTheNextRun()
    {
        Row("d-1", (DeliveryPhotoKeys.Pickup, Key("d-1", "pickup"), "v", null));
        Row("d-2", (DeliveryPhotoKeys.Pickup, Key("d-2", "pickup"), "v", null));
        _copyFails = r => r.SourceKey == Key("d-1", "pickup")
            ? new AmazonS3Exception("Slow Down") { StatusCode = HttpStatusCode.ServiceUnavailable }
            : null;

        var summary = await Backfill().RunAsync(null, Slice(), _context);

        summary["outcome"].Should().Be("partial");
        summary["failed"].Should().Be("1");
        summary["copied"].Should().Be("1");
        _rows[0].Should().NotContainKey("pickupPhotoKeptRef");
        _rows[1].Should().ContainKey("pickupPhotoKeptRef");
        Logs.Should().Contain($"{DeliveryPhotoCopyBackfill.FailedToken} step=copy deliveryId=d-1 type=pickup");

        // The next run copies what is left, and nothing twice.
        _copyFails = _ => null;
        var again = await Backfill().RunAsync(null, Slice(), _context);

        again["outcome"].Should().Be("ok");
        again["copied"].Should().Be("1");
    }

    [Fact]
    public async Task ARowThatChangedSinceTheRead_IsNotOverwritten()
    {
        Row("d-1", (DeliveryPhotoKeys.Pickup, Key("d-1", "pickup"), "v", null));
        _updateFails = _ => new ConditionalCheckFailedException("condition");

        var summary = await Backfill().RunAsync(null, Slice(), _context);

        summary["outcome"].Should().Be("ok");
        summary["present"].Should().Be("1");
        summary["copied"].Should().Be("0");
    }

    [Fact]
    public async Task AWriteThatFails_IsAFailure()
    {
        Row("d-1", (DeliveryPhotoKeys.Pickup, Key("d-1", "pickup"), "v", null));
        _updateFails = _ => new ProvisionedThroughputExceededException("throttled");

        var summary = await Backfill().RunAsync(null, Slice(), _context);

        summary["outcome"].Should().Be("partial");
        summary["failed"].Should().Be("1");
        Logs.Should().Contain($"{DeliveryPhotoCopyBackfill.FailedToken} step=write deliveryId=d-1 type=pickup");
    }

    [Fact]
    public async Task ARunThatStopsOnTime_AnswersItsCursor_AndTheNextRunResumesThere()
    {
        for (var i = 1; i <= 5; i++)
        {
            Row($"d-{i}", (DeliveryPhotoKeys.Pickup, Key($"d-{i}", "pickup"), "v", null));
        }

        // Each copy takes a minute: the 4 minute slice handles four rows.
        _perRowCopy = TimeSpan.FromMinutes(1);
        var first = await Backfill().RunAsync(null, Slice(), _context);

        first["outcome"].Should().Be("partial");
        first["copied"].Should().Be("4");
        first["scanCursor"].Should().Be("d-4|trip-d-4");

        _elapsed = TimeSpan.Zero;
        var second = await Backfill().RunAsync(first["scanCursor"], Slice(), _context);

        second["outcome"].Should().Be("ok");
        second["copied"].Should().Be("1");
        _scans.Last(s => s.ExclusiveStartKey != null).ExclusiveStartKey!["deliveryId"].S.Should().Be("d-4");
        _rows.Should().OnlyContain(r => r.ContainsKey("pickupPhotoKeptRef"));
    }

    [Fact]
    public async Task AScanThatFails_IsAFailure_WithTheCursorWhereItStopped()
    {
        Row("d-1", (DeliveryPhotoKeys.Pickup, Key("d-1", "pickup"), "v", null));
        _ddb.ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>())
            .Throws(new ProvisionedThroughputExceededException("throttled"));

        var summary = await Backfill().RunAsync("d-0|trip-d-0", Slice(), _context);

        summary["outcome"].Should().Be("partial");
        summary["failed"].Should().Be("1");
        summary["scanCursor"].Should().Be("d-0|trip-d-0");
    }

    [Theory]
    [InlineData("d-1|trip-1", "d-1", "trip-1")]
    [InlineData("d-1|self-pickup|x", "d-1", "self-pickup|x")]
    public void TheCursor_IsTheDeliveriesKey(string cursor, string deliveryId, string tripId)
    {
        var key = DeliveryPhotoCopyBackfill.KeyOf(cursor)!;

        key["deliveryId"].S.Should().Be(deliveryId);
        key["tripId"].S.Should().Be(tripId);
        DeliveryPhotoCopyBackfill.CursorOf(key).Should().Be(cursor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("d-1")]
    [InlineData("|trip-1")]
    [InlineData("d-1|")]
    [InlineData("d-1|trip\n1")]
    public void ATextThatIsNotACursor_IsNoKey(string cursor)
    {
        DeliveryPhotoCopyBackfill.KeyOf(cursor).Should().BeNull();
    }

    // ── The order-cron action ───────────────────────────────────────────

    private static APIGatewayProxyRequest Invoke(string body) => new() { HttpMethod = "POST", Body = body };

    [Fact]
    public async Task TheOrderCronAction_RunsTheBackfill_AndAnswersItsSummary()
    {
        Row("d-1", (DeliveryPhotoKeys.Pickup, Key("d-1", "pickup"), "v", null));
        var function = new OrderCronFunction(Substitute.For<IOrderRepository>()) { PhotoCopyBackfill = Backfill() };

        var response = await function.FunctionHandler(Invoke("{\"action\":\"backfill_delivery_photo_copies\"}"), _context);

        response.StatusCode.Should().Be(200, response.Body);
        using var body = JsonDocument.Parse(response.Body);
        body.RootElement.GetProperty("action").GetString().Should().Be("backfill_delivery_photo_copies");
        body.RootElement.GetProperty("outcome").GetString().Should().Be("ok");
        body.RootElement.GetProperty("copied").GetString().Should().Be("1");
    }

    [Fact]
    public async Task TheOrderCronAction_WithoutTheBucket_DoesNothing()
    {
        var function = new OrderCronFunction(Substitute.For<IOrderRepository>());

        var response = await function.FunctionHandler(Invoke("{\"action\":\"backfill_delivery_photo_copies\"}"), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("\"outcome\":\"unconfigured\"");
        Logs.Should().Contain(OrderCronFunction.PhotoCopyBackfillUnconfiguredToken);
    }

    [Fact]
    public async Task TheOrderCronAction_RefusesAScanFromThatIsNotACursor()
    {
        var function = new OrderCronFunction(Substitute.For<IOrderRepository>()) { PhotoCopyBackfill = Backfill() };

        var response = await function.FunctionHandler(
            Invoke("{\"action\":\"backfill_delivery_photo_copies\",\"scanFrom\":\"no-bar\"}"), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ORDER_CRON_VALIDATION");
        await _ddb.DidNotReceive().ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>());
    }
}
