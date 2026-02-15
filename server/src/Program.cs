using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using WeredaConnect.Server.Functions;

namespace WeredaConnect.Server;

/// <summary>
/// Lambda Native AOT bootstrap entry point.
/// Routes incoming invocations to the correct function handler
/// based on the FUNCTION_HANDLER environment variable.
///
/// Each Lambda is deployed with its own FUNCTION_HANDLER value:
///   RegisterFunction, VerifyOtpFunction, LoginFunction,
///   RefreshFunction, LogoutFunction, AuthorizerFunction, HealthCheckFunction
/// </summary>
public static class Program
{
    public static async Task Main()
    {
        var handler = Environment.GetEnvironmentVariable("FUNCTION_HANDLER")
                      ?? Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME")
                      ?? "HealthCheckFunction";

        var serializer = new SourceGeneratorLambdaJsonSerializer<AppJsonContext>();

        using var handlerWrapper = ResolveHandler(handler, serializer);
        var bootstrap = new LambdaBootstrap(handlerWrapper);
        await bootstrap.RunAsync();
    }

    private static HandlerWrapper ResolveHandler(string handler, SourceGeneratorLambdaJsonSerializer<AppJsonContext> serializer)
    {
        return handler switch
        {
            var h when h.Contains("RegisterFunction") =>
                HandlerWrapper.GetHandlerWrapper<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                    new RegisterFunction().FunctionHandler, serializer),

            var h when h.Contains("VerifyOtpFunction") =>
                HandlerWrapper.GetHandlerWrapper<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                    new VerifyOtpFunction().FunctionHandler, serializer),

            var h when h.Contains("LoginFunction") =>
                HandlerWrapper.GetHandlerWrapper<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                    new LoginFunction().FunctionHandler, serializer),

            var h when h.Contains("RefreshFunction") =>
                HandlerWrapper.GetHandlerWrapper<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                    new RefreshFunction().FunctionHandler, serializer),

            var h when h.Contains("LogoutFunction") =>
                HandlerWrapper.GetHandlerWrapper<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                    new LogoutFunction().FunctionHandler, serializer),

            var h when h.Contains("AuthorizerFunction") =>
                HandlerWrapper.GetHandlerWrapper(
                    (Func<APIGatewayCustomAuthorizerRequest, ILambdaContext, APIGatewayCustomAuthorizerResponse>)
                        new AuthorizerFunction().FunctionHandler, serializer),

            var h when h.Contains("SyncFunction") =>
                HandlerWrapper.GetHandlerWrapper<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                    new SyncFunction().FunctionHandler, serializer),

            var h when h.Contains("HealthCheckFunction") =>
                HandlerWrapper.GetHandlerWrapper(
                    (Func<APIGatewayProxyRequest, ILambdaContext, APIGatewayProxyResponse>)
                        new HealthCheckFunction().FunctionHandler, serializer),

            _ => throw new InvalidOperationException(
                $"Unknown handler: '{handler}'. Set FUNCTION_HANDLER to a valid function name.")
        };
    }
}
