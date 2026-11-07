using System.Globalization;
using WeredaConnect.Server.Data;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// FRD-18 (fraud-detection-architecture.md section 20.1): the vocabulary of the labelled outcomes. A label
/// is one transaction-audit row per resolved case: an admin decision on a fraud signal, a dispute verdict,
/// or the exit of a held escrow. FRD-20 (ML scoring) reads them through <c>scripts/export_outcome_labels.py</c>.
/// </summary>
public static class OutcomeLabels
{
    /// <summary>The transaction-audit action of a label row.</summary>
    public const string Action = "outcome_label";

    /// <summary>The <c>entityType</c> of a label row (its badge on the admin timeline).</summary>
    public const string EntityType = "label";

    /// <summary>
    /// The <c>entityId</c> every label row shares: the one <c>EntityIndex</c> partition the export Queries.
    /// The case's own ids are in the label's link fields.
    /// </summary>
    public const string StreamEntityId = "outcome-labels";

    /// <summary>Bumped when a field changes meaning; the export carries it.</summary>
    public const int SchemaVersion = 1;

    public const string CaseFraudSignal = "fraud_signal";
    public const string CaseDispute = "dispute";
    public const string CaseHeldEscrow = "held_escrow";

    /// <summary>Fraud Review <c>reject</c>.</summary>
    public const string FraudConfirmed = "fraud_confirmed";

    /// <summary>Fraud Review <c>approve</c>.</summary>
    public const string FalsePositive = "false_positive";

    /// <summary>Fraud Review <c>whitelist</c>.</summary>
    public const string Whitelisted = "whitelisted";

    public const string BuyerWins = "buyer_wins";
    public const string FarmerWins = "farmer_wins";

    /// <summary>An admin release of a held escrow.</summary>
    public const string HoldReleased = "hold_released";

    /// <summary>An admin refund of a held escrow.</summary>
    public const string HoldRefunded = "hold_refunded";

    /// <summary>The outcome of a Fraud Review action, or null for anything else.</summary>
    public static string? FraudOutcome(string action) => action switch
    {
        "approve" => FalsePositive,
        "reject" => FraudConfirmed,
        "whitelist" => Whitelisted,
        _ => null
    };

    /// <summary>The outcome of an admin exit of a held escrow (<c>release</c> or <c>refund</c>).</summary>
    public static string HeldExitOutcome(string op) => op == "release" ? HoldReleased : HoldRefunded;

    /// <summary>
    /// The ticks an outcome adds to its case's key time: one slot per outcome, so two labels of one
    /// partition never share a key unless they are the same decision on the same case.
    /// </summary>
    internal static int Slot(string outcome) => outcome switch
    {
        FalsePositive => 1,
        FraudConfirmed => 2,
        Whitelisted => 3,
        BuyerWins => 4,
        FarmerWins => 5,
        HoldReleased => 6,
        HoldRefunded => 7,
        _ => 8
    };
}

/// <summary>
/// FRD-18: one labelled outcome, ids only. <see cref="TransactionId"/> is the partition of the case's
/// other audit rows (the signal id for a fraud decision, the order id, else the escrow id, for a verdict
/// or a held exit), so the label shows on the same timeline. <see cref="KeyTime"/> is a time the case
/// keeps across retries; the sort key is that time plus the outcome's slot, so the conditional put writes
/// a retried decision once.
/// </summary>
public sealed record OutcomeLabel
{
    public required string Case { get; init; }
    public required string CaseId { get; init; }
    public required string Outcome { get; init; }

    /// <summary>The audit action that decided it (<c>fraud_reject</c>, <c>admin_dispute_resolved</c>, ...).</summary>
    public required string Source { get; init; }

    public required string TransactionId { get; init; }
    public required string KeyTime { get; init; }
    public required string AdminId { get; init; }

    /// <summary>When the admin decided (ISO 8601).</summary>
    public required string LabelledAt { get; init; }

    /// <summary>The admin's free text (notes or reason). Kept on the row, never exported.</summary>
    public string? Reason { get; init; }

    /// <summary>The user the case is about: the signal's user. Null for a trade (see the party ids).</summary>
    public string? SubjectUserId { get; init; }

    public IReadOnlyList<string>? SignalIds { get; init; }
    public string? SignalType { get; init; }
    public string? SignalEntityType { get; init; }
    public string? SignalEntityId { get; init; }
    public string? SignalCreatedAt { get; init; }
    public string? OrderId { get; init; }
    public string? EscrowId { get; init; }
    public string? DeliveryId { get; init; }
    public string? BuyerId { get; init; }
    public string? FarmerId { get; init; }
    public string? TruckerId { get; init; }
    public decimal? Amount { get; init; }
    public string? DisputedBy { get; init; }
    public string? Disposition { get; init; }
    public string? HoldReason { get; init; }
    public string? HeldFor { get; init; }
    public string? HeldFromStatus { get; init; }
    public string? HeldAt { get; init; }
    public string? WhitelistUntil { get; init; }
}

/// <summary>What <see cref="OutcomeLabelWriter.WriteAsync"/> did.</summary>
public enum OutcomeLabelWrite
{
    Written,

    /// <summary>A row with this key is there already: a retry of the same decision.</summary>
    AlreadyLabelled,

    /// <summary>No transaction-audit table, or no partition id: nothing to write to.</summary>
    NotConfigured,

    /// <summary>The write failed; logged as <c>OUTCOME_LABEL_FAILED</c>, the action goes on.</summary>
    Failed
}

/// <summary>
/// FRD-18: writes a label row into the transaction-audit table, in the code path of the decision. Append
/// only and idempotent (a conditional put at a key the case keeps), and best effort: a failure is logged and
/// never fails or undoes the decision it labels, like every transaction-audit write.
/// </summary>
public static class OutcomeLabelWriter
{
    public static async Task<OutcomeLabelWrite> WriteAsync(
        ITransactionAuditRepository? repo, OutcomeLabel label, AuditContext ctx, ILambdaContext? context = null)
    {
        if (repo == null || string.IsNullOrEmpty(label.TransactionId))
        {
            return OutcomeLabelWrite.NotConfigured;
        }

        try
        {
            var written = await repo.AppendIfAbsentAsync(BuildEntry(label, ctx));
            Log(context, written
                ? $"OUTCOME_LABEL_WRITTEN case={label.Case} outcome={label.Outcome} caseId={MoneyAlarmLog.LogId(label.CaseId)}"
                : $"OUTCOME_LABEL_EXISTS case={label.Case} outcome={label.Outcome} caseId={MoneyAlarmLog.LogId(label.CaseId)}");
            return written ? OutcomeLabelWrite.Written : OutcomeLabelWrite.AlreadyLabelled;
        }
        catch (Exception ex)
        {
            Log(context,
                $"OUTCOME_LABEL_FAILED case={label.Case} outcome={label.Outcome} caseId={MoneyAlarmLog.LogId(label.CaseId)} error={ex.GetType().Name}",
                warning: true);
            return OutcomeLabelWrite.Failed;
        }
    }

    /// <summary>
    /// The sort key: <see cref="OutcomeLabel.KeyTime"/> plus the outcome's slot in ticks, as ISO 8601 UTC.
    /// A key time that does not parse falls back to the decision time (no longer retry-proof; not expected).
    /// </summary>
    internal static string KeyTimestamp(OutcomeLabel label)
    {
        var at = ParseUtc(label.KeyTime) ?? ParseUtc(label.LabelledAt) ?? DateTime.UtcNow;
        return at.AddTicks(OutcomeLabels.Slot(label.Outcome)).ToString("o", CultureInfo.InvariantCulture);
    }

    internal static Dictionary<string, object> BuildEntry(OutcomeLabel label, AuditContext ctx)
    {
        var timestamp = KeyTimestamp(label);
        var entry = new Dictionary<string, object>
        {
            ["auditId"] = $"label-{label.Case}-{label.CaseId}-{label.Outcome}-"
                          + new string(timestamp.Where(char.IsAsciiLetterOrDigit).ToArray()),
            ["transactionId"] = label.TransactionId,
            ["timestamp"] = timestamp,
            ["entityType"] = OutcomeLabels.EntityType,
            ["entityId"] = OutcomeLabels.StreamEntityId,
            ["action"] = OutcomeLabels.Action,
            ["toState"] = label.Outcome,
            ["actor"] = label.AdminId,
            ["actorRole"] = "admin",
            ["metadata"] = $"case={label.Case};outcome={label.Outcome}",
            ["labelVersion"] = OutcomeLabels.SchemaVersion,
            ["labelCase"] = label.Case,
            ["labelCaseId"] = label.CaseId,
            ["labelOutcome"] = label.Outcome,
            ["labelSource"] = label.Source,
            ["labelledAt"] = label.LabelledAt,
        };

        Put(entry, "labelReason", string.IsNullOrWhiteSpace(label.Reason) ? null : label.Reason.Trim());
        Put(entry, "subjectUserId", label.SubjectUserId);
        if (label.SignalIds is { Count: > 0 } signalIds)
        {
            entry["signalIds"] = signalIds.ToList();
        }

        Put(entry, "signalType", label.SignalType);
        Put(entry, "signalEntityType", label.SignalEntityType);
        Put(entry, "signalEntityId", label.SignalEntityId);
        Put(entry, "signalCreatedAt", label.SignalCreatedAt);
        Put(entry, "orderId", label.OrderId);
        Put(entry, "escrowId", label.EscrowId);
        Put(entry, "deliveryId", label.DeliveryId);
        Put(entry, "buyerId", label.BuyerId);
        Put(entry, "farmerId", label.FarmerId);
        Put(entry, "truckerId", label.TruckerId);
        if (label.Amount is { } amount)
        {
            entry["amount"] = amount;
        }

        Put(entry, "disputedBy", label.DisputedBy);
        Put(entry, "disposition", label.Disposition);
        Put(entry, "holdReason", label.HoldReason);
        Put(entry, "heldFor", label.HeldFor);
        Put(entry, "heldFromStatus", label.HeldFromStatus);
        Put(entry, "heldAt", label.HeldAt);
        Put(entry, "whitelistUntil", label.WhitelistUntil);
        Put(entry, "ipAddress", ctx.IpAddress);
        Put(entry, "correlationId", ctx.CorrelationId);
        return entry;
    }

    private static void Put(Dictionary<string, object> entry, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            entry[key] = value;
        }
    }

    private static DateTime? ParseUtc(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
                : parsed.ToUniversalTime()
            : null;

    private static void Log(ILambdaContext? context, string line, bool warning = false)
    {
        if (context != null && warning)
        {
            context.Logger.LogWarning(line);
        }
        else if (context != null)
        {
            context.Logger.LogInformation(line);
        }
        else
        {
            Console.WriteLine(line);
        }
    }
}
