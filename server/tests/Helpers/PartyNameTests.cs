using FluentAssertions;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>FUP-130: an id is never copied onto a row as a party's name.</summary>
public class PartyNameTests
{
    private const string FarmerId = "3f2c1a9e-7b44-4c1d-9a55-0d6e8f1b2c3d";

    [Theory]
    [InlineData("Abebe Kebede", "Abebe Kebede")]
    [InlineData("  Abebe Kebede  ", "Abebe Kebede")]
    [InlineData("አበበ ከበደ", "አበበ ከበደ")]
    public void RealName_IsKeptTrimmed(string name, string expected)
    {
        PartyName.OrEmpty(name, FarmerId).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingName_IsEmpty(string? name)
    {
        PartyName.OrEmpty(name, FarmerId).Should().BeEmpty();
    }

    [Theory]
    [InlineData(FarmerId)]
    [InlineData(" 3f2c1a9e-7b44-4c1d-9a55-0d6e8f1b2c3d ")]
    // Another user's id, upper case, and without the hyphens: still an id.
    [InlineData("9A8B7C6D-1234-4ABC-8DEF-001122334455")]
    [InlineData("3f2c1a9e7b444c1d9a550d6e8f1b2c3d")]
    public void AnId_IsEmpty(string name)
    {
        PartyName.OrEmpty(name, FarmerId).Should().BeEmpty();
    }

    [Fact]
    public void ThePartysOwnId_IsEmpty_WhateverItsShape()
    {
        PartyName.OrEmpty("farmer-001", "farmer-001").Should().BeEmpty();
        PartyName.OrEmpty("farmer-001", null).Should().Be("farmer-001");
    }
}
