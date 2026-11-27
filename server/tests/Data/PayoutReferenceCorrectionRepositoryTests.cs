using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// FUP-218 (escrow-protocol.md 2.18.0): the correction of a recorded payout reference is one transaction whose
/// every item is conditioned on what was read. These tests capture the request: the leg's write, the escrow's
/// settlement field, the old reference item removed only while it names this leg, the new one put only if no leg
/// holds it, and the escrow-audit row. The same conditions against real DynamoDB are in
/// PayoutReferenceCorrectionDynamoDbTests.
/// </summary>
public class PayoutReferenceCorrectionRepositoryTests
{
    private const string Escrows = "escrows-test";
    private const string Ledger = "ledger-test";
    private const string References = "idempotency-test";
    private const string Audit = "audit-test";
    private const string EscrowId = "escrow-order-ord-1";
    private const string CreatedAt = "2026-10-01T10:00:00.0000000Z";
    private const string Now = "2026-11-12T10:00:00.0000000Z";

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly PayoutReferenceCorrectionRepository _repo;
    private TransactWriteItemsRequest? _transaction;

    public PayoutReferenceCorrectionRepositoryTests()
    {
        _dynamoDb.TransactWriteItemsAsync(Arg.Do<TransactWriteItemsRequest>(r => _transaction = r), Arg.Any<CancellationToken>())
            .Returns(new TransactWriteItemsResponse());
        _repo = new PayoutReferenceCorrectionRepository(_dynamoDb, Escrows, Ledger, References, Audit);
    }

    private static PayoutReferenceCorrection Legacy(
        int? version = 7, string? settlementField = "telebirrRefundRef", bool deleteOld = true) => new()
        {
            EscrowId = EscrowId,
            Ref = "buyer",
            OldRef = "TB-B-PAYMENT",
            NewRef = "tb-a-payment",
            PaidBy = "admin-1",
            Now = Now,
            Legacy = new LegacyLegRead(CreatedAt, "refunded", version),
            SettlementRefField = settlementField,
            DeleteOldItem = deleteOld,
            NewItem = new PayoutReferenceRecord(References, "tb-a-payment", EscrowId, "buyer", "admin-2", Now),
            Audit = new LedgerAudit(EscrowId, Now, "manual_payout_ref_corrected", "refunded", "refunded", "admin-2",
                "leg=buyer;oldRef=TB-B-PAYMENT;newRef=tb-a-payment;reason=Typed the id of the other payout.", "tb-a-payment")
        };

    private static TransactionCanceledException Canceled(params string[] codes) => new("canceled")
    {
        CancellationReasons = codes.Select(c => new CancellationReason { Code = c }).ToList()
    };

    [Fact]
    public async Task Legacy_WritesTheLegAndTheSettlementField_MovesTheItems_AndAuditsInOneTransaction()
    {
        var write = await _repo.CorrectAsync(Legacy());

        write.Should().Be(PayoutReferenceCorrectionWrite.Written);
        _transaction!.TransactItems.Should().HaveCount(4);

        var leg = _transaction.TransactItems[0].Update;
        leg.TableName.Should().Be(Escrows);
        leg.Key["escrowId"].S.Should().Be(EscrowId);
        leg.Key["createdAt"].S.Should().Be(CreatedAt);
        leg.UpdateExpression.Should().Be("SET #legRef = :new, #updated = :now, #settlementRef = :new ADD #version :one");
        leg.ConditionExpression.Should().Be(
            "#legStatus = :paid AND #legRef = :old AND #legPaidBy = :paidBy AND #status = :status"
            + " AND #version = :v AND #settlementRef = :old");
        leg.ExpressionAttributeNames["#legStatus"].Should().Be("manualBuyerStatus");
        leg.ExpressionAttributeNames["#legRef"].Should().Be("manualBuyerRef");
        leg.ExpressionAttributeNames["#legPaidBy"].Should().Be("manualBuyerPaidBy");
        leg.ExpressionAttributeNames["#settlementRef"].Should().Be("telebirrRefundRef");
        leg.ExpressionAttributeValues[":old"].S.Should().Be("TB-B-PAYMENT");
        leg.ExpressionAttributeValues[":new"].S.Should().Be("tb-a-payment");
        leg.ExpressionAttributeValues[":paidBy"].S.Should().Be("admin-1");
        leg.ExpressionAttributeValues[":status"].S.Should().Be("refunded");
        leg.ExpressionAttributeValues[":v"].N.Should().Be("7");
        // No amount, status, paid time or recorder is written.
        leg.ExpressionAttributeNames.Values.Should().NotContain(new[] { "amount", "farmerPayout", "manualBuyerPaidAt" });

        var delete = _transaction.TransactItems[1].Delete;
        delete.TableName.Should().Be(References);
        delete.Key["idempotencyKey"].S.Should().Be("payout-ref#TB-B-PAYMENT");
        delete.ConditionExpression.Should().Be("#escrow = :escrow AND #ref = :ref");
        delete.ExpressionAttributeNames["#escrow"].Should().Be("escrowId");
        delete.ExpressionAttributeNames["#ref"].Should().Be("ref");
        delete.ExpressionAttributeValues[":escrow"].S.Should().Be(EscrowId);
        delete.ExpressionAttributeValues[":ref"].S.Should().Be("buyer");

        var put = _transaction.TransactItems[2].Put;
        put.TableName.Should().Be(References);
        put.ConditionExpression.Should().Be("attribute_not_exists(#key)");
        put.Item["idempotencyKey"].S.Should().Be("payout-ref#TB-A-PAYMENT");
        put.Item["externalRef"].S.Should().Be("tb-a-payment");
        put.Item["recordedBy"].S.Should().Be("admin-2");
        put.Item["kind"].S.Should().Be("payout_ref");

        var audit = _transaction.TransactItems[3].Put;
        audit.TableName.Should().Be(Audit);
        audit.ConditionExpression.Should().Be("attribute_not_exists(#id)");
        audit.Item["action"].S.Should().Be("manual_payout_ref_corrected");
        audit.Item["actor"].S.Should().Be("admin-2");
        audit.Item["timestamp"].S.Should().Be(Now);
        audit.Item["telebirrRef"].S.Should().Be("tb-a-payment");
        audit.Item["metadata"].S.Should().Contain("oldRef=TB-B-PAYMENT;newRef=tb-a-payment;reason=");
    }

    [Fact]
    public async Task Legacy_WithoutAVersion_AnOpenQueue_AndAnItemOfAnotherLeg_WritesOnlyTheLegTheNewItemAndTheAudit()
    {
        await _repo.CorrectAsync(Legacy(version: null, settlementField: null, deleteOld: false));

        _transaction!.TransactItems.Should().HaveCount(3);
        var leg = _transaction.TransactItems[0].Update;
        leg.ConditionExpression.Should().Be(
            "#legStatus = :paid AND #legRef = :old AND #legPaidBy = :paidBy AND #status = :status AND attribute_not_exists(#version)");
        leg.UpdateExpression.Should().Be("SET #legRef = :new, #updated = :now ADD #version :one");
        _transaction.TransactItems.Should().NotContain(i => i.Delete != null);
        _transaction.TransactItems[1].Put.TableName.Should().Be(References);
        _transaction.TransactItems[2].Put.TableName.Should().Be(Audit);
    }

    [Fact]
    public async Task Ledger_WritesTheRowOnItsVersion_AndTheEscrowFieldOnTheEscrowVersion()
    {
        var correction = Legacy() with
        {
            Ref = "release#farmer",
            Legacy = null,
            Ledger = new LedgerRowRead(4, CreatedAt, 9),
            SettlementRefField = "telebirrReleaseRef",
            NewItem = new PayoutReferenceRecord(References, "tb-a-payment", EscrowId, "release#farmer", "admin-2", Now)
        };

        await _repo.CorrectAsync(correction);

        _transaction!.TransactItems.Should().HaveCount(5);
        var row = _transaction.TransactItems[0].Update;
        row.TableName.Should().Be(Ledger);
        row.Key["escrowId"].S.Should().Be(EscrowId);
        row.Key["ref"].S.Should().Be("release#farmer");
        row.UpdateExpression.Should().Be("SET #tref = :new ADD #version :one");
        row.ConditionExpression.Should().Be("#version = :v AND #status = :done AND #tref = :old AND #paidBy = :paidBy");
        row.ExpressionAttributeNames["#tref"].Should().Be("telebirrRef");
        row.ExpressionAttributeValues[":v"].N.Should().Be("4");
        row.ExpressionAttributeValues[":done"].S.Should().Be("done");

        var escrow = _transaction.TransactItems[1].Update;
        escrow.TableName.Should().Be(Escrows);
        escrow.UpdateExpression.Should().Be("SET #settlementRef = :new, #updated = :now ADD #version :one");
        escrow.ConditionExpression.Should().Be("#version = :v AND #settlementRef = :old");
        escrow.ExpressionAttributeNames["#settlementRef"].Should().Be("telebirrReleaseRef");
        escrow.ExpressionAttributeValues[":v"].N.Should().Be("9");

        _transaction.TransactItems[2].Delete.ExpressionAttributeValues[":ref"].S.Should().Be("release#farmer");
        _transaction.TransactItems[3].Put.Item["ref"].S.Should().Be("release#farmer");
        _transaction.TransactItems[4].Put.TableName.Should().Be(Audit);
    }

    [Fact]
    public async Task Ledger_WhoseEscrowFieldHoldsAnotherReference_LeavesTheEscrowAlone()
    {
        await _repo.CorrectAsync(Legacy() with
        {
            Ref = "release#trucker#t1",
            Legacy = null,
            Ledger = new LedgerRowRead(2),
            SettlementRefField = null,
            DeleteOldItem = false
        });

        _transaction!.TransactItems.Should().HaveCount(3);
        _transaction.TransactItems.Where(i => i.Update != null).Should().ContainSingle()
            .Which.Update.TableName.Should().Be(Ledger);
    }

    [Theory]
    [InlineData("ReferenceTaken", "None", "None", "ConditionalCheckFailed", "None")]
    [InlineData("Changed", "ConditionalCheckFailed", "None", "None", "None")]
    [InlineData("Changed", "None", "ConditionalCheckFailed", "None", "None")]
    [InlineData("Changed", "ConditionalCheckFailed", "None", "ConditionalCheckFailed", "None")]
    [InlineData("Changed", "None", "None", "None", "ConditionalCheckFailed")]
    [InlineData("Changed", "TransactionConflict", "None", "None", "None")]
    public async Task ACanceledTransaction_IsTakenOnlyWhenTheNewItemAloneFailed(
        string expected, string leg, string oldItem, string newItem, string audit)
    {
        _dynamoDb.TransactWriteItemsAsync(Arg.Any<TransactWriteItemsRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(Canceled(leg, oldItem, newItem, audit));

        var write = await _repo.CorrectAsync(Legacy(settlementField: null));

        write.Should().Be(Enum.Parse<PayoutReferenceCorrectionWrite>(expected));
    }
}
