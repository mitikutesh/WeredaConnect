using System.Text.Json.Serialization.Metadata;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Validation;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// Static helper for constructing APIGatewayProxyResponse with security headers.
/// All methods include the 5 standard security headers per security audit requirements.
/// Uses JsonTypeInfo&lt;T&gt; parameter for AOT-safe serialization.
/// </summary>
public static class LambdaResponse
{
    private static Dictionary<string, string> SecurityHeaders => new()
    {
        { "Content-Type", "application/json" },
        { "Strict-Transport-Security", "max-age=63072000; includeSubDomains; preload" },
        { "X-Content-Type-Options", "nosniff" },
        { "X-Frame-Options", "DENY" },
        { "Cache-Control", "no-store" }
    };

    /// <summary>
    /// 200 OK response with serialized body.
    /// </summary>
    public static APIGatewayProxyResponse Ok<T>(T body, JsonTypeInfo<T> typeInfo)
    {
        return new APIGatewayProxyResponse
        {
            StatusCode = 200,
            Body = JsonSerializer.Serialize(body, typeInfo),
            Headers = SecurityHeaders
        };
    }

    /// <summary>
    /// 201 Created response with serialized body.
    /// </summary>
    public static APIGatewayProxyResponse Created<T>(T body, JsonTypeInfo<T> typeInfo)
    {
        return new APIGatewayProxyResponse
        {
            StatusCode = 201,
            Body = JsonSerializer.Serialize(body, typeInfo),
            Headers = SecurityHeaders
        };
    }

    /// <summary>
    /// Error response with AUTH_* code, message, and optional field reference.
    /// </summary>
    public static APIGatewayProxyResponse Error(int statusCode, string code, string message, string? field = null)
    {
        var errorResponse = new AuthErrorResponse
        {
            Error = new AuthError
            {
                Code = code,
                Message = message,
                Field = field
            }
        };

        return new APIGatewayProxyResponse
        {
            StatusCode = statusCode,
            Body = JsonSerializer.Serialize(errorResponse, AppJsonContext.Default.AuthErrorResponse),
            Headers = SecurityHeaders
        };
    }

    /// <summary>
    /// Validation error response (400) from a ValidationResult. Returns the first error.
    /// </summary>
    public static APIGatewayProxyResponse ValidationError(ValidationResult validationResult)
    {
        var firstError = validationResult.Errors[0];
        return Error(400, firstError.Code, firstError.Message, firstError.Field);
    }
}
