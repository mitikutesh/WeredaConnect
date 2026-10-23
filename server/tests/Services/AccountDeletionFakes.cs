using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// In-memory <see cref="IAccountDeletionRepository"/> with the same conditional semantics as the
/// DynamoDB one (state machine, withdraw rules, version-checked cancel, scrubs), so a test can run a
/// deletion, break it at any step and resume it. <see cref="FailOnce"/> makes the next call of a
/// method throw; <see cref="BeforeCall"/> lets a test change rows between two steps (a race).
/// </summary>
internal sealed class FakeAccountDeletionRepository : IAccountDeletionRepository
{
    public Dictionary<string, Dictionary<string, AttributeValue>> Users { get; } = new();
    public List<Dictionary<string, object>> Orders { get; } = new();
    public List<Dictionary<string, object>> Escrows { get; } = new();
    public List<Dictionary<string, object>> Deliveries { get; } = new();
    public List<Dictionary<string, object>> Crops { get; } = new();
    public List<Dictionary<string, object>> Trips { get; } = new();
    public Dictionary<string, Dictionary<string, object>> Ratings { get; } = new();
    public Dictionary<string, Dictionary<string, object>> Messages { get; } = new();
    public List<(string BuyerId, string IntentId)> Intents { get; } = new();
    public Dictionary<string, List<string>> MatchEvents { get; } = new();
    public Dictionary<string, List<string>> DeviceTokens { get; } = new();
    public HashSet<string> CheckoutSlots { get; } = new();
    public Dictionary<string, List<Dictionary<string, object>>> PaymentTrails { get; } = new();

    /// <summary>Status the order indexes show for an order (a lagging GSI copy); the table keeps the real one.</summary>
    public Dictionary<string, string> IndexStatus { get; } = new();

    public List<string> Calls { get; } = new();
    public Action<string>? BeforeCall { get; set; }
    private readonly Dictionary<string, int> _failures = new();

    public void FailOnce(string method) => _failures[method] = _failures.GetValueOrDefault(method) + 1;

    private void Hit(string method)
    {
        Calls.Add(method);
        BeforeCall?.Invoke(method);
        if (_failures.TryGetValue(method, out var left) && left > 0)
        {
            _failures[method] = left - 1;
            throw new InvalidOperationException($"injected failure in {method}");
        }
    }

    public int Count(string method) => Calls.Count(c => c == method);

    public void AddUser(UserDto user)
    {
        var item = UserRepository.ToItem(user);
        // ToItem leaves the admin-only flag out (only the admin action writes it).
        if (user.Suspended)
        {
            item["suspended"] = new AttributeValue { BOOL = true };
        }

        Users[user.UserId] = item;
    }

    public Dictionary<string, AttributeValue> UserRow(string userId) => Users[userId];

    // ── users row ──

    public Task<UserDto?> GetAccountAsync(string userId)
    {
        Hit(nameof(GetAccountAsync));
        return Task.FromResult(Users.TryGetValue(userId, out var row) ? UserRepository.FromItem(Copy(row)) : null);
    }

    public Task<string?> TryMarkInProgressAsync(string userId, string requestedAt)
    {
        Hit(nameof(TryMarkInProgressAsync));
        if (!Users.TryGetValue(userId, out var row)
            || (row.TryGetValue("deletionState", out var ds) && ds.S != AccountDeletionPolicy.InProgress)
            || (row.TryGetValue("suspended", out var s) && s.BOOL))
        {
            return Task.FromResult<string?>(null);
        }

        row["deletionState"] = new AttributeValue { S = AccountDeletionPolicy.InProgress };
        if (!row.ContainsKey("deletionRequestedAt"))
        {
            row["deletionRequestedAt"] = new AttributeValue { S = requestedAt };
        }

        return Task.FromResult<string?>(row["deletionRequestedAt"].S);
    }

    public Task<bool> TryClearInProgressAsync(string userId)
    {
        Hit(nameof(TryClearInProgressAsync));
        if (!Users.TryGetValue(userId, out var row) || State(row) != AccountDeletionPolicy.InProgress)
        {
            return Task.FromResult(false);
        }

        row.Remove("deletionState");
        row.Remove("deletionRequestedAt");
        return Task.FromResult(true);
    }

    public Task<string?> TombstoneAsync(string userId, string deletedAt, string nextState, bool requireInProgress)
    {
        Hit(nameof(TombstoneAsync));
        if (!Users.TryGetValue(userId, out var row))
        {
            return Task.FromResult<string?>(null);
        }

        var state = State(row);
        if (requireInProgress && state != AccountDeletionPolicy.InProgress)
        {
            return Task.FromResult(state is AccountDeletionPolicy.CognitoPending or AccountDeletionPolicy.Completed
                ? row.TryGetValue("deletedAt", out var stored) ? stored.S : null
                : null);
        }

        foreach (var key in row.Keys.Where(k => !AccountDeletionPolicy.TombstoneAllowlist.Contains(k)).ToList())
        {
            row.Remove(key);
        }

        row["deletionState"] = new AttributeValue { S = nextState };
        if (!row.ContainsKey("deletedAt"))
        {
            row["deletedAt"] = new AttributeValue { S = deletedAt };
        }

        var version = row.TryGetValue("version", out var v) ? int.Parse(v.N) : 0;
        row["version"] = new AttributeValue { N = (version + 1).ToString() };
        return Task.FromResult<string?>(row["deletedAt"].S);
    }

    public Task<bool> MarkCompletedAsync(string userId)
    {
        Hit(nameof(MarkCompletedAsync));
        if (!Users.TryGetValue(userId, out var row)
            || State(row) is not (AccountDeletionPolicy.CognitoPending or AccountDeletionPolicy.Completed))
        {
            return Task.FromResult(false);
        }

        row["deletionState"] = new AttributeValue { S = AccountDeletionPolicy.Completed };
        return Task.FromResult(true);
    }

    public Task<(List<string> UserIds, Dictionary<string, AttributeValue>? Next)> ScanIncompleteAsync(
        string requestedBefore, Dictionary<string, AttributeValue>? startKey)
    {
        Hit(nameof(ScanIncompleteAsync));
        var ids = Users
            .Where(u => State(u.Value) is AccountDeletionPolicy.InProgress or AccountDeletionPolicy.CognitoPending
                        && u.Value.TryGetValue("deletionRequestedAt", out var at)
                        && string.CompareOrdinal(at.S, requestedBefore) < 0)
            .Select(u => u.Key)
            .ToList();
        return Task.FromResult<(List<string>, Dictionary<string, AttributeValue>?)>((ids, null));
    }

    // ── party rows ──

    public Task<List<PartyRow>> QueryOrdersAsync(string userId)
    {
        Hit(nameof(QueryOrdersAsync));
        var rows = Party(Orders, userId, ("buyerId", "buyer"), ("farmerId", "farmer"), ("truckerId", "trucker"));
        foreach (var row in rows)
        {
            if (IndexStatus.TryGetValue(Text(row.Item, "orderId"), out var stale))
            {
                row.Item["status"] = stale;
            }
        }

        return Task.FromResult(rows);
    }

    public Task<Dictionary<string, object>?> GetOrderAsync(string orderId, string createdAt)
    {
        Hit(nameof(GetOrderAsync));
        var order = Orders.FirstOrDefault(o => Text(o, "orderId") == orderId);
        return Task.FromResult(order == null ? null : new Dictionary<string, object>(order));
    }

    public Task<List<PartyRow>> QueryEscrowsAsync(string userId)
    {
        Hit(nameof(QueryEscrowsAsync));
        return Task.FromResult(Party(Escrows, userId, ("buyerId", "buyer"), ("farmerId", "farmer"), ("truckerId", "trucker")));
    }

    public Task<List<PartyRow>> QueryDeliveriesAsync(string userId, IReadOnlyCollection<BuyerOrderRef> buyerOrders)
    {
        Hit(nameof(QueryDeliveriesAsync));
        var rows = Party(Deliveries, userId, ("farmerId", "farmer"), ("truckerId", "trucker"), ("participantId", "buyer"));
        var seen = rows.Where(r => r.Role == "buyer").Select(r => Text(r.Item, "deliveryId")).ToHashSet();
        foreach (var order in buyerOrders)
        {
            foreach (var delivery in Deliveries.Where(d => Text(d, "listingId") == order.ListingId))
            {
                if ((Text(delivery, "buyerId") == userId || Text(delivery, "orderId") == order.OrderId)
                    && seen.Add(Text(delivery, "deliveryId")))
                {
                    rows.Add(new PartyRow("buyer", new Dictionary<string, object>(delivery)));
                }
            }
        }

        return Task.FromResult(rows);
    }

    public Task<bool> HasCheckoutSlotAsync(string orderId)
    {
        Hit(nameof(HasCheckoutSlotAsync));
        return Task.FromResult(CheckoutSlots.Contains(orderId));
    }

    public Task<List<Dictionary<string, object>>> GetOrderPaymentTrailAsync(string orderId)
    {
        Hit(nameof(GetOrderPaymentTrailAsync));
        return Task.FromResult(PaymentTrails.TryGetValue(orderId, out var trail) ? trail : new List<Dictionary<string, object>>());
    }

    // ── step 7 ──

    public Task<List<ListingKey>> QueryCropKeysAsync(string farmerId)
    {
        Hit(nameof(QueryCropKeysAsync));
        return Task.FromResult(Crops.Where(c => Text(c, "farmerId") == farmerId)
            .Select(c => new ListingKey(Text(c, "cropId"), Text(c, "createdAt"))).ToList());
    }

    public Task<List<ListingKey>> QueryTripKeysAsync(string truckerId)
    {
        Hit(nameof(QueryTripKeysAsync));
        return Task.FromResult(Trips.Where(t => Text(t, "truckerId") == truckerId)
            .Select(t => new ListingKey(Text(t, "tripId"), Text(t, "createdAt"))).ToList());
    }

    public Task<bool> WithdrawCropAsync(ListingKey key, string now)
    {
        Hit(nameof(WithdrawCropAsync));
        var crop = Crops.First(c => Text(c, "cropId") == key.Id);
        return Task.FromResult(Withdraw(crop, now, new[] { "available", "reserved" }, "deleted", "farmerName",
            new[] { "farmerPhone", "latitude", "longitude", "locationName", "description", "audioNoteRef" }));
    }

    public Task<bool> WithdrawTripAsync(ListingKey key, string now)
    {
        Hit(nameof(WithdrawTripAsync));
        var trip = Trips.First(t => Text(t, "tripId") == key.Id);
        return Task.FromResult(Withdraw(trip, now, new[] { "open", "matched" }, "cancelled", "truckerName",
            new[] { "truckerPhone", "vehiclePlate", "vehicleDescription" }));
    }

    private static bool Withdraw(
        Dictionary<string, object> row, string now, string[] from, string to, string nameField, string[] scrub)
    {
        var withdraw = from.Contains(Text(row, "status"));
        if (!withdraw && Text(row, nameField) == AccountDeletionPolicy.Sentinel && row.ContainsKey("deletedAt")
            && !scrub.Any(row.ContainsKey))
        {
            return false;
        }

        if (withdraw)
        {
            row["status"] = to;
            row["deletedAt"] = now;
        }
        else
        {
            row.TryAdd("deletedAt", now);
        }

        row[nameField] = AccountDeletionPolicy.Sentinel;
        foreach (var field in scrub)
        {
            row.Remove(field);
        }

        row["version"] = ((AccountDeletionPolicy.Number(row, "version") ?? 0) + 1).ToString();
        return true;
    }

    // ── step 8 ──

    public Task<bool> TryCancelUnpaidOrderAsync(Dictionary<string, object> order, string now)
    {
        Hit(nameof(TryCancelUnpaidOrderAsync));
        var stored = Orders.First(o => Text(o, "orderId") == Text(order, "orderId"));
        if (!AccountDeletionPolicy.IsUnpaidOrderStatus(Text(stored, "status"))
            || AccountDeletionPolicy.Number(stored, "version") != AccountDeletionPolicy.Number(order, "version"))
        {
            return Task.FromResult(false);
        }

        stored["status"] = "cancelled";
        stored["cancelReason"] = AccountDeletionPolicy.CancelReason;
        stored["cancelledAt"] = now;
        stored["version"] = ((AccountDeletionPolicy.Number(stored, "version") ?? 0) + 1).ToString();
        return Task.FromResult(true);
    }

    // ── step 10 ──

    public Task<bool> ScrubOrderAsync(Dictionary<string, object> order, IReadOnlyCollection<string> roles, string now)
    {
        Hit(nameof(ScrubOrderAsync));
        return Task.FromResult(Scrub(Orders, "orderId", order, roles, removePhones: false));
    }

    public Task<bool> ScrubDeliveryAsync(Dictionary<string, object> delivery, IReadOnlyCollection<string> roles, string now)
    {
        Hit(nameof(ScrubDeliveryAsync));
        return Task.FromResult(Scrub(Deliveries, "deliveryId", delivery, roles, removePhones: true));
    }

    public Task<bool> ScrubEscrowAsync(Dictionary<string, object> escrow, IReadOnlyCollection<string> roles)
    {
        Hit(nameof(ScrubEscrowAsync));
        return Task.FromResult(Scrub(Escrows, "escrowId", escrow, roles, removePhones: false));
    }

    private static bool Scrub(
        List<Dictionary<string, object>> table, string key, Dictionary<string, object> row,
        IReadOnlyCollection<string> roles, bool removePhones)
    {
        var stored = table.FirstOrDefault(r => Text(r, key) == Text(row, key));
        if (stored == null)
        {
            return false;
        }

        var changed = false;
        foreach (var role in roles)
        {
            var name = AccountDeletionPolicy.NameField(role);
            if (stored.ContainsKey(name) && Text(stored, name) != AccountDeletionPolicy.Sentinel)
            {
                stored[name] = AccountDeletionPolicy.Sentinel;
                changed = true;
            }

            if (removePhones && stored.Remove(AccountDeletionPolicy.PhoneField(role)))
            {
                changed = true;
            }
        }

        return changed;
    }

    public Task<List<Dictionary<string, object>>> QueryRatingsReceivedAsync(string rateeId)
    {
        Hit(nameof(QueryRatingsReceivedAsync));
        return Task.FromResult(Ratings.Values.Where(r => Text(r, "rateeId") == rateeId)
            .Select(r => new Dictionary<string, object>(r)).ToList());
    }

    public Task<Dictionary<string, object>?> GetRatingAsync(string ratingId)
    {
        Hit(nameof(GetRatingAsync));
        return Task.FromResult(Ratings.TryGetValue(ratingId, out var r) ? new Dictionary<string, object>(r) : null);
    }

    public Task<bool> RemoveRatingCommentAsync(string ratingId)
    {
        Hit(nameof(RemoveRatingCommentAsync));
        return Task.FromResult(Ratings.TryGetValue(ratingId, out var r) && r.Remove("comment"));
    }

    public Task<List<string>> QueryMessageIdsAsync(string userId)
    {
        Hit(nameof(QueryMessageIdsAsync));
        return Task.FromResult(Messages.Values
            .Where(m => Text(m, "senderId") == userId || Text(m, "recipientId") == userId)
            .Select(m => Text(m, "messageId")).ToList());
    }

    public Task DeleteMessageAsync(string messageId)
    {
        Hit(nameof(DeleteMessageAsync));
        Messages.Remove(messageId);
        return Task.CompletedTask;
    }

    public Task<List<string>> QueryIntentIdsAsync(string buyerId)
    {
        Hit(nameof(QueryIntentIdsAsync));
        return Task.FromResult(Intents.Where(i => i.BuyerId == buyerId).Select(i => i.IntentId).ToList());
    }

    public Task<List<string>> QueryMatchEventIdsAsync(string intentId)
    {
        Hit(nameof(QueryMatchEventIdsAsync));
        return Task.FromResult(MatchEvents.TryGetValue(intentId, out var ids) ? ids.ToList() : new List<string>());
    }

    public Task DeleteMatchEventAsync(string intentId, string eventId)
    {
        Hit(nameof(DeleteMatchEventAsync));
        if (MatchEvents.TryGetValue(intentId, out var ids))
        {
            ids.Remove(eventId);
        }

        return Task.CompletedTask;
    }

    public Task DeleteIntentAsync(string buyerId, string intentId)
    {
        Hit(nameof(DeleteIntentAsync));
        Intents.Remove((buyerId, intentId));
        return Task.CompletedTask;
    }

    public Task<List<string>> QueryDeviceEndpointsAsync(string userId)
    {
        Hit(nameof(QueryDeviceEndpointsAsync));
        return Task.FromResult(DeviceTokens.TryGetValue(userId, out var arns) ? arns.ToList() : new List<string>());
    }

    public Task DeleteDeviceTokenAsync(string userId, string endpointArn)
    {
        Hit(nameof(DeleteDeviceTokenAsync));
        if (DeviceTokens.TryGetValue(userId, out var arns))
        {
            arns.Remove(endpointArn);
        }

        return Task.CompletedTask;
    }

    // ── helpers ──

    private static List<PartyRow> Party(
        List<Dictionary<string, object>> table, string userId, params (string Field, string Role)[] roles)
    {
        var rows = new List<PartyRow>();
        foreach (var (field, role) in roles)
        {
            rows.AddRange(table.Where(r => Text(r, field) == userId)
                .Select(r => new PartyRow(role, new Dictionary<string, object>(r))));
        }

        return rows;
    }

    private static string? State(Dictionary<string, AttributeValue> row)
        => row.TryGetValue("deletionState", out var ds) ? ds.S : null;

    private static Dictionary<string, AttributeValue> Copy(Dictionary<string, AttributeValue> row) => new(row);

    internal static string Text(Dictionary<string, object> row, string key)
        => row.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";
}

/// <summary>Append-only transaction-audit fake keyed like the table (transactionId + timestamp).</summary>
internal sealed class FakeTransactionAuditRepository : ITransactionAuditRepository
{
    public List<Dictionary<string, object>> Rows { get; } = new();
    private int _failNextChecked;

    public void FailNextCheckedWrite() => _failNextChecked++;

    public IEnumerable<Dictionary<string, object>> WithAction(string action)
        => Rows.Where(r => r["action"].ToString() == action);

    public Task AppendAsync(Dictionary<string, object> entry)
    {
        Rows.Add(entry);
        return Task.CompletedTask;
    }

    public Task<bool> AppendIfAbsentAsync(Dictionary<string, object> entry)
    {
        if (_failNextChecked > 0)
        {
            _failNextChecked--;
            throw new InvalidOperationException("injected audit failure");
        }

        if (Rows.Any(r => r["transactionId"].Equals(entry["transactionId"]) && r["timestamp"].Equals(entry["timestamp"])))
        {
            return Task.FromResult(false);
        }

        Rows.Add(entry);
        return Task.FromResult(true);
    }

    public Task<List<Dictionary<string, object>>> GetByTransactionAsync(string transactionId)
        => Task.FromResult(Rows.Where(r => r["transactionId"].ToString() == transactionId).ToList());

    public Task<List<Dictionary<string, object>>> GetByEntityAsync(string entityId)
        => Task.FromResult(Rows.Where(r => r["entityId"].ToString() == entityId).ToList());

    public Task<List<Dictionary<string, object>>> GetRecentAsync(int limit)
        => Task.FromResult(Rows.TakeLast(limit).ToList());
}
