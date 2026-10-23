using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-27: the sync Lambda's dispute lines. DELIVERY_DISPUTE_FROZEN feeds the escrow dispute-rate
/// metric (before, only POST /escrow/dispute counted), once per escrow a delivery dispute froze.
/// DISPUTE_PROPAGATION_FAILED reaches the sync log group when the order does not follow the freeze.
/// Uses the ESC-07 dispute rig of <see cref="SyncFunctionTests"/>.
/// </summary>
public partial class SyncFunctionTests
{
    private const string DisputeRateToken = "DELIVERY_DISPUTE_FROZEN";

    private string DisputeRateLogs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    private int DisputeRateLines => DisputeRateLogs.Split('\n').Count(l => l.Contains(DisputeRateToken));

    [Fact]
    public async Task DeliveryDispute_FreezingTheEscrow_LogsOneDisputeRateLine()
    {
        var rig = DisputeFunction(OrderEscrowRow());

        var response = await SendDeliveryDisputeAsync(rig.Function);

        Assert.Contains("success", response.Body);
        Assert.Contains(
            "DELIVERY_DISPUTE_FROZEN escrowId=escrow-order-order-001 orderId=order-001 deliveryId=del-001", DisputeRateLogs);
        Assert.Equal(1, DisputeRateLines);
    }

    [Fact]
    public async Task DeliveryDispute_EscrowAlreadyDisputed_IsNotCountedAgain()
    {
        // POST /escrow/dispute froze it (and its audit line counted it): the delivery dispute only
        // finishes the delivery and the order.
        var rig = DisputeFunction(OrderEscrowRow(status: "disputed"));

        var response = await SendDeliveryDisputeAsync(rig.Function);

        Assert.Contains("success", response.Body);
        Assert.Equal(0, DisputeRateLines);
    }

    [Fact]
    public async Task DeliveryDispute_Refused_IsNotCounted()
    {
        // Clients before sync-protocol 1.12.0 resend a refused dispute on every reconnect.
        var escrow = OrderEscrowRow();
        escrow["manualPayout"] = "open";
        escrow["manualPayoutOp"] = "release";
        var rig = DisputeFunction(escrow);

        var response = await SendDeliveryDisputeAsync(rig.Function);

        Assert.Contains("DELIVERY_DISPUTE_REFUSED", response.Body);
        Assert.Equal(0, DisputeRateLines);
    }

    [Fact]
    public async Task DeliveryDispute_NoEscrow_LogsItsOwnAlarm_NotADisputeRateLine()
    {
        var rig = DisputeFunction(null);

        var response = await SendDeliveryDisputeAsync(rig.Function);

        Assert.Contains("DELIVERY_DISPUTE_REFUSED", response.Body);
        Assert.Contains("DISPUTE_NO_ESCROW orderId=order-001 deliveryId=del-001", DisputeRateLogs);
        Assert.Equal(0, DisputeRateLines);
    }

    [Fact]
    public async Task DeliveryDispute_FreezeUndecided_IsNotCounted()
    {
        var rig = DisputeFunction(null);
        rig.EscrowRepo.GetByIdAsync(OrderEscrowId, true).ThrowsAsync(new ProvisionedThroughputExceededException("slow down"));

        var response = await SendDeliveryDisputeAsync(rig.Function);

        Assert.Contains("DELIVERY_DISPUTE_RETRY", response.Body);
        Assert.Equal(0, DisputeRateLines);
    }

    [Fact]
    public async Task DeliveryDispute_RetriedAfterAFailedDeliveryWrite_IsCountedOnce()
    {
        // The freeze landed, the delivery write was throttled; the retry finds the escrow already
        // disputed. One dispute, one line.
        var rig = DisputeFunction(null);
        rig.EscrowRepo.GetByIdAsync(OrderEscrowId, true).Returns(OrderEscrowRow(), OrderEscrowRow(status: "disputed"));
        var deliveredItem = DeliveryItemForOrder(status: "delivered", confirmedAt: DateTime.UtcNow.AddHours(-2).ToString("o"));
        var deliveryWrites = 0;
        _mockDynamoDb.UpdateItemAsync(Arg.Is<UpdateItemRequest>(r => r.TableName == "test-deliveries-table"))
            .Returns(_ => ++deliveryWrites == 1
                ? throw new ProvisionedThroughputExceededException("throttled")
                : new UpdateItemResponse { Attributes = deliveredItem });

        var first = await SendDeliveryDisputeAsync(rig.Function);
        var retry = await SendDeliveryDisputeAsync(rig.Function);

        Assert.Contains("error", first.Body);
        Assert.Contains("success", retry.Body);
        Assert.Equal(1, DisputeRateLines);
    }

    [Fact]
    public async Task DeliveryDispute_DeliveryWriteConflictsAfterTheFreeze_IsStillCounted()
    {
        // The money is frozen whatever the delivery write does: the dispute happened.
        var rig = DisputeFunction(OrderEscrowRow());
        _mockDynamoDb.UpdateItemAsync(Arg.Is<UpdateItemRequest>(r => r.TableName == "test-deliveries-table"))
            .ThrowsAsync(new ConditionalCheckFailedException("version moved"));
        rig.DeliveryRepo.TryMarkDisputedAsync("del-001", "buyer-001", "wet bags").Returns(true);

        var response = await SendDeliveryDisputeAsync(rig.Function);

        Assert.Contains("conflict", response.Body);
        Assert.Equal(1, DisputeRateLines);
    }

    [Fact]
    public async Task DeliveryDispute_AgainOnADisputedDelivery_FreezingALegacyEscrow_IsNotCounted()
    {
        // A delivery disputed before ESC-07 never froze its escrow; the repeat freezes it now. That is
        // a repair of an old dispute (like the escrow-cron sweep freeze), not a new one.
        var rig = DisputeFunction(OrderEscrowRow());
        SetupDeliveryQuery(DeliveryItemForOrder(status: "disputed", version: 2,
            confirmedAt: DateTime.UtcNow.AddHours(-2).ToString("o")));

        var response = await SendDeliveryDisputeAsync(rig.Function, version: 1);

        Assert.Contains("already disputed", response.Body);
        await rig.EscrowRepo.Received(1).UpdateStatusAsync(
            OrderEscrowId, Arg.Any<string>(), "disputed", "escrowed", Arg.Any<Dictionary<string, object>?>());
        Assert.Equal(0, DisputeRateLines);
    }

    [Fact]
    public async Task DeliveryDispute_OrderDoesNotFollow_LogsThePropagationAlarmLine_AndStillSucceeds()
    {
        // The escrow is frozen and the delivery written, but the order keeps losing its version
        // check: the mirror gives up and the sync Lambda's stdout carries the alarm line.
        var rig = DisputeFunction(OrderEscrowRow());
        var alarmLines = new List<string>();
        var service = new EscrowService(rig.EscrowRepo, rig.AuditRepo, idempotencyRepo: null, telebirrGateway: null,
            orderRepo: _mockOrderRepo, deliveryRepo: rig.DeliveryRepo, transactionAuditRepo: rig.TxRepo)
        {
            AlarmLog = alarmLines.Add
        };
        var function = new SyncFunction(_mockSyncMeta, _mockCropRepo, _mockDynamoDb,
            "test-trips-table", "test-deliveries-table",
            userRepo: _mockUserRepo, orderRepo: _mockOrderRepo, escrowRepo: rig.EscrowRepo,
            transactionAuditRepo: rig.TxRepo, orderEscrowService: service);
        _mockOrderRepo.UpdateAsync("order-001", Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "Version conflict."));

        var response = await SendDeliveryDisputeAsync(function);

        Assert.Contains("success", response.Body);
        Assert.Equal(
            new[] { "DISPUTE_PROPAGATION_FAILED escrowId=escrow-order-order-001 orderId=order-001 deliveryId=del-001 legs=order:conflict" },
            alarmLines);
        Assert.Equal(1, DisputeRateLines);
    }
}
