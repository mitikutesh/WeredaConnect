using Amazon.DynamoDBv2;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Validation;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// POST /auth/fayda-verify Lambda handler.
/// Flow: parse → validate → exchange code with Fayda OIDC → check duplicate Fayda ID → return verification token.
/// Dev bypass: DEV_FAYDA_BYPASS=true accepts test-code-123 and returns a mock token.
/// </summary>
public class FaydaVerifyFunction
{
    private readonly IFaydaOidcService _faydaService;
    private readonly IUserRepository _userRepository;

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// Resolves SSM parameter names and Secrets Manager ARNs to actual values.
    /// </summary>
    public FaydaVerifyFunction()
    {
        var clientIdParam = Environment.GetEnvironmentVariable("FAYDA_CLIENT_ID_PARAM")
                            ?? throw new InvalidOperationException("FAYDA_CLIENT_ID_PARAM is not set.");
        var issuerUrlParam = Environment.GetEnvironmentVariable("FAYDA_ISSUER_URL_PARAM")
                             ?? throw new InvalidOperationException("FAYDA_ISSUER_URL_PARAM is not set.");
        var clientSecretArn = Environment.GetEnvironmentVariable("FAYDA_CLIENT_SECRET_ARN")
                              ?? throw new InvalidOperationException("FAYDA_CLIENT_SECRET_ARN is not set.");
        var signingSecretArn = Environment.GetEnvironmentVariable("FAYDA_SIGNING_SECRET_ARN")
                               ?? throw new InvalidOperationException("FAYDA_SIGNING_SECRET_ARN is not set.");
        var tableName = Environment.GetEnvironmentVariable("USERS_TABLE")
                        ?? throw new InvalidOperationException("USERS_TABLE is not set.");

        // Resolve SSM params and secrets to actual values
        var clientId = Helpers.AwsParameterResolver.GetSsmParameterValue(clientIdParam);
        var issuerUrl = Helpers.AwsParameterResolver.GetSsmParameterValue(issuerUrlParam);
        var clientSecret = Helpers.AwsParameterResolver.GetSecretValue(clientSecretArn);
        var signingSecret = Helpers.AwsParameterResolver.GetSecretValue(signingSecretArn);

        var dynamoDb = new AmazonDynamoDBClient();

        _faydaService = new FaydaOidcService(
            new HttpClient(), clientId, clientSecret, issuerUrl, signingSecret);
        _userRepository = new UserRepository(dynamoDb, tableName);
    }

    /// <summary>
    /// Constructor for testing.
    /// </summary>
    public FaydaVerifyFunction(IFaydaOidcService faydaService, IUserRepository userRepository)
    {
        _faydaService = faydaService ?? throw new ArgumentNullException(nameof(faydaService));
        _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation("FaydaVerify invoked");

        // Parse
        FaydaVerifyRequest? faydaRequest;
        try
        {
            faydaRequest = JsonSerializer.Deserialize(
                request.Body ?? "",
                AppJsonContext.Default.FaydaVerifyRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "AUTH_007", "Invalid JSON in request body.");
        }

        // Validate
        var validation = RequestValidator.ValidateFaydaVerifyRequest(faydaRequest);
        if (!validation.IsValid)
        {
            return LambdaResponse.ValidationError(validation);
        }

        // Dev bypass for testing
        var devBypass = Environment.GetEnvironmentVariable("DEV_FAYDA_BYPASS");
        if (devBypass == "true" && faydaRequest!.AuthorizationCode == "test-code-123")
        {
            var devFaydaId = "FAYDA-DEV-001";
            var devName = "Test User";
            var devToken = _faydaService.GenerateVerificationToken(devFaydaId, devName);
            var devExpiresAt = DateTime.UtcNow.AddMinutes(30).ToString("o");

            var devResponse = new FaydaVerifyResponse
            {
                Verified = true,
                FaydaId = devFaydaId,
                VerifiedName = devName,
                VerificationToken = devToken,
                ExpiresAt = devExpiresAt
            };

            return LambdaResponse.Ok(devResponse, AppJsonContext.Default.FaydaVerifyResponse);
        }

        // Exchange code with Fayda OIDC
        var exchangeResult = await _faydaService.ExchangeCodeAsync(
            faydaRequest!.AuthorizationCode, faydaRequest.RedirectUri);

        if (!exchangeResult.IsSuccess)
        {
            var error = exchangeResult.Error!;
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        var verification = exchangeResult.Value!;

        // Check if Fayda ID is already linked to another user
        var existingUser = await _userRepository.GetUserByFaydaIdAsync(verification.FaydaId);
        if (existingUser != null)
        {
            return LambdaResponse.Error(409, "FAYDA_004",
                "This Fayda ID is already linked to another account.", "authorizationCode");
        }

        // Generate verification token
        var verificationToken = _faydaService.GenerateVerificationToken(
            verification.FaydaId, verification.FullName);
        var expiresAt = DateTime.UtcNow.AddMinutes(30).ToString("o");

        var response = new FaydaVerifyResponse
        {
            Verified = true,
            FaydaId = verification.FaydaId,
            VerifiedName = verification.FullName,
            VerificationToken = verificationToken,
            ExpiresAt = expiresAt
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.FaydaVerifyResponse);
    }
}
