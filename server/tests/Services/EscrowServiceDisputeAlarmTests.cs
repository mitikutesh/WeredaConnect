using System.Globalization;
using Amazon.DynamoDBv2;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FUP-27: a dispute mirror leg that fails logs DISPUTE_PROPAGATION_FAILED through the money
/// alarm sink (stdout in every Lambda), which the dispute-propagation-failed alarm counts in the
/// escrow, sync and escrow-cron log groups. Before, the line had no filter and no test.
/// </summary>
public class EscrowServiceDisputeAlarmTests
{
    private const string EscrowId = "escrow-001";
    private const string OrderId = "order-1";
    private const string DeliveryId = "delivery-001";
    private const string BuyerId = "buyer-456";

    private readonly IEscrowAuditRepository _auditRepo = Substitute.For<IEscrowAuditRepository>();
    private readonly IDeliveryRepository _deliveryRepo = Substitute.For<IDeliveryRepository>();
    private readonly IOrderRepository _orderRepo = Substitute.For<IOrderRepository>();
    private readonly List<string> _alarmLines = new();

    public EscrowServiceDisputeAlarmTests()
    {
        _deliveryRepo.GetByIdAsync(DeliveryId, true).Returns(new Dictionary<string, object>
        {
            { "deliveryId", DeliveryId }, { "orderId", OrderId }, { "status", "disputed" }
        });
        _orderRepo.GetByIdAsync(Arg.Any<string>(), true).Returns(Order("delivered", 2));
        _orderRepo.UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Success(Order("disputed", 3)));
    }

    private static Dictionary<string, object> Order(string status, int version) => new()
    {
        { "orderId", OrderId }, { "status", status }, { "version", version.ToString(CultureInfo.InvariantCulture) }
    };

    private EscrowService Service() => new(
        Substitute.For<IEscrowRepository>(), _auditRepo, idempotencyRepo: null, telebirrGateway: null,
        orderRepo: _orderRepo, deliveryRepo: _deliveryRepo,
        transactionAuditRepo: Substitute.For<ITransactionAuditRepository>())
    {
        AlarmLog = _alarmLines.Add
    };

    private void OrderWritesAlwaysConflict()
        => _orderRepo.UpdateAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<int>())
            .Returns(AuthResult<Dictionary<string, object>>.Failure(409, "SYNC_CONFLICT", "Version conflict."));

    [Fact]
    public async Task OrderLegGivesUp_LogsOneAlarmLine_BeforeTheAuditRow()
    {
        OrderWritesAlwaysConflict();
        var steps = new List<string>();
        _auditRepo.When(r => r.AppendAsync(Arg.Any<Dictionary<string, object>>())).Do(_ => steps.Add("audit"));
        var service = new EscrowService(
            Substitute.For<IEscrowRepository>(), _auditRepo, idempotencyRepo: null, telebirrGateway: null,
            orderRepo: _orderRepo, deliveryRepo: _deliveryRepo)
        {
            AlarmLog = line =>
            {
                _alarmLines.Add(line);
                steps.Add("alarm");
            }
        };

        var legs = await service.MirrorDisputeAsync(EscrowId, OrderId, DeliveryId, BuyerId, "wet bags", markDelivery: false);

        legs.Should().Be("order:conflict");
        _alarmLines.Should().Equal(
            "DISPUTE_PROPAGATION_FAILED escrowId=escrow-001 orderId=order-1 deliveryId=delivery-001 legs=order:conflict");
        // A failed audit write must not hide the failure from the alarm (ESC-13 ordering).
        steps.Should().Equal("alarm", "audit");
        await _auditRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["action"].ToString() == "dispute_propagation_failed" && e["metadata"].ToString() == "legs=order:conflict"));
    }

    [Fact]
    public async Task DeliveryLegThrows_LogsThatLeg_DefangedOnTheLine_RawInTheAudit()
    {
        _deliveryRepo.TryMarkDisputedAsync(DeliveryId, BuyerId, "wet bags")
            .ThrowsAsync(new AmazonDynamoDBException("throttled"));

        var legs = await Service().MirrorDisputeAsync(EscrowId, OrderId, DeliveryId, BuyerId, "wet bags", markDelivery: true);

        legs.Should().Be("delivery:[AmazonDynamoDBException]");
        _alarmLines.Should().Equal(
            "DISPUTE_PROPAGATION_FAILED escrowId=escrow-001 orderId=order-1 deliveryId=delivery-001 legs=delivery:AmazonDynamoDBException");
        await _auditRepo.Received(1).AppendAsync(Arg.Is<Dictionary<string, object>>(e =>
            e["metadata"].ToString() == "legs=delivery:[AmazonDynamoDBException]"));
        // The order leg still ran (SIM-47: legs are independent).
        await _orderRepo.Received(1).UpdateAsync(OrderId,
            Arg.Is<Dictionary<string, object>>(d => d["status"].ToString() == "disputed"), 2);
    }

    [Fact]
    public async Task EveryLegFollows_LogsNoAlarmLine()
    {
        var legs = await Service().MirrorDisputeAsync(EscrowId, OrderId, DeliveryId, BuyerId, "wet bags", markDelivery: false);

        legs.Should().BeNull();
        _alarmLines.Should().BeEmpty();
        await _auditRepo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task AuditWriteFails_TheAlarmLineIsStillWritten_AndTheMirrorDoesNotThrow()
    {
        OrderWritesAlwaysConflict();
        _auditRepo.AppendAsync(Arg.Any<Dictionary<string, object>>()).ThrowsAsync(new AmazonDynamoDBException("down"));

        var legs = await Service().MirrorDisputeAsync(EscrowId, OrderId, DeliveryId, BuyerId, "wet bags", markDelivery: false);

        legs.Should().Be("order:conflict");
        _alarmLines.Should().ContainSingle().Which.Should().StartWith("DISPUTE_PROPAGATION_FAILED escrowId=escrow-001 ");
    }

    [Fact]
    public async Task HostileIds_TheLineCarriesOnlyItsOwnToken()
    {
        // An order escrow's id embeds the client-chosen order id; the delivery id is client-chosen.
        var hostile = WeredaConnect.Server.Tests.Helpers.MoneyAlarmLogTests.Hostile;
        OrderWritesAlwaysConflict();
        _deliveryRepo.GetByIdAsync(Arg.Any<string>(), true).Returns(new Dictionary<string, object> { { "status", "disputed" } });

        await Service().MirrorDisputeAsync($"escrow-order-{hostile}", hostile, hostile, BuyerId, null, markDelivery: false);

        var line = _alarmLines.Should().ContainSingle().Subject;
        WeredaConnect.Server.Tests.Helpers.MoneyAlarmLogTests.AlarmTokens.Where(line.Contains)
            .Should().Equal("DISPUTE_PROPAGATION_FAILED");
        line.Split(' ').Should().HaveCount(5);
    }
}
