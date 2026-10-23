using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>FRD-03 (fraud-detection-architecture.md 17.2, 18.1): the suspension gate the settlement sweeps ask.</summary>
public class PartySuspensionGateTests
{
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();

    private static UserDto User(string id, bool suspended) => new()
    {
        UserId = id,
        PhoneNumber = "+251900000001",
        Role = "buyer",
        Woreda = "w",
        IsPhoneVerified = true,
        Suspended = suspended,
        CreatedAt = "2026-01-01T00:00:00Z",
        UpdatedAt = "2026-01-01T00:00:00Z",
        Version = 1
    };

    private static Dictionary<string, object> Escrow(bool withTrucker = true)
    {
        var escrow = new Dictionary<string, object> { { "escrowId", "e-1" }, { "buyerId", "b" }, { "farmerId", "f" } };
        if (withTrucker)
        {
            escrow["truckerId"] = "t";
        }

        return escrow;
    }

    private void Users(bool buyer = false, bool farmer = false, bool trucker = false)
    {
        _users.GetUserByIdAsync("b").Returns(User("b", buyer));
        _users.GetUserByIdAsync("f").Returns(User("f", farmer));
        _users.GetUserByIdAsync("t").Returns(User("t", trucker));
    }

    [Fact]
    public async Task NoPartySuspended_Settles()
    {
        Users();

        var decision = await new PartySuspensionGate(_users).CheckAsync(Escrow());

        decision.Settles.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, false, false, "buyer")]
    [InlineData(false, true, false, "farmer")]
    [InlineData(false, false, true, "trucker")]
    public async Task AnySuspendedParty_HoldsForAccountSuspended(bool buyer, bool farmer, bool trucker, string party)
    {
        Users(buyer, farmer, trucker);

        var decision = await new PartySuspensionGate(_users).CheckAsync(Escrow());

        decision.Settles.Should().BeFalse();
        decision.HoldReason.Should().Be("account_suspended");
        decision.SkipReason.Should().BeNull();
        decision.Party.Should().Be(party);
    }

    [Fact]
    public async Task AnEscrowWithoutATrucker_ReadsOnlyTheBuyerAndTheFarmer()
    {
        Users();

        await new PartySuspensionGate(_users).CheckAsync(Escrow(withTrucker: false));

        await _users.DidNotReceive().GetUserByIdAsync("t");
    }

    [Fact]
    public async Task APartyWithoutAUserRow_IsNotSuspended()
    {
        Users();
        _users.GetUserByIdAsync("f").Returns((UserDto?)null);

        (await new PartySuspensionGate(_users).CheckAsync(Escrow())).Settles.Should().BeTrue();
    }

    [Fact]
    public async Task ARowThatCannotBeRead_SkipsForTheRun_EvenWithAnotherPartySuspended()
    {
        Users(buyer: true);
        _users.GetUserByIdAsync("f").ThrowsAsync(new InvalidOperationException("ddb"));

        var decision = await new PartySuspensionGate(_users).CheckAsync(Escrow());

        decision.SkipReason.Should().Be("user_read_failed");
        decision.HoldReason.Should().BeNull();
    }

    [Fact]
    public async Task WithoutAUsersTable_EveryCheckFailsClosed()
    {
        var decision = await new PartySuspensionGate(null).CheckAsync(Escrow());

        decision.SkipReason.Should().Be("user_read_failed");
    }

    [Fact]
    public async Task ReadsAreMemoizedPerRun_AndAFailedReadIsNotRemembered()
    {
        Users();
        _users.GetUserByIdAsync("t").ThrowsAsync(new InvalidOperationException("ddb"));
        var gate = new PartySuspensionGate(_users);
        gate.BeginRun();

        await gate.CheckAsync(Escrow());
        await gate.CheckAsync(Escrow());

        await _users.Received(1).GetUserByIdAsync("b");
        await _users.Received(2).GetUserByIdAsync("t");

        gate.BeginRun();
        await gate.CheckAsync(Escrow());
        await _users.Received(2).GetUserByIdAsync("b");
    }
}
