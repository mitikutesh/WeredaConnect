namespace WeredaConnect.Server.Services;

/// <summary>
/// Error detail for escrow operations. Carries HTTP status, ESCROW_* code, message, and optional field.
/// </summary>
public sealed record EscrowResultError
{
    public required int HttpStatus { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? Field { get; init; }
}

/// <summary>
/// Generic result type for escrow operations. Replaces exceptions for expected failures
/// (invalid state transitions, Telebirr errors, validation failures).
/// Follows the AuthResult&lt;T&gt; pattern: explicit control flow, no performance cost.
/// Adds a Duplicate factory method for idempotent duplicate detection.
/// </summary>
public sealed class EscrowResult<T>
{
    public bool IsSuccess { get; }
    public bool IsDuplicate { get; }
    public T? Value { get; }
    public EscrowResultError? Error { get; }

    private EscrowResult(T value, bool isDuplicate = false)
    {
        IsSuccess = true;
        IsDuplicate = isDuplicate;
        Value = value;
    }

    private EscrowResult(EscrowResultError error)
    {
        IsSuccess = false;
        Error = error;
    }

    public static EscrowResult<T> Success(T value) => new(value);

    public static EscrowResult<T> Duplicate(T value) => new(value, isDuplicate: true);

    public static EscrowResult<T> Failure(int httpStatus, string code, string message, string? field = null) =>
        new(new EscrowResultError
        {
            HttpStatus = httpStatus,
            Code = code,
            Message = message,
            Field = field
        });

    public static EscrowResult<T> Failure(EscrowResultError error) => new(error);
}
