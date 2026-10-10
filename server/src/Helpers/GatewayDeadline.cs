namespace WeredaConnect.Server.Helpers;

/// <summary>
/// ESC-45 (D6): the time by which every Telebirr HTTP call of the current invocation must be over,
/// so a call never outlives the Lambda (the escrow Lambda's 30 s, a sweep's 5 minutes) and the writes
/// after it (slot outcome, audit, settlement status, parking) always run. Set once per invocation by
/// the handler and read by the gateway; it flows across <c>await</c> (<see cref="AsyncLocal{T}"/>), so
/// no gateway signature changes. AOT-safe.
/// </summary>
public static class GatewayDeadline
{
    private static readonly AsyncLocal<DateTimeOffset?> Current = new();

    /// <summary>Time left before the deadline (negative once passed), or null when none is set.</summary>
    public static TimeSpan? Remaining => Current.Value is { } deadline ? deadline - DateTimeOffset.UtcNow : null;

    /// <summary>
    /// The deadline is the invocation's remaining time minus <paramref name="reserve"/>. A context
    /// whose remaining time is not positive (a test context) sets no deadline: only the HTTP client's
    /// own timeout applies then.
    /// </summary>
    public static IDisposable Begin(ILambdaContext context, TimeSpan reserve)
        => Begin(context.RemainingTime > TimeSpan.Zero
            ? DateTimeOffset.UtcNow + context.RemainingTime - reserve
            : null);

    /// <summary>Set <paramref name="deadline"/> (null: none) until the returned scope is disposed.</summary>
    public static IDisposable Begin(DateTimeOffset? deadline)
    {
        var previous = Current.Value;
        Current.Value = deadline;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly DateTimeOffset? _previous;
        private bool _disposed;

        public Scope(DateTimeOffset? previous) => _previous = previous;

        public void Dispose()
        {
            if (!_disposed)
            {
                Current.Value = _previous;
                _disposed = true;
            }
        }
    }
}
