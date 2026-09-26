using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// Know-your-customer gate for a buyer paying a farmer. Mirrors the checks
/// EscrowService makes on /escrow/initiate so the order-funded leg (the main
/// marketplace money path) enforces the same rules before any money moves:
/// both parties phone-verified, and above the NBE threshold both Fayda-verified.
/// </summary>
public static class KycPolicy
{
    public const string ErrorCode = "ORDER_KYC_REQUIRED";

    /// <summary>NBE mandate: Fayda ID required for digital transactions above this amount.</summary>
    public const decimal FaydaThresholdEtb = 10_000m;

    /// <summary>
    /// Null when the payment may proceed; otherwise the sync error message
    /// (code prefix included). A missing party record fails closed.
    /// </summary>
    public static string? CheckPayment(UserDto? buyer, UserDto? farmer, decimal amountEtb)
    {
        if (buyer == null || !buyer.IsPhoneVerified)
        {
            return $"{ErrorCode}: Verify your phone number before paying.";
        }

        if (farmer == null || !farmer.IsPhoneVerified)
        {
            return $"{ErrorCode}: The farmer's phone number is not verified yet.";
        }

        if (amountEtb > FaydaThresholdEtb)
        {
            if (!buyer.FaydaVerified)
            {
                return $"{ErrorCode}: Verify your Fayda ID to pay more than 10,000 ETB.";
            }

            if (!farmer.FaydaVerified)
            {
                return $"{ErrorCode}: The farmer must verify their Fayda ID for orders above 10,000 ETB.";
            }
        }

        return null;
    }
}
