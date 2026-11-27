using System.Globalization;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FRD-15: the <c>relationship_patterns</c> action of the order cron (the hourly EventBridge rule's body). It runs
/// the relationship scan when the Lambda wired one, answers 200 with the run summary, refuses to pretend when it
/// is unconfigured, and answers 500 when a status Query fails.
/// </summary>
public class OrderCronRelationshipTests
{
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IFraudDetectionService _fraud = Substitute.For<IFraudDetectionService>();
    private readonly TestLambdaContext _context = new();

    public OrderCronRelationshipTests()
    {
        _orders.QueryByStatusCreatedSinceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));
        _orders.QueryOrdersAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));
    }

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    private static APIGatewayProxyRequest Request() => new()
    {
        HttpMethod = "POST",
        Body = "{\"action\":\"relationship_patterns\"}"
    };

    private OrderCronFunction Wired() => new(_orders)
    {
        RelationshipPatterns = new RelationshipPatternService(_orders, _users, _fraud, RelationshipPatternSettings.Default)
    };

    [Fact]
    public async Task Unconfigured_Answers200_LogsTheToken_AndReadsNothing()
    {
        var response = await new OrderCronFunction(_orders).FunctionHandler(Request(), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("\"outcome\":\"unconfigured\"");
        Logs.Should().Contain("FRAUD_RELATIONSHIP_UNCONFIGURED");
        await _orders.DidNotReceiveWithAnyArgs().QueryByStatusCreatedSinceAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Wired_RunsTheScan_AndAnswersTheSummary()
    {
        var order = new Dictionary<string, object>
        {
            ["orderId"] = "ord-1",
            ["buyerId"] = "b-1",
            ["farmerId"] = "f-1",
            ["status"] = "delivered",
            ["createdAt"] = DateTime.UtcNow.AddHours(-2).ToString("o", CultureInfo.InvariantCulture),
            ["paymentVerifiedAt"] = DateTime.UtcNow.AddHours(-2).ToString("o", CultureInfo.InvariantCulture)
        };
        _orders.QueryByStatusCreatedSinceAsync("delivered", Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>> { order }, (Dictionary<string, AttributeValue>?)null));
        foreach (var (id, phone) in new[] { ("b-1", "+251911000001"), ("f-1", "+251911000002") })
        {
            _users.GetUserByIdAsync(id).Returns(new UserDto
            {
                UserId = id,
                PhoneNumber = phone,
                Role = "buyer",
                Woreda = "Ada'a",
                IsPhoneVerified = true,
                KnownDevices = ["install-1"],
                CreatedAt = "2026-01-01T00:00:00Z",
                UpdatedAt = "2026-01-01T00:00:00Z",
                Version = 1
            });
        }

        var response = await Wired().FunctionHandler(Request(), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("\"outcome\":\"ok\"").And.Contain("\"examined\":\"1\"").And.Contain("\"written\":\"2\"");
        await _fraud.Received(1).RecordFraudSignalAsync("b-1", "SHARED_DEVICE_TRADE", "ord-1", "order", Arg.Any<string?>());
        await _fraud.Received(1).RecordFraudSignalAsync("f-1", "SHARED_DEVICE_TRADE", "ord-1", "order", Arg.Any<string?>());
    }

    [Fact]
    public async Task AFailedStatusQuery_Answers500()
    {
        _orders.QueryByStatusCreatedSinceAsync("funded", Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var response = await Wired().FunctionHandler(Request(), _context);

        response.StatusCode.Should().Be(500);
        response.Body.Should().Contain("ORDER_CRON_ERROR");
        await _fraud.DidNotReceiveWithAnyArgs().RecordFraudSignalAsync(default!, default!, default, default!, default);
    }

    [Fact]
    public async Task TheOtherActions_DoNotRunTheScan()
    {
        _orders.QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(new List<Dictionary<string, object>>());

        var response = await Wired().FunctionHandler(new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Body = "{\"action\":\"auto_cancel_expired\"}"
        }, _context);

        response.StatusCode.Should().Be(200);
        await _orders.DidNotReceiveWithAnyArgs().QueryByStatusCreatedSinceAsync(default!, default!, default, default);
    }
}

/// <summary>FRD-15: the runtime constructor wires the relationship scan when its tables are set (and only then).</summary>
[Collection(LambdaConstructorEnvironmentCollection.Name)]
public sealed class OrderCronRelationshipWiringTests : IDisposable
{
    private static readonly Dictionary<string, string?> Environment_ = new()
    {
        { "AWS_REGION", "eu-north-1" },
        { "AWS_ACCESS_KEY_ID", "test-access-key" },
        { "AWS_SECRET_ACCESS_KEY", "test-secret-key" },
        { "ORDERS_TABLE", "orders-test" },
        { "USERS_TABLE", "users-test" },
        { "FRAUD_SIGNALS_TABLE", "fraud-signals-test" }
    };

    private readonly Dictionary<string, string?> _saved = new();

    public OrderCronRelationshipWiringTests()
    {
        foreach (var (name, value) in Environment_)
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public void WithTheTables_TheScanIsWired()
    {
        new OrderCronFunction().RelationshipPatterns.Should().NotBeNull();
    }

    [Fact]
    public void WithoutTheSignalsTable_TheScanIsNotWired()
    {
        Environment.SetEnvironmentVariable("FRAUD_SIGNALS_TABLE", null);

        new OrderCronFunction().RelationshipPatterns.Should().BeNull();
    }
}
