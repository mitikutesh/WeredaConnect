using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Helpers;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB trip listing repository using low-level API for AOT compatibility.
/// Manual marshalling — no Document Model or reflection.
///
/// Table schema (Trips):
///   PK: tripId (STRING)
///   SK: createdAt (STRING)
///   GSI StatusDepartureDateIndex: status (PK) + departureDate (SK)
///   GSI TruckerIdCreatedAtIndex: truckerId (PK) + createdAt (SK)
///   Booking attributes (ESC-15, absent on rows written before it): bookedQuintals (N),
///   remainingQuintals (N), bookedOrderIds (SS), acceptedOrderIds (SS).
///   ESC-17: cancelReason (S, <c>expired</c>) and cancelledAt (S) on a trip the order cron closed.
/// </summary>
public sealed class TripRepository : ITripRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;
    private readonly TimeProvider _clock;

    /// <param name="clock">The browse cutoff's clock (L-7); the system clock when null.</param>
    public TripRepository(IAmazonDynamoDB dynamoDb, string tableName, TimeProvider? clock = null)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryTripsAsync(
        string? fromWoreda = null,
        string? toWoreda = null,
        string? status = null,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 20;
        }

        // Default status to "open" for GSI query
        var queryStatus = string.IsNullOrEmpty(status) ? "open" : status;

        // Use StatusDepartureDateIndex GSI: PK = status, SK = departureDate
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "StatusDepartureDateIndex",
            KeyConditionExpression = "#status = :status",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":status", new AttributeValue { S = queryStatus } }
            },
            Limit = limit,
            ScanIndexForward = true // ascending by departureDate (soonest first)
        };

        // L-7: an open trip whose Ethiopia day is over has left (trucks run late, so a trip
        // stays listed all of its day). The cutoff is on the sort key, so DynamoDB skips the
        // departed trips instead of reading and filtering them, and a page is never left empty
        // by them. departureDate is stored UTC with milliseconds (SIM-60), so it compares as
        // a string. Other statuses keep every departure (a trip in transit left days ago).
        string? dropBefore = null;
        if (queryStatus == "open")
        {
            var cutoff = TripDeparture.CutoffIso(_clock.GetUtcNow().UtcDateTime);
            var keyFrom = cutoff;
            // A page token read before the cutoff moved (Ethiopia midnight came between two
            // pages) can sit below it, and DynamoDB refuses a start key outside the key
            // condition. The condition then starts at the token and the departed trips on
            // that page are dropped below.
            if (exclusiveStartKey != null
                && exclusiveStartKey.TryGetValue("departureDate", out var startDeparture)
                && startDeparture.S is { } startDate
                && string.CompareOrdinal(startDate, cutoff) < 0)
            {
                keyFrom = startDate;
                dropBefore = cutoff;
            }

            request.KeyConditionExpression += " AND #departureDate >= :departureCutoff";
            request.ExpressionAttributeNames["#departureDate"] = "departureDate";
            request.ExpressionAttributeValues[":departureCutoff"] = new AttributeValue { S = keyFrom };
        }

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        // Build filter expressions for fromWoreda/toWoreda
        var filterParts = new List<string>();

        // Exclude deleted items
        filterParts.Add("attribute_not_exists(#deletedAt)");
        request.ExpressionAttributeNames["#deletedAt"] = "deletedAt";

        if (!string.IsNullOrEmpty(fromWoreda))
        {
            filterParts.Add("#fromWoreda = :fromWoreda");
            request.ExpressionAttributeNames["#fromWoreda"] = "fromWoreda";
            request.ExpressionAttributeValues[":fromWoreda"] = new AttributeValue { S = fromWoreda };
        }

        if (!string.IsNullOrEmpty(toWoreda))
        {
            filterParts.Add("#toWoreda = :toWoreda");
            request.ExpressionAttributeNames["#toWoreda"] = "toWoreda";
            request.ExpressionAttributeValues[":toWoreda"] = new AttributeValue { S = toWoreda };
        }

        if (filterParts.Count > 0)
        {
            request.FilterExpression = string.Join(" AND ", filterParts);
        }

        var response = await _dynamoDb.QueryAsync(request);

        var items = response.Items
            .Where(item => dropBefore == null
                || (item.TryGetValue("departureDate", out var departure)
                    && string.CompareOrdinal(departure.S, dropBefore) >= 0))
            .Select(CropRepository.FromItem)
            .ToList();

        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryTripsByTruckerAsync(
        string truckerId,
        string? status = null,
        int limit = 20,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null)
    {
        if (limit < 1 || limit > 50)
        {
            limit = 20;
        }

        // Use TruckerIdCreatedAtIndex GSI: PK = truckerId, SK = createdAt
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "TruckerIdCreatedAtIndex",
            KeyConditionExpression = "#truckerId = :truckerId",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#truckerId", "truckerId" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":truckerId", new AttributeValue { S = truckerId } }
            },
            Limit = limit,
            ScanIndexForward = false // descending by createdAt (newest first)
        };

        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        // Build filter expressions
        var filterParts = new List<string>();

        // Exclude deleted items
        filterParts.Add("attribute_not_exists(#deletedAt)");
        request.ExpressionAttributeNames["#deletedAt"] = "deletedAt";

        // Optional status filter
        if (!string.IsNullOrEmpty(status))
        {
            filterParts.Add("#status = :status");
            request.ExpressionAttributeNames["#status"] = "status";
            request.ExpressionAttributeValues[":status"] = new AttributeValue { S = status };
        }

        if (filterParts.Count > 0)
        {
            request.FilterExpression = string.Join(" AND ", filterParts);
        }

        var response = await _dynamoDb.QueryAsync(request);

        var items = response.Items
            .Select(CropRepository.FromItem)
            .ToList();

        return (items, response.LastEvaluatedKey.Count > 0 ? response.LastEvaluatedKey : null);
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, object>?> GetByIdAsync(string tripId)
    {
        var item = await GetItemAsync(tripId);
        return item != null ? CropRepository.FromItem(item) : null;
    }

    /// <inheritdoc />
    public Task<Dictionary<string, AttributeValue>?> GetRawItemAsync(string tripId)
        => GetItemAsync(tripId, consistentRead: true);

    /// <inheritdoc />
    public async Task<TripReserveResult> TryReserveCapacityAsync(string tripId, string orderId, double quintals)
    {
        if (quintals <= 0 || double.IsNaN(quintals) || double.IsInfinity(quintals))
        {
            throw new ArgumentOutOfRangeException(nameof(quintals), quintals, "A booking must take a positive number of quintals.");
        }

        var existing = await GetItemAsync(tripId);
        if (existing == null)
        {
            return new TripReserveResult(TripReserveOutcome.NotFound, null);
        }

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = TripKey(tripId, existing["createdAt"].S),
            UpdateExpression =
                "SET #remaining = if_not_exists(#remaining, #capacity) - :q, #booked = if_not_exists(#booked, :zero) + :q, #updated = :now ADD #bookedIds :ids",
            ConditionExpression =
                "((attribute_not_exists(#remaining) AND #capacity >= :q) OR #remaining >= :q)"
                + " AND (attribute_not_exists(#bookedIds) OR NOT contains(#bookedIds, :orderId))"
                + " AND #status IN (:open, :matched) AND attribute_not_exists(#deletedAt)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#remaining", RemainingField },
                { "#capacity", "capacity" },
                { "#booked", BookedField },
                { "#updated", "updatedAt" },
                { "#bookedIds", BookedIdsField },
                { "#status", "status" },
                { "#deletedAt", "deletedAt" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":q", Number(quintals) },
                { ":zero", new AttributeValue { N = "0" } },
                { ":now", new AttributeValue { S = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) } },
                { ":ids", new AttributeValue { SS = new List<string> { orderId } } },
                { ":orderId", new AttributeValue { S = orderId } },
                { ":open", new AttributeValue { S = OpenStatus } },
                { ":matched", new AttributeValue { S = MatchedStatus } }
            },
            ReturnValues = ReturnValue.ALL_NEW
        };

        Dictionary<string, AttributeValue> written;
        try
        {
            written = (await _dynamoDb.UpdateItemAsync(request)).Attributes ?? new Dictionary<string, AttributeValue>();
        }
        catch (ConditionalCheckFailedException)
        {
            return await ClassifyRefusedReservationAsync(tripId, orderId);
        }

        var remaining = ReadNumber(written, RemainingField) ?? 0;
        if (remaining <= 0 && Str(written, "status") == OpenStatus)
        {
            // Fully booked: the trip leaves the open browse list. A lost race here only leaves the
            // trip open with zero room, which the next reservation's guard still refuses.
            await TrySetStatusAsync(tripId, existing["createdAt"].S, OpenStatus, MatchedStatus);
        }

        return new TripReserveResult(TripReserveOutcome.Reserved, remaining);
    }

    /// <summary>
    /// The reservation's condition failed: read the row again (strongly consistent) and say why.
    /// Order of the checks matters: a replayed funding on a trip that has since filled up or been
    /// cancelled is still "already reserved", so the caller treats it as done.
    /// </summary>
    private async Task<TripReserveResult> ClassifyRefusedReservationAsync(string tripId, string orderId)
    {
        var row = await GetItemAsync(tripId, consistentRead: true);
        if (row == null)
        {
            return new TripReserveResult(TripReserveOutcome.NotFound, null);
        }

        var remaining = ReadNumber(row, RemainingField) ?? ReadNumber(row, "capacity");
        if (SetContains(row, BookedIdsField, orderId))
        {
            return new TripReserveResult(TripReserveOutcome.AlreadyReserved, remaining);
        }

        if (row.ContainsKey("deletedAt") || Str(row, "status") is not (OpenStatus or MatchedStatus))
        {
            return new TripReserveResult(TripReserveOutcome.Unavailable, null);
        }

        return new TripReserveResult(TripReserveOutcome.Full, remaining);
    }

    /// <inheritdoc />
    public async Task<bool> TryReleaseCapacityAsync(string tripId, string orderId, double quintals)
    {
        var existing = await GetItemAsync(tripId, consistentRead: true);
        if (existing == null)
        {
            return false;
        }

        // DELETE on a set that does not exist is a validation error, not a no-op, so the accepted
        // set is only touched when the row shows the order in it.
        var alsoAccepted = SetContains(existing, AcceptedIdsField, orderId);

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = TripKey(tripId, existing["createdAt"].S),
            UpdateExpression = "SET #remaining = #remaining + :q, #booked = #booked - :q, #updated = :now DELETE #bookedIds :ids"
                               + (alsoAccepted ? ", #acceptedIds :ids" : string.Empty),
            ConditionExpression = "contains(#bookedIds, :orderId)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#remaining", RemainingField },
                { "#booked", BookedField },
                { "#updated", "updatedAt" },
                { "#bookedIds", BookedIdsField }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":q", Number(quintals) },
                { ":now", new AttributeValue { S = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) } },
                { ":ids", new AttributeValue { SS = new List<string> { orderId } } },
                { ":orderId", new AttributeValue { S = orderId } }
            },
            ReturnValues = ReturnValue.ALL_NEW
        };
        if (alsoAccepted)
        {
            request.ExpressionAttributeNames["#acceptedIds"] = AcceptedIdsField;
        }

        Dictionary<string, AttributeValue> written;
        try
        {
            written = (await _dynamoDb.UpdateItemAsync(request)).Attributes ?? new Dictionary<string, AttributeValue>();
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }

        var remaining = ReadNumber(written, RemainingField) ?? 0;
        if (remaining > 0 && Str(written, "status") == MatchedStatus && !written.ContainsKey("deletedAt"))
        {
            // Room again: back on the open browse list.
            await TrySetStatusAsync(tripId, existing["createdAt"].S, MatchedStatus, OpenStatus);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> TrySetStatusAsync(string tripId, string fromStatus, string toStatus)
    {
        var existing = await GetItemAsync(tripId);
        return existing != null && await TrySetStatusAsync(tripId, existing["createdAt"].S, fromStatus, toStatus);
    }

    /// <summary>The status write for a trip whose sort key is already known.</summary>
    private async Task<bool> TrySetStatusAsync(string tripId, string createdAt, string fromStatus, string toStatus)
    {
        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = TripKey(tripId, createdAt),
            UpdateExpression = "SET #status = :to, #updated = :now ADD #v :one",
            ConditionExpression = "#status = :from AND attribute_not_exists(#deletedAt)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" },
                { "#updated", "updatedAt" },
                { "#v", "version" },
                { "#deletedAt", "deletedAt" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":to", new AttributeValue { S = toStatus } },
                { ":from", new AttributeValue { S = fromStatus } },
                { ":now", new AttributeValue { S = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) } },
                { ":one", new AttributeValue { N = "1" } }
            }
        };

        try
        {
            await _dynamoDb.UpdateItemAsync(request);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public TransactWriteItem BuildConfirmBookingItem(string tripId, string tripCreatedAt, string orderId, string nowIso)
        => new()
        {
            Update = new Update
            {
                TableName = _tableName,
                Key = TripKey(tripId, tripCreatedAt),
                UpdateExpression = "SET #updated = :now ADD #acceptedIds :ids",
                ConditionExpression =
                    "contains(#bookedIds, :orderId) AND #status IN (:open, :matched) AND attribute_not_exists(#deletedAt)",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    { "#updated", "updatedAt" },
                    { "#acceptedIds", AcceptedIdsField },
                    { "#bookedIds", BookedIdsField },
                    { "#status", "status" },
                    { "#deletedAt", "deletedAt" }
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    { ":now", new AttributeValue { S = nowIso } },
                    { ":ids", new AttributeValue { SS = new List<string> { orderId } } },
                    { ":orderId", new AttributeValue { S = orderId } },
                    { ":open", new AttributeValue { S = OpenStatus } },
                    { ":matched", new AttributeValue { S = MatchedStatus } }
                }
            }
        };

    /// <inheritdoc />
    public async Task<(List<Dictionary<string, AttributeValue>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey)> QueryDepartedOpenAsync(
        string departedBeforeIso,
        int limit,
        Dictionary<string, AttributeValue>? exclusiveStartKey = null)
    {
        // The browse reads the same partition from the cutoff up (L-7); this reads what lies below
        // it. departureDate is stored UTC with milliseconds (SIM-60), so it compares as a string.
        // Newest first: trips that stay open after they left (booked, or kept by an order) collect
        // at the old end for good, so a read from that end would see the same rows every run and
        // never reach a trip that has just crossed the cutoff.
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "StatusDepartureDateIndex",
            KeyConditionExpression = "#status = :open AND #departureDate < :cutoff",
            FilterExpression = "attribute_not_exists(#deletedAt)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" },
                { "#departureDate", "departureDate" },
                { "#deletedAt", "deletedAt" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":open", new AttributeValue { S = OpenStatus } },
                { ":cutoff", new AttributeValue { S = departedBeforeIso } }
            },
            Limit = limit,
            ScanIndexForward = false
        };
        if (exclusiveStartKey != null)
        {
            request.ExclusiveStartKey = exclusiveStartKey;
        }

        var response = await _dynamoDb.QueryAsync(request);
        return (response.Items ?? new List<Dictionary<string, AttributeValue>>(),
            response.LastEvaluatedKey is { Count: > 0 } next ? next : null);
    }

    /// <inheritdoc />
    public async Task<bool> TryExpireUnbookedAsync(
        string tripId, string createdAt, int expectedVersion, string departedBeforeIso, string nowIso)
    {
        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = TripKey(tripId, createdAt),
            UpdateExpression =
                "SET #status = :cancelled, #cancelReason = :expired, #cancelledAt = :now, #updated = :now, #v = :next",
            ConditionExpression =
                "#v = :expected AND #status = :open AND attribute_not_exists(#deletedAt)"
                + " AND #departureDate < :cutoff AND attribute_not_exists(#bookedIds)"
                + " AND (attribute_not_exists(#booked) OR #booked <= :zero)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" },
                { "#cancelReason", "cancelReason" },
                { "#cancelledAt", "cancelledAt" },
                { "#updated", "updatedAt" },
                { "#v", "version" },
                { "#deletedAt", "deletedAt" },
                { "#departureDate", "departureDate" },
                { "#bookedIds", BookedIdsField },
                { "#booked", BookedField }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":cancelled", new AttributeValue { S = CancelledStatus } },
                { ":expired", new AttributeValue { S = TripLifecycle.ExpiredCancelReason } },
                { ":now", new AttributeValue { S = nowIso } },
                { ":next", new AttributeValue { N = (expectedVersion + 1).ToString(CultureInfo.InvariantCulture) } },
                { ":expected", new AttributeValue { N = expectedVersion.ToString(CultureInfo.InvariantCulture) } },
                { ":open", new AttributeValue { S = OpenStatus } },
                { ":cutoff", new AttributeValue { S = departedBeforeIso } },
                { ":zero", new AttributeValue { N = "0" } }
            }
        };

        try
        {
            await _dynamoDb.UpdateItemAsync(request);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryMarkDeliveredAsync(string tripId, string createdAt, int expectedVersion, string nowIso)
    {
        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = TripKey(tripId, createdAt),
            UpdateExpression = "SET #status = :delivered, #updated = :now, #v = :next",
            ConditionExpression = "#v = :expected AND #status = :inTransit AND attribute_not_exists(#deletedAt)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "status" },
                { "#updated", "updatedAt" },
                { "#v", "version" },
                { "#deletedAt", "deletedAt" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":delivered", new AttributeValue { S = DeliveredStatus } },
                { ":inTransit", new AttributeValue { S = InTransitStatus } },
                { ":now", new AttributeValue { S = nowIso } },
                { ":next", new AttributeValue { N = (expectedVersion + 1).ToString(CultureInfo.InvariantCulture) } },
                { ":expected", new AttributeValue { N = expectedVersion.ToString(CultureInfo.InvariantCulture) } }
            }
        };

        try
        {
            await _dynamoDb.UpdateItemAsync(request);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <summary>Trip row attributes of the ESC-15 bookings.</summary>
    private const string BookedField = "bookedQuintals";
    private const string RemainingField = "remainingQuintals";
    private const string BookedIdsField = "bookedOrderIds";
    private const string AcceptedIdsField = "acceptedOrderIds";

    /// <summary>A trip with room left; <see cref="MatchedStatus"/> is one that is fully booked.</summary>
    private const string OpenStatus = "open";
    private const string MatchedStatus = "matched";

    /// <summary>The statuses of the ESC-17 system moves (an unbooked trip that left, a finished trip).</summary>
    private const string CancelledStatus = "cancelled";
    private const string InTransitStatus = "inTransit";
    private const string DeliveredStatus = "delivered";

    private static Dictionary<string, AttributeValue> TripKey(string tripId, string createdAt) => new()
    {
        { "tripId", new AttributeValue { S = tripId } },
        { "createdAt", new AttributeValue { S = createdAt } }
    };

    private static AttributeValue Number(double value)
        => new() { N = value.ToString("R", CultureInfo.InvariantCulture) };

    private static double? ReadNumber(Dictionary<string, AttributeValue> row, string key)
        => row.TryGetValue(key, out var value) && value.N != null
           && double.TryParse(value.N, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static bool SetContains(Dictionary<string, AttributeValue> row, string key, string member)
        => row.TryGetValue(key, out var value) && value.SS != null && value.SS.Contains(member);

    private static string Str(Dictionary<string, AttributeValue> row, string key)
        => row.TryGetValue(key, out var value) ? value.S ?? string.Empty : string.Empty;

    /// <summary>
    /// Query for an item by tripId (partition key). Since sort key is createdAt,
    /// we query with limit 1 to get the single matching item. The booking writes read it
    /// strongly consistent before classifying a refused condition.
    /// </summary>
    private async Task<Dictionary<string, AttributeValue>?> GetItemAsync(string tripId, bool consistentRead = false)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "tripId = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":id", new AttributeValue { S = tripId } }
            },
            Limit = 1,
            ConsistentRead = consistentRead
        };

        var response = await _dynamoDb.QueryAsync(request);
        return response.Items.Count > 0 ? response.Items[0] : null;
    }
}
