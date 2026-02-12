using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.Runtime;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

public class CognitoAuthServiceTests
{
    private readonly IAmazonCognitoIdentityProvider _mockCognito;
    private readonly CognitoAuthService _service;

    private const string ClientId = "test-client-id";

    public CognitoAuthServiceTests()
    {
        _mockCognito = Substitute.For<IAmazonCognitoIdentityProvider>();
        _service = new CognitoAuthService(_mockCognito, ClientId);
    }

    // --- RegisterAsync ---

    [Fact]
    public async Task RegisterAsync_Success_ReturnsUserId()
    {
        _mockCognito.SignUpAsync(Arg.Any<SignUpRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SignUpResponse { UserSub = "user-123" });

        var result = await _service.RegisterAsync(
            "+251912345678", "password1", "farmer", "Adama", "Abebe", "am");

        Assert.True(result.IsSuccess);
        Assert.Equal("user-123", result.Value);
    }

    [Fact]
    public async Task RegisterAsync_UsernameExists_ReturnsAuth005()
    {
        _mockCognito.SignUpAsync(Arg.Any<SignUpRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new UsernameExistsException("exists"));

        var result = await _service.RegisterAsync(
            "+251912345678", "password1", "farmer", "Adama", null, "am");

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTH_005", result.Error!.Code);
        Assert.Equal(409, result.Error.HttpStatus);
    }

    [Fact]
    public async Task RegisterAsync_InvalidPassword_ReturnsAuth002()
    {
        _mockCognito.SignUpAsync(Arg.Any<SignUpRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidPasswordException("Password policy"));

        var result = await _service.RegisterAsync(
            "+251912345678", "weak", "farmer", "Adama", null, "am");

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTH_002", result.Error!.Code);
        Assert.Equal(400, result.Error.HttpStatus);
    }

    [Fact]
    public async Task RegisterAsync_TooManyRequests_ReturnsAuth006()
    {
        _mockCognito.SignUpAsync(Arg.Any<SignUpRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TooManyRequestsException("throttled"));

        var result = await _service.RegisterAsync(
            "+251912345678", "password1", "farmer", "Adama", null, "am");

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTH_006", result.Error!.Code);
        Assert.Equal(429, result.Error.HttpStatus);
    }

    [Fact]
    public async Task RegisterAsync_InternalError_ReturnsAuth008()
    {
        _mockCognito.SignUpAsync(Arg.Any<SignUpRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InternalErrorException("down"));

        var result = await _service.RegisterAsync(
            "+251912345678", "password1", "farmer", "Adama", null, "am");

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTH_008", result.Error!.Code);
        Assert.Equal(503, result.Error.HttpStatus);
    }

    // --- ConfirmSignUpAsync ---

    [Fact]
    public async Task ConfirmSignUpAsync_Success_ReturnsTrue()
    {
        _mockCognito.ConfirmSignUpAsync(Arg.Any<ConfirmSignUpRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ConfirmSignUpResponse());

        var result = await _service.ConfirmSignUpAsync("+251912345678", "123456");

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
    }

    [Fact]
    public async Task ConfirmSignUpAsync_CodeMismatch_ReturnsAuth011()
    {
        _mockCognito.ConfirmSignUpAsync(Arg.Any<ConfirmSignUpRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new CodeMismatchException("wrong"));

        var result = await _service.ConfirmSignUpAsync("+251912345678", "999999");

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTH_011", result.Error!.Code);
        Assert.Equal(400, result.Error.HttpStatus);
    }

    [Fact]
    public async Task ConfirmSignUpAsync_ExpiredCode_ReturnsAuth010()
    {
        _mockCognito.ConfirmSignUpAsync(Arg.Any<ConfirmSignUpRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ExpiredCodeException("expired"));

        var result = await _service.ConfirmSignUpAsync("+251912345678", "123456");

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTH_010", result.Error!.Code);
        Assert.Equal(400, result.Error.HttpStatus);
    }

    [Fact]
    public async Task ConfirmSignUpAsync_UserNotFound_ReturnsAuth013()
    {
        _mockCognito.ConfirmSignUpAsync(Arg.Any<ConfirmSignUpRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new UserNotFoundException("not found"));

        var result = await _service.ConfirmSignUpAsync("+251912345678", "123456");

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTH_013", result.Error!.Code);
        Assert.Equal(404, result.Error.HttpStatus);
    }

    // --- LoginAsync ---

    [Fact]
    public async Task LoginAsync_Success_ReturnsTokens()
    {
        _mockCognito.InitiateAuthAsync(Arg.Any<InitiateAuthRequest>(), Arg.Any<CancellationToken>())
            .Returns(new InitiateAuthResponse
            {
                AuthenticationResult = new AuthenticationResultType
                {
                    AccessToken = "access-tok",
                    IdToken = "id-tok",
                    RefreshToken = "refresh-tok",
                    ExpiresIn = 3600
                }
            });

        var result = await _service.LoginAsync("+251912345678", "password1");

        Assert.True(result.IsSuccess);
        Assert.Equal("access-tok", result.Value!.AccessToken);
        Assert.Equal("id-tok", result.Value.IdToken);
        Assert.Equal("refresh-tok", result.Value.RefreshToken);
        Assert.Equal(3600, result.Value.ExpiresIn);
    }

    [Fact]
    public async Task LoginAsync_NotAuthorized_ReturnsAuth014()
    {
        _mockCognito.InitiateAuthAsync(Arg.Any<InitiateAuthRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new NotAuthorizedException("bad creds"));

        var result = await _service.LoginAsync("+251912345678", "wrong");

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTH_014", result.Error!.Code);
        Assert.Equal(401, result.Error.HttpStatus);
    }

    [Fact]
    public async Task LoginAsync_UserNotConfirmed_ReturnsAuth016()
    {
        _mockCognito.InitiateAuthAsync(Arg.Any<InitiateAuthRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new UserNotConfirmedException("not confirmed"));

        var result = await _service.LoginAsync("+251912345678", "password1");

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTH_016", result.Error!.Code);
        Assert.Equal(403, result.Error.HttpStatus);
    }

    [Fact]
    public async Task LoginAsync_UserNotFound_ReturnsAuth014_PreventEnumeration()
    {
        _mockCognito.InitiateAuthAsync(Arg.Any<InitiateAuthRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new UserNotFoundException("not found"));

        var result = await _service.LoginAsync("+251912345678", "password1");

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTH_014", result.Error!.Code); // Same as invalid creds
        Assert.Equal(401, result.Error.HttpStatus);
    }

    // --- RefreshAsync ---

    [Fact]
    public async Task RefreshAsync_Success_ReturnsNewTokens()
    {
        _mockCognito.InitiateAuthAsync(Arg.Any<InitiateAuthRequest>(), Arg.Any<CancellationToken>())
            .Returns(new InitiateAuthResponse
            {
                AuthenticationResult = new AuthenticationResultType
                {
                    AccessToken = "new-access",
                    IdToken = "new-id",
                    ExpiresIn = 3600
                }
            });

        var result = await _service.RefreshAsync("valid-refresh-token");

        Assert.True(result.IsSuccess);
        Assert.Equal("new-access", result.Value!.AccessToken);
        Assert.Null(result.Value.RefreshToken); // Cognito doesn't rotate
    }

    [Fact]
    public async Task RefreshAsync_NotAuthorized_ReturnsAuth018()
    {
        _mockCognito.InitiateAuthAsync(Arg.Any<InitiateAuthRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new NotAuthorizedException("expired"));

        var result = await _service.RefreshAsync("expired-token");

        Assert.False(result.IsSuccess);
        Assert.Equal("AUTH_018", result.Error!.Code);
        Assert.Equal(401, result.Error.HttpStatus);
    }

    // --- LogoutAsync ---

    [Fact]
    public async Task LogoutAsync_Success_ReturnsTrue()
    {
        _mockCognito.RevokeTokenAsync(Arg.Any<RevokeTokenRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RevokeTokenResponse());

        var result = await _service.LogoutAsync("refresh-token");

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
    }

    [Fact]
    public async Task LogoutAsync_NotAuthorized_TreatsAsSuccess()
    {
        _mockCognito.RevokeTokenAsync(Arg.Any<RevokeTokenRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new NotAuthorizedException("already revoked"));

        var result = await _service.LogoutAsync("already-revoked-token");

        Assert.True(result.IsSuccess); // Idempotent — already revoked is fine
    }
}
