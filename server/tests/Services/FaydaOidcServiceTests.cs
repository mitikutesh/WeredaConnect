using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// Tests for FaydaOidcService — focuses on GenerateVerificationToken / ValidateVerificationToken
/// round-trip and edge cases. ExchangeCodeAsync tests require an HTTP mock and are covered
/// indirectly via FaydaVerifyFunctionTests with the interface mocked.
/// </summary>
public class FaydaOidcServiceTests
{
    private const string SigningSecret = "test-signing-secret-must-be-at-least-32-chars-long!";
    private readonly FaydaOidcService _service;

    public FaydaOidcServiceTests()
    {
        _service = new FaydaOidcService(
            new HttpClient(), "test-client-id", "test-client-secret",
            "https://fayda.test", SigningSecret);
    }

    [Fact]
    public void GenerateVerificationToken_ReturnsNonEmptyString()
    {
        var token = _service.GenerateVerificationToken("FAYDA-001", "Abebe Kebede");

        Assert.False(string.IsNullOrEmpty(token));
    }

    [Fact]
    public void GenerateVerificationToken_ProducesValidJwt()
    {
        var token = _service.GenerateVerificationToken("FAYDA-001", "Abebe Kebede");

        // JWT has 3 parts separated by dots
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
    }

    [Fact]
    public void ValidateVerificationToken_ValidToken_ReturnsSuccess()
    {
        var token = _service.GenerateVerificationToken("FAYDA-001", "Abebe Kebede");

        var result = _service.ValidateVerificationToken(token);

        Assert.True(result.IsSuccess);
        Assert.Equal("FAYDA-001", result.Value!.FaydaId);
        Assert.Equal("Abebe Kebede", result.Value.FullName);
    }

    [Fact]
    public void ValidateVerificationToken_RoundTrip_PreservesUnicodeName()
    {
        var token = _service.GenerateVerificationToken("FAYDA-002", "አበበ ከበደ");

        var result = _service.ValidateVerificationToken(token);

        Assert.True(result.IsSuccess);
        Assert.Equal("FAYDA-002", result.Value!.FaydaId);
        Assert.Equal("አበበ ከበደ", result.Value.FullName);
    }

    [Fact]
    public void ValidateVerificationToken_InvalidToken_ReturnsFailure()
    {
        var result = _service.ValidateVerificationToken("not-a-jwt");

        Assert.False(result.IsSuccess);
        Assert.Equal("FAYDA_001", result.Error!.Code);
    }

    [Fact]
    public void ValidateVerificationToken_TamperedToken_ReturnsFailure()
    {
        var token = _service.GenerateVerificationToken("FAYDA-001", "Abebe");

        // Tamper with the signature
        var tampered = token[..^5] + "XXXXX";

        var result = _service.ValidateVerificationToken(tampered);

        Assert.False(result.IsSuccess);
        Assert.Equal("FAYDA_001", result.Error!.Code);
    }

    [Fact]
    public void ValidateVerificationToken_WrongSigningKey_ReturnsFailure()
    {
        var otherService = new FaydaOidcService(
            new HttpClient(), "test-client-id", "test-client-secret",
            "https://fayda.test", "different-signing-secret-also-at-least-32-chars!");

        var token = otherService.GenerateVerificationToken("FAYDA-001", "Abebe");

        // Validate with the original service (different key)
        var result = _service.ValidateVerificationToken(token);

        Assert.False(result.IsSuccess);
        Assert.Equal("FAYDA_001", result.Error!.Code);
    }

    [Fact]
    public void ValidateVerificationToken_EmptyString_ReturnsFailure()
    {
        var result = _service.ValidateVerificationToken("");

        Assert.False(result.IsSuccess);
        Assert.Equal("FAYDA_001", result.Error!.Code);
    }

    [Fact]
    public void GenerateVerificationToken_DifferentInputs_ProduceDifferentTokens()
    {
        var token1 = _service.GenerateVerificationToken("FAYDA-001", "Abebe");
        var token2 = _service.GenerateVerificationToken("FAYDA-002", "Kebede");

        Assert.NotEqual(token1, token2);
    }
}
