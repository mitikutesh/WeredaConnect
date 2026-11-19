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

    // ===== FUP-135: Rule 2 on a robust band (median and MAD) =====

    /// <summary>
    /// Like the dev teff sample of FUP-135: 27 honest listings from 70 to 140 ETB/kg and four test listings at
    /// 1,000 (mean 217.6, sample deviation 306.7; dev had 216.6 and 322.0). Mean plus 3 deviations is about
    /// 1,138, so the old rule let 999 through.
    /// </summary>
    private static List<decimal> TeffSampleWithTestListings() =>
        Enumerable.Range(0, 27).Select(i => 70m + 5m * (i % 15)).Concat(Enumerable.Repeat(1000m, 4)).ToList();

    private void SetupPrices(IEnumerable<decimal> prices) =>
        SetupPriceHistoryQuery(prices
            .Select(p => new Dictionary<string, AttributeValue>
            {
                { "pricePerKg", new AttributeValue { N = p.ToString(System.Globalization.CultureInfo.InvariantCulture) } }
            })
            .ToList());

    [Fact]
    public async Task CheckListingCreation_AbsurdPriceIsFlagged_DespiteOutliersInTheSample()
    {
        var sample = TeffSampleWithTestListings();
        var mean = sample.Average();
        var sd = Math.Sqrt((double)sample.Sum(p => (p - mean) * (p - mean)) / (sample.Count - 1));
        Assert.True(Math.Abs(999 - (double)mean) < 3 * sd); // what the mean and deviation rule saw
        SetupFarmerListingsQuery(new List<Dictionary<string, AttributeValue>>());
        SetupPrices(sample);

        var result = await _service.CheckListingCreationAsync(FarmerId, "teff", 999m);

        Assert.True(result.IsAllowed); // flagged, not rejected
        Assert.True(result.IsFlagged);
        Assert.Equal("PRICE_ANOMALY", result.FlagReason);
        Assert.Equal("Price 999 ETB/kg is unusual for teff (median: 105.00, MAD: 20.00).", result.Message);
    }

    [Theory]
    [InlineData(70)]
    [InlineData(105)]
    [InlineData(160)]
    public async Task CheckListingCreation_AnOrdinaryPriceIsNotFlagged_InASampleWithOutliers(int price)
    {
        SetupFarmerListingsQuery(new List<Dictionary<string, AttributeValue>>());
        SetupPrices(TeffSampleWithTestListings());

        var result = await _service.CheckListingCreationAsync(FarmerId, "teff", price);

        Assert.True(result.IsAllowed);
        Assert.False(result.IsFlagged);
    }

    [Fact]
    public async Task CheckListingCreation_AnAbsurdlyLowPriceIsFlagged()
    {
        SetupFarmerListingsQuery(new List<Dictionary<string, AttributeValue>>());
        SetupPrices(TeffSampleWithTestListings());

        var result = await _service.CheckListingCreationAsync(FarmerId, "teff", 1m);

        Assert.True(result.IsFlagged);
        Assert.Equal("PRICE_ANOMALY", result.FlagReason);
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(129, false)] // within 3 spreads of 10 (the floor, 10% of the median)
    [InlineData(71, false)]
    [InlineData(131, true)]
    [InlineData(69, true)]
    [InlineData(999, true)]
    public async Task CheckListingCreation_MostFarmersAskingOnePrice_FlagsOnlyBeyondTheSpreadFloor(int price, bool flagged)
    {
        // 30 of 50 listings at 100 ETB/kg: the median absolute deviation is 0, so the spread is its floor.
        // The mean and deviation rule never flagged an all-identical sample at all (deviation 0).
        SetupFarmerListingsQuery(new List<Dictionary<string, AttributeValue>>());
        SetupPrices(Enumerable.Repeat(100m, 30).Concat(Enumerable.Range(0, 20).Select(i => 90m + i)));

        var result = await _service.CheckListingCreationAsync(FarmerId, "teff", price);

        Assert.True(result.IsAllowed);
        Assert.Equal(flagged, result.IsFlagged);
    }

    [Fact]
    public void PriceBand_IsTheMedianAndTheScaledMad_WithAFloor()
    {
        // Odd count: the middle value; MAD of {1, 2, 3, 4, 100} around 3 is median {2, 1, 0, 1, 97} = 1.
        var band = FraudDetectionService.PriceBand.Of(new List<decimal> { 4m, 1m, 100m, 3m, 2m })!;
        Assert.Equal(3m, band.Median);
        Assert.Equal(1m, band.Mad);
        Assert.Equal(FraudDetectionService.MadToStdDev, band.Spread);

        // Even count: the mean of the two middle values; the floor (10% of 200) beats 1.4826 x MAD (0).
        var flat = FraudDetectionService.PriceBand.Of(new List<decimal> { 200m, 200m, 200m, 200m, 200m, 200m })!;
        Assert.Equal(200m, flat.Median);
        Assert.Equal(0m, flat.Mad);
        Assert.Equal(20m, flat.Spread);

        Assert.Null(FraudDetectionService.PriceBand.Of(new List<decimal> { 1m, 2m, 3m, 4m }));
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
        // FUP-103: the row kind of section 20, which every reader also accepts absent (rows written before).
        Assert.Equal(FraudDetectionService.RecordTypeSignal, captured.Item["recordType"].S);
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

    // ===== FRD-16: one log line per signal row written (the signal metrics) =====

    private FraudDetectionService ServiceLoggingTo(List<string> lines, string fraudSignalsTable = FraudSignalsTable) =>
        new(_mockDynamoDb, CropsTable, fraudSignalsTable, _mockRateLimitRepo) { AlarmLog = lines.Add };

    [Fact]
    public async Task RecordFraudSignal_LogsTheSignalLineOnceTheRowIsWritten()
    {
        var lines = new List<string>();
        _mockDynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PutItemResponse());

        await ServiceLoggingTo(lines).RecordFraudSignalAsync(
            "user-with_underscore", "ROLE_FORBIDDEN", "crop-secret-1", "crop", "free text from a refusal");

        // The exact line the metric filters of infra/lib/stacks/monitoring-stack.ts read (fraud spec 23.1).
        var line = Assert.Single(lines);
        Assert.Equal("{\"event\":\"FRAUD_SIGNAL_RECORDED\",\"signalType\":\"ROLE_FORBIDDEN\",\"entityType\":\"crop\"}", line);
        // No user id, entity id or description.
        Assert.DoesNotContain("user-with", line);
        Assert.DoesNotContain("crop-secret", line);
        Assert.DoesNotContain("free text", line);
        using var json = System.Text.Json.JsonDocument.Parse(line);
        Assert.Equal(FraudDetectionService.FraudSignalRecordedToken, json.RootElement.GetProperty("event").GetString());
    }

    [Fact]
    public async Task RecordFraudSignal_TheWriteLimitLogsNoLine()
    {
        var lines = new List<string>();
        _mockDynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .Returns<PutItemResponse>(_ => throw new ConditionalCheckFailedException("exists"));

        await ServiceLoggingTo(lines).RecordFraudSignalAsync(UserId, "ROLE_FORBIDDEN", "crop-1", "crop", "msg");

        // A refusal the hourly limit absorbs is no new row, so the volume metric does not count it.
        Assert.Empty(lines);
    }

    [Fact]
    public async Task RecordFraudSignal_AFailedWriteOrNoTableLogsNoLine()
    {
        var lines = new List<string>();
        _mockDynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .Returns<PutItemResponse>(_ => throw new ProvisionedThroughputExceededException("busy"));

        await Assert.ThrowsAsync<ProvisionedThroughputExceededException>(
            () => ServiceLoggingTo(lines).RecordFraudSignalAsync(UserId, "ROLE_FORBIDDEN", "crop-1", "crop", "msg"));
        await ServiceLoggingTo(lines, fraudSignalsTable: "").RecordFraudSignalAsync(UserId, "ROLE_FORBIDDEN", null, "crop", "msg");
        await ServiceLoggingTo(lines).RecordFraudSignalAsync("", "ROLE_FORBIDDEN", null, "crop", "msg");

        Assert.Empty(lines);
    }

    [Theory]
    [InlineData("TYPE\"},\"x\":{\"y", "TYPEx:y")]
    [InlineData("A B\nC\\D", "ABCD")]
    [InlineData("", "-")]
    [InlineData(null, "-")]
    [InlineData("A_VERY_LONG_TYPE_A_VERY_LONG_TYPE_A_VERY_LONG_TYPE_A_VERY_LONG_TYPE_A_VERY_LONG_TYPE",
        "A_VERY_LONG_TYPE_A_VERY_LONG_TYPE_A_VERY_LONG_TYPE_A_VERY_LONG_T")]
    public void SignalRecordedLine_IsAlwaysValidJsonWithBoundedFields(string? signalType, string expectedType)
    {
        var line = FraudDetectionService.SignalRecordedLine(signalType, "or\"der");

        using var json = System.Text.Json.JsonDocument.Parse(line);
        Assert.Equal(expectedType, json.RootElement.GetProperty("signalType").GetString());
        Assert.Equal("order", json.RootElement.GetProperty("entityType").GetString());
        Assert.Equal(3, json.RootElement.EnumerateObject().Count());
    }

    [Theory]
    [InlineData("IDOR_CREATE_CONFLICT", 40, false, "high")]
    [InlineData("ROLE_FORBIDDEN", 25, false, "high")]
    [InlineData("IMMUTABLE_FIELD_CHANGE", 25, false, "high")]
    [InlineData("QR_HANDOFF_FAILURE", 5, true, "low")]
    [InlineData("TELEBIRR_PAYER_MISMATCH", 0, true, "low")]
    [InlineData("TELEBIRR_PAYER_REFUSED", 5, true, "low")]
    [InlineData("STALE_VERSION_BURST", 0, true, "low")]
    [InlineData("SUSPENDED_TOKEN_USE", 0, true, "low")]
    [InlineData("GPS_JUMP", 10, true, "medium")]
    [InlineData("GPS_MOCKED", 25, true, "high")]
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

    private static RiskScoreResult ScoreOf(params (int HoursAgo, string Type, string? EntityId)[] rows)
        => Calculate(rows.Select(r => (r.HoursAgo, r.Type, r.EntityId, (string?)null)).ToArray()).Result(truncated: false);

    private static RiskScoreResult ScoreOf(params (int HoursAgo, string Type, string? EntityId, string? Resolution)[] rows)
        => Calculate(rows).Result(truncated: false);

    private static RiskScoreCalculator Calculate((int HoursAgo, string Type, string? EntityId, string? Resolution)[] rows)
    {
        var calculator = new RiskScoreCalculator(RiskScoreSettings.Default, DateTime.UtcNow);
        foreach (var (hoursAgo, type, entityId, resolution) in rows.OrderBy(r => r.HoursAgo)) // newest first, as the Query reads
        {
            var hour = DateTime.UtcNow.AddHours(-hoursAgo).ToString("yyyy-MM-dd'T'HH':00:00.0000000Z'", System.Globalization.CultureInfo.InvariantCulture);
            var row = new Dictionary<string, AttributeValue>
            {
                ["signalKey"] = new() { S = $"{hour}#{type}" },
                ["signalType"] = new() { S = type },
                ["weight"] = new() { N = FraudDetectionService.SignalWeight(type).ToString() }
            };
            if (entityId != null)
            {
                row["entityId"] = new AttributeValue { S = entityId };
            }

            if (resolution != null)
            {
                row["resolution"] = new AttributeValue { S = resolution };
            }

            calculator.Add(row);
        }

        return calculator;
    }

    [Fact]
    public void RejectSignals_TiersFollowTheNumberOfProbedIds()
    {
        // One refused operation is LOW (25) however often it is retried, one create probe MEDIUM
        // (40); the three hard types on three ids are HIGH; CRITICAL needs probes of several ids.
        Assert.Equal(RiskTier.Low, ScoreOf((1, "ROLE_FORBIDDEN", "crop-1")).Tier);
        Assert.Equal(RiskTier.Medium, ScoreOf((1, "IDOR_CREATE_CONFLICT", "crop-1")).Tier);
        Assert.Equal(RiskTier.High, ScoreOf(
            (1, "IDOR_CREATE_CONFLICT", "crop-1"), (1, "ROLE_FORBIDDEN", "crop-2"), (1, "IMMUTABLE_FIELD_CHANGE", "crop-3")).Tier);
        Assert.Equal(RiskTier.Critical, ScoreOf(
            (1, "IDOR_CREATE_CONFLICT", "crop-1"), (2, "IDOR_CREATE_CONFLICT", "crop-2"), (3, "ROLE_FORBIDDEN", "crop-3")).Tier);
    }

    [Theory]
    [InlineData("ROLE_FORBIDDEN", 25)]
    [InlineData("IMMUTABLE_FIELD_CHANGE", 25)]
    [InlineData("QR_HANDOFF_FAILURE", 5)]
    [InlineData("TELEBIRR_PAYER_REFUSED", 5)]
    public void RejectSignals_AStuckOperationRetriedEveryHourCountsOnce(string type, int weight)
    {
        // The mobile queue resets failed operations on every reconnect, so one honest refusal writes
        // a row in each hour it is retried (here every hour for 30 days): it counts once.
        var rows = Enumerable.Range(1, 30 * 24 - 1).Select(h => (h, type, (string?)"delivery-1")).ToArray();

        var result = ScoreOf(rows);

        Assert.Equal(weight, result.Score);
        Assert.NotEqual(RiskTier.Critical, result.Tier);
    }

    [Fact]
    public void RejectSignals_IdorCountsEveryHour_NoHonestRetryRepeatsIt()
    {
        // A conflict is final for the client and web makes a new id per create: IDOR rows in three
        // hours count three times even when each hour's row names the same lead entity.
        var result = ScoreOf((1, "IDOR_CREATE_CONFLICT", "crop-1"), (2, "IDOR_CREATE_CONFLICT", "crop-1"),
            (3, "IDOR_CREATE_CONFLICT", "crop-1"));

        Assert.Equal(120, result.Score);
        Assert.Equal(RiskTier.Critical, result.Tier);
        Assert.False(FraudDetectionService.CountsOncePerEntity("IDOR_CREATE_CONFLICT"));
    }

    [Fact]
    public void RejectSignals_ApprovingAnOldRowClearsTheNewerRowsOfThatEntity()
    {
        // A stuck operation retried in hours 1 to 4; the admin approved its row of hour 5. The
        // newer rows are read first and counted, then the approved row clears the whole entity.
        var result = ScoreOf(
            (1, "ROLE_FORBIDDEN", "crop-1", null), (2, "ROLE_FORBIDDEN", "crop-1", null),
            (3, "ROLE_FORBIDDEN", "crop-1", null), (4, "ROLE_FORBIDDEN", "crop-1", null),
            (5, "ROLE_FORBIDDEN", "crop-1", "approve"), (6, "ROLE_FORBIDDEN", "crop-1", null),
            (2, "ROLE_FORBIDDEN", "crop-2", null));

        Assert.Equal(25, result.Score); // only crop-2 is left
    }

    [Fact]
    public void RejectSignals_ApprovingTheNewestRowClearsTheOlderRowsOfThatEntity()
    {
        var result = ScoreOf(
            (1, "QR_HANDOFF_FAILURE", "delivery-1", "whitelist"), (2, "QR_HANDOFF_FAILURE", "delivery-1", null),
            (3, "QR_HANDOFF_FAILURE", "delivery-1", null), (4, "QR_HANDOFF_FAILURE", "delivery-2", null));

        Assert.Equal(5, result.Score); // only delivery-2
    }

    [Fact]
    public void RejectSignals_RoleAndFieldRefusalsAreCappedPerType()
    {
        // RISK_REJECT_TYPE_CAP (50): one capped type alone stops at MEDIUM, both together reach
        // CRITICAL.
        var role = Enumerable.Range(1, 4).Select(h => (h, "ROLE_FORBIDDEN", (string?)$"crop-{h}")).ToArray();
        var field = Enumerable.Range(1, 4).Select(h => (h, "IMMUTABLE_FIELD_CHANGE", (string?)$"order-{h}")).ToArray();

        Assert.Equal(50, ScoreOf(role).Score);
        Assert.Equal(RiskTier.Medium, ScoreOf(role).Tier);
        Assert.Equal(100, ScoreOf(role.Concat(field).ToArray()).Score);
        Assert.Equal(RiskTier.Critical, ScoreOf(role.Concat(field).ToArray()).Tier);
        // The cap applies to rows without an id too (ESCROW_025 at the order checkout).
        Assert.Equal(50, ScoreOf(Enumerable.Range(1, 3).Select(h => (h, "ROLE_FORBIDDEN", (string?)null)).ToArray()).Score);
        Assert.True(FraudDetectionService.IsCappedRejectType("ROLE_FORBIDDEN"));
        Assert.False(FraudDetectionService.IsCappedRejectType("IDOR_CREATE_CONFLICT"));
    }

    [Fact]
    public void RejectSignals_TheCapFollowsTheSetting()
    {
        var settings = RiskScoreSettings.Default with { RejectTypeCap = 75 };
        var calculator = new RiskScoreCalculator(settings, DateTime.UtcNow);
        var hour = DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-dd'T'HH':00:00.0000000Z'", System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < 4; i++)
        {
            calculator.Add(new Dictionary<string, AttributeValue>
            {
                ["signalKey"] = new() { S = $"{hour}#ROLE_FORBIDDEN" },
                ["signalType"] = new() { S = "ROLE_FORBIDDEN" },
                ["weight"] = new() { N = "25" },
                ["entityId"] = new() { S = $"crop-{i}" }
            });
        }

        Assert.Equal(75, calculator.Result(truncated: false).Score);
    }

    [Fact]
    public void RejectSignals_EntityWeightNeverStopsTheReadEarly()
    {
        // An older approved row can still clear an entity, so weight that sits in entity buckets
        // does not end the read (section 14.3); hourly weight does.
        var qr = Enumerable.Range(1, 30).Select(h => (h, "QR_HANDOFF_FAILURE", (string?)$"delivery-{h}", (string?)null)).ToArray();
        var entityOnly = Calculate(qr);
        Assert.False(entityOnly.ReachedCritical);
        Assert.Equal(150, entityOnly.Result(truncated: false).Score);

        var idor = Enumerable.Range(1, 4).Select(h => (h, "IDOR_CREATE_CONFLICT", (string?)$"crop-{h}", (string?)null)).ToArray();
        Assert.True(Calculate(idor).ReachedCritical);

        // Capped role weight with no entity cannot be cleared, but the cap bounds it below the stop.
        var role = Enumerable.Range(1, 6).Select(h => (h, "ROLE_FORBIDDEN", (string?)null, (string?)null)).ToArray();
        Assert.False(Calculate(role).ReachedCritical);
    }

    [Fact]
    public void RejectSignals_DifferentIdsAddUp_RowsWithoutAnIdCountPerHour()
    {
        // QR failures on five deliveries, one per hour: each counts.
        Assert.Equal(25, ScoreOf(Enumerable.Range(1, 5).Select(h => (h, "QR_HANDOFF_FAILURE", (string?)$"delivery-{h}")).ToArray()).Score);
        // Two ids of ROLE_FORBIDDEN add up (below the cap).
        Assert.Equal(50, ScoreOf((1, "ROLE_FORBIDDEN", "crop-1"), (2, "ROLE_FORBIDDEN", "crop-2")).Score);
        // A row without an id keeps the hourly bucket of section 14.2: SUSPENDED_TOKEN_USE weighs 0, so
        // use TELEBIRR_PAYER_REFUSED rows without an id.
        Assert.Equal(15, ScoreOf(Enumerable.Range(1, 3).Select(h => (h, "TELEBIRR_PAYER_REFUSED", (string?)null)).ToArray()).Score);
    }

    [Theory]
    [InlineData("GPS_MOCKED", 25)]
    [InlineData("GPS_JUMP", 10)]
    public void CheckInSignals_CountOncePerTrip_HoweverManyHoursTheTruckDrives(string type, int weight)
    {
        // LT-07: a mock-location app on for a 20 hour trip writes a row in each hour: it counts once
        // for the trip, and a second trip counts again.
        var oneTrip = Enumerable.Range(1, 20).Select(h => (h, type, (string?)"trip-1")).ToArray();
        var twoTrips = oneTrip.Append((30, type, (string?)"trip-2")).ToArray();

        Assert.Equal(weight, ScoreOf(oneTrip).Score);
        Assert.Equal(2 * weight, ScoreOf(twoTrips).Score);
        Assert.Equal(RiskTier.Low, ScoreOf(oneTrip).Tier);
        Assert.True(FraudDetectionService.CountsOncePerEntity(type));
        Assert.False(FraudDetectionService.IsCappedRejectType(type));
    }

    [Fact]
    public void RejectSignals_TheEarlierProducersKeepTheHourlyCount()
    {
        // Only FRD-08 types count per entity: a velocity refusal of one listing in three hours is 45.
        var rows = Enumerable.Range(1, 3).Select(h => (h, "FRAUD_VELOCITY_EXCEEDED", (string?)"crop-1")).ToArray();

        Assert.Equal(45, ScoreOf(rows).Score);
        Assert.False(FraudDetectionService.CountsOncePerEntity("FRAUD_VELOCITY_EXCEEDED"));
        Assert.False(FraudDetectionService.CountsOncePerEntity("GPS_ANOMALY"));
    }

    [Fact]
    public void RejectSignals_HonestClientTypesStayLowAcrossADay()
    {
        // A QR failure on each of 5 deliveries, a stale-version burst, a suspended refresh and an
        // applied payment from a relative's wallet in each of 5 hours: 25 points.
        var rows = Enumerable.Range(1, 5)
            .SelectMany(h => new (int, string, string?)[]
            {
                (h, "QR_HANDOFF_FAILURE", $"delivery-{h}"), (h, "STALE_VERSION_BURST", $"crop-{h}"),
                (h, "SUSPENDED_TOKEN_USE", null), (h, "TELEBIRR_PAYER_MISMATCH", $"escrow-order-{h}")
            })
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

    // ===== Duplicate lot (Rule 1): the same stock at another price =====

    private const string LotCropId = "crop-new";
    private const double FarmLat = 9.6797;
    private const double FarmLng = 39.5308;

    private static ListingLot WheatLot(decimal quantity = 50m, string woreda = "Debre Birhan",
        double? lat = FarmLat, double? lng = FarmLng, string cropId = LotCropId) =>
        new(cropId, quantity, woreda, lat, lng);

    private static Dictionary<string, AttributeValue> StoredListing(
        string cropId, decimal price, decimal quantity = 50m, string cropType = "wheat",
        string woreda = "Debre Birhan", double? lat = FarmLat, double? lng = FarmLng,
        string status = "available", double hoursAgo = 0.01, bool deleted = false)
    {
        var row = new Dictionary<string, AttributeValue>
        {
            { "cropId", new AttributeValue { S = cropId } },
            { "createdAt", new AttributeValue { S = DateTime.UtcNow.AddHours(-hoursAgo).ToString("o") } },
            { "cropType", new AttributeValue { S = cropType } },
            { "pricePerKg", new AttributeValue { N = price.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
            { "quantity", new AttributeValue { N = quantity.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
            { "woreda", new AttributeValue { S = woreda } },
            { "status", new AttributeValue { S = status } }
        };
        if (lat is { } la && lng is { } ln)
        {
            row["latitude"] = new AttributeValue { N = la.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            row["longitude"] = new AttributeValue { N = ln.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        }

        if (deleted)
        {
            row["deletedAt"] = new AttributeValue { S = DateTime.UtcNow.ToString("o") };
        }

        return row;
    }

    /// <summary>
    /// The farmer's stored listings behind the mock: the farmer index answers with the filters DynamoDB
    /// would apply (the crop type and the age when asked for, never a deleted row), and BatchGetItem
    /// returns the rows whose keys were asked for.
    /// </summary>
    private void SetupFarmerListings(params Dictionary<string, AttributeValue>[] listings)
    {
        _mockDynamoDb.QueryAsync(
                Arg.Is<QueryRequest>(r => r.IndexName == "FarmerIdCreatedAtIndex"), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<QueryRequest>();
                var rows = listings.Where(l => !l.ContainsKey("deletedAt"));
                if (request.ExpressionAttributeValues.TryGetValue(":ct", out var cropType))
                {
                    rows = rows.Where(l => l["cropType"].S == cropType.S);
                }

                if (request.ExpressionAttributeValues.TryGetValue(":since", out var since))
                {
                    rows = rows.Where(l => string.CompareOrdinal(l["createdAt"].S, since.S) >= 0);
                }

                return new QueryResponse { Items = rows.ToList() };
            });
        _mockDynamoDb.BatchGetItemAsync(Arg.Any<BatchGetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var asked = call.Arg<BatchGetItemRequest>().RequestItems[CropsTable].Keys.Select(k => k["cropId"].S).ToHashSet();
                return new BatchGetItemResponse
                {
                    Responses = new Dictionary<string, List<Dictionary<string, AttributeValue>>>
                    {
                        { CropsTable, listings.Where(l => asked.Contains(l["cropId"].S)).ToList() }
                    }
                };
            });
    }

    [Theory]
    [InlineData(75)]
    [InlineData(90)]
    [InlineData(110)]
    public async Task CheckListingCreation_TheSameLotAtAnotherPrice_IsRefused(int price)
    {
        // BUG-2: 50 quintals of wheat listed at 60, then again at 75, 90 and 110. Each is more than 5%
        // from the first, so the price band lets it through; the lot is the same.
        SetupFarmerListings(StoredListing("crop-1", 60m));

        var result = await _service.CheckListingCreationAsync(FarmerId, "wheat", price, WheatLot());

        Assert.False(result.IsAllowed);
        Assert.Equal("FRAUD_DUPLICATE_LISTING", result.ErrorCode);
        Assert.Equal("A similar listing already exists. Please update your existing listing instead.", result.Message);
        // Recorded under a type that never scores, not under the wire code.
        Assert.Equal(FraudDetectionService.DuplicateLotSignal, result.FlagReason);
    }

    [Fact]
    public async Task CheckListingCreation_ThePriceBandRefusal_KeepsItsScoringSignal()
    {
        // The rule from before the lot check: same crop type, a price within 5 percent, the last hour.
        // It names no other signal type, so the create path records FRAUD_DUPLICATE_LISTING (20).
        SetupFarmerListings(StoredListing("crop-1", 60m, quantity: 30m));

        var result = await _service.CheckListingCreationAsync(FarmerId, "wheat", 61m, WheatLot());

        Assert.Equal("FRAUD_DUPLICATE_LISTING", result.ErrorCode);
        Assert.Null(result.FlagReason);
        Assert.Equal(20, FraudDetectionService.SignalWeight("FRAUD_DUPLICATE_LISTING"));
    }

    [Theory]
    [InlineData("DUPLICATE_LOT")]
    [InlineData("DUPLICATE_TRIP")]
    public void RefusalsHonestUsersAlsoMeet_AreRecordedForReviewAndNeverScore(string type)
    {
        // A farmer who lists the same harvest size again in five different hours, or a trucker whose web
        // Save was sent twice five times: five rows, a score of 0. Under FRAUD_DUPLICATE_LISTING (20,
        // hard, uncapped) the same five would be CRITICAL.
        Assert.Equal(0, FraudDetectionService.SignalWeight(type));
        Assert.True(FraudDetectionService.IsSoftSignal(type));
        var rows = Enumerable.Range(1, 5).Select(h => (h, type, (string?)$"entity-{h}")).ToArray();

        var result = ScoreOf(rows);

        Assert.Equal(0, result.Score);
        Assert.Equal(RiskTier.Low, result.Tier);
        Assert.Equal(RiskTier.Critical,
            ScoreOf(Enumerable.Range(1, 5).Select(h => (h, "FRAUD_DUPLICATE_LISTING", (string?)$"entity-{h}")).ToArray()).Tier);
    }

    [Theory]
    [InlineData("PICKUP_NO_FIX")]
    [InlineData("ORDER_ROUTE_MISMATCH")]
    [InlineData("CHAT_CONTACT_BEFORE_DELIVERY")]
    public void ReviewOnlyFlags_OfThingsHonestUsersAlsoDo_NeverScore_HoweverManyRows(string type)
    {
        // Rule 7 (persona simulation 2026-11-19): a trucker in a valley without a fix at every pickup, a
        // buyer whose trips are named after the town and not the woreda, a farmer who gives a number
        // in every chat. Twenty rows on twenty entities: a score of 0, tier LOW.
        Assert.Equal(0, FraudDetectionService.SignalWeight(type));
        Assert.True(FraudDetectionService.IsSoftSignal(type));
        var rows = Enumerable.Range(1, 20).Select(h => (h, type, (string?)$"entity-{h}")).ToArray();

        var result = ScoreOf(rows);

        Assert.Equal(0, result.Score);
        Assert.Equal(RiskTier.Low, result.Tier);
        // The default weight of an unknown type (10) would have made the same rows CRITICAL.
        Assert.Equal(RiskTier.Critical,
            ScoreOf(Enumerable.Range(1, 20).Select(h => (h, "SOME_UNLISTED_TYPE", (string?)$"entity-{h}")).ToArray()).Tier);
    }

    [Fact]
    public void ReviewOnlyFlags_AddNothingToAUserWhoAlreadyScores()
    {
        // One hard signal (20) with and without the three flags beside it: the same score and tier.
        var alone = ScoreOf((1, "FRAUD_DUPLICATE_LISTING", "crop-1"));

        var withFlags = ScoreOf(
            (1, "FRAUD_DUPLICATE_LISTING", "crop-1"), (2, "PICKUP_NO_FIX", "del-1"),
            (3, "ORDER_ROUTE_MISMATCH", "order-1"), (4, "CHAT_CONTACT_BEFORE_DELIVERY", "order-2"));

        Assert.Equal(alone.Score, withFlags.Score);
        Assert.Equal(alone.Tier, withFlags.Tier);
    }

    [Fact]
    public async Task CheckListingCreation_TheSameLotListedDaysAgo_IsStillRefused()
    {
        // The price band only looks back an hour; a lot stays one lot for as long as its listing is active.
        SetupFarmerListings(StoredListing("crop-1", 60m, hoursAgo: 72));

        var result = await _service.CheckListingCreationAsync(FarmerId, "wheat", 110m, WheatLot());

        Assert.Equal("FRAUD_DUPLICATE_LISTING", result.ErrorCode);
    }

    [Fact]
    public async Task CheckListingCreation_ASecondLotOfAnotherQuantity_IsAllowed()
    {
        SetupFarmerListings(StoredListing("crop-1", 60m, quantity: 50m));

        var result = await _service.CheckListingCreationAsync(FarmerId, "wheat", 75m, WheatLot(quantity: 30m));

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task CheckListingCreation_ASecondLotInAnotherWoreda_IsAllowed()
    {
        SetupFarmerListings(StoredListing("crop-1", 60m, woreda: "Debre Birhan"));

        var result = await _service.CheckListingCreationAsync(
            FarmerId, "wheat", 75m, WheatLot(woreda: "Ankober", lat: 9.59, lng: 39.73));

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task CheckListingCreation_ASecondLotAtAnotherFieldOfTheWoreda_IsAllowed()
    {
        // About 3 km north of the first field: two stores of one farmer in one woreda.
        SetupFarmerListings(StoredListing("crop-1", 60m));

        var result = await _service.CheckListingCreationAsync(
            FarmerId, "wheat", 75m, WheatLot(lat: FarmLat + 0.027, lng: FarmLng));

        Assert.True(result.IsAllowed);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task CheckListingCreation_WithoutAPointOnEitherSide_TheWoredaPlacesTheLot(bool storedHasPoint, bool newHasPoint)
    {
        SetupFarmerListings(storedHasPoint ? StoredListing("crop-1", 60m) : StoredListing("crop-1", 60m, lat: null, lng: null));
        var lot = newHasPoint ? WheatLot() : WheatLot(lat: null, lng: null);

        var result = await _service.CheckListingCreationAsync(FarmerId, "wheat", 75m, lot);

        Assert.Equal("FRAUD_DUPLICATE_LISTING", result.ErrorCode);
    }

    [Fact]
    public async Task CheckListingCreation_TheWoredaIsComparedWithoutCaseOrOuterSpaces()
    {
        SetupFarmerListings(StoredListing("crop-1", 60m, woreda: "Debre Birhan"));

        var result = await _service.CheckListingCreationAsync(FarmerId, "wheat", 75m, WheatLot(woreda: " debre birhan "));

        Assert.Equal("FRAUD_DUPLICATE_LISTING", result.ErrorCode);
    }

    [Fact]
    public async Task CheckListingCreation_AReservedListingStillHoldsItsLot()
    {
        SetupFarmerListings(StoredListing("crop-1", 60m, status: "reserved"));

        var result = await _service.CheckListingCreationAsync(FarmerId, "wheat", 75m, WheatLot());

        Assert.Equal("FRAUD_DUPLICATE_LISTING", result.ErrorCode);
    }

    [Fact]
    public async Task CheckListingCreation_ASoldOrDeletedListing_FreesItsLot()
    {
        // Last season's sold listing and a deleted one: the farmer lists the same quantity again.
        SetupFarmerListings(
            StoredListing("crop-sold", 60m, status: "sold", hoursAgo: 200),
            StoredListing("crop-deleted-by-update", 60m, status: "deleted", hoursAgo: 100),
            StoredListing("crop-deleted", 60m, hoursAgo: 50, deleted: true));

        var result = await _service.CheckListingCreationAsync(FarmerId, "wheat", 75m, WheatLot());

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task CheckListingCreation_AnotherCropTypeOfTheSameQuantityAndPlace_IsAllowed()
    {
        SetupFarmerListings(StoredListing("crop-1", 60m, cropType: "barley"));

        var result = await _service.CheckListingCreationAsync(FarmerId, "wheat", 75m, WheatLot());

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task CheckListingCreation_AReplayedCreate_IsNotItsOwnDuplicateLot()
    {
        // A lost response makes the phone send its create again, here more than an hour later (so the
        // price band is out of the picture): the stored row is this listing, and the create goes on
        // to the id conflict.
        SetupFarmerListings(StoredListing(LotCropId, 60m, hoursAgo: 2));

        var result = await _service.CheckListingCreationAsync(FarmerId, "wheat", 60m, WheatLot());

        Assert.True(result.IsAllowed);
        await _mockDynamoDb.DidNotReceive().BatchGetItemAsync(Arg.Any<BatchGetItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckListingCreation_WithoutALot_KeepsThePriceBandOnly()
    {
        SetupFarmerListings(StoredListing("crop-1", 60m));

        var result = await _service.CheckListingCreationAsync(FarmerId, "wheat", 110m);

        Assert.True(result.IsAllowed);
        await _mockDynamoDb.DidNotReceive().BatchGetItemAsync(Arg.Any<BatchGetItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckListingCreation_TheLotIsReadFromTheTable_StronglyConsistent_ByTheKeysOfTheFarmerIndex()
    {
        SetupFarmerListings(StoredListing("crop-1", 60m), StoredListing("crop-2", 20m, cropType: "barley"));

        await _service.CheckListingCreationAsync(FarmerId, "wheat", 110m, WheatLot());

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r => r.TableName == CropsTable && r.IndexName == "FarmerIdCreatedAtIndex"
                && r.KeyConditionExpression == "farmerId = :fid"
                && r.FilterExpression == "cropType = :ct AND attribute_not_exists(deletedAt)"
                && r.ExpressionAttributeValues[":fid"].S == FarmerId
                && r.ExpressionAttributeValues[":ct"].S == "wheat"),
            Arg.Any<CancellationToken>());
        await _mockDynamoDb.Received(1).BatchGetItemAsync(
            Arg.Is<BatchGetItemRequest>(r => r.RequestItems[CropsTable].ConsistentRead == true
                && r.RequestItems[CropsTable].Keys.Count == 1
                && r.RequestItems[CropsTable].Keys[0]["cropId"].S == "crop-1"
                && r.RequestItems[CropsTable].Keys[0].ContainsKey("createdAt")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckListingCreation_UnprocessedKeysAreAskedAgain()
    {
        var stored = StoredListing("crop-1", 60m);
        SetupFarmerListings(stored);
        var key = new Dictionary<string, AttributeValue> { { "cropId", stored["cropId"] }, { "createdAt", stored["createdAt"] } };
        _mockDynamoDb.BatchGetItemAsync(Arg.Any<BatchGetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new BatchGetItemResponse
                {
                    Responses = new Dictionary<string, List<Dictionary<string, AttributeValue>>>(),
                    UnprocessedKeys = new Dictionary<string, KeysAndAttributes>
                    {
                        { CropsTable, new KeysAndAttributes { Keys = new List<Dictionary<string, AttributeValue>> { key } } }
                    }
                },
                new BatchGetItemResponse
                {
                    Responses = new Dictionary<string, List<Dictionary<string, AttributeValue>>>
                    {
                        { CropsTable, new List<Dictionary<string, AttributeValue>> { stored } }
                    }
                });

        var result = await _service.CheckListingCreationAsync(FarmerId, "wheat", 110m, WheatLot());

        Assert.Equal("FRAUD_DUPLICATE_LISTING", result.ErrorCode);
        await _mockDynamoDb.Received(2).BatchGetItemAsync(Arg.Any<BatchGetItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void IsSameLot_ReadsAQuantityStoredAsAString()
    {
        var row = StoredListing("crop-1", 60m);
        row["quantity"] = new AttributeValue { S = "50.0" };

        Assert.True(FraudDetectionService.IsSameLot(row, WheatLot()));
    }

    // ===== Trips (Rule 6): one truck, one trip on a route per day =====

    private const string TripsTable = "test-trips-table";
    private const string TruckerId = "trucker-123";
    private static readonly DateTime Departure = new(2026, 11, 22, 6, 0, 0, DateTimeKind.Utc);

    private FraudDetectionService TripService() =>
        new(_mockDynamoDb, CropsTable, FraudSignalsTable, _mockRateLimitRepo) { TripsTable = TripsTable };

    private static TripPlan Plan(string from = "Debre Birhan", string to = "Addis Ababa", DateTime? departure = null,
        string? plate = null, string tripId = "trip-new") =>
        new(TruckerId, tripId, from, to, departure ?? Departure, plate);

    private static Dictionary<string, AttributeValue> StoredTrip(
        string tripId, string from = "Debre Birhan", string to = "Addis Ababa", string departure = "2026-11-22T06:00:00.000Z",
        string status = "open", string? plate = null)
    {
        var row = new Dictionary<string, AttributeValue>
        {
            { "tripId", new AttributeValue { S = tripId } },
            { "fromWoreda", new AttributeValue { S = from } },
            { "toWoreda", new AttributeValue { S = to } },
            { "departureDate", new AttributeValue { S = departure } },
            { "status", new AttributeValue { S = status } }
        };
        if (plate != null)
        {
            row["vehiclePlate"] = new AttributeValue { S = plate };
        }

        return row;
    }

    /// <summary>
    /// The trucker's stored trips behind the mock: the index answers with the status filter DynamoDB would
    /// apply, and the table answers a read of one trip id.
    /// </summary>
    private void SetupTruckerTrips(params Dictionary<string, AttributeValue>[] trips) => SetupTruckerTrips(trips, trips);

    /// <summary>The same with an index that lags the table: <paramref name="indexed"/> is what the index still says.</summary>
    private void SetupTruckerTrips(Dictionary<string, AttributeValue>[] indexed, Dictionary<string, AttributeValue>[] table)
    {
        _mockDynamoDb.QueryAsync(
                Arg.Is<QueryRequest>(r => r.TableName == TripsTable && r.IndexName == "TruckerIdCreatedAtIndex"),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var values = call.Arg<QueryRequest>().ExpressionAttributeValues;
                var statuses = new[] { values[":open"].S, values[":matched"].S };
                return new QueryResponse { Items = indexed.Where(t => statuses.Contains(t["status"].S)).ToList() };
            });
        _mockDynamoDb.QueryAsync(
                Arg.Is<QueryRequest>(r => r.TableName == TripsTable && r.IndexName == null), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = call.Arg<QueryRequest>().ExpressionAttributeValues[":id"].S;
                return new QueryResponse { Items = table.Where(t => t["tripId"].S == id).ToList() };
            });
    }

    [Theory]
    [InlineData("open")]
    [InlineData("matched")]
    public async Task CheckTripCreation_TheSameRouteAndDay_IsRefused_WhateverThePrice(string status)
    {
        // BUG-4: Debre Birhan to Addis Ababa on one morning at 100, then again at 150 and 200. The price
        // is not part of the plan at all.
        SetupTruckerTrips(StoredTrip("trip-1", status: status));

        var result = await TripService().CheckTripCreationAsync(Plan());

        Assert.False(result.IsAllowed);
        Assert.Equal("FRAUD_DUPLICATE_LISTING", result.ErrorCode);
        Assert.Equal("You already have a trip on this route on that day. Please update your existing trip instead.", result.Message);
        // Recorded under a type that never scores, not under the wire code.
        Assert.Equal(FraudDetectionService.DuplicateTripSignal, result.FlagReason);
    }

    [Fact]
    public async Task CheckTripCreation_ADuplicateIsConfirmedOnTheTripRow_StronglyConsistent()
    {
        SetupTruckerTrips(StoredTrip("trip-1"));

        await TripService().CheckTripCreationAsync(Plan());

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r => r.TableName == TripsTable && r.IndexName == null && r.ConsistentRead == true
                && r.KeyConditionExpression == "tripId = :id" && r.ExpressionAttributeValues[":id"].S == "trip-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckTripCreation_ATripCancelledAMomentAgo_StillOpenOnTheIndex_DoesNotRefuse()
    {
        // One offline batch: create A, cancel A, create B on the same route and day. The index still
        // says A is open when B arrives; the table says cancelled.
        SetupTruckerTrips(
            indexed: new[] { StoredTrip("trip-1") },
            table: new[] { StoredTrip("trip-1", status: "cancelled") });

        var result = await TripService().CheckTripCreationAsync(Plan());

        Assert.True(result.IsAllowed);
        Assert.False(result.IsFlagged);
    }

    [Fact]
    public async Task CheckTripCreation_ATripMovedToAnotherDayAMomentAgo_DoesNotRefuse()
    {
        SetupTruckerTrips(
            indexed: new[] { StoredTrip("trip-1") },
            table: new[] { StoredTrip("trip-1", departure: "2026-11-27T06:00:00.000Z") });

        var result = await TripService().CheckTripCreationAsync(Plan());

        Assert.True(result.IsAllowed);
        Assert.False(result.IsFlagged);
    }

    [Fact]
    public async Task CheckTripCreation_ATripMovedToAnotherRouteAMomentAgo_IsFlaggedByItsTableRow()
    {
        SetupTruckerTrips(
            indexed: new[] { StoredTrip("trip-1") },
            table: new[] { StoredTrip("trip-1", from: "Dire Dawa", to: "Mekelle") });

        var result = await TripService().CheckTripCreationAsync(Plan());

        Assert.True(result.IsAllowed);
        Assert.Equal(FraudDetectionService.TripSameDaySignal, result.FlagReason);
        Assert.Contains("(Dire Dawa to Mekelle)", result.Message);
    }

    [Fact]
    public async Task CheckTripCreation_ATripGoneFromTheTable_DoesNotRefuse()
    {
        SetupTruckerTrips(indexed: new[] { StoredTrip("trip-1") }, table: Array.Empty<Dictionary<string, AttributeValue>>());

        var result = await TripService().CheckTripCreationAsync(Plan());

        Assert.True(result.IsAllowed);
        Assert.False(result.IsFlagged);
    }

    [Fact]
    public async Task CheckTripCreation_ADeletedTripRow_DoesNotRefuse()
    {
        var deleted = StoredTrip("trip-1");
        deleted["deletedAt"] = new AttributeValue { S = "2026-11-19T08:00:00Z" };
        SetupTruckerTrips(indexed: new[] { StoredTrip("trip-1") }, table: new[] { deleted });

        var result = await TripService().CheckTripCreationAsync(Plan());

        Assert.True(result.IsAllowed);
    }

    [Theory]
    [InlineData("inTransit")]
    [InlineData("delivered")]
    [InlineData("cancelled")]
    public async Task CheckTripCreation_ATripThatLeftOrWasCancelled_FreesTheRoute(string status)
    {
        SetupTruckerTrips(StoredTrip("trip-1", status: status));

        var result = await TripService().CheckTripCreationAsync(Plan());

        Assert.True(result.IsAllowed);
        Assert.False(result.IsFlagged);
    }

    [Theory]
    // The Ethiopia day of 22 November runs from 21 Nov 21:00 UTC to 22 Nov 21:00 UTC.
    [InlineData("2026-11-21T21:00:00Z", true)]
    [InlineData("2026-11-22T20:59:00Z", true)]
    [InlineData("2026-11-21T20:59:00Z", false)]
    [InlineData("2026-11-22T21:00:00Z", false)]
    public async Task CheckTripCreation_TheDayIsTheEthiopiaDay(string newDeparture, bool sameDay)
    {
        SetupTruckerTrips(StoredTrip("trip-1"));
        var departure = DateTime.Parse(newDeparture, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal);

        var result = await TripService().CheckTripCreationAsync(Plan(departure: departure));

        Assert.Equal(!sameDay, result.IsAllowed);
        Assert.False(result.IsFlagged);
    }

    [Fact]
    public async Task CheckTripCreation_AnotherRouteOnTheSameDay_IsFlaggedNotRefused()
    {
        // BUG-4: Dire Dawa to Mekelle on the morning of the Debre Birhan trip. A trucker can chain legs,
        // so the trip is written and only flagged.
        SetupTruckerTrips(StoredTrip("trip-1"));

        var result = await TripService().CheckTripCreationAsync(Plan(from: "Dire Dawa", to: "Mekelle"));

        Assert.True(result.IsAllowed);
        Assert.True(result.IsFlagged);
        Assert.Equal(FraudDetectionService.TripSameDaySignal, result.FlagReason);
        Assert.Equal(
            "Trip Dire Dawa to Mekelle departs the same day as another open trip of this trucker (Debre Birhan to Addis Ababa).",
            result.Message);
    }

    [Fact]
    public async Task CheckTripCreation_TheReturnLegOfTheSameDay_IsFlaggedNotRefused()
    {
        SetupTruckerTrips(StoredTrip("trip-1"));

        var result = await TripService().CheckTripCreationAsync(Plan(from: "Addis Ababa", to: "Debre Birhan"));

        Assert.True(result.IsAllowed);
        Assert.True(result.IsFlagged);
    }

    [Fact]
    public async Task CheckTripCreation_ASecondTruckOnTheRoute_IsFlaggedNotRefused()
    {
        // Two plates are two trucks: an owner of two vehicles posts both.
        SetupTruckerTrips(StoredTrip("trip-1", plate: "3-AA-12345"));

        var result = await TripService().CheckTripCreationAsync(Plan(plate: "3-AA-67890"));

        Assert.True(result.IsAllowed);
        Assert.True(result.IsFlagged);
    }

    [Theory]
    [InlineData("3-AA-12345", "3 aa 12345")]
    [InlineData("3-AA-12345", null)]
    [InlineData(null, "3-AA-12345")]
    [InlineData(null, null)]
    public async Task CheckTripCreation_TheSamePlateOrAMissingOne_IsOneTruck(string? storedPlate, string? newPlate)
    {
        SetupTruckerTrips(StoredTrip("trip-1", plate: storedPlate));

        var result = await TripService().CheckTripCreationAsync(Plan(plate: newPlate));

        Assert.Equal("FRAUD_DUPLICATE_LISTING", result.ErrorCode);
    }

    [Fact]
    public async Task CheckTripCreation_RouteNamesAreComparedWithoutCaseOrOuterSpaces()
    {
        SetupTruckerTrips(StoredTrip("trip-1"));

        var result = await TripService().CheckTripCreationAsync(Plan(from: "debre birhan ", to: " ADDIS ABABA"));

        Assert.Equal("FRAUD_DUPLICATE_LISTING", result.ErrorCode);
    }

    [Fact]
    public async Task CheckTripCreation_AnotherDayOrNoOtherTrip_IsAllowedUnflagged()
    {
        SetupTruckerTrips(StoredTrip("trip-1", departure: "2026-11-27T06:00:00.000Z"));

        var result = await TripService().CheckTripCreationAsync(Plan());

        Assert.True(result.IsAllowed);
        Assert.False(result.IsFlagged);
    }

    [Fact]
    public async Task CheckTripCreation_AReplayedCreate_IsNotItsOwnDuplicate()
    {
        SetupTruckerTrips(StoredTrip("trip-new"));

        var result = await TripService().CheckTripCreationAsync(Plan());

        Assert.True(result.IsAllowed);
        Assert.False(result.IsFlagged);
    }

    [Fact]
    public async Task CheckTripCreation_ADuplicateWinsOverAnotherRouteOfTheDay()
    {
        SetupTruckerTrips(StoredTrip("trip-other", from: "Dire Dawa", to: "Mekelle"), StoredTrip("trip-1"));

        var result = await TripService().CheckTripCreationAsync(Plan());

        Assert.Equal("FRAUD_DUPLICATE_LISTING", result.ErrorCode);
    }

    [Fact]
    public async Task CheckTripCreation_ReadsTheTruckersOpenAndMatchedTripsFromTheirIndex()
    {
        SetupTruckerTrips();

        await TripService().CheckTripCreationAsync(Plan());

        await _mockDynamoDb.Received(1).QueryAsync(
            Arg.Is<QueryRequest>(r => r.TableName == TripsTable && r.IndexName == "TruckerIdCreatedAtIndex"
                && r.KeyConditionExpression == "truckerId = :tid"
                && r.FilterExpression == "#s IN (:open, :matched) AND attribute_not_exists(deletedAt)"
                && r.ExpressionAttributeNames["#s"] == "status"
                && r.ExpressionAttributeValues[":tid"].S == TruckerId
                && r.ExpressionAttributeValues[":open"].S == "open"
                && r.ExpressionAttributeValues[":matched"].S == "matched"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckTripCreation_WithoutATripsTable_AllowsAndReadsNothing()
    {
        var result = await _service.CheckTripCreationAsync(Plan());

        Assert.True(result.IsAllowed);
        await _mockDynamoDb.DidNotReceive().QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TripSameDay_IsRecordedForReviewAndNeverScores()
    {
        // A trucker who chains legs every day for a month: rows for Fraud Review, a score of 0.
        Assert.Equal(0, FraudDetectionService.SignalWeight(FraudDetectionService.TripSameDaySignal));
        Assert.True(FraudDetectionService.IsSoftSignal(FraudDetectionService.TripSameDaySignal));
        var rows = Enumerable.Range(1, 29)
            .Select(d => (d * 24, FraudDetectionService.TripSameDaySignal, (string?)$"trip-{d}")).ToArray();

        var result = ScoreOf(rows);

        Assert.Equal(0, result.Score);
        Assert.Equal(RiskTier.Low, result.Tier);
    }

    // ===== BUG-15 (persona simulation 2026-11-19): refused probes within one hour =====

    [Fact]
    public void ProbesOfOneHour_CountOncePerType_AndStopBelowCritical()
    {
        // The three abusers made about 20 refused probes each inside one UTC hour. The writer keeps one
        // row per user, type and hour (section 14.3), so the hour's rows are one per type. The farmer:
        // a duplicate listing (20), edits of another farmer's rows (25), a changed money field (25).
        var farmer = ScoreOf((0, "FRAUD_DUPLICATE_LISTING", "crop-1"), (0, "ROLE_FORBIDDEN", "crop-2"),
            (0, "IMMUTABLE_FIELD_CHANGE", "order-1"));
        // The trucker: the same two refusals, a mocked fix (25) and a jump (10) on the trip.
        var trucker = ScoreOf((0, "ROLE_FORBIDDEN", "trip-2"), (0, "IMMUTABLE_FIELD_CHANGE", "trip-1"),
            (0, "GPS_MOCKED", "trip-1"), (0, "GPS_JUMP", "trip-1"));

        Assert.Equal(70, farmer.Score);
        Assert.Equal(RiskTier.High, farmer.Tier);
        Assert.Equal(85, trucker.Score);
        Assert.Equal(RiskTier.High, trucker.Tier);
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
