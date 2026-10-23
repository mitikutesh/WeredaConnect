namespace WeredaConnect.Server.Models;

/// <summary>
/// DELETE /user/account request body (account-deletion.md). Anything other than
/// <c>{ "confirm": true }</c> is refused with <c>400 ACCOUNT_001</c>, so an unknown property
/// fails the parse.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountDeletionRequest
{
    public bool? Confirm { get; init; }
}

/// <summary>DELETE /user/account 200 body, also returned on a replay after completion.</summary>
public sealed record AccountDeletionResponse
{
    public required bool Deleted { get; init; }
    public required string UserId { get; init; }
    public required string DeletionState { get; init; }
    public required string DeletedAt { get; init; }
}

/// <summary>One row that blocks the deletion (409 ACCOUNT_002).</summary>
public sealed record AccountDeletionBlocker
{
    /// <summary><c>order</c>, <c>escrow</c>, <c>delivery</c>, <c>dispute</c> or <c>payment_review</c>.</summary>
    public required string Type { get; init; }
    public required string Id { get; init; }
    public required string Status { get; init; }
    /// <summary>The user's role on that row: <c>buyer</c>, <c>farmer</c> or <c>trucker</c>.</summary>
    public required string Role { get; init; }
}

/// <summary>
/// Error body of the ACCOUNT_ codes. The 409 carries the blockers, the 503 the retry delay, and
/// the 410 that other endpoints return carries <c>error.deletionState</c>.
/// </summary>
public sealed record AccountDeletionError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public List<AccountDeletionBlocker>? Blockers { get; init; }
    public int? BlockerCount { get; init; }
    public int? RetryAfterSeconds { get; init; }
    public string? DeletionState { get; init; }
}

/// <summary>
/// Error envelope of the ACCOUNT_ codes. The 503 <c>ACCOUNT_005</c> repeats the state at the top
/// level, as the contract's example shows.
/// </summary>
public sealed record AccountDeletionErrorResponse
{
    public required AccountDeletionError Error { get; init; }
    public string? DeletionState { get; init; }
}

/// <summary>
/// Body of the sweep Lambda's scheduled and ops invocations (no API route reaches it):
/// <c>{"action":"sweep"}</c> or <c>{"action":"reapply","userIds":[...]}</c>.
/// </summary>
public sealed record AccountDeletionJobRequest
{
    public string? Action { get; init; }
    public List<string>? UserIds { get; init; }
}
