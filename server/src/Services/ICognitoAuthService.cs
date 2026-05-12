namespace WeredaConnect.Server.Services;

/// <summary>
/// Tokens returned from Cognito authentication operations.
/// Decoupled from the API contract TokenResponse DTO — handlers map between them.
/// </summary>
public sealed record CognitoAuthTokens
{
    public required string AccessToken { get; init; }
    public required string IdToken { get; init; }
    public string? RefreshToken { get; init; }
    public required int ExpiresIn { get; init; }
}

/// <summary>
/// Interface for Cognito authentication operations.
/// All methods return AuthResult to handle expected failures without exceptions.
/// </summary>
public interface ICognitoAuthService
{
    /// <summary>
    /// Register a new user in Cognito with custom attributes.
    /// Returns the Cognito user ID (sub) on success.
    /// </summary>
    Task<AuthResult<string>> RegisterAsync(
        string phone,
        string password,
        string role,
        string woreda,
        string? displayName,
        string language);

    /// <summary>
    /// Confirm sign-up with OTP code sent via SMS.
    /// </summary>
    Task<AuthResult<bool>> ConfirmSignUpAsync(string phone, string otpCode);

    /// <summary>
    /// Admin-confirm a user signup and mark phone as verified.
    /// Used for Fayda-verified registrations that skip OTP.
    /// </summary>
    Task<AuthResult<bool>> AdminConfirmSignUpAsync(string phone);

    /// <summary>
    /// Authenticate user with phone + password. Returns tokens on success.
    /// </summary>
    Task<AuthResult<CognitoAuthTokens>> LoginAsync(string phone, string password);

    /// <summary>
    /// Refresh access/ID tokens using a refresh token.
    /// Cognito does not rotate refresh tokens, so only access + ID are returned.
    /// </summary>
    Task<AuthResult<CognitoAuthTokens>> RefreshAsync(string refreshToken);

    /// <summary>
    /// Revoke a refresh token (global sign-out for that session).
    /// </summary>
    Task<AuthResult<bool>> LogoutAsync(string refreshToken);

    /// <summary>
    /// Initiate forgot-password flow — sends a verification code via SMS.
    /// Returns the delivery medium (e.g. "SMS") on success.
    /// </summary>
    Task<AuthResult<string>> ForgotPasswordAsync(string phone);

    /// <summary>
    /// Confirm forgot-password with verification code and new password.
    /// Returns true on success.
    /// </summary>
    Task<AuthResult<bool>> ConfirmForgotPasswordAsync(string phone, string confirmationCode, string newPassword);
}
