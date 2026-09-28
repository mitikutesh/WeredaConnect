using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.Runtime;

namespace WeredaConnect.Server.Services;

/// <summary>
/// Cognito auth service implementation wrapping IAmazonCognitoIdentityProvider.
/// Maps Cognito exceptions to AUTH_* error codes per auth-api contract.
/// AOT-compatible — no reflection used.
/// </summary>
public sealed class CognitoAuthService : ICognitoAuthService
{
    private readonly IAmazonCognitoIdentityProvider _cognito;
    private readonly string _clientId;
    private readonly string? _userPoolId;

    public CognitoAuthService(IAmazonCognitoIdentityProvider cognito, string clientId, string? userPoolId = null)
    {
        _cognito = cognito ?? throw new ArgumentNullException(nameof(cognito));
        _clientId = clientId ?? throw new ArgumentNullException(nameof(clientId));
        _userPoolId = userPoolId;
    }

    public async Task<AuthResult<string>> RegisterAsync(
        string phone,
        string password,
        string role,
        string woreda,
        string? displayName,
        string language)
    {
        // SEC-VULN-001: custom:role is excluded from client writeAttributes to prevent
        // privilege escalation. Only client-writable attributes are included in SignUp.
        // Non-writable attributes (custom:role) are set via AdminUpdateUserAttributes.
        var request = new SignUpRequest
        {
            ClientId = _clientId,
            Username = phone,
            Password = password,
            UserAttributes =
            [
                new AttributeType { Name = "phone_number", Value = phone },
                new AttributeType { Name = "custom:woreda", Value = woreda },
                new AttributeType { Name = "custom:preferredLanguage", Value = language },
                new AttributeType { Name = "custom:displayName", Value = displayName ?? "" }
            ]
        };

        try
        {
            var response = await _cognito.SignUpAsync(request);

            // Set custom:role via admin API (bypasses client writeAttributes restriction)
            if (_userPoolId != null)
            {
                await _cognito.AdminUpdateUserAttributesAsync(new AdminUpdateUserAttributesRequest
                {
                    UserPoolId = _userPoolId,
                    Username = phone,
                    UserAttributes =
                    [
                        new AttributeType { Name = "custom:role", Value = role }
                    ]
                });
            }

            return AuthResult<string>.Success(response.UserSub);
        }
        catch (UsernameExistsException)
        {
            // If a previous registration left the user UNCONFIRMED (e.g. client
            // crashed before admin-confirm), delete the stale signup and retry.
            if (_userPoolId != null)
            {
                try
                {
                    var existing = await _cognito.AdminGetUserAsync(new AdminGetUserRequest
                    {
                        UserPoolId = _userPoolId,
                        Username = phone
                    });

                    if (existing.UserStatus == UserStatusType.UNCONFIRMED)
                    {
                        await _cognito.AdminDeleteUserAsync(new AdminDeleteUserRequest
                        {
                            UserPoolId = _userPoolId,
                            Username = phone
                        });

                        // Retry signup after deleting the stale user
                        var retryResponse = await _cognito.SignUpAsync(request);
                        if (_userPoolId != null)
                        {
                            await _cognito.AdminUpdateUserAttributesAsync(new AdminUpdateUserAttributesRequest
                            {
                                UserPoolId = _userPoolId,
                                Username = phone,
                                UserAttributes =
                                [
                                    new AttributeType { Name = "custom:role", Value = role }
                                ]
                            });
                        }
                        return AuthResult<string>.Success(retryResponse.UserSub);
                    }
                }
                catch (UserNotFoundException)
                {
                    // Race condition — user was deleted between checks; fall through
                }
                catch (Exception)
                {
                    // If cleanup fails, return the original "already exists" error
                }
            }

            return AuthResult<string>.Failure(409, "AUTH_005",
                "A user with this phone number already exists.", "phoneNumber");
        }
        catch (InvalidPasswordException)
        {
            return AuthResult<string>.Failure(400, "AUTH_002",
                "Password must be at least 8 characters with a lowercase letter and a number.", "password");
        }
        catch (InvalidParameterException)
        {
            return AuthResult<string>.Failure(400, "AUTH_002",
                "Password must be at least 8 characters with a lowercase letter and a number.", "password");
        }
        catch (TooManyRequestsException)
        {
            return AuthResult<string>.Failure(429, "AUTH_006",
                "Too many requests. Please try again later.");
        }
        catch (LimitExceededException)
        {
            return AuthResult<string>.Failure(429, "AUTH_006",
                "Too many requests. Please try again later.");
        }
        catch (InternalErrorException)
        {
            return AuthResult<string>.Failure(503, "AUTH_008",
                "Authentication service temporarily unavailable.");
        }
        catch (AmazonServiceException)
        {
            return AuthResult<string>.Failure(503, "AUTH_008",
                "Authentication service temporarily unavailable.");
        }
    }

    public async Task<AuthResult<bool>> ConfirmSignUpAsync(string phone, string otpCode)
    {
        var devBypass = Environment.GetEnvironmentVariable("DEV_OTP_BYPASS");
        if (devBypass == "true" && otpCode == "123456")
        {
            // Admin-confirm the user in Cognito so login works
            if (_userPoolId != null)
            {
                await _cognito.AdminConfirmSignUpAsync(new AdminConfirmSignUpRequest
                {
                    UserPoolId = _userPoolId,
                    Username = phone
                });

                // Mark phone as verified so the phone sign-in alias is created.
                // Without this, Cognito cannot find the user by phone number
                // when signInAliases includes phone.
                await _cognito.AdminUpdateUserAttributesAsync(new AdminUpdateUserAttributesRequest
                {
                    UserPoolId = _userPoolId,
                    Username = phone,
                    UserAttributes =
                    [
                        new AttributeType { Name = "phone_number_verified", Value = "true" }
                    ]
                });
            }
            return AuthResult<bool>.Success(true);
        }

        var request = new ConfirmSignUpRequest
        {
            ClientId = _clientId,
            Username = phone,
            ConfirmationCode = otpCode
        };

        try
        {
            await _cognito.ConfirmSignUpAsync(request);
            return AuthResult<bool>.Success(true);
        }
        catch (CodeMismatchException)
        {
            return AuthResult<bool>.Failure(400, "AUTH_011",
                "Invalid OTP code.", "otpCode");
        }
        catch (ExpiredCodeException)
        {
            return AuthResult<bool>.Failure(400, "AUTH_010",
                "OTP code has expired. Please request a new one.", "otpCode");
        }
        catch (UserNotFoundException)
        {
            return AuthResult<bool>.Failure(404, "AUTH_013",
                "User not found.", "userId");
        }
        catch (TooManyRequestsException)
        {
            return AuthResult<bool>.Failure(429, "AUTH_006",
                "Too many requests. Please try again later.");
        }
        catch (LimitExceededException)
        {
            return AuthResult<bool>.Failure(429, "AUTH_006",
                "Too many requests. Please try again later.");
        }
        catch (InternalErrorException)
        {
            return AuthResult<bool>.Failure(503, "AUTH_008",
                "Authentication service temporarily unavailable.");
        }
        catch (AmazonServiceException)
        {
            return AuthResult<bool>.Failure(503, "AUTH_008",
                "Authentication service temporarily unavailable.");
        }
    }

    public async Task<AuthResult<bool>> AdminConfirmSignUpAsync(string phone)
    {
        if (_userPoolId == null)
        {
            return AuthResult<bool>.Failure(500, "AUTH_008",
                "User pool ID not configured for admin operations.");
        }

        try
        {
            // Admin-confirm the user
            await _cognito.AdminConfirmSignUpAsync(new AdminConfirmSignUpRequest
            {
                UserPoolId = _userPoolId,
                Username = phone
            });

            // Mark phone as verified so the phone sign-in alias is created
            await _cognito.AdminUpdateUserAttributesAsync(new AdminUpdateUserAttributesRequest
            {
                UserPoolId = _userPoolId,
                Username = phone,
                UserAttributes =
                [
                    new AttributeType { Name = "phone_number_verified", Value = "true" }
                ]
            });

            return AuthResult<bool>.Success(true);
        }
        catch (NotAuthorizedException ex) when (ex.Message.Contains("CONFIRMED", StringComparison.OrdinalIgnoreCase))
        {
            // Already confirmed (a retried verify-otp): the outcome we wanted.
            return AuthResult<bool>.Success(true);
        }
        catch (UserNotFoundException)
        {
            return AuthResult<bool>.Failure(404, "AUTH_013",
                "User not found.", "phoneNumber");
        }
        catch (TooManyRequestsException)
        {
            return AuthResult<bool>.Failure(429, "AUTH_006",
                "Too many requests. Please try again later.");
        }
        catch (LimitExceededException)
        {
            return AuthResult<bool>.Failure(429, "AUTH_006",
                "Too many requests. Please try again later.");
        }
        catch (InternalErrorException)
        {
            return AuthResult<bool>.Failure(503, "AUTH_008",
                "Authentication service temporarily unavailable.");
        }
        catch (AmazonServiceException)
        {
            return AuthResult<bool>.Failure(503, "AUTH_008",
                "Authentication service temporarily unavailable.");
        }
    }

    public async Task<AuthResult<CognitoAuthTokens>> LoginAsync(string phone, string password)
    {
        if (_userPoolId == null)
        {
            return AuthResult<CognitoAuthTokens>.Failure(500, "AUTH_008",
                "User pool ID not configured for password login.");
        }

        // ADMIN_USER_PASSWORD_AUTH: the public app client has USER_PASSWORD_AUTH
        // disabled, so a password can only be checked through this server (IAM
        // signed), never by calling Cognito directly and skipping the lockout,
        // suspension and audit rules.
        var request = new AdminInitiateAuthRequest
        {
            UserPoolId = _userPoolId,
            ClientId = _clientId,
            AuthFlow = AuthFlowType.ADMIN_USER_PASSWORD_AUTH,
            AuthParameters = new Dictionary<string, string>
            {
                { "USERNAME", phone },
                { "PASSWORD", password }
            }
        };

        try
        {
            var response = await _cognito.AdminInitiateAuthAsync(request);
            var result = response.AuthenticationResult;

            // AuthenticationResult is null when Cognito returns a challenge
            // (e.g. NEW_PASSWORD_REQUIRED / MFA) instead of tokens. This flow does
            // not drive challenges, so fail closed rather than NRE on result.AccessToken.
            if (result == null)
            {
                return AuthResult<CognitoAuthTokens>.Failure(401, "AUTH_014",
                    "Invalid phone number or password.");
            }

            return AuthResult<CognitoAuthTokens>.Success(new CognitoAuthTokens
            {
                AccessToken = result.AccessToken,
                IdToken = result.IdToken,
                RefreshToken = result.RefreshToken,
                ExpiresIn = result.ExpiresIn
            });
        }
        catch (NotAuthorizedException)
        {
            return AuthResult<CognitoAuthTokens>.Failure(401, "AUTH_014",
                "Invalid phone number or password.");
        }
        catch (UserNotConfirmedException)
        {
            return AuthResult<CognitoAuthTokens>.Failure(403, "AUTH_016",
                "Phone number not verified. Please complete OTP verification.");
        }
        catch (UserNotFoundException)
        {
            // Return same error as NotAuthorizedException to prevent user enumeration
            return AuthResult<CognitoAuthTokens>.Failure(401, "AUTH_014",
                "Invalid phone number or password.");
        }
        catch (TooManyRequestsException)
        {
            return AuthResult<CognitoAuthTokens>.Failure(429, "AUTH_006",
                "Too many requests. Please try again later.");
        }
        catch (LimitExceededException)
        {
            return AuthResult<CognitoAuthTokens>.Failure(429, "AUTH_006",
                "Too many requests. Please try again later.");
        }
        catch (InternalErrorException)
        {
            return AuthResult<CognitoAuthTokens>.Failure(503, "AUTH_008",
                "Authentication service temporarily unavailable.");
        }
        catch (AmazonServiceException)
        {
            return AuthResult<CognitoAuthTokens>.Failure(503, "AUTH_008",
                "Authentication service temporarily unavailable.");
        }
    }

    public async Task<AuthResult<CognitoAuthTokens>> RefreshAsync(string refreshToken)
    {
        var request = new InitiateAuthRequest
        {
            ClientId = _clientId,
            AuthFlow = AuthFlowType.REFRESH_TOKEN_AUTH,
            AuthParameters = new Dictionary<string, string>
            {
                { "REFRESH_TOKEN", refreshToken }
            }
        };

        try
        {
            var response = await _cognito.InitiateAuthAsync(request);
            var result = response.AuthenticationResult;

            // Guard against a null AuthenticationResult (challenge/unexpected) before
            // dereferencing — otherwise this NREs to a 500 instead of a clean 401.
            if (result == null)
            {
                return AuthResult<CognitoAuthTokens>.Failure(401, "AUTH_018",
                    "Refresh token is invalid or expired.", "refreshToken");
            }

            return AuthResult<CognitoAuthTokens>.Success(new CognitoAuthTokens
            {
                AccessToken = result.AccessToken,
                IdToken = result.IdToken,
                RefreshToken = null, // Cognito does not rotate refresh tokens
                ExpiresIn = result.ExpiresIn
            });
        }
        catch (NotAuthorizedException)
        {
            return AuthResult<CognitoAuthTokens>.Failure(401, "AUTH_018",
                "Refresh token is invalid or expired.", "refreshToken");
        }
        catch (TooManyRequestsException)
        {
            return AuthResult<CognitoAuthTokens>.Failure(429, "AUTH_006",
                "Too many requests. Please try again later.");
        }
        catch (LimitExceededException)
        {
            return AuthResult<CognitoAuthTokens>.Failure(429, "AUTH_006",
                "Too many requests. Please try again later.");
        }
        catch (InternalErrorException)
        {
            return AuthResult<CognitoAuthTokens>.Failure(503, "AUTH_008",
                "Authentication service temporarily unavailable.");
        }
        catch (AmazonServiceException)
        {
            return AuthResult<CognitoAuthTokens>.Failure(503, "AUTH_008",
                "Authentication service temporarily unavailable.");
        }
    }

    public async Task<AuthResult<bool>> LogoutAsync(string refreshToken)
    {
        var request = new RevokeTokenRequest
        {
            ClientId = _clientId,
            Token = refreshToken
        };

        try
        {
            await _cognito.RevokeTokenAsync(request);
            return AuthResult<bool>.Success(true);
        }
        catch (NotAuthorizedException)
        {
            // Token already revoked or invalid — treat as success
            return AuthResult<bool>.Success(true);
        }
        catch (TooManyRequestsException)
        {
            return AuthResult<bool>.Failure(429, "AUTH_006",
                "Too many requests. Please try again later.");
        }
        catch (LimitExceededException)
        {
            return AuthResult<bool>.Failure(429, "AUTH_006",
                "Too many requests. Please try again later.");
        }
        catch (InternalErrorException)
        {
            return AuthResult<bool>.Failure(503, "AUTH_008",
                "Authentication service temporarily unavailable.");
        }
        catch (AmazonServiceException)
        {
            return AuthResult<bool>.Failure(503, "AUTH_008",
                "Authentication service temporarily unavailable.");
        }
    }

    public async Task<AuthResult<string>> ForgotPasswordAsync(string phone)
    {
        // Use fully-qualified name to avoid conflict with Models.ForgotPasswordRequest
        var request = new Amazon.CognitoIdentityProvider.Model.ForgotPasswordRequest
        {
            ClientId = _clientId,
            Username = phone
        };

        try
        {
            var response = await _cognito.ForgotPasswordAsync(request);
            var deliveryMedium = response.CodeDeliveryDetails?.DeliveryMedium?.Value ?? "SMS";
            return AuthResult<string>.Success(deliveryMedium);
        }
        catch (UserNotFoundException)
        {
            return AuthResult<string>.Failure(404, "AUTH_013",
                "User not found.", "phoneNumber");
        }
        catch (TooManyRequestsException)
        {
            return AuthResult<string>.Failure(429, "AUTH_006",
                "Too many requests. Please try again later.");
        }
        catch (LimitExceededException)
        {
            return AuthResult<string>.Failure(429, "AUTH_006",
                "Too many requests. Please try again later.");
        }
        catch (InvalidParameterException)
        {
            return AuthResult<string>.Failure(400, "AUTH_007",
                "Invalid parameter.", "phoneNumber");
        }
        catch (InternalErrorException)
        {
            return AuthResult<string>.Failure(503, "AUTH_008",
                "Authentication service temporarily unavailable.");
        }
        catch (AmazonServiceException)
        {
            return AuthResult<string>.Failure(503, "AUTH_008",
                "Authentication service temporarily unavailable.");
        }
    }

    public async Task<AuthResult<bool>> ConfirmForgotPasswordAsync(
        string phone,
        string confirmationCode,
        string newPassword)
    {
        // Use fully-qualified name to avoid conflict with Models.ResetPasswordRequest
        var request = new Amazon.CognitoIdentityProvider.Model.ConfirmForgotPasswordRequest
        {
            ClientId = _clientId,
            Username = phone,
            ConfirmationCode = confirmationCode,
            Password = newPassword
        };

        try
        {
            await _cognito.ConfirmForgotPasswordAsync(request);
            return AuthResult<bool>.Success(true);
        }
        catch (CodeMismatchException)
        {
            return AuthResult<bool>.Failure(400, "AUTH_022",
                "Invalid or expired verification code.", "confirmationCode");
        }
        catch (ExpiredCodeException)
        {
            return AuthResult<bool>.Failure(400, "AUTH_023",
                "Verification code expired.", "confirmationCode");
        }
        catch (InvalidPasswordException)
        {
            return AuthResult<bool>.Failure(400, "AUTH_024",
                "Password does not meet requirements.", "newPassword");
        }
        catch (TooManyRequestsException)
        {
            return AuthResult<bool>.Failure(429, "AUTH_006",
                "Too many requests. Please try again later.");
        }
        catch (UserNotFoundException)
        {
            return AuthResult<bool>.Failure(404, "AUTH_013",
                "User not found.", "phoneNumber");
        }
        catch (LimitExceededException)
        {
            return AuthResult<bool>.Failure(429, "AUTH_006",
                "Too many requests. Please try again later.");
        }
        catch (InternalErrorException)
        {
            return AuthResult<bool>.Failure(503, "AUTH_008",
                "Authentication service temporarily unavailable.");
        }
        catch (AmazonServiceException)
        {
            return AuthResult<bool>.Failure(503, "AUTH_008",
                "Authentication service temporarily unavailable.");
        }
    }

    /// <inheritdoc />
    public async Task DeleteUserAsync(string phone)
    {
        if (_userPoolId == null)
        {
            return;
        }

        try
        {
            await _cognito.AdminDeleteUserAsync(new AdminDeleteUserRequest
            {
                UserPoolId = _userPoolId,
                Username = phone
            });
        }
        catch (UserNotFoundException)
        {
            // Already gone — nothing to roll back.
        }
    }

    public async Task<bool> IsUnconfirmedOrMissingAsync(string phone)
    {
        if (_userPoolId == null)
        {
            return false;
        }

        try
        {
            var user = await _cognito.AdminGetUserAsync(new AdminGetUserRequest
            {
                UserPoolId = _userPoolId,
                Username = phone
            });
            return user.UserStatus == UserStatusType.UNCONFIRMED;
        }
        catch (UserNotFoundException)
        {
            return true;
        }
        catch (AmazonServiceException)
        {
            return false;
        }
    }

    public Task<AuthResult<bool>> AdminDisableUserAsync(string phone)
        => AdminCallAsync(() => _cognito.AdminDisableUserAsync(new AdminDisableUserRequest
        {
            UserPoolId = _userPoolId,
            Username = phone
        }));

    public Task<AuthResult<bool>> AdminEnableUserAsync(string phone)
        => AdminCallAsync(() => _cognito.AdminEnableUserAsync(new AdminEnableUserRequest
        {
            UserPoolId = _userPoolId,
            Username = phone
        }));

    public Task<AuthResult<bool>> AdminGlobalSignOutAsync(string phone)
        => AdminCallAsync(() => _cognito.AdminUserGlobalSignOutAsync(new AdminUserGlobalSignOutRequest
        {
            UserPoolId = _userPoolId,
            Username = phone
        }));

    public Task<AuthResult<bool>> AdminSetRoleAsync(string phone, string role)
        => AdminCallAsync(() => _cognito.AdminUpdateUserAttributesAsync(new AdminUpdateUserAttributesRequest
        {
            UserPoolId = _userPoolId,
            Username = phone,
            UserAttributes =
            [
                new AttributeType { Name = "custom:role", Value = role }
            ]
        }));

    /// <summary>Shared error mapping for the admin account-control calls.</summary>
    private async Task<AuthResult<bool>> AdminCallAsync(Func<Task> call)
    {
        if (_userPoolId == null)
        {
            return AuthResult<bool>.Failure(500, "AUTH_008",
                "User pool ID not configured for admin operations.");
        }

        try
        {
            await call();
            return AuthResult<bool>.Success(true);
        }
        catch (UserNotFoundException)
        {
            return AuthResult<bool>.Failure(404, "AUTH_013", "User not found.", "phoneNumber");
        }
        catch (TooManyRequestsException)
        {
            return AuthResult<bool>.Failure(429, "AUTH_006", "Too many requests. Please try again later.");
        }
        catch (LimitExceededException)
        {
            return AuthResult<bool>.Failure(429, "AUTH_006", "Too many requests. Please try again later.");
        }
        catch (AmazonServiceException)
        {
            return AuthResult<bool>.Failure(503, "AUTH_008", "Authentication service temporarily unavailable.");
        }
    }
}
