using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// FUP-05: a claim write that meets a record's transaction in flight on the same row gets
/// <c>TransactionConflictException</c>, not a failed condition. It is retried once, then reported like a failed
/// condition (false), so the service reads the row again and answers a 409 instead of a 500.
/// </summary>
public class ManualPayoutClaimRepositoryTests
{
    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();

    private ManualPayoutClaimRepository Repo() => new(_dynamoDb, "escrows-test", "ledger-test", "idempotency-test")
    {
        ConflictRetryDelay = TimeSpan.Zero
    };

    private Task<bool> Claim() => Repo().TryClaimLegAsync(
        "escrow-1", "2026-11-01T10:00:00.0000000Z", "refund", "buyer", "admin-a",
        "2026-11-12T10:00:00.0000000Z", "2026-11-12T10:30:00.0000000Z");

    [Fact]
    public async Task AWriteThatMeetsATransactionOnce_IsRetried_AndLands()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromException<UpdateItemResponse>(new TransactionConflictException("in flight")),
                _ => Task.FromResult(new UpdateItemResponse()));

        (await Claim()).Should().BeTrue();
        await _dynamoDb.Received(2).UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AWriteThatStaysBusy_IsReportedAsNotWritten_AfterOneRetry()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TransactionConflictException("in flight"));

        (await Claim()).Should().BeFalse();
        (await Repo().TryReleaseLedgerRowAsync("escrow-1", "release#farmer", "admin-a")).Should().BeFalse();
        await _dynamoDb.Received(4).UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailedCondition_IsNotRetried()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("claimed"));

        (await Claim()).Should().BeFalse();
        await _dynamoDb.Received(1).UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }
}
