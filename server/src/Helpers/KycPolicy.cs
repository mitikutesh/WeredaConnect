using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// Know-your-customer gate for a buyer paying a farmer. Mirrors the checks
/// EscrowService makes on /escrow/initiate so the order-funded leg (the main
/// marketplace money path) enforces the same rules before any money moves:
/// both parties phone-verified, and above the NBE threshold both Fayda-verified.
/// </summary>
/// <remarks>
/// Messages read <c>ORDER_KYC_REQUIRED: &lt;reason&gt;: &lt;text&gt;</c>. The reason
/// says whose verification is missing, so the client only offers Fayda to the
/// buyer when the buyer can fix it (a farmer-side problem cannot be fixed by the
/// buyer verifying).
/// </remarks>
public static class KycPolicy
{
    public const string ErrorCode = "ORDER_KYC_REQUIRED";

    /// <summary>Reason tokens, part of the sync contract (specs/contracts/sync-protocol.md).</summary>
    public const string ReasonBuyerPhone = "buyer_phone";
    public const string ReasonFarmerPhone = "farmer_phone";
    public const string ReasonBuyerFayda = "buyer_fayda";
    public const string ReasonFarmerFayda = "farmer_fayda";
    public const string ReasonUnavailable = "unavailable";

    /// <summary>NBE mandate: Fayda ID required for digital transactions above this amount.</summary>
    public const decimal FaydaThresholdEtb = 10_000m;

    public static string Message(string reason, string text) => $"{ErrorCode}: {reason}: {text}";

    /// <summary>
    /// Null when the payment may proceed; otherwise the sync error message
    /// (code prefix and reason included). A missing party record fails closed.
    /// </summary>
    public static string? CheckPayment(UserDto? buyer, UserDto? farmer, decimal amountEtb)
    {
        if (buyer == null || !buyer.IsPhoneVerified)
        {
            return Message(ReasonBuyerPhone, "Verify your phone number before paying.");
        }

        if (farmer == null || !farmer.IsPhoneVerified)
        {
            return Message(ReasonFarmerPhone, "The farmer's phone number is not verified yet.");
        }

        if (amountEtb > FaydaThresholdEtb)
        {
            if (!buyer.FaydaVerified)
            {
                return Message(ReasonBuyerFayda, "Verify your Fayda ID to pay more than 10,000 ETB.");
            }

            if (!farmer.FaydaVerified)
            {
                return Message(ReasonFarmerFayda, "The farmer must verify their Fayda ID for orders above 10,000 ETB.");
            }
        }

        return null;
    }
}
