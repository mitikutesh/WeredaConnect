using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

public class RegisterFunctionTests
{
    private readonly ICognitoAuthService _mockAuth;
    private readonly IUserRepository _mockRepo;
    private readonly RegisterFunction _function;
    private readonly TestLambdaContext _context;

    public RegisterFunctionTests()
    {
        _mockAuth = Substitute.For<ICognitoAuthService>();
        _mockRepo = Substitute.For<IUserRepository>();
        _function = new RegisterFunction(_mockAuth, _mockRepo);
        _context = new TestLambdaContext();
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static APIGatewayProxyRequest MakeRequest(object body) => new()
    {
        Body = JsonSerializer.Serialize(body, SerializerOptions)
    };

    [Fact]
    public async Task FunctionHandler_ValidRequest_Returns201WithUserId()
    {
        _mockAuth.RegisterAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>())
            .Returns(AuthResult<string>.Success("user-abc"));

        _mockRepo.CreateUserAsync(Arg.Any<UserDto>())
            .Returns(ci => AuthResult<UserDto>.Success(ci.Arg<UserDto>()));

        var request = MakeRequest(new
        {
            phoneNumber = "+251912345678",
            password = "secure123",
            role = "farmer",
            woreda = "Adama",
            displayName = "Abebe",
            preferredLanguage = "am"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(201, response.StatusCode);
        Assert.Contains("user-abc", response.Body);
        Assert.Contains("nosniff", response.Headers["X-Content-Type-Options"]);
    }

    [Fact]
    public async Task FunctionHandler_InvalidJson_Returns400()
    {
        var request = new APIGatewayProxyRequest { Body = "not json{{{" };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_007", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_ValidationFails_Returns400WithFirstError()
    {
        var request = MakeRequest(new
        {
            phoneNumber = "invalid",
            password = "ab",
            role = "admin",
            woreda = ""
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_001", response.Body); // First error: phone
    }

    [Fact]
    public async Task FunctionHandler_CognitoPhoneExists_Returns409()
    {
        _mockAuth.RegisterAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>())
            .Returns(AuthResult<string>.Failure(409, "AUTH_005", "Phone exists", "phoneNumber"));

        var request = MakeRequest(new
        {
            phoneNumber = "+251912345678",
            password = "secure123",
            role = "farmer",
            woreda = "Adama",
            preferredLanguage = "am"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(409, response.StatusCode);
        Assert.Contains("AUTH_005", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_DbFailure_StillReturns201()
    {
        _mockAuth.RegisterAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>())
            .Returns(AuthResult<string>.Success("user-xyz"));

        _mockRepo.CreateUserAsync(Arg.Any<UserDto>())
            .ThrowsAsync(new Exception("DynamoDB down"));

        var request = MakeRequest(new
        {
            phoneNumber = "+251912345678",
            password = "secure123",
            role = "farmer",
            woreda = "Adama",
            preferredLanguage = "am"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(201, response.StatusCode); // Cognito succeeded → success
    }

    [Fact]
    public async Task FunctionHandler_NullBody_Returns400()
    {
        var request = new APIGatewayProxyRequest { Body = null };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
    }
}
