namespace WeredaConnect.Server.Models;

/// <summary>
/// POST /auth/register request body.
/// FaydaVerificationToken is optional — two-tier model:
/// - Without Fayda: user is phone-verified, can trade up to 10,000 ETB
/// - With Fayda: user is identity-verified, can trade unlimited
/// </summary>
public sealed record RegisterRequest
{
    public required string PhoneNumber { get; init; }
    public required string Password { get; init; }
    public required string Role { get; init; }
    public required string Woreda { get; init; }
    public string? FaydaVerificationToken { get; init; }
    public string? DisplayName { get; init; }
    public string PreferredLanguage { get; init; } = "am";

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

/// <summary>
/// POST /auth/login request body.
/// </summary>
/// <summary>POST /auth/verify-otp request body.</summary>
public sealed record VerifyOtpRequest
{
    public required string PhoneNumber { get; init; }
    public required string OtpCode { get; init; }
    public required string Password { get; init; }
}

/// <summary>POST /auth/resend-otp request body.</summary>
public sealed record ResendOtpRequest
{
    public required string PhoneNumber { get; init; }
}

/// <summary>POST /auth/resend-otp response (always 200 for a well-formed phone).</summary>
public sealed record ResendOtpResponse
{
    public required string Message { get; init; }
    public string? OtpExpiresAt { get; init; }
}

public sealed record LoginRequest
{
    public required string PhoneNumber { get; init; }
    public required string Password { get; init; }
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
/// POST /auth/refresh request body.
/// </summary>
public sealed record RefreshRequest
{
    public required string RefreshToken { get; init; }
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
/// POST /auth/logout request body.
/// </summary>
public sealed record LogoutRequest
{
    public required string RefreshToken { get; init; }
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
/// POST /auth/forgot-password request body.
/// </summary>
public sealed record ForgotPasswordRequest
{
    public required string PhoneNumber { get; init; }
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
/// POST /auth/reset-password request body.
/// </summary>
public sealed record ResetPasswordRequest
{
    public required string PhoneNumber { get; init; }
    public required string ConfirmationCode { get; init; }
    public required string NewPassword { get; init; }
}

/// <summary>
/// POST /auth/reset-password success response (200 OK).
/// </summary>
public sealed record ResetPasswordResponse
{
    public required bool PasswordReset { get; init; }
    public string? Message { get; init; }
}
