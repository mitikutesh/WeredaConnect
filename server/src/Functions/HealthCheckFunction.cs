// Assembly attribute to enable AOT-compatible Lambda JSON serialization.
// Uses source-generated serialization instead of reflection-based DefaultLambdaJsonSerializer.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.SourceGeneratorLambdaJsonSerializer<WeredaConnect.Server.Functions.HealthCheckJsonContext>))]

namespace WeredaConnect.Server.Functions;

/// <summary>
/// JSON source generator context for HealthCheck function types.
/// Only registers types specific to this function — shared types
/// (APIGatewayProxyRequest/Response) are in AppJsonContext.
/// </summary>
[JsonSerializable(typeof(HealthCheckResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public partial class HealthCheckJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Health check Lambda function for monitoring and cold-start optimization.
/// Target: <2 seconds cold start on ARM64.
/// </summary>
public class HealthCheckFunction
{

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
            Body = JsonSerializer.Serialize(response, HealthCheckJsonContext.Default.HealthCheckResponse),
            Headers = new Dictionary<string, string>
            {
                { "Content-Type", "application/json" },
                { "X-WeredaConnect-Version", "0.1.0" },
                { "Strict-Transport-Security", "max-age=63072000; includeSubDomains; preload" },
                { "X-Content-Type-Options", "nosniff" },
                { "X-Frame-Options", "DENY" },
                { "Cache-Control", "no-store" }
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

