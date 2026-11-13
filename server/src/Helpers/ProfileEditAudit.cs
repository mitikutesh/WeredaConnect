using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// FUP-317 (user-entity.md 2.3.0 section 14): the transaction-audit row of an accepted profile edit. One row per edit
/// that changes <c>displayName</c>, <c>woreda</c> or <c>companyName</c>, written in the same transaction as the users
/// row (<see cref="Data.IUserRepository.UpdateUserWithAuditAsync"/>), so an edit never lands without it. Its
/// <c>metadata</c> holds the old and new value of each changed field (<see cref="ProfileFieldChange"/>).
/// A user's rows share the partition <c>profile-{userId}</c>, apart from the admin action rows (<c>{userId}</c>) and
/// the account rows (<c>account-{userId}</c>): the admin reads the history with one Query, and these rows, the only
/// ones that hold old names, stay in one place. The values are personal data and are never logged.
/// </summary>
internal static class ProfileEditAudit
{
    public const string Action = "profile_edited";
    public const string EntityType = "user";

    /// <summary>The newest edits the admin user detail lists.</summary>
    public const int HistoryPageSize = 50;

    /// <summary>The partition of a user's profile edit rows.</summary>
    public static string TransactionId(string userId) => $"profile-{userId}";

    /// <summary>
    /// The changed fields in <see cref="UserProfileEdit.TextFields"/> order, each with the stored value (absent when
    /// the row had none) and the new one.
    /// </summary>
    public static List<ProfileFieldChange> Changes(UserDto stored, IReadOnlyDictionary<string, string> changed)
        => UserProfileEdit.TextFields
            .Where(changed.ContainsKey)
            .Select(field => new ProfileFieldChange
            {
                Field = field,
                From = UserProfileEdit.StoredValue(stored, field) is { Length: > 0 } old ? old : null,
                To = changed[field]
            })
            .ToList();

    /// <summary>
    /// The row for <paramref name="changed"/> on <paramref name="stored"/>, at <paramref name="timestamp"/>: the actor
    /// and where from come from <paramref name="ctx"/>.
    /// </summary>
    public static Dictionary<string, object> Entry(
        UserDto stored, IReadOnlyDictionary<string, string> changed, AuditContext ctx, string timestamp)
        => TransactionAuditWriter.BuildOnceEntry(
            TransactionId(stored.UserId), EntityType, stored.UserId, Action, null, null, ctx, timestamp,
            JsonSerializer.Serialize(Changes(stored, changed), AppJsonContext.Default.ListProfileFieldChange));

    /// <summary>One row as the admin user detail lists it. A row whose metadata cannot be read lists no changes.</summary>
    public static AdminProfileEditDto FromRow(Dictionary<string, object> row)
    {
        List<ProfileFieldChange>? changes = null;
        if (Str(row, "metadata") is { Length: > 0 } metadata)
        {
            try
            {
                changes = JsonSerializer.Deserialize(metadata, AppJsonContext.Default.ListProfileFieldChange);
            }
            catch (JsonException)
            {
                changes = null;
            }
        }

        return new AdminProfileEditDto
        {
            EditedAt = Str(row, "timestamp") ?? "",
            Actor = Str(row, "actor") ?? "",
            ActorRole = Str(row, "actorRole"),
            Changes = changes?.Where(change => change != null).ToList() ?? []
        };
    }

    private static string? Str(Dictionary<string, object> row, string key)
        => row.TryGetValue(key, out var value) ? value?.ToString() : null;
}
