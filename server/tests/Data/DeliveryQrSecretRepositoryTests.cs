using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// T-1: the typed-code misses live in the QR secret row as one string set of operation ids per
/// leg. A new nonce (the farmer's Regenerate) clears them; the same nonce again keeps them.
/// </summary>
public class DeliveryQrSecretRepositoryTests
{
    private const string Table = "qr-secrets-test";

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly DeliveryQrSecretRepository _repo;
    private readonly List<UpdateItemRequest> _updates = new();

    public DeliveryQrSecretRepositoryTests()
    {
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => _updates.Add(r)), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());
        _repo = new DeliveryQrSecretRepository(_dynamoDb, Table);
    }

    [Fact]
    public async Task PutNonce_ANewNonce_ClearsTheMissesOfBothLegs()
    {
        await _repo.PutNonceAsync("del-1", "nonce-2");

        _updates.Should().ContainSingle();
        var put = _updates[0];
        put.TableName.Should().Be(Table);
        put.Key["deliveryId"].S.Should().Be("del-1");
        put.UpdateExpression.Should().Be(
            "SET #nonce = :nonce, #issuedAt = if_not_exists(#issuedAt, :now), #exp = :exp REMOVE #missP, #missD");
        put.ConditionExpression.Should().Be("attribute_not_exists(#nonce) OR #nonce <> :nonce");
        put.ExpressionAttributeNames["#missP"].Should().Be("codeMissesPickedUp");
        put.ExpressionAttributeNames["#missD"].Should().Be("codeMissesDelivered");
        put.ExpressionAttributeValues[":nonce"].S.Should().Be("nonce-2");
    }

    [Fact]
    public async Task PutNonce_TheSameNonceAgain_KeepsTheMisses()
    {
        _dynamoDb.UpdateItemAsync(
                Arg.Is<UpdateItemRequest>(r => r.ConditionExpression != null), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("same nonce"));

        await _repo.PutNonceAsync("del-1", "nonce-1");

        var refresh = _updates.Last();
        refresh.ConditionExpression.Should().BeNull();
        refresh.UpdateExpression.Should().Be("SET #nonce = :nonce, #issuedAt = if_not_exists(#issuedAt, :now), #exp = :exp");
        refresh.ExpressionAttributeNames.Should().NotContainKey("#missP");
    }

    [Theory]
    [InlineData("pickedUp", "codeMissesPickedUp")]
    [InlineData("delivered", "codeMissesDelivered")]
    public async Task TakeAttempt_AddsTheOperation_OnlyBelowTheCap_AndWhileTheNonceIsTheOneChecked(
        string leg, string attribute)
    {
        UpdateItemRequest? sent = null;
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse
            {
                Attributes = new Dictionary<string, AttributeValue>
                {
                    { attribute, new AttributeValue { SS = new List<string> { "op-1", "op-2", "op-3" } } }
                }
            });

        var attempt = await _repo.TryTakeCodeAttemptAsync("del-1", leg, "op-3:abc", "nonce-1", 5);

        attempt.Should().Be(new CodeAttempt(CodeAttemptOutcome.Taken, 3));
        sent!.UpdateExpression.Should().Be("ADD #m :ops");
        // One conditional write: parallel guesses are serialised on the row and cannot pass the
        // cap; the operation that already holds an attempt (its retry) gets in again.
        sent.ConditionExpression.Should().Be(
            "#nonce = :nonce AND (attribute_not_exists(#m) OR size(#m) < :max OR contains(#m, :op))");
        sent.ExpressionAttributeNames["#m"].Should().Be(attribute);
        sent.ExpressionAttributeValues[":ops"].SS.Should().Equal("op-3:abc");
        sent.ExpressionAttributeValues[":op"].S.Should().Be("op-3:abc");
        sent.ExpressionAttributeValues[":nonce"].S.Should().Be("nonce-1");
        sent.ExpressionAttributeValues[":max"].N.Should().Be("5");
        sent.ReturnValues.Should().Be(ReturnValue.UPDATED_NEW);
        sent.ReturnValuesOnConditionCheckFailure.Should().Be(ReturnValuesOnConditionCheckFailure.ALL_OLD);
    }

    [Fact]
    public async Task TakeAttempt_AtTheCap_IsLocked()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("full")
            {
                Item = new Dictionary<string, AttributeValue>
                {
                    { "nonce", new AttributeValue { S = "nonce-1" } }
                }
            });

        (await _repo.TryTakeCodeAttemptAsync("del-1", "pickedUp", "op-6", "nonce-1", 5))
            .Outcome.Should().Be(CodeAttemptOutcome.Locked);
    }

    [Fact]
    public async Task TakeAttempt_AfterARegenerate_IsNonceChanged()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("nonce changed")
            {
                Item = new Dictionary<string, AttributeValue>
                {
                    { "nonce", new AttributeValue { S = "new-nonce" } }
                }
            });

        (await _repo.TryTakeCodeAttemptAsync("del-1", "pickedUp", "op-1", "old-nonce", 5))
            .Outcome.Should().Be(CodeAttemptOutcome.NonceChanged);
    }

    [Fact]
    public async Task TakeAttempt_AnotherStatus_WritesNothing()
    {
        (await _repo.TryTakeCodeAttemptAsync("del-1", "inTransit", "op-1", "nonce-1", 5))
            .Outcome.Should().Be(CodeAttemptOutcome.Locked);

        _updates.Should().BeEmpty();
    }

    [Fact]
    public async Task ReturnAttempt_RemovesTheOperationFromTheLegsSet()
    {
        await _repo.ReturnCodeAttemptAsync("del-1", "delivered", "op-1");

        var sent = _updates.Should().ContainSingle().Subject;
        sent.UpdateExpression.Should().Be("DELETE #m :ops");
        sent.ExpressionAttributeNames["#m"].Should().Be("codeMissesDelivered");
        sent.ExpressionAttributeValues[":ops"].SS.Should().Equal("op-1");
    }

    [Fact]
    public async Task Get_ReadsTheMissesStronglyConsistent()
    {
        GetItemRequest? read = null;
        _dynamoDb.GetItemAsync(Arg.Do<GetItemRequest>(r => read = r), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse
            {
                Item = new Dictionary<string, AttributeValue>
                {
                    { "deliveryId", new AttributeValue { S = "del-1" } },
                    { "nonce", new AttributeValue { S = "nonce-1" } },
                    { "codeMissesPickedUp", new AttributeValue { SS = new List<string> { "a", "b" } } },
                    { "codeMissesDelivered", new AttributeValue { SS = new List<string> { "c" } } }
                }
            });

        var secret = await _repo.GetAsync("del-1");

        read!.ConsistentRead.Should().BeTrue();
        secret!.CodeMissesFor("pickedUp").Should().Be(2);
        secret.CodeMissesFor("delivered").Should().Be(1);
        secret.CodeMissesFor("inTransit").Should().Be(0);
        secret.DropoffNonce.Should().BeNull();
    }

    // ---- ESC-35: the trucker's drop-off nonce ----

    [Fact]
    public async Task PutDropoffNonce_ANewNonce_NextToTheFarmersOnly_RestartsItsFreshness_AndClearsTheDropOffMisses()
    {
        (await _repo.PutDropoffNonceAsync("del-1", "d.nonce-2")).Should().BeTrue();

        var put = _updates.Should().ContainSingle().Subject;
        put.TableName.Should().Be(Table);
        put.Key["deliveryId"].S.Should().Be("del-1");
        put.UpdateExpression.Should().Be("SET #dn = :dn, #dIssuedAt = :now, #exp = :exp REMOVE #missD");
        put.ConditionExpression.Should().Be("attribute_exists(#nonce) AND (attribute_not_exists(#dn) OR #dn <> :dn)");
        put.ExpressionAttributeNames["#nonce"].Should().Be("nonce");
        put.ExpressionAttributeNames["#dn"].Should().Be("dropoffNonce");
        put.ExpressionAttributeNames["#dIssuedAt"].Should().Be("dropoffIssuedAt");
        // Only the drop-off leg's misses go; the pickup's stay as they were.
        put.ExpressionAttributeNames["#missD"].Should().Be("codeMissesDelivered");
        put.ExpressionAttributeNames.Values.Should().NotContain("codeMissesPickedUp");
        put.ExpressionAttributeValues[":dn"].S.Should().Be("d.nonce-2");
        put.ReturnValuesOnConditionCheckFailure.Should().Be(ReturnValuesOnConditionCheckFailure.ALL_OLD);
    }

    [Fact]
    public async Task PutDropoffNonce_TheSameNonceAgain_KeepsItsFreshnessAndMisses()
    {
        _dynamoDb.UpdateItemAsync(
                Arg.Is<UpdateItemRequest>(r => r.UpdateExpression.Contains("REMOVE")), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("same nonce")
            {
                Item = new Dictionary<string, AttributeValue>
                {
                    { "nonce", new AttributeValue { S = "nonce-1" } },
                    { "dropoffNonce", new AttributeValue { S = "d.nonce-2" } }
                }
            });

        (await _repo.PutDropoffNonceAsync("del-1", "d.nonce-2")).Should().BeTrue();

        var refresh = _updates.Last();
        refresh.UpdateExpression.Should().Be("SET #exp = :exp");
        refresh.ConditionExpression.Should().Be("#dn = :dn");
        refresh.ExpressionAttributeNames.Should().NotContainKey("#missD");
    }

    [Fact]
    public async Task PutDropoffNonce_WithoutTheFarmersNonce_StoresNothing()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("no row")
            {
                Item = new Dictionary<string, AttributeValue>()
            });

        (await _repo.PutDropoffNonceAsync("del-1", "d.nonce-2")).Should().BeFalse();

        // No second write to refresh a row that is not there.
        await _dynamoDb.Received(1).UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TakeAttempt_ForTheTruckersCode_IsConditionalOnTheDropOffNonce()
    {
        UpdateItemRequest? sent = null;
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("new code")
            {
                Item = new Dictionary<string, AttributeValue>
                {
                    { "nonce", new AttributeValue { S = "d.old" } },
                    { "dropoffNonce", new AttributeValue { S = "d.new" } }
                }
            });

        var attempt = await _repo.TryTakeCodeAttemptAsync("del-1", "delivered", "op-1", "d.old", 5, dropoffNonce: true);

        sent!.ExpressionAttributeNames["#nonce"].Should().Be("dropoffNonce");
        // Read against the drop-off nonce, so the trucker's new code reads as a changed nonce.
        attempt.Outcome.Should().Be(CodeAttemptOutcome.NonceChanged);
    }

    [Fact]
    public async Task Get_ReadsTheDropOffNonce()
    {
        _dynamoDb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse
            {
                Item = new Dictionary<string, AttributeValue>
                {
                    { "deliveryId", new AttributeValue { S = "del-1" } },
                    { "nonce", new AttributeValue { S = "nonce-1" } },
                    { "dropoffNonce", new AttributeValue { S = "d.nonce-2" } },
                    { "dropoffIssuedAt", new AttributeValue { S = "2026-11-12T10:00:00.0000000Z" } }
                }
            });

        var secret = await _repo.GetAsync("del-1");

        secret!.Nonce.Should().Be("nonce-1");
        secret.DropoffNonce.Should().Be("d.nonce-2");
        secret.DropoffIssuedAt.Should().Be("2026-11-12T10:00:00.0000000Z");
    }
}
