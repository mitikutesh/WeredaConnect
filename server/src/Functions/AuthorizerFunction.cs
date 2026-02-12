using WeredaConnect.Server.Auth;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// JSON source generator context for Authorizer function types.
/// </summary>
[JsonSerializable(typeof(AuthErrorResponse))]
[JsonSerializable(typeof(AuthError))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public partial class AuthorizerJsonContext : JsonSerializerContext
{
}

/// <summary>
/// API Gateway Lambda authorizer function.
/// Extracts Bearer token from Authorization header, validates it via IJwtValidator,
/// and returns an IAM policy document (Allow/Deny) to API Gateway.
///
/// Environment variables:
///   COGNITO_USER_POOL_ID — Cognito User Pool ID
///   COGNITO_REGION — AWS region for Cognito
///   COGNITO_CLIENT_ID — Cognito App Client ID
/// </summary>
public class AuthorizerFunction
{
    private readonly IJwtValidator _jwtValidator;

    /// <summary>
    /// Default constructor for Lambda runtime. Reads configuration from environment variables.
    /// </summary>
    public AuthorizerFunction()
    {
        var userPoolId = Environment.GetEnvironmentVariable("COGNITO_USER_POOL_ID")
                         ?? throw new InvalidOperationException("COGNITO_USER_POOL_ID is not set.");
        var region = Environment.GetEnvironmentVariable("COGNITO_REGION")
                     ?? Environment.GetEnvironmentVariable("AWS_REGION")
                     ?? throw new InvalidOperationException("COGNITO_REGION or AWS_REGION is not set.");
        var clientId = Environment.GetEnvironmentVariable("COGNITO_CLIENT_ID")
                       ?? throw new InvalidOperationException("COGNITO_CLIENT_ID is not set.");

        _jwtValidator = new CognitoJwtValidator(userPoolId, region, clientId);
    }

    /// <summary>
    /// Constructor for testing. Accepts an IJwtValidator dependency.
    /// </summary>
    public AuthorizerFunction(IJwtValidator jwtValidator)
    {
        _jwtValidator = jwtValidator ?? throw new ArgumentNullException(nameof(jwtValidator));
    }

    /// <summary>
    /// Lambda handler for API Gateway custom authorizer (TOKEN type).
    /// </summary>
    public APIGatewayCustomAuthorizerResponse FunctionHandler(
        APIGatewayCustomAuthorizerRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation("Authorizer invoked");

        var token = ExtractBearerToken(request.AuthorizationToken);

        if (string.IsNullOrEmpty(token))
        {
            context.Logger.LogWarning("No Bearer token found in Authorization header");
            return GenerateDenyPolicy("unauthorized", request.MethodArn);
        }

        var result = _jwtValidator.ValidateToken(token);

        if (!result.IsValid)
        {
            context.Logger.LogWarning($"Token validation failed: {result.ErrorMessage}");
            return GenerateDenyPolicy("unauthorized", request.MethodArn);
        }

        context.Logger.LogInformation($"Token validated for user: {result.UserId}");
        return GenerateAllowPolicy(result.UserId!, request.MethodArn, result);
    }

    /// <summary>
    /// Extracts the Bearer token from the Authorization header value.
    /// </summary>
    internal static string? ExtractBearerToken(string? authorizationToken)
    {
        if (string.IsNullOrWhiteSpace(authorizationToken))
            return null;

        if (authorizationToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authorizationToken["Bearer ".Length..].Trim();

        // Some API Gateway configurations pass the token directly
        return authorizationToken.Trim();
    }

    private static APIGatewayCustomAuthorizerResponse GenerateAllowPolicy(
        string principalId,
        string methodArn,
        JwtValidationResult claims)
    {
        return new APIGatewayCustomAuthorizerResponse
        {
            PrincipalID = principalId,
            PolicyDocument = new APIGatewayCustomAuthorizerPolicy
            {
                Version = "2012-10-17",
                Statement =
                [
                    new APIGatewayCustomAuthorizerPolicy.IAMPolicyStatement
                    {
                        Action = ["execute-api:Invoke"],
                        Effect = "Allow",
                        Resource = [BuildResourceArn(methodArn)]
                    }
                ]
            },
            Context = new APIGatewayCustomAuthorizerContextOutput
            {
                ["userId"] = claims.UserId!,
                ["phoneNumber"] = claims.PhoneNumber!,
                ["role"] = claims.Role!,
                ["woreda"] = claims.Woreda!
            }
        };
    }

    private static APIGatewayCustomAuthorizerResponse GenerateDenyPolicy(
        string principalId,
        string methodArn)
    {
        return new APIGatewayCustomAuthorizerResponse
        {
            PrincipalID = principalId,
            PolicyDocument = new APIGatewayCustomAuthorizerPolicy
            {
                Version = "2012-10-17",
                Statement =
                [
                    new APIGatewayCustomAuthorizerPolicy.IAMPolicyStatement
                    {
                        Action = ["execute-api:Invoke"],
                        Effect = "Deny",
                        Resource = [BuildResourceArn(methodArn)]
                    }
                ]
            }
        };
    }

    /// <summary>
    /// Converts a method ARN to a wildcard ARN for the entire API stage.
    /// arn:aws:execute-api:region:account:api-id/stage/METHOD/resource -> arn:...:api-id/stage/*
    /// </summary>
    private static string BuildResourceArn(string methodArn)
    {
        if (string.IsNullOrEmpty(methodArn))
            return "*";

        var parts = methodArn.Split('/');
        return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}/*" : methodArn;
    }
}
