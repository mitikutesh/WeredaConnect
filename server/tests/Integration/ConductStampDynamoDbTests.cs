using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Tests.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// GCR-03 against real DynamoDB. The first buyer rows in the reputation events table, and the farmers' and truckers'
/// <c>HANDOFF_QUALITY</c> rows, change no existing reputation read: the party's own read and the public summary, the
/// event list, the admin read, the rows the policy and the daily job see (and the read cap), the users row's
/// <c>reputation</c> map. The stamp transaction (a free slot under the users row check, one re-evaluation) as the
/// table runs it, and account deletion still finds every row.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ConductStampDynamoDbTests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);

    private readonly LocalDynamoDbFixture _db;
    private readonly ManualClock _clock = new(T0);
    private readonly TestLambdaLogger _logger = new();

    /// <summary>The read cap: set to the farmer's own row count, so a stamp that reached the event reads would truncate them.</summary>
    private int _cap = 100;

    public ConductStampDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
    }

    private ReputationEventRepository Repo() => new(_db.Client, _db.ReputationEvents, _db.Users);

    private ReputationService Reputation()
        => new(Repo(), ReputationPolicySettings.Defaults with { EventsReadCap = _cap }, clock: _clock, backoff: _ => Task.CompletedTask);

    private ConductStampService Conduct()
        => new(Repo(), Substitute.For<IDeliveryRepository>(), null, null, null, Substitute.For<IFraudDetectionService>(),
            _clock, _ => Task.CompletedTask);

    private async Task<string> NewUser(string role, bool deleting = false)
    {
        var userId = $"{role}-{Guid.NewGuid():N}";
        var item = new Dictionary<string, AttributeValue>
        {
            ["userId"] = new() { S = userId },
            ["role"] = new() { S = role },
            ["version"] = new() { N = "1" }
        };
        if (deleting)
        {
            item["deletionState"] = new AttributeValue { S = "pending" };
        }

        await _db.Client.PutItemAsync(_db.Users, item);
        return userId;
    }

    private async Task Record(string userId, string party, string slot, string code, DateTime at, string buyerId)
        => (await Reputation().RecordAsync(new ReputationCandidate
        {
            UserId = userId,
            EventId = slot,
            Code = code,
            Party = party,
            OrderId = slot[(slot.IndexOf('#') + 1)..],
            BuyerId = buyerId,
            OrderTotal = code == ReputationCodes.OrderCompleted ? 800m : null,
            OccurredAt = at
        }, timeoutPenaltiesOn: true, _logger)).Should().Be(ReputationRecordResult.Recorded);

    /// <summary>Everything a reader of the events table gives for a farmer or trucker.</summary>
    private async Task<object> Reads(string userId)
    {
        var service = Reputation();
        var read = (await service.ReadAsync(userId, withEvents: true))!;
        var events = await service.ReadEventsAsync(userId);
        var admin = (await service.ReadAdminAsync(userId))!;
        var (rows, truncated) = await Repo().QueryEventsAsync(userId, _cap);
        return new
        {
            read.User,
            read.Aggregate,
            read.Ratings,
            read.NextPointExpiryAt,
            Public = ReputationReadModels.Public(read, publicTags: true),
            Listed = events.Where(ReputationReadModels.IsListed).ToList(),
            Events = events,
            AdminRead = admin.Read.Aggregate,
            AdminRows = admin.Rows,
            Rows = rows,
            Truncated = truncated,
            Due = ReputationService.DueChanges(rows, T0.AddDays(120)).Transitions,
            Policy = ReputationPolicy.Aggregate(rows, 0, 0, false, T0, ReputationPolicySettings.Defaults)
        };
    }

    private static ConductOrder Order(string orderId, string farmer, string trucker, string buyer, DateTime settledAt) => new()
    {
        OrderId = orderId,
        FarmerId = farmer,
        TruckerId = trucker,
        BuyerId = buyer,
        TripId = "trip-1",
        OrderTotal = 900m,
        SettledAt = settledAt
    };

    [LocalDynamoDbFact]
    public async Task FarmerAndTruckerReputationReads_AreUnchanged_WithBuyerAndHandoffRowsPresent()
    {
        var farmer = await NewUser("farmer");
        var trucker = await NewUser("trucker");
        var buyer = await NewUser("buyer");
        await Record(farmer, "farmer", "CANCEL#o1", ReputationCodes.LateCancel, T0.AddDays(-3), buyer);
        await Record(farmer, "farmer", "COMPLETED#o2", ReputationCodes.OrderCompleted, T0.AddDays(-2), buyer);
        await Record(trucker, "trucker", "REQUEST#o3", ReputationCodes.DeclinedRequest, T0.AddDays(-1), buyer);
        await Record(trucker, "trucker", "COMPLETED#o2", ReputationCodes.OrderCompleted, T0.AddDays(-2), buyer);
        // The penalty, its tier row and heal marker, the completion: the cap is exactly reached before the stamps.
        _cap = (await Repo().QueryEventsAsync(farmer, 100)).Rows.Count;
        _cap.Should().BeGreaterThanOrEqualTo(3);
        (await Repo().QueryEventsAsync(farmer, _cap)).Truncated.Should().BeFalse();
        var farmerBefore = await Reads(farmer);
        var truckerBefore = await Reads(trucker);

        // Five settled orders between the three: a row per party each, the buyer's the first buyer rows in the table.
        for (var i = 0; i < 5; i++)
        {
            (await Conduct().StampAsync(Order($"o-{i}", farmer, trucker, buyer, T0.AddDays(-10 + i)), _logger)).Should().Be(3);
        }

        (await Reads(farmer)).Should().BeEquivalentTo(farmerBefore, o => o.PreferringRuntimeMemberTypes());
        (await Reads(trucker)).Should().BeEquivalentTo(truckerBefore, o => o.PreferringRuntimeMemberTypes());
        var (_, truncated) = await Repo().QueryEventsAsync(farmer, _cap);
        truncated.Should().BeFalse("the stamps are filtered out before the cap counts");

        // The buyer: no reputation map, no event, five stamps.
        var buyerState = (await Repo().GetUserStateAsync(buyer))!;
        buyerState.MapVersion.Should().BeNull();
        buyerState.Snapshot.Should().BeNull();
        (await Repo().QueryEventsAsync(buyer, 100)).Rows.Should().BeEmpty();
        (await Repo().QueryStampsAsync(buyer, 100)).Should().HaveCount(5).And.OnlyContain(s => s.Party == "buyer");
        (await Repo().QueryStampsAsync(farmer, 100)).Should().HaveCount(5);

        // Never on DueIndex or AppealStatusIndex, never appealable, and account deletion still removes them.
        var raw = (await _db.Client.GetItemAsync(new GetItemRequest
        {
            TableName = _db.ReputationEvents,
            Key = ReputationEventRepository.EventKey(farmer, "HANDOFF#o-0"),
            ConsistentRead = true
        })).Item;
        raw.Should().NotContainKey("dueDay").And.NotContainKey("appealStatus").And.NotContainKey("status");
        (await Repo().TryAppealAsync(farmer, "HANDOFF#o-0", "not mine", null, T0, openReview: false)).Should().BeFalse();
        var deletion = new AccountDeletionRepository(_db.Client, new AccountDeletionTables
        {
            Users = _db.Users, Crops = _db.Crops, Trips = _db.Trips, Orders = _db.Orders, Deliveries = _db.Deliveries,
            Escrows = _db.Escrows, EscrowAudit = _db.EscrowAudit, EscrowIdempotency = _db.Idempotency, Ratings = _db.Ratings,
            Messages = "unused", Intents = _db.Intents, MatchEvents = "unused", DeviceTokens = "unused",
            Notifications = "unused", ReputationEvents = _db.ReputationEvents
        });
        (await deletion.QueryReputationEventIdsAsync(buyer)).Should().HaveCount(5).And.OnlyContain(id => id.StartsWith("HANDOFF#"));
    }

    [LocalDynamoDbFact]
    public async Task Stamp_OneRowPerParty_OneReevaluation_NoneForADeletingOrMissingAccount()
    {
        var farmer = await NewUser("farmer");
        var trucker = await NewUser("trucker", deleting: true);
        var buyer = await NewUser("buyer");
        var order = Order("o-1", farmer, trucker, buyer, T0) with { TruckerId = trucker };

        (await Conduct().StampAsync(order, _logger)).Should().Be(2, "the deleting trucker gets none");
        (await Conduct().StampAsync(order, _logger)).Should().Be(0, "settling twice writes one row per party");
        (await Repo().GetStampAsync(trucker, "o-1")).Should().BeNull();

        var stamp = (await Repo().GetStampAsync(buyer, "o-1"))!;
        stamp.Counted.Should().BeTrue();
        stamp.TruckerId.Should().Be(trucker);
        stamp.Steps.Should().NotBeEmpty();
        var again = stamp with { Earned = stamp.Earned + 1, ReevaluatedAt = T0.AddDays(1), Version = 2 };
        (await Repo().ReplaceOnceAsync(again)).Should().Be(ConductWriteOutcome.Written);
        (await Repo().ReplaceOnceAsync(again with { Earned = 0 })).Should().Be(ConductWriteOutcome.Exists);
        (await Repo().GetStampAsync(buyer, "o-1")).Should().BeEquivalentTo(again);

        var nobody = ConductScoring.Stamp(order, $"buyer-{Guid.NewGuid():N}",
            new ConductResult("buyer", 0, 0, Array.Empty<ConductStep>()), null, T0);
        (await Repo().PutNewAsync(nobody)).Should().Be(ConductWriteOutcome.Skipped);
        (await Repo().ReplaceOnceAsync(stamp with { OrderId = "o-none" })).Should().Be(ConductWriteOutcome.Exists);
    }
}
