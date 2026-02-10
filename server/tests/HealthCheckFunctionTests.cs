using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using WeredaConnect.Server.Functions;
using Xunit;
using System.Text.Json;

namespace WeredaConnect.Server.Tests;

public class HealthCheckFunctionTests
{
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

        var healthCheck = JsonSerializer.Deserialize<HealthCheckResponse>(response.Body);
        Assert.NotNull(healthCheck);
        Assert.Equal("healthy", healthCheck.Status);
        Assert.Equal("0.1.0", healthCheck.Version);
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
        Assert.True(response.Headers.ContainsKey("X-WeredaConnect-Version"));
        Assert.Equal("0.1.0", response.Headers["X-WeredaConnect-Version"]);
    }

    [Fact]
    public void FunctionHandler_DetectsColdStart()
    {
        // Arrange
        var function = new HealthCheckFunction();
        var request = new APIGatewayProxyRequest();
        var context = new TestLambdaContext();

        // Act - First invocation should be cold start
        var response1 = function.FunctionHandler(request, context);
        var healthCheck1 = JsonSerializer.Deserialize<HealthCheckResponse>(response1.Body);

        // Act - Second invocation should not be cold start
        var response2 = function.FunctionHandler(request, context);
        var healthCheck2 = JsonSerializer.Deserialize<HealthCheckResponse>(response2.Body);

        // Assert
        Assert.NotNull(healthCheck1);
        Assert.True(healthCheck1.ColdStart);

        Assert.NotNull(healthCheck2);
        Assert.False(healthCheck2.ColdStart);
    }
}

