using WeredaConnect.Server.Data;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// Who/where context for an audit event. Built once at a handler entry point
/// from the request (actor from JWT, source IP + correlation id from the edge),
/// then threaded to each transition that emits an audit event.
/// </summary>
public sealed record AuditContext
{
    /// <summary>Who performed the action: a userId, or "system"/"telebirr" for machine actors.</summary>
    public required string Actor { get; init; }

    public string? ActorRole { get; init; }
    public string? IpAddress { get; init; }
    public string? CorrelationId { get; init; }

    /// <summary>Build from an authenticated HTTP request (captures IP + correlation id — the "where").</summary>
    public static AuditContext From(APIGatewayProxyRequest request, string actor, string? role = null) => new()
    {
        Actor = actor,
        ActorRole = role,
        IpAddress = AuditLogger.GetSourceIp(request),
        CorrelationId = AuditLogger.GetCorrelationId(request),
    };

    /// <summary>Context for a machine actor (cron, webhook) with no request.</summary>
    public static AuditContext ForSystem(string actor = "system") => new() { Actor = actor };
}

/// <summary>
/// Best-effort writer for the unified transaction audit trail. Every emit is
/// wrapped so an audit failure can NEVER break the transaction it is recording —
/// a swallowed error is logged as a warning and the caller proceeds. Used for the
/// order/delivery/admin legs; the escrow leg writes its own escrow-audit trail.
/// </summary>
public static class TransactionAuditWriter
{
    /// <summary>
    /// Append one transaction-audit event. No-op when the repo is unconfigured
    /// (e.g. the table env var is unset in a test/DI path).
    /// </summary>
    /// <param name="transactionId">Anchor for the trail — the orderId when known, else the entity id.</param>
    public static async Task EmitAsync(
        ITransactionAuditRepository? repo,
        ILambdaContext? context,
        string transactionId,
        string entityType,
        string entityId,
        string action,
        string? fromState,
        string? toState,
        AuditContext ctx,
        string? metadata = null)
    {
        if (repo == null || string.IsNullOrEmpty(transactionId) || string.IsNullOrEmpty(entityId))
        {
            return;
        }

        try
        {
            var entry = new Dictionary<string, object>
            {
                ["auditId"] = $"txaudit-{Guid.NewGuid():N}",
                ["transactionId"] = transactionId,
                ["entityType"] = entityType,
                ["entityId"] = entityId,
                ["action"] = action,
                ["actor"] = ctx.Actor,
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
            };

            if (!string.IsNullOrEmpty(fromState)) entry["fromState"] = fromState;
            if (!string.IsNullOrEmpty(toState)) entry["toState"] = toState;
            if (!string.IsNullOrEmpty(ctx.ActorRole)) entry["actorRole"] = ctx.ActorRole!;
            if (!string.IsNullOrEmpty(ctx.IpAddress)) entry["ipAddress"] = ctx.IpAddress!;
            if (!string.IsNullOrEmpty(ctx.CorrelationId)) entry["correlationId"] = ctx.CorrelationId!;
            if (!string.IsNullOrEmpty(metadata)) entry["metadata"] = metadata!;

            await repo.AppendAsync(entry);
        }
        catch (Exception ex)
        {
            // Audit is observability, never a hard dependency of the transaction.
            context?.Logger.LogWarning(
                $"transaction-audit write failed (non-fatal): [{ex.GetType().Name}] action={action} entity={entityType}");
        }
    }
}
