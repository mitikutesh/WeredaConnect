namespace WeredaConnect.Server.Helpers;

/// <summary>
/// Plain-text log tokens behind the money alarms (ESC-13). Each method returns one line
/// that a CloudWatch metric filter in <c>infra/lib/stacks/monitoring-stack.ts</c> counts.
/// The alarm only counts lines, so the producer decides what is a failure and what is
/// overdue. AOT-safe: string formatting only, no JSON, no reflection.
/// </summary>
/// <remarks>
/// <para>
/// Line formats (one line each; ids go through <see cref="LogId"/>, the other fields keep
/// <c>[A-Za-z0-9-_.:]</c>; every field is cut at 64 characters and an empty one is <c>-</c>):
/// </para>
/// <code>
/// ESCROW_SETTLEMENT_FAILED escrowId=&lt;id&gt; action=&lt;action&gt; outcome=&lt;failed|unknown&gt; code=&lt;code&gt;
/// MANUAL_PAYOUT_OVERDUE escrowId=&lt;id&gt; ref=&lt;ref&gt; ageHours=&lt;n&gt;
/// ADMIN_QUEUE_OVERDUE orderId=&lt;id&gt; reason=&lt;code&gt; ageHours=&lt;n&gt;
/// DISPUTE_VERDICT_ORDER_MISMATCH escrowId=&lt;id&gt; op=&lt;release|refund&gt; legs=&lt;legs&gt;
/// DISPUTE_VERDICT_SETTLEMENT_BLOCKED escrowId=&lt;id&gt; op=&lt;release|refund&gt; legs=&lt;legs&gt;
/// TRUCKER_PAYOUT_OVERDUE escrowId=&lt;id&gt; reason=&lt;reason&gt; ageHours=&lt;n&gt;
/// DISPUTE_NO_ESCROW orderId=&lt;id&gt; deliveryId=&lt;id&gt;
/// </code>
/// <para>
/// The trucker line (ESC-04) is logged by escrow-cron's trucker retry, once per hourly run for
/// each released escrow whose trucker leg is still owed and not queued after
/// <see cref="QueueAlarmAfter"/>; <c>reason</c> is the payout block (<c>phone_unverified</c>,
/// <c>fayda_required</c>, <c>profile_missing</c>, <c>no_trucker</c>, <c>trucker_mismatch</c>) or why
/// the retry did not pay (<c>gateway_refused</c>, <c>queue_failed</c>, <c>in_flight</c>, ...).
/// </para>
/// <para>
/// The mismatch line (ESC-03) is logged by the escrow service each time it refuses to settle a
/// dispute verdict whose order or delivery cannot follow it: from escrow-cron on the hourly
/// dispute sweep and from the escrow Lambda on an admin release or refund. <c>legs</c> is the
/// mirror's failed-legs text, defanged, with <c>,</c> turned into <c>.</c>. The blocked line (ESC-03)
/// is logged each time a settlement fails closed because the order or delivery could not be read,
/// or a <c>buyer_wins</c> order could not be cancelled or restocked first; its alarm counts only
/// escrow-cron and fires when the sweep is blocked in three of four hourly periods.
/// </para>
/// <para>
/// <c>outcome=unknown</c> means the gateway may already have moved the money (a timeout,
/// a lost answer, or an exception after the settlement lock was taken); <c>failed</c> is a
/// definite refusal, or a failure before the lock. ESC-05: every unknown outcome ends in the
/// manual payout queue with the lock held, and a separate alarm counts the unknown lines.
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
/// Spoofing: a metric filter counts any line of its log group that contains the token, so a
/// client must never be able to put a token into any line there. Every alarm token contains
/// <c>_</c> (an infra test enforces it), so every client-influenced value written to the
/// escrow, escrow-cron, sync or webhook log group goes through <see cref="LogId"/> (ids,
/// path parameters, headers) or <see cref="LogText"/> (messages that may echo input), which
/// turn <c>_</c> into <c>-</c>. Action, outcome, code, ref and reason are server values.
/// Two sources stay unsanitized because no client reaches them: escrow-cron's <c>Cron action</c>
/// line (escrow-cron has only EventBridge triggers, so only an IAM principal picks the action)
/// and the message the Lambda runtime prints for an unhandled exception (the handlers catch
/// client-reachable exceptions and log a type name or <see cref="LogText"/>).
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
    public const string DisputeVerdictOrderMismatchToken = "DISPUTE_VERDICT_ORDER_MISMATCH";
    public const string DisputeVerdictSettlementBlockedToken = "DISPUTE_VERDICT_SETTLEMENT_BLOCKED";
    public const string TruckerPayoutOverdueToken = "TRUCKER_PAYOUT_OVERDUE";
    public const string DisputeNoEscrowToken = "DISPUTE_NO_ESCROW";

    /// <summary>The gateway definitely refused the settlement; no money moved.</summary>
    public const string OutcomeFailed = "failed";

    /// <summary>The gateway may have moved the money; check before any manual settlement.</summary>
    public const string OutcomeUnknown = "unknown";

    /// <summary>
    /// ESC-05: <see cref="Exception.Data"/> key set on an exception that escaped a release or
    /// refund after it took the settlement lock (the gateway may have been called).
    /// </summary>
    public const string SettlementInFlightKey = "WeredaConnect.SettlementInFlight";

    /// <summary>ESC-05: mark an exception that escapes a settlement after its lock was taken.</summary>
    public static void MarkSettlementInFlight(Exception ex) => ex.Data[SettlementInFlightKey] = true;

    /// <summary>
    /// ESC-05: the outcome of an exception caught around a settlement: <c>unknown</c> only when it
    /// escaped after the settlement lock was taken and before the settled status was written;
    /// before the lock no gateway call can have happened, and after the status write the payout is
    /// on record, so <c>failed</c>.
    /// </summary>
    public static string ExceptionOutcome(Exception ex)
        => ex.Data.Contains(SettlementInFlightKey) ? OutcomeUnknown : OutcomeFailed;

    /// <summary>A queue item older than this is overdue and gets one line per sweep.</summary>
    public static readonly TimeSpan QueueAlarmAfter = TimeSpan.FromHours(24);

    private const int MaxFieldLength = 64;
    private const int MaxTextLength = 200;

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
        => $"{SettlementFailedToken} escrowId={LogId(escrowId)} action={Safe(action)} outcome={Safe(outcome)} code={Safe(code)}";

    public static string ManualPayoutOverdue(string? escrowId, string? reference, int ageHours)
        => $"{ManualPayoutOverdueToken} escrowId={LogId(escrowId)} ref={Safe(reference)} ageHours={Math.Max(0, ageHours)}";

    public static string AdminQueueOverdue(string? orderId, string? reason, int ageHours)
        => $"{AdminQueueOverdueToken} orderId={LogId(orderId)} reason={Safe(reason)} ageHours={Math.Max(0, ageHours)}";

    /// <summary>ESC-04: a released escrow's trucker leg is still owed (and not queued) after <see cref="QueueAlarmAfter"/>.</summary>
    public static string TruckerPayoutOverdue(string? escrowId, string? reason, int ageHours)
        => $"{TruckerPayoutOverdueToken} escrowId={LogId(escrowId)} reason={Safe(reason)} ageHours={Math.Max(0, ageHours)}";

    /// <summary>ESC-03: a dispute verdict was not settled because its order or delivery cannot follow it.</summary>
    public static string DisputeVerdictOrderMismatch(string? escrowId, string? op, string? legs)
        => $"{DisputeVerdictOrderMismatchToken} escrowId={LogId(escrowId)} op={Safe(op)} legs={LogId(legs?.Replace(',', '.'))}";

    /// <summary>ESC-03: a dispute verdict's settlement failed closed (unreadable order, or a cancel or restock that did not land).</summary>
    public static string DisputeVerdictSettlementBlocked(string? escrowId, string? op, string? legs)
        => $"{DisputeVerdictSettlementBlockedToken} escrowId={LogId(escrowId)} op={Safe(op)} legs={LogId(legs?.Replace(',', '.'))}";

    /// <summary>
    /// ESC-07: a delivery of a funded order was disputed but no escrow holds its money, so the
    /// dispute was refused. Logged by the sync Lambda (the order-escrow problems alarm counts it).
    /// </summary>
    public static string DisputeNoEscrow(string? orderId, string? deliveryId)
        => $"{DisputeNoEscrowToken} orderId={LogId(orderId)} deliveryId={LogId(deliveryId)}";

    /// <summary>
    /// A client-chosen or client-influenced id (entity id, path parameter, header, an escrow id
    /// that embeds an order id) for any log line in a log group a money alarm filters: keeps
    /// <c>[A-Za-z0-9-.:]</c>, turns <c>_</c> into <c>-</c>, cuts at 64 characters, empty becomes
    /// <c>-</c>. No space, newline or underscore survives, so the value cannot spell an alarm token.
    /// </summary>
    public static string LogId(string? value) => Safe(Defang(value));

    /// <summary>
    /// Free text that may echo client input (an exception or error message) for the same log
    /// groups: control characters become spaces, <c>_</c> becomes <c>-</c>, cut at 200 characters,
    /// empty becomes <c>-</c>.
    /// </summary>
    public static string LogText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "-";
        }

        var text = new string(Defang(value)!
            .Select(c => char.IsControl(c) ? ' ' : c)
            .Take(MaxTextLength)
            .ToArray());
        return string.IsNullOrWhiteSpace(text) ? "-" : text;
    }

    /// <summary>
    /// <c>_</c> becomes <c>-</c>, nothing else changes. Every alarm token contains <c>_</c>, so a
    /// defanged value cannot contain one (write it after a space or <c>=</c>, never glued to a
    /// server prefix). For values their writer already escapes (the JSON audit line).
    /// </summary>
    public static string? Defang(string? value) => value?.Replace('_', '-');

    /// <summary>
    /// Keep <c>[A-Za-z0-9-_.:]</c>, cut at 64 characters, empty becomes <c>-</c>. A field can
    /// never hold a space or a newline, so it cannot add a second field or break the line.
    /// Only for server values (actions, codes, references); client-influenced ids use <see cref="LogId"/>.
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
