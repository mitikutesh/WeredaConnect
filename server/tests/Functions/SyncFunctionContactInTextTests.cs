using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-49: crop and trip text every buyer reads is refused with CROP_CONTACT_IN_TEXT or
/// TRIP_CONTACT_IN_TEXT when it carries a phone number, on create and on update, and nothing
/// is written. ESC-48 keeps contact closed until the money is held in escrow.
/// </summary>
public class SyncFunctionContactInTextTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private const string ContactTail = " Contact opens after payment is held in escrow.";

    private readonly ISyncMetadataRepository _meta = Substitute.For<ISyncMetadataRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly SyncFunction _fn;
    private readonly TestLambdaContext _ctx = new();

    public SyncFunctionContactInTextTests()
    {
        _meta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);

        // The owner's profile stamps the name and phone on create (SIM-40).
        _users.GetUserByIdAsync(Arg.Any<string>()).Returns(new UserDto
        {
            UserId = "owner",
            PhoneNumber = "+251933333333",
            DisplayName = "Abebe Kebede",
            Role = "farmer",
            Woreda = "Jimma",
            IsPhoneVerified = true,
            CreatedAt = "2026-02-16T10:00:00Z",
            UpdatedAt = "2026-02-16T10:00:00Z",
            Version = 1,
        });

        _crops.CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>())
            .Returns(ci => AuthResult<Dictionary<string, object>>.Success(
                new Dictionary<string, object> { { "cropId", ci.ArgAt<string>(0) }, { "version", "1" } }));
        _crops.UpdateAsync("crop-001", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(
                new Dictionary<string, object> { { "cropId", "crop-001" }, { "version", "2" } }));

        _fn = new SyncFunction(_meta, _crops, _ddb, "trips", "deliveries", userRepo: _users);
    }

    private static APIGatewayProxyRequest Req(SyncOperationRequest op, string userId, string role)
    {
        var body = new SyncRequest
        {
            SyncBatchId = "batch-1",
            ClientId = "device-1",
            Operations = new List<SyncOperationRequest> { op },
        };
        return new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(body, Json),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext
                {
                    { "userId", userId },
                    { "custom:role", role },
                },
            },
        };
    }

    private static SyncOperationRequest Op(
        string entityType, string entityId, string action, Dictionary<string, object> data) => new()
        {
            OperationId = "op-1",
            IdempotencyKey = $"user:{entityType}:{entityId}:{action}:12345",
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            Data = data,
        };

    private static Dictionary<string, object> CropCreateData(params (string Key, object Value)[] extra)
    {
        var data = new Dictionary<string, object>
        {
            { "farmerId", "farmer-001" },
            { "cropType", "teff" },
            { "quantity", 50 },
            { "pricePerKg", 45.5 },
            { "woreda", "Jimma" },
            { "locationName", "Jimma" },
            { "description", "White teff" },
            { "photoIds", CropPhotoFixtures.ValidPhotoIds() },
        };
        foreach (var (key, value) in extra)
        {
            data[key] = value;
        }

        return data;
    }

    private static Dictionary<string, object> TripCreateData(params (string Key, object Value)[] extra)
    {
        var data = new Dictionary<string, object>
        {
            { "truckerId", "trucker-001" },
            { "truckerName", "Tadesse Worku" },
            { "truckerPhone", "+251912345678" },
            { "fromWoreda", "Bishoftu" },
            { "toWoreda", "Addis Ababa" },
            { "departureDate", DateTime.UtcNow.AddDays(3).ToString("o") },
            { "capacity", 150.0 },
            { "pricePerQuintal", 120.0 },
        };
        foreach (var (key, value) in extra)
        {
            data[key] = value;
        }

        return data;
    }

    private void StoredCrop(string? description = null)
    {
        var stored = new Dictionary<string, object>
        {
            { "cropId", "crop-001" },
            { "farmerId", "farmer-001" },
            { "status", "available" },
            { "version", "1" },
        };
        if (description != null)
        {
            stored["description"] = description;
        }

        _crops.GetByIdAsync("crop-001").Returns(stored);
    }

    private void StoredTrip(string? vehicleDescription = null)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "tripId", new AttributeValue { S = "trip-001" } },
            { "truckerId", new AttributeValue { S = "trucker-001" } },
            { "fromWoreda", new AttributeValue { S = "Bishoftu" } },
            { "toWoreda", new AttributeValue { S = "Addis Ababa" } },
            { "createdAt", new AttributeValue { S = "2026-02-16T10:00:00Z" } },
            { "status", new AttributeValue { S = "open" } },
            { "version", new AttributeValue { N = "1" } },
        };
        if (vehicleDescription != null)
        {
            item["vehicleDescription"] = new AttributeValue { S = vehicleDescription };
        }

        _ddb.QueryAsync(Arg.Any<QueryRequest>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { item } });
        _ddb.UpdateItemAsync(Arg.Any<UpdateItemRequest>())
            .Returns(new UpdateItemResponse
            {
                Attributes = new Dictionary<string, AttributeValue>(item)
                {
                    ["version"] = new AttributeValue { N = "2" },
                },
            });
    }

    // ===== Crop create =====

    [Theory]
    [InlineData("description", "Call 0911234567")]
    [InlineData("description", "ለበለጠ መረጃ 0911 23 45 67 ይደውሉ")]
    [InlineData("description", "Bilbilaa +251 911 234 567 irratti bilbilaa")]
    [InlineData("locationName", "Jimma, 0712-345-678")]
    public async Task CropCreate_PhoneNumberInText_IsRefusedAndNotWritten(string field, string text)
    {
        var op = Op("crop_listing", "crop-001", "create", CropCreateData((field, text)));

        var response = await _fn.FunctionHandler(Req(op, "farmer-001", "farmer"), _ctx);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains($"CROP_CONTACT_IN_TEXT: Remove the phone number from {field}.{ContactTail}", response.Body);
        await _crops.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
        await _meta.DidNotReceive().StoreIdempotencyKeyAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task CropCreate_WoredaFromTheProfile_IsNotScreened()
    {
        // Clients send the registration profile's woreda; a refusal here would lock the farmer
        // out with nothing to fix in the form. Profile text belongs to registration and profile
        // update (a follow-up).
        var op = Op("crop_listing", "crop-001", "create", CropCreateData(("woreda", "Jimma 0911234567")));

        var response = await _fn.FunctionHandler(Req(op, "farmer-001", "farmer"), _ctx);

        Assert.Contains("\"status\":\"success\"", response.Body);
        await _crops.Received(1).CreateAsync("crop-001", Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task CropCreate_PhoneNumberInTwoFields_NamesBothInFieldOrder()
    {
        var op = Op("crop_listing", "crop-001", "create", CropCreateData(
            ("locationName", "Adama 0922334455"), ("description", "Call 0911234567")));

        var response = await _fn.FunctionHandler(Req(op, "farmer-001", "farmer"), _ctx);

        Assert.Contains("CROP_CONTACT_IN_TEXT: Remove the phone number from description, locationName.", response.Body);
    }

    [Theory]
    [InlineData("Harvested 2026-10-23, 500 quintals at 1,200 ETB per quintal")]
    [InlineData("50 ኩንታል ነጭ ጤፍ፣ በኪሎ 120 ብር")]
    [InlineData("Xaafii adii kuntaala 50, kiilootti qarshii 120")]
    public async Task CropCreate_PricesQuantitiesAndDates_AreCreated(string description)
    {
        var op = Op("crop_listing", "crop-001", "create", CropCreateData(("description", description)));

        var response = await _fn.FunctionHandler(Req(op, "farmer-001", "farmer"), _ctx);

        Assert.Contains("\"status\":\"success\"", response.Body);
        Assert.DoesNotContain("CONTACT_IN_TEXT", response.Body);
        await _crops.Received(1).CreateAsync("crop-001", Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task CropCreate_TooLongDescription_IsAFieldErrorFirst()
    {
        // Field validation runs first: one payload always gets the same answer.
        var op = Op("crop_listing", "crop-001", "create", CropCreateData(
            ("description", "Call 0911234567 " + new string('x', 500))));

        var response = await _fn.FunctionHandler(Req(op, "farmer-001", "farmer"), _ctx);

        Assert.Contains("CROP_VALIDATION: description must not exceed 500 characters.", response.Body);
    }

    // ===== Crop update =====

    [Fact]
    public async Task CropUpdate_ChangedTextWithPhoneNumber_IsRefusedAndNotWritten()
    {
        StoredCrop(description: "White teff");
        var op = Op("crop_listing", "crop-001", "update", new Dictionary<string, object>
        {
            { "description", "White teff, call 0911 234 567" }, { "version", 1 },
        });

        var response = await _fn.FunctionHandler(Req(op, "farmer-001", "farmer"), _ctx);

        Assert.Contains($"CROP_CONTACT_IN_TEXT: Remove the phone number from description.{ContactTail}", response.Body);
        await _crops.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task CropUpdate_UnchangedEchoOfAStoredNumber_IsAccepted()
    {
        // A listing stored before FUP-49 can still change status when a client echoes its text.
        StoredCrop(description: "Call 0911234567");
        var op = Op("crop_listing", "crop-001", "update", new Dictionary<string, object>
        {
            { "description", "Call 0911234567" }, { "status", "reserved" }, { "version", 1 },
        });

        var response = await _fn.FunctionHandler(Req(op, "farmer-001", "farmer"), _ctx);

        Assert.Contains("\"status\":\"success\"", response.Body);
        await _crops.Received(1).UpdateAsync("crop-001", Arg.Any<Dictionary<string, object>>(), 1);
    }

    // ===== Trip create =====

    [Theory]
    [InlineData("vehicleDescription", "Isuzu FSR, call 0911234567")]
    [InlineData("vehicleDescription", "አይሱዙ ስልክ 0911 23 45 67")]
    [InlineData("vehicleDescription", "Konkolaataa Isuzu, bilbilaa 0712 34 56 78")]
    [InlineData("vehiclePlate", "0911234567")]
    [InlineData("fromWoreda", "Bishoftu +251 911 234 567")]
    [InlineData("toWoreda", "Addis 00251911234567")]
    public async Task TripCreate_PhoneNumberInText_IsRefusedAndNotWritten(string field, string text)
    {
        var op = Op("trip", "trip-001", "create", TripCreateData((field, text)));

        var response = await _fn.FunctionHandler(Req(op, "trucker-001", "trucker"), _ctx);

        Assert.Contains($"TRIP_CONTACT_IN_TEXT: Remove the phone number from {field}.{ContactTail}", response.Body);
        await _ddb.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>());
    }

    [Fact]
    public async Task TripCreate_VehicleAndPlateWithoutANumber_AreCreated()
    {
        var op = Op("trip", "trip-001", "create", TripCreateData(
            ("vehicleDescription", "Isuzu FSR 2019 model, 70 quintal capacity"),
            ("vehiclePlate", "3-AA-12345")));

        var response = await _fn.FunctionHandler(Req(op, "trucker-001", "trucker"), _ctx);

        Assert.Contains("\"status\":\"success\"", response.Body);
        await _ddb.Received(1).PutItemAsync(Arg.Any<PutItemRequest>());
    }

    // ===== Trip update =====

    [Fact]
    public async Task TripUpdate_ChangedVehicleDescriptionWithPhoneNumber_IsRefusedAndNotWritten()
    {
        StoredTrip(vehicleDescription: "Isuzu FSR");
        var op = Op("trip", "trip-001", "update", new Dictionary<string, object>
        {
            { "vehicleDescription", "Isuzu FSR 0911-234-567" }, { "version", 1 },
        });

        var response = await _fn.FunctionHandler(Req(op, "trucker-001", "trucker"), _ctx);

        Assert.Contains($"TRIP_CONTACT_IN_TEXT: Remove the phone number from vehicleDescription.{ContactTail}", response.Body);
        await _ddb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>());
    }

    [Fact]
    public async Task TripUpdate_UnchangedEchoOfAStoredNumber_IsAccepted()
    {
        StoredTrip(vehicleDescription: "Isuzu FSR 0911234567");
        var op = Op("trip", "trip-001", "update", new Dictionary<string, object>
        {
            { "vehicleDescription", "Isuzu FSR 0911234567" }, { "status", "matched" }, { "version", 1 },
        });

        var response = await _fn.FunctionHandler(Req(op, "trucker-001", "trucker"), _ctx);

        Assert.DoesNotContain("CONTACT_IN_TEXT", response.Body);
        await _ddb.Received(1).UpdateItemAsync(Arg.Any<UpdateItemRequest>());
    }
}
