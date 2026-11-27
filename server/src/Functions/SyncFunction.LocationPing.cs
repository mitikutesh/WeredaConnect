using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// LT-05: the <c>location_ping</c> entity (live-trip-api.md 1.0.0 section 1, sync-protocol.md 1.31.0).
/// Create only. The trip's trucker records a check-in (<c>fix</c>) or a sharing event (<c>paused</c>,
/// <c>resumed</c>, <c>declined</c>) while a delivery on the trip is on the road. Rows go to the
/// trip-locations table only, never to the trip row (live-trip.md D6). The side effects of an
/// accepted fix (auto in transit, truck nearby, fraud signals) are in SyncFunction.CheckInEffects.cs (LT-07).
/// </summary>
public partial class SyncFunction
{
    /// <summary>Pages of the trip's deliveries read before the rule decides with what it has.</summary>
    private const int MaxTripDeliveryPages = 10;

    // Optional: the trip-locations table. Absent env (TRIP_LOCATIONS_TABLE) means a null repository,
    // and a location_ping answers an unprefixed error, which clients retry.
    private readonly ITripLocationRepository? _tripLocationRepo;

    // A delivery operation of this batch succeeded. The deliveries-by-trip read is an index read that
    // may not show that write yet, so a ping that would be told LOCATION_SHARING_ENDED (permanent) is
    // deferred instead and decided on the next sync.
    private bool _batchDeliveryWritten;

    /// <summary>The server clock of the location rules (the time window and <c>receivedAt</c>); tests set it.</summary>
    internal Func<DateTime> LocationClock { get; init; } = () => DateTime.UtcNow;

    private async Task<SyncOperationResult> ProcessLocationPingAsync(
        SyncOperationRequest operation,
        string? authenticatedUserId,
        ILambdaContext context)
    {
        if (_tripLocationRepo == null)
        {
            return LocationResult(operation, "error", "Location sharing is not enabled on this server yet.");
        }

        if (operation.Action != "create")
        {
            return LocationError(operation, LocationPingRules.ValidationCode,
                $"Unsupported action '{operation.Action}'. Location pings are create only.");
        }

        if (operation.Data == null || operation.Data.Count == 0)
        {
            return LocationError(operation, LocationPingRules.ValidationCode, "Operation requires a data payload.");
        }

        var errors = LocationPingRules.Parse(operation.EntityId, operation.Data, out var ping);
        if (errors.Count > 0 || ping == null)
        {
            return LocationError(operation, LocationPingRules.ValidationCode, string.Join(" ", errors));
        }

        var now = LocationClock();
        if (LocationPingRules.IsStale(ping.CapturedAtUtc, now))
        {
            return LocationError(operation, LocationPingRules.StaleCode,
                "capturedAt must be at most 2 minutes in the future and at most 7 days old.");
        }

        // The trip's trucker only. A missing trip gets the same answer, so the code says nothing
        // about which trips exist.
        var trip = await GetTripItemAsync(ping.TripId);
        if (trip == null || string.IsNullOrEmpty(authenticatedUserId) || AttrString(trip, "truckerId") != authenticatedUserId)
        {
            return LocationError(operation, LocationPingRules.NotTruckerCode,
                "Only the trip's trucker can share its location.");
        }

        var deliveries = await ReadTripDeliveriesAsync(ping.TripId, context);
        var coverage = LocationPingRules.Coverage(deliveries, ping.CapturedAtUtc);
        if (coverage == LocationCoverage.Ended && _batchDeliveryWritten)
        {
            coverage = LocationCoverage.NotActive;
        }

        if (coverage == LocationCoverage.NotActive)
        {
            // Not an error: the trip's first pickup may still be waiting in a queue (a pickup held back
            // until its QR handoff secret syncs), so the client re-queues without using a retry.
            return LocationResult(operation, "deferred",
                $"{LocationPingRules.NotActiveCode}: No delivery on this trip is on the road yet.");
        }

        if (coverage == LocationCoverage.Ended)
        {
            return LocationError(operation, LocationPingRules.SharingEndedCode,
                "Every delivery on this trip ended before this check-in. Location sharing has stopped.");
        }

        var sortKey = ping.SortKey;
        if (ping.Kind == LocationPingRules.KindFix)
        {
            var hour = await _tripLocationRepo.ListSortKeysAsync(ping.TripId, LocationPingRules.HourPrefix(ping.CapturedAtUtc));
            if (hour.Contains(sortKey, StringComparer.Ordinal))
            {
                return LocationRepeat(operation);
            }

            if (hour.Count(LocationPingRules.IsFixSortKey) >= LocationPingRules.MaxFixesPerHour)
            {
                return LocationError(operation, LocationPingRules.RateLimitedCode,
                    $"At most {LocationPingRules.MaxFixesPerHour} check-ins per trip in one clock hour.");
            }
        }

        var row = new TripLocationRow(
            ping.TripId,
            sortKey,
            authenticatedUserId,
            ping.Latitude is { } lat ? LocationPingRules.RoundCoordinate(lat) : null,
            ping.Longitude is { } lng ? LocationPingRules.RoundCoordinate(lng) : null,
            ping.AccuracyM,
            ping.Source,
            ping.Mocked,
            ping.PausedUntilUtc is { } until ? LocationPingRules.FormatUtc(until) : null,
            LocationPingRules.FormatUtc(now),
            LocationPingRules.ExpiresAt(ping.CapturedAtUtc));

        if (!await _tripLocationRepo.TryPutAsync(row))
        {
            return LocationRepeat(operation);
        }

        // LT-07: only a new fix has effects (a repeat's ran with its first write, or the next fix
        // runs them, since the moves and the stamp follow the deliveries' state). They never change
        // this answer.
        await RunCheckInEffectsAsync(ping, row, trip, deliveries, now, context);

        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "success",
            ServerVersion = 1
        };
    }

    /// <summary>
    /// Every delivery on the trip (the deliveries' <c>TripIdIndex</c>, all pages up to
    /// <see cref="MaxTripDeliveryPages"/>; a trip carries a handful). Soft-deleted rows are left out
    /// by the repository.
    /// </summary>
    private async Task<List<Dictionary<string, object>>> ReadTripDeliveriesAsync(string tripId, ILambdaContext context)
    {
        var deliveries = new List<Dictionary<string, object>>();
        Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>? startKey = null;
        var pages = 0;

        do
        {
            var (items, next) = await _deliveryRepo.QueryDeliveriesAsync(tripId: tripId, limit: 50, exclusiveStartKey: startKey);
            deliveries.AddRange(items);
            startKey = next;
        }
        while (startKey != null && ++pages < MaxTripDeliveryPages);

        if (startKey != null)
        {
            context.Logger.LogWarning(
                $"location_ping: trip {MoneyAlarmLog.LogId(tripId)} has more than {MaxTripDeliveryPages} pages of deliveries; deciding on the first ones.");
        }

        return deliveries;
    }

    private static SyncOperationResult LocationError(SyncOperationRequest operation, string code, string text)
        => LocationResult(operation, "error", $"{code}: {text}");

    private static SyncOperationResult LocationRepeat(SyncOperationRequest operation) => new()
    {
        OperationId = operation.OperationId,
        Status = "success",
        ServerVersion = 1,
        Message = "Location already stored."
    };

    private static SyncOperationResult LocationResult(SyncOperationRequest operation, string status, string message) => new()
    {
        OperationId = operation.OperationId,
        Status = status,
        Message = message
    };
}
