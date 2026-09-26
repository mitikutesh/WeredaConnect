namespace WeredaConnect.Server.Models;

/// <summary>
/// User roles in WeredaConnect. Immutable after registration.
/// Uses JsonStringEnumConverter source generator for AOT-compatible enum serialization.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<UserRole>))]
public enum UserRole
{
    Farmer,
    Trucker,
    Buyer
}

/// <summary>
/// Full user DTO returned from auth endpoints and profile queries.
/// Maps to DynamoDB users table item and auth-api.md contract.
/// </summary>
public sealed record UserDto
{
    public required string UserId { get; init; }
    public required string PhoneNumber { get; init; }
    public string? DisplayName { get; init; }
    public string? FullName { get; init; }
    public required string Role { get; init; }
    public required string Woreda { get; init; }
    public string? FaydaId { get; init; }
    public bool FaydaVerified { get; init; }
    public string? FaydaVerifiedAt { get; init; }
    public string PreferredLanguage { get; init; } = "am";
    public required bool IsPhoneVerified { get; init; }
    public bool Suspended { get; init; }
    /// <summary>Install ids (X-Device-Id) that have signed in; a new one triggers a notice.</summary>
    public List<string> KnownDevices { get; init; } = [];
    public string? ProfilePhotoRef { get; init; }
    // Buyer-only identity fields (F-32); null for farmers/truckers.
    public string? BuyerType { get; init; }
    public string? CompanyName { get; init; }
    public required string CreatedAt { get; init; }
    public required string UpdatedAt { get; init; }
    public required int Version { get; init; }
}

/// <summary>
/// Minimal user DTO for embedding in other entities (crop listings, trips).
/// Minimizes payload size for 3G optimization per user-entity.md contract.
/// </summary>
public sealed record MinimalUserDto
{
    public required string UserId { get; init; }
    public string? DisplayName { get; init; }
    public string? FullName { get; init; }
    public required string Role { get; init; }
    public required string Woreda { get; init; }
    public string? ProfilePhotoRef { get; init; }
}
