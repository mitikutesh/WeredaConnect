using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-37: escrow-cron's <c>order_payment_reconcile</c> action. A gateway with no hosted checkout (the mock)
/// asks nothing; a live one runs the reconciliation and answers its counts; an unwired one answers 503.
/// </summary>
public class EscrowCronPaymentReconcileTests
{
    private readonly IEscrowRepository _escrowRepo = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _auditRepo = Substitute.For<IEscrowAuditRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IOrderCheckoutRepository _checkouts = Substitute.For<IOrderCheckoutRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly TestLambdaContext _context = new() { RemainingTime = TimeSpan.FromMinutes(5) };

    public EscrowCronPaymentReconcileTests()
    {
        _orders.QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(new List<Dictionary<string, object>>
        {
            new() { ["orderId"] = "o-1", ["status"] = "payment_submitted", ["paymentSubmittedAt"] = DateTime.UtcNow.AddHours(-2).ToString("o") }
        });
    }

    private EscrowCronFunction Cron(bool hostedCheckout, bool wired = true)
    {
        _gateway.SupportsHostedCheckout.Returns(hostedCheckout);
        var service = new EscrowService(_escrowRepo, _auditRepo, null, _gateway);
        return new EscrowCronFunction(service, _escrowRepo, _auditRepo, _gateway, orderRepo: _orders)
        {
            PaymentReconciler = wired
                ? new OrderPaymentReconciler(_orders, _checkouts, Substitute.For<IWebhookInboxRepository>(), _gateway, _auditRepo)
                : null
        };
    }

    private static APIGatewayProxyRequest Request()
        => new() { Body = $"{{\"action\":\"{OrderPaymentReconciler.Action}\"}}" };

    [Fact]
    public async Task A_gateway_without_a_hosted_checkout_asks_nothing()
    {
        var response = await Cron(hostedCheckout: false).FunctionHandler(Request(), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("no_hosted_checkout");
        await _orders.DidNotReceiveWithAnyArgs().QueryByStatusForCronAsync(default!, default);
        await _gateway.DidNotReceiveWithAnyArgs().QueryOrderAsync(default!);
    }

    [Fact]
    public async Task A_live_gateway_runs_the_reconciliation_and_answers_its_counts()
    {
        var response = await Cron(hostedCheckout: true).FunctionHandler(Request(), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("\"seen\":\"1\"").And.Contain("\"queries\":\"0\"").And.Contain("\"truncated\":\"false\"");
        await _checkouts.Received(1).GetSlotAsync("o-1");
        ((TestLambdaLogger)_context.Logger).Buffer.ToString().Should().Contain(OrderPaymentReconciler.RunToken);
    }

    [Fact]
    public async Task An_unreadable_order_index_answers_500()
    {
        _orders.QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(Task.FromException<List<Dictionary<string, object>>>(new InvalidOperationException("x")));

        var response = await Cron(hostedCheckout: true).FunctionHandler(Request(), _context);

        response.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task An_unwired_reconciliation_answers_503()
    {
        var response = await Cron(hostedCheckout: true, wired: false).FunctionHandler(Request(), _context);

        response.StatusCode.Should().Be(503);
        ((TestLambdaLogger)_context.Logger).Buffer.ToString().Should().Contain("ORDER_PAYMENT_RECONCILE_UNCONFIGURED");
    }
}
