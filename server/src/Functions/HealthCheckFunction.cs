using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using System.Text.Json;
using System.Text.Json.Serialization;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace WeredaConnect.Server.Functions;

/// <summary>
/// Health check Lambda function for monitoring and cold-start optimization.
/// Target: <2 seconds cold start on ARM64.
/// </summary>
public class HealthCheckFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// Lambda function handler for health check endpoint.
    /// </summary>
    public APIGatewayProxyResponse FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        context.Logger.LogInformation("Health check invoked");

        var response = new HealthCheckResponse
        {
            Status = "healthy",
            Timestamp = DateTime.UtcNow,
            Version = "0.1.0",
            Region = Environment.GetEnvironmentVariable("AWS_REGION") ?? "unknown",
            ColdStart = IsColdStart()
        };

        return new APIGatewayProxyResponse
        {
            StatusCode = 200,
            Body = JsonSerializer.Serialize(response, JsonOptions),
            Headers = new Dictionary<string, string>
            {
                { "Content-Type", "application/json" },
                { "X-WeredaConnect-Version", "0.1.0" }
            }
        };
    }

    private static bool _isColdStart = true;

    private static bool IsColdStart()
    {
        if (_isColdStart)
        {
            _isColdStart = false;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Reset cold start state for testing purposes only.
    /// </summary>
    internal static void ResetColdStart()
    {
        _isColdStart = true;
    }
}

/// <summary>
/// Health check response model.
/// Uses System.Text.Json source generators for AOT compatibility.
/// </summary>
public record HealthCheckResponse
{
    public required string Status { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string Version { get; init; }
    public required string Region { get; init; }
    public required bool ColdStart { get; init; }
}

