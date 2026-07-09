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
}
