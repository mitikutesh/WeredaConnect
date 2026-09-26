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
    /// Get a user by phone number via PhoneIndex GSI.
    /// </summary>
    Task<UserDto?> GetUserByPhoneAsync(string phone);

    /// <summary>
    /// Update a user with optimistic concurrency (version check).
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
}
