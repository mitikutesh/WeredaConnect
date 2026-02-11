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

        var healthCheck = JsonSerializer.Deserialize(response.Body, HealthCheckJsonContext.Default.HealthCheckResponse);
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
        Assert.Equal("application/json", response.Headers["Content-Type"]);
        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", response.Headers["X-Frame-Options"]);
        Assert.Equal("no-store", response.Headers["Cache-Control"]);
        Assert.Contains("max-age=", response.Headers["Strict-Transport-Security"]);
    }

    [Fact]
    public void FunctionHandler_DetectsColdStart()
    {
        // Arrange
        HealthCheckFunction.ResetColdStart(); // Reset state for this test
        var function = new HealthCheckFunction();
        var request = new APIGatewayProxyRequest();
        var context = new TestLambdaContext();

        // Act - First invocation should be cold start
        var response1 = function.FunctionHandler(request, context);
        var healthCheck1 = JsonSerializer.Deserialize(response1.Body, HealthCheckJsonContext.Default.HealthCheckResponse);

        // Act - Second invocation should not be cold start
        var response2 = function.FunctionHandler(request, context);
        var healthCheck2 = JsonSerializer.Deserialize(response2.Body, HealthCheckJsonContext.Default.HealthCheckResponse);

        // Assert
        Assert.NotNull(healthCheck1);
        Assert.True(healthCheck1.ColdStart);

        Assert.NotNull(healthCheck2);
        Assert.False(healthCheck2.ColdStart);
    }
}

