using FluentAssertions;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// T-1: the typed handoff code. The vectors are shared with the mobile HandoffCode tests
/// (mobile/test/features/delivery_verification/data/services/handoff_code_test.dart), so both
/// sides derive the same nine digits from the same nonce.
/// </summary>
public sealed class HandoffCodeTests
{
    [Theory]
    [InlineData("farmer-nonce-1", "del-fup42", "757555978")]
    [InlineData("nonce-abc-123", "delivery-001-abcd-efgh", "340404994")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "bad5e4d5-37bc-4bd1-a216-1301cf9781f1", "649509129")]
    public void Derive_MatchesTheSharedVectors(string nonce, string deliveryId, string expected)
    {
        HandoffCode.Derive(nonce, deliveryId).Should().Be(expected);
    }

    [Fact]
    public void Derive_DependsOnTheNonceAndTheDelivery()
    {
        var code = HandoffCode.Derive("nonce-1", "del-1");

        code.Should().MatchRegex("^[0-9]{9}$");
        HandoffCode.IsWellFormed(code).Should().BeTrue();
        HandoffCode.Derive("nonce-2", "del-1").Should().NotBe(code);
        HandoffCode.Derive("nonce-1", "del-2").Should().NotBe(code);
    }

    [Fact]
    public void CheckDigit_IsTheDammDigit()
    {
        // The textbook example: 572 has the check digit 4.
        HandoffCode.CheckDigit("572").Should().Be(4);
        HandoffCode.CheckDigit("5724").Should().Be(0);
    }

    [Fact]
    public void IsWellFormed_CatchesEverySingleWrongDigit()
    {
        var code = HandoffCode.Derive("farmer-nonce-1", "del-fup42");
        for (var i = 0; i < code.Length; i++)
        {
            for (var d = '0'; d <= '9'; d++)
            {
                if (d == code[i])
                {
                    continue;
                }

                var typo = code[..i] + d + code[(i + 1)..];
                HandoffCode.IsWellFormed(typo).Should().BeFalse($"position {i} as {d}");
            }
        }
    }

    [Fact]
    public void IsWellFormed_CatchesEverySwapOfNeighbours()
    {
        var code = HandoffCode.Derive("nonce-abc-123", "delivery-001-abcd-efgh");
        for (var i = 0; i < code.Length - 1; i++)
        {
            if (code[i] == code[i + 1])
            {
                continue;
            }

            var swapped = code[..i] + code[i + 1] + code[i] + code[(i + 2)..];
            HandoffCode.IsWellFormed(swapped).Should().BeFalse($"swap at {i}");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("75755597")]
    [InlineData("7575559780")]
    [InlineData("75755597a")]
    [InlineData("757 55597")]
    [InlineData("٧٥٧٥٥٥٩٧٨")]
    public void IsWellFormed_RefusesAnythingButNineAsciiDigits(string? code)
    {
        HandoffCode.IsWellFormed(code).Should().BeFalse();
    }

    [Fact]
    public void Matches_OnlyTheNoncesOwnCode()
    {
        HandoffCode.Matches("757555978", "farmer-nonce-1", "del-fup42").Should().BeTrue();
        HandoffCode.Matches("757555978", "farmer-nonce-2", "del-fup42").Should().BeFalse();
        HandoffCode.Matches("340404994", "farmer-nonce-1", "del-fup42").Should().BeFalse();
    }

    [Fact]
    public void AttemptId_IsTheOperationAndAHashOfTheCode()
    {
        var a = HandoffCode.AttemptId("op-1", "757555978");

        a.Should().MatchRegex("^op-1:[0-9a-f]{16}$");
        a.Should().NotContain("757555978");
        // A retry with the same code is the same attempt; a new code is another one.
        HandoffCode.AttemptId("op-1", "757555978").Should().Be(a);
        HandoffCode.AttemptId("op-1", "340404994").Should().NotBe(a);
        HandoffCode.AttemptId("op-2", "757555978").Should().NotBe(a);
    }
}
