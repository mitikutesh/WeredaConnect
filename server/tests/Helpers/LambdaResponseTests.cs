using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Validation;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

public class LambdaResponseTests
{
    [Fact]
    public void Ok_ReturnsStatusCode200WithBody()
    {
        var body = new LogoutResponse { LoggedOut = true, Message = "Done" };
        var response = LambdaResponse.Ok(body, AppJsonContext.Default.LogoutResponse);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"loggedOut\":true", response.Body);
    }

    [Fact]
    public void Created_ReturnsStatusCode201WithBody()
    {
        var body = new RegisterResponse
        {
            UserId = "u-1",
            PhoneNumber = "+251912345678",
            Tokens = new TokenResponse
            {
                AccessToken = "access",
                IdToken = "id",
                ExpiresIn = 3600
            },
            User = new UserDto
            {
                UserId = "u-1",
                PhoneNumber = "+251912345678",
                Role = "farmer",
                Woreda = "Adama",
                IsPhoneVerified = true,
                CreatedAt = "2026-01-01T00:00:00Z",
                UpdatedAt = "2026-01-01T00:00:00Z",
                Version = 1
            }
        };
        var response = LambdaResponse.Created(body, AppJsonContext.Default.RegisterResponse);

        Assert.Equal(201, response.StatusCode);
        Assert.Contains("\"userId\":\"u-1\"", response.Body);
    }

    [Fact]
    public void Error_ReturnsCorrectStatusCodeAndErrorBody()
    {
        var response = LambdaResponse.Error(409, "AUTH_005", "Phone exists", "phoneNumber");

        Assert.Equal(409, response.StatusCode);
        Assert.Contains("AUTH_005", response.Body);
        Assert.Contains("Phone exists", response.Body);
        Assert.Contains("phoneNumber", response.Body);
    }

    [Fact]
    public void Error_WithoutField_OmitsFieldFromBody()
    {
        var response = LambdaResponse.Error(503, "AUTH_008", "Service unavailable");

        Assert.Equal(503, response.StatusCode);
        Assert.Contains("AUTH_008", response.Body);
        Assert.DoesNotContain("\"field\"", response.Body); // null fields are omitted
    }

    [Fact]
    public void AllResponses_IncludeSecurityHeaders()
    {
        var response = LambdaResponse.Ok(
            new LogoutResponse { LoggedOut = true },
            AppJsonContext.Default.LogoutResponse);

        Assert.Equal("application/json", response.Headers["Content-Type"]);
        Assert.Equal("max-age=63072000; includeSubDomains; preload", response.Headers["Strict-Transport-Security"]);
        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", response.Headers["X-Frame-Options"]);
        Assert.Equal("no-store", response.Headers["Cache-Control"]);
    }

    [Fact]
    public void ValidationError_ReturnsFirstErrorAs400()
    {
        var validationResult = ValidationResult.Failure(new ValidationError
        {
            Field = "phoneNumber",
            Code = "AUTH_001",
            Message = "Phone number is required."
        });

        var response = LambdaResponse.ValidationError(validationResult);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_001", response.Body);
        Assert.Contains("phoneNumber", response.Body);
    }

    [Fact]
    public void RateLimited_Returns429WithErrorBody()
    {
        var response = LambdaResponse.RateLimited("AUTH_006", "Too many requests.");

        Assert.Equal(429, response.StatusCode);
        Assert.Contains("AUTH_006", response.Body);
        Assert.Contains("Too many requests", response.Body);
    }

    [Fact]
    public void RateLimited_WithRetryAfter_IncludesHeader()
    {
        var response = LambdaResponse.RateLimited("AUTH_006", "Too many requests.", 900);

        Assert.Equal(429, response.StatusCode);
        Assert.Equal("900", response.Headers["Retry-After"]);
    }

    [Fact]
    public void RateLimited_WithoutRetryAfter_OmitsHeader()
    {
        var response = LambdaResponse.RateLimited("AUTH_006", "Too many requests.");

        Assert.False(response.Headers.ContainsKey("Retry-After"));
    }

    [Fact]
    public void RateLimited_IncludesSecurityHeaders()
    {
        var response = LambdaResponse.RateLimited("AUTH_006", "Too many requests.");

        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", response.Headers["X-Frame-Options"]);
        Assert.Equal("no-store", response.Headers["Cache-Control"]);
    }
}
