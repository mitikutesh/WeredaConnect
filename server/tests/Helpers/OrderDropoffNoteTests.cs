using System.Text.Json;
using FluentAssertions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// LT-12: the drop-off note's field rules (one line, at most 80 user-visible characters, counted as
/// extended grapheme clusters so an Amharic syllable or an emoji is one) and who may read it (the
/// buyer, the farmer and the committed trucker of a live order; nobody else).
/// </summary>
public class OrderDropoffNoteTests
{
    private const string BuyerId = "buyer-001";
    private const string FarmerId = "farmer-001";
    private const string TruckerId = "trucker-001";

    // ===== Length and format =====

    [Fact]
    public void EightyCharacters_AreAccepted_EightyOne_AreRefused()
    {
        OrderDropoffNote.ValidationError(new string('a', 80)).Should().BeNull();
        OrderDropoffNote.ValidationError(new string('a', 81))
            .Should().Be("dropoffNote must not exceed 80 characters.");
    }

    [Fact]
    public void AmharicSyllables_CountOneEach()
    {
        // "ሰ" (U+1230) is one syllable and one UTF-16 unit; 80 of them fit, 81 do not.
        var eighty = string.Concat(Enumerable.Repeat("ሰ", 80));
        OrderDropoffNote.Length(eighty).Should().Be(80);
        OrderDropoffNote.ValidationError(eighty).Should().BeNull();
        OrderDropoffNote.ValidationError(eighty + "ሰ").Should().NotBeNull();

        OrderDropoffNote.Length("መርካቶ፣ አንዋር መስጊድ አጠገብ").Should().Be(20);
    }

    [Fact]
    public void AnEmojiWithModifiers_CountsAsOneCharacter()
    {
        // A thumbs up with a skin tone is 4 UTF-16 units but one character on screen.
        const string thumbs = "\U0001F44D\U0001F3FD";
        thumbs.Length.Should().Be(4);
        OrderDropoffNote.Length(thumbs).Should().Be(1);
        OrderDropoffNote.ValidationError(new string('a', 79) + thumbs).Should().BeNull();
    }

    [Fact]
    public void SurroundingSpaces_DoNotCount_AndAreTrimmed()
    {
        var padded = "   " + new string('a', 80) + "  ";
        OrderDropoffNote.ValidationError(padded).Should().BeNull();
        OrderDropoffNote.Normalize(padded).Should().Be(new string('a', 80));
    }

    [Theory]
    [InlineData("Merkato\nnear the mosque")]
    [InlineData("Merkato\r\ngate 2")]
    [InlineData("Merkato\tgate 2")]
    [InlineData("Merkato\u0085gate 2")]
    [InlineData("Merkato\u2028gate 2")]
    [InlineData("Merkato\u2029gate 2")]
    public void ALineBreakOrAControlCharacter_IsRefused(string note)
        => OrderDropoffNote.ValidationError(note).Should().Be("dropoffNote must be one line of text.");

    [Fact]
    public void JsonText_AndNull_AreAccepted_OtherJsonKindsAreNot()
    {
        using var doc = JsonDocument.Parse("{\"s\":\" Kality \",\"n\":null,\"num\":5,\"obj\":{}}");
        var root = doc.RootElement;

        OrderDropoffNote.ValidationError(root.GetProperty("s")).Should().BeNull();
        OrderDropoffNote.Normalize(root.GetProperty("s")).Should().Be("Kality");
        OrderDropoffNote.ValidationError(root.GetProperty("n")).Should().BeNull();
        OrderDropoffNote.Normalize(root.GetProperty("n")).Should().BeEmpty();
        OrderDropoffNote.ValidationError(null).Should().BeNull();
        OrderDropoffNote.ValidationError(root.GetProperty("num")).Should().Be("dropoffNote must be text.");
        OrderDropoffNote.ValidationError(root.GetProperty("obj")).Should().Be("dropoffNote must be text.");
    }

    // ===== Who may read it =====

    [Theory]
    [InlineData(BuyerId, "created")]
    [InlineData(BuyerId, "cancelled")]
    [InlineData(FarmerId, "funded")]
    [InlineData(FarmerId, "settled")]
    public void TheBuyerAndTheFarmer_AlwaysReadIt(string caller, string status)
    {
        var order = Order(status, truckerResponse: "not_asked");

        OrderDropoffNote.VisibleTo(order, caller).Should().BeTrue();
        OrderDropoffNote.For(order, caller).Should().Be("Merkato, near Anwar Mosque");
    }

    [Theory]
    [InlineData("accepted", "accepted")]
    [InlineData("pickup_confirmed", "accepted")]
    [InlineData("in_transit", "accepted")]
    [InlineData("delivered", "accepted")]
    // An order from before ESC-15 has no stored answer: from accepted on its trucker counts as committed.
    [InlineData("accepted", null)]
    public void TheCommittedTrucker_ReadsIt(string status, string? truckerResponse)
        => OrderDropoffNote.VisibleTo(Order(status, truckerResponse), TruckerId).Should().BeTrue();

    [Theory]
    [InlineData("funded", "not_asked")]
    [InlineData("funded", "pending")]
    [InlineData("awaiting_transport", "declined")]
    [InlineData("awaiting_transport", "expired")]
    [InlineData("awaiting_transport", "cancelled")]
    // Committed, but the money went back: nothing to deliver any more.
    [InlineData("cancelled", "accepted")]
    [InlineData("dispute_resolved", "accepted")]
    // An unpaid order of an older row reads as accepted, but no money is held yet.
    [InlineData("created", null)]
    public void ATruckerNotCommittedToALiveOrder_DoesNotReadIt(string status, string? truckerResponse)
    {
        var order = Order(status, truckerResponse);

        OrderDropoffNote.VisibleTo(order, TruckerId).Should().BeFalse();
        OrderDropoffNote.For(order, TruckerId).Should().BeNull();
    }

    [Theory]
    [InlineData("someone-else")]
    [InlineData("")]
    [InlineData(null)]
    public void AnyoneElse_NeverReadsIt(string? caller)
        => OrderDropoffNote.VisibleTo(Order("accepted", "accepted"), caller).Should().BeFalse();

    [Fact]
    public void AnEmptyStoredNote_IsNoNote()
    {
        var order = Order("accepted", "accepted");
        order["dropoffNote"] = "";

        OrderDropoffNote.Of(order).Should().BeNull();
        OrderDropoffNote.For(order, BuyerId).Should().BeNull();
    }

    // ===== Sync results =====

    [Fact]
    public void ASyncResult_ForAnUncommittedTrucker_LeavesTheNoteOut_AndKeepsTheRest()
    {
        var row = Order("funded", "pending");
        var result = new SyncOperationResult { OperationId = "op-1", Status = "conflict", ServerData = row };

        var forTrucker = OrderDropoffNote.ForCaller(result, TruckerId);
        var forBuyer = OrderDropoffNote.ForCaller(result, BuyerId);

        forTrucker.ServerData.Should().NotContainKey("dropoffNote");
        forTrucker.ServerData.Should().ContainKey("status");
        forTrucker.Status.Should().Be("conflict");
        row.Should().ContainKey("dropoffNote", "the stored row itself is never changed");
        forBuyer.Should().BeSameAs(result);
    }

    [Fact]
    public void ASyncResult_WithoutARow_IsUnchanged()
    {
        var result = new SyncOperationResult { OperationId = "op-1", Status = "error", Message = "ORDER_VALIDATION: x" };

        OrderDropoffNote.ForCaller(result, TruckerId).Should().BeSameAs(result);
    }

    private static Dictionary<string, object> Order(string status, string? truckerResponse)
    {
        var order = new Dictionary<string, object>
        {
            ["orderId"] = "order-001",
            ["buyerId"] = BuyerId,
            ["farmerId"] = FarmerId,
            ["truckerId"] = TruckerId,
            ["tripId"] = "trip-001",
            ["status"] = status,
            ["dropoffNote"] = "Merkato, near Anwar Mosque"
        };
        if (truckerResponse != null)
        {
            order["truckerResponse"] = truckerResponse;
        }

        return order;
    }
}
