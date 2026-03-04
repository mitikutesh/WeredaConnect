using WeredaConnect.Server.Models;
using WeredaConnect.Server.Validation;
using Xunit;

namespace WeredaConnect.Server.Tests.Validation;

public class RequestValidatorTests
{
    // --- Phone Number ---

    [Theory]
    [InlineData("+251912345678")]
    [InlineData("+251911000000")]
    [InlineData("+251700000000")]
    public void ValidatePhoneNumber_ValidPhones_ReturnsSuccess(string phone)
    {
        var result = RequestValidator.ValidatePhoneNumber(phone);
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("+25191234567")]   // 8 digits
    [InlineData("+2519123456789")] // 10 digits
    [InlineData("0912345678")]     // no +251
    [InlineData("+252912345678")]  // wrong country
    [InlineData("not-a-number")]
    public void ValidatePhoneNumber_InvalidPhones_ReturnsFailure(string? phone)
    {
        var result = RequestValidator.ValidatePhoneNumber(phone);
        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Equal("AUTH_001", result.Errors[0].Code);
        Assert.Equal("phoneNumber", result.Errors[0].Field);
    }

    // --- Password ---

    [Theory]
    [InlineData("abcdefg1")]  // 8 chars, lowercase + digit
    [InlineData("securePass1")]
    [InlineData("a very long password that has 1 digit")]
    public void ValidatePassword_ValidPasswords_ReturnsSuccess(string password)
    {
        var result = RequestValidator.ValidatePassword(password);
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abcde1")]  // too short (6 chars)
    [InlineData("abcd1")]   // too short (5 chars)
    public void ValidatePassword_TooShortOrEmpty_ReturnsFailure(string? password)
    {
        var result = RequestValidator.ValidatePassword(password);
        Assert.False(result.IsValid);
        Assert.Equal("AUTH_002", result.Errors[0].Code);
    }

    [Fact]
    public void ValidatePassword_TooLong_ReturnsFailure()
    {
        var password = new string('a', 129);
        var result = RequestValidator.ValidatePassword(password);
        Assert.False(result.IsValid);
        Assert.Equal("AUTH_002", result.Errors[0].Code);
    }

    [Fact]
    public void ValidatePassword_NoLowercase_ReturnsFailure()
    {
        var result = RequestValidator.ValidatePassword("12345678");
        Assert.False(result.IsValid);
        Assert.Equal("AUTH_002", result.Errors[0].Code);
        Assert.Contains("lowercase", result.Errors[0].Message);
    }

    [Fact]
    public void ValidatePassword_NoDigit_ReturnsFailure()
    {
        var result = RequestValidator.ValidatePassword("abcdefgh");
        Assert.False(result.IsValid);
        Assert.Equal("AUTH_002", result.Errors[0].Code);
        Assert.Contains("digit", result.Errors[0].Message);
    }

    // --- Role ---

    [Theory]
    [InlineData("farmer")]
    [InlineData("trucker")]
    [InlineData("buyer")]
    [InlineData("Farmer")]  // case-insensitive
    [InlineData("BUYER")]
    public void ValidateRole_ValidRoles_ReturnsSuccess(string role)
    {
        var result = RequestValidator.ValidateRole(role);
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("admin")]
    [InlineData("unknown")]
    public void ValidateRole_InvalidRoles_ReturnsFailure(string? role)
    {
        var result = RequestValidator.ValidateRole(role);
        Assert.False(result.IsValid);
        Assert.Equal("AUTH_003", result.Errors[0].Code);
    }

    // --- Woreda ---

    [Theory]
    [InlineData("Adama")]
    [InlineData("Jimma")]
    [InlineData("አዳማ")]  // Amharic
    public void ValidateWoreda_Valid_ReturnsSuccess(string woreda)
    {
        var result = RequestValidator.ValidateWoreda(woreda);
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ValidateWoreda_Invalid_ReturnsFailure(string? woreda)
    {
        var result = RequestValidator.ValidateWoreda(woreda);
        Assert.False(result.IsValid);
        Assert.Equal("AUTH_004", result.Errors[0].Code);
    }

    [Fact]
    public void ValidateWoreda_TooLong_ReturnsFailure()
    {
        var woreda = new string('A', 101);
        var result = RequestValidator.ValidateWoreda(woreda);
        Assert.False(result.IsValid);
        Assert.Equal("AUTH_004", result.Errors[0].Code);
    }

    // --- OTP ---

    [Theory]
    [InlineData("123456")]
    [InlineData("000000")]
    [InlineData("999999")]
    public void ValidateOtpCode_Valid_ReturnsSuccess(string otp)
    {
        var result = RequestValidator.ValidateOtpCode(otp);
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]   // 5 digits
    [InlineData("1234567")] // 7 digits
    [InlineData("abcdef")]  // letters
    [InlineData("12345a")]  // mixed
    public void ValidateOtpCode_Invalid_ReturnsFailure(string? otp)
    {
        var result = RequestValidator.ValidateOtpCode(otp);
        Assert.False(result.IsValid);
        Assert.Equal("AUTH_009", result.Errors[0].Code);
    }

    // --- Register Request ---

    [Fact]
    public void ValidateRegisterRequest_ValidRequest_ReturnsSuccess()
    {
        var request = new RegisterRequest
        {
            PhoneNumber = "+251912345678",
            Password = "securePass1",
            Role = "farmer",
            Woreda = "Adama",
            FaydaVerificationToken = "some-valid-token",
            DisplayName = "Abebe Kebede",
            PreferredLanguage = "am"
        };

        var result = RequestValidator.ValidateRegisterRequest(request);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateRegisterRequest_NullBody_ReturnsFailure()
    {
        var result = RequestValidator.ValidateRegisterRequest(null);
        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void ValidateRegisterRequest_MultipleErrors_ReturnsAll()
    {
        var request = new RegisterRequest
        {
            PhoneNumber = "invalid",
            Password = "12",
            Role = "admin",
            Woreda = "",
            FaydaVerificationToken = ""
        };

        var result = RequestValidator.ValidateRegisterRequest(request);
        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 4); // phone, password, role, woreda
    }

    // --- Login Request ---

    [Fact]
    public void ValidateLoginRequest_ValidRequest_ReturnsSuccess()
    {
        var request = new LoginRequest
        {
            PhoneNumber = "+251912345678",
            Password = "securePass1"
        };

        var result = RequestValidator.ValidateLoginRequest(request);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateLoginRequest_NullBody_ReturnsFailure()
    {
        var result = RequestValidator.ValidateLoginRequest(null);
        Assert.False(result.IsValid);
    }

    // --- Fayda Verify Request ---

    [Fact]
    public void ValidateFaydaVerifyRequest_ValidRequest_ReturnsSuccess()
    {
        var request = new FaydaVerifyRequest
        {
            AuthorizationCode = "code-123",
            State = "state-abc",
            RedirectUri = "weredaconnect://fayda-callback"
        };

        var result = RequestValidator.ValidateFaydaVerifyRequest(request);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateFaydaVerifyRequest_NullBody_ReturnsFailure()
    {
        var result = RequestValidator.ValidateFaydaVerifyRequest(null);
        Assert.False(result.IsValid);
        Assert.Equal("AUTH_007", result.Errors[0].Code);
    }

    // --- Refresh Request ---

    [Fact]
    public void ValidateRefreshRequest_ValidRequest_ReturnsSuccess()
    {
        var request = new RefreshRequest
        {
            RefreshToken = "some-valid-refresh-token"
        };

        var result = RequestValidator.ValidateRefreshRequest(request);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateRefreshRequest_NullBody_ReturnsFailure()
    {
        var result = RequestValidator.ValidateRefreshRequest(null);
        Assert.False(result.IsValid);
        Assert.Equal("AUTH_017", result.Errors[0].Code);
    }

    [Fact]
    public void ValidateRefreshRequest_EmptyToken_ReturnsFailure()
    {
        var request = new RefreshRequest { RefreshToken = "" };
        var result = RequestValidator.ValidateRefreshRequest(request);
        Assert.False(result.IsValid);
        Assert.Equal("AUTH_017", result.Errors[0].Code);
    }

    // --- Logout Request ---

    [Fact]
    public void ValidateLogoutRequest_ValidRequest_ReturnsSuccess()
    {
        var request = new LogoutRequest { RefreshToken = "some-valid-token" };
        var result = RequestValidator.ValidateLogoutRequest(request);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateLogoutRequest_NullBody_ReturnsFailure()
    {
        var result = RequestValidator.ValidateLogoutRequest(null);
        Assert.False(result.IsValid);
        Assert.Equal("AUTH_007", result.Errors[0].Code);
    }

    [Fact]
    public void ValidateLogoutRequest_EmptyToken_ReturnsFailure()
    {
        var request = new LogoutRequest { RefreshToken = "" };
        var result = RequestValidator.ValidateLogoutRequest(request);
        Assert.False(result.IsValid);
        Assert.Equal("AUTH_017", result.Errors[0].Code);
    }

    [Fact]
    public void ValidateLogoutRequest_WhitespaceToken_ReturnsFailure()
    {
        var request = new LogoutRequest { RefreshToken = "   " };
        var result = RequestValidator.ValidateLogoutRequest(request);
        Assert.False(result.IsValid);
        Assert.Equal("AUTH_017", result.Errors[0].Code);
    }
}
