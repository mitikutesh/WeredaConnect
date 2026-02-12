using Amazon.CognitoIdentityProvider;
using Amazon.DynamoDBv2;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Validation;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// POST /auth/login Lambda handler.
/// Flow: parse → validate → Cognito login → DynamoDB get user → 200 response with tokens + user.
/// </summary>
public class LoginFunction
{
    private readonly ICognitoAuthService _authService;
    private readonly IUserRepository _userRepository;

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// </summary>
    public LoginFunction()
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
    public LoginFunction(ICognitoAuthService authService, IUserRepository userRepository)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation("Login invoked");

        // Parse
        LoginRequest? loginRequest;
        try
        {
            loginRequest = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.LoginRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "AUTH_007", "Invalid JSON in request body.");
        }

        // Validate
        var validation = RequestValidator.ValidateLoginRequest(loginRequest);
        if (!validation.IsValid)
            return LambdaResponse.ValidationError(validation);

        // Cognito login
        var loginResult = await _authService.LoginAsync(
            loginRequest!.PhoneNumber, loginRequest.Password);

        if (!loginResult.IsSuccess)
        {
            var error = loginResult.Error!;
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        var tokens = loginResult.Value!;

        // Get user from DynamoDB
        UserDto? user;
        try
        {
            user = await _userRepository.GetUserByPhoneAsync(loginRequest.PhoneNumber);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Failed to fetch user by phone {loginRequest.PhoneNumber}: {ex.Message}");
            user = null;
        }

        if (user == null)
        {
            return LambdaResponse.Error(404, "AUTH_013",
                "User profile not found. Please contact support.");
        }

        var response = new LoginResponse
        {
            Tokens = new TokenResponse
            {
                AccessToken = tokens.AccessToken,
                IdToken = tokens.IdToken,
                RefreshToken = tokens.RefreshToken,
                ExpiresIn = tokens.ExpiresIn
            },
            User = user
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.LoginResponse);
    }
}
