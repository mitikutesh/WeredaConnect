using WeredaConnect.Server.Models;

namespace WeredaConnect.Server;

/// <summary>
/// Root JSON source generator context for AOT compatibility.
/// Register all shared types that Lambda handlers need to serialize/deserialize.
/// Each function may also define its own context for function-specific types.
/// </summary>
[JsonSerializable(typeof(APIGatewayProxyRequest))]
[JsonSerializable(typeof(APIGatewayProxyResponse))]
[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(List<UserDto>))]
[JsonSerializable(typeof(MinimalUserDto))]
[JsonSerializable(typeof(RegisterRequest))]
[JsonSerializable(typeof(RegisterResponse))]
[JsonSerializable(typeof(VerifyOtpRequest))]
[JsonSerializable(typeof(VerifyOtpResponse))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(LoginResponse))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(RefreshRequest))]
[JsonSerializable(typeof(RefreshResponse))]
[JsonSerializable(typeof(LogoutRequest))]
[JsonSerializable(typeof(LogoutResponse))]
[JsonSerializable(typeof(AuthErrorResponse))]
[JsonSerializable(typeof(AuthError))]
[JsonSerializable(typeof(SyncRequest))]
[JsonSerializable(typeof(SyncOperationRequest))]
[JsonSerializable(typeof(List<SyncOperationRequest>))]
[JsonSerializable(typeof(SyncResponse))]
[JsonSerializable(typeof(SyncOperationResult))]
[JsonSerializable(typeof(List<SyncOperationResult>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public partial class AppJsonContext : JsonSerializerContext
{
}
