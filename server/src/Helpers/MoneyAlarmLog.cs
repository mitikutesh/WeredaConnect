namespace WeredaConnect.Server.Helpers;

/// <summary>
/// Plain-text log tokens behind the money alarms (ESC-13). Each method returns one line
/// that a CloudWatch metric filter in <c>infra/lib/stacks/monitoring-stack.ts</c> counts.
/// The alarm only counts lines, so the producer decides what is a failure and what is
/// overdue. AOT-safe: string formatting only, no JSON, no reflection.
/// </summary>
/// <remarks>
/// <para>
/// Line formats (one line each; ids are reduced to <c>[A-Za-z0-9-_.:]</c>, cut at 64
/// characters, empty becomes <c>-</c>):
/// </para>
/// <code>
/// ESCROW_SETTLEMENT_FAILED escrowId=&lt;id&gt; action=&lt;action&gt; outcome=&lt;failed|unknown&gt; code=&lt;code&gt;
/// MANUAL_PAYOUT_OVERDUE escrowId=&lt;id&gt; ref=&lt;ref&gt; ageHours=&lt;n&gt;
/// ADMIN_QUEUE_OVERDUE orderId=&lt;id&gt; reason=&lt;code&gt; ageHours=&lt;n&gt;
/// </code>
/// <para>
/// <c>outcome=unknown</c> means the gateway may already have moved the money (a timeout,
/// a lost answer, or an exception mid-settlement); <c>failed</c> is a definite refusal.
/// </para>
/// <para>
/// The two queue lines are the producer API for the manual payout queue (ESC-02) and the
/// admin stuck-order queue (ESC-06). Both must be logged from the escrow-cron Lambda, once
/// per hourly run for each item older than <see cref="QueueAlarmAfter"/>, because the
/// metric filters sit on that log group. <c>EscrowCronFunction.ReportOverdueManualPayoutsAsync</c>
/// logs <c>MANUAL_PAYOUT_OVERDUE</c> per pending leg with <c>ref=&lt;op&gt;:&lt;leg&gt;</c>;
/// the <c>ADMIN_QUEUE_OVERDUE</c> producer comes with ESC-06. Contract:
/// <c>specs/contracts/escrow-protocol.md</c>, "Additions in 1.8.1".
/// </para>
/// <para>
/// The overdue line is written with <c>LogWarning</c> and the failure lines with
/// <c>LogError</c> or stdout. Raising escrow-cron's log level above Warning (the runtime's
/// handler log level, or a Lambda application log level) would silence the manual payout
/// alarm without any other sign.
/// </para>
/// </remarks>
public static class MoneyAlarmLog
{
    public const string SettlementFailedToken = "ESCROW_SETTLEMENT_FAILED";
    public const string ManualPayoutOverdueToken = "MANUAL_PAYOUT_OVERDUE";
    public const string AdminQueueOverdueToken = "ADMIN_QUEUE_OVERDUE";

    /// <summary>The gateway definitely refused the settlement; no money moved.</summary>
    public const string OutcomeFailed = "failed";

    /// <summary>The gateway may have moved the money; check before any manual settlement.</summary>
    public const string OutcomeUnknown = "unknown";

    /// <summary>A queue item older than this is overdue and gets one line per sweep.</summary>
    public static readonly TimeSpan QueueAlarmAfter = TimeSpan.FromHours(24);

    private const int MaxFieldLength = 64;

    /// <summary>
    /// Escrow-audit actions that count as a settlement failure. Not counted on purpose:
    /// <c>release_blocked_kyc</c> (a known wait for the farmer to verify), a lock-held
    /// <c>ESCROW_INVALID_TRANSITION</c> answer (no gateway call was made) and a failed
    /// <c>complete</c> bookkeeping step (the money already moved).
    /// </summary>
    private static readonly HashSet<string> SettlementFailureAuditActions = new(StringComparer.Ordinal)
    {
        "release_failed", "refund_failed", "trucker_release_failed"
    };

    public static bool IsSettlementFailureAuditAction(string? action)
        => action != null && SettlementFailureAuditActions.Contains(action);

    public static string SettlementFailed(string? escrowId, string? action, string? outcome, string? code)
        => $"{SettlementFailedToken} escrowId={Safe(escrowId)} action={Safe(action)} outcome={Safe(outcome)} code={Safe(code)}";

    public static string ManualPayoutOverdue(string? escrowId, string? reference, int ageHours)
        => $"{ManualPayoutOverdueToken} escrowId={Safe(escrowId)} ref={Safe(reference)} ageHours={Math.Max(0, ageHours)}";

    public static string AdminQueueOverdue(string? orderId, string? reason, int ageHours)
        => $"{AdminQueueOverdueToken} orderId={Safe(orderId)} reason={Safe(reason)} ageHours={Math.Max(0, ageHours)}";

    /// <summary>
    /// Same rule as the webhook's log sanitizer: keep <c>[A-Za-z0-9-_.:]</c>, cut at 64
    /// characters, empty becomes <c>-</c>. A field can never hold a space or a newline, so a
    /// value cannot add a second token to the line or break the metric filter.
    /// </summary>
    private static string Safe(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "-";
        }

        var safe = new string(value
            .Where(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                or '-' or '_' or '.' or ':')
            .Take(MaxFieldLength)
            .ToArray());
        return safe.Length == 0 ? "-" : safe;
    }
}
