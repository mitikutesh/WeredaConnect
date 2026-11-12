using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// FUP-05 (escrow-protocol.md 2.12.0): an admin claims a manual payout leg before paying it in the Telebirr
/// merchant portal, so no other admin pays the same leg, and gives it back when they will not pay it. Both
/// routes take no body and need the admin pool's admin group. Each claim and release leaves an escrow-audit
/// row (written by the service) and a transaction-audit row (here), like the record itself.
/// </summary>
public partial class AdminFunction
{
    /// <summary>POST /admin/payouts/{escrowId}/{ref}/claim (claim or renew) and /unclaim (release).</summary>
    private async Task<APIGatewayProxyResponse> HandleClaimManualPayoutAsync(
        string escrowId, string leg, APIGatewayProxyRequest request, ILambdaContext context, bool claim)
    {
        var service = ManualPayoutService();
        if (service == null || string.IsNullOrEmpty(_escrowIdempotencyTable))
        {
            return LambdaResponse.Error(503, "ADMIN_004", "Escrow tables not configured.");
        }

        if (!IsSafeEscrowId(escrowId) || !IsPayoutLegRef(leg))
        {
            return LambdaResponse.Error(404, "ESCROW_035", "No such payout leg.");
        }

        var adminUserId = AuthHelper.GetUserId(request) ?? "admin";
        var adminGroupVerified = AdminAuthHelper.IsInAdminGroup(request);
        var auditContext = AuditContext.From(request, adminUserId, "admin");
        var correlationId = AuditLogger.GetCorrelationId(request);
        var auditAction = claim ? "MANUAL_PAYOUT_CLAIM" : "MANUAL_PAYOUT_UNCLAIM";

        var result = claim
            ? await service.ClaimManualPayoutAsync(escrowId, leg, adminUserId, adminGroupVerified, auditContext)
            : await service.ReleaseManualPayoutClaimAsync(escrowId, leg, adminUserId, adminGroupVerified, auditContext);
        if (!result.IsSuccess)
        {
            var error = result.Error!;
            AuditLogger.LogAudit("ADMIN", auditAction,
                userId: adminUserId, correlationId: correlationId,
                entityType: "escrow", entityId: escrowId, outcome: "failure", errorCode: error.Code);
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        var value = result.Value!;
        if (value.Claimed || value.Released)
        {
            await TransactionAuditWriter.EmitAsync(
                TxAuditRepo(), context, value.OrderId ?? escrowId, "escrow", escrowId,
                claim ? "admin_manual_payout_claimed" : "admin_manual_payout_claim_released", null, null, auditContext,
                metadata: claim
                    ? $"leg={value.Ref};expiresAt={value.ClaimExpiresAt};renewed={(value.Renewed ? "true" : "false")}"
                    : $"leg={value.Ref}");
        }

        AuditLogger.LogAudit("ADMIN", auditAction,
            userId: adminUserId, correlationId: correlationId,
            entityType: "escrow", entityId: escrowId, outcome: value.Claimed || value.Released ? "success" : "noop",
            detail: $"leg={value.Ref}");
        return LambdaResponse.Ok(value, AppJsonContext.Default.ManualPayoutClaimResponse);
    }

    /// <summary>
    /// A payout leg in the path: a 1.x leg name (<c>farmer</c>, <c>trucker</c>, <c>buyer</c>) or a ledger ref
    /// (<c>release#farmer</c>, URL-encoded as <c>release%23farmer</c>; ESC-20 P.8). Before FUP-05 the record route
    /// accepted only the leg names, so a ledger row could not be recorded from the admin app.
    /// </summary>
    internal static bool IsPayoutLegRef(string leg)
    {
        var decoded = Uri.UnescapeDataString(leg);
        if (ManualPayoutFields.IsLeg(decoded))
        {
            return true;
        }

        return decoded.Length is > 0 and <= 200
               && decoded.Contains('#', StringComparison.Ordinal)
               && decoded.All(c => char.IsAsciiLetterOrDigit(c) || c is '#' or '-' or '_' or '.' or ':');
    }
}
