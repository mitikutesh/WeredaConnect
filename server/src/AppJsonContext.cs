namespace WeredaConnect.Server;

/// <summary>
/// Root JSON source generator context for AOT compatibility.
/// Register all shared types that Lambda handlers need to serialize/deserialize.
/// Each function may also define its own context for function-specific types.
/// </summary>
[JsonSerializable(typeof(APIGatewayProxyRequest))]
[JsonSerializable(typeof(APIGatewayProxyResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public partial class AppJsonContext : JsonSerializerContext
{
}
