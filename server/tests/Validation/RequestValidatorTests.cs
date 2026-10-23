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
    [InlineData("00251912345678")]  // 00251 format
    [InlineData("00251911000000")]
    [InlineData("0912345678")]      // local 0 format
    [InlineData("0911000000")]
    public void ValidatePhoneNumber_ValidPhones_ReturnsSuccess(string phone)
    {
        var result = RequestValidator.ValidatePhoneNumber(phone);
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("+25191234567")]   // 8 digits after +251
    [InlineData("+2519123456789")] // 10 digits after +251
    [InlineData("+252912345678")]  // wrong country
    [InlineData("not-a-number")]
    [InlineData("091234567")]      // only 8 digits after 0
    [InlineData("09123456789")]    // 10 digits after 0
    [InlineData("0025191234567")]  // 8 digits after 00251
    public void ValidatePhoneNumber_InvalidPhones_ReturnsFailure(string? phone)
    {
        var result = RequestValidator.ValidatePhoneNumber(phone);
        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Equal("AUTH_001", result.Errors[0].Code);
        Assert.Equal("phoneNumber", result.Errors[0].Field);
    }

    // --- Phone Normalization ---

    [Theory]
    [InlineData("+251912345678", "+251912345678")]
    [InlineData("00251912345678", "+251912345678")]
    [InlineData("0912345678", "+251912345678")]
    [InlineData("+251 911 234 567", "+251911234567")]
    [InlineData("0911-234-567", "+251911234567")]
    public void NormalizePhoneNumber_ValidFormats_ReturnsCanonical(string input, string expected)
    {
        var result = RequestValidator.NormalizePhoneNumber(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("+252912345678")]
    [InlineData("+251abc234567")]
    public void NormalizePhoneNumber_InvalidInput_ReturnsNull(string? input)
    {
        var result = RequestValidator.NormalizePhoneNumber(input);
        Assert.Null(result);
    }

    [Fact]
    public void NormalizePhoneNumber_AllFormats_ProduceSameResult()
    {
        var e164 = RequestValidator.NormalizePhoneNumber("+251911234567");
        var international = RequestValidator.NormalizePhoneNumber("00251911234567");
        var local = RequestValidator.NormalizePhoneNumber("0911234567");

        Assert.Equal(e164, international);
        Assert.Equal(e164, local);
        Assert.Equal("+251911234567", e164);
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
        Assert.Equal(string.IsNullOrWhiteSpace(otp) ? "AUTH_009" : "AUTH_011", result.Errors[0].Code);
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ValidateRegisterRequest_MissingFaydaToken_ReturnsAuth021(string? token)
    {
        var request = new RegisterRequest
        {
            PhoneNumber = "+251912345678",
            Password = "securePass1",
            Role = "farmer",
            Woreda = "Adama",
            FaydaVerificationToken = token
        };

        var result = RequestValidator.ValidateRegisterRequest(request);
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("AUTH_021", error.Code);
        Assert.Equal("faydaVerificationToken", error.Field);
    }

    [Fact]
    public void ValidateRegisterRequest_BadFieldAndMissingFaydaToken_ReportsTheFieldFirst()
    {
        var request = new RegisterRequest
        {
            PhoneNumber = "invalid",
            Password = "securePass1",
            Role = "farmer",
            Woreda = "Adama"
        };

        var result = RequestValidator.ValidateRegisterRequest(request);
        Assert.Equal("phoneNumber", result.Errors[0].Field);
        Assert.Equal("AUTH_021", result.Errors[^1].Code);
    }

    // --- F-32: buyer-only identity fields ---

    [Fact]
    public void ValidateRegisterRequest_CompanyBuyer_WithCompanyName_ReturnsSuccess()
    {
        var request = new RegisterRequest
        {
            PhoneNumber = "+251912345678",
            Password = "securePass1",
            Role = "buyer",
            Woreda = "Adama",
            FaydaVerificationToken = "some-valid-token",
            BuyerType = "company",
            CompanyName = "Abebe Trading PLC"
        };

        var result = RequestValidator.ValidateRegisterRequest(request);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateRegisterRequest_PrivateBuyer_NoCompanyName_ReturnsSuccess()
    {
        var request = new RegisterRequest
        {
            PhoneNumber = "+251912345678",
            Password = "securePass1",
            Role = "buyer",
            Woreda = "Adama",
            FaydaVerificationToken = "some-valid-token",
            BuyerType = "private"
        };

        var result = RequestValidator.ValidateRegisterRequest(request);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateRegisterRequest_CompanyBuyer_MissingCompanyName_ReturnsFailure()
    {
        var request = new RegisterRequest
        {
            PhoneNumber = "+251912345678",
            Password = "securePass1",
            Role = "buyer",
            Woreda = "Adama",
            FaydaVerificationToken = "some-valid-token",
            BuyerType = "company"
        };

        var result = RequestValidator.ValidateRegisterRequest(request);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "companyName");
    }

    [Fact]
    public void ValidateRegisterRequest_PrivateBuyer_WithCompanyName_ReturnsFailure()
    {
        var request = new RegisterRequest
        {
            PhoneNumber = "+251912345678",
            Password = "securePass1",
            Role = "buyer",
            Woreda = "Adama",
            FaydaVerificationToken = "some-valid-token",
            BuyerType = "private",
            CompanyName = "Should Not Be Here"
        };

        var result = RequestValidator.ValidateRegisterRequest(request);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "companyName");
    }

    [Fact]
    public void ValidateRegisterRequest_NonBuyer_WithBuyerFields_ReturnsFailure()
    {
        var request = new RegisterRequest
        {
            PhoneNumber = "+251912345678",
            Password = "securePass1",
            Role = "farmer",
            Woreda = "Adama",
            FaydaVerificationToken = "some-valid-token",
            BuyerType = "company",
            CompanyName = "Nope PLC"
        };

        var result = RequestValidator.ValidateRegisterRequest(request);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "buyerType");
    }

    [Fact]
    public void ValidateRegisterRequest_InvalidBuyerType_ReturnsFailure()
    {
        var request = new RegisterRequest
        {
            PhoneNumber = "+251912345678",
            Password = "securePass1",
            Role = "buyer",
            Woreda = "Adama",
            FaydaVerificationToken = "some-valid-token",
            BuyerType = "enterprise"
        };

        var result = RequestValidator.ValidateRegisterRequest(request);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Field == "buyerType");
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


    // --- ACC-02: the deleted-user sentinel is not a display name ---

    [Theory]
    [InlineData("Deleted user")]
    [InlineData("  deleted USER ")]
    public void ValidateRegisterRequest_SentinelDisplayName_IsRefused(string displayName)
    {
        var request = new RegisterRequest
        {
            PhoneNumber = "+251912345678",
            Password = "securePass1",
            Role = "farmer",
            Woreda = "Adama",
            FaydaVerificationToken = "some-valid-token",
            DisplayName = displayName,
            PreferredLanguage = "am"
        };

        var result = RequestValidator.ValidateRegisterRequest(request);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("displayName", error.Field);
        Assert.Equal("AUTH_007", error.Code);
    }

    [Theory]
    [InlineData("Deleted")]
    [InlineData("Deleted users")]
    public void ValidateRegisterRequest_NameThatOnlyLooksLikeTheSentinel_IsAccepted(string displayName)
    {
        var request = new RegisterRequest
        {
            PhoneNumber = "+251912345678",
            Password = "securePass1",
            Role = "farmer",
            Woreda = "Adama",
            FaydaVerificationToken = "some-valid-token",
            DisplayName = displayName,
            PreferredLanguage = "am"
        };

        Assert.True(RequestValidator.ValidateRegisterRequest(request).IsValid);
    }
}
