using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// SIM-29: every escrow write that changes what the escrow reads return adds 1 to <c>version</c>
/// with <c>ADD</c>, never a value computed from an earlier read, and keeps its own condition. That
/// covers the self-transitions (delivery link, confirmation stamp, scheduled refund, hold-cap stamp)
/// that used to leave the version alone. The integration suite runs the same writes against real
/// DynamoDB (<c>EscrowVersionDynamoDbTests</c>).
/// </summary>
public class EscrowRepositoryVersionTests
{
    private const string Table = "escrows-test";
    private const string EscrowId = "escrow-order-ord-29";
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly EscrowRepository _repo;
    private UpdateItemRequest? _update;

    public EscrowRepositoryVersionTests()
    {
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => _update = r), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());
        _repo = new EscrowRepository(_dynamoDb, Table);
    }

    /// <summary>The request adds exactly 1 to <c>version</c> and sets it nowhere else.</summary>
    private static void AssertAddsOneToTheVersion(UpdateItemRequest r)
    {
        r.UpdateExpression.Should().Contain("ADD #version :one");
        r.ExpressionAttributeNames["#version"].Should().Be("version");
        r.ExpressionAttributeValues[":one"].N.Should().Be("1");
        r.ExpressionAttributeNames.Where(n => n.Value == "version").Should().ContainSingle();
        r.ExpressionAttributeValues.Values.Where(v => v.N != null).Select(v => v.N).Should().NotContain(new[] { "2", "7", "99" });
    }

    [Fact]
    public async Task UpdateStatus_DeliveryLink_AddsToTheVersion_KeepsTheStatusCondition()
    {
        await _repo.UpdateStatusAsync(EscrowId, CreatedAt, "escrowed", "escrowed",
            new Dictionary<string, object> { { "deliveryId", "delivery-29" }, { "truckerId", "trucker-29" } });

        _update!.UpdateExpression.Should().StartWith("SET #status = :newStatus, #updated = :updated, ")
            .And.EndWith(" ADD #version :one");
        _update.ConditionExpression.Should().Be("#status = :expectedStatus");
        AssertAddsOneToTheVersion(_update);
    }

    [Fact]
    public async Task UpdateStatus_ConfirmationStamp_AddsToTheVersion()
    {
        await _repo.UpdateStatusAsync(EscrowId, CreatedAt, "escrowed", "escrowed", new Dictionary<string, object>
        {
            { "deliveryConfirmedAt", "2026-10-02T10:00:00.0000000Z" },
            { "disputeWindowEndsAt", "2026-10-04T10:00:00.0000000Z" },
            { "releaseNotBefore", "2026-10-05T10:00:00.0000000Z" }
        });

        _update!.ExpressionAttributeNames.Values.Should().Contain(new[] { "deliveryConfirmedAt", "disputeWindowEndsAt", "releaseNotBefore" });
        _update.ConditionExpression.Should().Be("#status = :expectedStatus");
        AssertAddsOneToTheVersion(_update);
    }

    [Theory]
    [InlineData("escrowed", "initiated", 2)]
    [InlineData("released", "escrowed", 7)]
    [InlineData("refunded", "escrowed", 7)]
    [InlineData("completed", "released", 99)]
    [InlineData("disputed", "escrowed", 7)]
    public async Task UpdateStatus_ACallersVersion_IsIgnored_TheVersionIsAddedTo(string newStatus, string from, int callerVersion)
    {
        await _repo.UpdateStatusAsync(EscrowId, CreatedAt, newStatus, from,
            new Dictionary<string, object> { { "version", callerVersion }, { "completedAt", "2026-10-02T10:00:00Z" } });

        AssertAddsOneToTheVersion(_update!);
    }

    [Fact]
    public async Task UpdateStatus_ToReleased_AddsToTheVersion_AndStillRemovesTheBlockReason()
    {
        await _repo.UpdateStatusAsync(EscrowId, CreatedAt, "released", "escrowed",
            new Dictionary<string, object> { { "releasedAt", "2026-10-02T10:00:00Z" } });

        _update!.UpdateExpression.Should().EndWith(" ADD #version :one REMOVE #blockReason, #blockReasonAt");
        _update.ConditionExpression.Should().Be("#status = :expectedStatus AND attribute_not_exists(#manualPayout)");
        AssertAddsOneToTheVersion(_update);
    }

    [Fact]
    public async Task UpdateStatus_WithoutAnExpectedStatus_StillAddsToTheVersion()
    {
        await _repo.UpdateStatusAsync(EscrowId, CreatedAt, "cancelled");

        _update!.ConditionExpression.Should().BeNull();
        AssertAddsOneToTheVersion(_update);
    }

    [Fact]
    public async Task ExtendExpiry_AddsToTheVersion_KeepsItsCondition()
    {
        (await _repo.TryExtendExpiryAsync(EscrowId, CreatedAt, "2026-10-20T10:00:00.0000000Z")).Should().BeTrue();

        _update!.UpdateExpression.Should().Be("SET #expiresAt = :expiresAt, #updated = :updated ADD #version :one");
        _update.ConditionExpression.Should().Be("#status = :escrowed AND attribute_not_exists(#refundReason) "
            + "AND attribute_not_exists(#lock) AND attribute_not_exists(#manualPayout)");
        AssertAddsOneToTheVersion(_update);
    }

    [Fact]
    public async Task BackfillTruckerId_AddsToTheVersion_KeepsItsCondition()
    {
        (await _repo.TryBackfillTruckerIdAsync(EscrowId, CreatedAt, "trucker-29")).Should().BeTrue();

        _update!.UpdateExpression.Should().Be("SET #truckerId = :truckerId, #updated = :now ADD #version :one");
        _update.ConditionExpression.Should().Be("attribute_exists(escrowId) AND attribute_not_exists(#truckerId)");
        AssertAddsOneToTheVersion(_update);
    }

    [Fact]
    public void RefundArm_AddsToTheVersion()
    {
        var arm = _repo.BuildRefundArm(EscrowId, CreatedAt, "order_cancelled", "2026-10-02T10:00:00.0000000Z").Update;

        arm.UpdateExpression.Should().EndWith(" ADD #version :one");
        arm.ExpressionAttributeNames["#version"].Should().Be("version");
        arm.ExpressionAttributeValues[":one"].N.Should().Be("1");
    }
}
