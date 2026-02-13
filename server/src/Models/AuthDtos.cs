namespace WeredaConnect.Server.Models;

/// <summary>
/// POST /auth/register request body.
/// </summary>
public sealed record RegisterRequest
{
    public required string PhoneNumber { get; init; }
    public required string Password { get; init; }
    public required string Role { get; init; }
    public required string Woreda { get; init; }
    public string? DisplayName { get; init; }
    public string PreferredLanguage { get; init; } = "am";
}

/// <summary>
/// POST /auth/register success response (201 Created).
/// </summary>
public sealed record RegisterResponse
{
    public required string UserId { get; init; }
    public required string PhoneNumber { get; init; }
    public required bool OtpRequired { get; init; }
    public string? OtpExpiresAt { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// POST /auth/verify-otp request body.
/// </summary>
public sealed record VerifyOtpRequest
{
    public required string UserId { get; init; }
    public required string PhoneNumber { get; init; }
    public required string OtpCode { get; init; }
}

/// <summary>
/// POST /auth/verify-otp success response (200 OK).
/// </summary>
public sealed record VerifyOtpResponse
{
    public required bool Verified { get; init; }
    public TokenResponse? Tokens { get; init; }
    public UserDto? User { get; init; }
}

/// <summary>
/// POST /auth/login request body.
/// </summary>
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
}
