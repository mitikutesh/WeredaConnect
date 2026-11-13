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
/// ESC-15: sequential three-party commitments through <c>/sync</c>. The farmer is asked first;
/// the trucker only after the farmer accepted; a failed transport parks the order in
/// awaiting_transport; the cancel rules of a paid order; and the flag off keeps the old flow.
/// </summary>
public class SyncFunctionCommitmentTests
{
    private const string BuyerId = "buyer-001";
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";
    private const string OrderId = "order-001";
    private const string TripId = "trip-001";
    private const string DeliveryId = "del-001";
    private const string ListingId = "listing-001";
    private const string EscrowId = "escrow-order-order-001";
    private const string TripsTable = "test-trips-table";
    private const string DeliveriesTable = "test-deliveries-table";

    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ICropRepository _crops = Substitute.For<ICropRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISyncMetadataRepository _syncMeta = Substitute.For<ISyncMetadataRepository>();
    private readonly IDeliveryRepository _deliveries = Substitute.For<IDeliveryRepository>();
    private readonly IEscrowRepository _escrows = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _escrowAudit = Substitute.For<IEscrowAuditRepository>();
    private readonly ITransactionAuditRepository _txAudit = Substitute.For<ITransactionAuditRepository>();
    private readonly ITripRepository _trips = Substitute.For<ITripRepository>();
    private readonly List<Dictionary<string, object>> _auditRows = new();
    private readonly TestLambdaContext _context = new();

    private Dictionary<string, object> _order;
    private Dictionary<string, AttributeValue>? _trip;
    private Dictionary<string, object>? _written;
    private string? _writtenExpectedStatus;
    private string _deliveryStatus = "pending";

    public SyncFunctionCommitmentTests()
    {
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        _txAudit.AppendAsync(Arg.Do<Dictionary<string, object>>(row => _auditRows.Add(row))).Returns(Task.CompletedTask);

        _order = Order("funded", version: 2);
        _trip = Trip(DateTime.UtcNow.AddDays(3));

        _orders.GetByIdAsync(OrderId).Returns(_ => _order);
        _orders.GetByIdAsync(OrderId, true).Returns(_ => _order);
        _orders.UpdateAsync(OrderId, Arg.Do<Dictionary<string, object>>(d => _written = d), Arg.Any<int>())
            .Returns(call => AuthResult<Dictionary<string, object>>.Success(Merge(_order, call.ArgAt<Dictionary<string, object>>(1), call.ArgAt<int>(2) + 1)));
        _orders.TryUpdateWithAsync(OrderId, Arg.Do<Dictionary<string, object>>(d => _written = d), Arg.Any<int>(),
                Arg.Do<string>(s => _writtenExpectedStatus = s), Arg.Any<IReadOnlyList<TransactWriteItem>>())
            .Returns(call => new OrderLinkedWriteResult(OrderLinkedWriteOutcome.Written,
                Merge(_order, call.ArgAt<Dictionary<string, object>>(1), call.ArgAt<int>(2) + 1)));
        _escrows.BuildRefundArm(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new TransactWriteItem { Update = new Update { TableName = "test-escrows", UpdateExpression = "arm" } });
        _escrows.GetByIdAsync(EscrowId, true).Returns(_ => Escrow());
        _escrows.GetByIdAsync(EscrowId).Returns(_ => Escrow());
        _escrows.QueryByOrderIdAsync(OrderId, Arg.Any<int>()).Returns(_ => new List<Dictionary<string, object>> { Escrow() });
        _crops.TryRestoreStockAsync(ListingId, OrderId, Arg.Any<double>()).Returns(true);
        _trips.TryReleaseCapacityAsync(TripId, OrderId, Arg.Any<double>()).Returns(true);
        _trips.BuildConfirmBookingItem(TripId, Arg.Any<string>(), OrderId, Arg.Any<string>())
            .Returns(new TransactWriteItem { Update = new Update { TableName = TripsTable, UpdateExpression = "confirm" } });
        _deliveries.TryCancelPendingAsync(DeliveryId).Returns(true);

        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == TripsTable))
            .Returns(_ => new QueryResponse { Items = _trip == null ? new() : new() { _trip } });
        _dynamoDb.QueryAsync(Arg.Is<QueryRequest>(r => r.TableName == DeliveriesTable))
            .Returns(_ => new QueryResponse
            {
                Items = new()
                {
                    new()
                    {
                        { "deliveryId", new AttributeValue { S = DeliveryId } },
                        { "tripId", new AttributeValue { S = TripId } },
                        { "orderId", new AttributeValue { S = OrderId } },
                        { "truckerId", new AttributeValue { S = TruckerId } },
                        { "buyerId", new AttributeValue { S = BuyerId } },
                        { "status", new AttributeValue { S = _deliveryStatus } }
                    }
                }
            });
    }

    private bool _fraudHoldEnabled;

    // ESC-34: pickupClassification for the stamp at acceptance; off keeps every other test as it was.
    private bool _pickupClassification;

    // ESC-29: reputationPenalties for the accept gate; off keeps every other test as it was.
    private bool _reputationGates;

    // FRD-09: set by the pattern rule tests only; null keeps every other test as it was.
    private IFraudDetectionService? _fraud;
    private TransactionPatternService? _patterns;

    private SyncFunction MakeFunction(bool enabled = true)
    {
        var escrowService = new EscrowService(_escrows, _escrowAudit, idempotencyRepo: null, telebirrGateway: null,
            userRepo: _users, orderRepo: _orders, deliveryRepo: _deliveries);
        return new SyncFunction(_syncMeta, _crops, _dynamoDb, TripsTable, DeliveriesTable,
            fraudService: _fraud,
            userRepo: _users, orderRepo: _orders, escrowRepo: _escrows, transactionAuditRepo: _txAudit,
            orderEscrowService: escrowService, deliveryRepo: _deliveries, tripRepo: _trips)
        {
            CommitmentsEnabled = enabled,
            ReputationGatesEnabled = _reputationGates,
            FraudHoldEnabled = _fraudHoldEnabled,
            PickupClassificationEnabled = _pickupClassification,
            EscrowsTableName = "test-escrows",
            PatternRules = _patterns
        };
    }

    // ===== ESC-29: a restricted party takes no new commitment (reputation-api.md 8.1) =====

    private void Restrict(string userId, string role) => _users.GetUserByIdAsync(userId, true).Returns(new UserDto
    {
        UserId = userId,
        PhoneNumber = "+251900000000",
        Role = role,
        Woreda = "Jimma",
        IsPhoneVerified = true,
        CreatedAt = "2026-02-16T10:00:00Z",
        UpdatedAt = "2026-02-16T10:00:00Z",
        Version = 1,
        Reputation = new ReputationSnapshot
        {
            Tier = ReputationTiers.Restricted,
            ActivePoints = 10,
            HasRestriction = true,
            RestrictionCause = ReputationCauses.Points,
            RestrictionSince = DateTime.UtcNow.AddDays(-1),
            RestrictionUntil = DateTime.UtcNow.AddDays(13)
        }
    });

    [Fact]
    public async Task FarmerAccept_WhileRestricted_IsRefused_NothingWritten()
    {
        _reputationGates = true;
        Restrict(FarmerId, "farmer");

        var result = await SendAsync(FarmerId, new Dictionary<string, object> { { "farmerResponse", "accept" }, { "version", 2 } });

        result.GetProperty("status").GetString().Should().Be("error");
        // No X-App-Build on this request: the permanent code every client knows.
        result.GetProperty("message").GetString().Should().StartWith("ORDER_VALIDATION: Your account can't take new orders until ");
        _written.Should().BeNull();
    }

    [Fact]
    public async Task FarmerDecline_WhileRestricted_IsStillAllowed()
    {
        _reputationGates = true;
        Restrict(FarmerId, "farmer");

        var result = await SendAsync(FarmerId, new Dictionary<string, object> { { "farmerResponse", "decline" }, { "version", 2 } });

        result.GetProperty("status").GetString().Should().Be("success");
    }

    [Fact]
    public async Task FarmerAccept_Replayed_WhileRestricted_IsStillTheNoOpSuccess()
    {
        _reputationGates = true;
        Restrict(FarmerId, "farmer");
        _order = Order("funded", version: 3, farmer: "accepted", trucker: "pending");

        var result = await SendAsync(FarmerId, new Dictionary<string, object> { { "farmerResponse", "accept" }, { "version", 3 } });

        result.GetProperty("status").GetString().Should().Be("success");
        _written.Should().BeNull();
    }

    [Fact]
    public async Task TruckerAccept_WhileRestricted_IsRefused_AndADeclineIsNot()
    {
        _reputationGates = true;
        Restrict(TruckerId, "trucker");
        _order = Order("funded", version: 3, farmer: "accepted", trucker: "pending");

        var accept = await SendAsync(TruckerId, new Dictionary<string, object> { { "truckerResponse", "accept" }, { "version", 3 } });
        accept.GetProperty("message").GetString().Should().StartWith("ORDER_VALIDATION: Your account can't take new orders until ");
        _written.Should().BeNull();

        var decline = await SendAsync(TruckerId, new Dictionary<string, object> { { "truckerResponse", "decline" }, { "version", 3 } });
        decline.GetProperty("status").GetString().Should().Be("success");
    }

    [Fact]
    public async Task FarmerAccept_WhileRestricted_FlagOff_GoesThrough()
    {
        Restrict(FarmerId, "farmer");

        var result = await SendAsync(FarmerId, new Dictionary<string, object> { { "farmerResponse", "accept" }, { "version", 2 } });

        result.GetProperty("status").GetString().Should().Be("success");
    }

    // ===== farmer first =====

    [Theory]
    [InlineData("status", "accepted")]
    [InlineData("farmerResponse", "accept")]
    public async Task FarmerAccept_TripOrder_AsksTheTrucker_AndStaysFunded(string key, string value)
    {
        var result = await SendAsync(FarmerId, new Dictionary<string, object> { { key, value }, { "version", 2 } });

        result.GetProperty("status").GetString().Should().Be("success");
        _written.Should().NotBeNull();
        _written.Should().NotContainKey("status");
        _written![OrderCommitment.FarmerResponseField].Should().Be("accepted");
        _written[OrderCommitment.TruckerResponseField].Should().Be("pending");
        _written.Should().ContainKey(OrderCommitment.TruckerDeadlineAtField);
        _written.Should().ContainKey("respondedAt");
        _written.Should().NotContainKey(PickupDeadline.Field);
        _auditRows.Should().ContainSingle(r => (string)r["action"] == "farmer_responded");
        // The serverData row tells the farmer's screen what happened.
        result.GetProperty("serverData").GetProperty("truckerResponse").GetString().Should().Be("pending");
    }

    [Fact]
    public async Task FarmerAccept_SelfPickup_AcceptsTheOrder_WithThePickupClock()
    {
        _order = Order("funded", version: 2, selfPickup: true);

        var result = await SendAsync(FarmerId, new Dictionary<string, object> { { "status", "accepted" }, { "version", 2 } });

        result.GetProperty("status").GetString().Should().Be("success");
        _written!["status"].Should().Be("accepted");
        _written.Should().ContainKey(PickupDeadline.Field);
        _written[OrderCommitment.FarmerResponseField].Should().Be("accepted");
    }

    [Fact]
    public async Task FarmerAccept_Again_IsASuccessWithoutAWrite()
    {
        _order = Order("funded", version: 3, farmer: "accepted", trucker: "pending");

        var result = await SendAsync(FarmerId, new Dictionary<string, object> { { "status", "accepted" }, { "version", 3 } });

        result.GetProperty("status").GetString().Should().Be("success");
        await _orders.DidNotReceive().UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    [Fact]
    public async Task FarmerAccept_HeldEscrow_IsRefused()
    {
        _escrows.GetByIdAsync(EscrowId, true).Returns(_ => Escrow(status: EscrowHoldFields.Status));

        var result = await SendAsync(FarmerId, new Dictionary<string, object> { { "status", "accepted" }, { "version", 2 } });

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("ORDER_VALIDATION");
        _written.Should().BeNull();
    }

    [Fact]
    public async Task FarmerDecline_Rejects_Refunds_Restocks_AndReleasesTheTrip()
    {
        var result = await SendAsync(FarmerId, new Dictionary<string, object>
        {
            { "farmerResponse", "decline" }, { "declineReason", "crop_not_ready" }, { "rejectionReason", "Hail damage." }, { "version", 2 }
        });

        result.GetProperty("status").GetString().Should().Be("success");
        _written!["status"].Should().Be("rejected");
        _written[OrderCommitment.FarmerResponseField].Should().Be("declined");
        _written[OrderCommitment.FaultPartyField].Should().Be("farmer");
        _written["rejectionReason"].Should().Be("Hail damage.");
        await _crops.Received(1).TryRestoreStockAsync(ListingId, OrderId, 5.0);
        await _trips.Received(1).TryReleaseCapacityAsync(TripId, OrderId, 5.0);
        // FUP-53: the reject and the refund arm are one transaction on the funded order; no second step.
        await _orders.Received(1).TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 2, "funded",
            Arg.Is<IReadOnlyList<TransactWriteItem>>(l => l.Count == 1 && l[0].Update.UpdateExpression == "arm"));
        _escrows.Received(1).BuildRefundArm(EscrowId, Arg.Any<string>(), "order_rejected", Arg.Any<string>());
        await _orders.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default);
        await _escrows.DidNotReceiveWithAnyArgs().UpdateStatusAsync(default!, default!, default!);
        await _escrowAudit.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(a =>
            a["action"].ToString() == "refund_scheduled" && a["metadata"].ToString() == "order_rejected" && a["actor"].ToString() == "system"));
    }

    [Fact]
    public async Task FarmerDecline_WhoseEscrowADisputeFroze_IsRefused_AndNothingIsWritten()
    {
        // FUP-53: the money is frozen, so the order must not be closed over it.
        _escrows.GetByIdAsync(EscrowId, true).Returns(_ => Escrow(status: "disputed"));

        var result = await SendAsync(FarmerId, new Dictionary<string, object> { { "farmerResponse", "decline" }, { "version", 2 } });

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().Be("ORDER_CANCEL_NOT_ALLOWED: The payment is being settled or reviewed.");
        _written.Should().BeNull();
        await _crops.DidNotReceiveWithAnyArgs().TryRestoreStockAsync(default!, default!, default);
        await _trips.DidNotReceiveWithAnyArgs().TryReleaseCapacityAsync(default!, default!, default);
        _auditRows.Should().BeEmpty();
    }

    [Fact]
    public async Task Buyer_SendingTheFarmerCommand_IsUnauthorized()
    {
        var result = await SendAsync(BuyerId, new Dictionary<string, object> { { "farmerResponse", "accept" }, { "version", 2 } });

        result.GetProperty("message").GetString().Should().StartWith("ORDER_UNAUTHORIZED");
        _written.Should().BeNull();
    }

    // ===== the trucker, only after the farmer =====

    [Fact]
    public async Task TruckerAccept_BeforeTheFarmer_IsRefused()
    {
        var result = await SendAsync(TruckerId, new Dictionary<string, object> { { "truckerResponse", "accept" }, { "version", 2 } });

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith(OrderCommitment.ResponseNotPendingCode)
            .And.Contain("farmer has not accepted");
        _written.Should().BeNull();
    }

    [Fact]
    public async Task TruckerAccept_AfterTheFarmer_AcceptsTheOrder_InOneTransactionWithTheBooking()
    {
        _order = Order("funded", version: 3, farmer: "accepted", trucker: "pending",
            truckerDeadline: DateTime.UtcNow.AddHours(10));

        var result = await SendAsync(TruckerId, new Dictionary<string, object> { { "truckerResponse", "accept" }, { "version", 3 } });

        result.GetProperty("status").GetString().Should().Be("success");
        result.GetProperty("serverVersion").GetInt32().Should().Be(4);
        _writtenExpectedStatus.Should().Be("funded");
        _written!["status"].Should().Be("accepted");
        _written[OrderCommitment.TruckerResponseField].Should().Be("accepted");
        _written.Should().ContainKey(PickupDeadline.Field);
        await _orders.Received(1).TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 3, "funded",
            Arg.Is<IReadOnlyList<TransactWriteItem>>(items => items.Count == 1 && items[0].Update.UpdateExpression == "confirm"));
        _auditRows.Should().ContainSingle(r => (string)r["action"] == "trucker_responded");
    }

    // ===== ESC-34: the stamp of the pickup judgement at acceptance =====

    private static Dictionary<string, object> Listing(double? latitude, double? longitude)
    {
        var listing = new Dictionary<string, object> { { "listingId", ListingId }, { "farmerId", FarmerId } };
        if (latitude != null && longitude != null)
        {
            listing["latitude"] = latitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            listing["longitude"] = longitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return listing;
    }

    [Fact]
    public async Task TruckerAccept_WithPickupClassification_StampsTheJudgement_AndTheFarmsPoint()
    {
        _pickupClassification = true;
        _crops.GetByIdAsync(ListingId).Returns(Listing(8.7523, 38.9785));
        _order = Order("funded", version: 3, farmer: "accepted", trucker: "pending",
            truckerDeadline: DateTime.UtcNow.AddHours(10));

        var result = await SendAsync(TruckerId, new Dictionary<string, object> { { "truckerResponse", "accept" }, { "version", 3 } });

        result.GetProperty("status").GetString().Should().Be("success");
        _written!["status"].Should().Be("accepted");
        _written[PickupJudgement.Field].Should().Be(PickupJudgement.Outcome.Pending);
        _written[PickupJudgement.CheckInsField].Should().BeOfType<Dictionary<string, object>>()
            .Which.Should().BeEmpty();
        _written[PickupJudgement.PickupLatitudeField].Should().Be(8.7523);
        _written[PickupJudgement.PickupLongitudeField].Should().Be(38.9785);
        // The farm's point is server-only: the trucker's screen never gets it.
        result.GetProperty("serverData").TryGetProperty(PickupJudgement.PickupLatitudeField, out _).Should().BeFalse();
        result.GetProperty("serverData").GetProperty(PickupJudgement.Field).GetString().Should().Be("pending");
    }

    [Fact]
    public async Task FarmerAccept_SelfPickup_WithPickupClassification_StampsTheJudgement()
    {
        _pickupClassification = true;
        _crops.GetByIdAsync(ListingId).Returns(Listing(8.7523, 38.9785));
        _order = Order("funded", version: 2, selfPickup: true);

        var result = await SendAsync(FarmerId, new Dictionary<string, object> { { "farmerResponse", "accept" }, { "version", 2 } });

        result.GetProperty("status").GetString().Should().Be("success");
        _written!["status"].Should().Be("accepted");
        _written[PickupJudgement.Field].Should().Be(PickupJudgement.Outcome.Pending);
        _written.Should().ContainKey(PickupJudgement.PickupLatitudeField);
    }

    [Fact]
    public async Task Accept_WithPickupClassification_AndAnUnreadableListing_StillAccepts_WithoutThePoint()
    {
        _pickupClassification = true;
        _crops.GetByIdAsync(ListingId).ThrowsAsync(new ProvisionedThroughputExceededException("slow down"));
        _order = Order("funded", version: 2, selfPickup: true);

        var result = await SendAsync(FarmerId, new Dictionary<string, object> { { "farmerResponse", "accept" }, { "version", 2 } });

        result.GetProperty("status").GetString().Should().Be("success");
        _written![PickupJudgement.Field].Should().Be(PickupJudgement.Outcome.Pending);
        _written.Should().NotContainKey(PickupJudgement.PickupLatitudeField);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Accept_WithoutBothFlags_StampsNothing(bool commitments, bool classification)
    {
        _pickupClassification = classification;
        _order = Order("funded", version: 2, selfPickup: true);

        var result = await SendAsync(FarmerId, new Dictionary<string, object> { { "status", "accepted" }, { "version", 2 } }, enabled: commitments);

        result.GetProperty("status").GetString().Should().Be("success");
        _written.Should().NotContainKey(PickupJudgement.Field);
        _written.Should().NotContainKey(PickupJudgement.CheckInsField);
        await _crops.DidNotReceive().GetByIdAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task AClientSentPickupReopen_IsDropped_AndTheStoredStampNeverGoesBack()
    {
        // FUP-287: only the admin Lambda writes pickupReopen, and the party's echo leaves it out.
        _order = Order("funded", version: 2, selfPickup: true);
        _order[PickupReopen.Field] = PickupReopen.Stamp(PickupReopen.Exit.Pending, PickupJudgement.Outcome.BothPresentNoScan,
            1, "2026-11-23T08:00:00.0000000Z", "2026-11-24T08:00:00.0000000Z");

        var result = await SendAsync(FarmerId, new Dictionary<string, object>
        {
            { "status", "accepted" },
            { "version", 2 },
            { PickupReopen.Field, new Dictionary<string, object> { { PickupReopen.StampExit, PickupReopen.Exit.HandoffConfirmed } } }
        }, enabled: false);

        result.GetProperty("status").GetString().Should().Be("success");
        _written.Should().NotContainKey(PickupReopen.Field);
        result.GetProperty("serverData").TryGetProperty(PickupReopen.Field, out _).Should().BeFalse();
    }

    [Fact]
    public async Task FarmerAccept_TripOrder_StaysFunded_AndIsNotStampedYet()
    {
        _pickupClassification = true;

        var result = await SendAsync(FarmerId, new Dictionary<string, object> { { "farmerResponse", "accept" }, { "version", 2 } });

        result.GetProperty("status").GetString().Should().Be("success");
        _written.Should().NotContainKey(PickupJudgement.Field);
    }

    [Fact]
    public async Task TruckerAccept_TripRefusesAndIsFull_ParksTheOrderWithTheBuyer()
    {
        _order = Order("funded", version: 3, farmer: "accepted", trucker: "pending");
        _orders.TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<TransactWriteItem>>())
            .Returns(new OrderLinkedWriteResult(OrderLinkedWriteOutcome.LinkedItemRefused));
        _trips.TryReserveCapacityAsync(TripId, OrderId, 5.0).Returns(new TripReserveResult(TripReserveOutcome.Full, 0));

        var result = await SendAsync(TruckerId, new Dictionary<string, object> { { "truckerResponse", "accept" }, { "version", 3 } });

        result.GetProperty("status").GetString().Should().Be("success");
        _written!["status"].Should().Be(OrderCommitment.AwaitingTransportStatus);
        _written[OrderCommitment.TransportFailureField].Should().Be(OrderCommitment.TransportFailure.TripFull);
        _written[OrderCommitment.TruckerResponseField].Should().Be(OrderCommitment.Response.FailedCapacity);
    }

    [Fact]
    public async Task TruckerAccept_WithTheFraudHoldOn_ChecksTheEscrowInTheSameTransaction()
    {
        _fraudHoldEnabled = true;
        _order = Order("funded", version: 3, farmer: "accepted", trucker: "pending");

        var result = await SendAsync(TruckerId, new Dictionary<string, object> { { "truckerResponse", "accept" }, { "version", 3 } });

        result.GetProperty("status").GetString().Should().Be("success");
        await _orders.Received(1).TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), 3, "funded",
            Arg.Is<IReadOnlyList<TransactWriteItem>>(items => items.Count == 2
                && items[0].Update.UpdateExpression == "confirm"
                && items[1].ConditionCheck.TableName == "test-escrows"
                && items[1].ConditionCheck.ConditionExpression.Contains(":held")));
    }

    [Fact]
    public async Task TruckerAccept_EscrowHeldSinceTheCheck_IsAReviewRefusal()
    {
        _fraudHoldEnabled = true;
        _order = Order("funded", version: 3, farmer: "accepted", trucker: "pending");
        _orders.TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<TransactWriteItem>>())
            .Returns(new OrderLinkedWriteResult(OrderLinkedWriteOutcome.LinkedItemRefused));
        var calls = 0;
        _escrows.GetByIdAsync(EscrowId, true).Returns(_ => ++calls == 1 ? Escrow() : Escrow(status: EscrowHoldFields.Status));

        var result = await SendAsync(TruckerId, new Dictionary<string, object> { { "truckerResponse", "accept" }, { "version", 3 } });

        result.GetProperty("message").GetString().Should().StartWith("ORDER_VALIDATION").And.Contain("review");
        await _trips.DidNotReceive().TryReserveCapacityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>());
    }

    [Fact]
    public async Task FarmerDecline_RejectionReasonOver500Characters_IsRefused()
    {
        var result = await SendAsync(FarmerId, new Dictionary<string, object>
        {
            { "farmerResponse", "decline" }, { "rejectionReason", new string('x', 501) }, { "version", 2 }
        });

        result.GetProperty("message").GetString().Should().StartWith("ORDER_VALIDATION").And.Contain("500");
        _written.Should().BeNull();
    }

    [Theory]
    [InlineData(BuyerId)]
    [InlineData(FarmerId)]
    public async Task DeclineReason_CannotBeRewrittenThroughAPlainUpdate(string caller)
    {
        _order = Order("rejected", version: 3, farmer: "declined", trucker: "not_asked");
        _order[OrderCommitment.DeclineReasonField] = "crop_not_ready";

        var result = await SendAsync(caller, new Dictionary<string, object> { { "declineReason", "price" }, { "version", 3 } });

        result.GetProperty("message").GetString().Should().StartWith("ORDER_VALIDATION").And.Contain("immutable");
        _written.Should().BeNull();
    }

    [Fact]
    public async Task TruckerAccept_TripGone_ParksTheOrder_WithTheTruckerAtFault()
    {
        _order = Order("funded", version: 3, farmer: "accepted", trucker: "pending");
        _trip = Trip(DateTime.UtcNow.AddDays(3), status: "cancelled");

        var result = await SendAsync(TruckerId, new Dictionary<string, object> { { "truckerResponse", "accept" }, { "version", 3 } });

        result.GetProperty("status").GetString().Should().Be("success");
        _written!["status"].Should().Be(OrderCommitment.AwaitingTransportStatus);
        _written[OrderCommitment.TransportFailureField].Should().Be(OrderCommitment.TransportFailure.TripCancelled);
        _written[OrderCommitment.FaultPartyField].Should().Be(OrderCommitment.Party.Trucker);
        await _orders.DidNotReceive().TryUpdateWithAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>(),
            Arg.Any<string>(), Arg.Any<IReadOnlyList<TransactWriteItem>>());
    }

    [Fact]
    public async Task TruckerAccept_TripFull_IsNobodysFault()
    {
        _order = Order("funded", version: 3, farmer: "accepted", trucker: "pending");
        _orders.TryUpdateWithAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<TransactWriteItem>>())
            .Returns(new OrderLinkedWriteResult(OrderLinkedWriteOutcome.LinkedItemRefused));
        _trips.TryReserveCapacityAsync(TripId, OrderId, 5.0).Returns(new TripReserveResult(TripReserveOutcome.Full, 0));

        await SendAsync(TruckerId, new Dictionary<string, object> { { "truckerResponse", "accept" }, { "version", 3 } });

        _written![OrderCommitment.FaultPartyField].Should().Be(OrderCommitment.Party.None);
    }

    [Fact]
    public async Task BuyerCancel_InAwaitingTransport_KeepsTheRecordedFault()
    {
        _order = Order(OrderCommitment.AwaitingTransportStatus, version: 4, farmer: "accepted", trucker: "failed_capacity", activeDeliveryId: null);
        _order[OrderCommitment.FaultPartyField] = OrderCommitment.Party.None;

        await SendAsync(BuyerId, new Dictionary<string, object> { { "status", "cancelled" }, { "version", 4 } });

        _written![OrderCommitment.FaultPartyField].Should().Be(OrderCommitment.Party.None);
        _written[OrderCommitment.CancelledByField].Should().Be("buyer");
    }

    [Fact]
    public async Task TruckerAccept_BookingMissing_ReservesAndRetries()
    {
        _order = Order("funded", version: 3, farmer: "accepted", trucker: "pending");
        var calls = 0;
        _orders.TryUpdateWithAsync(OrderId, Arg.Do<Dictionary<string, object>>(d => _written = d), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<TransactWriteItem>>())
            .Returns(call => ++calls == 1
                ? new OrderLinkedWriteResult(OrderLinkedWriteOutcome.LinkedItemRefused)
                : new OrderLinkedWriteResult(OrderLinkedWriteOutcome.Written, Merge(_order, call.ArgAt<Dictionary<string, object>>(1), 4)));
        _trips.TryReserveCapacityAsync(TripId, OrderId, 5.0).Returns(new TripReserveResult(TripReserveOutcome.Reserved, 95));

        var result = await SendAsync(TruckerId, new Dictionary<string, object> { { "truckerResponse", "accept" }, { "version", 3 } });

        result.GetProperty("status").GetString().Should().Be("success");
        calls.Should().Be(2);
        _written!["status"].Should().Be("accepted");
    }

    [Fact]
    public async Task TruckerAccept_AfterTheDeadline_IsRefused()
    {
        _order = Order("funded", version: 3, farmer: "accepted", trucker: "pending",
            truckerDeadline: DateTime.UtcNow.AddMinutes(-5));

        var result = await SendAsync(TruckerId, new Dictionary<string, object> { { "truckerResponse", "accept" }, { "version", 3 } });

        result.GetProperty("message").GetString().Should().StartWith(OrderCommitment.DeadlinePassedCode);
        _written.Should().BeNull();
    }

    [Fact]
    public async Task TruckerDecline_ParksTheOrder_AndReleasesTheBooking()
    {
        _order = Order("funded", version: 3, farmer: "accepted", trucker: "pending");

        var result = await SendAsync(TruckerId, new Dictionary<string, object>
        {
            { "truckerResponse", "decline" }, { "declineReason", "date" }, { "version", 3 }
        });

        result.GetProperty("status").GetString().Should().Be("success");
        _written!["status"].Should().Be(OrderCommitment.AwaitingTransportStatus);
        _written[OrderCommitment.TruckerResponseField].Should().Be("declined");
        _written[OrderCommitment.DeclineReasonField].Should().Be("date");
        _written[OrderCommitment.TransportFailureField].Should().Be(OrderCommitment.TransportFailure.TruckerDeclined);
        _written.Should().ContainKey(OrderCommitment.BuyerChoiceDeadlineAtField);
        await _trips.Received(1).TryReleaseCapacityAsync(TripId, OrderId, 5.0);
    }

    [Fact]
    public async Task TruckerDropOut_NeedsAFreshSignIn_ThenCancelsTheDeliveryAndParksTheOrder()
    {
        _order = Order("accepted", version: 5, farmer: "accepted", trucker: "accepted", activeDeliveryId: DeliveryId);

        var stale = await SendAsync(TruckerId, new Dictionary<string, object> { { "truckerResponse", "decline" }, { "version", 5 } },
            authTime: DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds());
        stale.GetProperty("message").GetString().Should().StartWith(StepUpPolicy.ErrorCode);
        _written.Should().BeNull();

        var result = await SendAsync(TruckerId, new Dictionary<string, object> { { "truckerResponse", "decline" }, { "version", 5 } },
            authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        result.GetProperty("status").GetString().Should().Be("success");
        _written!["status"].Should().Be(OrderCommitment.AwaitingTransportStatus);
        _written[OrderCommitment.TransportFailureField].Should().Be(OrderCommitment.TransportFailure.TruckerCancelled);
        await _deliveries.Received(1).TryCancelPendingAsync(DeliveryId);
        await _orders.Received(1).ReleaseDeliverySlotAsync(OrderId, DeliveryId);
    }

    [Fact]
    public async Task TruckerDropOut_AfterPickup_IsRefused()
    {
        _order = Order("accepted", version: 5, farmer: "accepted", trucker: "accepted", activeDeliveryId: DeliveryId);
        _deliveryStatus = "pickedUp";

        var result = await SendAsync(TruckerId, new Dictionary<string, object> { { "truckerResponse", "decline" }, { "version", 5 } },
            authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        result.GetProperty("message").GetString().Should().StartWith("ORDER_CANCEL_NOT_ALLOWED");
        _written.Should().BeNull();
    }

    [Fact]
    public async Task Buyer_SendingTheTruckerCommand_IsUnauthorized()
    {
        var result = await SendAsync(BuyerId, new Dictionary<string, object> { { "truckerResponse", "accept" }, { "version", 2 } });

        result.GetProperty("message").GetString().Should().StartWith("ORDER_UNAUTHORIZED");
        _written.Should().BeNull();
    }

    // ===== awaiting_transport is system-managed; the cancel rules =====

    [Theory]
    [InlineData(BuyerId)]
    [InlineData(FarmerId)]
    public async Task NoClient_CanSetAwaitingTransport(string caller)
    {
        _order = Order("funded", version: 2, farmer: "accepted", trucker: "pending");

        var result = await SendAsync(caller, new Dictionary<string, object> { { "status", OrderCommitment.AwaitingTransportStatus }, { "version", 2 } });

        result.GetProperty("message").GetString().Should().StartWith("ORDER_UNAUTHORIZED").And.Contain("system-managed");
        _written.Should().BeNull();
    }

    [Fact]
    public async Task BuyerCancel_WhileThePartiesDecide_IsRefused()
    {
        var result = await SendAsync(BuyerId, new Dictionary<string, object> { { "status", "cancelled" }, { "version", 2 } });

        result.GetProperty("message").GetString().Should().StartWith("ORDER_CANCEL_NOT_ALLOWED");
        _written.Should().BeNull();
    }

    [Fact]
    public async Task BuyerCancel_InAwaitingTransport_RefundsInFull()
    {
        _order = Order(OrderCommitment.AwaitingTransportStatus, version: 4, farmer: "accepted", trucker: "declined", activeDeliveryId: null);

        var result = await SendAsync(BuyerId, new Dictionary<string, object> { { "status", "cancelled" }, { "version", 4 } });

        result.GetProperty("status").GetString().Should().Be("success");
        _writtenExpectedStatus.Should().Be(OrderCommitment.AwaitingTransportStatus);
        _written!["status"].Should().Be("cancelled");
        _written["cancelReason"].Should().Be(PickupDeadline.CancelReason);
        _written[OrderCommitment.CancelledByField].Should().Be("buyer");
        await _crops.Received(1).TryRestoreStockAsync(ListingId, OrderId, 5.0);
    }

    [Fact]
    public async Task FarmerCancel_InAwaitingTransport_HasNoPenalty()
    {
        _order = Order(OrderCommitment.AwaitingTransportStatus, version: 4, farmer: "accepted", trucker: "expired", activeDeliveryId: null);

        var result = await SendAsync(FarmerId, new Dictionary<string, object> { { "status", "cancelled" }, { "version", 4 } });

        result.GetProperty("status").GetString().Should().Be("success");
        _written!["cancelReason"].Should().Be("farmer_cancelled");
        _written[OrderCommitment.CancelledByField].Should().Be("farmer");
        _written[OrderCommitment.FaultPartyField].Should().Be("none");
    }

    [Fact]
    public async Task FarmerCancel_OfACommittedOrder_NeedsAFreshSignIn_AndCountsAgainstTheFarmer()
    {
        _order = Order("accepted", version: 5, farmer: "accepted", trucker: "accepted", activeDeliveryId: DeliveryId);

        var stale = await SendAsync(FarmerId, new Dictionary<string, object> { { "status", "cancelled" }, { "version", 5 } });
        stale.GetProperty("message").GetString().Should().StartWith(StepUpPolicy.ErrorCode);

        var result = await SendAsync(FarmerId, new Dictionary<string, object> { { "status", "cancelled" }, { "version", 5 } },
            authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        result.GetProperty("status").GetString().Should().Be("success");
        _written!["cancelReason"].Should().Be("farmer_cancelled");
        _written[OrderCommitment.FaultPartyField].Should().Be("farmer");
        await _deliveries.Received(1).TryCancelPendingAsync(DeliveryId);
        await _trips.Received(1).TryReleaseCapacityAsync(TripId, OrderId, 5.0);
    }

    // ===== FRD-09: the cancel-after-fund rate of the party who cancels =====

    private IFraudDetectionService WithPatternRules()
    {
        _fraud = Substitute.For<IFraudDetectionService>();
        _patterns = new TransactionPatternService(_orders, _escrows, TransactionPatternSettings.Default);
        return _fraud;
    }

    private static Dictionary<string, object> PastOrder(string id, string status = "settled", string? cancelledBy = null,
        string? faultParty = null, string? truckerResponse = null)
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", id }, { "status", status },
            { "createdAt", DateTime.UtcNow.AddDays(-10).ToString("o") },
            { "paymentVerifiedAt", DateTime.UtcNow.AddDays(-10).ToString("o") }
        };
        if (cancelledBy != null)
        {
            order[OrderCommitment.CancelledByField] = cancelledBy;
        }

        if (faultParty != null)
        {
            order[OrderCommitment.FaultPartyField] = faultParty;
        }

        if (truckerResponse != null)
        {
            order[OrderCommitment.TruckerResponseField] = truckerResponse;
        }

        return order;
    }

    private static (List<Dictionary<string, object>>, Dictionary<string, AttributeValue>?) History(params Dictionary<string, object>[] rows)
        => (rows.ToList(), null);

    [Fact]
    public async Task FarmerCancel_OfACommittedOrder_FarmerWhoCancelsOften_IsFlagged()
    {
        var fraud = WithPatternRules();
        _orders.QueryOrdersAsync(null, FarmerId, null, null, null, 50, null).Returns(History(
            PastOrder("o-1", "cancelled", cancelledBy: "farmer", faultParty: "farmer"),
            PastOrder("o-2"), PastOrder("o-3"), PastOrder("o-4")));
        _order = Order("accepted", version: 5, farmer: "accepted", trucker: "accepted", activeDeliveryId: DeliveryId);

        var result = await SendAsync(FarmerId, new Dictionary<string, object> { { "status", "cancelled" }, { "version", 5 } },
            authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        result.GetProperty("status").GetString().Should().Be("success");
        await fraud.Received(1).RecordFraudSignalAsync(FarmerId, "CANCEL_AFTER_FUND_RATE", OrderId, "order",
            Arg.Is<string>(d => d.StartsWith("The farmer cancelled 2 of 5 funded orders")));
    }

    [Fact]
    public async Task FarmerCancel_OfACommittedOrder_FirstCancel_IsNotFlagged()
    {
        var fraud = WithPatternRules();
        _orders.QueryOrdersAsync(null, FarmerId, null, null, null, 50, null).Returns(History(
            PastOrder("o-1"), PastOrder("o-2"), PastOrder("o-3"), PastOrder("o-4")));
        _order = Order("accepted", version: 5, farmer: "accepted", trucker: "accepted", activeDeliveryId: DeliveryId);

        var result = await SendAsync(FarmerId, new Dictionary<string, object> { { "status", "cancelled" }, { "version", 5 } },
            authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        result.GetProperty("status").GetString().Should().Be("success");
        await fraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task FarmerCancel_InAwaitingTransport_IsNoFaultAndReadsNoHistory()
    {
        WithPatternRules();
        _order = Order(OrderCommitment.AwaitingTransportStatus, version: 4, farmer: "accepted", trucker: "expired", activeDeliveryId: null);

        var result = await SendAsync(FarmerId, new Dictionary<string, object> { { "status", "cancelled" }, { "version", 4 } });

        result.GetProperty("status").GetString().Should().Be("success");
        await _orders.DidNotReceiveWithAnyArgs().QueryOrdersAsync(default, default, default, default, default, default, default);
    }

    [Fact]
    public async Task BuyerCancel_InAwaitingTransport_IsNotTheCancelRule()
    {
        // The buyer's cancel follows the trucker's failure; the ESC-48 signal covers the buyer side.
        WithPatternRules();
        _order = Order(OrderCommitment.AwaitingTransportStatus, version: 4, farmer: "accepted", trucker: "declined", activeDeliveryId: null);

        await SendAsync(BuyerId, new Dictionary<string, object> { { "status", "cancelled" }, { "version", 4 } });

        await _orders.DidNotReceiveWithAnyArgs().QueryOrdersAsync(default, default, default, default, default, default, default);
    }

    [Fact]
    public async Task TruckerDropOut_TruckerWhoDropsOutOften_IsFlagged()
    {
        var fraud = WithPatternRules();
        _orders.QueryOrdersAsync(null, null, TruckerId, null, null, 50, null).Returns(History(
            PastOrder("o-1", OrderCommitment.AwaitingTransportStatus, truckerResponse: "cancelled"),
            PastOrder("o-2", truckerResponse: "accepted"), PastOrder("o-3", truckerResponse: "accepted"),
            PastOrder("o-4", truckerResponse: "accepted")));
        _order = Order("accepted", version: 5, farmer: "accepted", trucker: "accepted", activeDeliveryId: DeliveryId);

        var result = await SendAsync(TruckerId, new Dictionary<string, object> { { "truckerResponse", "decline" }, { "version", 5 } },
            authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        result.GetProperty("status").GetString().Should().Be("success");
        await fraud.Received(1).RecordFraudSignalAsync(TruckerId, "CANCEL_AFTER_FUND_RATE", OrderId, "order",
            Arg.Is<string>(d => d.StartsWith("The trucker cancelled 2 of 5 funded orders")));
    }

    [Fact]
    public async Task TruckerDecline_OfARequest_IsAnAnswerNotACancel()
    {
        WithPatternRules();
        _order = Order("funded", version: 3, farmer: "accepted", trucker: "pending");

        var result = await SendAsync(TruckerId, new Dictionary<string, object> { { "truckerResponse", "decline" }, { "version", 3 } });

        result.GetProperty("status").GetString().Should().Be("success");
        await _orders.DidNotReceiveWithAnyArgs().QueryOrdersAsync(default, default, default, default, default, default, default);
    }

    [Fact]
    public async Task TruckerDropOut_HistoryReadFails_TheDropOutStillStands()
    {
        var fraud = WithPatternRules();
        _orders.QueryOrdersAsync(null, null, TruckerId, null, null, 50, null)
            .ThrowsAsync(new ProvisionedThroughputExceededException("busy"));
        _order = Order("accepted", version: 5, farmer: "accepted", trucker: "accepted", activeDeliveryId: DeliveryId);

        var result = await SendAsync(TruckerId, new Dictionary<string, object> { { "truckerResponse", "decline" }, { "version", 5 } },
            authTime: DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        result.GetProperty("status").GetString().Should().Be("success");
        _written![OrderCommitment.TransportFailureField].Should().Be(OrderCommitment.TransportFailure.TruckerCancelled);
        await fraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }

    // ===== the payment gate =====

    [Fact]
    public async Task Payment_OnACancelledTrip_IsRefused()
    {
        _order = Order("created", version: 1);
        _trip = Trip(DateTime.UtcNow.AddDays(3), status: "cancelled");

        var result = await SendAsync(BuyerId, new Dictionary<string, object> { { "status", "payment_submitted" }, { "version", 1 } });

        result.GetProperty("message").GetString().Should().StartWith("ORDER_VALIDATION").And.Contain("no longer available");
        _written.Should().BeNull();
    }

    [Fact]
    public async Task Payment_OnATripLeavingWithin12Hours_IsRefused()
    {
        _order = Order("created", version: 1);
        _trip = Trip(DateTime.UtcNow.AddHours(6));

        var result = await SendAsync(BuyerId, new Dictionary<string, object> { { "status", "payment_submitted" }, { "version", 1 } });

        result.GetProperty("message").GetString().Should().StartWith("ORDER_VALIDATION").And.Contain("no longer available");
    }

    [Fact]
    public async Task Payment_OnAFullTrip_IsRefused()
    {
        _order = Order("created", version: 1);
        _trip = Trip(DateTime.UtcNow.AddDays(3), remaining: 2);

        var result = await SendAsync(BuyerId, new Dictionary<string, object> { { "status", "payment_submitted" }, { "version", 1 } });

        result.GetProperty("message").GetString().Should().StartWith("ORDER_VALIDATION").And.Contain("no longer available");
    }

    // ===== flag off =====

    [Fact]
    public async Task FlagOff_FarmerAccept_MovesTheOrderToAcceptedAsBefore()
    {
        var result = await SendAsync(FarmerId, new Dictionary<string, object> { { "status", "accepted" }, { "version", 2 } }, enabled: false);

        result.GetProperty("status").GetString().Should().Be("success");
        // The legacy path writes the request's JsonElement as it is; compare its text.
        _written!["status"].ToString().Should().Be("accepted");
        _written.Should().NotContainKey(OrderCommitment.FarmerResponseField);
    }

    [Fact]
    public async Task FlagOff_FarmerCommandShape_IsTranslatedToTheOldWrite()
    {
        var result = await SendAsync(FarmerId, new Dictionary<string, object> { { "farmerResponse", "accept" }, { "version", 2 } }, enabled: false);

        result.GetProperty("status").GetString().Should().Be("success");
        _written!["status"].ToString().Should().Be("accepted");
    }

    [Fact]
    public async Task FlagOff_TruckerCommand_IsNotEnabled()
    {
        _order = Order("funded", version: 3, farmer: "accepted", trucker: "pending");

        var result = await SendAsync(TruckerId, new Dictionary<string, object> { { "truckerResponse", "accept" }, { "version", 3 } }, enabled: false);

        result.GetProperty("message").GetString().Should().StartWith("ORDER_VALIDATION").And.Contain("not enabled");
    }

    [Fact]
    public async Task FlagOff_BuyerCancelOnFunded_StillAllowed()
    {
        var result = await SendAsync(BuyerId, new Dictionary<string, object> { { "status", "cancelled" }, { "version", 2 } }, enabled: false);

        result.GetProperty("status").GetString().Should().Be("success");
        _written!["status"].ToString().Should().Be("cancelled");
    }

    // ===== helpers =====

    private static Dictionary<string, object> Merge(Dictionary<string, object> row, Dictionary<string, object> fields, int version)
    {
        // The repository hands back DynamoDB strings; a request value is a JsonElement the
        // source-generated serializer cannot write back, so echo it as text.
        var merged = new Dictionary<string, object>(row);
        foreach (var (key, value) in fields)
        {
            merged[key] = value is JsonElement element ? element.ToString() : value;
        }

        merged["version"] = version.ToString();
        return merged;
    }

    private async Task<JsonElement> SendAsync(string userId, Dictionary<string, object> data, bool enabled = true, long? authTime = null)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId } };
        if (authTime != null)
        {
            authorizer["auth_time"] = authTime.Value.ToString();
        }

        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = "batch-esc-15",
                ClientId = "device-001",
                Operations = new List<SyncOperationRequest>
                {
                    new()
                    {
                        OperationId = "op-esc-15",
                        IdempotencyKey = $"{userId}:order:{OrderId}:update:{Guid.NewGuid():N}",
                        EntityType = "order",
                        EntityId = OrderId,
                        Action = "update",
                        Data = data
                    }
                }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer }
        };

        var response = await MakeFunction(enabled).FunctionHandler(request, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }

    private static Dictionary<string, object> Order(
        string status, int version, string? farmer = null, string? trucker = null, bool selfPickup = false,
        string? activeDeliveryId = null, DateTime? truckerDeadline = null)
    {
        var order = new Dictionary<string, object>
        {
            { "orderId", OrderId }, { "listingId", ListingId },
            { "buyerId", BuyerId }, { "farmerId", FarmerId },
            { "buyerName", "Meron Alemu" }, { "farmerName", "Abebe Kebede" },
            { "cropType", "teff" }, { "quantity", 500.0 }, { "pricePerUnit", 45.5 },
            { "cropCost", 22750.0 }, { "commissionRate", 0.05 }, { "commissionAmount", 1137.5 },
            { "orderTotal", 24187.5 },
            { "status", status }, { "version", version.ToString() },
            { "createdAt", DateTime.UtcNow.AddDays(-1).ToString("o") },
            { "paymentVerifiedAt", DateTime.UtcNow.AddHours(-2).ToString("o") }
        };
        if (!selfPickup)
        {
            order["tripId"] = TripId;
            order["truckerId"] = TruckerId;
            order["truckerName"] = "Tadesse Worku";
            order["transportCost"] = 300.0;
        }

        if (farmer != null)
        {
            order[OrderCommitment.FarmerResponseField] = farmer;
        }

        if (trucker != null)
        {
            order[OrderCommitment.TruckerResponseField] = trucker;
        }

        if (truckerDeadline != null)
        {
            order[OrderCommitment.TruckerDeadlineAtField] = truckerDeadline.Value.ToString("o");
        }

        if (activeDeliveryId != null)
        {
            order["activeDeliveryId"] = activeDeliveryId;
        }

        if (status == "accepted")
        {
            order[PickupDeadline.Field] = DateTime.UtcNow.AddDays(3).ToString("o");
        }

        return order;
    }

    private static Dictionary<string, AttributeValue> Trip(DateTime departure, string status = "open", double? remaining = null)
    {
        var trip = new Dictionary<string, AttributeValue>
        {
            { "tripId", new AttributeValue { S = TripId } },
            { "truckerId", new AttributeValue { S = TruckerId } },
            { "status", new AttributeValue { S = status } },
            { "capacity", new AttributeValue { N = "100" } },
            // Invariant culture: ':' is a culture-sensitive separator and a Finnish locale would write '.'.
            { "departureDate", new AttributeValue { S = departure.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture) } },
            { "createdAt", new AttributeValue { S = "2026-10-01T08:00:00.000Z" } }
        };
        if (remaining != null)
        {
            trip["remainingQuintals"] = new AttributeValue { N = remaining.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        }

        return trip;
    }

    private static Dictionary<string, object> Escrow(string status = "escrowed") => new()
    {
        { "escrowId", EscrowId }, { "orderId", OrderId }, { "source", "order" },
        { "buyerId", BuyerId }, { "farmerId", FarmerId },
        { "status", status }, { "amount", 24187.5m },
        { "createdAt", "2026-10-01T08:00:00.0000000Z" },
        { "expiresAt", "2026-10-08T08:00:00.0000000Z" }
    };
}
