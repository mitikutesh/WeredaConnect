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
///   RegisterFunction, FaydaVerifyFunction, LoginFunction,
///   RefreshFunction, LogoutFunction, HealthCheckFunction
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

            var h when h.Contains("FaydaVerifyFunction") =>
                HandlerWrapper.GetHandlerWrapper<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                    new FaydaVerifyFunction().FunctionHandler, serializer),

            var h when h.Contains("LoginFunction") =>
                HandlerWrapper.GetHandlerWrapper<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                    new LoginFunction().FunctionHandler, serializer),

            var h when h.Contains("RefreshFunction") =>
                HandlerWrapper.GetHandlerWrapper<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                    new RefreshFunction().FunctionHandler, serializer),

            var h when h.Contains("LogoutFunction") =>
                HandlerWrapper.GetHandlerWrapper<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                    new LogoutFunction().FunctionHandler, serializer),

            var h when h.Contains("SyncFunction") =>
                HandlerWrapper.GetHandlerWrapper<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                    new SyncFunction().FunctionHandler, serializer),

            var h when h.Contains("CropFunction") =>
                HandlerWrapper.GetHandlerWrapper<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                    new CropFunction().FunctionHandler, serializer),

            var h when h.Contains("TripFunction") =>
                HandlerWrapper.GetHandlerWrapper<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                    new TripFunction().FunctionHandler, serializer),

            var h when h.Contains("MatchFunction") =>
                HandlerWrapper.GetHandlerWrapper<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                    new MatchFunction().FunctionHandler, serializer),

            var h when h.Contains("DeviceTokenFunction") =>
                HandlerWrapper.GetHandlerWrapper<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                    new DeviceTokenFunction().FunctionHandler, serializer),

            // DynamoDB Streams consumer: raw Stream in, no response (AOT — parses
            // the event with JsonDocument rather than the typed DynamoDBEvent).
            var h when h.Contains("CropMatchStreamFunction") =>
                HandlerWrapper.GetHandlerWrapper(
                    (Func<Stream, ILambdaContext, Task>)new CropMatchStreamFunction().ProcessStreamAsync),

            var h when h.Contains("DeliveryFunction") =>
                HandlerWrapper.GetHandlerWrapper<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                    new DeliveryFunction().FunctionHandler, serializer),

            var h when h.Contains("EscrowCronFunction") =>
                HandlerWrapper.GetHandlerWrapper<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                    new EscrowCronFunction().FunctionHandler, serializer),

            var h when h.Contains("AdminFunction") =>
                HandlerWrapper.GetHandlerWrapper<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                    new AdminFunction().FunctionHandler, serializer),

            var h when h.Contains("OrderCronFunction") =>
                HandlerWrapper.GetHandlerWrapper<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                    new OrderCronFunction().FunctionHandler, serializer),

            var h when h.Contains("OrderFunction") =>
                HandlerWrapper.GetHandlerWrapper<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                    new OrderFunction().FunctionHandler, serializer),

            var h when h.Contains("EscrowFunction") =>
                HandlerWrapper.GetHandlerWrapper<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                    new EscrowFunction().FunctionHandler, serializer),

            var h when h.Contains("PasswordResetFunction") =>
                HandlerWrapper.GetHandlerWrapper<APIGatewayProxyRequest, APIGatewayProxyResponse>(
                    new PasswordResetFunction().FunctionHandler, serializer),

            var h when h.Contains("HealthCheckFunction") =>
                HandlerWrapper.GetHandlerWrapper(
                    (Func<APIGatewayProxyRequest, ILambdaContext, APIGatewayProxyResponse>)
                        new HealthCheckFunction().FunctionHandler, serializer),

            _ => throw new InvalidOperationException(
                $"Unknown handler: '{handler}'. Set FUNCTION_HANDLER to a valid function name.")
        };
    }
}
