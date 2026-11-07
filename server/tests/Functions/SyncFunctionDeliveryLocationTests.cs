using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// LT-14 (live-trip.md section 10, order-lifecycle.md 2.8.0): the order's own drop-off point,
/// <c>deliveryLatitude</c> and <c>deliveryLongitude</c>, through <c>POST /sync</c>. On create: both or
/// neither, numbers inside Ethiopia's box; a pair of nulls is no point. On update: only the buyer
/// changes it, only before pickup (the order's status and its active delivery, read strongly
/// consistent); an unchanged echo passes from a party who reads it; a trucker not committed gets the
/// same refusal whatever it sends. Every refusal writes nothing.
/// </summary>
public class SyncFunctionDeliveryLocationTests
{
    private const string BuyerId = "buyer-001";
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";
    private const string OrderId = "order-001";
    private const string DeliveryId = "del-001";
    private const string DeliveriesTable = "test-deliveries-table";
    private const string NotBuyer = "ORDER_UNAUTHORIZED: Only the buyer can change the drop-off place.";
    private const string Locked = "ORDER_DELIVERY_LOCATION_LOCKED: The drop-off place can only be changed before pickup.";

    // Adama, the stored point; Mojo, a change.
    private const double StoredLat = 8.5423;
    private const double StoredLng = 39.2687;
    private const double NewLat = 8.5872;
    private const double NewLng = 39.1213;

    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISyncMetadataRepository _syncMeta = Substitute.For<ISyncMetadataRepository>();
    private readonly TestLambdaContext _context = new();
    private readonly SyncFunction _function;

    private Dictionary<string, object>? _created;
    private Dictionary<string, object>? _written;
    private Dictionary<string, object> _order = Order("accepted");
    private string? _deliveryStatus = "pending";

    public SyncFunctionDeliveryLocationTests()
    {
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);

        _crops.GetByIdAsync("listing-001").Returns(new Dictionary<string, object>
        {
            { "cropId", "listing-001" }, { "listingId", "listing-001" }, { "farmerId", FarmerId },
            { "farmerName", "Abebe Kebede" }, { "cropType", "teff" },
            { "quantity", 10.0 }, { "pricePerKg", 45.5 }, { "status", "available" }, { "version", "1" }
        });
        _users.GetUserByIdAsync(BuyerId).Returns(new UserDto
        {
            UserId = BuyerId,
            PhoneNumber = "+251933333333",
            DisplayName = "Selam Traders",
            Role = "buyer",
            Woreda = "Adama",
            IsPhoneVerified = true,
            CreatedAt = "2026-02-16T10:00:00Z",
            UpdatedAt = "2026-02-16T10:00:00Z",
            Version = 1
        });
        _orders.CreateAsync(Arg.Any<string>(), Arg.Do<Dictionary<string, object>>(d => _created = d))
            .Returns(call => AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", call.ArgAt<string>(0) }, { "status", "created" }, { "version", "1" }
            }));

        _orders.GetByIdAsync(OrderId).Returns(_ => _order);
        _orders.GetByIdAsync(OrderId, Arg.Any<bool>()).Returns(_ => _order);
        _orders.UpdateAsync(OrderId, Arg.Do<Dictionary<string, object>>(d => _written = d), Arg.Any<int>())
            .Returns(call =>
            {
                var row = new Dictionary<string, object>(_order);
                foreach (var (key, value) in call.ArgAt<Dictionary<string, object>>(1))
                {
                    row[key] = value is JsonElement json ? json.ToString() : value;
                }

                row["version"] = (call.ArgAt<int>(2) + 1).ToString();
                return AuthResult<Dictionary<string, object>>.Success(row);
            });
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable))
            .Returns(_ => DeliveryResponse(_deliveryStatus));
        // An open trip of trucker-001, leaving in three days: the creates below are trucked orders.
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == "test-trips-table"))
            .Returns(_ => new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new()
                    {
                        { "tripId", new AttributeValue { S = "trip-001" } },
                        { "truckerId", new AttributeValue { S = TruckerId } },
                        { "truckerName", new AttributeValue { S = "Tadesse Worku" } },
                        { "status", new AttributeValue { S = "open" } },
                        { "departureDate", new AttributeValue { S = DateTime.UtcNow.AddDays(3).ToString("o") } },
                        { "capacity", new AttributeValue { N = "100" } },
                        { "pricePerQuintal", new AttributeValue { N = "120" } }
                    }
                }
            });

        _function = new SyncFunction(_syncMeta, _crops, _dynamoDb, "test-trips-table", DeliveriesTable,
            userRepo: _users, orderRepo: _orders);
    }

    // ===== Create =====

    [Fact]
    public async Task Create_StoresThePoint_AsNumbers()
    {
        var result = await CreateAsync(new() { { "deliveryLatitude", StoredLat }, { "deliveryLongitude", StoredLng } });

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        ((JsonElement)_created!["deliveryLatitude"]).GetDouble().Should().Be(StoredLat);
        ((JsonElement)_created["deliveryLongitude"]).GetDouble().Should().Be(StoredLng);
    }

    [Fact]
    public async Task Create_WithoutAPoint_IsUnchanged()
    {
        var result = await CreateAsync(new());

        result.GetProperty("status").GetString().Should().Be("success");
        _created.Should().NotContainKey("deliveryLatitude").And.NotContainKey("deliveryLongitude");
    }

    [Fact]
    public async Task Create_WithAPairOfNulls_StoresNone()
    {
        var result = await CreateAsync(new() { { "deliveryLatitude", null! }, { "deliveryLongitude", null! } });

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        _created.Should().NotContainKey("deliveryLatitude").And.NotContainKey("deliveryLongitude");
    }

    public static IEnumerable<object[]> BadPoints() => new[]
    {
        new object[] { new Dictionary<string, object> { { "deliveryLatitude", 8.5 } }, "deliveryLatitude and deliveryLongitude must be sent together." },
        new object[] { new Dictionary<string, object> { { "deliveryLongitude", 39.2 } }, "deliveryLatitude and deliveryLongitude must be sent together." },
        new object[] { new Dictionary<string, object> { { "deliveryLatitude", 8.5 }, { "deliveryLongitude", null! } }, "deliveryLatitude and deliveryLongitude must be sent together." },
        new object[] { new Dictionary<string, object> { { "deliveryLatitude", 51.5 }, { "deliveryLongitude", 39.2 } }, "deliveryLatitude must be a number between 3 and 15." },
        new object[] { new Dictionary<string, object> { { "deliveryLatitude", 2.99 }, { "deliveryLongitude", 39.2 } }, "deliveryLatitude must be a number between 3 and 15." },
        new object[] { new Dictionary<string, object> { { "deliveryLatitude", 8.5 }, { "deliveryLongitude", -0.12 } }, "deliveryLongitude must be a number between 32.5 and 48.5." },
        new object[] { new Dictionary<string, object> { { "deliveryLatitude", 8.5 }, { "deliveryLongitude", 48.51 } }, "deliveryLongitude must be a number between 32.5 and 48.5." },
        new object[] { new Dictionary<string, object> { { "deliveryLatitude", "8.5" }, { "deliveryLongitude", 39.2 } }, "deliveryLatitude must be a number between 3 and 15." },
        new object[] { new Dictionary<string, object> { { "deliveryLatitude", 0 }, { "deliveryLongitude", 0 } }, "deliveryLatitude must be a number between 3 and 15. deliveryLongitude must be a number between 32.5 and 48.5." },
    };

    [Theory]
    [MemberData(nameof(BadPoints))]
    public async Task Create_WithABadPoint_IsRefused_AndNothingIsWritten(Dictionary<string, object> point, string error)
    {
        var result = await CreateAsync(point);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().Be($"ORDER_VALIDATION: {error}");
        await _orders.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!);
    }

    [Fact]
    public async Task Create_TheEdgesOfTheBox_AreInside()
    {
        var result = await CreateAsync(new() { { "deliveryLatitude", 3 }, { "deliveryLongitude", 48.5 } });

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
    }

    // ===== Update: the edit window =====

    [Theory]
    [InlineData("created")]
    [InlineData("pending_payment")]
    [InlineData("payment_submitted")]
    [InlineData("payment_failed")]
    [InlineData("funded")]
    [InlineData("awaiting_transport")]
    [InlineData("accepted")]
    public async Task TheBuyer_ChangesThePoint_BeforePickup(string status)
    {
        _order = Order(status);

        var result = await UpdateAsync(BuyerId, NewLat, NewLng);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        _written!["deliveryLatitude"].ToString().Should().Be("8.5872");
        _written["deliveryLongitude"].ToString().Should().Be("39.1213");
        var row = result.GetProperty("serverData");
        row.GetProperty("deliveryLatitude").GetDouble().Should().Be(NewLat);
        row.GetProperty("deliveryLongitude").GetDouble().Should().Be(NewLng);
    }

    [Fact]
    public async Task TheBuyer_SetsAPoint_OnAnOrderThatHadNone()
    {
        _order = Order("funded", withPoint: false);

        var result = await UpdateAsync(BuyerId, NewLat, NewLng);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        _written!["deliveryLatitude"].ToString().Should().Be("8.5872");
    }

    [Fact]
    public async Task TheBuyer_ClearsThePoint_WithAPairOfNulls()
    {
        var result = await UpdateAsync(BuyerId, null, null);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        // Written as nulls (stored as NULL, read back as no point).
        _written!.Should().ContainKey("deliveryLatitude").And.ContainKey("deliveryLongitude");
        _written["deliveryLatitude"].Should().BeNull();
        _written["deliveryLongitude"].Should().BeNull();
    }

    [Theory]
    [InlineData("pickup_confirmed")]
    [InlineData("in_transit")]
    [InlineData("delivered")]
    [InlineData("settled")]
    [InlineData("disputed")]
    [InlineData("cancelled")]
    [InlineData("rejected")]
    public async Task AfterPickup_OrOnAnEndedOrder_TheChangeIsRefused(string status)
    {
        _order = Order(status);

        var result = await UpdateAsync(BuyerId, NewLat, NewLng);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().Be(Locked);
        await NothingWritten();
    }

    [Theory]
    [InlineData("pickedUp")]
    [InlineData("inTransit")]
    [InlineData("delivered")]
    [InlineData("disputed")]
    public async Task AnOrderStillAccepted_WhoseDeliveryWasPickedUp_IsLocked(string deliveryStatus)
    {
        _deliveryStatus = deliveryStatus;

        var result = await UpdateAsync(BuyerId, NewLat, NewLng);

        result.GetProperty("message").GetString().Should().Be(Locked);
        await NothingWritten();
        await _dynamoDb.Received(1).QueryAsync(Arg.Is<QueryRequest>(r =>
            r.TableName == DeliveriesTable && r.ConsistentRead == true
            && r.ExpressionAttributeValues[":id"].S == DeliveryId));
    }

    [Fact]
    public async Task ADeliveryThatCannotBeRead_RefusesForNow_WithARetryableError()
    {
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable))
            .Returns<QueryResponse>(_ => throw new AmazonDynamoDBException("throttled"));

        var result = await UpdateAsync(BuyerId, NewLat, NewLng);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().NotStartWith("ORDER_");
        await NothingWritten();
    }

    [Fact]
    public async Task AChangeOutsideTheBox_IsRefused()
    {
        var result = await UpdateAsync(BuyerId, 51.5, -0.12);

        result.GetProperty("message").GetString().Should().Be(
            "ORDER_VALIDATION: deliveryLatitude must be a number between 3 and 15. deliveryLongitude must be a number between 32.5 and 48.5.");
        await NothingWritten();
    }

    [Fact]
    public async Task AChangeWithOnlyOneField_IsRefused()
    {
        var result = await SendAsync(BuyerId, "buyer", new Dictionary<string, object>
        {
            { "deliveryLatitude", NewLat }, { "version", 2 }
        });

        result.GetProperty("message").GetString().Should()
            .Be("ORDER_VALIDATION: deliveryLatitude and deliveryLongitude must be sent together.");
        await NothingWritten();
    }

    [Fact]
    public async Task ANoteAndAPoint_ChangeTogether()
    {
        var result = await UpdateAsync(BuyerId, NewLat, NewLng, extra: new() { { "dropoffNote", "Mojo, by the dry port" } });

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        _written!["dropoffNote"].Should().Be("Mojo, by the dry port");
        _written["deliveryLatitude"].ToString().Should().Be("8.5872");
    }

    // ===== Update: who =====

    [Theory]
    [InlineData(FarmerId, "farmer")]
    [InlineData(TruckerId, "trucker")]
    public async Task TheFarmerOrTheCommittedTrucker_CannotChangeIt(string caller, string role)
    {
        var result = await UpdateAsync(caller, NewLat, NewLng, role: role);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().Be(NotBuyer);
        await NothingWritten();
    }

    [Fact]
    public async Task TheFarmer_CannotSlipAChangeIntoAnotherUpdate()
    {
        _order = Order("funded");

        var result = await UpdateAsync(FarmerId, NewLat, NewLng, role: "farmer", extra: new() { { "status", "rejected" } });

        result.GetProperty("message").GetString().Should().Be(NotBuyer);
        await NothingWritten();
    }

    [Fact]
    public async Task AnUnchangedEcho_PassesFromAPartyWhoReadsIt_AndIsNotWritten()
    {
        // The committed trucker's in-transit catch-up resends the row's point: no change, no refusal.
        _order = Order("pickup_confirmed");
        _deliveryStatus = "inTransit";

        var result = await UpdateAsync(TruckerId, StoredLat, StoredLng, role: "trucker", extra: new() { { "status", "in_transit" } });

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        _written.Should().NotContainKey("deliveryLatitude").And.NotContainKey("deliveryLongitude");
        _written!["status"].ToString().Should().Be("in_transit");
    }

    [Fact]
    public async Task AnUnchangedEcho_AfterPickup_IsNotRefused()
    {
        _order = Order("in_transit");

        var result = await UpdateAsync(BuyerId, StoredLat, StoredLng);

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        _written.Should().NotContainKey("deliveryLatitude");
    }

    [Fact]
    public async Task AnAskedTrucker_ReadsThePoint_SoAnEchoPasses_AndAChangeIsRefused()
    {
        // The trucker answers the transport request knowing where the goods go: the point is theirs
        // to read while the money is held, committed or not (the note waits for the commitment).
        _order = Order("funded");
        _order["truckerResponse"] = "pending";

        // The echo passes the point's rules and is dropped; the rest of the payload then meets the
        // trucker's own rule (no status to set).
        var echo = await UpdateAsync(TruckerId, StoredLat, StoredLng, role: "trucker");
        echo.GetProperty("message").GetString().Should()
            .Be("ORDER_UNAUTHORIZED: The trucker may only set pickup_confirmed, in_transit or delivered.", echo.ToString());

        _written = null;
        var change = await UpdateAsync(TruckerId, NewLat, NewLng, role: "trucker");
        change.GetProperty("status").GetString().Should().Be("error");
        change.GetProperty("message").GetString().Should().Be(NotBuyer);
        await NothingWritten();
    }

    [Theory]
    [InlineData("created", StoredLat, StoredLng)]
    [InlineData("created", NewLat, NewLng)]
    [InlineData("pending_payment", null, null)]
    [InlineData("cancelled", 51.5, -0.12)]
    public async Task ATruckerOfAnOrderWhoseMoneyIsNotHeld_GetsTheSameRefusal_WhateverThePoint(
        string status, double? lat, double? lng)
    {
        // No equality oracle: the same answer whether the probe matches the stored point or not.
        _order = Order(status);

        var result = await UpdateAsync(TruckerId, lat, lng, role: "trucker");

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().Be(NotBuyer);
        (!result.TryGetProperty("serverData", out var row) || row.ValueKind == JsonValueKind.Null)
            .Should().BeTrue(result.ToString());
        await NothingWritten();
    }

    // ===== A self-pickup order has no drop-off =====

    [Fact]
    public async Task Create_WithoutATrip_IgnoresThePoint_EvenABadOne()
    {
        var data = new Dictionary<string, object>
        {
            { "buyerId", BuyerId }, { "farmerId", FarmerId }, { "listingId", "listing-001" },
            { "buyerName", "Selam Traders" }, { "farmerName", "Abebe Kebede" },
            { "cropType", "teff" }, { "quantity", 50 }, { "pricePerUnit", 45.5 },
            { "deliveryLatitude", 51.5 }, { "deliveryLongitude", -0.12 }
        };

        var result = await RunAsync(BuyerId, "buyer", new SyncOperationRequest
        {
            OperationId = "op-create-sp",
            IdempotencyKey = $"{BuyerId}:order:{OrderId}:create:777",
            EntityType = "order",
            EntityId = OrderId,
            Action = "create",
            Data = data
        });

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        _created.Should().NotContainKey("deliveryLatitude").And.NotContainKey("deliveryLongitude");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_OnASelfPickupOrder_IgnoresThePoint(bool stampedSelfPickup)
    {
        _order = Order("funded", withPoint: false);
        if (stampedSelfPickup)
        {
            _order["fulfillment"] = "self_pickup";
        }
        else
        {
            _order.Remove("tripId");
            _order.Remove("truckerId");
            _order.Remove("truckerName");
        }

        var result = await UpdateAsync(BuyerId, NewLat, NewLng, extra: new() { { "dropoffNote", "Kality" } });

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        _written.Should().NotContainKey("deliveryLatitude").And.NotContainKey("deliveryLongitude");
        _written!["dropoffNote"].Should().Be("Kality");
    }

    // ===== The row a result carries back =====

    [Fact]
    public async Task TheRowSentBackToATruckerNotCommitted_KeepsThePoint_ButNotTheNote()
    {
        // The trucker dropped out of an order whose money is still held: the point is still theirs to
        // read (its own rule), the note is not (LT-12).
        _order = Order("accepted");
        _order["truckerResponse"] = "cancelled";
        _order["dropoffNote"] = "Merkato, near Anwar Mosque";
        _deliveryStatus = "pickedUp";
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "Version conflict."));

        var result = await SendAsync(TruckerId, "trucker", new Dictionary<string, object>
        {
            { "status", "pickup_confirmed" }, { "version", 2 }
        });

        result.GetProperty("status").GetString().Should().Be("conflict", result.ToString());
        var row = result.GetProperty("serverData");
        row.GetProperty("status").GetString().Should().Be("accepted");
        row.GetProperty("deliveryLatitude").GetDouble().Should().Be(StoredLat);
        row.GetProperty("deliveryLongitude").GetDouble().Should().Be(StoredLng);
        row.TryGetProperty("dropoffNote", out _).Should().BeFalse();
    }

    [Fact]
    public async Task TheRowSentBackToTheBuyer_CarriesThePoint_AsNumbers()
    {
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "Version conflict."));

        // A note change that loses the version race gets the stored row back.
        var result = await SendAsync(BuyerId, "buyer", new Dictionary<string, object>
        {
            { "dropoffNote", "Kality" }, { "version", 2 }
        });

        result.GetProperty("status").GetString().Should().Be("conflict", result.ToString());
        var row = result.GetProperty("serverData");
        row.GetProperty("deliveryLatitude").ValueKind.Should().Be(JsonValueKind.Number, result.ToString());
        row.GetProperty("deliveryLatitude").GetDouble().Should().Be(StoredLat);
    }

    // ===== helpers =====

    private async Task NothingWritten()
    {
        _written.Should().BeNull();
        await _orders.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    private async Task<JsonElement> CreateAsync(Dictionary<string, object> point)
    {
        var data = new Dictionary<string, object>
        {
            { "buyerId", BuyerId }, { "farmerId", FarmerId }, { "listingId", "listing-001" },
            { "buyerName", "Selam Traders" }, { "farmerName", "Abebe Kebede" },
            { "cropType", "teff" }, { "quantity", 50 }, { "pricePerUnit", 45.5 },
            { "tripId", "trip-001" }, { "truckerId", TruckerId }
        };
        foreach (var (key, value) in point)
        {
            data[key] = value;
        }

        return await RunAsync(BuyerId, "buyer", new SyncOperationRequest
        {
            OperationId = "op-create",
            IdempotencyKey = $"{BuyerId}:order:{OrderId}:create:12345",
            EntityType = "order",
            EntityId = OrderId,
            Action = "create",
            Data = data
        });
    }

    private Task<JsonElement> UpdateAsync(
        string caller, double? latitude, double? longitude, string role = "buyer", Dictionary<string, object>? extra = null)
    {
        var data = new Dictionary<string, object>
        {
            { "deliveryLatitude", latitude! }, { "deliveryLongitude", longitude! }, { "version", 2 }
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, object>())
        {
            data[key] = value;
        }

        return SendAsync(caller, role, data);
    }

    private Task<JsonElement> SendAsync(string caller, string role, Dictionary<string, object> data)
        => RunAsync(caller, role, new SyncOperationRequest
        {
            OperationId = "op-update",
            IdempotencyKey = $"{caller}:order:{OrderId}:update:12345",
            EntityType = "order",
            EntityId = OrderId,
            Action = "update",
            Data = data
        });

    private async Task<JsonElement> RunAsync(string caller, string role, SyncOperationRequest operation)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-lt-14",
                ClientId = "device-001",
                Operations = new List<SyncOperationRequest> { operation }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", caller }, { "custom:role", role } }
            }
        };

        var response = await _function.FunctionHandler(request, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }

    private static QueryResponse DeliveryResponse(string? status) => new()
    {
        Items = status == null
            ? new List<Dictionary<string, AttributeValue>>()
            : new()
            {
                new()
                {
                    { "deliveryId", new AttributeValue { S = DeliveryId } },
                    { "tripId", new AttributeValue { S = "trip-001" } },
                    { "orderId", new AttributeValue { S = OrderId } },
                    { "farmerId", new AttributeValue { S = FarmerId } },
                    { "truckerId", new AttributeValue { S = TruckerId } },
                    { "buyerId", new AttributeValue { S = BuyerId } },
                    { "status", new AttributeValue { S = status } },
                    { "version", new AttributeValue { N = "3" } }
                }
            }
    };

    /// <summary>A trip order whose active delivery is del-001, with its own point at Adama (stored as text).</summary>
    private static Dictionary<string, object> Order(string status, bool withPoint = true)
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", OrderId }, { "listingId", "listing-001" }, { "createdAt", "2026-10-30T08:00:00Z" },
            { "buyerId", BuyerId }, { "farmerId", FarmerId },
            { "buyerName", "Selam Traders" }, { "farmerName", "Abebe Kebede" },
            { "cropType", "teff" }, { "quantity", 50.0 }, { "pricePerUnit", 45.5 },
            { "tripId", "trip-001" }, { "truckerId", TruckerId }, { "truckerName", "Tadesse Worku" },
            { "transportCost", 60.0 }, { "cropCost", 2275.0 }, { "commissionRate", 0.05 },
            { "commissionAmount", 113.75 }, { "orderTotal", 2448.75 },
            { "activeDeliveryId", DeliveryId },
            { "status", status }, { "version", "2" }
        };
        if (withPoint)
        {
            order["deliveryLatitude"] = "8.5423";
            order["deliveryLongitude"] = "39.2687";
        }

        return order;
    }
}
