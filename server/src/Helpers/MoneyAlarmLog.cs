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
/// <c>[A-Za-z0-9-_.:]</c>; every field is cut at 64 characters, the propagation line's legs at 200,
/// and an empty one is <c>-</c>):
/// </para>
/// <code>
/// ESCROW_SETTLEMENT_FAILED escrowId=&lt;id&gt; action=&lt;action&gt; outcome=&lt;failed|unknown&gt; code=&lt;code&gt;
/// MANUAL_PAYOUT_OVERDUE escrowId=&lt;id&gt; ref=&lt;ref&gt; ageHours=&lt;n&gt;
/// ADMIN_QUEUE_OVERDUE orderId=&lt;id&gt; reason=&lt;code&gt; ageHours=&lt;n&gt; [escrowId=&lt;id&gt;]
/// DISPUTE_VERDICT_ORDER_MISMATCH escrowId=&lt;id&gt; op=&lt;release|refund&gt; legs=&lt;legs&gt;
/// DISPUTE_VERDICT_SETTLEMENT_BLOCKED escrowId=&lt;id&gt; op=&lt;release|refund&gt; legs=&lt;legs&gt;
/// TRUCKER_PAYOUT_OVERDUE escrowId=&lt;id&gt; reason=&lt;reason&gt; ageHours=&lt;n&gt;
/// DISPUTE_NO_ESCROW orderId=&lt;id&gt; deliveryId=&lt;id&gt;
/// ORDER_MONEY_MISMATCH orderId=&lt;id&gt; escrowId=&lt;id&gt; orderStatus=&lt;s&gt; escrowStatus=&lt;s&gt; reason=&lt;reason&gt;
/// RISK_SETTLEMENT_SKIPPED escrowId=&lt;id&gt; sweep=&lt;auto-release|auto-refund|dispute&gt; reason=&lt;flag_off|user_read_failed|score_unavailable&gt; holdReason=&lt;reason&gt;
/// RISK_TABLE_UNCONFIGURED action=&lt;action&gt;
/// RISK_HOLD_ORDER_MISMATCH escrowId=&lt;id&gt; heldFor=&lt;release|refund&gt; legs=&lt;legs&gt;
/// DISPUTE_PROPAGATION_FAILED escrowId=&lt;id&gt; orderId=&lt;id&gt; deliveryId=&lt;id&gt; legs=&lt;legs&gt;
/// DELIVERY_DISPUTE_FROZEN escrowId=&lt;id&gt; orderId=&lt;id&gt; deliveryId=&lt;id&gt;
/// </code>
/// <para>
/// The two risk lines (FRD-03, fraud-detection-architecture.md section 18). Escrow-cron logs
/// <c>RISK_SETTLEMENT_SKIPPED</c> each run a settlement sweep leaves an escrow unsettled because of the
/// risk gate: a trigger while <c>FRAUD_HOLD_ENABLED</c> is off, a party row it could not read, or (FRD-04)
/// a party risk score it could not read; the <c>risk-settlement-skipped</c> alarm counts it from the FRD-03
/// deploy. The escrow Lambda logs <c>RISK_HOLD_ORDER_MISMATCH</c> each time an admin refund of a held
/// escrow is refused because its order cannot follow the refund; FRD-16 adds its alarm. FRD-04:
/// escrow-cron logs <c>RISK_TABLE_UNCONFIGURED</c> once per settlement run while its gate reads no risk
/// tier (no <c>FRAUD_SIGNALS_TABLE</c>), flag on or off, so a CRITICAL party is neither held nor skipped
/// (section 14.4); FRD-16 adds its alarm.
/// </para>
/// <para>
/// FUP-27. The propagation line is logged by the escrow service's dispute mirror when the delivery
/// or order behind a frozen dispute did not follow it, from the three Lambdas that run the mirror:
/// escrow (<c>POST /escrow/dispute</c> and its repeat), sync (a delivery dispute) and escrow-cron (a
/// sweep that froze a disputed trade). <c>legs</c> is the mirror's failed-legs text through
/// <see cref="LogLegs"/> (defanged, <c>,</c> as <c>.</c>, cut at 200 characters, not 64); the
/// escrow-audit row <c>dispute_propagation_failed</c> keeps it raw. The frozen line is logged by the sync Lambda (with <c>LogWarning</c>) once a delivery
/// dispute froze its escrow, and nowhere else: its filter feeds the escrow dispute-rate metric that
/// the escrow Lambda's <c>ESCROW</c>/<c>DISPUTE</c> audit line feeds too. It is a rate, not a failure.
/// </para>
/// <para>
/// The order-money line (ESC-09) is logged by escrow-cron's hourly order-money sweep when the order
/// does not show what its escrow's money says: <c>write_failed</c> (it could follow but the writes keep
/// failing, every run once the money moved more than two hours ago), <c>cannot_follow</c> and
/// <c>order_ahead</c> (once per escrow, decided by an escrow-audit row), <c>sweep_truncated</c> (a sweep
/// cycle, resumed run after run from a cursor, is unfinished 24 hours after it started) and
/// <c>sweep_failed</c> (a page query failed); ids are <c>-</c> for the last two.
/// </para>
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
/// <c>EscrowCronFunction.ReportAdminQueueAsync</c> (ESC-06) logs <c>ADMIN_QUEUE_OVERDUE</c> per
/// entry due for that long, with <c>ageHours</c> counted from when it became due, and the
/// auto-refund sweep logs it for a capped escrow whose order is missing, and (ESC-10) with
/// <c>orderId=- reason=no_order</c> and a trailing <c>escrowId=</c> for an /escrow/initiate escrow with no
/// order held to its cap. Contract: <c>specs/contracts/escrow-protocol.md</c>, "Additions in 1.8.1",
/// "Additions in 1.13.0" and "Additions in 1.15.0".
/// </para>
/// <para>
/// ESC-10 adds <c>ESCROW_SETTLEMENT_FAILED</c> actions from escrow-cron: <c>sweep_truncated</c> for a paged
/// sweep its budget stopped (<c>code=&lt;sweep&gt;:&lt;pages|rows|time&gt;</c>), <c>auto_release_blocked</c> and
/// <c>auto_refund_blocked</c> once per escrow and reason when the order and delivery check first blocks a
/// settlement (<c>code=&lt;op&gt;:trade:&lt;reason&gt;</c>), and <c>reconciliation_exception</c>.
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
    public const string OrderMoneyMismatchToken = "ORDER_MONEY_MISMATCH";
    public const string RiskSettlementSkippedToken = "RISK_SETTLEMENT_SKIPPED";
    public const string RiskTableUnconfiguredToken = "RISK_TABLE_UNCONFIGURED";
    public const string RiskHoldOrderMismatchToken = "RISK_HOLD_ORDER_MISMATCH";
    public const string DisputePropagationFailedToken = "DISPUTE_PROPAGATION_FAILED";
    public const string DeliveryDisputeFrozenToken = "DELIVERY_DISPUTE_FROZEN";

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

    public static string AdminQueueOverdue(string? orderId, string? reason, int ageHours, string? escrowId = null)
        => $"{AdminQueueOverdueToken} orderId={LogId(orderId)} reason={Safe(reason)} ageHours={Math.Max(0, ageHours)}"
           + (escrowId == null ? "" : $" escrowId={LogId(escrowId)}");

    /// <summary>ESC-04: a released escrow's trucker leg is still owed (and not queued) after <see cref="QueueAlarmAfter"/>.</summary>
    public static string TruckerPayoutOverdue(string? escrowId, string? reason, int ageHours)
        => $"{TruckerPayoutOverdueToken} escrowId={LogId(escrowId)} reason={Safe(reason)} ageHours={Math.Max(0, ageHours)}";

    /// <summary>FRD-03: a settlement sweep left an escrow unsettled for this run because of the risk gate.</summary>
    public static string RiskSettlementSkipped(string? escrowId, string? sweep, string? reason, string? holdReason)
        => $"{RiskSettlementSkippedToken} escrowId={LogId(escrowId)} sweep={Safe(sweep)} reason={Safe(reason)} holdReason={Safe(holdReason)}";

    /// <summary>FRD-04: the settlement gate reads no risk tier, so only suspended parties are held or skipped.</summary>
    public static string RiskTableUnconfigured(string? action)
        => $"{RiskTableUnconfiguredToken} action={Safe(action)}";

    /// <summary>FRD-03: an admin refund of a held escrow was refused because its order cannot follow the refund.</summary>
    public static string RiskHoldOrderMismatch(string? escrowId, string? heldFor, string? legs)
        => $"{RiskHoldOrderMismatchToken} escrowId={LogId(escrowId)} heldFor={Safe(heldFor)} legs={LogId(legs?.Replace(',', '.'))}";

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

    /// <summary>ESC-09: an order does not show what its escrow's money says (see the remarks for the reasons).</summary>
    public static string OrderMoneyMismatch(
        string? orderId, string? escrowId, string? orderStatus, string? escrowStatus, string? reason)
        => $"{OrderMoneyMismatchToken} orderId={LogId(orderId)} escrowId={LogId(escrowId)} "
           + $"orderStatus={Safe(orderStatus)} escrowStatus={Safe(escrowStatus)} reason={Safe(reason)}";

    /// <summary>
    /// FUP-27: the delivery or order behind a frozen dispute did not follow it (the dispute
    /// mirror's failed legs). The money is frozen; the order may still be open to ratings.
    /// </summary>
    public static string DisputePropagationFailed(string? escrowId, string? orderId, string? deliveryId, string? legs)
        => $"{DisputePropagationFailedToken} escrowId={LogId(escrowId)} orderId={LogId(orderId)} "
           + $"deliveryId={LogId(deliveryId)} legs={LogLegs(legs)}";

    /// <summary>
    /// FUP-27: a delivery dispute through sync froze its escrow. Counted with the escrow Lambda's
    /// disputes by the escrow dispute-rate alarm; not a failure.
    /// </summary>
    public static string DeliveryDisputeFrozen(string? escrowId, string? orderId, string? deliveryId)
        => $"{DeliveryDisputeFrozenToken} escrowId={LogId(escrowId)} orderId={LogId(orderId)} deliveryId={LogId(deliveryId)}";

    /// <summary>
    /// A client-chosen or client-influenced id (entity id, path parameter, header, an escrow id
    /// that embeds an order id) for any log line in a log group a money alarm filters: keeps
    /// <c>[A-Za-z0-9-.:]</c>, turns <c>_</c> into <c>-</c>, cuts at 64 characters, empty becomes
    /// <c>-</c>. No space, newline or underscore survives, so the value cannot spell an alarm token.
    /// </summary>
    public static string LogId(string? value) => Safe(Defang(value));

    /// <summary>
    /// FUP-27: a failed-legs text (<c>leg:[reason],leg:reason</c>) for one field: the same characters
    /// as <see cref="LogId"/> (<c>_</c> becomes <c>-</c>, brackets and spaces dropped), <c>,</c> becomes
    /// <c>.</c>, cut at 200 characters instead of 64 so three legs with long exception names survive.
    /// </summary>
    public static string LogLegs(string? legs) => Safe(Defang(legs?.Replace(',', '.')), MaxTextLength);

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
    /// <paramref name="maxLength"/> is longer only for <see cref="LogLegs"/>.
    /// </summary>
    private static string Safe(string? value, int maxLength = MaxFieldLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "-";
        }

        var safe = new string(value
            .Where(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                or '-' or '_' or '.' or ':')
            .Take(maxLength)
            .ToArray());
        return safe.Length == 0 ? "-" : safe;
    }
}
