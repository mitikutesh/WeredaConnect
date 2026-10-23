using Amazon.Lambda.TestUtilities;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// ACC-02: the deletion steps of account-deletion.md against an in-memory store: one success per
/// role, every blocker, the order of checks, the resume after a failure at any step, the step 9
/// abort, the sweep and reapply.
/// </summary>
public class AccountDeletionServiceTests
{
    private const string FarmerId = "farmer-1";
    private const string TruckerId = "trucker-1";
    private const string BuyerId = "buyer-1";
    private const string EndpointArn = "arn:aws:sns:eu-north-1:123:endpoint/GCM/wereda-connect-dev-fcm/abc";

    private static readonly DateTime Now = new(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc);

    private readonly FakeAccountDeletionRepository _repo = new();
    private readonly FakeTransactionAuditRepository _audit = new();
    private readonly ICognitoAuthService _cognito = Substitute.For<ICognitoAuthService>();
    private readonly IAmazonSimpleNotificationService _sns = Substitute.For<IAmazonSimpleNotificationService>();
    private readonly TestLambdaContext _context = new();
    private readonly HashSet<string> _cognitoUsersDeleted = new();
    private int _cognitoDeleteFailures;
    private int _snsFailures;
    // Ticks a millisecond per read, as a real clock moves between the steps of one run.
    private DateTime _now = Now;
    private readonly AccountDeletionService _service;

    public AccountDeletionServiceTests()
    {
        _cognito.AdminGlobalSignOutAsync(Arg.Any<string>()).Returns(AuthResult<bool>.Success(true));
        // Like Cognito: the first delete removes the user, later ones find no user.
        _cognito.AdminDeleteUserAsync(Arg.Any<string>()).Returns(ci =>
        {
            if (_cognitoDeleteFailures > 0)
            {
                _cognitoDeleteFailures--;
                return Task.FromResult(AuthResult<bool>.Failure(503, "AUTH_008", "unavailable"));
            }

            return Task.FromResult(_cognitoUsersDeleted.Add(ci.Arg<string>())
                ? AuthResult<bool>.Success(true)
                : AuthResult<bool>.Failure(404, "AUTH_013", "User not found."));
        });
        _sns.DeleteEndpointAsync(Arg.Any<DeleteEndpointRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (_snsFailures > 0)
                {
                    _snsFailures--;
                    return Task.FromException<DeleteEndpointResponse>(new AmazonSimpleNotificationServiceException("throttled"));
                }

                return Task.FromResult(new DeleteEndpointResponse());
            });
        _service = new AccountDeletionService(_repo, _cognito, _sns, _audit, () => _now = _now.AddMilliseconds(1));
    }

    private string DeletedAt(string userId) => _repo.UserRow(userId)["deletedAt"].S;

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    private static UserDto Live(string userId, string role, bool suspended = false) => new()
    {
        UserId = userId,
        PhoneNumber = "+251911000001",
        DisplayName = "Abebe",
        FullName = "Abebe Kebede",
        Role = role,
        Woreda = "Adama",
        FaydaId = "FAYDA-" + userId,
        FaydaVerified = true,
        FaydaVerifiedAt = "2026-02-01T00:00:00Z",
        IsPhoneVerified = true,
        Suspended = suspended,
        KnownDevices = ["device-1"],
        PreferredLanguage = "am",
        CreatedAt = "2026-01-01T00:00:00Z",
        UpdatedAt = "2026-01-01T00:00:00Z",
        Version = 3
    };

    private Task<AccountDeletionOutcome> Delete(
        string userId, bool confirmed = true, bool stepUpFresh = true, DeletionBudget? budget = null)
        => _service.RequestAsync(userId, confirmed, stepUpFresh,
            new AuditContext { Actor = userId, IpAddress = "203.0.113.7", CorrelationId = "corr-1" },
            budget ?? DeletionBudget.Unlimited, _context);

    private Dictionary<string, object> AddOrder(
        string orderId, string status, string buyerId = BuyerId, string farmerId = FarmerId,
        string? truckerId = null, Action<Dictionary<string, object>>? with = null)
    {
        var order = new Dictionary<string, object>
        {
            ["orderId"] = orderId,
            ["createdAt"] = "2026-10-01T00:00:00Z",
            ["status"] = status,
            ["buyerId"] = buyerId,
            ["buyerName"] = "Buyer B",
            ["farmerId"] = farmerId,
            ["farmerName"] = "Farmer F",
            ["listingId"] = "crop-1",
            ["version"] = "2"
        };
        if (truckerId != null)
        {
            order["truckerId"] = truckerId;
            order["truckerName"] = "Trucker T";
        }

        with?.Invoke(order);
        _repo.Orders.Add(order);
        return order;
    }

    private Dictionary<string, object> AddEscrow(string escrowId, string status, string role, string userId, bool truckerLegPending = false)
    {
        var escrow = new Dictionary<string, object>
        {
            ["escrowId"] = escrowId,
            ["createdAt"] = "2026-10-01T00:00:00Z",
            ["status"] = status,
            ["buyerId"] = role == "buyer" ? userId : "other-buyer",
            ["farmerId"] = role == "farmer" ? userId : "other-farmer",
            ["buyerName"] = "Buyer B",
            ["farmerName"] = "Farmer F",
            ["amount"] = "1500"
        };
        if (role == "trucker")
        {
            escrow["truckerId"] = userId;
        }

        if (truckerLegPending)
        {
            escrow["truckerPayoutPending"] = true;
        }

        _repo.Escrows.Add(escrow);
        return escrow;
    }

    private Dictionary<string, object> AddDelivery(string deliveryId, string status, string role, string userId)
    {
        var delivery = new Dictionary<string, object>
        {
            ["deliveryId"] = deliveryId,
            ["tripId"] = "trip-9",
            ["createdAt"] = "2026-10-02T00:00:00Z",
            ["status"] = status,
            ["orderId"] = "order-for-" + deliveryId,
            ["listingId"] = "crop-1",
            ["farmerId"] = role == "farmer" ? userId : "other-farmer",
            ["farmerName"] = "Farmer F",
            ["farmerPhone"] = "+251911000001",
            ["buyerId"] = role == "buyer" ? userId : "other-buyer",
            ["participantId"] = role == "buyer" ? userId : "other-buyer",
            ["buyerName"] = "Buyer B",
            ["buyerPhone"] = "+251911000003",
            ["gpsPickup"] = "9.0,38.7"
        };
        if (role == "trucker")
        {
            delivery["truckerId"] = userId;
            delivery["truckerName"] = "Trucker T";
            delivery["truckerPhone"] = "+251911000002";
        }

        _repo.Deliveries.Add(delivery);
        return delivery;
    }

    private void AssertTombstone(string userId)
    {
        var row = _repo.UserRow(userId);
        row.Keys.Should().BeSubsetOf(AccountDeletionPolicy.TombstoneAllowlist);
        row["deletionState"].S.Should().Be(AccountDeletionPolicy.Completed);
        row["deletedAt"].S.Should().NotBeNullOrEmpty();
        row.Should().NotContainKey("phoneNumber").And.NotContainKey("faydaId").And.NotContainKey("woreda");
    }

    private void AssertOneOfEachAuditRow(string userId)
    {
        var started = _audit.WithAction("account_deletion_started").Should().ContainSingle().Subject;
        started["transactionId"].Should().Be($"account-{userId}");
        started["timestamp"].Should().Be(_repo.UserRow(userId)["deletionRequestedAt"].S);
        var deleted = _audit.WithAction("account_deleted").Should().ContainSingle().Subject;
        deleted["transactionId"].Should().Be($"account-{userId}");
        deleted["timestamp"].Should().Be(DeletedAt(userId));
        deleted["entityType"].Should().Be("account");
        deleted["entityId"].Should().Be(userId);
        deleted["actor"].Should().Be(userId);
    }

    // ──────────────────────────────────────────────
    // Success, one per role
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Farmer_IsDeleted_ListingsWithdrawn_RowsAnonymized_PersonalDataGone()
    {
        _repo.AddUser(Live(FarmerId, "farmer"));
        _repo.Crops.Add(new Dictionary<string, object>
        {
            ["cropId"] = "crop-1", ["createdAt"] = "2026-09-01T00:00:00Z", ["farmerId"] = FarmerId, ["status"] = "available",
            ["farmerName"] = "Abebe", ["farmerPhone"] = "+251911000001", ["description"] = "Teff", ["latitude"] = "9.0",
            ["cropType"] = "teff", ["quantity"] = "20", ["version"] = "4"
        });
        _repo.Crops.Add(new Dictionary<string, object>
        {
            ["cropId"] = "crop-2", ["createdAt"] = "2026-08-01T00:00:00Z", ["farmerId"] = FarmerId, ["status"] = "sold",
            ["farmerName"] = "Abebe", ["farmerPhone"] = "+251911000001", ["version"] = "7"
        });
        AddOrder("order-1", "settled");
        AddEscrow("escrow-1", "completed", "farmer", FarmerId);
        AddDelivery("delivery-1", "delivered", "farmer", FarmerId);
        _repo.Ratings["order-1:" + FarmerId] = new Dictionary<string, object>
        {
            ["ratingId"] = "order-1:" + FarmerId, ["rateeId"] = FarmerId, ["stars"] = "4", ["comment"] = "call me on 0911"
        };
        _repo.Messages["msg-1"] = new Dictionary<string, object> { ["messageId"] = "msg-1", ["senderId"] = FarmerId, ["recipientId"] = BuyerId };
        _repo.Messages["msg-2"] = new Dictionary<string, object> { ["messageId"] = "msg-2", ["senderId"] = BuyerId, ["recipientId"] = FarmerId };
        _repo.Messages["msg-3"] = new Dictionary<string, object> { ["messageId"] = "msg-3", ["senderId"] = BuyerId, ["recipientId"] = TruckerId };
        _repo.DeviceTokens[FarmerId] = new List<string> { EndpointArn };

        var outcome = await Delete(FarmerId);

        outcome.Result.Should().Be(AccountDeletionResult.Completed);
        outcome.DeletedAt.Should().Be(DeletedAt(FarmerId));

        var available = _repo.Crops[0];
        available["status"].Should().Be("deleted");
        available.Should().ContainKey("deletedAt");
        available["farmerName"].Should().Be(AccountDeletionPolicy.Sentinel);
        available.Should().NotContainKey("farmerPhone").And.NotContainKey("description").And.NotContainKey("latitude");
        available["cropType"].Should().Be("teff");
        var sold = _repo.Crops[1];
        sold["status"].Should().Be("sold", "settled history keeps its status");
        sold.Should().ContainKey("deletedAt");
        sold.Should().NotContainKey("farmerPhone");

        _repo.Orders[0]["farmerName"].Should().Be(AccountDeletionPolicy.Sentinel);
        _repo.Orders[0]["buyerName"].Should().Be("Buyer B", "only the deleted user's copy is scrubbed");
        _repo.Orders[0]["status"].Should().Be("settled");
        _repo.Escrows[0]["farmerName"].Should().Be(AccountDeletionPolicy.Sentinel);
        _repo.Escrows[0]["amount"].Should().Be("1500");
        _repo.Deliveries[0]["farmerName"].Should().Be(AccountDeletionPolicy.Sentinel);
        _repo.Deliveries[0].Should().NotContainKey("farmerPhone");
        _repo.Deliveries[0]["buyerPhone"].Should().Be("+251911000003");
        _repo.Deliveries[0]["gpsPickup"].Should().Be("9.0,38.7", "evidence is kept");
        _repo.Ratings["order-1:" + FarmerId].Should().NotContainKey("comment");
        _repo.Ratings["order-1:" + FarmerId]["stars"].Should().Be("4");
        _repo.Messages.Keys.Should().BeEquivalentTo(new[] { "msg-3" });
        _repo.DeviceTokens[FarmerId].Should().BeEmpty();
        await _sns.Received(1).DeleteEndpointAsync(
            Arg.Is<DeleteEndpointRequest>(r => r.EndpointArn == EndpointArn), Arg.Any<CancellationToken>());

        AssertTombstone(FarmerId);
        await _cognito.Received(1).AdminGlobalSignOutAsync(FarmerId);
        await _cognito.Received(1).AdminDeleteUserAsync(FarmerId);
        AssertOneOfEachAuditRow(FarmerId);
        _audit.WithAction("account_deleted").Single()["metadata"].ToString()
            .Should().Contain("crops=2").And.Contain("messages=2").And.Contain("deviceTokens=1");
    }

    [Fact]
    public async Task Trucker_IsDeleted_OpenTripCancelled_DeliveredTripKept_TruckerCopiesScrubbed()
    {
        _repo.AddUser(Live(TruckerId, "trucker"));
        _repo.Trips.Add(new Dictionary<string, object>
        {
            ["tripId"] = "trip-1", ["createdAt"] = "2026-09-01T00:00:00Z", ["truckerId"] = TruckerId, ["status"] = "open",
            ["truckerName"] = "Kebede", ["truckerPhone"] = "+251911000002", ["vehiclePlate"] = "AA-123", ["version"] = "1"
        });
        _repo.Trips.Add(new Dictionary<string, object>
        {
            ["tripId"] = "trip-2", ["createdAt"] = "2026-08-01T00:00:00Z", ["truckerId"] = TruckerId, ["status"] = "delivered",
            ["truckerName"] = "Kebede", ["truckerPhone"] = "+251911000002", ["version"] = "5"
        });
        AddOrder("order-1", "settled", truckerId: TruckerId);
        AddEscrow("escrow-1", "completed", "trucker", TruckerId);
        AddDelivery("delivery-1", "delivered", "trucker", TruckerId);

        var outcome = await Delete(TruckerId);

        outcome.Result.Should().Be(AccountDeletionResult.Completed);
        _repo.Trips[0]["status"].Should().Be("cancelled");
        _repo.Trips[0].Should().ContainKey("deletedAt");
        _repo.Trips[0].Should().NotContainKey("truckerPhone").And.NotContainKey("vehiclePlate");
        _repo.Trips[1]["status"].Should().Be("delivered");
        _repo.Trips[1]["truckerName"].Should().Be(AccountDeletionPolicy.Sentinel);
        _repo.Orders[0]["truckerName"].Should().Be(AccountDeletionPolicy.Sentinel);
        _repo.Orders[0]["farmerName"].Should().Be("Farmer F");
        _repo.Deliveries[0]["truckerName"].Should().Be(AccountDeletionPolicy.Sentinel);
        _repo.Deliveries[0].Should().NotContainKey("truckerPhone");
        _repo.Deliveries[0]["farmerPhone"].Should().Be("+251911000001");
        AssertTombstone(TruckerId);
        await _cognito.Received(1).AdminDeleteUserAsync(TruckerId);
        AssertOneOfEachAuditRow(TruckerId);
    }

    [Fact]
    public async Task Buyer_IsDeleted_UnpaidOrdersCancelled_IntentsAndAuthoredCommentsGone()
    {
        _repo.AddUser(Live(BuyerId, "buyer"));
        AddOrder("order-new", "created");
        AddOrder("order-wait", "pending_payment");
        AddOrder("order-failed", "payment_failed");
        AddOrder("order-done", "settled", truckerId: TruckerId);
        _repo.Ratings[$"order-done:{FarmerId}"] = new Dictionary<string, object>
        {
            ["ratingId"] = $"order-done:{FarmerId}", ["rateeId"] = FarmerId, ["raterId"] = BuyerId, ["stars"] = "5", ["comment"] = "good"
        };
        _repo.Ratings[$"order-done:{TruckerId}"] = new Dictionary<string, object>
        {
            ["ratingId"] = $"order-done:{TruckerId}", ["rateeId"] = TruckerId, ["raterId"] = BuyerId, ["stars"] = "3", ["comment"] = "late"
        };
        _repo.Intents.Add((BuyerId, "intent-1"));
        _repo.Intents.Add(("buyer-2", "intent-9"));
        _repo.MatchEvents["intent-1"] = new List<string> { "event-1", "event-2" };

        var outcome = await Delete(BuyerId);

        outcome.Result.Should().Be(AccountDeletionResult.Completed);
        foreach (var orderId in new[] { "order-new", "order-wait", "order-failed" })
        {
            var order = _repo.Orders.Single(o => (string)o["orderId"] == orderId);
            order["status"].Should().Be("cancelled");
            order["cancelReason"].Should().Be(AccountDeletionPolicy.CancelReason);
            order["buyerName"].Should().Be(AccountDeletionPolicy.Sentinel);
        }

        _audit.WithAction("order_cancelled").Select(r => r["entityId"])
            .Should().BeEquivalentTo(new[] { "order-new", "order-wait", "order-failed" });
        _audit.WithAction("order_cancelled").Should().OnlyContain(r => (string)r["actor"] == "system");
        _repo.Orders.Single(o => (string)o["orderId"] == "order-done")["status"].Should().Be("settled");
        _repo.Ratings.Values.Should().OnlyContain(r => !r.ContainsKey("comment"));
        _repo.Ratings[$"order-done:{FarmerId}"]["stars"].Should().Be("5");
        _repo.Intents.Should().BeEquivalentTo(new[] { ("buyer-2", "intent-9") });
        _repo.MatchEvents["intent-1"].Should().BeEmpty();
        AssertTombstone(BuyerId);
        AssertOneOfEachAuditRow(BuyerId);
    }

    [Fact]
    public async Task RefreshTokensAreRevokedBeforeAnyListingOrOrderWrite()
    {
        _repo.AddUser(Live(FarmerId, "farmer"));
        _repo.Crops.Add(new Dictionary<string, object>
        {
            ["cropId"] = "crop-1", ["createdAt"] = "c", ["farmerId"] = FarmerId, ["status"] = "available", ["version"] = "1"
        });
        var signedOutBeforeWithdraw = false;
        _repo.BeforeCall = method =>
        {
            if (method == nameof(_repo.WithdrawCropAsync))
            {
                signedOutBeforeWithdraw = _cognito.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ICognitoAuthService.AdminGlobalSignOutAsync));
            }
        };

        (await Delete(FarmerId)).Result.Should().Be(AccountDeletionResult.Completed);

        signedOutBeforeWithdraw.Should().BeTrue();
        await _cognito.Received(1).AdminGlobalSignOutAsync(FarmerId);
    }

    // ──────────────────────────────────────────────
    // Order of checks
    // ──────────────────────────────────────────────

    [Fact]
    public async Task NoUsersRow_IsNotFound()
    {
        (await Delete("ghost")).Result.Should().Be(AccountDeletionResult.NotFound);
        _repo.Count(nameof(_repo.TryMarkInProgressAsync)).Should().Be(0);
    }

    [Fact]
    public async Task Suspended_IsRefused_BeforeTheBody()
    {
        _repo.AddUser(Live(FarmerId, "farmer", suspended: true));

        (await Delete(FarmerId, confirmed: false)).Result.Should().Be(AccountDeletionResult.Suspended);
        await _cognito.DidNotReceiveWithAnyArgs().AdminGlobalSignOutAsync(default!);
    }

    [Fact]
    public async Task UnconfirmedBody_IsInvalid_NothingWritten()
    {
        _repo.AddUser(Live(FarmerId, "farmer"));

        (await Delete(FarmerId, confirmed: false)).Result.Should().Be(AccountDeletionResult.InvalidBody);
        _repo.UserRow(FarmerId).Should().NotContainKey("deletionState");
    }

    [Fact]
    public async Task Blockers_AreAnswered_BeforeTheStepUp()
    {
        _repo.AddUser(Live(BuyerId, "buyer"));
        AddOrder("order-1", "funded");

        var outcome = await Delete(BuyerId, stepUpFresh: false);

        outcome.Result.Should().Be(AccountDeletionResult.Blocked);
    }

    [Fact]
    public async Task StaleSession_WithoutBlockers_NeedsStepUp_NothingWritten()
    {
        _repo.AddUser(Live(BuyerId, "buyer"));

        var outcome = await Delete(BuyerId, stepUpFresh: false);

        outcome.Result.Should().Be(AccountDeletionResult.StepUpRequired);
        _repo.UserRow(BuyerId).Should().NotContainKey("deletionState");
        _audit.Rows.Should().BeEmpty();
        await _cognito.DidNotReceiveWithAnyArgs().AdminGlobalSignOutAsync(default!);
    }

    [Fact]
    public async Task Completed_IsReplayed_WithTheStoredDeletedAt_NothingRuns()
    {
        _repo.AddUser(Live(FarmerId, "farmer"));
        var first = await Delete(FarmerId);
        first.Result.Should().Be(AccountDeletionResult.Completed);
        _cognito.ClearReceivedCalls();
        _now = Now.AddMinutes(5);

        var replay = await Delete(FarmerId, confirmed: false, stepUpFresh: false);

        replay.Result.Should().Be(AccountDeletionResult.Completed);
        replay.DeletedAt.Should().Be(first.DeletedAt);
        _cognito.ReceivedCalls().Should().BeEmpty();
        _audit.WithAction("account_deleted").Should().ContainSingle();
    }

    // ──────────────────────────────────────────────
    // Blockers
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("payment_submitted")]
    [InlineData("funded")]
    [InlineData("accepted")]
    [InlineData("pickup_confirmed")]
    [InlineData("in_transit")]
    [InlineData("delivered")]
    [InlineData("disbursing")]
    [InlineData("some_future_status")]
    public async Task ActiveOrder_Blocks_AsOrder_ForEveryParty(string status)
    {
        foreach (var (userId, role) in new[] { (BuyerId, "buyer"), (FarmerId, "farmer"), (TruckerId, "trucker") })
        {
            _repo.AddUser(Live(userId, role));
        }

        AddOrder("order-1", status, truckerId: TruckerId);

        foreach (var (userId, role) in new[] { (BuyerId, "buyer"), (FarmerId, "farmer"), (TruckerId, "trucker") })
        {
            var outcome = await Delete(userId);

            outcome.Result.Should().Be(AccountDeletionResult.Blocked);
            outcome.Blockers.Should().ContainSingle().Which.Should().BeEquivalentTo(new AccountDeletionBlocker
            {
                Type = "order", Id = "order-1", Status = status, Role = role
            });
            _repo.UserRow(userId).Should().NotContainKey("deletionState");
        }

        _audit.Rows.Should().BeEmpty();
        await _cognito.DidNotReceiveWithAnyArgs().AdminDeleteUserAsync(default!);
    }

    [Fact]
    public async Task DisputedOrder_BlocksAsDispute()
    {
        _repo.AddUser(Live(FarmerId, "farmer"));
        AddOrder("order-1", "disputed");

        var outcome = await Delete(FarmerId);

        outcome.Blockers.Should().ContainSingle().Which.Type.Should().Be("dispute");
    }

    [Fact]
    public async Task IndexLag_TheOrderIsReReadStronglyConsistent()
    {
        _repo.AddUser(Live(BuyerId, "buyer"));
        AddOrder("order-1", "funded");
        _repo.IndexStatus["order-1"] = "created";

        var outcome = await Delete(BuyerId);

        outcome.Result.Should().Be(AccountDeletionResult.Blocked);
        outcome.Blockers.Single().Status.Should().Be("funded");
    }

    [Theory]
    [InlineData("slot")]
    [InlineData("cbe")]
    [InlineData("telebirr")]
    public async Task PaymentFailedOrderOfTheBuyer_WithACheckoutOrPaymentReference_Blocks(string evidence)
    {
        _repo.AddUser(Live(BuyerId, "buyer"));
        AddOrder("order-1", "payment_failed", with: o =>
        {
            if (evidence == "cbe") o["cbeTransactionId"] = "CBE-1";
            if (evidence == "telebirr") o["telebirrMerchOrderId"] = "merch-1";
        });
        if (evidence == "slot")
        {
            _repo.CheckoutSlots.Add("order-1");
        }

        var outcome = await Delete(BuyerId);

        outcome.Result.Should().Be(AccountDeletionResult.Blocked);
        outcome.Blockers.Should().ContainSingle().Which.Should().BeEquivalentTo(new AccountDeletionBlocker
        {
            Type = "order", Id = "order-1", Status = "payment_failed", Role = "buyer"
        });
    }

    [Fact]
    public async Task PaymentFailedOrder_DoesNotBlockTheFarmer_AndIsCancelled()
    {
        _repo.AddUser(Live(FarmerId, "farmer"));
        AddOrder("order-1", "payment_failed", with: o => o["cbeTransactionId"] = "CBE-1");
        _repo.CheckoutSlots.Add("order-1");

        var outcome = await Delete(FarmerId);

        outcome.Result.Should().Be(AccountDeletionResult.Completed);
        _repo.Orders[0]["status"].Should().Be("cancelled");
        _repo.Orders[0]["cbeTransactionId"].Should().Be("CBE-1", "the buyer can still be refunded by hand");
    }

    [Theory]
    [InlineData("escrowed", "escrow")]
    [InlineData("released", "escrow")]
    [InlineData("held", "escrow")]
    [InlineData("dispute_resolved", "escrow")]
    [InlineData("disputed", "dispute")]
    public async Task UnreleasedEscrow_Blocks(string status, string type)
    {
        _repo.AddUser(Live(BuyerId, "buyer"));
        AddEscrow("escrow-1", status, "buyer", BuyerId);

        var outcome = await Delete(BuyerId);

        outcome.Result.Should().Be(AccountDeletionResult.Blocked);
        outcome.Blockers.Should().ContainSingle().Which.Should().BeEquivalentTo(new AccountDeletionBlocker
        {
            Type = type, Id = "escrow-1", Status = status, Role = "buyer"
        });
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("cancelled")]
    [InlineData("refunded")]
    public async Task FinishedEscrow_DoesNotBlock(string status)
    {
        _repo.AddUser(Live(FarmerId, "farmer"));
        AddEscrow("escrow-1", status, "farmer", FarmerId);

        (await Delete(FarmerId)).Result.Should().Be(AccountDeletionResult.Completed);
    }

    [Fact]
    public async Task OwedTruckerLeg_BlocksTheTrucker_EvenOnACompletedEscrow()
    {
        _repo.AddUser(Live(TruckerId, "trucker"));
        AddEscrow("escrow-1", "completed", "trucker", TruckerId, truckerLegPending: true);

        var outcome = await Delete(TruckerId);

        outcome.Blockers.Should().ContainSingle().Which.Should().BeEquivalentTo(new AccountDeletionBlocker
        {
            Type = "escrow", Id = "escrow-1", Status = "completed", Role = "trucker"
        });
    }

    [Theory]
    [InlineData("pending", "delivery")]
    [InlineData("pickedUp", "delivery")]
    [InlineData("inTransit", "delivery")]
    [InlineData("disputed", "dispute")]
    public async Task OpenDelivery_Blocks_ForEveryParty(string status, string type)
    {
        foreach (var (userId, role) in new[] { (BuyerId, "buyer"), (FarmerId, "farmer"), (TruckerId, "trucker") })
        {
            _repo.AddUser(Live(userId, role));
            _repo.Deliveries.Clear();
            AddDelivery("delivery-1", status, role, userId);

            var outcome = await Delete(userId);

            outcome.Result.Should().Be(AccountDeletionResult.Blocked);
            outcome.Blockers.Should().ContainSingle().Which.Should().BeEquivalentTo(new AccountDeletionBlocker
            {
                Type = type, Id = "delivery-1", Status = status, Role = role
            });
        }
    }

    [Fact]
    public async Task SoftDeletedDelivery_DoesNotBlock()
    {
        _repo.AddUser(Live(TruckerId, "trucker"));
        AddDelivery("delivery-1", "pending", "trucker", TruckerId)["deletedAt"] = "2026-10-03T00:00:00Z";

        (await Delete(TruckerId)).Result.Should().Be(AccountDeletionResult.Completed);
    }

    [Fact]
    public async Task LegacyDeliveryWithoutParticipantId_IsFoundThroughTheListing_AndBlocks()
    {
        _repo.AddUser(Live(BuyerId, "buyer"));
        AddOrder("order-1", "cancelled");
        var delivery = AddDelivery("delivery-1", "pending", "farmer", "other-farmer");
        delivery.Remove("participantId");
        delivery["buyerId"] = BuyerId;

        var outcome = await Delete(BuyerId);

        outcome.Blockers.Should().ContainSingle().Which.Should().BeEquivalentTo(new AccountDeletionBlocker
        {
            Type = "delivery", Id = "delivery-1", Status = "pending", Role = "buyer"
        });
    }

    // FUP-63: payment_review goes through UnappliedPayments with the order's funded ids.

    private static Dictionary<string, object> PaymentRow(string action, string timestamp, string? paymentOrderId, string metadata)
    {
        var row = new Dictionary<string, object>
        {
            ["escrowId"] = "escrow-order-order-1",
            ["timestamp"] = timestamp,
            ["action"] = action,
            ["metadata"] = metadata
        };
        if (paymentOrderId != null)
        {
            row["telebirrRef"] = paymentOrderId;
        }

        return row;
    }

    [Fact]
    public async Task UnappliedPayment_BlocksTheBuyer_AsPaymentReview_OnACancelledOrder()
    {
        _repo.AddUser(Live(BuyerId, "buyer"));
        AddOrder("order-1", "cancelled");
        _repo.CheckoutSlots.Add("order-1");
        _repo.PaymentTrails["order-1"] = new List<Dictionary<string, object>>
        {
            PaymentRow("order_payment_unmatched", "2026-10-02T00:00:00Z", "pay-1", "reason=status,merchOrderId=merch-1,amount=1500")
        };

        var outcome = await Delete(BuyerId);

        outcome.Result.Should().Be(AccountDeletionResult.Blocked);
        outcome.Blockers.Should().ContainSingle().Which.Should().BeEquivalentTo(new AccountDeletionBlocker
        {
            Type = "payment_review", Id = "order-1", Status = "order_payment_unmatched", Role = "buyer"
        });
    }

    [Fact]
    public async Task RefundedPayment_NoLongerBlocks_ButASecondPaymentOnTheOrderDoes()
    {
        _repo.AddUser(Live(BuyerId, "buyer"));
        AddOrder("order-1", "cancelled");
        _repo.CheckoutSlots.Add("order-1");
        _repo.PaymentTrails["order-1"] = new List<Dictionary<string, object>>
        {
            PaymentRow("order_payment_unmatched", "2026-10-02T00:00:00Z", "pay-1", "reason=status,merchOrderId=merch-1"),
            PaymentRow("order_payment_duplicate", "2026-10-02T01:00:00Z", "pay-2", "merchOrderId=merch-2"),
            PaymentRow("order_payment_refunded_manual", "2026-10-03T00:00:00Z", "refund-tx-1",
                UnappliedPayments.RefundMetadata("pay-1", 1500m, "merch-1"))
        };

        var outcome = await Delete(BuyerId);

        outcome.Blockers.Should().ContainSingle().Which.Status.Should().Be("order_payment_duplicate");

        _repo.PaymentTrails["order-1"].Add(PaymentRow("order_payment_refunded_manual", "2026-10-04T00:00:00Z", "refund-tx-2",
            UnappliedPayments.RefundMetadata("pay-2", 1500m, "merch-2")));

        (await Delete(BuyerId)).Result.Should().Be(AccountDeletionResult.Completed);
    }

    [Fact]
    public async Task PaymentThatFundedTheOrder_NeverBlocksAsPaymentReview()
    {
        _repo.AddUser(Live(BuyerId, "buyer"));
        AddOrder("order-1", "settled", with: o => o[OrderFundingService.PaymentOrderIdField] = "pay-1");
        _repo.CheckoutSlots.Add("order-1");
        _repo.PaymentTrails["order-1"] = new List<Dictionary<string, object>>
        {
            PaymentRow("order_payment_unprocessed", "2026-10-02T00:00:00Z", "pay-1", "merchOrderId=merch-1")
        };

        (await Delete(BuyerId)).Result.Should().Be(AccountDeletionResult.Completed);
    }

    [Fact]
    public async Task PaymentReview_IsOnlyCheckedForOrdersWithACheckoutSlot()
    {
        _repo.AddUser(Live(BuyerId, "buyer"));
        AddOrder("order-1", "cancelled");

        (await Delete(BuyerId)).Result.Should().Be(AccountDeletionResult.Completed);
        _repo.Count(nameof(_repo.GetOrderPaymentTrailAsync)).Should().Be(0);
    }

    // ──────────────────────────────────────────────
    // Idempotent resume
    // ──────────────────────────────────────────────

    public static IEnumerable<object[]> FailingSteps => new[]
    {
        new object[] { "audit_started", AccountDeletionPolicy.InProgress },
        new object[] { "sign_out", AccountDeletionPolicy.InProgress },
        new object[] { nameof(IFailing.WithdrawCropAsync), AccountDeletionPolicy.InProgress },
        new object[] { nameof(IFailing.WithdrawTripAsync), AccountDeletionPolicy.InProgress },
        new object[] { nameof(IFailing.TryCancelUnpaidOrderAsync), AccountDeletionPolicy.InProgress },
        new object[] { "step9_scan", AccountDeletionPolicy.InProgress },
        new object[] { nameof(IFailing.ScrubOrderAsync), AccountDeletionPolicy.InProgress },
        new object[] { nameof(IFailing.ScrubDeliveryAsync), AccountDeletionPolicy.InProgress },
        new object[] { nameof(IFailing.RemoveRatingCommentAsync), AccountDeletionPolicy.InProgress },
        new object[] { nameof(IFailing.DeleteMessageAsync), AccountDeletionPolicy.InProgress },
        new object[] { nameof(IFailing.DeleteMatchEventAsync), AccountDeletionPolicy.InProgress },
        new object[] { "sns", AccountDeletionPolicy.InProgress },
        new object[] { nameof(IFailing.TombstoneAsync), AccountDeletionPolicy.InProgress },
        new object[] { "cognito_delete", AccountDeletionPolicy.CognitoPending },
        new object[] { "audit_deleted", AccountDeletionPolicy.CognitoPending },
        new object[] { nameof(IFailing.MarkCompletedAsync), AccountDeletionPolicy.CognitoPending }
    };

    /// <summary>Only for nameof: the fake's methods that a test can make fail.</summary>
    private interface IFailing
    {
        Task WithdrawCropAsync();
        Task WithdrawTripAsync();
        Task TryCancelUnpaidOrderAsync();
        Task ScrubOrderAsync();
        Task ScrubDeliveryAsync();
        Task RemoveRatingCommentAsync();
        Task DeleteMessageAsync();
        Task DeleteMatchEventAsync();
        Task TombstoneAsync();
        Task MarkCompletedAsync();
    }

    [Theory]
    [MemberData(nameof(FailingSteps))]
    public async Task AFailureAtAnyStep_Answers503_AndARetryResumesAndCompletesOnce(string step, string stateAfterFailure)
    {
        // A user who is a party in every role, so every step has a row to work on.
        _repo.AddUser(Live(FarmerId, "farmer"));
        _repo.Crops.Add(new Dictionary<string, object>
        {
            ["cropId"] = "crop-1", ["createdAt"] = "c", ["farmerId"] = FarmerId, ["status"] = "available",
            ["farmerPhone"] = "+251911000001", ["version"] = "1"
        });
        _repo.Trips.Add(new Dictionary<string, object>
        {
            ["tripId"] = "trip-1", ["createdAt"] = "c", ["truckerId"] = FarmerId, ["status"] = "open",
            ["truckerPhone"] = "+251911000001", ["version"] = "1"
        });
        AddOrder("order-unpaid", "created");
        AddOrder("order-settled", "settled");
        AddDelivery("delivery-1", "delivered", "farmer", FarmerId);
        _repo.Ratings["order-settled:" + FarmerId] = new Dictionary<string, object>
        {
            ["ratingId"] = "order-settled:" + FarmerId, ["rateeId"] = FarmerId, ["stars"] = "4", ["comment"] = "ok"
        };
        _repo.Messages["msg-1"] = new Dictionary<string, object> { ["messageId"] = "msg-1", ["senderId"] = FarmerId, ["recipientId"] = BuyerId };
        _repo.Intents.Add((FarmerId, "intent-1"));
        _repo.MatchEvents["intent-1"] = new List<string> { "event-1" };
        _repo.DeviceTokens[FarmerId] = new List<string> { EndpointArn };

        switch (step)
        {
            case "audit_started":
            case "audit_deleted":
                // The started row is the first checked write; the deleted row the second.
                if (step == "audit_started")
                {
                    _audit.FailNextCheckedWrite();
                }
                else
                {
                    _repo.BeforeCall = m =>
                    {
                        if (m == nameof(_repo.TombstoneAsync))
                        {
                            _audit.FailNextCheckedWrite();
                        }
                    };
                }

                break;
            case "step9_scan":
                // The first escrow query is step 3's; fail the one after the deletion started.
                _repo.BeforeCall = m =>
                {
                    if (m == nameof(_repo.TryMarkInProgressAsync))
                    {
                        _repo.FailOnce(nameof(_repo.QueryEscrowsAsync));
                    }
                };
                break;
            case "sign_out":
                _cognito.AdminGlobalSignOutAsync(FarmerId).Returns(
                    AuthResult<bool>.Failure(503, "AUTH_008", "unavailable"), AuthResult<bool>.Success(true));
                break;
            case "cognito_delete":
                _cognitoDeleteFailures = 1;
                break;
            case "sns":
                _snsFailures = 1;
                break;
            default:
                _repo.FailOnce(step);
                break;
        }

        var first = await Delete(FarmerId);

        first.Result.Should().Be(AccountDeletionResult.Incomplete);
        first.DeletionState.Should().Be(stateAfterFailure);
        Logs.Should().Contain("ACCOUNT_DELETION_INCOMPLETE");

        // The retry needs no new step-up and no body: the started deletion resumes.
        _now = _now.AddSeconds(3);
        var retry = await Delete(FarmerId, confirmed: false, stepUpFresh: false);

        retry.Result.Should().Be(AccountDeletionResult.Completed);
        _repo.UserRow(FarmerId)["deletionState"].S.Should().Be(AccountDeletionPolicy.Completed);
        _repo.UserRow(FarmerId).Keys.Should().BeSubsetOf(AccountDeletionPolicy.TombstoneAllowlist);
        _audit.WithAction("account_deletion_started").Should().ContainSingle();
        _audit.WithAction("account_deleted").Should().ContainSingle();
        _audit.WithAction("order_cancelled").Should().ContainSingle();
        _repo.Orders.Single(o => (string)o["orderId"] == "order-unpaid")["status"].Should().Be("cancelled");
        _repo.Crops[0]["status"].Should().Be("deleted");
        _repo.Trips[0]["status"].Should().Be("cancelled");
        _repo.Messages.Should().BeEmpty();
        _repo.Intents.Should().BeEmpty();
        _repo.DeviceTokens[FarmerId].Should().BeEmpty();
        _repo.Ratings.Values.Should().OnlyContain(r => !r.ContainsKey("comment"));
        _repo.Deliveries[0].Should().NotContainKey("farmerPhone");
        _cognitoUsersDeleted.Should().BeEquivalentTo(new[] { FarmerId }, "the Cognito user is deleted, once");

        // A third call is a replay.
        (await Delete(FarmerId)).Result.Should().Be(AccountDeletionResult.Completed);
        _audit.WithAction("account_deleted").Should().ContainSingle();
    }

    [Fact]
    public async Task CognitoUserAlreadyGone_CountsAsDone()
    {
        _repo.AddUser(Live(FarmerId, "farmer"));
        _cognito.AdminDeleteUserAsync(FarmerId).Returns(AuthResult<bool>.Failure(404, "AUTH_013", "User not found."));
        _cognito.AdminGlobalSignOutAsync(FarmerId).Returns(AuthResult<bool>.Failure(404, "AUTH_013", "User not found."));

        (await Delete(FarmerId)).Result.Should().Be(AccountDeletionResult.Completed);
    }

    [Fact]
    public async Task RunningOutOfTime_StopsBetweenWrites_AndTheRetryFinishes()
    {
        _repo.AddUser(Live(FarmerId, "farmer"));
        for (var i = 0; i < 5; i++)
        {
            _repo.Messages[$"msg-{i}"] = new Dictionary<string, object> { ["messageId"] = $"msg-{i}", ["senderId"] = FarmerId };
        }

        // Time runs out once two messages are gone.
        var budget = new DeletionBudget(() => _repo.Count(nameof(_repo.DeleteMessageAsync)) >= 2);

        var first = await Delete(FarmerId, budget: budget);

        first.Result.Should().Be(AccountDeletionResult.Incomplete);
        first.DeletionState.Should().Be(AccountDeletionPolicy.InProgress);
        _repo.Messages.Should().NotBeEmpty("the run stopped before it finished");

        (await Delete(FarmerId, stepUpFresh: false)).Result.Should().Be(AccountDeletionResult.Completed);
        _repo.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task ABlockerThatAppearsAfterTheFirstCheck_AbortsAtStep9_AndTheAccountStaysLive()
    {
        _repo.AddUser(Live(FarmerId, "farmer"));
        _repo.Crops.Add(new Dictionary<string, object>
        {
            ["cropId"] = "crop-1", ["createdAt"] = "c", ["farmerId"] = FarmerId, ["status"] = "available", ["version"] = "1"
        });
        // A buyer pays an order of this farmer right after the deletion started.
        _repo.BeforeCall = m =>
        {
            if (m == nameof(_repo.TryMarkInProgressAsync))
            {
                AddOrder("order-1", "funded");
            }
        };

        var outcome = await Delete(FarmerId);

        outcome.Result.Should().Be(AccountDeletionResult.Blocked);
        outcome.Blockers.Should().ContainSingle().Which.Id.Should().Be("order-1");
        var row = _repo.UserRow(FarmerId);
        row.Should().NotContainKey("deletionState").And.NotContainKey("deletionRequestedAt");
        row["phoneNumber"].S.Should().Be("+251911000001");
        _repo.Crops[0]["status"].Should().Be("deleted", "withdrawn listings stay withdrawn");
        _audit.WithAction("account_deletion_aborted").Should().ContainSingle();
        await _cognito.DidNotReceiveWithAnyArgs().AdminDeleteUserAsync(default!);
    }

    [Fact]
    public async Task AnUnpaidOrderPaidDuringStep8_IsABlocker_AndIsNotCancelled()
    {
        _repo.AddUser(Live(FarmerId, "farmer"));
        var order = AddOrder("order-1", "created");
        _repo.BeforeCall = m =>
        {
            if (m == nameof(_repo.TryCancelUnpaidOrderAsync))
            {
                order["status"] = "payment_submitted";
                order["version"] = "3";
            }
        };

        var outcome = await Delete(FarmerId);

        outcome.Result.Should().Be(AccountDeletionResult.Blocked);
        order["status"].Should().Be("payment_submitted");
        _repo.UserRow(FarmerId).Should().NotContainKey("deletionState");
    }

    [Fact]
    public async Task UnknownDeletionState_IsNeverActedOn()
    {
        _repo.AddUser(Live(FarmerId, "farmer"));
        _repo.UserRow(FarmerId)["deletionState"] = new Amazon.DynamoDBv2.Model.AttributeValue { S = "weird" };

        (await Delete(FarmerId)).Result.Should().Be(AccountDeletionResult.Incomplete);
        await _cognito.DidNotReceiveWithAnyArgs().AdminGlobalSignOutAsync(default!);
    }

    // ──────────────────────────────────────────────
    // Completion sweep
    // ──────────────────────────────────────────────

    private void StartedAt(string userId, string state, DateTime requestedAt)
    {
        var row = _repo.UserRow(userId);
        row["deletionState"] = new Amazon.DynamoDBv2.Model.AttributeValue { S = state };
        row["deletionRequestedAt"] = new Amazon.DynamoDBv2.Model.AttributeValue { S = requestedAt.ToString("o") };
        if (state == AccountDeletionPolicy.CognitoPending)
        {
            row["deletedAt"] = new Amazon.DynamoDBv2.Model.AttributeValue { S = requestedAt.AddSeconds(5).ToString("o") };
        }
    }

    [Fact]
    public async Task Sweep_FinishesStartedDeletions_OlderThanAnHour_AsTheSystem()
    {
        _repo.AddUser(Live(FarmerId, "farmer"));
        _repo.AddUser(Live(TruckerId, "trucker"));
        _repo.AddUser(Live(BuyerId, "buyer"));
        StartedAt(FarmerId, AccountDeletionPolicy.InProgress, Now.AddHours(-2));
        StartedAt(TruckerId, AccountDeletionPolicy.CognitoPending, Now.AddHours(-3));
        StartedAt(BuyerId, AccountDeletionPolicy.InProgress, Now.AddMinutes(-10));
        AddOrder("order-x", "created", buyerId: "someone", farmerId: FarmerId);

        var summary = await _service.SweepAsync(DeletionBudget.Unlimited, _context);

        summary.Should().Be(new AccountDeletionService.SweepSummary(2, 0, 0, false));
        _repo.UserRow(FarmerId)["deletionState"].S.Should().Be(AccountDeletionPolicy.Completed);
        _repo.UserRow(TruckerId)["deletionState"].S.Should().Be(AccountDeletionPolicy.Completed);
        _repo.UserRow(BuyerId)["deletionState"].S.Should().Be(AccountDeletionPolicy.InProgress, "a recent request is left to its client");
        _repo.Orders[0]["status"].Should().Be("cancelled");
        _audit.WithAction("order_cancelled").Single()["actor"].Should().Be("system");
        Logs.Should().Contain("ACCOUNT_DELETION_INCOMPLETE").And.Contain("sweep=finished");
    }

    [Fact]
    public async Task Sweep_AbortsAStartedDeletionThatBecameBlocked()
    {
        _repo.AddUser(Live(FarmerId, "farmer"));
        StartedAt(FarmerId, AccountDeletionPolicy.InProgress, Now.AddHours(-2));
        AddOrder("order-1", "funded");

        var summary = await _service.SweepAsync(DeletionBudget.Unlimited, _context);

        summary.Aborted.Should().Be(1);
        _repo.UserRow(FarmerId).Should().NotContainKey("deletionState");
        Logs.Should().Contain("sweep=aborted");
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(14, true)]
    public async Task Sweep_LogsStuck_OnlyForARowTheLastRunHadToo(int ageHours, bool stuck)
    {
        _repo.AddUser(Live(FarmerId, "farmer"));
        StartedAt(FarmerId, AccountDeletionPolicy.CognitoPending, Now.AddHours(-ageHours));
        _cognito.AdminDeleteUserAsync(FarmerId).Returns(AuthResult<bool>.Failure(503, "AUTH_008", "unavailable"));

        var summary = await _service.SweepAsync(DeletionBudget.Unlimited, _context);

        summary.Failed.Should().Be(1);
        if (stuck)
        {
            Logs.Should().Contain("ACCOUNT_DELETION_STUCK");
        }
        else
        {
            Logs.Should().NotContain("ACCOUNT_DELETION_STUCK");
        }
    }

    [Fact]
    public async Task Sweep_StopsWhenTheInvocationRunsLow()
    {
        _repo.AddUser(Live(FarmerId, "farmer"));
        StartedAt(FarmerId, AccountDeletionPolicy.InProgress, Now.AddHours(-2));

        var summary = await _service.SweepAsync(new DeletionBudget(() => true), _context);

        summary.Truncated.Should().BeTrue();
        _repo.UserRow(FarmerId)["deletionState"].S.Should().Be(AccountDeletionPolicy.InProgress);
    }

    // ──────────────────────────────────────────────
    // reapply (after a backup restore)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Reapply_ErasesRestoredData_KeepsCompleted_AndWritesNoSecondAccountDeleted()
    {
        _repo.AddUser(Live(FarmerId, "farmer"));
        var deletion = await Delete(FarmerId);
        deletion.Result.Should().Be(AccountDeletionResult.Completed);

        // The restore brought back the profile, a message, a device token and the crop's phone.
        _repo.AddUser(Live(FarmerId, "farmer"));
        _repo.Messages["msg-9"] = new Dictionary<string, object> { ["messageId"] = "msg-9", ["senderId"] = FarmerId };
        _repo.DeviceTokens[FarmerId] = new List<string> { EndpointArn };
        _repo.Crops.Add(new Dictionary<string, object>
        {
            ["cropId"] = "crop-1", ["createdAt"] = "c", ["farmerId"] = FarmerId, ["status"] = "sold",
            ["farmerName"] = "Abebe", ["farmerPhone"] = "+251911000001", ["version"] = "2"
        });
        _now = Now.AddDays(3);

        (await _service.ReapplyAsync(FarmerId, DeletionBudget.Unlimited, _context)).Should().BeTrue();

        var row = _repo.UserRow(FarmerId);
        row.Keys.Should().BeSubsetOf(AccountDeletionPolicy.TombstoneAllowlist);
        row["deletionState"].S.Should().Be(AccountDeletionPolicy.Completed);
        row["deletedAt"].S.Should().Be(deletion.DeletedAt, "the original deletion time is kept");
        _repo.Messages.Should().BeEmpty();
        _repo.DeviceTokens[FarmerId].Should().BeEmpty();
        _repo.Crops[0].Should().NotContainKey("farmerPhone");
        _repo.Crops[0]["status"].Should().Be("sold");
        _audit.WithAction("account_deleted").Should().ContainSingle();
        await _cognito.Received(1).AdminDeleteUserAsync(FarmerId);
    }

    [Fact]
    public async Task Reapply_NeverTouchesALiveAccount()
    {
        _repo.AddUser(Live(FarmerId, "farmer"));
        _repo.Messages["msg-1"] = new Dictionary<string, object> { ["messageId"] = "msg-1", ["senderId"] = FarmerId };

        (await _service.ReapplyAsync(FarmerId, DeletionBudget.Unlimited, _context)).Should().BeFalse();

        _repo.Messages.Should().ContainKey("msg-1");
        _repo.UserRow(FarmerId)["phoneNumber"].S.Should().Be("+251911000001");
    }
}
