using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// SIM-56 (user-entity.md 2.2.0): the caller's own profile.
///   - <c>GET /user/profile</c> reads it: the same <c>user</c> object sign-in returns.
///   - The <c>/sync</c> <c>user</c> entity edits it (<see cref="ProcessUserAsync"/>), so an edit
///     made offline waits in the phone's queue like any other change.
/// The read lives on this Lambda because it owns the profile write and already reads the users
/// table: one route on the existing integration, no new Lambda.
/// </summary>
public partial class SyncFunction
{
    internal const string UserProfilePath = "/user/profile";

    /// <summary>Whether the request is <c>GET /user/profile</c>, the only GET this Lambda serves.</summary>
    internal static bool IsProfileRead(APIGatewayProxyRequest request) =>
        string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
        && (request.Path ?? request.Resource ?? "").TrimEnd('/')
            .EndsWith(UserProfilePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>GET /user/profile</c>: 200 <c>{ "user": ... }</c> for the caller, read strongly
    /// consistent, as the <see cref="UserProfileDto"/> view (no Fayda ID, install ids, phone or
    /// flags). 401 without a caller, 404 <c>AUTH_013</c> without a row, 410 <c>ACCOUNT_004</c>
    /// for a deleting or deleted account and 403 <c>AUTH_025</c> for a suspended one (the refusals
    /// <c>/sync</c> answers), 503 <c>USER_PROFILE_UNAVAILABLE</c> when the row cannot be read.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleProfileReadAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        var userId = AuthHelper.GetUserId(request);
        if (string.IsNullOrEmpty(userId))
        {
            return LambdaResponse.Error(401, "UNAUTHORIZED", "Authentication required.");
        }

        if (_userRepo == null)
        {
            context.Logger.LogError("Profile read unavailable: user repository not configured.");
            return LambdaResponse.Error(503, "USER_PROFILE_UNAVAILABLE", "Your profile could not be read. Please try again.");
        }

        UserDto? user;
        try
        {
            user = await _userRepo.GetUserByIdAsync(userId, consistentRead: true);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Profile read failed for {MoneyAlarmLog.LogId(userId)} [{ex.GetType().Name}].");
            return LambdaResponse.Error(503, "USER_PROFILE_UNAVAILABLE", "Your profile could not be read. Please try again.");
        }

        if (user == null)
        {
            return LambdaResponse.Error(404, "AUTH_013", "User profile not found.");
        }

        if (AccountDeletionGuard.RefusalFor(user) is { } refused)
        {
            return refused;
        }

        return LambdaResponse.Ok(
            new UserProfileResponse { User = UserProfileDto.From(user) },
            AppJsonContext.Default.UserProfileResponse);
    }

    /// <summary>A refused <c>user</c> operation: <c>{code}: {message}</c>, nothing written.</summary>
    private static SyncOperationResult UserProfileError(SyncOperationRequest operation, string code, string message)
        => new()
        {
            OperationId = operation.OperationId,
            Status = "error",
            Message = $"{code}: {message}"
        };
}
