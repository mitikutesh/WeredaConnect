using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FUP-137: the journal of clean-delivery credits escrow-cron still owes, one item of the escrow idempotency
/// table holding a string set of escrow ids. No Query and no Scan: two updates per release, one read per run.
/// </summary>
public class CleanDeliveryCreditJournalTests
{
    private const string Table = "test-escrow-idempotency";

    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private UpdateItemRequest? _update;

    public CleanDeliveryCreditJournalTests()
    {
        _ddb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => _update = r), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());
    }

    private CleanDeliveryCreditJournal Journal() => new(_ddb, Table);

    [Fact]
    public async Task Add_AddsTheIdToTheSetOfTheOneItem_BoundedAndWithATtl()
    {
        var added = await Journal().AddAsync("esc-1");

        added.Should().BeTrue();
        _update!.TableName.Should().Be(Table);
        _update.Key.Should().ContainSingle().Which.Value.S.Should().Be(CleanDeliveryCreditJournal.Key);
        _update.Key.Should().ContainKey("idempotencyKey");
        _update.UpdateExpression.Should().Be("ADD #pending :ids SET #expires = :expires");
        _update.ExpressionAttributeNames["#pending"].Should().Be(CleanDeliveryCreditJournal.PendingAttribute);
        _update.ExpressionAttributeNames["#expires"].Should().Be("expiresAt");
        _update.ExpressionAttributeValues[":ids"].SS.Should().Equal("esc-1");
        _update.ConditionExpression.Should().Be("attribute_not_exists(#pending) OR size(#pending) < :max");
        _update.ExpressionAttributeValues[":max"].N.Should().Be("500");
        var expires = long.Parse(_update.ExpressionAttributeValues[":expires"].N);
        expires.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds(), 60);
    }

    [Fact]
    public async Task Add_AFullJournalRefusesTheId()
    {
        _ddb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("full"));

        (await Journal().AddAsync("esc-1")).Should().BeFalse();
    }

    [Fact]
    public async Task Remove_DeletesTheIdFromTheSet()
    {
        await Journal().RemoveAsync("esc-1");

        _update!.TableName.Should().Be(Table);
        _update.Key["idempotencyKey"].S.Should().Be(CleanDeliveryCreditJournal.Key);
        _update.UpdateExpression.Should().Be("DELETE #pending :ids");
        _update.ExpressionAttributeValues[":ids"].SS.Should().Equal("esc-1");
        _update.ConditionExpression.Should().BeNull();
    }

    [Fact]
    public async Task Read_ReturnsTheIdsOfTheItem_WithAConsistentGet()
    {
        _ddb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>()).Returns(new GetItemResponse
        {
            Item = new Dictionary<string, AttributeValue>
            {
                ["idempotencyKey"] = new() { S = CleanDeliveryCreditJournal.Key },
                [CleanDeliveryCreditJournal.PendingAttribute] = new() { SS = new List<string> { "esc-b", "esc-a" } }
            }
        });

        var pending = await Journal().ReadAsync();

        pending.Should().Equal("esc-a", "esc-b");
        await _ddb.Received(1).GetItemAsync(
            Arg.Is<GetItemRequest>(g => g.TableName == Table && g.ConsistentRead == true
                                        && g.Key["idempotencyKey"].S == CleanDeliveryCreditJournal.Key),
            Arg.Any<CancellationToken>());
        await _ddb.DidNotReceive().QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>());
        await _ddb.DidNotReceive().ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)] // no item yet
    [InlineData(true)] // the item is there and its set is empty (DynamoDB drops an empty set attribute)
    public async Task Read_WithNothingPending_IsEmpty(bool itemExists)
    {
        var item = new Dictionary<string, AttributeValue>();
        if (itemExists)
        {
            item["idempotencyKey"] = new AttributeValue { S = CleanDeliveryCreditJournal.Key };
        }

        _ddb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse { Item = item });

        (await Journal().ReadAsync()).Should().BeEmpty();
    }
}
