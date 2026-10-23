using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using NSubstitute;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.RateLimiting;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

public class FraudDetectionServiceTests
{
    private readonly IAmazonDynamoDB _mockDynamoDb;
    private readonly IRateLimitRepository _mockRateLimitRepo;
    private readonly FraudDetectionService _service;

    private const string CropsTable = "test-crops-table";
    private const string FraudSignalsTable = "test-fraud-signals-table";
    private const string FarmerId = "farmer-123";
    private const string UserId = "user-456";

    public FraudDetectionServiceTests()
    {
        _mockDynamoDb = Substitute.For<IAmazonDynamoDB>();
        _mockRateLimitRepo = Substitute.For<IRateLimitRepository>();
        _service = new FraudDetectionService(_mockDynamoDb, CropsTable, FraudSignalsTable, _mockRateLimitRepo);

        // Default: no velocity entries, no duplicate listings, no price history
        _mockRateLimitRepo.GetAsync(Arg.Any<string>()).Returns((RateLimitEntry?)null);
        _mockDynamoDb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });
    }

    // ===== Listing Velocity Tests =====

    [Fact]
    public async Task CheckListingCreation_VelocityUnderLimit_ReturnsAllowed()
    {
        _mockRateLimitRepo.GetAsync(Arg.Any<string>())
            .Returns(new RateLimitEntry { Pk = "test", Count = 2, Ttl = 0 });

        var result = await _service.CheckListingCreationAsync(FarmerId, "teff", 45m);

        Assert.True(result.IsAllowed);
        Assert.False(result.IsFlagged);
    }

    [Fact]
    public async Task CheckListingCreation_VelocityAtLimit_ReturnsRejected()
    {
        _mockRateLimitRepo.GetAsync(Arg.Any<string>())
            .Returns(new RateLimitEntry { Pk = "test", Count = 5, Ttl = 0 });

        var result = await _service.CheckListingCreationAsync(FarmerId, "teff", 45m);

        Assert.False(result.IsAllowed);
        Assert.Equal("FRAUD_VELOCITY_EXCEEDED", result.ErrorCode);
    }

    // ===== Duplicate Listing Tests =====

    [Fact]
    public async Task CheckListingCreation_DuplicateExactMatch_ReturnsRejected()
    {
        // Velocity under limit
        _mockRateLimitRepo.GetAsync(Arg.Any<string>()).Returns((RateLimitEntry?)null);

        // Return a matching listing from GSI query (FarmerIdCreatedAtIndex)
        var existingItem = new Dictionary<string, AttributeValue>
        {
            { "cropType", new AttributeValue { S = "teff" } },
            { "pricePerKg", new AttributeValue { N = "45" } },
            { "createdAt", new AttributeValue { S = DateTime.UtcNow.ToString("o") } }
        };
        SetupFarmerListingsQuery(new List<Dictionary<string, AttributeValue>> { existingItem });

        var result = await _service.CheckListingCreationAsync(FarmerId, "teff", 45m);

        Assert.False(result.IsAllowed);
        Assert.Equal("FRAUD_DUPLICATE_LISTING", result.ErrorCode);
    }

    [Fact]
    public async Task CheckListingCreation_DuplicateWithin5Percent_ReturnsRejected()
    {
        _mockRateLimitRepo.GetAsync(Arg.Any<string>()).Returns((RateLimitEntry?)null);

        // Price = 45, incoming = 46 → diff = 2.2% → within 5%
        var existingItem = new Dictionary<string, AttributeValue>
        {
            { "cropType", new AttributeValue { S = "teff" } },
            { "pricePerKg", new AttributeValue { N = "45" } },
            { "createdAt", new AttributeValue { S = DateTime.UtcNow.ToString("o") } }
        };
        SetupFarmerListingsQuery(new List<Dictionary<string, AttributeValue>> { existingItem });

        var result = await _service.CheckListingCreationAsync(FarmerId, "teff", 46m);

        Assert.False(result.IsAllowed);
        Assert.Equal("FRAUD_DUPLICATE_LISTING", result.ErrorCode);
    }

    [Fact]
    public async Task CheckListingCreation_DuplicateBeyond5Percent_ReturnsAllowed()
    {
        _mockRateLimitRepo.GetAsync(Arg.Any<string>()).Returns((RateLimitEntry?)null);

        // Price = 45, incoming = 55 → diff = 22.2% → beyond 5%
        var existingItem = new Dictionary<string, AttributeValue>
        {
            { "cropType", new AttributeValue { S = "teff" } },
            { "pricePerKg", new AttributeValue { N = "45" } },
            { "createdAt", new AttributeValue { S = DateTime.UtcNow.ToString("o") } }
        };
        SetupFarmerListingsQuery(new List<Dictionary<string, AttributeValue>> { existingItem });

        var result = await _service.CheckListingCreationAsync(FarmerId, "teff", 55m);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task CheckListingCreation_DuplicateDifferentCropType_ReturnsAllowed()
    {
        _mockRateLimitRepo.GetAsync(Arg.Any<string>()).Returns((RateLimitEntry?)null);

        var existingItem = new Dictionary<string, AttributeValue>
        {
            { "cropType", new AttributeValue { S = "wheat" } },
            { "pricePerKg", new AttributeValue { N = "45" } },
            { "createdAt", new AttributeValue { S = DateTime.UtcNow.ToString("o") } }
        };
        SetupFarmerListingsQuery(new List<Dictionary<string, AttributeValue>> { existingItem });

        var result = await _service.CheckListingCreationAsync(FarmerId, "teff", 45m);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task CheckListingCreation_DeletedListingExcluded_ReturnsAllowed()
    {
        _mockRateLimitRepo.GetAsync(Arg.Any<string>()).Returns((RateLimitEntry?)null);

        // The filter expression attribute_not_exists(deletedAt) means deleted items
        // are excluded from the query response. So we return an empty list.
        SetupFarmerListingsQuery(new List<Dictionary<string, AttributeValue>>());

        var result = await _service.CheckListingCreationAsync(FarmerId, "teff", 45m);

        Assert.True(result.IsAllowed);
    }

    // ===== Price Anomaly Tests =====

    [Fact]
    public async Task CheckListingCreation_PriceAnomaly4Sigma_ReturnsFlagged()
    {
        _mockRateLimitRepo.GetAsync(Arg.Any<string>()).Returns((RateLimitEntry?)null);
        SetupFarmerListingsQuery(new List<Dictionary<string, AttributeValue>>());

        // Set up price history with stable prices around 45
        // Mean = 45, stddev ≈ 1 → 4 sigma = 49
        var priceItems = new List<Dictionary<string, AttributeValue>>();
        for (var i = 0; i < 10; i++)
        {
            priceItems.Add(new Dictionary<string, AttributeValue>
            {
                { "pricePerKg", new AttributeValue { N = (44 + i % 3).ToString() } }
            });
        }
        SetupPriceHistoryQuery(priceItems);

        // Price 200 is way beyond 3 sigma
        var result = await _service.CheckListingCreationAsync(FarmerId, "teff", 200m);

        Assert.True(result.IsAllowed); // flagged, not rejected
        Assert.True(result.IsFlagged);
        Assert.Equal("PRICE_ANOMALY", result.FlagReason);
    }

    [Fact]
    public async Task CheckListingCreation_PriceWithin3Sigma_ReturnsAllowed()
    {
        _mockRateLimitRepo.GetAsync(Arg.Any<string>()).Returns((RateLimitEntry?)null);
        SetupFarmerListingsQuery(new List<Dictionary<string, AttributeValue>>());

        // Set up price history with stable prices around 45
        var priceItems = new List<Dictionary<string, AttributeValue>>();
        for (var i = 0; i < 10; i++)
        {
            priceItems.Add(new Dictionary<string, AttributeValue>
            {
                { "pricePerKg", new AttributeValue { N = (44 + i % 3).ToString() } }
            });
        }
        SetupPriceHistoryQuery(priceItems);

        // Price 45 is within the normal range
        var result = await _service.CheckListingCreationAsync(FarmerId, "teff", 45m);

        Assert.True(result.IsAllowed);
        Assert.False(result.IsFlagged);
    }

    [Fact]
    public async Task CheckListingCreation_InsufficientSamples_SkipsPriceCheck()
    {
        _mockRateLimitRepo.GetAsync(Arg.Any<string>()).Returns((RateLimitEntry?)null);
        SetupFarmerListingsQuery(new List<Dictionary<string, AttributeValue>>());

        // Only 3 price samples (< 5 minimum)
        var priceItems = new List<Dictionary<string, AttributeValue>>
        {
            new() { { "pricePerKg", new AttributeValue { N = "45" } } },
            new() { { "pricePerKg", new AttributeValue { N = "46" } } },
            new() { { "pricePerKg", new AttributeValue { N = "44" } } }
        };
        SetupPriceHistoryQuery(priceItems);

        // Even an extreme price should be allowed when insufficient samples
        var result = await _service.CheckListingCreationAsync(FarmerId, "teff", 500m);

        Assert.True(result.IsAllowed);
        Assert.False(result.IsFlagged);
    }

    // ===== Payment Velocity Tests =====

    [Fact]
    public async Task CheckPaymentVelocity_UnderLimit_ReturnsAllowed()
    {
        // Contract limit is 5 payment initiations per hour; 4 is under.
        _mockRateLimitRepo.GetAsync(Arg.Any<string>())
            .Returns(new RateLimitEntry { Pk = "test", Count = 4, Ttl = 0 });

        var result = await _service.CheckPaymentVelocityAsync(UserId);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task CheckPaymentVelocity_AtLimit_ReturnsRejected()
    {
        // 5 initiations already recorded this hour → the 6th is rejected.
        _mockRateLimitRepo.GetAsync(Arg.Any<string>())
            .Returns(new RateLimitEntry { Pk = "test", Count = 5, Ttl = 0 });

        var result = await _service.CheckPaymentVelocityAsync(UserId);

        Assert.False(result.IsAllowed);
        Assert.Equal("FRAUD_VELOCITY_EXCEEDED", result.ErrorCode);
    }

    // ===== GPS Consistency Tests =====

    [Fact]
    public void CheckGpsConsistency_InterWoredaLessThan1Km_ReturnsFlagged()
    {
        // Two points ~0.5km apart (Addis Ababa area)
        var result = _service.CheckGpsConsistency(
            pickupLat: 9.0000, pickupLng: 38.7000,
            dropoffLat: 9.0040, dropoffLng: 38.7000,
            fromWoreda: "Woreda A", toWoreda: "Woreda B");

        Assert.True(result.IsAllowed); // flagged, not rejected
        Assert.True(result.IsFlagged);
        Assert.Equal("GPS_ANOMALY", result.FlagReason);
    }

    [Fact]
    public void CheckGpsConsistency_SameWoredaLessThan1Km_ReturnsAllowed()
    {
        // Same woreda — < 1km is fine
        var result = _service.CheckGpsConsistency(
            pickupLat: 9.0000, pickupLng: 38.7000,
            dropoffLat: 9.0040, dropoffLng: 38.7000,
            fromWoreda: "Woreda A", toWoreda: "Woreda A");

        Assert.True(result.IsAllowed);
        Assert.False(result.IsFlagged);
    }

    [Fact]
    public void CheckGpsConsistency_IdenticalCoordinates_ReturnsFlagged()
    {
        var result = _service.CheckGpsConsistency(
            pickupLat: 9.0000, pickupLng: 38.7000,
            dropoffLat: 9.0000, dropoffLng: 38.7000,
            fromWoreda: "Woreda A", toWoreda: "Woreda A");

        Assert.True(result.IsAllowed);
        Assert.True(result.IsFlagged);
        Assert.Equal("GPS_ANOMALY", result.FlagReason);
        Assert.Contains("identical", result.Message!);
    }

    [Fact]
    public void CheckGpsConsistency_MissingCoordinates_ReturnsAllowed()
    {
        var result = _service.CheckGpsConsistency(
            pickupLat: null, pickupLng: null,
            dropoffLat: 9.0000, dropoffLng: 38.7000,
            fromWoreda: "Woreda A", toWoreda: "Woreda B");

        Assert.True(result.IsAllowed);
        Assert.False(result.IsFlagged);
    }

    [Fact]
    public void CheckGpsConsistency_NormalDelivery_ReturnsAllowed()
    {
        // Two points ~50km apart (inter-woreda, legitimate distance)
        var result = _service.CheckGpsConsistency(
            pickupLat: 9.0000, pickupLng: 38.7000,
            dropoffLat: 9.4000, dropoffLng: 38.7000,
            fromWoreda: "Woreda A", toWoreda: "Woreda B");

        Assert.True(result.IsAllowed);
        Assert.False(result.IsFlagged);
    }

    // ===== Record Methods Tests =====

    [Fact]
    public async Task RecordListingCreated_CallsIncrementWithCorrectKey()
    {
        await _service.RecordListingCreatedAsync(FarmerId);

        await _mockRateLimitRepo.Received(1).IncrementAsync(
            Arg.Is<string>(k => k.StartsWith($"FRAUD_LISTING#{FarmerId}#")),
            FraudDetectionService.ListingVelocityTtlSeconds);
    }

    [Fact]
    public async Task RecordPaymentInitiated_CallsIncrementWithCorrectKey()
    {
        await _service.RecordPaymentInitiatedAsync(UserId);

        await _mockRateLimitRepo.Received(1).IncrementAsync(
            Arg.Is<string>(k => k.StartsWith($"FRAUD_PAYMENT#{UserId}#")),
            FraudDetectionService.PaymentVelocityTtlSeconds);
    }

    // ===== Fraud Signal Persistence Tests =====

    [Fact]
    public async Task RecordFraudSignal_WritesItemWithCompositeKeyAndAdminFields()
    {
        PutItemRequest? captured = null;
        _mockDynamoDb.PutItemAsync(Arg.Do<PutItemRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new PutItemResponse());

        await _service.RecordFraudSignalAsync(
            FarmerId, "PRICE_ANOMALY", "crop-1", "crop", "Price 999 ETB/kg is unusual for teff");

        Assert.NotNull(captured);
        Assert.Equal(FraudSignalsTable, captured!.TableName);
        Assert.Equal(FarmerId, captured.Item["userId"].S);
        Assert.EndsWith("#PRICE_ANOMALY", captured.Item["signalKey"].S);
        Assert.Equal("PRICE_ANOMALY", captured.Item["signalType"].S);
        Assert.Equal("medium", captured.Item["severity"].S);
        Assert.Equal("10", captured.Item["weight"].N);
        Assert.Equal("pending", captured.Item["status"].S);
        Assert.Equal("crop-1", captured.Item["entityId"].S);
        Assert.Equal("crop", captured.Item["entityType"].S);
        Assert.Equal("Price 999 ETB/kg is unusual for teff", captured.Item["description"].S);

        // signalId must round-trip back to the composite key
        Assert.True(FraudDetectionService.TryParseSignalId(
            captured.Item["signalId"].S, out var userId, out var signalKey));
        Assert.Equal(FarmerId, userId);
        Assert.Equal(captured.Item["signalKey"].S, signalKey);

        // TTL ~30 days out (epoch seconds)
        var expiresAt = long.Parse(captured.Item["expiresAt"].N);
        var expected = DateTimeOffset.UtcNow.AddDays(FraudDetectionService.SignalTtlDays).ToUnixTimeSeconds();
        Assert.InRange(expiresAt, expected - 60, expected + 60);
    }

    [Fact]
    public async Task RecordFraudSignal_HighSeverityForGpsAnomaly()
    {
        PutItemRequest? captured = null;
        _mockDynamoDb.PutItemAsync(Arg.Do<PutItemRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new PutItemResponse());

        await _service.RecordFraudSignalAsync(
            UserId, "GPS_ANOMALY", "delivery-1", "delivery", "Identical coordinates");

        Assert.Equal("high", captured!.Item["severity"].S);
        Assert.Equal("25", captured.Item["weight"].N);
    }

    [Fact]
    public async Task RecordFraudSignal_EmptyTable_NoOp()
    {
        var service = new FraudDetectionService(_mockDynamoDb, CropsTable, "", _mockRateLimitRepo);

        await service.RecordFraudSignalAsync(FarmerId, "PRICE_ANOMALY", null, "crop", "msg");

        await _mockDynamoDb.DidNotReceive()
            .PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordFraudSignal_EmptyUserId_NoOp()
    {
        await _service.RecordFraudSignalAsync("", "PRICE_ANOMALY", null, "crop", "msg");

        await _mockDynamoDb.DidNotReceive()
            .PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordFraudSignal_NullEntityId_OmitsAttribute()
    {
        PutItemRequest? captured = null;
        _mockDynamoDb.PutItemAsync(Arg.Do<PutItemRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new PutItemResponse());

        await _service.RecordFraudSignalAsync(UserId, "FRAUD_VELOCITY_EXCEEDED", null, "escrow", "msg");

        Assert.False(captured!.Item.ContainsKey("entityId"));
        Assert.Equal("medium", captured.Item["severity"].S);
        Assert.Equal("15", captured.Item["weight"].N);
    }

    // ===== FRD-08: per-user, per-type write limit and the reject signal catalogue =====

    [Fact]
    public async Task RecordFraudSignal_KeysTheRowToTheStartOfTheHourAndWritesOnlyIfNew()
    {
        PutItemRequest? captured = null;
        _mockDynamoDb.PutItemAsync(Arg.Do<PutItemRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new PutItemResponse());
        var before = DateTime.UtcNow;

        await _service.RecordFraudSignalAsync(UserId, "ROLE_FORBIDDEN", "crop-1", "crop", "msg");

        // One row per user, type and UTC hour: the key is the hour, the put refuses a second row.
        Assert.Equal("attribute_not_exists(signalKey)", captured!.ConditionExpression);
        var key = captured.Item["signalKey"].S;
        Assert.EndsWith("#ROLE_FORBIDDEN", key);
        var keyTime = DateTime.Parse(key[..key.IndexOf('#')], null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(0, keyTime.Minute);
        Assert.Equal(0, keyTime.Second);
        Assert.Equal(0, keyTime.Millisecond);
        Assert.InRange(keyTime, before.AddHours(-1), DateTime.UtcNow);
        // The scorer's window comparison is ordinal on the ISO string: the key keeps the "o" shape.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:00:00\.0000000Z#", key);
        // createdAt keeps the exact time of the first refusal for the admin.
        Assert.True(DateTime.Parse(captured.Item["createdAt"].S, null,
            System.Globalization.DateTimeStyles.RoundtripKind) >= keyTime);
    }

    [Fact]
    public async Task RecordFraudSignal_SecondRowOfTheHourIsTheLimitNotAnError()
    {
        _mockDynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .Returns<PutItemResponse>(_ => throw new ConditionalCheckFailedException("exists"));

        var ex = await Xunit.Record.ExceptionAsync(
            () => _service.RecordFraudSignalAsync(UserId, "ROLE_FORBIDDEN", "crop-1", "crop", "msg"));

        Assert.Null(ex);
    }

    [Fact]
    public async Task RecordFraudSignal_OtherWriteFailuresStillReachTheCaller()
    {
        // Callers log these (best-effort); only the limit itself is swallowed here.
        _mockDynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .Returns<PutItemResponse>(_ => throw new ProvisionedThroughputExceededException("busy"));

        await Assert.ThrowsAsync<ProvisionedThroughputExceededException>(
            () => _service.RecordFraudSignalAsync(UserId, "ROLE_FORBIDDEN", "crop-1", "crop", "msg"));
    }

    [Theory]
    [InlineData("IDOR_CREATE_CONFLICT", 40, false, "high")]
    [InlineData("ROLE_FORBIDDEN", 25, false, "high")]
    [InlineData("IMMUTABLE_FIELD_CHANGE", 25, false, "high")]
    [InlineData("QR_HANDOFF_FAILURE", 5, true, "low")]
    [InlineData("TELEBIRR_PAYER_MISMATCH", 5, true, "low")]
    [InlineData("STALE_VERSION_BURST", 0, true, "low")]
    [InlineData("SUSPENDED_TOKEN_USE", 0, true, "low")]
    public async Task RecordFraudSignal_RejectSignalsCarryTheCatalogueWeightAndClass(
        string signalType, int weight, bool soft, string severity)
    {
        PutItemRequest? captured = null;
        _mockDynamoDb.PutItemAsync(Arg.Do<PutItemRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new PutItemResponse());

        await _service.RecordFraudSignalAsync(UserId, signalType, null, "sync", "msg");

        Assert.Equal(weight, FraudDetectionService.SignalWeight(signalType));
        Assert.Equal(soft, FraudDetectionService.IsSoftSignal(signalType));
        Assert.Equal(weight.ToString(), captured!.Item["weight"].N);
        Assert.Equal(severity, captured.Item["severity"].S);
        Assert.Equal(UserId, captured.Item["userId"].S);
    }

    private static RiskScoreResult ScoreOf(params (int HoursAgo, string Type)[] rows)
    {
        var calculator = new RiskScoreCalculator(RiskScoreSettings.Default, DateTime.UtcNow);
        foreach (var (hoursAgo, type) in rows.OrderBy(r => r.HoursAgo)) // newest first, as the Query reads
        {
            var hour = DateTime.UtcNow.AddHours(-hoursAgo).ToString("yyyy-MM-dd'T'HH':00:00.0000000Z'", System.Globalization.CultureInfo.InvariantCulture);
            calculator.Add(new Dictionary<string, AttributeValue>
            {
                ["signalKey"] = new() { S = $"{hour}#{type}" },
                ["signalType"] = new() { S = type },
                ["weight"] = new() { N = FraudDetectionService.SignalWeight(type).ToString() }
            });
        }

        return calculator.Result(truncated: false);
    }

    [Fact]
    public void RejectSignals_TiersFollowTheHoursOfProbing()
    {
        // One role or field probe is LOW, one create probe MEDIUM; the three hard probes of one hour
        // are HIGH; CRITICAL needs probes in more than one hour.
        Assert.Equal(RiskTier.Low, ScoreOf((1, "ROLE_FORBIDDEN")).Tier);
        Assert.Equal(RiskTier.Medium, ScoreOf((1, "IDOR_CREATE_CONFLICT")).Tier);
        Assert.Equal(RiskTier.High, ScoreOf(
            (1, "IDOR_CREATE_CONFLICT"), (1, "ROLE_FORBIDDEN"), (1, "IMMUTABLE_FIELD_CHANGE")).Tier);
        Assert.Equal(RiskTier.Critical, ScoreOf(
            (1, "IDOR_CREATE_CONFLICT"), (2, "IDOR_CREATE_CONFLICT"), (3, "ROLE_FORBIDDEN")).Tier);
    }

    [Fact]
    public void RejectSignals_HonestClientTypesStayLowAcrossADay()
    {
        // A QR failure, a stale-version burst and a suspended refresh in each of 5 hours: 25 points.
        var rows = Enumerable.Range(1, 5)
            .SelectMany(h => new[] { (h, "QR_HANDOFF_FAILURE"), (h, "STALE_VERSION_BURST"), (h, "SUSPENDED_TOKEN_USE") })
            .ToArray();

        var result = ScoreOf(rows);

        Assert.Equal(25, result.Score);
        Assert.Equal(RiskTier.Low, result.Tier);
    }

    [Theory]
    [InlineData("not-base64!!")]
    [InlineData("")]
    [InlineData("bm9zZXBhcmF0b3I")] // "noseparator"
    [InlineData("fHRyYWlsaW5n")] // "|trailing" — empty userId
    public void TryParseSignalId_InvalidInput_ReturnsFalse(string signalId)
    {
        Assert.False(FraudDetectionService.TryParseSignalId(signalId, out _, out _));
    }

    [Fact]
    public void SignalId_RoundTrips()
    {
        var signalKey = $"{DateTime.UtcNow:o}#FRAUD_DUPLICATE_LISTING";
        var id = FraudDetectionService.BuildSignalId(FarmerId, signalKey);

        Assert.True(FraudDetectionService.TryParseSignalId(id, out var userId, out var parsedKey));
        Assert.Equal(FarmerId, userId);
        Assert.Equal(signalKey, parsedKey);
    }

    // ===== Helper Methods =====

    /// <summary>
    /// Setup the DynamoDB mock to return specific items for the FarmerIdCreatedAtIndex GSI query.
    /// </summary>
    private void SetupFarmerListingsQuery(List<Dictionary<string, AttributeValue>> items)
    {
        _mockDynamoDb.QueryAsync(
                Arg.Is<QueryRequest>(r => r.IndexName == "FarmerIdCreatedAtIndex"),
                Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = items });
    }

    /// <summary>
    /// Setup the DynamoDB mock to return specific items for the CropTypeCreatedAtIndex GSI query.
    /// </summary>
    private void SetupPriceHistoryQuery(List<Dictionary<string, AttributeValue>> items)
    {
        _mockDynamoDb.QueryAsync(
                Arg.Is<QueryRequest>(r => r.IndexName == "CropTypeCreatedAtIndex"),
                Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = items });
    }
}
