using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// Golden vectors for <see cref="WoredaNames"/>. These mirror the Dart tests in
/// mobile/test/shared/services/place_normalizer_test.dart (normalizeWoreda +
/// compareWoredas) so server and on-device woreda matching stay in lockstep.
/// </summary>
public class WoredaNamesTests
{
    [Theory]
    [InlineData("Adama", "adama")]
    [InlineData("  ADAMA  ", "adama")]
    [InlineData("Ādama", "adama")]
    [InlineData("Adama, Oromia", "adama")]
    [InlineData("Adama,Oromia", "adama")]
    [InlineData("Addis Ababa (Finfinne)", "addis ababa")]
    [InlineData("Dirē Dawa [Dire Dawa]", "dire dawa")]
    [InlineData("Debre Mark’os", "debre markos")]
    [InlineData("Debre Mark'os", "debre markos")]
    [InlineData("Bahir-Dar", "bahir dar")]
    [InlineData("Addis   Ababa", "addis ababa")]
    [InlineData("አዳማ፣ ኦሮሚያ", "አዳማ")]
    [InlineData("Bishoftu ቦታ", "bishoftu ቦታ")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(", Oromia", "")]
    public void Normalize_GoldenVectors(string input, string expected)
    {
        Assert.Equal(expected, WoredaNames.Normalize(input));
    }

    [Fact]
    public void Normalize_NullIsEmpty()
    {
        Assert.Equal(string.Empty, WoredaNames.Normalize(null));
    }

    [Theory]
    [InlineData("Adama, Oromia", "Ādama", WoredaMatch.Exact)]
    [InlineData("adama", "ADAMA", WoredaMatch.Exact)]
    [InlineData("Addis Ababa", "addis-ababa, Addis Ababa", WoredaMatch.Exact)]
    [InlineData("Adama", "Adama Zone", WoredaMatch.Partial)]
    [InlineData("Wolaita Sodo", "Sodo", WoredaMatch.Partial)]
    [InlineData("Ambo", "Gambo", WoredaMatch.None)]
    [InlineData("Adama", "Mekelle", WoredaMatch.None)]
    [InlineData("Addis Ababa", "Addis Abeba", WoredaMatch.None)]
    [InlineData("", "Adama", WoredaMatch.None)]
    [InlineData("Adama", "", WoredaMatch.None)]
    [InlineData(null, "Adama", WoredaMatch.None)]
    public void Compare_GoldenVectors(string? a, string? b, WoredaMatch expected)
    {
        Assert.Equal(expected, WoredaNames.Compare(a, b));
        Assert.Equal(expected, WoredaNames.Compare(b, a));
    }
}
