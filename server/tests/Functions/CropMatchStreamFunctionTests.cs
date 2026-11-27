using System.Text;
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

public class CropMatchStreamFunctionTests
{
    private readonly IIntentRepository _intents = Substitute.For<IIntentRepository>();
    private readonly IDeviceTokenRepository _devices = Substitute.For<IDeviceTokenRepository>();
    private readonly IAmazonSimpleNotificationService _sns = Substitute.For<IAmazonSimpleNotificationService>();
    private readonly CropMatchStreamFunction _fn;
    private readonly TestLambdaContext _ctx = new();

    public CropMatchStreamFunctionTests()
    {
        _sns.PublishAsync(Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishResponse());
        // No substitute intents unless a test overrides.
        _intents.QueryActiveByCropTypeAsync(Arg.Any<string>())
            .Returns(new List<Dictionary<string, object>>());
        _fn = new CropMatchStreamFunction(_intents, _devices, _sns);
    }

    private static Stream StreamEvent(
        string eventName = "INSERT",
        string cropType = "wheat",
        string status = "available",
        double pricePerKg = 100,
        double quantity = 10,
        string woreda = "Adama",
        string sequenceNumber = "seq-1",
        double? latitude = null,
        double? longitude = null)
    {
        var gps = latitude.HasValue && longitude.HasValue
            ? "\"latitude\": { \"N\": \"" + latitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\" },"
              + "\"longitude\": { \"N\": \"" + longitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\" },"
            : "";
        var json = $$"""
        {
          "Records": [
            {
              "eventName": "{{eventName}}",
              "dynamodb": {
                "SequenceNumber": "{{sequenceNumber}}",
                "NewImage": {
                  "cropId": { "S": "crop-1" },
                  "cropType": { "S": "{{cropType}}" },
                  "status": { "S": "{{status}}" },
                  "pricePerKg": { "N": "{{pricePerKg}}" },
                  "quantity": { "N": "{{quantity}}" },
                  {{gps}}
                  "woreda": { "S": "{{woreda}}" },
                  "locationName": { "S": "{{woreda}}" },
                  "createdAt": { "S": "2026-06-19T00:00:00Z" }
                }
              }
            }
          ]
        }
        """;
        return new MemoryStream(Encoding.UTF8.GetBytes(json));
    }

    private static Dictionary<string, object> Intent(
        string intentId = "i1",
        string buyerId = "b1",
        string cropType = "wheat",
        string maxPricePerKg = "150",
        string quantityNeeded = "5",
        string deliveryWoreda = "Adama",
        string? deliveryLatitude = null,
        string? deliveryLongitude = null)
    {
        var item = new Dictionary<string, object>
        {
            { "intentId", intentId },
            { "buyerId", buyerId },
            { "cropType", cropType },
            { "maxPricePerKg", maxPricePerKg },
            { "quantityNeeded", quantityNeeded },
            { "deliveryWoreda", deliveryWoreda },
        };
        if (deliveryLatitude != null)
        {
            item["deliveryLatitude"] = deliveryLatitude;
        }
        if (deliveryLongitude != null)
        {
            item["deliveryLongitude"] = deliveryLongitude;
        }
        return item;
    }

    [Fact]
    public async Task MatchingIntent_WithEndpoint_PublishesPush()
    {
        _intents.QueryActiveByCropTypeAsync("wheat")
            .Returns(new List<Dictionary<string, object>> { Intent() });
        _devices.GetEndpointArnsByUserAsync("b1")
            .Returns(new List<string> { "arn:endpoint:1" });

        await _fn.ProcessStreamAsync(StreamEvent(), _ctx);

        await _sns.Received(1).PublishAsync(
            Arg.Is<PublishRequest>(r =>
                r.TargetArn == "arn:endpoint:1"
                && r.MessageStructure == "json"
                && r.Message.Contains("i1")
                && r.Message.Contains("crop-1")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonMatchingIntent_DoesNotPublish()
    {
        // Budget far below the crop price → not a candidate.
        _intents.QueryActiveByCropTypeAsync("wheat")
            .Returns(new List<Dictionary<string, object>> { Intent(maxPricePerKg: "50") });
        _devices.GetEndpointArnsByUserAsync(Arg.Any<string>())
            .Returns(new List<string> { "arn:endpoint:1" });

        await _fn.ProcessStreamAsync(StreamEvent(), _ctx);

        await _sns.DidNotReceive().PublishAsync(
            Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MatchingIntent_NoEndpoint_DoesNotPublish()
    {
        _intents.QueryActiveByCropTypeAsync("wheat")
            .Returns(new List<Dictionary<string, object>> { Intent() });
        _devices.GetEndpointArnsByUserAsync("b1").Returns(new List<string>());

        await _fn.ProcessStreamAsync(StreamEvent(), _ctx);

        await _sns.DidNotReceive().PublishAsync(
            Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonInsertEvent_IsIgnored()
    {
        await _fn.ProcessStreamAsync(StreamEvent(eventName: "MODIFY"), _ctx);

        await _intents.DidNotReceive().QueryActiveByCropTypeAsync(Arg.Any<string>());
        await _sns.DidNotReceive().PublishAsync(
            Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonAvailableCrop_IsIgnored()
    {
        await _fn.ProcessStreamAsync(StreamEvent(status: "sold"), _ctx);

        await _intents.DidNotReceive().QueryActiveByCropTypeAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task TwoIntentsSameBuyer_OnePushPerCrop()
    {
        // One push per buyer per crop: two distinct matching intents for one
        // buyer collapse into a single alert (less notification noise, fewer
        // SNS publishes), and device tokens are queried only once.
        _intents.QueryActiveByCropTypeAsync("wheat").Returns(new List<Dictionary<string, object>>
        {
            Intent(intentId: "i1"),
            Intent(intentId: "i2"),
        });
        _devices.GetEndpointArnsByUserAsync("b1").Returns(new List<string> { "arn:endpoint:1" });

        await _fn.ProcessStreamAsync(StreamEvent(), _ctx);

        await _sns.Received(1).PublishAsync(Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>());
        await _devices.Received(1).GetEndpointArnsByUserAsync("b1");
    }

    [Fact]
    public async Task TwoIntentsDifferentBuyers_EachBuyerGetsAPush()
    {
        _intents.QueryActiveByCropTypeAsync("wheat").Returns(new List<Dictionary<string, object>>
        {
            Intent(intentId: "i1", buyerId: "b1"),
            Intent(intentId: "i2", buyerId: "b2"),
        });
        _devices.GetEndpointArnsByUserAsync(Arg.Any<string>())
            .Returns(new List<string> { "arn:endpoint:1" });

        await _fn.ProcessStreamAsync(StreamEvent(), _ctx);

        await _sns.Received(2).PublishAsync(Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IntentWithCoords_FarCrop_GeoDropsAlertBelowThreshold()
    {
        // Marginal match (substitute type + near-miss price + partial quantity):
        // the geo signal decides the alert. Crop GPS ~550 km from the delivery
        // point (Adama→Mekelle) → score falls below the 0.5 threshold → no push.
        // Pins the haversine path over intent deliveryLatitude/Longitude.
        _intents.QueryActiveByCropTypeAsync("wheat").Returns(new List<Dictionary<string, object>>
        {
            Intent(cropType: "wheat", maxPricePerKg: "100", quantityNeeded: "5",
                deliveryWoreda: "Adama", deliveryLatitude: "8.54", deliveryLongitude: "39.27"),
        });
        _devices.GetEndpointArnsByUserAsync("b1").Returns(new List<string> { "arn:endpoint:1" });

        await _fn.ProcessStreamAsync(
            StreamEvent(cropType: "barley", pricePerKg: 114, quantity: 3,
                woreda: "Mekelle", latitude: 13.49, longitude: 39.47),
            _ctx);

        await _sns.DidNotReceive().PublishAsync(
            Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IntentWithCoords_NearCrop_GeoLiftsAlertAboveThreshold()
    {
        // Same marginal match as above but the crop sits on the delivery point
        // → geo ≈ 1.0 lifts the score above the threshold → push.
        _intents.QueryActiveByCropTypeAsync("wheat").Returns(new List<Dictionary<string, object>>
        {
            Intent(cropType: "wheat", maxPricePerKg: "100", quantityNeeded: "5",
                deliveryWoreda: "Adama", deliveryLatitude: "8.54", deliveryLongitude: "39.27"),
        });
        _devices.GetEndpointArnsByUserAsync("b1").Returns(new List<string> { "arn:endpoint:1" });

        await _fn.ProcessStreamAsync(
            StreamEvent(cropType: "barley", pricePerKg: 114, quantity: 3,
                woreda: "Mekelle", latitude: 8.54, longitude: 39.27),
            _ctx);

        await _sns.Received(1).PublishAsync(
            Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NewBarleyCrop_AlertsWheatIntent_ViaSubstituteFanOut()
    {
        // A new barley crop is an accepted substitute for a wheat intent — the
        // symmetric substitute map must fan the alert out to the wheat buyer.
        _intents.QueryActiveByCropTypeAsync("wheat")
            .Returns(new List<Dictionary<string, object>> { Intent(cropType: "wheat") });
        _devices.GetEndpointArnsByUserAsync("b1").Returns(new List<string> { "arn:endpoint:1" });

        await _fn.ProcessStreamAsync(StreamEvent(cropType: "barley"), _ctx);

        await _sns.Received(1).PublishAsync(Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordThatThrows_IsReportedAsBatchItemFailure()
    {
        // A transient fault on a record must be reported (by sequence number) so
        // Lambda retries it, rather than silently dropping the alert.
        _intents.QueryActiveByCropTypeAsync(Arg.Any<string>())
            .Returns<List<Dictionary<string, object>>>(_ => throw new Exception("dynamo throttle"));

        var response = await _fn.ProcessBatchAsync(StreamEvent(sequenceNumber: "seq-42"), _ctx);

        Assert.Single(response.BatchItemFailures);
        Assert.Equal("seq-42", response.BatchItemFailures[0].ItemIdentifier);
    }

    [Fact]
    public async Task SuccessfulBatch_ReportsNoFailures()
    {
        _intents.QueryActiveByCropTypeAsync("wheat")
            .Returns(new List<Dictionary<string, object>> { Intent() });
        _devices.GetEndpointArnsByUserAsync("b1").Returns(new List<string> { "arn:endpoint:1" });

        var response = await _fn.ProcessBatchAsync(StreamEvent(), _ctx);

        Assert.Empty(response.BatchItemFailures);
    }

    [Fact]
    public async Task DisabledEndpoint_IsPruned()
    {
        _intents.QueryActiveByCropTypeAsync("wheat")
            .Returns(new List<Dictionary<string, object>> { Intent() });
        _devices.GetEndpointArnsByUserAsync("b1").Returns(new List<string> { "arn:dead" });
        _sns.PublishAsync(Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>())
            .Returns<PublishResponse>(_ => throw new EndpointDisabledException("disabled"));

        await _fn.ProcessStreamAsync(StreamEvent(), _ctx);

        await _devices.Received(1).DeleteAsync("b1", "arn:dead");
    }

    // ===== FUP-185: the alert's reliability is the one GET /match uses =====

    // A marginal listing for the wheat intent below (barley substitute, over budget by 10 or 14
    // percent, 3 of the 5 wanted, Mekelle against Adama): type 0.18, geo 0.054, quantity 0.072 and
    // freshness 0.10, plus price 0.0667 at 110 or 0.0133 at 114. The reliability term (0.10 times
    // pct / 100, or the neutral 0.07) decides the 0.5 threshold:
    //   110: neutral 0.543 (alert), pct 0 0.473 (no alert).
    //   114: neutral 0.489 (no alert), pct 100 0.519 (alert).
    [Theory]
    [InlineData(true, 100, 114, true)]   // the stamped 100 lifts it over the threshold
    [InlineData(false, 100, 114, false)] // flag off: neutral, as before
    [InlineData(true, null, 114, false)] // no copy on the row: neutral
    [InlineData(true, 0, 110, false)]    // the stamped 0 drops it under the threshold
    [InlineData(false, 0, 110, true)]    // flag off: neutral, as before
    [InlineData(true, null, 110, true)]  // no copy on the row: neutral
    public async Task Alert_ScoresTheListingAsGetMatchDoes_WithTheSameReliability(
        bool publicTags, int? reliabilityPct, double price, bool alerted)
    {
        var row = ListingRow(price, reliabilityPct);

        // GET /match for the same listing, the same criteria and the same flag.
        var crops = Substitute.For<ICropRepository>();
        crops.QueryCropsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>?>(), Arg.Any<double?>())
            .Returns((new List<Dictionary<string, object>> { row }, (Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>?)null));
        var match = new MatchFunction(crops) { ReputationPublicTags = publicTags, HideRestrictedOwners = false };
        var response = await match.FunctionHandler(BuyerMatchRequest(), _ctx);
        Assert.Equal(200, response.StatusCode);
        var matchScore = JsonDocument.Parse(response.Body).RootElement.GetProperty("matches")[0].GetProperty("score").GetDouble();

        // The alert's candidate for the listing's stream image scores the same.
        var now = DateTime.UtcNow;
        var image = JsonDocument.Parse(ImageJson(row)).RootElement.Clone();
        var criteria = new MatchCriteria
        {
            CropType = "wheat",
            MaxPricePerKg = 100,
            QuantityNeeded = 5,
            DeliveryWoreda = "Adama",
            NowUtc = now,
        };
        var alertScore = MatchScoring.ScoreListing(criteria, CropMatchStreamFunction.ToCandidate(image, publicTags, now));
        Assert.Equal(matchScore, alertScore, 12);
        Assert.Equal(alerted, matchScore >= 0.5);

        // And the alert fires exactly when that score reaches the threshold.
        _intents.QueryActiveByCropTypeAsync("wheat").Returns(new List<Dictionary<string, object>>
        {
            Intent(cropType: "wheat", maxPricePerKg: "100", quantityNeeded: "5", deliveryWoreda: "Adama"),
        });
        _devices.GetEndpointArnsByUserAsync("b1").Returns(new List<string> { "arn:endpoint:1" });
        var fn = new CropMatchStreamFunction(_intents, _devices, _sns) { ReputationPublicTags = publicTags };

        await fn.ProcessStreamAsync(RecordWith(ImageJson(row)), _ctx);

        await _sns.Received(alerted ? 1 : 0).PublishAsync(Arg.Any<PublishRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void PublicTagsOff_TheCandidateIsTheOneBeforeFup185_StampedOrNot()
    {
        // Flag off: a stamped row gives exactly the candidate the alert built before FUP-185 (no
        // reliability, so the neutral 0.7), the same as a row without the copy.
        var now = DateTime.UtcNow;
        var stamped = JsonDocument.Parse(ImageJson(ListingRow(110, 0))).RootElement.Clone();
        var unstamped = JsonDocument.Parse(ImageJson(ListingRow(110, null))).RootElement.Clone();
        var before = new CropCandidate
        {
            CropType = "barley",
            PricePerKg = 110,
            Quantity = 3,
            LocationName = "Mekelle",
            CreatedAt = now,
        };

        var candidate = CropMatchStreamFunction.ToCandidate(stamped, publicTags: false, now);

        Assert.Equal(before, candidate);
        Assert.Equal(before, CropMatchStreamFunction.ToCandidate(unstamped, publicTags: false, now));
        var criteria = new MatchCriteria { CropType = "wheat", MaxPricePerKg = 100, QuantityNeeded = 5, DeliveryWoreda = "Adama", NowUtc = now };
        Assert.Equal(MatchScoring.ScoreListing(criteria, before), MatchScoring.ScoreListing(criteria, candidate));
        // On, the same row carries the stamped value.
        Assert.Equal(0, CropMatchStreamFunction.ToCandidate(stamped, publicTags: true, now).OwnerReliabilityPct);
    }

    /// <summary>The listing as a table read returns it (strings, numbers as their text).</summary>
    private static Dictionary<string, object> ListingRow(double price, int? reliabilityPct)
    {
        var createdAt = DateTime.UtcNow.AddMinutes(-1).ToString("o");
        var row = new Dictionary<string, object>
        {
            { "cropId", "crop-1" }, { "farmerId", "farmer-1" }, { "cropType", "barley" }, { "status", "available" },
            { "pricePerKg", price.ToString(System.Globalization.CultureInfo.InvariantCulture) }, { "quantity", "3" },
            { "woreda", "Mekelle" }, { "locationName", "Mekelle" },
            { "createdAt", createdAt }, { "updatedAt", createdAt }, { "version", "1" },
        };
        if (reliabilityPct is int pct)
        {
            row["ownerTier"] = "good";
            row["ownerEffectiveRating"] = "4.2";
            row["ownerRatingCount"] = "12";
            row["ownerReliabilityPct"] = pct.ToString(System.Globalization.CultureInfo.InvariantCulture);
            row["ownerReputationAt"] = "2026-11-10T09:00:00.000Z";
        }

        return row;
    }

    private static readonly HashSet<string> NumberAttributes = new()
    {
        "pricePerKg", "quantity", "version", "ownerEffectiveRating", "ownerRatingCount", "ownerReliabilityPct",
    };

    /// <summary>The same row as a stream NewImage (DynamoDB JSON).</summary>
    private static string ImageJson(Dictionary<string, object> row)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in row)
            {
                writer.WriteStartObject(key);
                writer.WriteString(NumberAttributes.Contains(key) ? "N" : "S", value.ToString());
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static Stream RecordWith(string imageJson)
        => new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"Records\":[{\"eventName\":\"INSERT\",\"dynamodb\":{\"SequenceNumber\":\"seq-1\",\"NewImage\":"
            + imageJson + "}}]}"));

    private static APIGatewayProxyRequest BuyerMatchRequest() => new()
    {
        HttpMethod = "GET",
        Path = "/match",
        QueryStringParameters = new Dictionary<string, string>
        {
            { "cropType", "wheat" }, { "maxPricePerKg", "100" }, { "quantityNeeded", "5" }, { "deliveryWoreda", "Adama" },
        },
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "b1" }, { "custom:role", "buyer" } },
        },
    };
}
