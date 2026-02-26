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
    private const string FarmerId = "farmer-123";
    private const string UserId = "user-456";

    public FraudDetectionServiceTests()
    {
        _mockDynamoDb = Substitute.For<IAmazonDynamoDB>();
        _mockRateLimitRepo = Substitute.For<IRateLimitRepository>();
        _service = new FraudDetectionService(_mockDynamoDb, CropsTable, _mockRateLimitRepo);

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
        _mockRateLimitRepo.GetAsync(Arg.Any<string>())
            .Returns(new RateLimitEntry { Pk = "test", Count = 5, Ttl = 0 });

        var result = await _service.CheckPaymentVelocityAsync(UserId);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task CheckPaymentVelocity_AtLimit_ReturnsRejected()
    {
        _mockRateLimitRepo.GetAsync(Arg.Any<string>())
            .Returns(new RateLimitEntry { Pk = "test", Count = 10, Ttl = 0 });

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
