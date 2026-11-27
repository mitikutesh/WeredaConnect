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

    public const string SuspendedCode = "AUTH_025";
    public const string SuspendedMessage = "Account suspended.";

    /// <summary>
    /// ESC-29 (reputation-api.md 8.4): the refusal of a write endpoint for the caller's row as read:
    /// <c>410 ACCOUNT_004</c> for a deleting or deleted account, else <c>403 AUTH_025</c> for a
    /// suspended one (the code sign-in, OTP and refresh already answer), else null. A missing row
    /// refuses nothing: the endpoint answers that itself.
    /// </summary>
    public static APIGatewayProxyResponse? RefusalFor(UserDto? caller)
    {
        if (caller == null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(caller.DeletionState))
        {
            return Gone(caller.DeletionState!);
        }

        return caller.Suspended ? LambdaResponse.Error(403, SuspendedCode, SuspendedMessage) : null;
    }

    /// <summary>
    /// <see cref="RefuseIfDeletingAsync"/> plus the suspension refusal of <see cref="RefusalFor"/>,
    /// from the same strongly consistent read. A failed read throws (the caller fails closed).
    /// </summary>
    public static async Task<APIGatewayProxyResponse?> RefuseIfDeletingOrSuspendedAsync(IUserRepository users, string userId)
        => RefusalFor(await users.GetUserByIdAsync(userId, consistentRead: true));

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
