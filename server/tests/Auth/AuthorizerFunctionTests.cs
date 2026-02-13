using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Auth;
using WeredaConnect.Server.Functions;
using Xunit;

namespace WeredaConnect.Server.Tests.Auth;

public class AuthorizerFunctionTests
{
    private readonly IJwtValidator _mockValidator;
    private readonly AuthorizerFunction _function;
    private readonly TestLambdaContext _context;

    private const string TestMethodArn =
        "arn:aws:execute-api:me-south-1:123456789012:abcdef1234/dev/GET/health";

    public AuthorizerFunctionTests()
    {
        _mockValidator = Substitute.For<IJwtValidator>();
        _function = new AuthorizerFunction(_mockValidator);
        _context = new TestLambdaContext();
    }

    [Fact]
    public void FunctionHandler_ValidToken_ReturnsAllowPolicy()
    {
        _mockValidator.ValidateToken("valid-token")
            .Returns(JwtValidationResult.Valid(
                "user-123", "+251912345678", "farmer", "Adama"));

        var request = new APIGatewayCustomAuthorizerRequest
        {
            AuthorizationToken = "Bearer valid-token",
            MethodArn = TestMethodArn
        };

        var response = _function.FunctionHandler(request, _context);

        Assert.Equal("user-123", response.PrincipalID);
        Assert.Equal("Allow", response.PolicyDocument.Statement[0].Effect);
        Assert.Equal("user-123", response.Context["userId"]);
        Assert.Equal("+251912345678", response.Context["phoneNumber"]);
        Assert.Equal("farmer", response.Context["role"]);
        Assert.Equal("Adama", response.Context["woreda"]);
    }

    [Fact]
    public void FunctionHandler_InvalidToken_ReturnsDenyPolicy()
    {
        _mockValidator.ValidateToken("bad-token")
            .Returns(JwtValidationResult.Invalid("Token has expired."));

        var request = new APIGatewayCustomAuthorizerRequest
        {
            AuthorizationToken = "Bearer bad-token",
            MethodArn = TestMethodArn
        };

        var response = _function.FunctionHandler(request, _context);

        Assert.Equal("unauthorized", response.PrincipalID);
        Assert.Equal("Deny", response.PolicyDocument.Statement[0].Effect);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FunctionHandler_MissingToken_ReturnsDenyPolicy(string? token)
    {
        var request = new APIGatewayCustomAuthorizerRequest
        {
            AuthorizationToken = token,
            MethodArn = TestMethodArn
        };

        var response = _function.FunctionHandler(request, _context);

        Assert.Equal("unauthorized", response.PrincipalID);
        Assert.Equal("Deny", response.PolicyDocument.Statement[0].Effect);
    }

    [Fact]
    public void ExtractBearerToken_BearerPrefix_ExtractsToken()
    {
        var token = AuthorizerFunction.ExtractBearerToken("Bearer my-token-123");
        Assert.Equal("my-token-123", token);
    }

    [Fact]
    public void ExtractBearerToken_NoBearerPrefix_ReturnsRawToken()
    {
        var token = AuthorizerFunction.ExtractBearerToken("raw-token-456");
        Assert.Equal("raw-token-456", token);
    }

    [Fact]
    public void ExtractBearerToken_CaseInsensitive_ExtractsToken()
    {
        var token = AuthorizerFunction.ExtractBearerToken("bearer my-token");
        Assert.Equal("my-token", token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExtractBearerToken_EmptyOrNull_ReturnsNull(string? input)
    {
        var token = AuthorizerFunction.ExtractBearerToken(input);
        Assert.Null(token);
    }
}
