using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

public class TransactionAuditWriterTests
{
    private readonly TestLambdaContext _context = new();

    private static AuditContext Ctx() => new()
    {
        Actor = "user-1",
        ActorRole = "buyer",
        IpAddress = "203.0.113.7",
        CorrelationId = "corr-123",
    };

    [Fact]
    public async Task EmitAsync_WritesEntry_WithWhoWhatWhenWhere()
    {
        var repo = Substitute.For<ITransactionAuditRepository>();
        Dictionary<string, object>? captured = null;
        await repo.AppendAsync(Arg.Do<Dictionary<string, object>>(d => captured = d));

        await TransactionAuditWriter.EmitAsync(
            repo, _context, "order-1", "order", "order-1",
            "order_created", null, "created", Ctx(), metadata: "note");

        captured.Should().NotBeNull();
        captured!["transactionId"].Should().Be("order-1");
        captured["entityType"].Should().Be("order");
        captured["entityId"].Should().Be("order-1");
        captured["action"].Should().Be("order_created");
        captured["toState"].Should().Be("created");
        captured["actor"].Should().Be("user-1");        // who
        captured["actorRole"].Should().Be("buyer");
        captured["ipAddress"].Should().Be("203.0.113.7"); // where
        captured["correlationId"].Should().Be("corr-123");
        captured["metadata"].Should().Be("note");
        captured.Should().ContainKey("timestamp");        // when
        captured.Should().ContainKey("auditId");
        // fromState omitted when null (keeps items sparse).
        captured.Should().NotContainKey("fromState");
    }

    [Fact]
    public async Task EmitAsync_NullRepo_IsNoOp()
    {
        // Should not throw when audit is unconfigured.
        await TransactionAuditWriter.EmitAsync(
            null, _context, "order-1", "order", "order-1", "x", null, null, Ctx());
    }

    [Fact]
    public async Task EmitAsync_EmptyTransactionId_IsNoOp()
    {
        var repo = Substitute.For<ITransactionAuditRepository>();
        await TransactionAuditWriter.EmitAsync(
            repo, _context, "", "order", "order-1", "x", null, null, Ctx());
        await repo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task EmitAsync_RepoThrows_DoesNotPropagate()
    {
        // Audit is observability, never a hard dependency: a failing write must be
        // swallowed so the transaction it records still succeeds.
        var repo = Substitute.For<ITransactionAuditRepository>();
        repo.AppendAsync(Arg.Any<Dictionary<string, object>>())
            .Returns<Task>(_ => throw new InvalidOperationException("dynamo down"));

        var act = async () => await TransactionAuditWriter.EmitAsync(
            repo, _context, "order-1", "order", "order-1", "x", null, null, Ctx());

        await act.Should().NotThrowAsync();
    }

    // ===== ESC-47: one row per event at a deterministic key =====

    [Fact]
    public async Task EmitOnceAsync_WritesConditionallyAtTheGivenTimestamp_WithADeterministicId()
    {
        var repo = Substitute.For<ITransactionAuditRepository>();
        var captured = new List<Dictionary<string, object>>();
        // The second write finds the row already there.
        repo.AppendIfAbsentAsync(Arg.Do<Dictionary<string, object>>(captured.Add)).Returns(true, false);

        var results = new List<bool>();
        for (var i = 0; i < 2; i++)
        {
            results.Add(await TransactionAuditWriter.EmitOnceAsync(
                repo, _context, "order-1", "order", "order-1", "payment_verified", "payment_submitted", "funded",
                AuditContext.ForSystem("telebirr"), "2026-09-30T09:00:00.0000000Z", metadata: "m"));
        }

        results.Should().Equal(new[] { true, true }, "the row is there after both calls");
        captured.Should().HaveCount(2);
        captured[0].Should().BeEquivalentTo(captured[1], "two writers of the same event build the same row");
        var row = captured[0];
        row["transactionId"].Should().Be("order-1");
        row["timestamp"].Should().Be("2026-09-30T09:00:00.0000000Z");
        row["auditId"].Should().Be("txaudit-order-1-payment_verified-20260930T0900000000000Z");
        row["action"].Should().Be("payment_verified");
        row["fromState"].Should().Be("payment_submitted");
        row["toState"].Should().Be("funded");
        row["actor"].Should().Be("telebirr");
        row["metadata"].Should().Be("m");
        await repo.DidNotReceive().AppendAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task EmitOnceAsync_SameEventTimeOnTwoTransactions_GetsTwoIds()
    {
        var repo = Substitute.For<ITransactionAuditRepository>();
        var ids = new List<string>();
        repo.AppendIfAbsentAsync(Arg.Do<Dictionary<string, object>>(e => ids.Add((string)e["auditId"]))).Returns(true);

        foreach (var orderId in new[] { "order-1", "order-2" })
        {
            await TransactionAuditWriter.EmitOnceAsync(
                repo, _context, orderId, "order", orderId, "payment_verified", null, null, Ctx(),
                "2026-09-30T09:00:00.0000000Z");
        }

        ids.Should().OnlyHaveUniqueItems().And.HaveCount(2);
    }

    [Fact]
    public async Task EmitOnceAsync_WithoutRepoOrTimestamp_IsNoOp()
    {
        var repo = Substitute.For<ITransactionAuditRepository>();

        var noRepo = await TransactionAuditWriter.EmitOnceAsync(
            null, _context, "order-1", "order", "order-1", "x", null, null, Ctx(), "2026-09-30T09:00:00.0000000Z");
        var noTimestamp = await TransactionAuditWriter.EmitOnceAsync(
            repo, _context, "order-1", "order", "order-1", "x", null, null, Ctx(), "");

        noRepo.Should().BeFalse();
        noTimestamp.Should().BeFalse();
        await repo.DidNotReceive().AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>());
    }

    [Fact]
    public async Task EmitOnceAsync_RepoThrows_DoesNotPropagate()
    {
        var repo = Substitute.For<ITransactionAuditRepository>();
        repo.AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("dynamo down"));

        bool? written = null;
        var act = async () => written = await TransactionAuditWriter.EmitOnceAsync(
            repo, _context, "order-1", "order", "order-1", "x", null, null, Ctx(), "2026-09-30T09:00:00.0000000Z");

        await act.Should().NotThrowAsync();
        written.Should().BeFalse("the caller logs which order lost its row");
        ((TestLambdaLogger)_context.Logger).Buffer.ToString().Should().Contain("transaction-audit write failed (non-fatal)");
    }


    // ===== ACC-02: the checked write =====

    [Fact]
    public async Task EmitOnceCheckedAsync_ReportsWrittenOrExisting_AndThrowsOnAFailedWrite()
    {
        var repo = Substitute.For<ITransactionAuditRepository>();
        repo.AppendIfAbsentAsync(Arg.Any<Dictionary<string, object>>())
            .Returns(Task.FromResult(true), Task.FromResult(false),
                Task.FromException<bool>(new InvalidOperationException("throttled")));

        Task<bool> Write() => TransactionAuditWriter.EmitOnceCheckedAsync(
            repo, "account-user-1", "account", "user-1", "account_deleted", "cognito_pending", "completed",
            Ctx(), "2026-10-23T10:00:00.0000000Z");

        (await Write()).Should().BeTrue();
        (await Write()).Should().BeFalse("the row already exists");
        await FluentActions.Awaiting(Write).Should().ThrowAsync<InvalidOperationException>();
    }
}
