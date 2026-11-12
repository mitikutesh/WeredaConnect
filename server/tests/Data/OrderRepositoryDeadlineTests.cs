using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// ESC-16: every order write keeps the order's next deadline (<see cref="OrderDeadlines"/>) on the row
/// in the same request: set when something is pending, removed when nothing is, left out when it does
/// not change. A stale read before the write is corrected with one write conditioned on the version
/// just written, and that correction never fails the write. The sweep's read is a Query on the sparse
/// <c>DeadlineIndex</c>, never a Scan, and its own deadline write is conditioned on the version read.
/// </summary>
public class OrderRepositoryDeadlineTests
{
    private const string TableName = "test-orders";
    private const string OrderId = "order-1";
    private const string CreatedAt = "2026-11-01T09:00:00.0000000Z";

    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly OrderRepository _repository;
    private readonly List<UpdateItemRequest> _updates = new();
    private readonly DateTime _now = DateTime.UtcNow;

    public OrderRepositoryDeadlineTests()
    {
        _repository = new OrderRepository(_ddb, TableName);
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });
    }

    // ===== fixture =====

    private static string Iso(DateTime utc) => OrderCommitment.Iso(utc);

    private static AttributeValue S(string value) => new() { S = value };

    private static Dictionary<string, AttributeValue> Row(string status, string version, params (string Key, string Value)[] fields)
    {
        var row = new Dictionary<string, AttributeValue>
        {
            { "orderId", S(OrderId) },
            { "createdAt", S(CreatedAt) },
            { "status", S(status) },
            { "version", new AttributeValue { N = version } }
        };
        foreach (var (key, value) in fields)
        {
            row[key] = S(value);
        }

        return row;
    }

    /// <summary>A funded order whose farmer has <paramref name="hoursLeft"/> to answer (asked 18 hours ago).</summary>
    private (string Key, string Value)[] FarmerPending(double hoursLeft) => new[]
    {
        (OrderCommitment.FarmerResponseField, OrderCommitment.Response.Pending),
        (OrderCommitment.FarmerDeadlineAtField, Iso(_now.AddHours(hoursLeft))),
        ("paymentVerifiedAt", Iso(_now.AddHours(-18))),
        (OrderCommitment.TruckerResponseField, OrderCommitment.Response.None)
    };

    private static (string Key, string Value)[] With(OrderDeadline due) => new[]
    {
        (OrderDeadlines.AtField, due.AtIso), (OrderDeadlines.KindField, due.Kind), (OrderDeadlines.ShardField, due.Shard)
    };

    private void Stored(Dictionary<string, AttributeValue> row)
        => _ddb.QueryAsync(Arg.Is<QueryRequest>(r => r.IndexName == null), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { row } });

    /// <summary>Every UpdateItem is recorded; the first answers <paramref name="written"/> (ALL_NEW), later ones answer nothing.</summary>
    private void Writes(Dictionary<string, AttributeValue> written, Exception? correctionFails = null)
        => _ddb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _updates.Add(ci.Arg<UpdateItemRequest>());
                if (_updates.Count > 1 && correctionFails != null)
                {
                    throw correctionFails;
                }

                return new UpdateItemResponse { Attributes = _updates.Count == 1 ? written : new Dictionary<string, AttributeValue>() };
            });

    private static bool SetsDeadline(UpdateItemRequest r, OrderDeadline due)
        => r.UpdateExpression.Contains("#ndAt = :ndAt")
           && r.ExpressionAttributeNames["#ndAt"] == OrderDeadlines.AtField
           && r.ExpressionAttributeNames["#ndKind"] == OrderDeadlines.KindField
           && r.ExpressionAttributeNames["#ndShard"] == OrderDeadlines.ShardField
           && r.ExpressionAttributeValues[":ndAt"].S == due.AtIso
           && r.ExpressionAttributeValues[":ndKind"].S == due.Kind
           && r.ExpressionAttributeValues[":ndShard"].S == due.Shard;

    private static bool RemovesDeadline(string expression)
        => expression.Contains("REMOVE #ndAt, #ndKind, #ndShard");

    private static bool TouchesDeadline(string expression) => expression.Contains("#nd");

    // ===== the write carries the deadline =====

    [Fact]
    public async Task TryMarkFunded_StartsTheFarmersWindow_TheReminderIsTheNextDeadline()
    {
        Stored(Row("payment_submitted", "2"));
        var farmerDeadline = _now.AddHours(24);
        var reminder = new OrderDeadline(OrderCommitment.TryParseUtc(Iso(farmerDeadline), out var at) ? at - OrderCommitment.ReminderLead : default,
            OrderDeadlines.Kind.FarmerReminder);
        var funded = Row("funded", "3", FarmerPending(24).Select(f => f.Key == "paymentVerifiedAt" ? (f.Key, Iso(_now)) : f)
            .Concat(With(reminder)).ToArray());
        Writes(funded);

        var result = await _repository.TryMarkFundedAsync(OrderId, new Dictionary<string, object>
        {
            { OrderCommitment.FarmerResponseField, OrderCommitment.Response.Pending },
            { OrderCommitment.FarmerDeadlineAtField, Iso(farmerDeadline) },
            { "paymentVerifiedAt", Iso(_now) },
            { OrderCommitment.TruckerResponseField, OrderCommitment.Response.None }
        });

        Assert.NotNull(result);
        var write = Assert.Single(_updates);
        Assert.True(SetsDeadline(write, reminder), write.UpdateExpression);
        Assert.Equal(reminder.AtIso, result![OrderDeadlines.AtField]);
    }

    [Fact]
    public async Task Update_CancellingAPendingOrder_RemovesTheDeadline_InTheSameWrite()
    {
        var reminder = OrderDeadlines.Next(DynamoDbHelper.FromItem(Row("funded", "3", FarmerPending(10))))!.Value;
        Stored(Row("funded", "3", FarmerPending(10).Concat(With(reminder)).ToArray()));
        Writes(Row("cancelled", "4", FarmerPending(10)));

        var result = await _repository.UpdateAsync(OrderId, new Dictionary<string, object> { { "status", "cancelled" } }, 3);

        Assert.True(result.IsSuccess);
        var write = Assert.Single(_updates);
        Assert.True(RemovesDeadline(write.UpdateExpression), write.UpdateExpression);
        Assert.DoesNotContain(":ndAt", write.ExpressionAttributeValues.Keys);
        Assert.Equal("#v = :expectedVersion", write.ConditionExpression);
    }

    [Fact]
    public async Task Update_NothingDueChanges_TheExpressionLeavesTheDeadlineAlone()
    {
        Stored(Row("created", "1"));
        Writes(Row("created", "2", ("paymentMethod", "telebirr")));

        var result = await _repository.UpdateAsync(OrderId, new Dictionary<string, object>
        {
            { "paymentMethod", "telebirr" },
            // A caller's deadline is never taken: the write derives it.
            { OrderDeadlines.AtField, "2099-01-01T00:00:00.0000000Z" },
            { OrderDeadlines.ShardField, OrderDeadlines.Shard }
        }, 1);

        Assert.True(result.IsSuccess);
        var write = Assert.Single(_updates);
        Assert.False(TouchesDeadline(write.UpdateExpression), write.UpdateExpression);
        Assert.DoesNotContain(write.ExpressionAttributeValues.Values, v => v.S == "2099-01-01T00:00:00.0000000Z");
    }

    [Fact]
    public async Task Stamp_TheReminderMovesTheDeadlineToTheWindowsEnd()
    {
        var row = Row("funded", "3", FarmerPending(5));
        var reminder = OrderDeadlines.Next(DynamoDbHelper.FromItem(row))!.Value;
        Assert.Equal(OrderDeadlines.Kind.FarmerReminder, reminder.Kind);
        Stored(Row("funded", "3", FarmerPending(5).Concat(With(reminder)).ToArray()));
        var windowEnd = OrderCommitment.TryParseUtc(Iso(_now.AddHours(5)), out var end) ? end : default;
        var expected = new OrderDeadline(windowEnd, OrderDeadlines.Kind.FarmerResponse);
        Writes(Row("funded", "3", FarmerPending(5).Append((OrderCommitment.FarmerReminderAtField, Iso(_now)))
            .Concat(With(expected)).ToArray()));

        var stamped = await _repository.TryStampAsync(OrderId,
            new Dictionary<string, object> { { OrderCommitment.FarmerReminderAtField, Iso(_now) } },
            new Dictionary<string, string> { { "status", "funded" } });

        Assert.True(stamped);
        var write = Assert.Single(_updates);
        Assert.True(SetsDeadline(write, expected), write.UpdateExpression);
        // A stamp never bumps the version.
        Assert.DoesNotContain("version", write.ExpressionAttributeNames.Values);
    }

    [Fact]
    public async Task Stamp_ThePickupNotice_MovesTheEntryToTheWatchPartition_AtThePickupDeadline()
    {
        // FUP-170: the order stays in the index after its notice, where only the admin reads.
        var pickupBy = _now.AddHours(-2);
        var pickupAt = PickupDeadline.TryParse(PickupDeadline.Format(pickupBy), out var at) ? at : default;
        var overdue = new OrderDeadline(pickupAt, OrderDeadlines.Kind.PickupOverdue);
        var waiting = new OrderDeadline(pickupAt, OrderDeadlines.Kind.PickupWaiting);
        Stored(Row("accepted", "5", new[] { (PickupDeadline.Field, PickupDeadline.Format(pickupBy)) }.Concat(With(overdue)).ToArray()));
        Writes(Row("accepted", "5", new[]
        {
            (PickupDeadline.Field, PickupDeadline.Format(pickupBy)), (OrderCommitment.PickupOverdueNotifiedAtField, Iso(_now))
        }.Concat(With(waiting)).ToArray()));

        var stamped = await _repository.TryStampAsync(OrderId,
            new Dictionary<string, object> { { OrderCommitment.PickupOverdueNotifiedAtField, Iso(_now) } },
            new Dictionary<string, string> { { "status", "accepted" } });

        Assert.True(stamped);
        var write = Assert.Single(_updates);
        Assert.True(SetsDeadline(write, waiting), write.UpdateExpression);
        Assert.Equal(OrderDeadlines.WatchShard, write.ExpressionAttributeValues[":ndShard"].S);
    }

    [Fact]
    public async Task Update_ThePickup_TakesTheWaitOutOfTheIndex()
    {
        var pickupBy = _now.AddHours(-30);
        var fields = new[]
        {
            (PickupDeadline.Field, PickupDeadline.Format(pickupBy)), (OrderCommitment.PickupOverdueNotifiedAtField, Iso(_now.AddHours(-29)))
        };
        var waiting = OrderDeadlines.Next(DynamoDbHelper.FromItem(Row("accepted", "6", fields)))!.Value;
        Assert.Equal(OrderDeadlines.Kind.PickupWaiting, waiting.Kind);
        Stored(Row("accepted", "6", fields.Concat(With(waiting)).ToArray()));
        Writes(Row("pickup_confirmed", "7", fields));

        var result = await _repository.UpdateAsync(OrderId, new Dictionary<string, object> { { "status", "pickup_confirmed" } }, 6);

        Assert.True(result.IsSuccess);
        var write = Assert.Single(_updates);
        Assert.True(RemovesDeadline(write.UpdateExpression), write.UpdateExpression);
    }

    [Fact]
    public async Task AdvanceStatus_ToAwaitingTransport_TheBuyersChoiceStarts()
    {
        Stored(Row("accepted", "5", (PickupDeadline.Field, PickupDeadline.Format(_now.AddHours(-2)))));
        var choiceBy = _now.AddHours(48);
        var reminder = new OrderDeadline(
            (OrderCommitment.TryParseUtc(Iso(choiceBy), out var by) ? by : default) - OrderCommitment.ReminderLead,
            OrderDeadlines.Kind.BuyerChoiceReminder);
        Writes(Row(OrderCommitment.AwaitingTransportStatus, "6",
            new[] { (OrderCommitment.BuyerChoiceDeadlineAtField, Iso(choiceBy)) }.Concat(With(reminder)).ToArray()));

        var advanced = await _repository.TryAdvanceStatusAsync(OrderId, "accepted", OrderCommitment.AwaitingTransportStatus,
            new Dictionary<string, object> { { OrderCommitment.BuyerChoiceDeadlineAtField, Iso(choiceBy) } });

        Assert.NotNull(advanced);
        var write = Assert.Single(_updates);
        Assert.True(SetsDeadline(write, reminder), write.UpdateExpression);
    }

    [Fact]
    public async Task LinkedUpdate_TheTransactionCarriesTheDeadline_AndTheRowReturnedHasIt()
    {
        var pickupBy = _now.AddHours(40);
        Stored(Row("funded", "3", FarmerPending(10)));
        TransactWriteItemsRequest? sent = null;
        _ddb.TransactWriteItemsAsync(Arg.Do<TransactWriteItemsRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new TransactWriteItemsResponse());

        var result = await _repository.TryUpdateWithAsync(OrderId, new Dictionary<string, object>
        {
            { "status", "accepted" },
            { OrderCommitment.FarmerResponseField, OrderCommitment.Response.Accepted },
            { PickupDeadline.Field, PickupDeadline.Format(pickupBy) }
        }, 3, "funded", Array.Empty<TransactWriteItem>());

        Assert.Equal(OrderLinkedWriteOutcome.Written, result.Outcome);
        PickupDeadline.TryParse(PickupDeadline.Format(pickupBy), out var pickupAt);
        var expected = new OrderDeadline(pickupAt, OrderDeadlines.Kind.PickupOverdue);
        var update = sent!.TransactItems[0].Update;
        Assert.Contains("#ndAt = :ndAt", update.UpdateExpression);
        Assert.Equal(expected.AtIso, update.ExpressionAttributeValues[":ndAt"].S);
        Assert.Equal(expected.Kind, update.ExpressionAttributeValues[":ndKind"].S);
        Assert.Equal(expected.AtIso, result.Order![OrderDeadlines.AtField]);
        await _ddb.DidNotReceive().UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    // ===== a stale read before the write =====

    [Fact]
    public async Task StaleRead_TheRowWrittenDisagrees_CorrectedOnceOnTheVersionWritten()
    {
        // The read before the write missed the funding; the row written is funded and due for the reminder.
        Stored(Row("payment_submitted", "2"));
        var written = Row("funded", "3", FarmerPending(10));
        var expected = OrderDeadlines.Next(DynamoDbHelper.FromItem(written))!.Value;
        Writes(written);

        var result = await _repository.UpdateAsync(OrderId, new Dictionary<string, object> { { "cbeTransactionId", "CBE-1" } }, 2);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, _updates.Count);
        var correction = _updates[1];
        Assert.Equal("attribute_exists(orderId) AND #v = :v", correction.ConditionExpression);
        Assert.Equal("3", correction.ExpressionAttributeValues[":v"].N);
        Assert.True(SetsDeadline(correction, expected), correction.UpdateExpression);
        Assert.Equal(expected.AtIso, result.Value![OrderDeadlines.AtField]);
    }

    [Fact]
    public async Task StaleRead_TheCorrectionFails_TheWriteStillSucceeds()
    {
        Stored(Row("payment_submitted", "2"));
        Writes(Row("funded", "3", FarmerPending(10)), new AmazonDynamoDBException("throttled"));

        var result = await _repository.UpdateAsync(OrderId, new Dictionary<string, object> { { "cbeTransactionId", "CBE-1" } }, 2);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, _updates.Count);
        Assert.False(result.Value!.ContainsKey(OrderDeadlines.AtField));
    }

    // ===== the sweep's read and its own write =====

    [Fact]
    public async Task QueryDueDeadlines_ReadsTheSparseIndexOldestFirst_NeverScans()
    {
        var startKey = new Dictionary<string, AttributeValue> { { "orderId", S("order-0") } };
        var lastKey = new Dictionary<string, AttributeValue> { { "orderId", S(OrderId) } };
        QueryRequest? sent = null;
        _ddb.QueryAsync(Arg.Is<QueryRequest>(r => r.IndexName == OrderDeadlines.IndexName), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                sent = ci.Arg<QueryRequest>();
                return new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { new() { { "orderId", S(OrderId) } } },
                    LastEvaluatedKey = lastKey
                };
            });

        var (items, next) = await _repository.QueryDueDeadlinesAsync("2026-11-07T10:00:00.0000000Z", 0, startKey);

        Assert.NotNull(sent);
        Assert.Equal(TableName, sent!.TableName);
        Assert.Equal("#shard = :shard AND #at < :due", sent.KeyConditionExpression);
        Assert.Equal(OrderDeadlines.ShardField, sent.ExpressionAttributeNames["#shard"]);
        Assert.Equal(OrderDeadlines.AtField, sent.ExpressionAttributeNames["#at"]);
        Assert.Equal(OrderDeadlines.Shard, sent.ExpressionAttributeValues[":shard"].S);
        Assert.Equal("2026-11-07T10:00:00.0000000Z", sent.ExpressionAttributeValues[":due"].S);
        Assert.True(sent.ScanIndexForward);
        Assert.Equal(100, sent.Limit);
        Assert.Same(startKey, sent.ExclusiveStartKey);
        Assert.Equal(OrderId, Assert.Single(items)["orderId"]);
        Assert.Same(lastKey, next);
        await _ddb.DidNotReceive().ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryDeadlinePartition_ReadsTheNamedPartition_TheSweepsReadStaysOnItsOwn()
    {
        var sent = new List<QueryRequest>();
        _ddb.QueryAsync(Arg.Is<QueryRequest>(r => r.IndexName == OrderDeadlines.IndexName), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                sent.Add(ci.Arg<QueryRequest>());
                return new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() };
            });

        await _repository.QueryDeadlinePartitionAsync(OrderDeadlines.WatchShard, "2026-11-07T10:00:00.0000000Z", 20, null);
        await _repository.QueryDueDeadlinesAsync("2026-11-07T10:00:00.0000000Z", 20, null);

        Assert.Equal(2, sent.Count);
        Assert.Equal(OrderDeadlines.WatchShard, sent[0].ExpressionAttributeValues[":shard"].S);
        Assert.Equal("#shard = :shard AND #at < :due", sent[0].KeyConditionExpression);
        Assert.Equal(20, sent[0].Limit);
        Assert.True(sent[0].ScanIndexForward);
        Assert.Equal(OrderDeadlines.Shard, sent[1].ExpressionAttributeValues[":shard"].S);
        await _ddb.DidNotReceive().ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TrySetDeadline_SetsOnlyWhileTheVersionIsTheOneRead()
    {
        var due = new OrderDeadline(_now.AddHours(1), OrderDeadlines.Kind.PickupOverdue);
        Writes(new Dictionary<string, AttributeValue>());

        Assert.True(await _repository.TrySetDeadlineAsync(OrderId, CreatedAt, 7, due));

        var write = Assert.Single(_updates);
        Assert.Equal(OrderId, write.Key["orderId"].S);
        Assert.Equal(CreatedAt, write.Key["createdAt"].S);
        Assert.Equal("attribute_exists(orderId) AND #v = :v", write.ConditionExpression);
        Assert.Equal("7", write.ExpressionAttributeValues[":v"].N);
        Assert.True(SetsDeadline(write, due), write.UpdateExpression);
        Assert.DoesNotContain("updatedAt", write.ExpressionAttributeNames.Values);
    }

    [Fact]
    public async Task TrySetDeadline_Null_RemovesTheThreeAttributes()
    {
        Writes(new Dictionary<string, AttributeValue>());

        Assert.True(await _repository.TrySetDeadlineAsync(OrderId, CreatedAt, 7, null));

        var write = Assert.Single(_updates);
        Assert.Equal("REMOVE #ndAt, #ndKind, #ndShard", write.UpdateExpression);
    }

    [Fact]
    public async Task TrySetDeadline_TheVersionMoved_ReturnsFalse()
    {
        _ddb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns<UpdateItemResponse>(_ => throw new ConditionalCheckFailedException("version moved"));

        Assert.False(await _repository.TrySetDeadlineAsync(OrderId, CreatedAt, 7, null));
    }
}
