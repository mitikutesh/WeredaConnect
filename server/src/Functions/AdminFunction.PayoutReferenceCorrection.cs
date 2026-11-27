using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// FUP-218 (escrow-protocol.md 2.18.0): an admin replaces the portal reference of a manual payout leg recorded by
/// mistake. The action shares <c>POST /admin/payouts/{escrowId}/{ref}/{action}</c> with complete, claim and unclaim
/// and needs the admin pool's admin group. The service writes the leg, the reference items and the escrow-audit row
/// in one transaction; the transaction-audit row (the admin Audit Log) is written here, once per correction.
/// </summary>
public partial class AdminFunction
{
    /// <summary>POST /admin/payouts/{escrowId}/{ref}/correct with {oldExternalRef, externalRef, reason}.</summary>
    private async Task<APIGatewayProxyResponse> HandleCorrectPayoutReferenceAsync(
        string escrowId, string leg, APIGatewayProxyRequest request, ILambdaContext context)
    {
        var service = string.IsNullOrEmpty(_escrowIdempotencyTable) ? null : ManualPayoutService();
        if (service == null)
        {
            return LambdaResponse.Error(503, "ADMIN_004", "Escrow tables not configured.");
        }

        CorrectPayoutReferenceRequest? body;
        try
        {
            body = JsonSerializer.Deserialize(request.Body ?? "", AppJsonContext.Default.CorrectPayoutReferenceRequest);
        }
        catch (JsonException)
        {
            return LambdaResponse.Error(400, "ADMIN_005", "Invalid JSON in request body.");
        }

        if (body == null)
        {
            return LambdaResponse.Error(400, "ADMIN_005", "Request body is required.");
        }

        if (!IsSafeEscrowId(escrowId) || !IsPayoutLegRef(leg))
        {
            return LambdaResponse.Error(404, "ESCROW_035", "No such payout leg.");
        }

        var adminUserId = AuthHelper.GetUserId(request) ?? "admin";
        var auditContext = AuditContext.From(request, adminUserId, "admin");
        var correlationId = AuditLogger.GetCorrelationId(request);

        var result = await service.CorrectPayoutReferenceAsync(
            escrowId, leg, body.OldExternalRef, body.ExternalRef, body.Reason,
            adminUserId, AdminAuthHelper.IsInAdminGroup(request), auditContext);
        if (!result.IsSuccess)
        {
            var error = result.Error!;
            AuditLogger.LogAudit("ADMIN", "MANUAL_PAYOUT_REF_CORRECT",
                userId: adminUserId, correlationId: correlationId,
                entityType: "escrow", entityId: escrowId, outcome: "failure", errorCode: error.Code);
            return LambdaResponse.Error(error.HttpStatus, error.Code, error.Message, error.Field);
        }

        var value = result.Value!;
        // A replay wrote nothing: no second transaction-audit row. The references go only to the audit tables,
        // never to the log line.
        if (!value.AlreadyCorrected)
        {
            await TransactionAuditWriter.EmitAsync(
                TxAuditRepo(), context, value.OrderId ?? escrowId, "escrow", escrowId,
                "admin_manual_payout_ref_corrected", null, null, auditContext,
                metadata: $"leg={value.Ref};oldRef={value.OldExternalRef};newRef={value.ExternalRef};reason={value.Reason}");
        }

        AuditLogger.LogAudit("ADMIN", "MANUAL_PAYOUT_REF_CORRECT",
            userId: adminUserId, correlationId: correlationId,
            entityType: "escrow", entityId: escrowId, outcome: value.AlreadyCorrected ? "replay" : "success",
            detail: $"leg={value.Ref}");
        return LambdaResponse.Ok(value, AppJsonContext.Default.CorrectPayoutReferenceResponse);
    }
}
