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

    public async Task<AuthResult<CognitoAuthTokens>> LoginAsync(string phone, string password)
    {
        var request = new InitiateAuthRequest
        {
            ClientId = _clientId,
            AuthFlow = AuthFlowType.USER_PASSWORD_AUTH,
            AuthParameters = new Dictionary<string, string>
            {
                { "USERNAME", phone },
                { "PASSWORD", password }
            }
        };

        try
        {
            var response = await _cognito.InitiateAuthAsync(request);
            var result = response.AuthenticationResult;

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
}
