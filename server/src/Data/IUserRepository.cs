using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Data;

/// <summary>
/// Repository interface for user entity persistence in DynamoDB.
/// </summary>
public interface IUserRepository
{
    /// <summary>
    /// Create a new user. Fails if userId already exists.
    /// </summary>
    Task<AuthResult<UserDto>> CreateUserAsync(UserDto user);

    /// <summary>
    /// Get a user by primary key (userId).
    /// </summary>
    Task<UserDto?> GetUserByIdAsync(string userId);

    /// <summary>
    /// Get a user by primary key with a strongly consistent read when <paramref name="consistentRead"/>
    /// is true. ACC-02: the refusals for a deleting account read the row this way, so a write that
    /// races the start of a deletion sees it.
    /// </summary>
    Task<UserDto?> GetUserByIdAsync(string userId, bool consistentRead);

    /// <summary>
    /// Get a user by phone number via PhoneIndex GSI.
    /// </summary>
    Task<UserDto?> GetUserByPhoneAsync(string phone);

    /// <summary>
    /// Update a user with optimistic concurrency (version check). Refused like a version conflict
    /// once the row carries <c>deletionState</c> (ACC-02).
    /// </summary>
    Task<AuthResult<UserDto>> UpdateUserAsync(UserDto user, int expectedVersion);

    /// <summary>
    /// Find a user by Fayda ID. Returns null if no user has this Fayda ID linked.
    /// Uses a table scan with filter — acceptable for duplicate check during registration.
    /// </summary>
    Task<UserDto?> GetUserByFaydaIdAsync(string faydaId);

    /// <summary>
    /// Remove a profile row. Used only to replace an unconfirmed registration
    /// (phone never verified); never for confirmed accounts.
    /// </summary>
    Task DeleteUserAsync(string userId);

    /// <summary>
    /// The phone numbers of these users in one batched read (BatchGetItem, phone attribute only).
    /// Users with no row or no phone are left out, and so are keys DynamoDB still left unprocessed
    /// after a few retries: a caller shows no phone rather than failing.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetPhoneNumbersAsync(IReadOnlyCollection<string> userIds);
}
