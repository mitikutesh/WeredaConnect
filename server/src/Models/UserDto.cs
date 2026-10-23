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
    /// <summary>
    /// Last successful Fayda login through /user/fayda-link (ISO 8601). Payments above
    /// 10,000 ETB need it to be recent (step-up AUTH_041). Server-side only, never
    /// returned to clients.
    /// </summary>
    [JsonIgnore]
    public string? FaydaAuthenticatedAt { get; init; }
    public string PreferredLanguage { get; init; } = "am";
    public required bool IsPhoneVerified { get; init; }
    public bool Suspended { get; init; }
    /// <summary>Install ids (X-Device-Id) that have signed in; a new one triggers a notice.</summary>
    public List<string> KnownDevices { get; init; } = [];
    public string? ProfilePhotoRef { get; init; }
    // Buyer-only identity fields (F-32); null for farmers/truckers.
    public string? BuyerType { get; init; }
    public string? CompanyName { get; init; }
    /// <summary>
    /// ACC-02: <c>in_progress</c>, <c>cognito_pending</c> or <c>completed</c> once the user asked
    /// to delete the account (account-deletion.md); null on a live account. Server-side only.
    /// </summary>
    [JsonIgnore]
    public string? DeletionState { get; init; }
    [JsonIgnore]
    public string? DeletionRequestedAt { get; init; }
    /// <summary>ACC-02: when the personal data was erased (the tombstone write). Server-side only.</summary>
    [JsonIgnore]
    public string? DeletedAt { get; init; }
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
