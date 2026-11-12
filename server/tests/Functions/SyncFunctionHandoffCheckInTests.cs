using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-34 (delivery-verification-api.md 2.6.0, sync-protocol.md 1.55.0): the <c>handoff_check_in</c> sync
/// entity, the late pickup scan of a judged order and the buyer's deadline cancel under the judgement.
/// The check-in goes into the order's <c>checkIns</c> map through <see cref="IOrderRepository.TryRecordCheckInAsync"/>
/// (no version bump); the server's receipt time is the only time that counts.
/// </summary>
public sealed class SyncFunctionHandoffCheckInTests
{
    private const string OrderId = "ord-ci";
    private const string ListingId = "listing-ci";
    private const string DeliveryId = "del-ci";
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";
    private const string BuyerId = "buyer-001";
    private const string DeliveriesTable = "test-deliveries-table";
    private const string Nonce = "nonce-of-the-farmer-0123456789abcdef";

    // The farm (the listing's point as stamped at acceptance) and two places relative to it.
    private const double FarmLat = 8.7523;
    private const double FarmLon = 38.9785;
    private const double NearLat = 8.7600; // about 0.9 km north
    private const double FarLat = 8.8500; // about 11 km north

    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly ISyncMetadataRepository _syncMeta = Substitute.For<ISyncMetadataRepository>();
    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly IDeliveryQrSecretRepository _secrets = Substitute.For<IDeliveryQrSecretRepository>();
    private readonly TestLambdaContext _context = new();

    private Dictionary<string, object> _order;
    private readonly List<(string Kind, Dictionary<string, object> Entry, bool WhileOpen)> _recorded = new();
    private bool _commitments = true;
    private bool _classification = true;

    public SyncFunctionHandoffCheckInTests()
    {
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _order = Order();
        _orders.GetByIdAsync(OrderId, true).Returns(_ => _order);
        _orders.GetByIdAsync(OrderId).Returns(_ => _order);
        _orders.TryRecordCheckInAsync(OrderId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<bool>())
            .Returns(call =>
            {
                _recorded.Add((call.ArgAt<string>(2), call.ArgAt<Dictionary<string, object>>(3), call.ArgAt<bool>(4)));
                return true;
            });
        _orders.TryEnterAdminQueueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
    }

    // ===== fixture =====

    /// <summary>An accepted trip order stamped judgeable: the pickup day tomorrow, the deadline two days on.</summary>
    private static Dictionary<string, object> Order(
        string status = "accepted", string judgement = PickupJudgement.Outcome.Pending, bool selfPickup = false,
        DateTime? pickupDate = null, DateTime? deadline = null, bool withPoint = true)
    {
        var pickup = pickupDate ?? DateTime.UtcNow.AddHours(12);
        var order = new Dictionary<string, object>
        {
            { "orderId", OrderId }, { "listingId", ListingId }, { "createdAt", DateTime.UtcNow.AddDays(-3).ToString("o") },
            { "buyerId", BuyerId }, { "farmerId", FarmerId }, { "status", status }, { "version", "6" },
            { OrderCommitment.FulfillmentField, selfPickup ? OrderCommitment.Fulfillment.SelfPickup : OrderCommitment.Fulfillment.Trucker },
            { PickupDeadline.Field, OrderCommitment.Iso(deadline ?? pickup.AddHours(48)) },
            { PickupJudgement.Field, judgement }
        };
        if (!selfPickup)
        {
            order["tripId"] = "trip-ci";
            order["truckerId"] = TruckerId;
            order[OrderCommitment.PickupDateField] = OrderCommitment.Iso(pickup);
        }

        if (withPoint)
        {
            order[PickupJudgement.PickupLatitudeField] = FarmLat.ToString(System.Globalization.CultureInfo.InvariantCulture);
            order[PickupJudgement.PickupLongitudeField] = FarmLon.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return order;
    }

    private SyncFunction MakeFunction() => new(_syncMeta, _crops, _dynamoDb, "test-trips-table", DeliveriesTable,
        fraudService: _fraud, orderRepo: _orders, qrSecretRepo: _secrets)
    {
        CommitmentsEnabled = _commitments,
        PickupClassificationEnabled = _classification
    };

    private static Dictionary<string, object> Arrival(double latitude = NearLat, double longitude = FarmLon, string? capturedAt = null)
    {
        var data = new Dictionary<string, object>
        {
            { "orderId", OrderId }, { "kind", PickupJudgement.Kind.ArrivedPickup },
            { "latitude", latitude }, { "longitude", longitude }, { "accuracyM", 25 }
        };
        if (capturedAt != null)
        {
            data["capturedAt"] = capturedAt;
        }

        return data;
    }

    private static Dictionary<string, object> CropsReady(double? latitude = null)
    {
        var data = new Dictionary<string, object> { { "orderId", OrderId }, { "kind", PickupJudgement.Kind.CropsReady } };
        if (latitude != null)
        {
            data["latitude"] = latitude.Value;
            data["longitude"] = FarmLon;
        }

        return data;
    }

    private Task<JsonElement> CheckInAsync(string userId, Dictionary<string, object> data, string? entityId = null, string action = "create")
        => SendAsync(userId, PickupJudgement.EntityType, entityId ?? $"{OrderId}#{data["kind"]}", action, data);

    private async Task<JsonElement> SendAsync(string userId, string entityType, string entityId, string action, Dictionary<string, object> data)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-esc-34",
                ClientId = "device-001",
                Operations = new List<SyncOperationRequest>
                {
                    new()
                    {
                        OperationId = "op-esc-34",
                        IdempotencyKey = $"{userId}:{entityType}:{entityId}:{action}:{Guid.NewGuid():N}",
                        EntityType = entityType,
                        EntityId = entityId,
                        Action = action,
                        Data = data
                    }
                }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId } }
            }
        };

        var response = await MakeFunction().FunctionHandler(request, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }

    private static string MessageOf(JsonElement result) => result.GetProperty("message").GetString() ?? "";

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    private Task NoWriteAsync()
        => _orders.DidNotReceive().TryRecordCheckInAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Dictionary<string, object>>(), Arg.Any<bool>());

    private Task NoSignalAsync()
        => _fraud.DidNotReceive().RecordFraudSignalAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>());

    // ===== on time =====

    [Fact]
    public async Task TheTrucker_ChecksInNearTheFarm_TheEntryIsStoredWithTheServersTime()
    {
        var before = DateTime.UtcNow;

        var result = await CheckInAsync(TruckerId, Arrival(capturedAt: before.AddMinutes(-3).ToString("o")));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        result.GetProperty("serverVersion").GetInt32().Should().Be(6, "a check-in moves no version");
        _recorded.Should().ContainSingle();
        var (kind, entry, whileOpen) = _recorded[0];
        kind.Should().Be(PickupJudgement.Kind.ArrivedPickup);
        whileOpen.Should().BeTrue();
        entry[PickupJudgement.EntryBy].Should().Be(TruckerId);
        entry[PickupJudgement.EntryDistanceChecked].Should().Be(true);
        entry[PickupJudgement.EntryLate].Should().Be(false);
        OrderCommitment.TryParseUtc((string)entry[PickupJudgement.EntryReceivedAt], out var received).Should().BeTrue();
        received.Should().BeOnOrAfter(before.AddSeconds(-1));
        entry.Should().ContainKey(PickupJudgement.EntryCapturedAt);
        await _crops.DidNotReceive().GetByIdAsync(Arg.Any<string>());
        await NoSignalAsync();
    }

    [Fact]
    public async Task TheFarmer_SaysTheCropsAreReady_WithoutGps()
    {
        var result = await CheckInAsync(FarmerId, CropsReady());

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        _recorded.Should().ContainSingle(r => r.Kind == PickupJudgement.Kind.CropsReady
                                              && (bool)r.Entry[PickupJudgement.EntryDistanceChecked] == false);
    }

    [Fact]
    public async Task ASelfPickupBuyer_ChecksIn_ForTheTruckersColumn()
    {
        _order = Order(selfPickup: true);

        var result = await CheckInAsync(BuyerId, Arrival());

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        _recorded.Should().ContainSingle(r => (string)r.Entry[PickupJudgement.EntryBy] == BuyerId);
    }

    [Fact]
    public async Task ARepeat_IsASuccess_AndTheFirstCheckInIsKept()
    {
        _order[PickupJudgement.CheckInsField] = new Dictionary<string, object>
        {
            { PickupJudgement.Kind.ArrivedPickup, new Dictionary<string, object> { { PickupJudgement.EntryReceivedAt, OrderCommitment.Iso(DateTime.UtcNow.AddHours(-1)) } } }
        };

        var result = await CheckInAsync(TruckerId, Arrival());

        result.GetProperty("status").GetString().Should().Be("success");
        await NoWriteAsync();
    }

    [Fact]
    public async Task ARepeatOfALateCheckIn_KeepsItsLateAnswer()
    {
        _order[PickupJudgement.CheckInsField] = new Dictionary<string, object>
        {
            {
                PickupJudgement.Kind.ArrivedPickup, new Dictionary<string, object>
                {
                    { PickupJudgement.EntryReceivedAt, OrderCommitment.Iso(DateTime.UtcNow.AddHours(-1)) },
                    { PickupJudgement.EntryLate, true }
                }
            }
        };

        var result = await CheckInAsync(TruckerId, Arrival());

        MessageOf(result).Should().StartWith($"{PickupJudgement.TooLateCode}: ");
        await NoWriteAsync();
    }

    [Fact]
    public async Task AWriteThatLosesToAnotherCheckInOfTheKind_IsASuccess()
    {
        _orders.TryRecordCheckInAsync(OrderId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<bool>())
            .Returns(_ =>
            {
                _order = Order();
                _order[PickupJudgement.CheckInsField] = new Dictionary<string, object>
                {
                    { PickupJudgement.Kind.ArrivedPickup, new Dictionary<string, object> { { PickupJudgement.EntryReceivedAt, OrderCommitment.Iso(DateTime.UtcNow) } } }
                };
                return false;
            });

        var result = await CheckInAsync(TruckerId, Arrival());

        result.GetProperty("status").GetString().Should().Be("success");
    }

    // ===== who =====

    [Theory]
    [InlineData(FarmerId, "arrived_pickup")]
    [InlineData(BuyerId, "arrived_pickup")]
    [InlineData(TruckerId, "crops_ready")]
    [InlineData(BuyerId, "crops_ready")]
    public async Task AnotherPartyOfTheOrder_CannotSendIt_AndIsNotFlagged(string caller, string kind)
    {
        var data = kind == PickupJudgement.Kind.ArrivedPickup ? Arrival() : CropsReady();

        var result = await CheckInAsync(caller, data);

        MessageOf(result).Should().StartWith($"{PickupJudgement.NotPartyCode}: ");
        await NoWriteAsync();
        await NoSignalAsync();
    }

    [Fact]
    public async Task AStranger_IsRefused_WithTheRoleSignal()
    {
        var result = await CheckInAsync("stranger-9", Arrival());

        MessageOf(result).Should().StartWith($"{PickupJudgement.NotPartyCode}: ");
        await NoWriteAsync();
        await _fraud.Received(1).RecordFraudSignalAsync("stranger-9", FraudDetectionService.RoleForbiddenSignal,
            $"{OrderId}#arrived_pickup", PickupJudgement.EntityType, Arg.Any<string?>());
    }

    [Fact]
    public async Task OnASelfPickupOrder_NoTruckerCanCheckIn()
    {
        _order = Order(selfPickup: true);

        var result = await CheckInAsync(TruckerId, Arrival());

        MessageOf(result).Should().StartWith($"{PickupJudgement.NotPartyCode}: ");
    }

    // ===== when the window is not open =====

    [Theory]
    [InlineData(false, true, TruckerId)]
    [InlineData(true, false, TruckerId)]
    [InlineData(true, false, "stranger-9")]
    public async Task WithoutBothFlags_TheEntityIsUnsupported_AsBefore_NoReadNoSignal(bool commitments, bool classification, string caller)
    {
        _commitments = commitments;
        _classification = classification;

        var result = await CheckInAsync(caller, Arrival());

        result.GetProperty("status").GetString().Should().Be("error");
        MessageOf(result).Should().Be("Unsupported entity type: 'handoff_check_in'.");
        await _orders.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
        await _orders.DidNotReceive().GetByIdAsync(Arg.Any<string>());
        await NoWriteAsync();
        await NoSignalAsync();
    }

    [Fact]
    public async Task AnOrderAcceptedBeforeTheFlag_TakesNoCheckIn()
    {
        _order.Remove(PickupJudgement.Field);

        var result = await CheckInAsync(TruckerId, Arrival());

        MessageOf(result).Should().StartWith($"{PickupJudgement.NotOpenCode}: ");
        await NoWriteAsync();
    }

    [Theory]
    [InlineData("pickup_confirmed", PickupJudgement.Outcome.Pending)]
    [InlineData("awaiting_transport", PickupJudgement.Outcome.Pending)]
    [InlineData("delivered", PickupJudgement.Outcome.Scanned)]
    public async Task AnOrderNoLongerWaitingForItsPickup_IsNotOpen(string status, string judgement)
    {
        _order = Order(status: status, judgement: judgement);

        var result = await CheckInAsync(TruckerId, Arrival());

        MessageOf(result).Should().StartWith($"{PickupJudgement.NotOpenCode}: ");
        await NoWriteAsync();
    }

    [Fact]
    public async Task MoreThanADayBeforeThePickupDay_IsTooEarly()
    {
        _order = Order(pickupDate: DateTime.UtcNow.AddHours(30));

        var result = await CheckInAsync(TruckerId, Arrival());

        MessageOf(result).Should().StartWith($"{PickupJudgement.TooEarlyCode}: Check-ins open at ");
        await NoWriteAsync();
    }

    [Fact]
    public async Task MoreThan5KmFromTheFarm_IsTooFar()
    {
        var result = await CheckInAsync(TruckerId, Arrival(latitude: FarLat));

        MessageOf(result).Should().StartWith($"{PickupJudgement.TooFarCode}: You are 10.9 km from the farm");
        await NoWriteAsync();
    }

    [Fact]
    public async Task AFarmersFixFarFromTheFarm_IsKeptUnmeasured_AndTheCropsAreStillReady()
    {
        // Spec 4.8: the farmer's GPS is optional, so a bad fix must not cost the farmer the defence.
        var result = await CheckInAsync(FarmerId, CropsReady(latitude: FarLat));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        var (kind, entry, whileOpen) = _recorded.Should().ContainSingle().Subject;
        kind.Should().Be(PickupJudgement.Kind.CropsReady);
        whileOpen.Should().BeTrue();
        entry[PickupJudgement.EntryDistanceChecked].Should().Be(false);
        entry[PickupJudgement.EntryOutOfRange].Should().Be(true);
        ((double)entry[PickupJudgement.EntryDistanceKm]).Should().BeGreaterThan(5);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task OnlyCreate(string action)
    {
        var result = await CheckInAsync(TruckerId, Arrival(), action: action);

        MessageOf(result).Should().StartWith($"{PickupJudgement.ValidationCode}: ");
    }

    [Fact]
    public async Task AnArrivalWithoutGps_IsInvalid()
    {
        var data = Arrival();
        data.Remove("latitude");
        data.Remove("longitude");

        var result = await CheckInAsync(TruckerId, data);

        MessageOf(result).Should().StartWith($"{PickupJudgement.ValidationCode}: ").And.Contain("required for arrived_pickup");
        await _orders.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task AnUnreadableOrder_IsRetried()
    {
        _orders.GetByIdAsync(OrderId, true).ThrowsAsync(new ProvisionedThroughputExceededException("slow down"));

        var result = await CheckInAsync(TruckerId, Arrival());

        result.GetProperty("status").GetString().Should().Be("error");
        MessageOf(result).Should().NotContain("CHECKIN_", "an unprefixed error is retried by the phone");
    }

    // ===== FUP-204: the farmer cannot push the trucker out of range =====

    [Fact]
    public async Task FUP204_AListingMovedAfterAcceptance_DoesNotFailTheTruckersCheckIn()
    {
        // The farmer moved the listing 11 km away after the order was accepted: the check-in is measured
        // against the point stamped at acceptance, and the moved listing is never read.
        _crops.GetByIdAsync(ListingId).Returns(new Dictionary<string, object>
        {
            { "listingId", ListingId }, { "latitude", FarLat.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            { "longitude", FarmLon.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        });

        var result = await CheckInAsync(TruckerId, Arrival(latitude: NearLat));

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        await _crops.DidNotReceive().GetByIdAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task WithoutAStampedPoint_TheLiveListingIsUsed_AndWithoutOneTheCheckInIsUnmeasured()
    {
        _order = Order(withPoint: false);
        _crops.GetByIdAsync(ListingId).Returns(new Dictionary<string, object> { { "listingId", ListingId } });

        var result = await CheckInAsync(TruckerId, Arrival());

        result.GetProperty("status").GetString().Should().Be("success");
        _recorded.Should().ContainSingle(r => (bool)r.Entry[PickupJudgement.EntryDistanceChecked] == false);
    }

    // ===== late =====

    [Fact]
    public async Task ALateOfflineSync_BeforeTheJudgement_IsKeptAsLateEvidence_AndRefused()
    {
        // Captured an hour before the deadline on a phone that was offline; it reaches the server after it.
        var deadline = DateTime.UtcNow.AddMinutes(-10);
        _order = Order(pickupDate: deadline.AddHours(-48), deadline: deadline);

        var result = await CheckInAsync(TruckerId, Arrival(capturedAt: deadline.AddHours(-1).ToString("o")));

        MessageOf(result).Should().StartWith($"{PickupJudgement.TooLateCode}: ");
        _recorded.Should().ContainSingle(r => r.WhileOpen == false && (bool)r.Entry[PickupJudgement.EntryLate]);
        // Not judged yet: the judgement queues it for the admin when it judges the trucker absent.
        await _orders.DidNotReceive().TryEnterAdminQueueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ALateCheckIn_AfterATruckerNoShowJudgement_GoesToTheAdminQueue()
    {
        _order = Order(status: OrderCommitment.AwaitingTransportStatus, judgement: PickupJudgement.Outcome.TruckerNoShow,
            deadline: DateTime.UtcNow.AddHours(-3));
        _order[PickupJudgement.JudgedAtField] = OrderCommitment.Iso(DateTime.UtcNow.AddHours(-3));

        var result = await CheckInAsync(TruckerId, Arrival());

        MessageOf(result).Should().StartWith($"{PickupJudgement.TooLateCode}: ");
        _recorded.Should().ContainSingle(r => r.WhileOpen == false && (bool)r.Entry[PickupJudgement.EntryLate]);
        await _orders.Received(1).TryEnterAdminQueueAsync(OrderId, PickupJudgement.ReasonLateEvidence,
            $"escrow-order-{OrderId}", Arg.Any<string>(), OrderCommitment.AwaitingTransportStatus);
    }

    [Fact]
    public async Task ALateCheckIn_ThatDoesNotContradictTheJudgement_IsKeptButNotQueued()
    {
        // The trucker was judged absent; the farmer's late "crops are ready" changes nothing about that.
        _order = Order(status: OrderCommitment.AwaitingTransportStatus, judgement: PickupJudgement.Outcome.TruckerNoShow,
            deadline: DateTime.UtcNow.AddHours(-3));
        _order[PickupJudgement.JudgedAtField] = OrderCommitment.Iso(DateTime.UtcNow.AddHours(-3));

        var result = await CheckInAsync(FarmerId, CropsReady());

        MessageOf(result).Should().StartWith($"{PickupJudgement.TooLateCode}: ");
        await _orders.DidNotReceive().TryEnterAdminQueueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ACheckInThatTheJudgementOvertook_IsStoredAsLate()
    {
        // On time when read; the judgement landed before the write, so the open write is refused.
        var first = true;
        _orders.TryRecordCheckInAsync(OrderId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<bool>())
            .Returns(call =>
            {
                _recorded.Add((call.ArgAt<string>(2), call.ArgAt<Dictionary<string, object>>(3), call.ArgAt<bool>(4)));
                if (first)
                {
                    first = false;
                    _order = Order(status: OrderCommitment.AwaitingTransportStatus, judgement: PickupJudgement.Outcome.TruckerNoShow);
                    _order[PickupJudgement.JudgedAtField] = OrderCommitment.Iso(DateTime.UtcNow);
                    return false;
                }

                return true;
            });

        var result = await CheckInAsync(TruckerId, Arrival());

        MessageOf(result).Should().StartWith($"{PickupJudgement.TooLateCode}: ");
        _recorded.Should().HaveCount(2);
        _recorded[1].WhileOpen.Should().BeFalse();
        ((bool)_recorded[1].Entry[PickupJudgement.EntryLate]).Should().BeTrue();
    }

    // ===== a pickup scan after the judgement (spec 4.8 "Late evidence", D10) =====

    private void SetupDelivery(string status, string deliveryType = "trucker")
        => _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new()
                    {
                        { "deliveryId", new AttributeValue { S = DeliveryId } },
                        { "tripId", new AttributeValue { S = "trip-ci" } },
                        { "orderId", new AttributeValue { S = OrderId } },
                        { "listingId", new AttributeValue { S = ListingId } },
                        { "farmerId", new AttributeValue { S = FarmerId } },
                        { "truckerId", new AttributeValue { S = deliveryType == "self_pickup" ? "" : TruckerId } },
                        { "buyerId", new AttributeValue { S = BuyerId } },
                        { "deliveryType", new AttributeValue { S = deliveryType } },
                        { "status", new AttributeValue { S = status } },
                        { "createdAt", new AttributeValue { S = DateTime.UtcNow.AddDays(-2).ToString("o") } },
                        { "version", new AttributeValue { N = "2" } }
                    }
                }
            });

    private Task<JsonElement> ScanAsync(string userId, string status, string? nonce)
    {
        var data = new Dictionary<string, object> { { "status", status }, { "version", 2 } };
        if (nonce != null)
        {
            data["scannedQrNonce"] = nonce;
        }

        return SendAsync(userId, "delivery", DeliveryId, "update", data);
    }

    private void JudgedFarmerNoShow()
    {
        _order = Order(status: "cancelled", judgement: PickupJudgement.Outcome.FarmerNoShow, deadline: DateTime.UtcNow.AddHours(-2));
        _order[PickupJudgement.JudgedAtField] = OrderCommitment.Iso(DateTime.UtcNow.AddHours(-2));
        _order["cancelReason"] = PickupJudgement.FarmerNoShowReason;
    }

    [Fact]
    public async Task ARealScanAfterTheJudgement_IsRefused_QueuedForTheAdmin_AndAlarmed()
    {
        JudgedFarmerNoShow();
        SetupDelivery("cancelled");
        _secrets.GetAsync(DeliveryId).Returns(new DeliveryQrSecret { DeliveryId = DeliveryId, Nonce = Nonce });

        var result = await ScanAsync(TruckerId, "pickedUp", Nonce);

        MessageOf(result).Should().StartWith($"{OrderCommitment.DeadlinePassedCode}: ");
        await _orders.Received(1).TryEnterAdminQueueAsync(OrderId, PickupJudgement.ReasonLateEvidence,
            $"escrow-order-{OrderId}", Arg.Any<string>(), "cancelled");
        Logs.Should().Contain("ORDER_TRANSPORT_CANCEL_PICKUP_REFUSED").And.Contain("reason=late_pickup_evidence");
        await _dynamoDb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
        await _secrets.DidNotReceive().MarkConsumedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData("wrong-nonce")]
    [InlineData(null)]
    public async Task AScanThatProvesNothing_IsRefusedToo_WithoutAQueueEntry(string? nonce)
    {
        _order = Order(judgement: PickupJudgement.Outcome.BothPresentNoScan, deadline: DateTime.UtcNow.AddHours(-2));
        _order[PickupJudgement.JudgedAtField] = OrderCommitment.Iso(DateTime.UtcNow.AddHours(-2));
        SetupDelivery("pending");
        _secrets.GetAsync(DeliveryId).Returns(new DeliveryQrSecret { DeliveryId = DeliveryId, Nonce = Nonce });

        var result = await ScanAsync(TruckerId, "pickedUp", nonce);

        MessageOf(result).Should().StartWith($"{OrderCommitment.DeadlinePassedCode}: ").And.Contain("Contact support");
        await _orders.DidNotReceive().TryEnterAdminQueueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>());
        Logs.Should().NotContain("ORDER_TRANSPORT_CANCEL_PICKUP_REFUSED");
    }

    [Fact]
    public async Task ASelfPickupBuyersLateScan_IsRefused()
    {
        _order = Order(status: "cancelled", judgement: PickupJudgement.Outcome.FarmerNoShow, selfPickup: true,
            deadline: DateTime.UtcNow.AddHours(-2));
        _order[PickupJudgement.JudgedAtField] = OrderCommitment.Iso(DateTime.UtcNow.AddHours(-2));
        SetupDelivery("cancelled", deliveryType: "self_pickup");

        var result = await ScanAsync(BuyerId, "delivered", Nonce);

        MessageOf(result).Should().StartWith($"{OrderCommitment.DeadlinePassedCode}: ");
    }

    [Fact]
    public async Task WithTheFlagOff_ALateScanMeetsTheOldRules()
    {
        _classification = false;
        JudgedFarmerNoShow();
        SetupDelivery("cancelled");

        var result = await ScanAsync(TruckerId, "pickedUp", Nonce);

        MessageOf(result).Should().StartWith("DELIVERY_ALREADY_CANCELLED");
        await _orders.DidNotReceive().GetByIdAsync(OrderId, true);
    }

    [Fact]
    public async Task AScanBeforeTheJudgement_IsNotAffected()
    {
        // Still pending: the normal flow decides (here the nonce check, with no secret on record yet).
        SetupDelivery("pending");

        var result = await ScanAsync(TruckerId, "pickedUp", Nonce);

        MessageOf(result).Should().NotStartWith(OrderCommitment.DeadlinePassedCode);
        await _orders.DidNotReceive().TryEnterAdminQueueAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>());
    }

    // ===== the buyer's deadline cancel under the judgement =====

    [Fact]
    public async Task TheBuyersDeadlineCancel_IsRefused_ForAJudgeableOrder()
    {
        _order = Order(pickupDate: DateTime.UtcNow.AddHours(-60), deadline: DateTime.UtcNow.AddHours(-12));

        var result = await SendAsync(BuyerId, "order", OrderId, "update",
            new Dictionary<string, object> { { "status", "cancelled" }, { "version", 6 } });

        MessageOf(result).Should().Be(
            "ORDER_CANCEL_NOT_ALLOWED: The pickup is judged from the arrival check-ins; you will be told the outcome.");
    }

    [Fact]
    public async Task TheBuyersDeadlineCancel_WithTheFlagOff_IsNotRefusedForTheJudgement()
    {
        _classification = false;
        _order = Order(pickupDate: DateTime.UtcNow.AddHours(-60), deadline: DateTime.UtcNow.AddHours(-12));

        var result = await SendAsync(BuyerId, "order", OrderId, "update",
            new Dictionary<string, object> { { "status", "cancelled" }, { "version", 6 } });

        MessageOf(result).Should().NotContain("arrival check-ins");
    }
}
