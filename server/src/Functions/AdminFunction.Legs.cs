using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// ESC-24: the admin's read-only view of the money legs. The transport leg on the escrow DTO, the escrow
/// ledger (<c>GET /admin/escrows/{id}/ledger</c>) and the stuck orders (<c>GET /admin/orders/queue</c>).
/// Nothing here writes: no amount, status or deadline changes.
/// </summary>
public partial class AdminFunction
{
    /// <summary>
    /// ESC-24: how long past its deadline an order must be before it counts as stuck. The deadline sweep runs
    /// every 15 minutes, so two runs have passed it by.
    /// </summary>
    internal static readonly TimeSpan StuckOrderGrace = TimeSpan.FromMinutes(30);

    /// <summary>ESC-24: index pages one stuck-order request reads at most while looking for stuck rows.</summary>
    private const int StuckOrderMaxPages = 5;

    private const string StuckSourceQueue = "queue";
    private const string StuckSourceDeadline = "deadline";

    /// <summary>The key attributes of an <c>AdminQueueIndex</c> page's last evaluated key.</summary>
    private static readonly string[] AdminQueueIndexKeys = { "orderId", "createdAt", AdminQueueFields.Marker, AdminQueueFields.QueuedAt };

    /// <summary>The key attributes of a <c>DeadlineIndex</c> page's last evaluated key.</summary>
    private static readonly string[] DeadlineIndexKeys = { "orderId", "createdAt", OrderDeadlines.ShardField, OrderDeadlines.AtField };

    /// <summary>The key attributes of a ledger page's last evaluated key.</summary>
    private static readonly string[] LedgerPageKeys = { LedgerFields.EscrowId, LedgerFields.Ref };

    private static decimal? GetDecimalOrNull(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var val) && val != null ? GetDecimal(item, key) : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// ESC-24: where the transport leg stands, from the escrow row alone. Null when the escrow carries no
    /// transport money and no trace of a trucker payout.
    /// </summary>
    internal static AdminTruckerLegDto? TruckerLegOf(Dictionary<string, object> item)
    {
        var pending = GetBool(item, TruckerPayoutFields.Pending);
        var paidAt = NullIfEmpty(GetStringOrNull(item, TruckerPayoutFields.PaidAt));
        var paidRef = NullIfEmpty(GetStringOrNull(item, TruckerPayoutFields.Ref));
        var inQueue = GetStringOrNull(item, ManualPayoutFields.Marker) == ManualPayoutFields.MarkerOpen
                      && GetStringOrNull(item, ManualPayoutFields.StatusAttr(ManualPayoutFields.LegTrucker)) == ManualPayoutFields.LegPending;
        if (GetDecimal(item, "truckerPayout") <= 0 && !pending && paidAt == null && paidRef == null && !inQueue)
        {
            return null;
        }

        var blockReason = NullIfEmpty(GetStringOrNull(item, TruckerPayoutFields.BlockReason));
        string state;
        if (inQueue)
        {
            state = "manual_queue";
        }
        else if (paidAt != null || paidRef != null)
        {
            state = "paid";
        }
        else if (pending)
        {
            state = blockReason != null ? "blocked" : "pending";
        }
        else if (LedgerEscrowFields.IsLedger(item))
        {
            // The ledger names the leg's state; whether a released leg is paid is on its ledger row.
            state = LedgerEscrow.From(item).LegState(LedgerEscrowFields.LegTransport) ?? "none";
        }
        else
        {
            state = GetString(item, "status") switch
            {
                "initiated" or "escrowed" or "disputed" or "dispute_resolved" or "held" => "held",
                "refunded" => "refunded",
                _ => "none"
            };
        }

        return new AdminTruckerLegDto
        {
            State = state,
            Pending = pending,
            PendingSince = NullIfEmpty(GetStringOrNull(item, TruckerPayoutFields.PendingSince)),
            BlockReason = blockReason,
            InManualQueue = inQueue,
            PaidAt = paidAt,
            TelebirrRef = paidRef
        };
    }

    /// <summary>ESC-24: the split recorded with a <c>partial_refund</c> verdict, or null.</summary>
    private static VerdictAmountsDto? VerdictAmountsOf(Dictionary<string, object> item)
        => VerdictAmounts.From(item) is { } split
            ? new VerdictAmountsDto { FarmerAmount = split.FarmerAmount, TruckerAmount = split.TruckerAmount, BuyerRefund = split.BuyerRefund }
            : null;

    /// <summary>ESC-24: a ledger escrow's legs for the admin DTO, or null on a 1.x escrow.</summary>
    private static Dictionary<string, string>? LegStatusOf(Dictionary<string, object> item)
        => LedgerEscrowFields.IsLedger(item)
            ? new Dictionary<string, string>(LedgerEscrow.From(item).Legs, StringComparer.Ordinal)
            : null;

    /// <summary>
    /// A start key whose attributes are exactly <paramref name="keyNames"/>, from the request's
    /// <c>nextToken</c>. False for a token that does not decode or belongs to another index.
    /// </summary>
    private static bool TryReadPageToken(
        IDictionary<string, string> queryParams, string[] keyNames, out Dictionary<string, AttributeValue>? startKey)
    {
        startKey = null;
        if (!queryParams.TryGetValue("nextToken", out var nextToken) || string.IsNullOrEmpty(nextToken))
        {
            return true;
        }

        var decoded = PaginationTokenHelper.DecodeNextToken(nextToken);
        if (decoded == null || decoded.Count != keyNames.Length || !keyNames.All(decoded.ContainsKey))
        {
            return false;
        }

        startKey = decoded;
        return true;
    }

    /// <summary>
    /// ESC-24: <c>GET /admin/escrows/{id}/ledger?limit=&amp;nextToken=</c>, one page (50, at most 100) of the
    /// escrow's ledger rows in ref order, a Query on the escrow's partition. An escrow on the 1.x path answers
    /// <c>settlementModel: legacy</c> with no rows: its money is on the escrow row and the audit trail.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleEscrowLedgerAsync(string escrowId, APIGatewayProxyRequest request)
    {
        if (string.IsNullOrEmpty(_escrowsTable))
        {
            return LambdaResponse.Error(503, "ADMIN_004", "Escrow tables not configured.");
        }

        var item = IsSafeEscrowId(escrowId) ? await QuerySingleItemAsync(_escrowsTable, "escrowId", escrowId) : null;
        if (item == null)
        {
            return LambdaResponse.Error(404, "ADMIN_003", "Escrow not found.");
        }

        if (!LedgerEscrowFields.IsLedger(DynamoDbHelper.FromItem(item)))
        {
            return LambdaResponse.Ok(
                new AdminEscrowLedgerResponse { EscrowId = escrowId, SettlementModel = "legacy", Rows = new List<AdminLedgerRowDto>() },
                AppJsonContext.Default.AdminEscrowLedgerResponse);
        }

        var ledger = LedgerRepository();
        if (ledger == null)
        {
            return LambdaResponse.Error(503, "ADMIN_004", "Ledger table not configured.");
        }

        var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();
        var limit = ParseLimit(queryParams, 50);
        if (!TryReadPageToken(queryParams, LedgerPageKeys, out var startKey)
            || (startKey != null && startKey[LedgerFields.EscrowId].S != escrowId))
        {
            return InvalidNextToken();
        }

        var (rows, next) = await ledger.GetRowsPageAsync(escrowId, limit, startKey);
        var names = await BatchGetUserNamesAsync(rows.Select(r => r.PayeeId ?? ""));
        var response = new AdminEscrowLedgerResponse
        {
            EscrowId = escrowId,
            SettlementModel = LedgerEscrowFields.ModelLedger,
            Rows = rows.Select(row => new AdminLedgerRowDto
            {
                Ref = row.Ref,
                Kind = row.Kind,
                Legs = row.Legs.ToList(),
                PayeeRole = row.PayeeRole,
                PayeeId = row.PayeeId,
                PayeeName = row.PayeeId != null ? names.GetValueOrDefault(row.PayeeId) : null,
                Amount = row.Amount,
                Status = row.Status,
                GatewayRef = row.GatewayRef,
                TelebirrRef = row.TelebirrRef,
                Attempts = row.Attempts,
                LastCode = row.LastCode,
                ManualReason = row.ManualReason,
                Reason = row.Reason,
                Actor = row.Actor,
                PaidBy = row.PaidBy,
                CreatedAt = row.CreatedAt,
                UpdatedAt = row.UpdatedAt,
                SentAt = row.SentAt,
                DoneAt = row.DoneAt
            }).ToList(),
            NextToken = next == null ? null : PaginationTokenHelper.EncodeNextToken(next)
        };

        return LambdaResponse.Ok(response, AppJsonContext.Default.AdminEscrowLedgerResponse);
    }

    /// <summary>
    /// ESC-24: the real deadline of <paramref name="order"/> that passed more than
    /// <see cref="StuckOrderGrace"/> ago, or null. A reminder is not a deadline: the reminders are taken as
    /// sent, so the answer is the farmer's or the trucker's response deadline, the buyer's transport choice or
    /// the pickup deadline (<see cref="OrderDeadlines"/>).
    /// </summary>
    internal static OrderDeadline? OverdueDeadline(Dictionary<string, object> order, DateTime now)
    {
        var probe = new Dictionary<string, object>(order);
        foreach (var reminder in new[]
                 {
                     OrderCommitment.FarmerReminderAtField, OrderCommitment.TruckerReminderAtField,
                     OrderCommitment.BuyerChoiceReminderAtField
                 })
        {
            if (string.IsNullOrEmpty(GetStringOrNull(probe, reminder)))
            {
                probe[reminder] = "sent";
            }
        }

        return OrderDeadlines.Next(probe) is { } due && due.At <= now - StuckOrderGrace ? due : null;
    }

    /// <summary>
    /// ESC-24: <c>GET /admin/orders/queue?source=queue|deadline&amp;limit=&amp;nextToken=</c>, the stuck orders,
    /// oldest first, 20 a page (at most 50). Never a Scan.
    /// <list type="bullet">
    /// <item><c>queue</c> (default): the admin stuck-order queue of ESC-06 (<c>AdminQueueIndex</c>): an order
    /// escrow held 21 days whose order is not done (<c>hold_cap</c>).</item>
    /// <item><c>deadline</c>: the orders in <c>DeadlineIndex</c> (ESC-16) due before now plus the sweep's retry
    /// delay, each read again and kept only when its own deadline (the farmer's answer, the trucker's answer,
    /// the buyer's transport choice, the pickup) passed more than <see cref="StuckOrderGrace"/> ago: the sweep
    /// did not act on it. A row the sweep pushed an hour ahead for a retry is still found, because the answer
    /// comes from the order's own stamps, not from the index time. A page can be short or empty with a
    /// <c>nextToken</c>: at most <see cref="StuckOrderMaxPages"/> index pages are read per request.</item>
    /// </list>
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleListStuckOrdersAsync(APIGatewayProxyRequest request)
    {
        if (string.IsNullOrEmpty(_ordersTable))
        {
            return LambdaResponse.Error(503, "ADMIN_004", "Orders table not configured.");
        }

        var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();
        var source = queryParams.TryGetValue("source", out var asked) && !string.IsNullOrEmpty(asked) ? asked : StuckSourceQueue;
        if (source is not (StuckSourceQueue or StuckSourceDeadline))
        {
            return LambdaResponse.Error(400, "ADMIN_005", "source must be queue or deadline.", "source");
        }

        var limit = Math.Min(ParseLimit(queryParams, 20), MaxIndexPageSize);
        if (!TryReadPageToken(queryParams, source == StuckSourceQueue ? AdminQueueIndexKeys : DeadlineIndexKeys, out var startKey))
        {
            return InvalidNextToken();
        }

        var orders = new OrderRepository(_dynamoDb, _ordersTable);
        var now = Clock();
        var stuck = new List<AdminStuckOrderDto>();
        Dictionary<string, AttributeValue>? next;
        if (source == StuckSourceQueue)
        {
            (var items, next) = await orders.QueryAdminQueueAsync(limit, startKey);
            stuck.AddRange(items.Select(item => MapToStuckOrder(
                item, GetStringOrNull(item, AdminQueueFields.Reason) ?? AdminQueueFields.ReasonHoldCap,
                GetString(item, AdminQueueFields.QueuedAt), now)));
        }
        else
        {
            var dueBefore = OrderCommitment.Iso(now + OrderCronFunction.DeadlineRetryAfter);
            next = startKey;
            for (var page = 0; page < StuckOrderMaxPages && stuck.Count < limit; page++)
            {
                // Each read asks only for the rows still missing, so the page never passes the limit.
                (var indexed, next) = await orders.QueryDueDeadlinesAsync(dueBefore, limit - stuck.Count, next);
                var rows = await Task.WhenAll(indexed
                    .Select(row => GetString(row, "orderId"))
                    .Where(id => id.Length > 0)
                    .Select(id => orders.GetByIdAsync(id)));
                foreach (var order in rows)
                {
                    if (order != null && OverdueDeadline(order, now) is { } due)
                    {
                        stuck.Add(MapToStuckOrder(order, due.Kind, due.AtIso, now));
                    }
                }

                if (next == null)
                {
                    break;
                }
            }
        }

        var response = new AdminStuckOrderListResponse
        {
            Source = source,
            Orders = stuck,
            NextToken = next == null ? null : PaginationTokenHelper.EncodeNextToken(next),
            Total = stuck.Count,
            AsOf = OrderCommitment.Iso(now)
        };
        return LambdaResponse.Ok(response, AppJsonContext.Default.AdminStuckOrderListResponse);
    }

    private static AdminStuckOrderDto MapToStuckOrder(Dictionary<string, object> order, string reason, string stuckSince, DateTime now)
    {
        var summary = MapToTransactionSummary(order);
        var age = OrderCommitment.TryParseUtc(stuckSince, out var since) ? (int)Math.Max(0, Math.Floor((now - since).TotalHours)) : 0;
        return new AdminStuckOrderDto
        {
            OrderId = summary.OrderId,
            Status = summary.Status,
            Stage = summary.Stage,
            Reason = reason,
            StuckSince = stuckSince,
            AgeHours = age,
            BuyerId = summary.BuyerId,
            BuyerName = summary.BuyerName,
            FarmerId = summary.FarmerId,
            FarmerName = summary.FarmerName,
            TruckerId = summary.TruckerId,
            TruckerName = summary.TruckerName,
            CropType = summary.CropType,
            OrderTotal = summary.OrderTotal,
            Currency = summary.Currency,
            EscrowId = NullIfEmpty(GetStringOrNull(order, AdminQueueFields.EscrowId)),
            CreatedAt = summary.CreatedAt,
            UpdatedAt = summary.UpdatedAt
        };
    }
}
