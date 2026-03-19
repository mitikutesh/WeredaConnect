using System.Text.Json;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// Structured audit logger for CloudWatch Logs.
/// Writes JSON-formatted log entries to stdout (captured by Lambda runtime).
/// AOT-compatible — uses source-generated JSON serialization.
/// </summary>
public static class AuditLogger
{
    /// <summary>
    /// Log an audit event with structured data.
    /// </summary>
    public static void LogAudit(
        string category,
        string action,
        string? userId = null,
        string? correlationId = null,
        string? entityType = null,
        string? entityId = null,
        string? detail = null,
        string? outcome = null,
        string? errorCode = null)
    {
        var entry = new AuditLogEntry
        {
            Timestamp = DateTime.UtcNow.ToString("O"),
            Category = category,
            Action = action,
            UserId = userId,
            CorrelationId = correlationId,
            EntityType = entityType,
            EntityId = entityId,
            Detail = detail,
            Outcome = outcome ?? "success",
            ErrorCode = errorCode
        };

        var json = JsonSerializer.Serialize(entry, AuditLogJsonContext.Default.AuditLogEntry);
        Console.WriteLine(json);
    }

    /// <summary>
    /// Log a security-relevant event (login, failed auth, rate limit, etc).
    /// </summary>
    public static void LogSecurity(
        string action,
        string? userId = null,
        string? correlationId = null,
        string? detail = null,
        string? outcome = null,
        string? sourceIp = null)
    {
        var entry = new SecurityLogEntry
        {
            Timestamp = DateTime.UtcNow.ToString("O"),
            Category = "SECURITY",
            Action = action,
            UserId = userId,
            CorrelationId = correlationId,
            Detail = detail,
            Outcome = outcome ?? "success",
            SourceIp = sourceIp
        };

        var json = JsonSerializer.Serialize(entry, AuditLogJsonContext.Default.SecurityLogEntry);
        Console.WriteLine(json);
    }

    /// <summary>
    /// Extract the X-Correlation-Id header from an API Gateway request.
    /// If the header is missing, generates a short unique ID.
    /// </summary>
    public static string GetCorrelationId(APIGatewayProxyRequest request)
    {
        if (request.Headers != null &&
            request.Headers.TryGetValue("X-Correlation-Id", out var correlationId) &&
            !string.IsNullOrWhiteSpace(correlationId))
        {
            return correlationId;
        }

        return Guid.NewGuid().ToString("N")[..12];
    }

    /// <summary>
    /// Extract the source IP address from the API Gateway request context.
    /// </summary>
    public static string? GetSourceIp(APIGatewayProxyRequest request)
    {
        return request.RequestContext?.Identity?.SourceIp;
    }
}

public sealed record AuditLogEntry
{
    public required string Timestamp { get; init; }
    public required string Category { get; init; }
    public required string Action { get; init; }
    public string? UserId { get; init; }
    public string? CorrelationId { get; init; }
    public string? EntityType { get; init; }
    public string? EntityId { get; init; }
    public string? Detail { get; init; }
    public required string Outcome { get; init; }
    public string? ErrorCode { get; init; }
}

public sealed record SecurityLogEntry
{
    public required string Timestamp { get; init; }
    public required string Category { get; init; }
    public required string Action { get; init; }
    public string? UserId { get; init; }
    public string? CorrelationId { get; init; }
    public string? Detail { get; init; }
    public required string Outcome { get; init; }
    public string? SourceIp { get; init; }
}

[JsonSerializable(typeof(AuditLogEntry))]
[JsonSerializable(typeof(SecurityLogEntry))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
internal partial class AuditLogJsonContext : JsonSerializerContext
{
}
