using System.Text.RegularExpressions;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Validation;

/// <summary>
/// Validation result for request validation.
/// </summary>
public sealed record ValidationResult
{
    public required bool IsValid { get; init; }
    public IReadOnlyList<ValidationError> Errors { get; init; } = [];

    public static ValidationResult Success() => new() { IsValid = true };

    public static ValidationResult Failure(ValidationError error) =>
        new() { IsValid = false, Errors = [error] };

    public static ValidationResult Failure(IEnumerable<ValidationError> errors) =>
        new() { IsValid = false, Errors = errors.ToList() };
}

/// <summary>
/// Single validation error with field, code, and message.
/// </summary>
public sealed record ValidationError
{
    public required string Field { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// AOT-safe request validator using GeneratedRegex for compile-time regex.
/// Validates all auth request types per auth-api.md contract.
/// </summary>
public static partial class RequestValidator
{
    // Ethiopian phone: +251 followed by 9 digits
    [GeneratedRegex(@"^\+251[0-9]{9}$")]
    private static partial Regex PhoneRegex();

    // OTP: exactly 6 digits
    [GeneratedRegex(@"^[0-9]{6}$")]
    private static partial Regex OtpRegex();

    /// <summary>
    /// Normalize an Ethiopian phone number to canonical E.164 format (+251XXXXXXXXX).
    /// Accepts: +251XXXXXXXXX, 00251XXXXXXXXX, 0XXXXXXXXX.
    /// Returns null if the input cannot be recognized as a valid Ethiopian phone number.
    /// </summary>
    public static string? NormalizePhoneNumber(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var cleaned = input.Trim().Replace(" ", "").Replace("-", "");

        // Format 1: +251XXXXXXXXX (already E.164, 13 chars)
        if (cleaned.StartsWith("+251") && cleaned.Length == 13)
        {
            return PhoneRegex().IsMatch(cleaned) ? cleaned : null;
        }

        // Format 2: 00251XXXXXXXXX (14 chars)
        if (cleaned.StartsWith("00251") && cleaned.Length == 14)
        {
            var normalized = "+251" + cleaned[5..];
            return PhoneRegex().IsMatch(normalized) ? normalized : null;
        }

        // Format 3: 0XXXXXXXXX (local, 10 chars)
        if (cleaned.StartsWith("0") && cleaned.Length == 10)
        {
            var normalized = "+251" + cleaned[1..];
            return PhoneRegex().IsMatch(normalized) ? normalized : null;
        }

        return null;
    }

    private static readonly HashSet<string> ValidRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "farmer", "trucker", "buyer"
    };

    private static readonly HashSet<string> ValidLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "am", "om"
    };

    /// <summary>
    /// Validate Ethiopian phone number format.
    /// Accepts: +251XXXXXXXXX, 00251XXXXXXXXX, 0XXXXXXXXX.
    /// </summary>
    public static ValidationResult ValidatePhoneNumber(string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return ValidationResult.Failure(new ValidationError
            {
                Field = "phoneNumber",
                Code = "AUTH_001",
                Message = "Phone number is required."
            });
        }

        if (NormalizePhoneNumber(phoneNumber) == null)
        {
            return ValidationResult.Failure(new ValidationError
            {
                Field = "phoneNumber",
                Code = "AUTH_001",
                Message = "Invalid phone number format. Use +251XXXXXXXXX, 00251XXXXXXXXX, or 0XXXXXXXXX."
            });
        }

        return ValidationResult.Success();
    }

    /// <summary>
    /// Validate password: 8-128 characters with at least one lowercase letter and one digit.
    /// </summary>
    public static ValidationResult ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return ValidationResult.Failure(new ValidationError
            {
                Field = "password",
                Code = "AUTH_002",
                Message = "Password is required."
            });
        }

        if (password.Length < 8)
        {
            return ValidationResult.Failure(new ValidationError
            {
                Field = "password",
                Code = "AUTH_002",
                Message = "Password must be at least 8 characters."
            });
        }

        if (password.Length > 128)
        {
            return ValidationResult.Failure(new ValidationError
            {
                Field = "password",
                Code = "AUTH_002",
                Message = "Password must not exceed 128 characters."
            });
        }

        if (!password.Any(char.IsLower))
        {
            return ValidationResult.Failure(new ValidationError
            {
                Field = "password",
                Code = "AUTH_002",
                Message = "Password must contain at least one lowercase letter."
            });
        }

        if (!password.Any(char.IsDigit))
        {
            return ValidationResult.Failure(new ValidationError
            {
                Field = "password",
                Code = "AUTH_002",
                Message = "Password must contain at least one digit."
            });
        }

        return ValidationResult.Success();
    }

    /// <summary>
    /// Validate user role: farmer, trucker, or buyer.
    /// </summary>
    public static ValidationResult ValidateRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return ValidationResult.Failure(new ValidationError
            {
                Field = "role",
                Code = "AUTH_003",
                Message = "Role is required."
            });
        }

        if (!ValidRoles.Contains(role))
        {
            return ValidationResult.Failure(new ValidationError
            {
                Field = "role",
                Code = "AUTH_003",
                Message = "Invalid role. Must be farmer, trucker, or buyer."
            });
        }

        return ValidationResult.Success();
    }

    /// <summary>
    /// Validate woreda: non-empty, max 100 characters.
    /// </summary>
    public static ValidationResult ValidateWoreda(string? woreda)
    {
        if (string.IsNullOrWhiteSpace(woreda))
        {
            return ValidationResult.Failure(new ValidationError
            {
                Field = "woreda",
                Code = "AUTH_004",
                Message = "Woreda is required."
            });
        }

        if (woreda.Length > 100)
        {
            return ValidationResult.Failure(new ValidationError
            {
                Field = "woreda",
                Code = "AUTH_004",
                Message = "Woreda must not exceed 100 characters."
            });
        }

        return ValidationResult.Success();
    }

    /// <summary>
    /// Validate OTP code: exactly 6 digits.
    /// </summary>
    public static ValidationResult ValidateOtpCode(string? otpCode)
    {
        if (string.IsNullOrWhiteSpace(otpCode))
        {
            return ValidationResult.Failure(new ValidationError
            {
                Field = "otpCode",
                Code = "AUTH_009",
                Message = "OTP code is required."
            });
        }

        if (!OtpRegex().IsMatch(otpCode))
        {
            return ValidationResult.Failure(new ValidationError
            {
                Field = "otpCode",
                Code = "AUTH_009",
                Message = "Invalid OTP format. Must be exactly 6 digits."
            });
        }

        return ValidationResult.Success();
    }

    /// <summary>
    /// Combined validation for POST /auth/register request.
    /// </summary>
    public static ValidationResult ValidateRegisterRequest(RegisterRequest? request)
    {
        if (request is null)
        {
            return ValidationResult.Failure(new ValidationError
            {
                Field = "body",
                Code = "AUTH_007",
                Message = "Request body is required."
            });
        }

        var errors = new List<ValidationError>();

        var phoneResult = ValidatePhoneNumber(request.PhoneNumber);
        if (!phoneResult.IsValid)
        {
            errors.AddRange(phoneResult.Errors);
        }

        var passwordResult = ValidatePassword(request.Password);
        if (!passwordResult.IsValid)
        {
            errors.AddRange(passwordResult.Errors);
        }

        var roleResult = ValidateRole(request.Role);
        if (!roleResult.IsValid)
        {
            errors.AddRange(roleResult.Errors);
        }

        var weredaResult = ValidateWoreda(request.Woreda);
        if (!weredaResult.IsValid)
        {
            errors.AddRange(weredaResult.Errors);
        }

        if (request.DisplayName is not null && request.DisplayName.Length > 100)
        {
            errors.Add(new ValidationError
            {
                Field = "displayName",
                Code = "AUTH_007",
                Message = "Display name must not exceed 100 characters."
            });
        }

        if (!ValidLanguages.Contains(request.PreferredLanguage))
        {
            errors.Add(new ValidationError
            {
                Field = "preferredLanguage",
                Code = "AUTH_007",
                Message = "Invalid language. Must be en, am, or om."
            });
        }

        // F-32: buyer-only identity fields. buyerType ∈ {private, company};
        // companyName required iff buyerType == company; both must be absent for
        // non-buyer roles.
        var isBuyer = string.Equals(request.Role, "buyer", StringComparison.OrdinalIgnoreCase);
        var isCompany = string.Equals(request.BuyerType, "company", StringComparison.OrdinalIgnoreCase);
        var isPrivate = string.Equals(request.BuyerType, "private", StringComparison.OrdinalIgnoreCase);

        if (!isBuyer)
        {
            if (request.BuyerType != null || request.CompanyName != null)
            {
                errors.Add(new ValidationError
                {
                    Field = "buyerType",
                    Code = "AUTH_007",
                    Message = "buyerType and companyName are only valid for buyer registrations."
                });
            }
        }
        else
        {
            if (request.BuyerType != null && !isPrivate && !isCompany)
            {
                errors.Add(new ValidationError
                {
                    Field = "buyerType",
                    Code = "AUTH_007",
                    Message = "buyerType must be 'private' or 'company'."
                });
            }

            if (isCompany && string.IsNullOrWhiteSpace(request.CompanyName))
            {
                errors.Add(new ValidationError
                {
                    Field = "companyName",
                    Code = "AUTH_007",
                    Message = "companyName is required when buyerType is 'company'."
                });
            }

            if (!isCompany && !string.IsNullOrWhiteSpace(request.CompanyName))
            {
                errors.Add(new ValidationError
                {
                    Field = "companyName",
                    Code = "AUTH_007",
                    Message = "companyName is only valid when buyerType is 'company'."
                });
            }

            if (request.CompanyName is { Length: > 100 })
            {
                errors.Add(new ValidationError
                {
                    Field = "companyName",
                    Code = "AUTH_007",
                    Message = "companyName must not exceed 100 characters."
                });
            }
        }

        return errors.Count > 0
            ? ValidationResult.Failure(errors)
            : ValidationResult.Success();
    }

    /// <summary>
    /// Combined validation for POST /auth/login request.
    /// </summary>
    public static ValidationResult ValidateLoginRequest(LoginRequest? request)
    {
        if (request is null)
        {
            return ValidationResult.Failure(new ValidationError
            {
                Field = "body",
                Code = "AUTH_007",
                Message = "Request body is required."
            });
        }

        var errors = new List<ValidationError>();

        var phoneResult = ValidatePhoneNumber(request.PhoneNumber);
        if (!phoneResult.IsValid)
        {
            errors.AddRange(phoneResult.Errors);
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            errors.Add(new ValidationError
            {
                Field = "password",
                Code = "AUTH_002",
                Message = "Password is required."
            });
        }

        return errors.Count > 0
            ? ValidationResult.Failure(errors)
            : ValidationResult.Success();
    }

    /// <summary>
    /// Combined validation for POST /auth/fayda-verify request.
    /// </summary>
    public static ValidationResult ValidateFaydaVerifyRequest(FaydaVerifyRequest? request)
    {
        if (request is null)
        {
            return ValidationResult.Failure(new ValidationError
            {
                Field = "body",
                Code = "AUTH_007",
                Message = "Request body is required."
            });
        }

        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(request.AuthorizationCode))
        {
            errors.Add(new ValidationError
            {
                Field = "authorizationCode",
                Code = "FAYDA_001",
                Message = "Authorization code is required."
            });
        }

        if (string.IsNullOrWhiteSpace(request.State))
        {
            errors.Add(new ValidationError
            {
                Field = "state",
                Code = "FAYDA_002",
                Message = "State parameter is required."
            });
        }

        if (string.IsNullOrWhiteSpace(request.RedirectUri))
        {
            errors.Add(new ValidationError
            {
                Field = "redirectUri",
                Code = "AUTH_007",
                Message = "Redirect URI is required."
            });
        }

        return errors.Count > 0
            ? ValidationResult.Failure(errors)
            : ValidationResult.Success();
    }

    /// <summary>
    /// Combined validation for POST /auth/refresh request.
    /// </summary>
    public static ValidationResult ValidateRefreshRequest(RefreshRequest? request)
    {
        if (request is null)
        {
            return ValidationResult.Failure(new ValidationError
            {
                Field = "body",
                Code = "AUTH_017",
                Message = "Request body is required."
            });
        }

        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return ValidationResult.Failure(new ValidationError
            {
                Field = "refreshToken",
                Code = "AUTH_017",
                Message = "Refresh token is required."
            });
        }

        return ValidationResult.Success();
    }

    /// <summary>
    /// Combined validation for POST /auth/logout request.
    /// </summary>
    public static ValidationResult ValidateLogoutRequest(LogoutRequest? request)
    {
        if (request is null)
        {
            return ValidationResult.Failure(new ValidationError
            {
                Field = "body",
                Code = "AUTH_007",
                Message = "Request body is required."
            });
        }

        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return ValidationResult.Failure(new ValidationError
            {
                Field = "refreshToken",
                Code = "AUTH_017",
                Message = "Refresh token is required."
            });
        }

        return ValidationResult.Success();
    }
}
