using Amazon.CognitoIdentityProvider;
using Amazon.DynamoDBv2;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Validation;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// POST /auth/register Lambda handler.
/// Flow: parse → validate → Cognito register → DynamoDB create user → 201 response.
/// Cognito is auth source of truth. If DB write fails, log warning and return success.
/// </summary>
public class RegisterFunction
{
    private readonly ICognitoAuthService _authService;
    private readonly IUserRepository _userRepository;

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// </summary>
    public RegisterFunction()
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
    public RegisterFunction(ICognitoAuthService authService, IUserRepository userRepository)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation("Register invoked");

        // Parse
        RegisterRequest? registerRequest;
        try
        {
            registerRequest = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.RegisterRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "AUTH_007", "Invalid JSON in request body.");
        }

        // Validate
        var validation = RequestValidator.ValidateRegisterRequest(registerRequest);
        if (!validation.IsValid)
            return LambdaResponse.ValidationError(validation);

        // Cognito register
        var cognitoResult = await _authService.RegisterAsync(
            registerRequest!.PhoneNumber,
            registerRequest.Password,
            registerRequest.Role,
            registerRequest.Woreda,
            registerRequest.DisplayName,
            registerRequest.PreferredLanguage);

        if (!cognitoResult.IsSuccess)
        {
            var error = cognitoResult.Error!;
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        var userId = cognitoResult.Value!;
        var now = DateTime.UtcNow.ToString("o");

        // DynamoDB create user (best effort — Cognito is source of truth)
        var userDto = new UserDto
        {
            UserId = userId,
            PhoneNumber = registerRequest.PhoneNumber,
            DisplayName = registerRequest.DisplayName,
            Role = registerRequest.Role,
            Woreda = registerRequest.Woreda,
            PreferredLanguage = registerRequest.PreferredLanguage,
            IsPhoneVerified = false,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1
        };

        try
        {
            await _userRepository.CreateUserAsync(userDto);
        }
        catch (Exception ex)
        {
            // Log but don't fail — Cognito registration succeeded
            context.Logger.LogWarning(
                $"Failed to create DynamoDB user record for {userId}: {ex.Message}");
        }

        var response = new RegisterResponse
        {
            UserId = userId,
            PhoneNumber = registerRequest.PhoneNumber,
            OtpRequired = true,
            Message = "Registration successful. Please verify your phone number with the OTP sent via SMS."
        };

        return LambdaResponse.Created(response, AppJsonContext.Default.RegisterResponse);
    }
}
