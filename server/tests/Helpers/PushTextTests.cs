using FluentAssertions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;
using Xunit;
using P = WeredaConnect.Server.Services.NotificationParams;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// ESC-21: push text parity. Every notification type has a title and a body in en, am and om
/// that read cleanly with or without params: no template characters, no phone number, a body
/// under 140 characters; amounts and Ethiopia-time deadlines formatted for reading.
/// </summary>
public class PushTextTests
{
    private static readonly IReadOnlyDictionary<string, string> Full = new Dictionary<string, string>
    {
        [P.BuyerName] = "Abebe Tesfaye",
        [P.FarmerName] = "Chaltu Gemechu",
        [P.TruckerName] = "Dawit Kebede",
        [P.CropType] = "teff",
        [P.QuantityKg] = "500",
        [P.AmountEtb] = "42500.00",
        [P.DeadlineAt] = "2026-10-27T15:00:00.0000000Z",
        [P.PickupDate] = "2026-10-29T06:00:00.0000000Z",
        [P.Reason] = "fayda_required",
        [P.CancelledBy] = "buyer",
        [P.Resolution] = "farmer_wins",
        [P.Party] = "buyer"
    };

    /// <summary>The longest values the rows allow: 100-character names and seven-digit amounts.</summary>
    private static readonly IReadOnlyDictionary<string, string> Longest = new Dictionary<string, string>
    {
        [P.BuyerName] = new string('A', 100),
        [P.FarmerName] = new string('B', 100),
        [P.TruckerName] = new string('C', 100),
        [P.CropType] = "chickpea",
        [P.QuantityKg] = "1000000",
        [P.AmountEtb] = "9999999.99",
        [P.DeadlineAt] = "2026-12-31T20:59:00Z",
        [P.PickupDate] = "2026-12-31T20:59:00Z",
        [P.Reason] = "phone_unverified",
        [P.Resolution] = "buyer_wins"
    };

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    public static IEnumerable<object[]> TypesAndLanguages()
        => NotificationTypes.All.SelectMany(type => PushText.Languages.Select(language => new object[] { type, language }));

    [Theory]
    [MemberData(nameof(TypesAndLanguages))]
    public void EveryType_InEveryLanguage_HasItsOwnCleanText(string type, string language)
    {
        foreach (var p in new[] { Full, Longest, Empty })
        {
            var (title, body) = PushText.Render(type, language, p);

            title.Should().NotBeNullOrWhiteSpace();
            body.Should().NotBeNullOrWhiteSpace();
            foreach (var text in new[] { title, body })
            {
                text.Should().NotContainAny("{", "}", "[", "]");
                text.Should().NotContain("+251");
                text.Should().NotContain("  ");
                text.Should().Be(text.Trim());
            }

            body.Length.Should().BeLessThan(140, $"{type}/{language} body: {body}");
            title.Length.Should().BeLessThanOrEqualTo(PushText.MaxTitleLength);
        }

        // A real template, not the generic line an unknown type gets.
        PushText.Render(type, language, Full).Title
            .Should().NotBe(PushText.Render("no_such_type", language, Full).Title);
    }

    [Theory]
    [MemberData(nameof(TypesAndLanguages))]
    public void TypicalText_FitsWithoutBeingCut(string type, string language)
    {
        var (_, body) = PushText.Render(type, language, Full);

        body.Should().NotEndWith("…", "a typical order fits the body limit");
    }

    [Fact]
    public void LanguagesAndFallback_AreTheContractsThree()
    {
        PushText.Languages.Should().Equal("en", "am", "om");
        PushText.FallbackLanguage.Should().Be("am");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fr")]
    [InlineData("ti")]
    [InlineData("  ")]
    public void UnknownLanguage_RendersAmharic(string? language)
    {
        PushText.Render(NotificationTypes.OrderRequest, language, Full)
            .Should().Be(PushText.Render(NotificationTypes.OrderRequest, "am", Full));
    }

    [Theory]
    [InlineData("EN", "en")]
    [InlineData("en-US", "en")]
    [InlineData("om_ET", "om")]
    [InlineData(" am ", "am")]
    public void StoredPreference_IsReadCaseAndRegionInsensitive(string stored, string expected)
    {
        PushText.LanguageOf(stored).Should().Be(expected);
    }

    [Fact]
    public void UnknownType_RendersAGenericLine()
    {
        var (title, body) = PushText.Render("brand_new_type", "en", Full);

        title.Should().Be("Order update");
        body.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("9640.00", "9,640 ETB")]
    [InlineData("9640", "9,640 ETB")]
    [InlineData("9640.5", "9,640.50 ETB")]
    [InlineData("1234567.89", "1,234,567.89 ETB")]
    [InlineData("0.00", "0 ETB")]
    [InlineData("", "")]
    [InlineData("abc", "")]
    [InlineData("-5", "")]
    [InlineData(null, "")]
    public void Amounts_UseThousandsSeparators_AndNoDecimalsWhenWhole(string? raw, string expected)
    {
        PushText.FormatAmount("en", raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("en", "2026-10-27T15:00:00.0000000Z", "27 Oct 18:00")]
    [InlineData("en", "2026-10-31T22:30:00Z", "1 Nov 01:30")]
    [InlineData("en", "2026-10-27T15:00:00+00:00", "27 Oct 18:00")]
    [InlineData("am", "2026-10-27T15:00:00Z", "27 ኦክቶ 18:00")]
    [InlineData("om", "2026-10-27T15:00:00Z", "27 Onk 18:00")]
    [InlineData("en", "not a date", "")]
    [InlineData("en", null, "")]
    public void Deadlines_AreShownInEthiopiaTime(string language, string? iso, string expected)
    {
        PushText.FormatDeadline(language, iso).Should().Be(expected);
    }

    [Fact]
    public void PickupDates_AreDayAndMonth_InEthiopiaTime()
    {
        PushText.FormatDate("en", "2026-10-29T22:00:00Z").Should().Be("30 Oct");
    }

    [Fact]
    public void OrderRequest_InEnglish_ReadsAsAWholeSentence()
    {
        var (title, body) = PushText.Render(NotificationTypes.OrderRequest, "en", Full);

        title.Should().Be("New order: 500 kg teff");
        body.Should().Be("Buyer Abebe Tesfaye paid 42,500 ETB, held safely until delivery. Please accept or decline by 27 Oct 18:00.");
    }

    [Fact]
    public void CropAndQuantity_AreWrittenTheWayEachLanguageReadsThem()
    {
        PushText.Render(NotificationTypes.OrderRequest, "am", Full).Title.Should().Contain("500 ኪ.ግ ጤፍ");
        PushText.Render(NotificationTypes.OrderRequest, "om", Full).Title.Should().Contain("xaafii kg 500");

        var unknownCrop = new Dictionary<string, string>(Full) { [P.CropType] = "quinoa" };
        PushText.Render(NotificationTypes.OrderRequest, "en", unknownCrop).Title.Should().Be("New order: 500 kg quinoa");
    }

    [Fact]
    public void MissingParams_DropTheirOptionalPart_AndNeverLeaveAPlaceholder()
    {
        var noDeadline = new Dictionary<string, string>(Full);
        noDeadline.Remove(P.DeadlineAt);
        noDeadline.Remove(P.AmountEtb);

        var (title, body) = PushText.Render(NotificationTypes.OrderRequest, "en", noDeadline);

        title.Should().Be("New order: 500 kg teff");
        body.Should().Be("Buyer Abebe Tesfaye paid, held safely until delivery. Please accept or decline.");

        PushText.Render(NotificationTypes.OrderRequest, "en", Empty)
            .Should().Be(("New order", "Buyer paid, held safely until delivery. Please accept or decline."));
    }

    [Fact]
    public void APhoneNumberInAName_IsNeverPushed()
    {
        var p = new Dictionary<string, string>(Full) { [P.BuyerName] = "Abebe +251 911-22-33-44 Tesfaye" };

        foreach (var language in PushText.Languages)
        {
            var (title, body) = PushText.Render(NotificationTypes.OrderRequest, language, p);
            (title + body).Should().NotContain("+251").And.NotContain("911").And.NotContain("33-44");
            body.Should().Contain("Abebe Tesfaye");
        }

        PushText.Name("0911223344").Should().BeEmpty();
        PushText.Name("Lot 12 Farm").Should().Be("Lot 12 Farm");
    }

    [Theory]
    [InlineData("Abebe 0911/223/344", "Abebe")]
    [InlineData("Abebe 0911_223344", "Abebe")]
    [InlineData("Abebe 09 11 22 33 44", "Abebe")]
    [InlineData("Abebe ፱፩፩፪፪፫፫፬", "Abebe")]
    [InlineData("Lot 12 Farm 345", "Lot 12 Farm 345")]
    public void PhoneNumbers_AreRemoved_HoweverTheyAreSeparated(string raw, string expected)
    {
        PushText.ScrubName(raw).Should().Be(expected);
    }

    [Fact]
    public void FormatCharacters_AreRemoved_SoARightToLeftOverrideCannotReorderThePush()
    {
        PushText.ScrubName("Abebe\u202E Kebede\u200B").Should().Be("Abebe Kebede");
    }

    [Fact]
    public void ACut_NeverSplitsASurrogatePair()
    {
        // 29 letters then an emoji (two UTF-16 units) straddling the 30 character cut.
        var name = PushText.Name(new string('A', PushText.MaxNameLength - 2) + "\U0001F33E" + "tail");

        name.Should().EndWith("…");
        char.IsHighSurrogate(name[^2]).Should().BeFalse();
    }

    [Fact]
    public void LongNames_AreCut_SoTheBodyStaysWhole()
    {
        var (_, body) = PushText.Render(NotificationTypes.OrderRequest, "en", Longest);

        body.Should().Contain(new string('A', PushText.MaxNameLength - 1) + "…");
        body.Should().EndWith("Please accept or decline by 31 Dec 23:59.");
    }

    [Theory]
    [InlineData("fayda_required", "Please verify your Fayda ID in the app.")]
    [InlineData("phone_unverified", "Please verify your phone number in the app.")]
    [InlineData("profile_missing", "Please complete your profile in the app.")]
    [InlineData("something_new", "Open the app to see what is needed.")]
    public void PayoutBlocked_SaysWhatThePayeeCanDo(string reason, string hint)
    {
        var p = new Dictionary<string, string> { [P.AmountEtb] = "1500.00", [P.Reason] = reason };

        PushText.Render(NotificationTypes.PayoutBlocked, "en", p).Body
            .Should().Be($"Your payment of 1,500 ETB is waiting. {hint}");
    }

    [Theory]
    [InlineData("farmer_wins", "The money goes to the farmer.")]
    [InlineData("buyer_wins", "The money goes back to the buyer.")]
    [InlineData("", "Open the app for the details.")]
    public void DisputeResolved_SaysTheVerdict(string resolution, string sentence)
    {
        var p = new Dictionary<string, string> { [P.Resolution] = resolution };

        PushText.Render(NotificationTypes.DisputeResolved, "en", p).Body
            .Should().Be($"Our team has decided on the reported problem. {sentence}");
    }

    [Fact]
    public void AmharicAndOromo_AreTheirOwnScripts()
    {
        var (amTitle, amBody) = PushText.Render(NotificationTypes.PaymentConfirmed, "am", Full);
        (amTitle + amBody).Should().MatchRegex("[ሀ-፿]");
        amBody.Should().Contain("42,500 ብር");

        var (omTitle, omBody) = PushText.Render(NotificationTypes.PaymentConfirmed, "om", Full);
        (omTitle + omBody).Should().NotMatchRegex("[ሀ-፿]");
        omBody.Should().Contain("42,500 Birrii");
    }
}
