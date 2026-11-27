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
using WeredaConnect.Server.Helpers;
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

    /// <summary>What today's apps send in <c>X-App-Build</c>; FUP-241 tests also send none.</summary>
    private const string CurrentAppBuild = "3";

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

    // FRD-09: set by the pattern rule tests only; null keeps every other test as it was.
    private IFraudDetectionService? _fraud;
    private TransactionPatternService? _patterns;

    private SyncFunction MakeFunction(bool enabled = true) =>
        new(_syncMeta, _crops, _dynamoDb, TripsTable, DeliveriesTable, fraudService: _fraud,
            userRepo: _users, orderRepo: _orders, transactionAuditRepo: _txAudit, deliveryRepo: _deliveries, tripRepo: _trips)
        {
            CommitmentsEnabled = enabled,
            PatternRules = _patterns
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

    // trip-listing-api 2.1.0: the destination point belongs to the route. A booked trip may get the
    // one an older client never sent, but not a different one.
    [Fact]
    public async Task BookedTrip_GetsAMissingDestinationPoint()
    {
        var result = await UpdateTripAsync(new Dictionary<string, object>
        {
            { "destinationLatitude", 9.6 }, { "destinationLongitude", 41.85 }, { "version", 3 }
        });

        result.GetProperty("status").GetString().Should().Be("success", result.GetRawText());
    }

    [Fact]
    public async Task BookedTrip_MovingTheDestinationPoint_IsRefused()
    {
        _trip["destinationLatitude"] = new AttributeValue { N = "9.6" };
        _trip["destinationLongitude"] = new AttributeValue { N = "41.85" };

        var result = await UpdateTripAsync(new Dictionary<string, object>
        {
            { "destinationLatitude", 8.55 }, { "destinationLongitude", 39.27 }, { "version", 3 }
        });

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("TRIP_HAS_BOOKINGS")
            .And.Contain("destinationLatitude");
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
    public async Task TripCancel_WithPaidOrders_CountsAgainstTheTruckersCancelRate_OncePerTrip()
    {
        // FRD-09: two paid orders lose their transport; with two earlier funded orders and one earlier
        // drop-out the trucker cancelled 3 of 5. One signal for the trip, on the trucker.
        _fraud = Substitute.For<IFraudDetectionService>();
        _patterns = new TransactionPatternService(_orders, null, TransactionPatternSettings.Default);
        _ordersOnTrip.Add(Order("order-farmer-deciding", "funded", farmer: "pending", trucker: "not_asked"));
        _ordersOnTrip.Add(Order("order-trucker-asked", "funded", farmer: "accepted", trucker: "pending"));
        _ordersOnTrip.Add(Order("order-unpaid", "created"));
        var past = new[] { "past-1", "past-2", "past-3" }.Select((id, i) => new Dictionary<string, object>
        {
            { "orderId", id }, { "status", i == 0 ? OrderCommitment.AwaitingTransportStatus : "settled" },
            { "createdAt", DateTime.UtcNow.AddDays(-20).ToString("o", CultureInfo.InvariantCulture) },
            { "paymentVerifiedAt", DateTime.UtcNow.AddDays(-20).ToString("o", CultureInfo.InvariantCulture) },
            { OrderCommitment.TruckerResponseField, i == 0 ? "cancelled" : "accepted" }
        }).ToList();
        _orders.QueryOrdersAsync(null, null, TruckerId, null, null, 50, null)
            .Returns((past, (Dictionary<string, AttributeValue>?)null));

        var result = await UpdateTripAsync(new Dictionary<string, object> { { "status", "cancelled" }, { "version", 3 } });

        result.GetProperty("status").GetString().Should().Be("success");
        await _fraud.Received(1).RecordFraudSignalAsync(TruckerId, "CANCEL_AFTER_FUND_RATE", TripId, "trip",
            Arg.Is<string>(d => d.StartsWith("The trucker cancelled 3 of 5 funded orders")));
    }

    [Fact]
    public async Task TripCancel_WithoutPaidOrders_ReadsNoHistory()
    {
        _fraud = Substitute.For<IFraudDetectionService>();
        _patterns = new TransactionPatternService(_orders, null, TransactionPatternSettings.Default);
        _ordersOnTrip.Add(Order("order-unpaid", "created"));

        var result = await UpdateTripAsync(new Dictionary<string, object> { { "status", "cancelled" }, { "version", 3 } });

        result.GetProperty("status").GetString().Should().Be("success");
        await _orders.DidNotReceiveWithAnyArgs().QueryOrdersAsync(default, default, default, default, default, default, default);
        await _fraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
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

    [Fact]
    public async Task TripCancel_TheUpdateTheAppsSend_AndTheLegacyDelete_BothCancelAtTheVersionRead()
    {
        // ESC-17: mobile and web send an update to cancelled (trip-listing-api 2.4.0); builds before it
        // queued a delete, which stays a cancel (with deletedAt) for the operations still in the field.
        _ordersOnTrip.Add(Order("order-trucker-asked", "funded", farmer: "accepted", trucker: "pending"));

        var update = await UpdateTripAsync(new Dictionary<string, object> { { "status", "cancelled" }, { "version", 3 } });
        var updateWrite = _tripWrite!;
        _tripWrite = null;
        var delete = await DeleteTripAsync();
        var deleteWrite = _tripWrite!;

        update.GetProperty("status").GetString().Should().Be("success");
        update.GetProperty("serverVersion").GetInt32().Should().Be(4);
        updateWrite.ConditionExpression.Should().Be("#v = :expectedVersion");
        updateWrite.ExpressionAttributeValues[":expectedVersion"].N.Should().Be("3");
        updateWrite.ExpressionAttributeValues.Values.Should().Contain(v => v.S == "cancelled");
        updateWrite.ExpressionAttributeNames.Values.Should().NotContain("deletedAt");

        delete.GetProperty("status").GetString().Should().Be("success");
        deleteWrite.ExpressionAttributeValues[":status"].S.Should().Be("cancelled");
        deleteWrite.ExpressionAttributeNames.Values.Should().Contain("deletedAt");
        deleteWrite.ExpressionAttributeValues[":expectedVersion"].N.Should().Be("3");

        // Both cascade the same way.
        _advanced.Where(a => a.OrderId == "order-trucker-asked").Should().HaveCount(2);
    }

    // ===== FUP-241: TRIP_HAS_BOOKINGS only to apps that send X-App-Build =====

    [Theory]
    [InlineData("2", "TRIP_HAS_BOOKINGS")]
    [InlineData(CurrentAppBuild, "TRIP_HAS_BOOKINGS")]
    [InlineData(null, "SYNC_VALIDATION")]
    public async Task BookedTrip_ChangingTheRoute_CodeFollowsTheAppBuild(string? appBuild, string code)
    {
        var result = await UpdateTripAsync(
            new Dictionary<string, object> { { "fromWoreda", "Somewhere Else" }, { "version", 3 } }, appBuild: appBuild);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().Be(
            $"{code}: fromWoreda cannot change while orders are booked on this trip (20 quintals).");
        _tripWrite.Should().BeNull();
    }

    [Theory]
    [InlineData("2", "TRIP_HAS_BOOKINGS")]
    [InlineData(null, "SYNC_VALIDATION")]
    public async Task BookedTrip_MovingTheDestinationPoint_CodeFollowsTheAppBuild(string? appBuild, string code)
    {
        _trip["destinationLatitude"] = new AttributeValue { N = "9.6" };
        _trip["destinationLongitude"] = new AttributeValue { N = "41.85" };

        var result = await UpdateTripAsync(new Dictionary<string, object>
        {
            { "destinationLatitude", 8.55 }, { "destinationLongitude", 39.27 }, { "version", 3 }
        }, appBuild: appBuild);

        result.GetProperty("message").GetString().Should().Be(
            $"{code}: destinationLatitude cannot change while orders are booked on this trip (20 quintals).");
        _tripWrite.Should().BeNull();
    }

    [Theory]
    [InlineData("2", "TRIP_HAS_BOOKINGS")]
    [InlineData(null, "SYNC_VALIDATION")]
    [InlineData("abc", "SYNC_VALIDATION")] // an unreadable header is an old app (ClientAppBuild)
    public async Task BookedTrip_CapacityBelowTheBookings_CodeFollowsTheAppBuild(string? appBuild, string code)
    {
        var result = await UpdateTripAsync(
            new Dictionary<string, object> { { "capacity", 10.0 }, { "version", 3 } }, appBuild: appBuild);

        result.GetProperty("message").GetString().Should().Be(
            $"{code}: capacity cannot drop below the 20 quintals already booked.");
        _tripWrite.Should().BeNull();
    }

    [Fact]
    public async Task BookedTrip_NoAppBuild_TheFraudSignalKeepsTheRealCode()
    {
        _fraud = Substitute.For<IFraudDetectionService>();

        var result = await UpdateTripAsync(
            new Dictionary<string, object> { { "capacity", 10.0 }, { "version", 3 } }, appBuild: null);

        result.GetProperty("message").GetString().Should().StartWith("SYNC_VALIDATION: ");
        await _fraud.Received(1).RecordFraudSignalAsync(TruckerId, FraudDetectionService.ImmutableFieldChangeSignal, TripId, "trip",
            "TRIP_HAS_BOOKINGS: capacity cannot drop below the 20 quintals already booked.");
    }

    [Theory]
    [InlineData("2", "TRIP_HAS_BOOKINGS")]
    [InlineData(null, "SYNC_VALIDATION")]
    public async Task TripCancel_WithCropsOnTheRoad_CodeFollowsTheAppBuild(string? appBuild, string code)
    {
        _ordersOnTrip.Add(Order("order-1", "in_transit", farmer: "accepted", trucker: "accepted"));

        var update = await UpdateTripAsync(
            new Dictionary<string, object> { { "status", "cancelled" }, { "version", 3 } }, appBuild: appBuild);
        var delete = await DeleteTripAsync(appBuild);

        var expected = $"{code}: Crops from this trip are already on the way; it can no longer be cancelled.";
        update.GetProperty("message").GetString().Should().Be(expected);
        delete.GetProperty("message").GetString().Should().Be(expected);
        _tripWrite.Should().BeNull();
    }

    [Fact]
    public async Task ClientCannotSetMatched_NoAppBuild_KeepsItsCode()
    {
        // Only the two FUP-01 codes change for an old app; TRIP_VALIDATION is not one of them.
        var result = await UpdateTripAsync(
            new Dictionary<string, object> { { "status", "matched" }, { "version", 3 } }, appBuild: null);

        result.GetProperty("message").GetString().Should().Be("TRIP_VALIDATION: 'matched' is set by the server when the trip is fully booked.");
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OrderCreate_TripThatLeftBeforeToday_IsRefusedWhateverTheFlag(bool enabled)
    {
        // L-7: a stale offline cache must not book a truck that already left. Yesterday's
        // Ethiopia midnight, the stored form of a trip picked for yesterday.
        var yesterday = TripDeparture.StartOfEthiopiaDayUtc(DateTime.UtcNow).AddDays(-1);
        _trip = Trip(yesterday, booked: 0);

        var result = await CreateOrderAsync(quantityKg: 50, enabled: enabled);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should()
            .StartWith("ORDER_TRIP_NOT_ELIGIBLE: This trip has already left");
        await _orders.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task OrderCreate_TripLeavingToday_IsNotRefusedAsLeft()
    {
        // Trucks run late: a trip for today (stored as today's Ethiopia midnight, already
        // past by the clock) still takes an order with the flag off.
        _trip = Trip(TripDeparture.StartOfEthiopiaDayUtc(DateTime.UtcNow), booked: 0);

        var result = await CreateOrderAsync(quantityKg: 50, enabled: false);

        result.GetProperty("status").GetString().Should().Be("success");
    }

    // ===== helpers =====

    private Task<JsonElement> UpdateTripAsync(
        Dictionary<string, object> data, bool enabled = true, string? appBuild = CurrentAppBuild)
        => SendAsync(TruckerId, "trucker", new SyncOperationRequest
        {
            OperationId = "op-trip-update",
            IdempotencyKey = $"{TruckerId}:trip:{TripId}:update:{Guid.NewGuid():N}",
            EntityType = "trip",
            EntityId = TripId,
            Action = "update",
            Data = data
        }, enabled, appBuild);

    private Task<JsonElement> DeleteTripAsync(string? appBuild = CurrentAppBuild)
        => SendAsync(TruckerId, "trucker", new SyncOperationRequest
        {
            OperationId = "op-trip-delete",
            IdempotencyKey = $"{TruckerId}:trip:{TripId}:delete:{Guid.NewGuid():N}",
            EntityType = "trip",
            EntityId = TripId,
            Action = "delete",
            Data = new Dictionary<string, object> { { "version", 3 } }
        }, appBuild: appBuild);

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

    /// <summary><paramref name="appBuild"/> null sends no <c>X-App-Build</c> (an app before build 2).</summary>
    private async Task<JsonElement> SendAsync(
        string userId, string role, SyncOperationRequest operation, bool enabled = true, string? appBuild = CurrentAppBuild)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-esc-17",
                ClientId = "device-001",
                Operations = new List<SyncOperationRequest> { operation }
            }, SerializerOptions),
            Headers = appBuild == null ? null : new Dictionary<string, string> { { ClientAppBuild.Header, appBuild } },
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
