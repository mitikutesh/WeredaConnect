using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// FUP-147 (decided in ESC-20): <c>POST /escrow/release</c> compares its <c>deliveryId</c> with the delivery the
/// escrow is linked to (a mismatch is <c>422 ESCROW_VALIDATION</c>, field <c>deliveryId</c>, before anything is
/// read or written), and stores its <c>trigger</c> with the release (<c>releaseTrigger</c> on the escrow; a
/// ledger row also carries it as its <c>reason</c>). FUP-139: the <c>deliveryId</c> is optional, and an escrow
/// without a delivery (a self-pickup) is never compared.
/// </summary>
public class EscrowServiceReleaseRequestTests
{
    private const string EscrowId = "escrow-rel-1";
    private const string CreatedAt = "2026-11-01T10:00:00.0000000Z";

    private readonly IEscrowRepository _escrowRepo = Substitute.For<IEscrowRepository>();
    private readonly ITelebirrGateway _gateway = Substitute.For<ITelebirrGateway>();
    private readonly EscrowService _service;
    private bool _linked = true;

    public EscrowServiceReleaseRequestTests()
    {
        _escrowRepo.GetByIdAsync(EscrowId).Returns(_ => _linked ? Escrow() : Unlinked());
        _escrowRepo.GetByIdAsync(EscrowId, Arg.Any<bool>()).Returns(_ => Escrow("completed"));
        _escrowRepo.TryAcquireSettlementLockAsync(EscrowId, CreatedAt, "release", "escrowed").Returns(true);
        _gateway.ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>()).Returns(new ReleaseFundsResponse { Success = true, TelebirrRef = "TB-1" });
        _service = new EscrowService(_escrowRepo, Substitute.For<IEscrowAuditRepository>(),
            Substitute.For<IEscrowIdempotencyRepository>(), _gateway);
    }

    private static Dictionary<string, object> Escrow(string status = "escrowed") => new()
    {
        ["escrowId"] = EscrowId,
        ["createdAt"] = CreatedAt,
        ["status"] = status,
        ["deliveryId"] = "delivery-1",
        ["buyerId"] = "buyer-1",
        ["farmerId"] = "farmer-1",
        ["amount"] = 1050m,
        ["farmerPayout"] = 1000m,
        ["commission"] = 50m,
        ["telebirrHoldRef"] = "HOLD-1",
        ["version"] = 2
    };

    /// <summary>A self-pickup order escrow: no delivery was ever linked.</summary>
    private static Dictionary<string, object> Unlinked()
    {
        var escrow = Escrow();
        escrow.Remove("deliveryId");
        return escrow;
    }

    private static ReleaseEscrowRequest Release(string? deliveryId) => new()
    {
        EscrowId = EscrowId,
        Trigger = "admin_override",
        DeliveryId = deliveryId,
        IdempotencyKey = $"system:{EscrowId}:release:1"
    };

    [Fact]
    public async Task ADeliveryIdThatIsNotTheEscrows_IsRefused_BeforeAnyMoneyMoves()
    {
        var result = await _service.ReleaseAsync(Release("delivery-other"), "system");

        result.Error!.HttpStatus.Should().Be(422);
        result.Error.Code.Should().Be("ESCROW_VALIDATION");
        result.Error.Field.Should().Be("deliveryId");
        await _escrowRepo.DidNotReceiveWithAnyArgs().TryAcquireSettlementLockAsync(default!, default!, default!, default!);
        await _gateway.DidNotReceiveWithAnyArgs().ReleaseFundsAsync(default!);
    }

    [Theory]
    [InlineData("delivery-1")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task TheEscrowsOwnDeliveryOrNone_Releases_AndTheTriggerIsStored(string? deliveryId)
    {
        var result = await _service.ReleaseAsync(Release(deliveryId), "system");

        result.IsSuccess.Should().BeTrue();
        await _escrowRepo.Received(1).UpdateStatusAsync(EscrowId, CreatedAt, "released", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => (string)f["releaseTrigger"] == "admin_override"));
    }

    // FUP-139: a self-pickup escrow has no delivery, so the optional deliveryId is not compared: none, and
    // the order or escrow id the admin page sends in its place, both release.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("order-1")]
    public async Task AnEscrowWithoutADelivery_ReleasesWhateverTheRequestNames(string? deliveryId)
    {
        _linked = false;

        var result = await _service.ReleaseAsync(Release(deliveryId), "system");

        result.IsSuccess.Should().BeTrue();
        await _gateway.Received(1).ReleaseFundsAsync(Arg.Any<ReleaseFundsRequest>());
        await _escrowRepo.Received(1).UpdateStatusAsync(EscrowId, CreatedAt, "released", "escrowed",
            Arg.Is<Dictionary<string, object>>(f => (string)f["releaseTrigger"] == "admin_override"));
    }
}
