using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

public class FaydaVerifyFunctionTests
{
    private readonly IFaydaOidcService _mockFayda;
    private readonly IUserRepository _mockRepo;
    private readonly FaydaVerifyFunction _function;
    private readonly TestLambdaContext _context;

    public FaydaVerifyFunctionTests()
    {
        _mockFayda = Substitute.For<IFaydaOidcService>();
        _mockRepo = Substitute.For<IUserRepository>();
        _function = new FaydaVerifyFunction(_mockFayda, _mockRepo);
        _context = new TestLambdaContext();

        // Default: code exchange succeeds
        _mockFayda.ExchangeCodeAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<FaydaVerificationResult>.Success(new FaydaVerificationResult
            {
                FaydaId = "FAYDA-123",
                FullName = "Abebe Kebede"
            }));

        // Default: no duplicate Fayda ID
        _mockRepo.GetUserByFaydaIdAsync(Arg.Any<string>())
            .Returns((UserDto?)null);

        // Default: generate a mock verification token
        _mockFayda.GenerateVerificationToken(Arg.Any<string>(), Arg.Any<string>())
            .Returns("mock-verification-token");
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
    public async Task FunctionHandler_ValidCode_Returns200WithVerificationToken()
    {
        var request = MakeRequest(new
        {
            authorizationCode = "valid-code",
            state = "state-xyz",
            redirectUri = "weredaconnect://fayda-callback"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("mock-verification-token", response.Body);
        Assert.DoesNotContain("faydaId", response.Body);
        Assert.Contains("Abebe Kebede", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_ValidCode_IncludesSecurityHeaders()
    {
        var request = MakeRequest(new
        {
            authorizationCode = "valid-code",
            state = "state-xyz",
            redirectUri = "weredaconnect://fayda-callback"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal("DENY", response.Headers["X-Frame-Options"]);
        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
        Assert.Equal("no-store", response.Headers["Cache-Control"]);
    }

    [Fact]
    public async Task FunctionHandler_ValidCode_ChecksDuplicateFaydaId()
    {
        var request = MakeRequest(new
        {
            authorizationCode = "valid-code",
            state = "state-xyz",
            redirectUri = "weredaconnect://fayda-callback"
        });

        await _function.FunctionHandler(request, _context);

        await _mockRepo.Received(1).GetUserByFaydaIdAsync("FAYDA-123");
    }

    [Fact]
    public async Task FunctionHandler_DuplicateFaydaId_Returns409()
    {
        _mockRepo.GetUserByFaydaIdAsync("FAYDA-123")
            .Returns(new UserDto
            {
                UserId = "existing-user",
                PhoneNumber = "+251911111111",
                Role = "farmer",
                Woreda = "Adama",
                FaydaId = "FAYDA-123",
                IsPhoneVerified = true,
                CreatedAt = "2026-01-01T00:00:00Z",
                UpdatedAt = "2026-01-01T00:00:00Z",
                Version = 1
            });

        var request = MakeRequest(new
        {
            authorizationCode = "valid-code",
            state = "state-xyz",
            redirectUri = "weredaconnect://fayda-callback"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(409, response.StatusCode);
        Assert.Contains("FAYDA_004", response.Body);
        Assert.Contains("already linked", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_InvalidCode_Returns400()
    {
        _mockFayda.ExchangeCodeAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<FaydaVerificationResult>.Failure(400, "FAYDA_001",
                "Invalid or expired authorization code.", "authorizationCode"));

        var request = MakeRequest(new
        {
            authorizationCode = "bad-code",
            state = "state-xyz",
            redirectUri = "weredaconnect://fayda-callback"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("FAYDA_001", response.Body);
        // Should NOT have checked for duplicates
        await _mockRepo.DidNotReceive().GetUserByFaydaIdAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task FunctionHandler_FaydaServiceUnavailable_Returns503()
    {
        _mockFayda.ExchangeCodeAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(AuthResult<FaydaVerificationResult>.Failure(503, "FAYDA_003",
                "Fayda identity service is temporarily unavailable."));

        var request = MakeRequest(new
        {
            authorizationCode = "valid-code",
            state = "state-xyz",
            redirectUri = "weredaconnect://fayda-callback"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(503, response.StatusCode);
        Assert.Contains("FAYDA_003", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_InvalidJson_Returns400()
    {
        var request = new APIGatewayProxyRequest { Body = "not{json" };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("AUTH_007", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_NullBody_Returns400()
    {
        var request = new APIGatewayProxyRequest { Body = null };

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
    }

    [Fact]
    public async Task FunctionHandler_MissingAuthorizationCode_Returns400()
    {
        var request = MakeRequest(new
        {
            authorizationCode = "",
            state = "state-xyz",
            redirectUri = "weredaconnect://fayda-callback"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("FAYDA_001", response.Body);
        // Fayda service should NOT have been called
        await _mockFayda.DidNotReceive().ExchangeCodeAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task FunctionHandler_MissingState_Returns400()
    {
        var request = MakeRequest(new
        {
            authorizationCode = "valid-code",
            state = "",
            redirectUri = "weredaconnect://fayda-callback"
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("FAYDA_002", response.Body);
    }

    [Fact]
    public async Task FunctionHandler_MissingRedirectUri_Returns400()
    {
        var request = MakeRequest(new
        {
            authorizationCode = "valid-code",
            state = "state-xyz",
            redirectUri = ""
        });

        var response = await _function.FunctionHandler(request, _context);

        Assert.Equal(400, response.StatusCode);
    }
}
