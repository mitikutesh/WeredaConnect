using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-29 (reputation-api.md 8.3 and section 7): with <c>reputationPenalties</c> on, a buyer's
/// browse and match leave out rows whose stored <c>ownerTier</c> is <c>restricted</c>; the owner's
/// own list, a read by id and an admin still get them; with the flag off nothing is hidden. The
/// owner fields are returned (the public tier) only with <c>reputationPublicTags</c> on.
/// </summary>
public class ReputationBrowseTests
{
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly ITripRepository _trips = Substitute.For<ITripRepository>();
    private readonly TestLambdaContext _ctx = new();

    public ReputationBrowseTests()
    {
        var crops = new List<Dictionary<string, object>>
        {
            Crop("crop-good", "farmer-1", "good"), Crop("crop-low", "farmer-2", "low"),
            Crop("crop-restricted", "farmer-3", "restricted"), Crop("crop-unstamped", "farmer-4", null)
        };
        _crops.QueryCropsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>(), Arg.Any<double?>())
            .Returns(call => (crops.Where(c => call.ArgAt<string?>(0) == null || (string)c["farmerId"] == call.ArgAt<string?>(0)).ToList(),
                (Dictionary<string, AttributeValue>?)null));
        _crops.GetByIdAsync("crop-restricted").Returns(crops[2]);

        var trips = new List<Dictionary<string, object>>
        {
            Trip("trip-good", "trucker-1", "warning"), Trip("trip-restricted", "trucker-3", "restricted")
        };
        _trips.QueryTripsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(),
                Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((trips, (Dictionary<string, AttributeValue>?)null));
        _trips.QueryTripsByTruckerAsync("trucker-3", Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { trips[1] }, (Dictionary<string, AttributeValue>?)null));
        _trips.GetByIdAsync("trip-restricted").Returns(trips[1]);
    }

    // ===== crops =====

    [Fact]
    public async Task Buyer_CropBrowse_LeavesOutRestrictedFarmers_AndKeepsEveryoneElse()
    {
        var ids = await CropIds("buyer-1", "buyer", hide: true);

        ids.Should().BeEquivalentTo("crop-good", "crop-low", "crop-unstamped");
    }

    [Fact]
    public async Task Buyer_CropBrowse_FlagOff_SeesAllCrops()
        => (await CropIds("buyer-1", "buyer", hide: false)).Should().HaveCount(4);

    [Fact]
    public async Task Farmer_OwnList_StillShowsTheirRows_WhileRestricted()
        => (await CropIds("farmer-3", "farmer", hide: true)).Should().BeEquivalentTo("crop-restricted");

    [Fact]
    public async Task Admin_CropBrowse_IsNotFiltered()
        => (await CropIds("admin-1", "admin", hide: true)).Should().HaveCount(4);

    [Fact]
    public async Task Buyer_CropById_StillReadsARestrictedFarmersListing_AsLow()
    {
        var function = new CropFunction(_crops) { HideRestrictedOwners = true, ReputationPublicTags = true };
        var response = await function.FunctionHandler(
            Get("/crops/crop-restricted", "buyer-1", "buyer", new() { { "id", "crop-restricted" } }), _ctx);

        response.StatusCode.Should().Be(200, response.Body);
        var crop = JsonDocument.Parse(response.Body).RootElement.GetProperty("crop");
        crop.GetProperty("ownerTier").GetString().Should().Be("low", "buyers never see restricted");
        crop.GetProperty("ownerEffectiveRating").GetDecimal().Should().Be(4.2m);
        crop.GetProperty("ownerRatingCount").GetInt32().Should().Be(12);
        crop.GetProperty("ownerReliabilityPct").GetInt32().Should().Be(83);
        crop.GetProperty("ownerReputationAt").GetString().Should().Be("2026-11-10T09:00:00.000Z");
    }

    [Fact]
    public async Task CropBrowse_PublicTagsOff_ReturnsNoOwnerField()
    {
        var function = new CropFunction(_crops) { HideRestrictedOwners = false, ReputationPublicTags = false };
        var response = await function.FunctionHandler(Get("/crops", "buyer-1", "buyer"), _ctx);

        response.Body.Should().NotContain("owner");
    }

    [Fact]
    public async Task CropBrowse_PublicTagsOn_WarningReadsAsGood_AndUnstampedRowsCarryNothing()
    {
        var function = new CropFunction(_crops) { ReputationPublicTags = true, HideRestrictedOwners = false };
        var response = await function.FunctionHandler(Get("/crops", "buyer-1", "buyer"), _ctx);

        var crops = JsonDocument.Parse(response.Body).RootElement.GetProperty("crops").EnumerateArray().ToList();
        crops.Single(c => c.GetProperty("cropId").GetString() == "crop-good").GetProperty("ownerTier").GetString().Should().Be("good");
        crops.Single(c => c.GetProperty("cropId").GetString() == "crop-unstamped").TryGetProperty("ownerTier", out _).Should().BeFalse();
    }

    // ===== trips =====

    [Fact]
    public async Task Buyer_TripBrowse_LeavesOutRestrictedTruckers()
    {
        (await TripIds("buyer-1", "buyer", hide: true)).Should().BeEquivalentTo("trip-good");
        (await TripIds("buyer-1", "buyer", hide: false)).Should().BeEquivalentTo("trip-good", "trip-restricted");
    }

    [Fact]
    public async Task Trucker_OwnList_StillShowsTheirTrips_WhileRestricted()
        => (await TripIds("trucker-3", "trucker", hide: true)).Should().BeEquivalentTo("trip-restricted");

    [Fact]
    public async Task Admin_TripBrowse_IsNotFiltered()
        => (await TripIds("admin-1", "admin", hide: true)).Should().HaveCount(2);

    [Fact]
    public async Task Buyer_TripById_StillReadsARestrictedTruckersTrip()
    {
        var function = new TripFunction(_trips) { HideRestrictedOwners = true, ReputationPublicTags = true };
        var response = await function.FunctionHandler(
            Get("/trips/trip-restricted", "buyer-1", "buyer", new() { { "id", "trip-restricted" } }), _ctx);

        response.StatusCode.Should().Be(200, response.Body);
        JsonDocument.Parse(response.Body).RootElement.GetProperty("trip").GetProperty("ownerTier").GetString().Should().Be("low");
    }

    [Fact]
    public async Task TripBrowse_PublicTagsOff_ReturnsNoOwnerField()
    {
        var response = await new TripFunction(_trips) { HideRestrictedOwners = false, ReputationPublicTags = false }
            .FunctionHandler(Get("/trips", "buyer-1", "buyer"), _ctx);

        response.Body.Should().NotContain("ownerTier").And.NotContain("ownerRatingCount");
    }

    // ===== match =====

    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 4)]
    public async Task Buyer_Match_LeavesOutRestrictedFarmers_OnlyWithTheFlag(bool hide, int expected)
    {
        var function = new MatchFunction(_crops) { HideRestrictedOwners = hide, ReputationPublicTags = true };
        var request = Get("/match", "buyer-1", "buyer");
        request.QueryStringParameters = new Dictionary<string, string> { { "cropType", "wheat" } };

        var response = await function.FunctionHandler(request, _ctx);

        response.StatusCode.Should().Be(200, response.Body);
        var matches = JsonDocument.Parse(response.Body).RootElement.GetProperty("matches").EnumerateArray().ToList();
        matches.Should().HaveCount(expected);
        matches.Any(m => m.GetProperty("cropId").GetString() == "crop-restricted").Should().Be(!hide);
        matches.Single(m => m.GetProperty("cropId").GetString() == "crop-low").GetProperty("ownerTier").GetString().Should().Be("low");
    }

    // ===== helpers =====

    private async Task<List<string?>> CropIds(string userId, string role, bool hide)
    {
        var response = await new CropFunction(_crops) { HideRestrictedOwners = hide }.FunctionHandler(Get("/crops", userId, role), _ctx);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("crops").EnumerateArray()
            .Select(c => c.GetProperty("cropId").GetString()).ToList();
    }

    private async Task<List<string?>> TripIds(string userId, string role, bool hide)
    {
        var response = await new TripFunction(_trips) { HideRestrictedOwners = hide }.FunctionHandler(Get("/trips", userId, role), _ctx);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("trips").EnumerateArray()
            .Select(c => c.GetProperty("tripId").GetString()).ToList();
    }

    private static APIGatewayProxyRequest Get(string path, string userId, string role, Dictionary<string, string>? pathParams = null) => new()
    {
        HttpMethod = "GET",
        Path = path,
        PathParameters = pathParams,
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", userId }, { "custom:role", role } }
        }
    };

    private static Dictionary<string, object> Crop(string id, string farmerId, string? tier)
    {
        var row = new Dictionary<string, object>
        {
            { "cropId", id }, { "farmerId", farmerId }, { "farmerName", "Test Farmer" }, { "cropType", "wheat" },
            { "quantity", "10" }, { "pricePerKg", "100" }, { "woreda", "Adama" }, { "locationName", "Adama" },
            { "status", "available" }, { "createdAt", "2026-01-01T00:00:00Z" }, { "updatedAt", "2026-01-01T00:00:00Z" }, { "version", "1" }
        };
        Owner(row, tier);
        return row;
    }

    private static Dictionary<string, object> Trip(string id, string truckerId, string? tier)
    {
        var row = new Dictionary<string, object>
        {
            { "tripId", id }, { "truckerId", truckerId }, { "truckerName", "Tadesse Worku" }, { "truckerPhone", "+251912345678" },
            { "fromWoreda", "Bishoftu" }, { "toWoreda", "Addis Ababa" }, { "departureDate", "2099-02-18T06:00:00Z" },
            { "capacity", "150" }, { "pricePerQuintal", "120" }, { "status", "open" }, { "matchedListings", "[]" },
            { "createdAt", "2026-01-01T00:00:00Z" }, { "updatedAt", "2026-01-01T00:00:00Z" }, { "version", "1" }
        };
        Owner(row, tier);
        return row;
    }

    private static void Owner(Dictionary<string, object> row, string? tier)
    {
        if (tier == null)
        {
            return;
        }

        row["ownerTier"] = tier;
        row["ownerEffectiveRating"] = "4.2";
        row["ownerRatingCount"] = "12";
        row["ownerReliabilityPct"] = "83";
        row["ownerReputationAt"] = "2026-11-10T09:00:00.000Z";
    }
}
