namespace WeredaConnect.Server.Models;

/// <summary>
/// POST /auth/register request body.
/// FaydaVerificationToken is required (auth-api 2.2.0): accounts are created only
/// through Fayda. Nullable so a missing token is reported as AUTH_021 by the
/// validator rather than as a JSON error. The SMS code is still asked for when
/// Fayda's phone differs from the registration phone.
///
/// The auth request bodies mark no member <c>required</c> (auth-api 2.13.0, SIM-27): a
/// missing field must reach the validator and get its own code (AUTH_001 phone, AUTH_002
/// password, ...). With <c>required</c> the deserializer threw and the caller got
/// <c>AUTH_007 Invalid JSON</c>. The source-generated reader does not run the initializers
/// of init properties: a missing field arrives as null, which the validators treat like a
/// blank one. The initializers only serve code that builds a request itself.
/// </summary>
public sealed record RegisterRequest
{
    /// <summary>The language of a registration that names none (auth-api: optional, default am).</summary>
    public const string DefaultLanguage = "am";

    public string PhoneNumber { get; init; } = "";
    public string Password { get; init; } = "";
    public string Role { get; init; } = "";
    public string Woreda { get; init; } = "";
    public string? FaydaVerificationToken { get; init; }
    public string? DisplayName { get; init; }

    /// <summary>
    /// The initializer does not apply to a parsed body: the source-generated reader leaves an absent
    /// field null, so RegisterFunction falls back to <see cref="DefaultLanguage"/>.
    /// </summary>
    public string PreferredLanguage { get; init; } = DefaultLanguage;

    // Buyer-only identity fields (F-32). buyerType is "private" or "company";
    // companyName is required when buyerType is "company". Both must be absent
    // for non-buyer roles. The trucker driving-license is intentionally NOT part
    // of this contract — it stays local-only on the device until an image-upload
    // pipeline exists (F-32 scope A).
    public string? BuyerType { get; init; }
    public string? CompanyName { get; init; }
}

/// <summary>
/// POST /auth/register success response (201 Created).
/// Returns tokens directly — no OTP step required (Fayda-verified).
/// </summary>
public sealed record RegisterResponse
{
    public required string UserId { get; init; }
    public required string PhoneNumber { get; init; }
    /// <summary>Null when the phone still has to be confirmed (see OtpRequired).</summary>
    public TokenResponse? Tokens { get; init; }
    public required UserDto User { get; init; }
    /// <summary>True (HTTP 202) when the client must complete POST /auth/verify-otp.</summary>
    public bool OtpRequired { get; init; }
    public string? OtpExpiresAt { get; init; }
    public string? Message { get; init; }
}

/// <summary>POST /auth/verify-otp request body. No member is <c>required</c> (SIM-27, see <see cref="RegisterRequest"/>).</summary>
public sealed record VerifyOtpRequest
{
    public string PhoneNumber { get; init; } = "";
    public string OtpCode { get; init; } = "";
    public string Password { get; init; } = "";
}

/// <summary>POST /auth/resend-otp request body. No member is <c>required</c> (SIM-27).</summary>
public sealed record ResendOtpRequest
{
    public string PhoneNumber { get; init; } = "";
}

/// <summary>POST /auth/resend-otp response (always 200 for a well-formed phone).</summary>
public sealed record ResendOtpResponse
{
    public required string Message { get; init; }
    public string? OtpExpiresAt { get; init; }
}

/// <summary>POST /auth/login request body. No member is <c>required</c> (SIM-27, see <see cref="RegisterRequest"/>).</summary>
public sealed record LoginRequest
{
    public string PhoneNumber { get; init; } = "";
    public string Password { get; init; } = "";
    public string? DeviceFingerprint { get; init; }
}

/// <summary>
/// POST /auth/login success response (200 OK).
/// </summary>
public sealed record LoginResponse
{
    public required TokenResponse Tokens { get; init; }
    public required UserDto User { get; init; }
    /// <summary>True when this sign-in came from an install id not seen on the account before.</summary>
    public bool NewDevice { get; init; }
}

/// <summary>
/// GET /user/profile success response (200 OK, SIM-56, user-entity.md 2.2.0): the caller's own
/// profile, as <see cref="UserProfileDto"/>.
/// </summary>
public sealed record UserProfileResponse
{
    public required UserProfileDto User { get; init; }
}

/// <summary>
/// The profile view GET /user/profile returns (user-entity.md 2.2.0 section 14): what the app's
/// profile and edit screens read, nothing more. Not the sign-in <see cref="UserDto"/>: the Fayda ID
/// (the FaydaIdIndex key), the install ids, the phone and the verification and suspension flags stay
/// off this read, so a leaked ID token learns no more than the profile text.
/// </summary>
public sealed record UserProfileDto
{
    public required string UserId { get; init; }
    public string? DisplayName { get; init; }
    public string? FullName { get; init; }
    public required string Role { get; init; }
    public required string Woreda { get; init; }
    public required string PreferredLanguage { get; init; }
    public string? BuyerType { get; init; }
    public string? CompanyName { get; init; }
    public required int Version { get; init; }

    public static UserProfileDto From(UserDto user) => new()
    {
        UserId = user.UserId,
        DisplayName = user.DisplayName,
        FullName = user.FullName,
        Role = user.Role,
        Woreda = user.Woreda,
        PreferredLanguage = user.PreferredLanguage,
        BuyerType = user.BuyerType,
        CompanyName = user.CompanyName,
        Version = user.Version
    };
}

/// <summary>
/// Token set returned by auth endpoints.
/// </summary>
public sealed record TokenResponse
{
    public required string AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public required string IdToken { get; init; }
    public required int ExpiresIn { get; init; }
    public string TokenType { get; init; } = "Bearer";
}

/// <summary>
/// POST /auth/refresh request body. Not <c>required</c>: a missing token is AUTH_017 (SIM-27).
/// </summary>
public sealed record RefreshRequest
{
    public string RefreshToken { get; init; } = "";
}

/// <summary>
/// POST /auth/refresh success response (200 OK).
/// Note: No new refresh token — Cognito does not rotate refresh tokens.
/// </summary>
public sealed record RefreshResponse
{
    public required TokenResponse Tokens { get; init; }
}

/// <summary>
/// POST /auth/logout request body. Not <c>required</c>: a missing token is AUTH_017 (SIM-27).
/// </summary>
public sealed record LogoutRequest
{
    public string RefreshToken { get; init; } = "";
}

/// <summary>
/// POST /auth/logout success response (200 OK).
/// </summary>
public sealed record LogoutResponse
{
    public required bool LoggedOut { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// Error response body per auth-api.md contract.
/// </summary>
public sealed record AuthErrorResponse
{
    public required AuthError Error { get; init; }
}

/// <summary>
/// Error detail with code, message, and optional field reference.
/// </summary>
public sealed record AuthError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? Field { get; init; }
    public int? RetryAfterSeconds { get; init; }
}

/// <summary>
/// POST /auth/forgot-password request body. Not <c>required</c>: a missing phone is AUTH_001 (SIM-27).
/// </summary>
public sealed record ForgotPasswordRequest
{
    public string PhoneNumber { get; init; } = "";
}

/// <summary>
/// POST /auth/forgot-password success response (200 OK).
/// </summary>
public sealed record ForgotPasswordResponse
{
    public required string Message { get; init; }
    public required string DeliveryMedium { get; init; }
}

/// <summary>
/// POST /auth/reset-password request body. No member is <c>required</c>: a missing field gets
/// its own code (SIM-27, see <see cref="RegisterRequest"/>).
/// </summary>
public sealed record ResetPasswordRequest
{
    public string PhoneNumber { get; init; } = "";
    public string ConfirmationCode { get; init; } = "";
    public string NewPassword { get; init; } = "";
}

/// <summary>
/// POST /auth/reset-password success response (200 OK).
/// </summary>
public sealed record ResetPasswordResponse
{
    public required bool PasswordReset { get; init; }
    public string? Message { get; init; }
}
