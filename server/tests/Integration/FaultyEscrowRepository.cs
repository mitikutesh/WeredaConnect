using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// The real repository with fault hooks on the status write and (ESC-04) the trucker leg's record
/// write, to simulate a crash after the gateway paid (the write never lands) or a transient failure.
/// Shared by the ESC-05 and ESC-04 integration suites.
/// </summary>
internal sealed class FaultyEscrowRepository : IEscrowRepository
{
    private readonly IEscrowRepository _inner;

    public FaultyEscrowRepository(IEscrowRepository inner) => _inner = inner;

    /// <summary>Called with (escrowId, newStatus) before each status write; may throw.</summary>
    public Func<string, string, object?>? OnUpdateStatus { get; init; }

    public Task UpdateStatusAsync(string escrowId, string createdAt, string newStatus, string? expectedCurrentStatus = null,
        Dictionary<string, object>? additionalFields = null)
    {
        OnUpdateStatus?.Invoke(escrowId, newStatus);
        return _inner.UpdateStatusAsync(escrowId, createdAt, newStatus, expectedCurrentStatus, additionalFields);
    }

    public Task<bool> TryExtendExpiryAsync(string escrowId, string createdAt, string expiresAt)
        => _inner.TryExtendExpiryAsync(escrowId, createdAt, expiresAt);

    public TransactWriteItem BuildRefundArm(string escrowId, string createdAt, string reason, string scheduledAt)
        => _inner.BuildRefundArm(escrowId, createdAt, reason, scheduledAt);

    /// <summary>Called with the escrowId before each trucker leg record write; may throw.</summary>
    public Action<string>? OnRecordTruckerPayout { get; init; }

    public Task<bool> TryRecordTruckerPayoutAsync(
        string escrowId, string createdAt, string lockId, string truckerRef, string paidAt, string completedAt)
    {
        OnRecordTruckerPayout?.Invoke(escrowId);
        return _inner.TryRecordTruckerPayoutAsync(escrowId, createdAt, lockId, truckerRef, paidAt, completedAt);
    }

    public Task<List<Dictionary<string, object>>> QueryPendingTruckerLegsAsync(int limit) => _inner.QueryPendingTruckerLegsAsync(limit);
    public Task<List<Dictionary<string, object>>> QueryLegacyTruckerLegsAsync(int limit) => _inner.QueryLegacyTruckerLegsAsync(limit);
    public Task<bool> TrySetTruckerBlockReasonAsync(string escrowId, string createdAt, string reason, string reasonAt)
        => _inner.TrySetTruckerBlockReasonAsync(escrowId, createdAt, reason, reasonAt);
    public Task<bool> TryBackfillTruckerIdAsync(string escrowId, string createdAt, string truckerId)
        => _inner.TryBackfillTruckerIdAsync(escrowId, createdAt, truckerId);
    public Task<string?> TryAcquireTruckerPayoutLockAsync(string escrowId, string createdAt)
        => _inner.TryAcquireTruckerPayoutLockAsync(escrowId, createdAt);
    public Task<bool> ReleaseTruckerPayoutLockAsync(string escrowId, string createdAt, string lockId)
        => _inner.ReleaseTruckerPayoutLockAsync(escrowId, createdAt, lockId);
    public Task<bool> TryQueueTruckerLegAsync(string escrowId, string createdAt, string reason,
        IReadOnlyList<ManualPayoutLegSeed> legs, string queuedAt, string lockId, string? parkId)
        => _inner.TryQueueTruckerLegAsync(escrowId, createdAt, reason, legs, queuedAt, lockId, parkId);
    public Task<bool> TryParkLegacyTruckerLegAsync(string escrowId, string createdAt, string currentStatus,
        IReadOnlyList<ManualPayoutLegSeed> legs, string queuedAt, string pendingSince)
        => _inner.TryParkLegacyTruckerLegAsync(escrowId, createdAt, currentStatus, legs, queuedAt, pendingSince);

    public Task CreateAsync(Dictionary<string, object> escrow) => _inner.CreateAsync(escrow);
    public Task<Dictionary<string, object>?> GetByIdAsync(string escrowId) => _inner.GetByIdAsync(escrowId);
    public Task<Dictionary<string, object>?> GetByIdAsync(string escrowId, bool consistentRead) => _inner.GetByIdAsync(escrowId, consistentRead);
    public Task<bool> TryAcquireSettlementLockAsync(string escrowId, string createdAt, string op, string expectedStatus, bool allowHeld = false)
        => _inner.TryAcquireSettlementLockAsync(escrowId, createdAt, op, expectedStatus, allowHeld);
    /// <summary>FRD-03: called with the escrowId before each hold write; may change another row first (an accept racing the hold).</summary>
    public Func<string, Task>? OnHold { get; init; }

    public async Task<EscrowHoldWrite> TryHoldAsync(string escrowId, string createdAt, EscrowHoldRequest hold, OrderFence? fence = null)
    {
        if (OnHold != null)
        {
            await OnHold(escrowId);
        }

        return await _inner.TryHoldAsync(escrowId, createdAt, hold, fence);
    }
    public Task ReleaseSettlementLockAsync(string escrowId, string createdAt) => _inner.ReleaseSettlementLockAsync(escrowId, createdAt);
    public Task<List<Dictionary<string, object>>> QueryStaleSettlementLocksAsync(string status, string lockedBefore, int limit = 50)
        => _inner.QueryStaleSettlementLocksAsync(status, lockedBefore, limit);
    public Task<bool> TryQueueStaleSettlementAsync(string escrowId, string createdAt, string op, string fromStatus,
        IReadOnlyList<ManualPayoutLegSeed> legs, string queuedAt, string? lockId, string parkId)
        => _inner.TryQueueStaleSettlementAsync(escrowId, createdAt, op, fromStatus, legs, queuedAt, lockId, parkId);
    public Task<bool> TryQueueManualPayoutAsync(string escrowId, string createdAt, string op, string reason, string fromStatus,
        IReadOnlyList<ManualPayoutLegSeed> legs, string queuedAt)
        => _inner.TryQueueManualPayoutAsync(escrowId, createdAt, op, reason, fromStatus, legs, queuedAt);
    public Task<bool> TryMarkManualLegPaidAsync(string escrowId, string createdAt, string op, string leg, string externalRef,
        string paidBy, string paidAt, string? otherLegMustBePending)
        => _inner.TryMarkManualLegPaidAsync(escrowId, createdAt, op, leg, externalRef, paidBy, paidAt, otherLegMustBePending);
    public Task<bool> TryCloseManualPayoutAsync(string escrowId, string createdAt, string op, string leg, string externalRef,
        string paidBy, string paidAt, IReadOnlyList<string> otherLegsMustBePaid, string newStatus, Dictionary<string, object> settlementFields)
        => _inner.TryCloseManualPayoutAsync(escrowId, createdAt, op, leg, externalRef, paidBy, paidAt, otherLegsMustBePaid, newStatus, settlementFields);
    public Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryOpenManualPayoutsAsync(
        int limit, Dictionary<string, AttributeValue>? exclusiveStartKey) => _inner.QueryOpenManualPayoutsAsync(limit, exclusiveStartKey);
    public Task<Dictionary<string, object>?> GetByDeliveryIdAsync(string deliveryId) => _inner.GetByDeliveryIdAsync(deliveryId);
    public Task<List<Dictionary<string, object>>> QueryByOrderIdAsync(string orderId, int limit = 5) => _inner.QueryByOrderIdAsync(orderId, limit);
    public Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByBuyerAsync(
        string buyerId, string? status, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey)
        => _inner.QueryByBuyerAsync(buyerId, status, limit, exclusiveStartKey);
    public Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByFarmerAsync(
        string farmerId, string? status, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey)
        => _inner.QueryByFarmerAsync(farmerId, status, limit, exclusiveStartKey);
    public Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryByTruckerAsync(
        string truckerId, string? status, int limit, Dictionary<string, AttributeValue>? exclusiveStartKey)
        => _inner.QueryByTruckerAsync(truckerId, status, limit, exclusiveStartKey);
    public Task<List<Dictionary<string, object>>> QueryByStatusForCronAsync(
        string status, string? expiresAtBefore = null, string? deliveryConfirmedBefore = null, int limit = 100)
        => _inner.QueryByStatusForCronAsync(status, expiresAtBefore, deliveryConfirmedBefore, limit);
    public Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryCronPageAsync(
        CronSweepQuery query, Dictionary<string, AttributeValue>? exclusiveStartKey) => _inner.QueryCronPageAsync(query, exclusiveStartKey);
    public Task<bool> TrySetSettlementBlockReasonAsync(string escrowId, string createdAt, string expectedStatus, string reason, string reasonAt)
        => _inner.TrySetSettlementBlockReasonAsync(escrowId, createdAt, expectedStatus, reason, reasonAt);
    public Task<bool> TryClearSettlementBlockReasonAsync(string escrowId, string createdAt, string storedReason)
        => _inner.TryClearSettlementBlockReasonAsync(escrowId, createdAt, storedReason);
}
