using System.Text.Json;
using FluentAssertions;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>FUP-67: parsing of the <c>listing_report</c> payload (listing-report-api.md 1.0.0 section 1).</summary>
public class ListingReportRulesTests
{
    [Fact]
    public void Parse_AValidCropReport_ReadsItsFields()
    {
        var errors = ListingReportRules.Parse("crop#c-1", Data("crop", "c-1", "contact_info"), out var input);

        errors.Should().BeEmpty();
        input.Should().Be(new ListingReportInput("crop", "c-1", "contact_info"));
    }

    [Fact]
    public void Parse_JsonElementValues_AreRead_AndAVersionIsIgnored()
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, object>>(
            "{\"listingType\":\"trip\",\"listingId\":\"t-1\",\"reason\":\"scam\",\"version\":1}")!;

        var errors = ListingReportRules.Parse("trip#t-1", data, out var input);

        errors.Should().BeEmpty();
        input!.ListingType.Should().Be("trip");
    }

    [Theory]
    [InlineData("listing")]
    [InlineData("order")]
    [InlineData("")]
    public void Parse_AnUnknownListingType_IsAnError(string listingType)
    {
        var errors = ListingReportRules.Parse($"{listingType}#c-1", Data(listingType, "c-1", "scam"), out var input);

        errors.Should().Contain("listingType must be crop or trip.");
        input.Should().BeNull();
    }

    [Fact]
    public void Parse_AListingIdWithAHash_OrTooLong_IsAnError()
    {
        ListingReportRules.Parse("crop#a#b", Data("crop", "a#b", "scam"), out _)
            .Should().ContainSingle().Which.Should().StartWith("listingId must be at most");
        var longId = new string('a', 129);
        ListingReportRules.Parse($"crop#{longId}", Data("crop", longId, "scam"), out _)
            .Should().ContainSingle().Which.Should().StartWith("listingId must be at most");
    }

    [Fact]
    public void Parse_AMissingListingId_IsAnError()
    {
        var data = Data("crop", "c-1", "scam");
        data.Remove("listingId");

        ListingReportRules.Parse("crop#c-1", data, out _).Should().Contain("listingId is required.");
    }

    [Fact]
    public void Parse_ANonStringReason_IsAnError()
    {
        var data = Data("crop", "c-1", "scam");
        data["reason"] = 3;

        ListingReportRules.Parse("crop#c-1", data, out _).Should().ContainSingle().Which.Should().StartWith("reason must be one of");
    }

    [Fact]
    public void Parse_UnknownFields_AreListedInOrder()
    {
        var data = Data("crop", "c-1", "scam");
        data["note"] = "x";
        data["farmerId"] = "f-1";

        ListingReportRules.Parse("crop#c-1", data, out _).Should().Contain("Unknown fields: farmerId, note.");
    }

    [Fact]
    public void RateLimitKey_IsPerReporterAndUtcDay()
    {
        ListingReportRules.RateLimitKey("b-1", new DateTime(2026, 11, 7, 23, 59, 0, DateTimeKind.Utc))
            .Should().Be("LISTING_REPORT#b-1#2026-11-07");
    }

    [Fact]
    public void ExpiresAt_Is180DaysLater()
    {
        var now = new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc);

        ListingReportRules.ExpiresAt(now).Should().Be(new DateTimeOffset(now).AddDays(180).ToUnixTimeSeconds());
    }

    private static Dictionary<string, object> Data(string listingType, string listingId, string reason) => new()
    {
        ["listingType"] = listingType,
        ["listingId"] = listingId,
        ["reason"] = reason
    };
}
