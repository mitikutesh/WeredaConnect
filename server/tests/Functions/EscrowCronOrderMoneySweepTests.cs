using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NSubstitute.Extensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-09: the hourly order-money sweep in escrow-cron (self-heal and backfill of the order mirror):
/// partition order, paging, the run budget and its truncation line, a failed page query, the
/// crash recovery's follow, and the run summary.
/// </summary>
public class EscrowCronOrderMoneySweepTests
{
    private readonly IEscrowRepository _escrows = Substitute.For<IEscrowRepository>();
    private readonly IEscrowAuditRepository _audit = Substitute.For<IEscrowAuditRepository>();
    private readonly IEscrowIdempotencyRepository _idempotency = Substitute.For<IEscrowIdempotencyRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly TestLambdaContext _context = new();
    private readonly Dictionary<string, string> _orderStatus = new();
    private readonly List<string> _queried = new();
    private TimeSpan _elapsed = TimeSpan.Zero;

    public EscrowCronOrderMoneySweepTests()
    {
        _escrows.QueryByStatusForCronAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>>());
        _escrows.QueryOpenManualPayoutsAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));
        _orders.QueryAdminQueueAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<Dictionary<string, object>>(), (Dictionary<string, AttributeValue>?)null));
        _orders.GetByIdAsync(Arg.Any<string>(), true).Returns(call => Order(call.ArgAt<string>(0)));
        _orders.TryAdvanceStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(call =>
        {
            var id = call.ArgAt<string>(0);
            if (!_orderStatus.TryGetValue(id, out var status) || status != call.ArgAt<string>(1))
            {
                return Task.FromResult<Dictionary<string, object>?>(null);
            }

            _orderStatus[id] = call.ArgAt<string>(2);
            return Task.FromResult(Order(id));
        });
    }

    private Dictionary<string, object>? Order(string orderId)
        => _orderStatus.TryGetValue(orderId, out var status)
            ? new Dictionary<string, object> { { "orderId", orderId }, { "status", status }, { "version", 3 } }
            : null;

    private void SeedOrder(string orderId, string status, string escrowStatus)
    {
        _orderStatus[orderId] = status;
        _escrows.GetByIdAsync($"escrow-order-{orderId}", true).Returns(new Dictionary<string, object>
        {
            { "escrowId", $"escrow-order-{orderId}" }, { "status", escrowStatus }, { "orderId", orderId },
            { "createdAt", "2026-10-01T10:00:00.0000000Z" }, { "deliveryConfirmedAt", "2026-10-02T10:00:00.0000000Z" }
        });
    }

    /// <summary>Each status partition answers its orders in pages of <paramref name="pageSize"/>.</summary>
    private void PartitionsHold(int pageSize = 100)
    {
        _orders.QueryMoneySweepPageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns(call =>
            {
                var status = call.ArgAt<string>(0);
                var start = call.ArgAt<Dictionary<string, AttributeValue>?>(2);
                _queried.Add(status);
                // Rows ordered by id; a start key is the last row read, as DynamoDB's index keys are.
                var rows = _orderStatus.Where(o => o.Value == status).OrderBy(o => o.Key, StringComparer.Ordinal)
                    .Where(o => start == null || string.CompareOrdinal(o.Key, start["orderId"].S) > 0)
                    .Select(o => new Dictionary<string, object>
                    {
                        { "orderId", o.Key }, { "status", o.Value }, { "createdAt", "2026-10-01T09:00:00.0000000Z" }
                    }).ToList();
                var page = rows.Take(pageSize).ToList();
                Dictionary<string, AttributeValue>? next = rows.Count > pageSize
                    ? new Dictionary<string, AttributeValue>
                    {
                        ["orderId"] = new AttributeValue { S = page[^1]["orderId"].ToString() },
                        ["createdAt"] = new AttributeValue { S = "2026-10-01T09:00:00.0000000Z" },
                        ["status"] = new AttributeValue { S = status }
                    }
                    : null;
                return Task.FromResult((page, next));
            });
    }

    private EscrowCronFunction MakeFunction(int maxRows = 2000, TimeSpan? slice = null, IEscrowIdempotencyRepository? cursorStore = null)
        => new(new EscrowService(_escrows, _audit, _idempotency, _gateway, orderRepo: _orders), _escrows, _audit, _gateway,
            orderRepo: _orders, idempotencyRepo: cursorStore)
        {
            BudgetFactory = () => new CronRunBudget(() => _elapsed),
            OrderMoneySweepMaxRows = maxRows,
            OrderMoneySweepSlice = slice ?? TimeSpan.FromSeconds(30)
        };

    private async Task<Dictionary<string, string>> RunAsync(EscrowCronFunction function)
    {
        var response = await function.FunctionHandler(new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new Dictionary<string, string> { { "action", "auto_release_and_refund" } })
        }, _context);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(response.Body)!;
    }

    private string Logs => ((TestLambdaLogger)_context.Logger).Buffer.ToString();

    [Fact]
    public async Task TheSweep_ReadsThePartitionsWhereMoneyMovesFirst_ThenTheLogisticsOnes()
    {
        PartitionsHold();

        await RunAsync(MakeFunction());

        _queried.Should().Equal("delivered", "disbursing", "disputed", "accepted", "pickup_confirmed", "in_transit");
    }

    [Fact]
    public async Task TheSweep_SettlesEveryOrderBehindItsMoney_AcrossPages_AndCountsIt()
    {
        for (var i = 0; i < 150; i++)
        {
            SeedOrder($"wait-{i:000}", "delivered", "escrowed");
        }

        SeedOrder("z-paid", "delivered", "completed");
        SeedOrder("z-owed", "delivered", "released");
        SeedOrder("z-lagging", "in_transit", "completed");
        PartitionsHold();

        var summary = await RunAsync(MakeFunction());

        _orderStatus["z-paid"].Should().Be("settled");
        _orderStatus["z-owed"].Should().Be("disbursing");
        _orderStatus["z-lagging"].Should().Be("settled");
        _orderStatus.Where(o => o.Key.StartsWith("wait-")).Should().OnlyContain(o => o.Value == "delivered");
        summary["ordersSettled"].Should().Be("2");
        summary["ordersMoved"].Should().Be("1");
    }

    [Fact]
    public async Task WithoutACursorStore_EveryEarlyStop_LogsTheOrderMoneyTruncationLine_NotTheMoneyAlarm()
    {
        for (var i = 0; i < 5; i++)
        {
            SeedOrder($"o-{i}", "delivered", "completed");
        }

        PartitionsHold();

        await RunAsync(MakeFunction(maxRows: 3));

        _orderStatus.Values.Count(v => v == "settled").Should().Be(3);
        Logs.Should().Contain("ORDER_MONEY_MISMATCH orderId=- escrowId=- orderStatus=- escrowStatus=- reason=sweep_truncated");
        Logs.Should().NotContain("ESCROW_SETTLEMENT_FAILED");
    }

    [Fact]
    public async Task TheSweep_StopsWhenItsSliceOfTheRunIsUsedUp()
    {
        SeedOrder("o-1", "delivered", "completed");
        PartitionsHold();
        _elapsed = TimeSpan.FromMinutes(1);

        await RunAsync(MakeFunction(slice: TimeSpan.Zero));

        _orderStatus["o-1"].Should().Be("delivered");
        Logs.Should().Contain("reason=sweep_truncated");
    }

    [Fact]
    public async Task AFailedPageQuery_IsLogged_AndTheOtherPartitionsStillRun()
    {
        SeedOrder("o-1", "disbursing", "completed");
        PartitionsHold();
        _orders.Configure().QueryMoneySweepPageAsync("delivered", Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .ThrowsAsync(new InvalidOperationException("throttled"));

        await RunAsync(MakeFunction());

        Logs.Should().Contain("ORDER_MONEY_MISMATCH orderId=- escrowId=- orderStatus=delivered escrowStatus=- reason=sweep_failed");
        _orderStatus["o-1"].Should().Be("settled");
    }

    [Fact]
    public async Task ARepositoryDefaultPage_IsAnEmptyPage()
    {
        // NSubstitute answers an unconfigured tuple query with (null, null).
        var summary = await RunAsync(MakeFunction());

        summary["ordersSettled"].Should().Be("0");
    }

    [Fact]
    public async Task CrashRecovery_CompletesTheEscrow_AndSettlesItsOrder()
    {
        SeedOrder("ord-crash", "delivered", "released");
        var released = new Dictionary<string, object>
        {
            { "escrowId", "escrow-order-ord-crash" }, { "status", "released" }, { "createdAt", "2026-10-01T10:00:00.0000000Z" },
            { "orderId", "ord-crash" }
        };
        _escrows.Configure().QueryByStatusForCronAsync("released", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(new List<Dictionary<string, object>> { released });
        _escrows.UpdateStatusAsync("escrow-order-ord-crash", Arg.Any<string>(), "completed", "released", Arg.Any<Dictionary<string, object>?>())
            .Returns(_ =>
            {
                _escrows.Configure().GetByIdAsync("escrow-order-ord-crash", true).Returns(new Dictionary<string, object>(released)
                {
                    ["status"] = "completed"
                });
                return Task.CompletedTask;
            });

        var summary = await RunAsync(MakeFunction());

        summary["completed"].Should().Be("1");
        _orderStatus["ord-crash"].Should().Be("settled");
    }

    // ──────────────────────────────────────────────
    // The cursor (review item 8: no starvation, no hourly alarm)
    // ──────────────────────────────────────────────

    /// <summary>An in-memory escrow idempotency table (the cursor's home).</summary>
    private sealed class MemoryIdempotency : IEscrowIdempotencyRepository
    {
        public Dictionary<string, Dictionary<string, object>> Rows { get; } = new();

        public Task<Dictionary<string, object>?> GetAsync(string key)
            => Task.FromResult(Rows.TryGetValue(key, out var row) ? new Dictionary<string, object>(row) : null);

        public Task<bool> StoreAsync(string key, string escrowId, string status, string? result = null)
        {
            if (Rows.ContainsKey(key))
            {
                return Task.FromResult(false);
            }

            Rows[key] = new Dictionary<string, object>
            {
                { "idempotencyKey", key }, { "escrowId", escrowId }, { "status", status },
                { "createdAt", DateTime.UtcNow.ToString("o") }, { "expiresAt", "ttl" }, { "result", result ?? "" }
            };
            return Task.FromResult(true);
        }

        /// <summary>Runs before a conditional update (a concurrent run that ends the cycle, say).</summary>
        public Action? BeforeConditionalUpdate { get; set; }

        public Task UpdateAsync(string key, string status, string? result = null)
        {
            // Unconditional, like DynamoDB's UpdateItem: a missing row comes back bare.
            if (!Rows.TryGetValue(key, out var row))
            {
                row = new Dictionary<string, object> { { "idempotencyKey", key } };
                Rows[key] = row;
            }

            row["status"] = status;
            if (result != null)
            {
                row["result"] = result;
            }

            return Task.CompletedTask;
        }

        public Task<bool> TryUpdateExistingAsync(string key, string status, string result)
        {
            BeforeConditionalUpdate?.Invoke();
            if (!Rows.TryGetValue(key, out var row))
            {
                return Task.FromResult(false);
            }

            row["status"] = status;
            row["result"] = result;
            return Task.FromResult(true);
        }

        public Task DeleteAsync(string key)
        {
            Rows.Remove(key);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ACycleThatTakesSeveralRuns_GoesOnWhereItStopped_AndEndsByDeletingTheCursor()
    {
        for (var i = 1; i <= 7; i++)
        {
            SeedOrder($"o-{i}", "delivered", "completed");
        }

        PartitionsHold();
        var store = new MemoryIdempotency();

        await RunAsync(MakeFunction(maxRows: 3, cursorStore: store));
        _orderStatus.Values.Count(v => v == "settled").Should().Be(3);
        store.Rows.Should().ContainKey(EscrowCronFunction.OrderMoneyCursorKey);
        store.Rows[EscrowCronFunction.OrderMoneyCursorKey]["result"].ToString().Should().Contain("\"orderId\":\"o-3\"");

        await RunAsync(MakeFunction(maxRows: 3, cursorStore: store));
        _orderStatus.Values.Count(v => v == "settled").Should().Be(6);

        await RunAsync(MakeFunction(maxRows: 3, cursorStore: store));
        _orderStatus.Values.Should().OnlyContain(v => v == "settled");
        store.Rows.Should().NotContainKey(EscrowCronFunction.OrderMoneyCursorKey);

        // A young cycle pauses quietly: no alarm line on any of the three runs.
        Logs.Should().Contain("ORDER_MONEY_SWEEP_PAUSED");
        Logs.Should().NotContain("reason=sweep_truncated");
    }

    [Fact]
    public async Task ACursorPastAPartition_StartsTheNextRunAtThatPartition()
    {
        SeedOrder("a-1", "delivered", "completed");
        SeedOrder("t-1", "in_transit", "completed");
        PartitionsHold();
        var store = new MemoryIdempotency();
        await store.StoreAsync(EscrowCronFunction.OrderMoneyCursorKey, "-", "open",
            "{\"status\":\"in_transit\"}");

        await RunAsync(MakeFunction(cursorStore: store));

        _queried.Should().Equal("in_transit");
        _orderStatus["t-1"].Should().Be("settled");
        _orderStatus["a-1"].Should().Be("delivered");
        store.Rows.Should().NotContainKey(EscrowCronFunction.OrderMoneyCursorKey);
    }

    [Fact]
    public async Task ACycleStillUnfinishedAfter24Hours_Alarms()
    {
        for (var i = 1; i <= 3; i++)
        {
            SeedOrder($"o-{i}", "delivered", "completed");
        }

        PartitionsHold();
        var store = new MemoryIdempotency();
        await store.StoreAsync(EscrowCronFunction.OrderMoneyCursorKey, "-", "open", "{\"status\":\"delivered\"}");
        store.Rows[EscrowCronFunction.OrderMoneyCursorKey]["createdAt"] = DateTime.UtcNow.AddHours(-25).ToString("o");

        await RunAsync(MakeFunction(maxRows: 1, cursorStore: store));

        Logs.Should().Contain("ORDER_MONEY_MISMATCH orderId=- escrowId=- orderStatus=- escrowStatus=- reason=sweep_truncated");
    }

    [Fact]
    public async Task ACursorDeletedByAnotherRunMeanwhile_ComesBackAsANewCycle_WithItsStartAndTtl()
    {
        for (var i = 1; i <= 3; i++)
        {
            SeedOrder($"o-{i}", "delivered", "completed");
        }

        PartitionsHold();
        var store = new MemoryIdempotency();
        await store.StoreAsync(EscrowCronFunction.OrderMoneyCursorKey, "-", "open", "{\"status\":\"delivered\"}");
        store.Rows[EscrowCronFunction.OrderMoneyCursorKey]["createdAt"] = DateTime.UtcNow.AddHours(-30).ToString("o");
        // Another run finishes the cycle and deletes the cursor while this one is still going.
        store.BeforeConditionalUpdate = () => store.Rows.Remove(EscrowCronFunction.OrderMoneyCursorKey);

        await RunAsync(MakeFunction(maxRows: 1, cursorStore: store));

        var row = store.Rows[EscrowCronFunction.OrderMoneyCursorKey];
        row.Should().ContainKey("expiresAt");
        DateTime.Parse(row["createdAt"].ToString()!).Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
        row["result"].ToString().Should().Contain("\"orderId\":\"o-1\"");
    }
}
