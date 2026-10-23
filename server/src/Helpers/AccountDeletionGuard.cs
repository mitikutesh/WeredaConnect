using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// ACC-02: the refusal a write endpoint returns once the caller's account carries
/// <c>deletionState</c> (account-deletion.md "Effects on Other Endpoints"): <c>410 ACCOUNT_004</c>
/// with that state. For <c>in_progress</c> the client offers "Finish deleting"; otherwise it wipes
/// its data and goes to login.
/// </summary>
public static class AccountDeletionGuard
{
    public const string GoneCode = "ACCOUNT_004";
    public const string GoneMessage = "This account has been deleted.";

    /// <summary>
    /// The 410 when the caller's row, read strongly consistent, carries <c>deletionState</c>; null
    /// otherwise (also when there is no row: the endpoint answers that itself). A failed read throws,
    /// so the caller fails closed with its own retryable error.
    /// </summary>
    public static async Task<APIGatewayProxyResponse?> RefuseIfDeletingAsync(IUserRepository users, string userId)
    {
        var user = await users.GetUserByIdAsync(userId, consistentRead: true);
        return string.IsNullOrEmpty(user?.DeletionState) ? null : Gone(user.DeletionState!);
    }

    /// <summary>The refusal text of a payment for an order of a deleting or deleted account.</summary>
    public const string DeletedPartyMessage = "A party to this order has deleted their account.";

    /// <summary>
    /// The counterparty money entry points: true when any of these parties' rows, read strongly
    /// consistent, carries <c>deletionState</c> (empty ids are skipped, a missing row is not a
    /// deletion). A failed read throws, so no payment passes on doubt.
    /// </summary>
    public static async Task<bool> AnyPartyDeletingAsync(IUserRepository users, params string?[] partyIds)
    {
        foreach (var partyId in partyIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal))
        {
            var party = await users.GetUserByIdAsync(partyId!, consistentRead: true);
            if (!string.IsNullOrEmpty(party?.DeletionState))
            {
                return true;
            }
        }

        return false;
    }

    public static APIGatewayProxyResponse Gone(string deletionState)
        => LambdaResponse.Json(410, new AccountDeletionErrorResponse
        {
            Error = new AccountDeletionError
            {
                Code = GoneCode,
                Message = GoneMessage,
                DeletionState = deletionState
            }
        }, AppJsonContext.Default.AccountDeletionErrorResponse);
}
