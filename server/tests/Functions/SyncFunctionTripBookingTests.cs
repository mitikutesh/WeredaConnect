using System.Globalization;
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
/// ESC-17 through <c>/sync</c>: a booked trip is frozen, <c>matched</c> is the server's word for
/// full, a trip with bookings may start, cancelling a trip moves its paid orders (and refuses when
/// crops are already on the road), and an order create respects the trip's room and lead time.
/// </summary>
public class SyncFunctionTripBookingTests
{
    private const string BuyerId = "buyer-001";
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";
    private const string TripId = "trip-001";
    private const string ListingId = "listing-001";
    private const string TripsTable = "test-trips-table";
    private const string DeliveriesTable = "test-deliveries-table";

    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISyncMetadataRepository _syncMeta = Substitute.For<ISyncMetadataRepository>();
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly ITransactionAuditRepository _txAudit = Substitute.For<ITransactionAuditRepository>();
    private readonly ITripRepository _trips = Substitute.For<ITripRepository>();
    private readonly TestLambdaContext _context = new();

    private Dictionary<string, AttributeValue> _trip;
    private UpdateItemRequest? _tripWrite;
    private readonly List<Dictionary<string, object>> _ordersOnTrip = new();
    private readonly List<(string OrderId, string From, string To, Dictionary<string, object>? Fields)> _advanced = new();
    private readonly List<(string OrderId, Dictionary<string, object> Fields)> _stamped = new();
    private string _deliveryStatus = "pending";

    public SyncFunctionTripBookingTests()
    {
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _trip = Trip(DateTime.UtcNow.AddDays(5), booked: 20);

        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == TripsTable))
            .Returns(_ => new QueryResponse { Items = new() { _trip } });
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => _tripWrite = r))
            .Returns(_ => new UpdateItemResponse { Attributes = new Dictionary<string, AttributeValue>(_trip) });
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable))
            .Returns(_ => new QueryResponse
            {
                Items = new()
                {
                    new()
                    {
                        { "deliveryId", new AttributeValue { S = "del-001" } },
                        { "tripId", new AttributeValue { S = TripId } },
                        { "status", new AttributeValue { S = _deliveryStatus } }
                    }
                }
            });

        _orders.QueryByTripAsync(TripId).Returns(_ => _ordersOnTrip.ToList());
        _orders.TryAdvanceStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>())
            .Returns(call =>
            {
                _advanced.Add((call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<string>(2), call.ArgAt<Dictionary<string, object>?>(3)));
                return new Dictionary<string, object> { { "orderId", call.ArgAt<string>(0) }, { "status", call.ArgAt<string>(2) } };
            });
        _orders.TryStampAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<IReadOnlyDictionary<string, string>>())
            .Returns(call =>
            {
                _stamped.Add((call.ArgAt<string>(0), call.ArgAt<Dictionary<string, object>>(1)));
                return true;
            });
        _deliveries.TryCancelPendingAsync(Arg.Any<string>()).Returns(_ => _deliveryStatus == "pending");
        _crops.GetByIdAsync(ListingId).Returns(new Dictionary<string, object>
        {
            { "cropId", ListingId }, { "listingId", ListingId }, { "farmerId", FarmerId },
            { "farmerName", "Abebe Kebede" }, { "cropType", "teff" },
            { "quantity", 10.0 }, { "pricePerKg", 45.5 }, { "status", "available" }, { "version", "1" }
        });
        _orders.CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>())
            .Returns(call => AuthResult<Dictionary<string, object>>.Success(new Dictionary<string, object>
            {
                { "orderId", call.ArgAt<string>(0) }, { "status", "created" }, { "version", "1" }
            }));
        // An order create stamps the buyer's name from the profile.
        _users.GetUserByIdAsync(BuyerId).Returns(new UserDto
        {
            UserId = BuyerId,
            PhoneNumber = "+251933333333",
            DisplayName = "Meron Alemu",
            Role = "buyer",
            Woreda = "Dire Dawa",
            IsPhoneVerified = true,
            CreatedAt = "2026-02-16T10:00:00Z",
            UpdatedAt = "2026-02-16T10:00:00Z",
            Version = 1
        });
    }

    private SyncFunction MakeFunction(bool enabled = true) =>
        new(_syncMeta, _crops, _dynamoDb, TripsTable, DeliveriesTable,
            userRepo: _users, orderRepo: _orders, transactionAuditRepo: _txAudit, deliveryRepo: _deliveries, tripRepo: _trips)
        {
            CommitmentsEnabled = enabled
        };

    // ===== a booked trip is frozen =====

    [Theory]
    [InlineData("departureDate")]
    [InlineData("pricePerQuintal")]
    [InlineData("fromWoreda")]
    [InlineData("toWoreda")]
    public async Task BookedTrip_ChangingWhatThePartiesAgreedTo_IsRefused(string field)
    {
        object value = field switch
        {
            "departureDate" => DateTime.UtcNow.AddDays(9).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            "pricePerQuintal" => 150.0,
            _ => "Somewhere Else"
        };

        var result = await UpdateTripAsync(new Dictionary<string, object> { { field, value }, { "version", 3 } });

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("TRIP_HAS_BOOKINGS").And.Contain(field);
        _tripWrite.Should().BeNull();
    }

    [Fact]
    public async Task BookedTrip_UnchangedEcho_IsAccepted()
    {
        var result = await UpdateTripAsync(new Dictionary<string, object>
        {
            { "pricePerQuintal", 120.0 }, { "fromWoreda", "Bishoftu" }, { "vehiclePlate", "AA-12345" }, { "version", 3 }
        });

        result.GetProperty("status").GetString().Should().Be("success", result.GetRawText());
    }

    [Fact]
    public async Task BookedTrip_CapacityBelowTheBookings_IsRefused()
    {
        var result = await UpdateTripAsync(new Dictionary<string, object> { { "capacity", 10.0 }, { "version", 3 } });

        result.GetProperty("message").GetString().Should().StartWith("TRIP_HAS_BOOKINGS").And.Contain("capacity");
        _tripWrite.Should().BeNull();
    }

    [Fact]
    public async Task CapacityChange_LetsDynamoRecomputeTheRoom()
    {
        var result = await UpdateTripAsync(new Dictionary<string, object> { { "capacity", 150.0 }, { "version", 3 } });

        result.GetProperty("status").GetString().Should().Be("success");
        _tripWrite.Should().NotBeNull();
        _tripWrite!.UpdateExpression.Should().Contain("#remaining = :newCapacity - if_not_exists(#booked, :zeroBooked)");
        _tripWrite.ExpressionAttributeValues[":newCapacity"].N.Should().Be("150");
        _tripWrite.ExpressionAttributeNames["#booked"].Should().Be("bookedQuintals");
    }

    [Fact]
    public async Task ClientCannotSetMatched()
    {
        var result = await UpdateTripAsync(new Dictionary<string, object> { { "status", "matched" }, { "version", 3 } });

        result.GetProperty("message").GetString().Should().Be("TRIP_VALIDATION: 'matched' is set by the server when the trip is fully booked.");
        _tripWrite.Should().BeNull();
    }

    [Fact]
    public async Task OpenTripWithBookings_CanStart()
    {
        var result = await UpdateTripAsync(new Dictionary<string, object> { { "status", "inTransit" }, { "version", 3 } });

        result.GetProperty("status").GetString().Should().Be("success");
        _tripWrite!.ExpressionAttributeValues.Values.Should().Contain(v => v.S == "inTransit");
    }

    [Fact]
    public async Task OpenTripWithoutBookings_CannotStart()
    {
        _trip = Trip(DateTime.UtcNow.AddDays(5), booked: 0);

        var result = await UpdateTripAsync(new Dictionary<string, object> { { "status", "inTransit" }, { "version", 3 } });

        result.GetProperty("message").GetString().Should().StartWith("TRIP_INVALID_TRANSITION");
    }

    [Fact]
    public async Task FlagOff_BookedTripRulesDoNotApply()
    {
        var result = await UpdateTripAsync(new Dictionary<string, object> { { "pricePerQuintal", 150.0 }, { "version", 3 } }, enabled: false);

        result.GetProperty("status").GetString().Should().Be("success");
    }

    // ===== cancelling a trip =====

    [Theory]
    [InlineData("pickup_confirmed")]
    [InlineData("in_transit")]
    public async Task TripCancel_WithCropsOnTheRoad_IsRefused(string orderStatus)
    {
        _ordersOnTrip.Add(Order("order-1", orderStatus, farmer: "accepted", trucker: "accepted"));

        var update = await UpdateTripAsync(new Dictionary<string, object> { { "status", "cancelled" }, { "version", 3 } });
        var delete = await DeleteTripAsync();

        update.GetProperty("message").GetString().Should().StartWith("TRIP_HAS_BOOKINGS");
        delete.GetProperty("message").GetString().Should().StartWith("TRIP_HAS_BOOKINGS");
        _tripWrite.Should().BeNull();
    }

    [Fact]
    public async Task TripCancel_MovesEveryPaidOrder_AccordingToWhereItStood()
    {
        _ordersOnTrip.Add(Order("order-farmer-deciding", "funded", farmer: "pending", trucker: "not_asked"));
        _ordersOnTrip.Add(Order("order-trucker-asked", "funded", farmer: "accepted", trucker: "pending"));
        _ordersOnTrip.Add(Order("order-agreed", "accepted", farmer: "accepted", trucker: "accepted", activeDeliveryId: "del-001"));
        _ordersOnTrip.Add(Order("order-unpaid", "created"));
        _ordersOnTrip.Add(Order("order-done", "cancelled", trucker: "declined"));

        var result = await UpdateTripAsync(new Dictionary<string, object> { { "status", "cancelled" }, { "version", 3 } });

        result.GetProperty("status").GetString().Should().Be("success");
        // The farmer still decides: only the trucker's answer is recorded.
        _stamped.Should().ContainSingle(s => s.OrderId == "order-farmer-deciding"
            && (string)s.Fields[OrderCommitment.TruckerResponseField] == OrderCommitment.Response.Cancelled);
        // The farmer already said yes: the buyer decides now.
        _advanced.Should().Contain(a => a.OrderId == "order-trucker-asked" && a.From == "funded"
            && a.To == OrderCommitment.AwaitingTransportStatus
            && (string)a.Fields![OrderCommitment.TransportFailureField] == OrderCommitment.TransportFailure.TripCancelled
            && (string)a.Fields[OrderCommitment.FaultPartyField] == OrderCommitment.Party.Trucker);
        // Everyone had agreed: the pending delivery goes first, then the order.
        await _deliveries.Received(1).TryCancelPendingAsync("del-001");
        await _orders.Received(1).ReleaseDeliverySlotAsync("order-agreed", "del-001");
        _advanced.Should().Contain(a => a.OrderId == "order-agreed" && a.From == "accepted" && a.To == OrderCommitment.AwaitingTransportStatus);
        // Unpaid and finished orders are left alone (the payment gate refuses the unpaid one).
        _advanced.Should().NotContain(a => a.OrderId == "order-unpaid" || a.OrderId == "order-done");
        _stamped.Should().NotContain(s => s.OrderId == "order-unpaid" || s.OrderId == "order-done");
    }

    [Fact]
    public async Task TripCancel_AcceptedOrderWhoseDeliveryLeft_IsLeftForTheParties()
    {
        // The order row lags its delivery: the pre-check saw `accepted`, the delivery is picked up.
        _deliveryStatus = "pickedUp";
        _ordersOnTrip.Add(Order("order-moving", "accepted", farmer: "accepted", trucker: "accepted", activeDeliveryId: "del-001"));

        var result = await UpdateTripAsync(new Dictionary<string, object> { { "status", "cancelled" }, { "version", 3 } });

        result.GetProperty("status").GetString().Should().Be("success");
        _advanced.Should().BeEmpty();
        await _orders.DidNotReceive().ReleaseDeliverySlotAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task TripDelete_CascadesLikeACancel()
    {
        _ordersOnTrip.Add(Order("order-trucker-asked", "funded", farmer: "accepted", trucker: "pending"));

        var result = await DeleteTripAsync();

        result.GetProperty("status").GetString().Should().Be("success");
        _advanced.Should().ContainSingle(a => a.OrderId == "order-trucker-asked" && a.To == OrderCommitment.AwaitingTransportStatus);
    }

    // ===== an order create respects the trip =====

    [Fact]
    public async Task OrderCreate_TripLeavingWithin24Hours_IsRefused()
    {
        _trip = Trip(DateTime.UtcNow.AddHours(20), booked: 0);

        var result = await CreateOrderAsync(quantityKg: 50);

        result.GetProperty("message").GetString().Should().StartWith("ORDER_VALIDATION").And.Contain("24 hours");
        await _orders.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task OrderCreate_MoreThanTheTripHasFree_IsRefused()
    {
        _trip = Trip(DateTime.UtcNow.AddDays(5), booked: 95); // 5 quintals free

        var result = await CreateOrderAsync(quantityKg: 600); // 6 quintals

        result.GetProperty("message").GetString().Should().StartWith("TRIP_CAPACITY_EXCEEDED").And.Contain("5 quintals free");
        await _orders.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task OrderCreate_WithinTheRoomAndLeadTime_Succeeds()
    {
        _trip = Trip(DateTime.UtcNow.AddDays(5), booked: 95);

        var result = await CreateOrderAsync(quantityKg: 500); // exactly the 5 quintals free

        result.GetProperty("status").GetString().Should().Be("success");
    }

    [Theory]
    [InlineData("farmerResponse", "accepted")]
    [InlineData("truckerResponse", "accepted")]
    [InlineData("declineReason", "price")]
    public async Task OrderCreate_PlantingACommitmentResponse_IsRefused(string field, string value)
    {
        // Planted "accepted" values would open the farmer's and trucker's phones at funding
        // before anyone agreed, and block the farmer's decline.
        _trip = Trip(DateTime.UtcNow.AddDays(5), booked: 0);

        var result = await CreateOrderAsync(quantityKg: 50, extra: (field, value));

        result.GetProperty("message").GetString().Should().StartWith("ORDER_VALIDATION").And.Contain(field);
        await _orders.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task OrderCreate_FlagOff_SkipsTheBookingChecks()
    {
        _trip = Trip(DateTime.UtcNow.AddHours(20), booked: 95);

        var result = await CreateOrderAsync(quantityKg: 600, enabled: false);

        result.GetProperty("status").GetString().Should().Be("success");
    }

    // ===== helpers =====

    private Task<JsonElement> UpdateTripAsync(Dictionary<string, object> data, bool enabled = true)
        => SendAsync(TruckerId, "trucker", new SyncOperationRequest
        {
            OperationId = "op-trip-update",
            IdempotencyKey = $"{TruckerId}:trip:{TripId}:update:{Guid.NewGuid():N}",
            EntityType = "trip",
            EntityId = TripId,
            Action = "update",
            Data = data
        }, enabled);

    private Task<JsonElement> DeleteTripAsync()
        => SendAsync(TruckerId, "trucker", new SyncOperationRequest
        {
            OperationId = "op-trip-delete",
            IdempotencyKey = $"{TruckerId}:trip:{TripId}:delete:{Guid.NewGuid():N}",
            EntityType = "trip",
            EntityId = TripId,
            Action = "delete",
            Data = new Dictionary<string, object> { { "version", 3 } }
        });

    private Task<JsonElement> CreateOrderAsync(double quantityKg, bool enabled = true, (string Key, object Value)? extra = null)
    {
        var data = new Dictionary<string, object>
        {
            { "buyerId", BuyerId }, { "farmerId", FarmerId }, { "listingId", ListingId },
            { "buyerName", "Meron Alemu" }, { "farmerName", "Abebe Kebede" },
            { "cropType", "teff" }, { "quantity", quantityKg }, { "pricePerUnit", 45.5 },
            { "tripId", TripId }, { "truckerId", TruckerId }, { "truckerName", "Tadesse Worku" },
            { "transportCost", quantityKg / 100 * 120.0 }, { "paymentMethod", "telebirr" }
        };
        if (extra != null)
        {
            data[extra.Value.Key] = extra.Value.Value;
        }

        return SendAsync(BuyerId, "buyer", new SyncOperationRequest
        {
            OperationId = "op-order-create",
            IdempotencyKey = $"{BuyerId}:order:order-new:create:{Guid.NewGuid():N}",
            EntityType = "order",
            EntityId = "order-new",
            Action = "create",
            Data = data
        }, enabled);
    }

    private async Task<JsonElement> SendAsync(string userId, string role, SyncOperationRequest operation, bool enabled = true)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-esc-17",
                ClientId = "device-001",
                Operations = new List<SyncOperationRequest> { operation }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId }, { "custom:role", role } }
            }
        };

        var response = await MakeFunction(enabled).FunctionHandler(request, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }

    private static Dictionary<string, AttributeValue> Trip(DateTime departure, double booked)
    {
        var trip = new Dictionary<string, AttributeValue>
        {
            { "tripId", new AttributeValue { S = TripId } },
            { "truckerId", new AttributeValue { S = TruckerId } },
            { "truckerName", new AttributeValue { S = "Tadesse Worku" } },
            { "fromWoreda", new AttributeValue { S = "Bishoftu" } },
            { "toWoreda", new AttributeValue { S = "Addis Ababa" } },
            { "departureDate", new AttributeValue { S = departure.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) } },
            { "capacity", new AttributeValue { N = "100" } },
            { "pricePerQuintal", new AttributeValue { N = "120" } },
            { "status", new AttributeValue { S = "open" } },
            { "version", new AttributeValue { N = "3" } },
            { "createdAt", new AttributeValue { S = "2026-10-01T08:00:00.000Z" } },
            { "updatedAt", new AttributeValue { S = "2026-10-01T08:00:00.000Z" } }
        };
        if (booked > 0)
        {
            trip["bookedQuintals"] = new AttributeValue { N = booked.ToString(CultureInfo.InvariantCulture) };
            trip["remainingQuintals"] = new AttributeValue { N = (100 - booked).ToString(CultureInfo.InvariantCulture) };
        }

        return trip;
    }

    private static Dictionary<string, object> Order(
        string orderId, string status, string? farmer = null, string? trucker = null, string? activeDeliveryId = null)
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", orderId }, { "listingId", ListingId }, { "buyerId", BuyerId }, { "farmerId", FarmerId },
            { "tripId", TripId }, { "truckerId", TruckerId }, { "quantity", 500.0 },
            { "status", status }, { "version", "4" }
        };
        if (farmer != null)
        {
            order[OrderCommitment.FarmerResponseField] = farmer;
        }

        if (trucker != null)
        {
            order[OrderCommitment.TruckerResponseField] = trucker;
        }

        if (activeDeliveryId != null)
        {
            order["activeDeliveryId"] = activeDeliveryId;
        }

        return order;
    }
}
