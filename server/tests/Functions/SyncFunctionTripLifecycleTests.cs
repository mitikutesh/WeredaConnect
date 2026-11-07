using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-17 (design 4.10) through <c>/sync</c>, on the delivery rig of <see cref="SyncFunctionTests"/>:
/// an <c>inTransit</c> trip becomes <c>delivered</c> when its last delivery is done (or the last
/// open one is cancelled and another was delivered), never while a delivery is still active; and a
/// change to a trip the server already closed or finished answers the stored row as a conflict.
/// </summary>
public partial class SyncFunctionTests
{
    private const string Esc17TripCreatedAt = "2026-10-20T08:00:00.000Z";

    private readonly ITripRepository _esc17Trips = Substitute.For<ITripRepository>();
    private readonly IDeliveryRepository _esc17Deliveries = Substitute.For<IDeliveryRepository>();

    private SyncFunction Esc17Function(bool enabled = true)
        => new(_mockSyncMeta, _mockCropRepo, _mockDynamoDb, "test-trips-table", "test-deliveries-table",
            userRepo: _mockUserRepo, orderRepo: _mockOrderRepo, deliveryRepo: _esc17Deliveries, tripRepo: _esc17Trips)
        {
            CommitmentsEnabled = enabled
        };

    private void Esc17Trip(string status, int version = 6)
        => _esc17Trips.GetRawItemAsync("trip-001").Returns(new Dictionary<string, AttributeValue>
        {
            { "tripId", new AttributeValue { S = "trip-001" } },
            { "createdAt", new AttributeValue { S = Esc17TripCreatedAt } },
            { "truckerId", new AttributeValue { S = "trucker-001" } },
            { "status", new AttributeValue { S = status } },
            { "version", new AttributeValue { N = version.ToString() } }
        });

    /// <summary>What the deliveries TripIdIndex returns for trip-001 (it may lag the write).</summary>
    private void Esc17DeliveriesOnTrip(params (string Id, string Status)[] deliveries)
        => _esc17Deliveries.QueryDeliveriesAsync(Arg.Any<string?>(), "trip-001", Arg.Any<string?>(), Arg.Any<int>(),
                Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((deliveries
                .Select(d => new Dictionary<string, object> { { "deliveryId", d.Id }, { "tripId", "trip-001" }, { "status", d.Status } })
                .ToList(), (Dictionary<string, AttributeValue>?)null));

    /// <summary>The deliveries TripIdIndex with each delivery's order.</summary>
    private void Esc17DeliveriesWithOrders(params (string Id, string Status, string OrderId)[] deliveries)
        => _esc17Deliveries.QueryDeliveriesAsync(Arg.Any<string?>(), "trip-001", Arg.Any<string?>(), Arg.Any<int>(),
                Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((deliveries
                .Select(d => new Dictionary<string, object>
                {
                    { "deliveryId", d.Id }, { "tripId", "trip-001" }, { "status", d.Status }, { "orderId", d.OrderId }
                })
                .ToList(), (Dictionary<string, AttributeValue>?)null));

    /// <summary>What the orders TripIdIndex returns for trip-001 (it may lag too).</summary>
    private void Esc17OrdersOnTrip(params (string Id, string Status)[] orders)
        => _mockOrderRepo.QueryByTripAsync("trip-001").Returns(orders
            .Select(o => new Dictionary<string, object> { { "orderId", o.Id }, { "tripId", "trip-001" }, { "status", o.Status } })
            .ToList());

    private async Task<JsonElement> Esc17SendAsync(SyncFunction function, SyncOperationRequest op, string userId, string role)
    {
        var response = await function.FunctionHandler(MakeAuthenticatedRequest(MakeSyncRequest(op), userId, role), _context);
        Assert.Equal(200, response.StatusCode);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }

    /// <summary>The buyer confirms del-001 (picked up, with its pickup nonce on the row).</summary>
    private Task<JsonElement> Esc17ConfirmAsync(SyncFunction function)
    {
        SetupExistingDeliveryWithQrNonce("del-001", "trucker-001", "pickedUp", 1, "nonce-esc17");
        return Esc17SendAsync(function, new SyncOperationRequest
        {
            OperationId = "op-esc17-deliver",
            IdempotencyKey = "buyer-001:delivery:del-001:update:esc17",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "update",
            Data = new Dictionary<string, object>
            {
                { "status", "delivered" }, { "version", 1 }, { "scannedQrNonce", "nonce-esc17" }
            }
        }, "buyer-001", "buyer");
    }

    /// <summary>The trucker cancels del-001 (pending) with the legacy delete.</summary>
    private Task<JsonElement> Esc17CancelAsync(SyncFunction function)
    {
        SetupExistingDelivery("del-001", "trucker-001", "pending", 1);
        return Esc17SendAsync(function, new SyncOperationRequest
        {
            OperationId = "op-esc17-cancel",
            IdempotencyKey = "trucker-001:delivery:del-001:delete:esc17",
            EntityType = "delivery",
            EntityId = "del-001",
            Action = "delete",
            Data = new Dictionary<string, object> { { "version", 1 } }
        }, "trucker-001", "trucker");
    }

    private Task Esc17MarkedDelivered(int times = 1)
        => _esc17Trips.Received(times).TryMarkDeliveredAsync("trip-001", Esc17TripCreatedAt, 6, Arg.Any<string>());

    private Task Esc17NotMarked()
        => _esc17Trips.DidNotReceiveWithAnyArgs().TryMarkDeliveredAsync(default!, default!, default, default!);

    private string Esc17Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    // ===== the trip follows its last delivery =====

    [Fact]
    public async Task Esc17_LastDeliveryConfirmed_OtherCancelled_TripBecomesDelivered()
    {
        Esc17Trip("inTransit");
        _esc17Trips.TryMarkDeliveredAsync(default!, default!, default, default!).ReturnsForAnyArgs(true);
        // The index still shows del-001 picked up: the write this request made counts anyway.
        Esc17DeliveriesOnTrip(("del-001", "pickedUp"), ("del-002", "cancelled"));

        var result = await Esc17ConfirmAsync(Esc17Function());

        Assert.Equal("success", result.GetProperty("status").GetString());
        await Esc17MarkedDelivered();
        Assert.Contains("inTransit -> delivered (all 2 deliveries done or cancelled)", Esc17Logs);
    }

    [Fact]
    public async Task Esc17_DisputedAndDeliveredDeliveries_AreDone()
    {
        Esc17Trip("inTransit");
        Esc17DeliveriesOnTrip(("del-001", "pickedUp"), ("del-002", "disputed"));

        await Esc17ConfirmAsync(Esc17Function());

        await Esc17MarkedDelivered();
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("pickedUp")]
    [InlineData("inTransit")]
    public async Task Esc17_AnotherDeliveryStillActive_TripStaysInTransit(string otherStatus)
    {
        Esc17Trip("inTransit");
        Esc17DeliveriesOnTrip(("del-001", "pickedUp"), ("del-002", otherStatus));

        var result = await Esc17ConfirmAsync(Esc17Function());

        Assert.Equal("success", result.GetProperty("status").GetString());
        await Esc17NotMarked();
    }

    [Theory]
    [InlineData("open")]
    [InlineData("matched")]
    [InlineData("delivered")]
    [InlineData("cancelled")]
    public async Task Esc17_TripNotInTransit_IsLeftAsItIs(string tripStatus)
    {
        Esc17Trip(tripStatus);
        Esc17DeliveriesOnTrip(("del-001", "pickedUp"));

        await Esc17ConfirmAsync(Esc17Function());

        await Esc17NotMarked();
        await _esc17Deliveries.DidNotReceiveWithAnyArgs().QueryDeliveriesAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task Esc17_CancellingTheLastPendingDelivery_FinishesATripThatDeliveredTheOther()
    {
        Esc17Trip("inTransit");
        Esc17DeliveriesOnTrip(("del-001", "pending"), ("del-002", "delivered"));

        var result = await Esc17CancelAsync(Esc17Function());

        Assert.Equal("success", result.GetProperty("status").GetString());
        await Esc17MarkedDelivered();
    }

    [Fact]
    public async Task Esc17_TripWhoseOnlyDeliveryIsCancelled_IsNotDelivered()
    {
        // Nothing was carried: a trip with no delivery done is not finished.
        Esc17Trip("inTransit");
        Esc17DeliveriesOnTrip(("del-001", "pending"));

        await Esc17CancelAsync(Esc17Function());

        await Esc17NotMarked();
    }

    [Fact]
    public async Task Esc17_IndexShowsNoDelivery_OnlyTheCancelledOneCounts_NoChange()
    {
        Esc17Trip("inTransit");
        Esc17DeliveriesOnTrip();

        await Esc17CancelAsync(Esc17Function());

        await Esc17NotMarked();
    }

    [Theory]
    [InlineData("funded")]
    [InlineData("accepted")]
    public async Task Esc17_AnOrderOnTheTripWithoutADeliveryYet_KeepsItInTransit(string orderStatus)
    {
        // Every delivery is done, but a booked or agreed order has no delivery yet: the truck still
        // has a load to carry.
        Esc17Trip("inTransit");
        Esc17DeliveriesWithOrders(("del-001", "pickedUp", "order-1"));
        Esc17OrdersOnTrip(("order-1", "in_transit"), ("order-2", orderStatus));

        var result = await Esc17ConfirmAsync(Esc17Function());

        Assert.Equal("success", result.GetProperty("status").GetString());
        await Esc17NotMarked();
        Assert.Contains("still waits for the truck; not marked delivered", Esc17Logs);
    }

    [Theory]
    [InlineData("pickup_confirmed")]
    [InlineData("in_transit")]
    public async Task Esc17_AnOrderRowLaggingItsDoneDelivery_DoesNotHoldTheTrip(string laggingStatus)
    {
        // The order follows its delivery best effort and the orders index lags: an order whose
        // delivery on this trip is done counts as carried. The delivery confirmed now keeps the
        // order the index gave it.
        Esc17Trip("inTransit");
        Esc17DeliveriesWithOrders(("del-001", "pickedUp", "order-1"), ("del-002", "delivered", "order-2"));
        Esc17OrdersOnTrip(("order-1", laggingStatus), ("order-2", "in_transit"), ("order-3", "cancelled"));

        await Esc17ConfirmAsync(Esc17Function());

        await Esc17MarkedDelivered();
    }

    [Fact]
    public async Task Esc17_TruckerMarkedItFirst_TheRaceIsHarmless()
    {
        Esc17Trip("inTransit");
        _esc17Trips.TryMarkDeliveredAsync(default!, default!, default, default!).ReturnsForAnyArgs(false);
        Esc17DeliveriesOnTrip(("del-001", "pickedUp"));

        var result = await Esc17ConfirmAsync(Esc17Function());

        Assert.Equal("success", result.GetProperty("status").GetString());
        await Esc17MarkedDelivered();
        Assert.Contains("changed since it was read; not marked delivered", Esc17Logs);
    }

    [Fact]
    public async Task Esc17_TripReadFails_TheDeliveryStillSucceeds_AndTheFailureIsLogged()
    {
        _esc17Trips.GetRawItemAsync("trip-001").ThrowsAsync(new InvalidOperationException("throttled"));

        var result = await Esc17ConfirmAsync(Esc17Function());

        Assert.Equal("success", result.GetProperty("status").GetString());
        Assert.Contains("TRIP_COMPLETE_FAILED", Esc17Logs);
        await Esc17NotMarked();
    }

    [Fact]
    public async Task Esc17_TooManyDeliveriesToRead_TheTripIsLeftToItsTrucker()
    {
        Esc17Trip("inTransit");
        _esc17Deliveries.QueryDeliveriesAsync(Arg.Any<string?>(), "trip-001", Arg.Any<string?>(), Arg.Any<int>(),
                Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>
                {
                    new() { { "deliveryId", "del-002" }, { "status", "delivered" } }
                },
                new Dictionary<string, AttributeValue> { { "deliveryId", new AttributeValue { S = "more" } } }));

        await Esc17ConfirmAsync(Esc17Function());

        await Esc17NotMarked();
        Assert.Contains("deliveries; left for the trucker to mark delivered", Esc17Logs);
    }

    [Fact]
    public async Task Esc17_FlagOff_TheTripIsNotEvenRead()
    {
        Esc17Trip("inTransit");
        Esc17DeliveriesOnTrip(("del-001", "pickedUp"));

        await Esc17ConfirmAsync(Esc17Function(enabled: false));

        await _esc17Trips.DidNotReceiveWithAnyArgs().GetRawItemAsync(default!);
        await Esc17NotMarked();
    }

    // ===== a trip the server already closed or finished =====

    [Fact]
    public async Task Esc17_CancelOfATripTheCronClosed_AnswersTheStoredRow()
    {
        // The trucker's phone cancels offline at version 3; the order cron closed it to version 4.
        SetupExistingTrip("trip-001", "trucker-001", "cancelled", 4);

        var result = await Esc17SendAsync(_function, TripUpdate(new() { { "status", "cancelled" }, { "version", 3 } }),
            "trucker-001", "trucker");

        Assert.Equal("conflict", result.GetProperty("status").GetString());
        Assert.Equal(4, result.GetProperty("serverVersion").GetInt32());
        Assert.Equal("cancelled", result.GetProperty("serverData").GetProperty("status").GetString());
        Assert.StartsWith("TRIP_ALREADY_CANCELLED", result.GetProperty("message").GetString());
        await _mockDynamoDb.DidNotReceiveWithAnyArgs().UpdateItemAsync(default(UpdateItemRequest)!);
    }

    [Fact]
    public async Task Esc17_StaleEditOfATripTheCronClosed_AnswersTheStoredRow()
    {
        SetupExistingTrip("trip-001", "trucker-001", "cancelled", 4);

        var result = await Esc17SendAsync(_function, TripUpdate(new() { { "capacity", 80.0 }, { "version", 3 } }),
            "trucker-001", "trucker");

        Assert.Equal("conflict", result.GetProperty("status").GetString());
        Assert.Equal(4, result.GetProperty("serverVersion").GetInt32());
    }

    [Fact]
    public async Task Esc17_MarkDeliveredAfterTheLastDeliveryFinishedTheTrip_AnswersTheStoredRow()
    {
        SetupExistingTrip("trip-001", "trucker-001", "delivered", 7);

        var result = await Esc17SendAsync(_function, TripUpdate(new() { { "status", "delivered" }, { "version", 6 } }),
            "trucker-001", "trucker");

        Assert.Equal("conflict", result.GetProperty("status").GetString());
        Assert.Equal(7, result.GetProperty("serverVersion").GetInt32());
        Assert.StartsWith("TRIP_ALREADY_DELIVERED", result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Esc17_CurrentVersionAskingForAnotherStatus_IsStillRefused()
    {
        SetupExistingTrip("trip-001", "trucker-001", "cancelled", 4);

        var result = await Esc17SendAsync(_function, TripUpdate(new() { { "status", "inTransit" }, { "version", 4 } }),
            "trucker-001", "trucker");

        Assert.Equal("error", result.GetProperty("status").GetString());
        Assert.StartsWith("TRIP_TERMINAL_STATE", result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Esc17_LegacyDeleteOfASoftDeletedTrip_AnswersTheStoredRow()
    {
        SetupExistingTrip("trip-001", "trucker-001", "cancelled", 4, deletedAt: "2026-11-01T08:00:00.000Z");

        var result = await Esc17SendAsync(_function, new SyncOperationRequest
        {
            OperationId = "op-esc17-trip-delete",
            IdempotencyKey = "trucker-001:trip:trip-001:delete:esc17",
            EntityType = "trip",
            EntityId = "trip-001",
            Action = "delete",
            Data = new Dictionary<string, object> { { "version", 4 } }
        }, "trucker-001", "trucker");

        Assert.Equal("conflict", result.GetProperty("status").GetString());
        Assert.StartsWith("TRIP_ALREADY_CANCELLED", result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Esc17_AnotherTruckersClosedTrip_IsStillUnauthorized()
    {
        // Ownership is checked before the terminal answer, so no row leaks to another trucker.
        SetupExistingTrip("trip-001", "trucker-002", "cancelled", 4);

        var result = await Esc17SendAsync(_function, TripUpdate(new() { { "status", "cancelled" }, { "version", 3 } }),
            "trucker-001", "trucker");

        Assert.Equal("error", result.GetProperty("status").GetString());
        Assert.StartsWith("TRIP_UNAUTHORIZED", result.GetProperty("message").GetString());
        Assert.False(result.TryGetProperty("serverData", out var data) && data.ValueKind == JsonValueKind.Object);
    }

    private static SyncOperationRequest TripUpdate(Dictionary<string, object> data) => new()
    {
        OperationId = "op-esc17-trip-update",
        IdempotencyKey = $"trucker-001:trip:trip-001:update:{Guid.NewGuid():N}",
        EntityType = "trip",
        EntityId = "trip-001",
        Action = "update",
        Data = data
    };
}
