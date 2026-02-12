using Amazon.CognitoIdentityProvider;
using Amazon.DynamoDBv2;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Validation;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// POST /auth/verify-otp Lambda handler.
/// Flow: parse → validate → Cognito confirm sign-up → DynamoDB update verified → 200 response.
/// Returns tokens: null — client must call /auth/login after verification.
/// </summary>
public class VerifyOtpFunction
{
    private readonly ICognitoAuthService _authService;
    private readonly IUserRepository _userRepository;

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// </summary>
    public VerifyOtpFunction()
    {
        var clientId = Environment.GetEnvironmentVariable("COGNITO_CLIENT_ID")
                       ?? throw new InvalidOperationException("COGNITO_CLIENT_ID is not set.");
        var tableName = Environment.GetEnvironmentVariable("USERS_TABLE")
                        ?? throw new InvalidOperationException("USERS_TABLE is not set.");

        var cognito = new AmazonCognitoIdentityProviderClient();
        var dynamoDb = new AmazonDynamoDBClient();

        _authService = new CognitoAuthService(cognito, clientId);
        _userRepository = new UserRepository(dynamoDb, tableName);
    }

    /// <summary>
    /// Constructor for testing.
    /// </summary>
    public VerifyOtpFunction(ICognitoAuthService authService, IUserRepository userRepository)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation("VerifyOtp invoked");

        // Parse
        VerifyOtpRequest? otpRequest;
        try
        {
            otpRequest = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.VerifyOtpRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "AUTH_007", "Invalid JSON in request body.");
        }

        // Validate
        var validation = RequestValidator.ValidateVerifyOtpRequest(otpRequest);
        if (!validation.IsValid)
            return LambdaResponse.ValidationError(validation);

        // Cognito confirm sign-up
        var confirmResult = await _authService.ConfirmSignUpAsync(
            otpRequest!.PhoneNumber, otpRequest.OtpCode);

        if (!confirmResult.IsSuccess)
        {
            var error = confirmResult.Error!;
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        // Update user verified status in DynamoDB (best effort)
        UserDto? user = null;
        try
        {
            user = await _userRepository.GetUserByIdAsync(otpRequest.UserId);
            if (user != null)
            {
                var updatedUser = user with { IsPhoneVerified = true };
                var updateResult = await _userRepository.UpdateUserAsync(updatedUser, user.Version);
                if (updateResult.IsSuccess)
                    user = updateResult.Value;
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Failed to update verified status for {otpRequest.UserId}: {ex.Message}");
        }

        var response = new VerifyOtpResponse
        {
            Verified = true,
            Tokens = null, // Client must call /auth/login after verification
            User = user
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.VerifyOtpResponse);
    }
}
