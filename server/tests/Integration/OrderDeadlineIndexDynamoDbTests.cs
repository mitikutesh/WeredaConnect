using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// ESC-16 against real DynamoDB (LocalStack in CI): the order writes keep the sparse
/// <c>DeadlineIndex</c> in step. The funding puts the order in it, the reminder stamp moves it, the
/// cancel takes it out, the sweep reads it with a Query bounded by "due before", and the cron's own
/// deadline write loses to any write that moved the version since its read.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OrderDeadlineIndexDynamoDbTests
{
    private const string CreatedAt = "2026-11-01T09:00:00.0000000Z";

    private readonly LocalDynamoDbFixture _db;
    private readonly string _orderId = $"order-esc16-{Guid.NewGuid():N}";

    public OrderDeadlineIndexDynamoDbTests(LocalDynamoDbFixture db) => _db = db;

    private IAmazonDynamoDB Ddb => _db.Client;

    private OrderRepository Repo => new(Ddb, _db.Orders);

    private async Task SeedSubmittedAsync()
        => await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = _db.Orders,
            Item = new Dictionary<string, AttributeValue>
            {
                ["orderId"] = new() { S = _orderId },
                ["createdAt"] = new() { S = CreatedAt },
                ["status"] = new() { S = "payment_submitted" },
                ["version"] = new() { N = "2" },
                ["buyerId"] = new() { S = "buyer-esc16" },
                ["farmerId"] = new() { S = "farmer-esc16" }
            }
        });

    /// <summary>This order's entry in the index among everything due before <paramref name="dueBefore"/>, paging through.</summary>
    private async Task<Dictionary<string, object>?> IndexEntryAsync(DateTime dueBefore)
    {
        Dictionary<string, AttributeValue>? start = null;
        do
        {
            var (items, next) = await Repo.QueryDueDeadlinesAsync(OrderCommitment.Iso(dueBefore), 100, start);
            var mine = items.FirstOrDefault(i => (string)i["orderId"] == _orderId);
            if (mine != null)
            {
                return mine;
            }

            start = next;
        }
        while (start != null);

        return null;
    }

    [LocalDynamoDbFact]
    public async Task FundingEntersTheIndex_TheReminderMovesIt_TheCancelTakesItOut()
    {
        await SeedSubmittedAsync();
        var now = DateTime.UtcNow;
        var farmerDeadline = now.AddHours(24);

        // Funding: the farmer's request starts; the reminder 6 hours before its end is the next deadline.
        var funded = await Repo.TryMarkFundedAsync(_orderId, new Dictionary<string, object>
        {
            { OrderCommitment.FarmerResponseField, OrderCommitment.Response.Pending },
            { OrderCommitment.FarmerDeadlineAtField, OrderCommitment.Iso(farmerDeadline) },
            { "paymentVerifiedAt", OrderCommitment.Iso(now) },
            { OrderCommitment.TruckerResponseField, OrderCommitment.Response.None }
        });
        funded.Should().NotBeNull();
        var reminderAt = OrderCommitment.Iso(farmerDeadline - OrderCommitment.ReminderLead);
        funded![OrderDeadlines.AtField].Should().Be(reminderAt);
        funded[OrderDeadlines.KindField].Should().Be(OrderDeadlines.Kind.FarmerReminder);

        // Not due yet: a sweep now does not see it; one past the reminder does.
        (await IndexEntryAsync(now)).Should().BeNull();
        (await IndexEntryAsync(farmerDeadline)).Should().NotBeNull();

        // The reminder stamp moves it to the window's end, without a version bump.
        (await Repo.TryStampAsync(_orderId,
            new Dictionary<string, object> { { OrderCommitment.FarmerReminderAtField, OrderCommitment.Iso(now) } },
            new Dictionary<string, string> { { "status", "funded" } })).Should().BeTrue();
        var stamped = await Repo.GetByIdAsync(_orderId, consistentRead: true);
        stamped![OrderDeadlines.AtField].Should().Be(OrderCommitment.Iso(farmerDeadline));
        stamped[OrderDeadlines.KindField].Should().Be(OrderDeadlines.Kind.FarmerResponse);
        stamped["version"].Should().Be("3");
        (await IndexEntryAsync(farmerDeadline)).Should().BeNull();
        (await IndexEntryAsync(farmerDeadline.AddMinutes(1))).Should().NotBeNull();

        // The cancel removes all three attributes, so the order leaves the sparse index.
        var cancelled = await Repo.UpdateAsync(_orderId, new Dictionary<string, object> { { "status", "cancelled" } }, 3);
        cancelled.IsSuccess.Should().BeTrue();
        var row = await Repo.GetByIdAsync(_orderId, consistentRead: true);
        row!.Keys.Should().NotContain(OrderDeadlines.Fields);
        (await IndexEntryAsync(DateTime.UtcNow.AddDays(30))).Should().BeNull();
    }

    [LocalDynamoDbFact]
    public async Task TheCronsDeadlineWrite_LosesToAWriteSinceItsRead()
    {
        await SeedSubmittedAsync();
        var now = DateTime.UtcNow;
        await Repo.TryMarkFundedAsync(_orderId, new Dictionary<string, object>
        {
            { OrderCommitment.FarmerResponseField, OrderCommitment.Response.Pending },
            { OrderCommitment.FarmerDeadlineAtField, OrderCommitment.Iso(now.AddHours(24)) },
            { "paymentVerifiedAt", OrderCommitment.Iso(now) },
            { OrderCommitment.TruckerResponseField, OrderCommitment.Response.None }
        });

        // The cron read version 3; the farmer's accept lands first (version 4).
        var accepted = await Repo.UpdateAsync(_orderId, new Dictionary<string, object>
        {
            { "status", "accepted" },
            { OrderCommitment.FarmerResponseField, OrderCommitment.Response.Accepted },
            { PickupDeadline.Field, PickupDeadline.Format(now.AddHours(30)) }
        }, 3);
        accepted.IsSuccess.Should().BeTrue();

        var retry = new OrderDeadline(now.AddHours(1), OrderDeadlines.Kind.FarmerResponse);
        (await Repo.TrySetDeadlineAsync(_orderId, CreatedAt, 3, retry)).Should().BeFalse();

        var row = await Repo.GetByIdAsync(_orderId, consistentRead: true);
        row![OrderDeadlines.KindField].Should().Be(OrderDeadlines.Kind.PickupOverdue);
        (await Repo.TrySetDeadlineAsync(_orderId, CreatedAt, 4, retry)).Should().BeTrue();
        (await Repo.GetByIdAsync(_orderId, consistentRead: true))![OrderDeadlines.AtField].Should().Be(retry.AtIso);
    }
}
