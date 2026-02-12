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
}
