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
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// FUP-19 (spec E9) against real DynamoDB (LocalStack in CI): the admin's cancel of a delivery after pickup, with its
/// transaction's conditions as DynamoDB evaluates them. The cancel lands with its audit row and touches nothing else;
/// a replay writes nothing twice; money still held refuses it; an escrow or delivery that moved between the read and
/// the write refuses the whole transaction; and afterwards the order takes no new delivery and the trucker's and the
/// buyer's handoffs are refused.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AdminDeliveryCancelDynamoDbTests
{
    private const string CreatedAt = "2026-11-10T08:00:00.0000000Z";
    private const string Reason = "Truck broke down; the farmer took the goods back.";

    private readonly LocalDynamoDbFixture _db;
    private readonly string _buyerId = Guid.NewGuid().ToString();
    private readonly string _farmerId = Guid.NewGuid().ToString();
    private readonly string _truckerId = Guid.NewGuid().ToString();
    private readonly string _listingId = Guid.NewGuid().ToString();
    private readonly string _tripId = Guid.NewGuid().ToString();
    private readonly string _orderId = Guid.NewGuid().ToString();
    private readonly string _deliveryId = Guid.NewGuid().ToString();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();

    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public AdminDeliveryCancelDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
        _users.GetUserByIdAsync(Arg.Any<string>()).Returns(ci => new UserDto
        {
            UserId = (string)ci[0],
            PhoneNumber = "+251900100000",
            DisplayName = "Party Name",
            Role = (string)ci[0] == _buyerId ? "buyer" : (string)ci[0] == _farmerId ? "farmer" : "trucker",
            Woreda = "Jimma",
            IsPhoneVerified = true,
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
            Version = 1
        });
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private string OrderEscrowId => $"escrow-order-{_orderId}";

    private AdminFunction Admin() => new(Ddb, _db.Users, _db.Orders, _db.Escrows, _db.Deliveries, _db.EscrowAudit, _db.TransactionAudit);

    private SyncFunction Sync()
    {
        var syncMeta = Substitute.For<ISyncMetadataRepository>();
        syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        return new SyncFunction(syncMeta, new CropRepository(Ddb, _db.Crops), Ddb, _db.Trips, _db.Deliveries,
            userRepo: _users, orderRepo: new OrderRepository(Ddb, _db.Orders),
            transactionAuditRepo: new TransactionAuditRepository(Ddb, _db.TransactionAudit));
    }

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue N(int value) => new() { N = value.ToString() };

    private static string? Str(Dictionary<string, object>? item, string key)
        => item != null && item.TryGetValue(key, out var value) ? value?.ToString() : null;

    // ──────────────────────────────────────────────
    // Seeding: an order on the road whose money an admin refunded with the override (ESC-09)
    // ──────────────────────────────────────────────

    private async Task SeedAsync(string escrowStatus = "refunded", string deliveryStatus = "inTransit")
    {
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Crops,
            Item = new()
            {
                ["cropId"] = S(_listingId),
                ["createdAt"] = S(CreatedAt),
                ["farmerId"] = S(_farmerId),
                ["status"] = S("reserved"),
                ["version"] = N(1)
            }
        });
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Trips,
            Item = new()
            {
                ["tripId"] = S(_tripId),
                ["createdAt"] = S(CreatedAt),
                ["truckerId"] = S(_truckerId),
                ["status"] = S("inTransit"),
                ["version"] = N(2)
            }
        });
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Orders,
            Item = new()
            {
                ["orderId"] = S(_orderId),
                ["createdAt"] = S(CreatedAt),
                ["status"] = S("in_transit"),
                ["buyerId"] = S(_buyerId),
                ["farmerId"] = S(_farmerId),
                ["truckerId"] = S(_truckerId),
                ["tripId"] = S(_tripId),
                ["listingId"] = S(_listingId),
                ["buyerName"] = S("Mekonnen"),
                ["farmerName"] = S("Abebe"),
                ["truckerName"] = S("Chaltu"),
                ["activeDeliveryId"] = S(_deliveryId),
                ["activeDeliveryClaimedAt"] = S(CreatedAt),
                ["refundOverrideAt"] = S("2026-11-12T10:00:00.0000000Z"),
                ["version"] = N(6)
            }
        });
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Escrows,
            Item = new()
            {
                ["escrowId"] = S(OrderEscrowId),
                ["createdAt"] = S(CreatedAt),
                ["orderId"] = S(_orderId),
                ["deliveryId"] = S(_deliveryId),
                ["status"] = S(escrowStatus),
                ["buyerId"] = S(_buyerId),
                ["farmerId"] = S(_farmerId),
                ["version"] = N(5)
            }
        });
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Deliveries,
            Item = new()
            {
                ["deliveryId"] = S(_deliveryId),
                ["tripId"] = S(_tripId),
                ["orderId"] = S(_orderId),
                ["listingId"] = S(_listingId),
                ["buyerId"] = S(_buyerId),
                ["participantId"] = S(_buyerId),
                ["farmerId"] = S(_farmerId),
                ["truckerId"] = S(_truckerId),
                ["status"] = S(deliveryStatus),
                ["pickedUpAt"] = S("2026-11-11T07:00:00.0000000Z"),
                ["createdAt"] = S(CreatedAt),
                ["version"] = N(3)
            }
        });
    }

    private Task<Dictionary<string, object>?> DeliveryAsync() => new DeliveryRepository(Ddb, _db.Deliveries).GetByIdAsync(_deliveryId, true);

    private Task<Dictionary<string, object>?> OrderAsync() => new OrderRepository(Ddb, _db.Orders).GetByIdAsync(_orderId, true);

    private Task<Dictionary<string, object>?> EscrowAsync() => new EscrowRepository(Ddb, _db.Escrows).GetByIdAsync(OrderEscrowId, true);

    private async Task<List<Dictionary<string, object>>> CancelAuditRowsAsync()
        => (await new TransactionAuditRepository(Ddb, _db.TransactionAudit).GetByTransactionAsync(_orderId))
            .Where(r => Str(r, "action") == AdminDeliveryCancel.AuditAction)
            .ToList();

    private async Task<APIGatewayProxyResponse> CancelAsync(int expectedVersion = 3)
    {
        var request = new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = $"/admin/deliveries/{_deliveryId}/cancel",
            Body = JsonSerializer.Serialize(new { reason = Reason, expectedVersion }),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-fup19" }, { "cognito:groups", "admin" } }
            }
        };
        return await Admin().FunctionHandler(request, new TestLambdaContext());
    }

    private async Task<JsonElement> SendAsync(SyncOperationRequest operation, string userId)
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new SyncRequest
            {
                SyncBatchId = Guid.NewGuid().ToString(),
                ClientId = "device-it",
                Operations = new List<SyncOperationRequest> { operation }
            }, SerializerOptions),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId } }
            }
        };

        var response = await Sync().FunctionHandler(request, new TestLambdaContext());
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.GetProperty("results")[0].Clone();
    }

    private SyncOperationRequest Op(string deliveryId, string action, string userId, Dictionary<string, object> data) => new()
    {
        OperationId = Guid.NewGuid().ToString(),
        IdempotencyKey = $"{userId}:delivery:{deliveryId}:{action}:{Guid.NewGuid():N}",
        EntityType = "delivery",
        EntityId = deliveryId,
        Action = action,
        Data = data
    };

    // ──────────────────────────────────────────────
    // The cancel
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task TheCancel_ClosesTheDeliveryWithItsAuditRow_AndTouchesNothingElse()
    {
        await SeedAsync();

        var response = await CancelAsync();

        response.StatusCode.Should().Be(200, response.Body);
        var delivery = await DeliveryAsync();
        Str(delivery, "status").Should().Be("cancelled");
        Str(delivery, "cancelReason").Should().Be("admin_cancelled");
        Str(delivery, "cancelledAt").Should().NotBeNullOrEmpty();
        Str(delivery, "version").Should().Be("4");
        Str(delivery, "deletedAt").Should().BeNull("the cancelled row stays in every party's list");

        var audit = (await CancelAuditRowsAsync()).Should().ContainSingle().Subject;
        Str(audit, "entityId").Should().Be(_deliveryId);
        Str(audit, "fromState").Should().Be("inTransit");
        Str(audit, "toState").Should().Be("cancelled");
        Str(audit, "actor").Should().Be("admin-fup19");
        Str(audit, "timestamp").Should().Be(Str(delivery, "cancelledAt"));
        Str(audit, "metadata").Should().Be($"orderStatus=in_transit;escrows={OrderEscrowId}:refunded;reason={Reason}");

        var order = await OrderAsync();
        Str(order, "status").Should().Be("in_transit");
        Str(order, "version").Should().Be("6");
        Str(order, "activeDeliveryId").Should().Be(_deliveryId, "the order keeps its delivery slot");
        Str(await EscrowAsync(), "status").Should().Be("refunded");
        Str(await EscrowAsync(), "version").Should().Be("5");
    }

    [LocalDynamoDbFact]
    public async Task TheSameRequestAgain_WritesNothing()
    {
        await SeedAsync();
        (await CancelAsync()).StatusCode.Should().Be(200);

        var again = await CancelAsync();

        again.StatusCode.Should().Be(200, again.Body);
        JsonDocument.Parse(again.Body).RootElement.GetProperty("alreadyCancelled").GetBoolean().Should().BeTrue();
        Str(await DeliveryAsync(), "version").Should().Be("4");
        (await CancelAuditRowsAsync()).Should().ContainSingle();
    }

    [LocalDynamoDbFact]
    public async Task MoneyStillHeld_RefusesTheCancel_AndWritesNothing()
    {
        await SeedAsync(escrowStatus: "escrowed");

        var response = await CancelAsync();

        response.StatusCode.Should().Be(409);
        response.Body.Should().Contain(AdminDeliveryCancel.MoneyHeldCode);
        Str(await DeliveryAsync(), "status").Should().Be("inTransit");
        Str(await DeliveryAsync(), "version").Should().Be("3");
        (await CancelAuditRowsAsync()).Should().BeEmpty();
    }

    // ──────────────────────────────────────────────
    // The transaction's conditions
    // ──────────────────────────────────────────────

    private AdminDeliveryCancelPlan Plan(string escrowStatusRead = "refunded", int versionRead = 3) => new()
    {
        DeliveryId = _deliveryId,
        TripId = _tripId,
        FromStatus = "inTransit",
        ExpectedVersion = versionRead,
        CancelReason = AdminDeliveryCancel.CancelReason,
        Now = "2026-11-13T09:30:00.0000000Z",
        AuditEntry = TransactionAuditWriter.BuildOnceEntry(
            _orderId, "delivery", _deliveryId, AdminDeliveryCancel.AuditAction, "inTransit", "cancelled",
            AuditContext.ForSystem("admin-fup19"), "2026-11-13T09:30:00.0000000Z", $"reason={Reason}"),
        Checks = new[]
        {
            new AdminDeliveryCancelCheck(_db.Orders, new() { ["orderId"] = S(_orderId), ["createdAt"] = S(CreatedAt) }, "in_transit"),
            new AdminDeliveryCancelCheck(_db.Escrows, new() { ["escrowId"] = S(OrderEscrowId), ["createdAt"] = S(CreatedAt) },
                escrowStatusRead)
        }
    };

    [LocalDynamoDbFact]
    public async Task AnEscrowThatMovedAfterTheRead_RefusesTheWholeTransaction()
    {
        // Read as refunded; a write since (here the row is held) makes the escrow's check fail.
        await SeedAsync(escrowStatus: "held");

        var write = await new AdminDeliveryCancelRepository(Ddb, _db.Deliveries, _db.TransactionAudit).CancelAsync(Plan());

        write.Should().Be(AdminDeliveryCancelWrite.LinkedChanged);
        Str(await DeliveryAsync(), "status").Should().Be("inTransit");
        (await CancelAuditRowsAsync()).Should().BeEmpty();
    }

    [LocalDynamoDbFact]
    public async Task ADeliveryThatMovedAfterTheRead_RefusesTheWholeTransaction()
    {
        await SeedAsync();

        var write = await new AdminDeliveryCancelRepository(Ddb, _db.Deliveries, _db.TransactionAudit)
            .CancelAsync(Plan(versionRead: 2));

        write.Should().Be(AdminDeliveryCancelWrite.DeliveryChanged);
        Str(await DeliveryAsync(), "status").Should().Be("inTransit");
        (await CancelAuditRowsAsync()).Should().BeEmpty();
    }

    // ──────────────────────────────────────────────
    // After the cancel
    // ──────────────────────────────────────────────

    [LocalDynamoDbFact]
    public async Task AfterTheCancel_TheOrderTakesNoNewDelivery()
    {
        await SeedAsync();
        (await CancelAsync()).StatusCode.Should().Be(200);
        var newDeliveryId = Guid.NewGuid().ToString();

        var result = await SendAsync(Op(newDeliveryId, "create", _truckerId, new Dictionary<string, object>
        {
            { "tripId", _tripId }, { "orderId", _orderId }, { "listingId", _listingId },
            { "farmerId", _farmerId }, { "farmerName", "Abebe" },
            { "truckerId", _truckerId }, { "truckerName", "Chaltu" },
            { "buyerId", _buyerId }, { "buyerName", "Mekonnen" },
            { "qrToken", "qr-token-it" }
        }), _truckerId);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().Be("DELIVERY_VALIDATION: The order already has an active delivery.");
        Str(await OrderAsync(), "activeDeliveryId").Should().Be(_deliveryId);
        (await new DeliveryRepository(Ddb, _db.Deliveries).GetByIdAsync(newDeliveryId, true)).Should().BeNull();
    }

    [LocalDynamoDbFact]
    public async Task AfterTheCancel_TheBuyersDropOffConfirmation_IsRefused()
    {
        await SeedAsync();
        (await CancelAsync()).StatusCode.Should().Be(200);

        // At the version after the cancel, so no FUP-128 conflict answer: the plain refusal.
        var result = await SendAsync(Op(_deliveryId, "update", _buyerId, new Dictionary<string, object>
        {
            { "status", "delivered" }, { "version", 4 }, { "confirmedAt", "2026-11-13T10:00:00.0000000Z" }
        }), _buyerId);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().StartWith("DELIVERY_ALREADY_CANCELLED");
        Str(await DeliveryAsync(), "status").Should().Be("cancelled");
    }

    [LocalDynamoDbFact]
    public async Task AfterTheCancel_ATruckerWhoWasOffline_GetsTheCancelledRowBack()
    {
        // FUP-128: the trucker's queued "in transit" from the version before the cancel takes the server's row.
        await SeedAsync(deliveryStatus: "pickedUp");
        (await CancelAsync()).StatusCode.Should().Be(200);

        var result = await SendAsync(Op(_deliveryId, "update", _truckerId, new Dictionary<string, object>
        {
            { "status", "inTransit" }, { "version", 3 }
        }), _truckerId);

        result.GetProperty("status").GetString().Should().Be("conflict");
        result.GetProperty("serverData").GetProperty("status").GetString().Should().Be("cancelled");
        Str(await DeliveryAsync(), "version").Should().Be("4");
    }
}
