namespace WeredaConnect.Server.Services;

/// <summary>
/// Error detail for auth operations. Carries HTTP status, AUTH_* code, message, and optional field.
/// </summary>
public sealed record AuthResultError
{
    public required int HttpStatus { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? Field { get; init; }
    public int? RetryAfterSeconds { get; init; }
}

/// <summary>
/// Generic result type for auth operations. Replaces exceptions for expected failures
/// (invalid credentials, phone exists, OTP expired). Explicit control flow, no performance cost.
/// </summary>
public sealed class AuthResult<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public AuthResultError? Error { get; }

    private AuthResult(T value)
    {
        IsSuccess = true;
        Value = value;
    }

    private AuthResult(AuthResultError error)
    {
        IsSuccess = false;
        Error = error;
    }

    public static AuthResult<T> Success(T value) => new(value);

    public static AuthResult<T> Failure(int httpStatus, string code, string message, string? field = null, int? retryAfterSeconds = null) =>
        new(new AuthResultError
        {
            HttpStatus = httpStatus,
            Code = code,
            Message = message,
            Field = field,
            RetryAfterSeconds = retryAfterSeconds
        });

    public static AuthResult<T> Failure(AuthResultError error) => new(error);
}
