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
/// LT-12 (live-trip.md section 10, order-lifecycle.md 2.5.0): the buyer's drop-off note through
/// <c>POST /sync</c>. On create: stored trimmed, left off when empty, at most 80 characters, no phone
/// number. On update: only the buyer changes it, only before pickup (the order's status and its
/// active delivery, read strongly consistent), with the same length and phone rules; an unchanged
/// echo passes from anyone. Every refusal writes nothing.
/// </summary>
public class SyncFunctionDropoffNoteTests
{
    private const string BuyerId = "buyer-001";
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";
    private const string OrderId = "order-001";
    private const string DeliveryId = "del-001";
    private const string DeliveriesTable = "test-deliveries-table";
    private const string StoredNote = "Merkato, near Anwar Mosque";
    private const string ContactRefusal =
        "ORDER_CONTACT_IN_TEXT: Remove the phone number from dropoffNote. Contact opens after payment is held in escrow.";

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

    public SyncFunctionDropoffNoteTests()
    {
        _syncMeta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);

        // Create: farmer-001's teff listing, 10 quintals at 45.5 ETB/kg, and the buyer's profile.
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
            Woreda = "Addis Ketema",
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

        // Update: the stored order (read strongly consistent) and its active delivery.
        _orders.GetByIdAsync(OrderId).Returns(_ => _order);
        _orders.GetByIdAsync(OrderId, Arg.Any<bool>()).Returns(_ => _order);
        _orders.UpdateAsync(OrderId, Arg.Do<Dictionary<string, object>>(d => _written = d), Arg.Any<int>())
            .Returns(call =>
            {
                // As the repository answers: plain stored values, never the request's JsonElements.
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

        _function = new SyncFunction(_syncMeta, _crops, _dynamoDb, "test-trips-table", DeliveriesTable,
            userRepo: _users, orderRepo: _orders);
    }

    // ===== Create =====

    [Fact]
    public async Task Create_StoresTheNoteTrimmed()
    {
        var result = await CreateAsync("  Merkato, near Anwar Mosque  ");

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        _created!["dropoffNote"].Should().Be(StoredNote);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_WithAnEmptyNote_StoresNone(string note)
    {
        var result = await CreateAsync(note);

        result.GetProperty("status").GetString().Should().Be("success");
        _created.Should().NotContainKey("dropoffNote");
    }

    [Fact]
    public async Task Create_WithoutANote_IsUnchanged()
    {
        var result = await CreateAsync(note: null);

        result.GetProperty("status").GetString().Should().Be("success");
        _created.Should().NotContainKey("dropoffNote");
    }

    [Fact]
    public async Task Create_EightyCharacters_AreStored_EightyOne_AreRefused()
    {
        var ok = await CreateAsync(new string('a', 80));
        ok.GetProperty("status").GetString().Should().Be("success");
        _created!["dropoffNote"].Should().Be(new string('a', 80));

        _created = null;
        var refused = await CreateAsync(new string('a', 81));
        refused.GetProperty("status").GetString().Should().Be("error");
        refused.GetProperty("message").GetString().Should().Be("ORDER_VALIDATION: dropoffNote must not exceed 80 characters.");
        _created.Should().BeNull();
    }

    [Fact]
    public async Task Create_EightyAmharicSyllables_AreStored()
    {
        var note = string.Concat(Enumerable.Repeat("መር", 40));

        var result = await CreateAsync(note);

        result.GetProperty("status").GetString().Should().Be("success");
        _created!["dropoffNote"].Should().Be(note);
    }

    [Theory]
    [InlineData("Merkato, call 0911 234 567")]
    [InlineData("+251-911-234567 at the gate")]
    [InlineData("Kality ፱፩፩፪፫፬፭፮፯")]
    public async Task Create_WithAPhoneNumber_IsRefused_AndNothingIsWritten(string note)
    {
        var result = await CreateAsync(note);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().Be(ContactRefusal);
        await _orders.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!);
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
    public async Task TheBuyer_ChangesTheNote_BeforePickup(string status)
    {
        _order = Order(status);

        var result = await UpdateAsync(BuyerId, " Kality, behind the bus station ");

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        _written!["dropoffNote"].Should().Be("Kality, behind the bus station");
        result.GetProperty("serverData").GetProperty("dropoffNote").GetString().Should().Be("Kality, behind the bus station");
    }

    [Fact]
    public async Task TheBuyer_ClearsTheNote_WithAnEmptyOne()
    {
        var result = await UpdateAsync(BuyerId, "");

        result.GetProperty("status").GetString().Should().Be("success");
        _written!["dropoffNote"].Should().Be("");
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

        var result = await UpdateAsync(BuyerId, "Kality");

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should()
            .Be("ORDER_DROPOFF_NOTE_LOCKED: The drop-off note can only be changed before pickup.");
        await NothingWritten();
    }

    [Theory]
    [InlineData("pickedUp")]
    [InlineData("inTransit")]
    [InlineData("delivered")]
    [InlineData("disputed")]
    public async Task AnOrderStillAccepted_WhoseDeliveryWasPickedUp_IsLocked(string deliveryStatus)
    {
        // The order follows its delivery on a best-effort write: the delivery decides.
        _deliveryStatus = deliveryStatus;

        var result = await UpdateAsync(BuyerId, "Kality");

        result.GetProperty("message").GetString().Should().StartWith("ORDER_DROPOFF_NOTE_LOCKED:");
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

        var result = await UpdateAsync(BuyerId, "Kality");

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().NotStartWith("ORDER_");
        await NothingWritten();
    }

    // ===== Update: who =====

    [Theory]
    [InlineData(FarmerId, "farmer")]
    [InlineData(TruckerId, "trucker")]
    public async Task TheFarmerOrTheTrucker_CannotChangeIt(string caller, string role)
    {
        var result = await UpdateAsync(caller, "Kality", role: role);

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should().Be("ORDER_UNAUTHORIZED: Only the buyer can change the drop-off note.");
        await NothingWritten();
    }

    [Fact]
    public async Task TheFarmer_CannotSlipAChangeIntoAnotherUpdate()
    {
        _order = Order("funded");

        var result = await UpdateAsync(FarmerId, "Kality", role: "farmer", extra: new() { { "status", "rejected" } });

        result.GetProperty("message").GetString().Should().StartWith("ORDER_UNAUTHORIZED:");
        await NothingWritten();
    }

    [Fact]
    public async Task AnUnchangedEcho_PassesFromAPartyWhoReadsIt_AndIsNotWritten()
    {
        // The committed trucker's in-transit catch-up resends the row's note: no change, so no
        // refusal (a trip order from before 2.0.0 counts as committed from accepted on).
        _order = Order("pickup_confirmed");
        _deliveryStatus = "inTransit";

        var result = await UpdateAsync(TruckerId, StoredNote, role: "trucker", extra: new() { { "status", "in_transit" } });

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        _written.Should().NotContainKey("dropoffNote");
        _written!["status"].ToString().Should().Be("in_transit");
    }

    [Fact]
    public async Task TheCommittedTrucker_AndTheFarmer_MayEchoTheNote()
    {
        _order = Order("accepted");
        _order["truckerResponse"] = "accepted";
        _deliveryStatus = "pickedUp";

        var trucker = await UpdateAsync(
            TruckerId, StoredNote, role: "trucker", extra: new() { { "status", "pickup_confirmed" } });

        trucker.GetProperty("status").GetString().Should().Be("success", trucker.ToString());
        _written.Should().NotContainKey("dropoffNote");

        _written = null;
        _order = Order("funded");
        _order["truckerResponse"] = "pending";
        var farmer = await UpdateAsync(FarmerId, StoredNote, role: "farmer");

        // The farmer reads the note: an echo is dropped, the rest of the update goes on.
        farmer.GetProperty("status").GetString().Should().Be("success", farmer.ToString());
        _written.Should().NotContainKey("dropoffNote");
    }

    [Theory]
    [InlineData(StoredNote)]
    [InlineData("A guess at the note")]
    [InlineData("")]
    [InlineData("call 0911234567")]
    public async Task ATruckerNotCommitted_GetsTheSameRefusal_WhateverTheNote(string probe)
    {
        // No equality oracle: the answer is the same whether the probe matches the stored note,
        // differs, is empty, or would break the field rules.
        _order = Order("funded");
        _order["truckerResponse"] = "pending";

        var result = await UpdateAsync(TruckerId, probe, role: "trucker");

        result.GetProperty("status").GetString().Should().Be("error");
        result.GetProperty("message").GetString().Should()
            .Be("ORDER_UNAUTHORIZED: Only the buyer can change the drop-off note.");
        // No row comes back either.
        (!result.TryGetProperty("serverData", out var row) || row.ValueKind == JsonValueKind.Null)
            .Should().BeTrue(result.ToString());
        await NothingWritten();
    }

    [Fact]
    public async Task ATruckerNotCommitted_GetsTheSameRefusal_ForAnOverlongNote()
    {
        _order = Order("funded");
        _order["truckerResponse"] = "pending";

        var result = await UpdateAsync(TruckerId, new string('a', 81), role: "trucker");

        result.GetProperty("message").GetString().Should()
            .Be("ORDER_UNAUTHORIZED: Only the buyer can change the drop-off note.");
        await NothingWritten();
    }

    [Fact]
    public async Task TheBuyer_IsUnaffected_ByTheTruckersCommitment()
    {
        _order = Order("funded");
        _order["truckerResponse"] = "pending";

        var echo = await UpdateAsync(BuyerId, StoredNote);
        echo.GetProperty("status").GetString().Should().Be("success", echo.ToString());
        _written.Should().NotContainKey("dropoffNote");

        _written = null;
        var change = await UpdateAsync(BuyerId, "Kality");
        change.GetProperty("status").GetString().Should().Be("success", change.ToString());
        _written!["dropoffNote"].Should().Be("Kality");
    }

    [Fact]
    public async Task AnUnchangedEcho_AfterPickup_IsNotRefused()
    {
        _order = Order("in_transit");

        var result = await UpdateAsync(BuyerId, StoredNote + "  ");

        result.GetProperty("status").GetString().Should().Be("success", result.ToString());
        _written.Should().NotContainKey("dropoffNote");
    }

    // ===== Update: the text =====

    [Fact]
    public async Task AChangeWithAPhoneNumber_IsRefused()
    {
        var result = await UpdateAsync(BuyerId, "Kality 0911234567");

        result.GetProperty("message").GetString().Should().Be(ContactRefusal);
        await NothingWritten();
    }

    [Fact]
    public async Task AChangeOver80Characters_IsRefused()
    {
        var result = await UpdateAsync(BuyerId, new string('ሰ', 81));

        result.GetProperty("message").GetString().Should().Be("ORDER_VALIDATION: dropoffNote must not exceed 80 characters.");
        await NothingWritten();
    }

    [Fact]
    public async Task AChangeOnTwoLines_IsRefused()
    {
        var result = await UpdateAsync(BuyerId, "Kality\nGate 2");

        result.GetProperty("message").GetString().Should().Be("ORDER_VALIDATION: dropoffNote must be one line of text.");
        await NothingWritten();
    }

    // ===== The row a result carries back =====

    [Fact]
    public async Task TheRowSentBackToATruckerNotCommitted_LeavesTheNoteOut()
    {
        // A trucker catch-up that loses the version race gets the stored row back: without the note,
        // since this trucker's answer is not an accept.
        _order = Order("accepted");
        _order["truckerResponse"] = "cancelled";
        _deliveryStatus = "pickedUp";
        _orders.UpdateAsync(OrderId, Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "Version conflict."));

        var result = await SendAsync(TruckerId, "trucker", new Dictionary<string, object>
        {
            { "status", "pickup_confirmed" }, { "version", 2 }
        });

        result.GetProperty("status").GetString().Should().Be("conflict", result.ToString());
        result.GetProperty("serverData").GetProperty("status").GetString().Should().Be("accepted");
        result.GetProperty("serverData").TryGetProperty("dropoffNote", out _).Should().BeFalse();
    }

    // ===== helpers =====

    private async Task NothingWritten()
    {
        _written.Should().BeNull();
        await _orders.DidNotReceive().UpdateAsync(
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>());
    }

    private async Task<JsonElement> CreateAsync(string? note)
    {
        var data = new Dictionary<string, object>
        {
            { "buyerId", BuyerId }, { "farmerId", FarmerId }, { "listingId", "listing-001" },
            { "buyerName", "Selam Traders" }, { "farmerName", "Abebe Kebede" },
            { "cropType", "teff" }, { "quantity", 50 }, { "pricePerUnit", 45.5 }
        };
        if (note != null)
        {
            data["dropoffNote"] = note;
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
        string caller, string note, string role = "buyer", Dictionary<string, object>? extra = null)
    {
        var data = new Dictionary<string, object> { { "dropoffNote", note }, { "version", 2 } };
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
                SyncBatchId = "batch-lt-12",
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

    /// <summary>A trip order with a note, whose active delivery is del-001.</summary>
    private static Dictionary<string, object> Order(string status) => new()
    {
        { "orderId", OrderId }, { "listingId", "listing-001" }, { "createdAt", "2026-10-30T08:00:00Z" },
        { "buyerId", BuyerId }, { "farmerId", FarmerId },
        { "buyerName", "Selam Traders" }, { "farmerName", "Abebe Kebede" },
        { "cropType", "teff" }, { "quantity", 50.0 }, { "pricePerUnit", 45.5 },
        { "tripId", "trip-001" }, { "truckerId", TruckerId }, { "truckerName", "Tadesse Worku" },
        { "transportCost", 60.0 }, { "cropCost", 2275.0 }, { "commissionRate", 0.05 },
        { "commissionAmount", 113.75 }, { "orderTotal", 2448.75 },
        { "activeDeliveryId", DeliveryId }, { "dropoffNote", StoredNote },
        { "status", status }, { "version", "2" }
    };
}
