using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using WeredaConnect.Server.Functions;
using Xunit;

namespace WeredaConnect.Server.Tests;

public class HealthCheckFunctionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    [Fact]
    public void FunctionHandler_ReturnsHealthyStatus()
    {
        // Arrange
        var function = new HealthCheckFunction();
        var request = new APIGatewayProxyRequest();
        var context = new TestLambdaContext();

        // Act
        var response = function.FunctionHandler(request, context);

        // Assert
        Assert.Equal(200, response.StatusCode);
        Assert.NotNull(response.Body);

        var healthCheck = JsonSerializer.Deserialize<HealthCheckResponse>(response.Body, JsonOptions);
        Assert.NotNull(healthCheck);
        Assert.Equal("healthy", healthCheck.Status);
    }

    [Fact]
    public void FunctionHandler_IncludesCorrectHeaders()
    {
        // Arrange
        var function = new HealthCheckFunction();
        var request = new APIGatewayProxyRequest();
        var context = new TestLambdaContext();

        // Act
        var response = function.FunctionHandler(request, context);

        // Assert
        Assert.True(response.Headers.ContainsKey("Content-Type"));
        Assert.Equal("application/json", response.Headers["Content-Type"]);
        Assert.False(response.Headers.ContainsKey("X-WeredaConnect-Version"));
    }

}

