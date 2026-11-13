using System.Globalization;
using System.Text.Json;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// ESC-34 (spec 4.8, delivery-verification-api.md 2.6.0 section "Arrival check-ins"): the arrival
/// check-ins of an accepted order and the pickup judgement at T7, as pure rules on the order row and
/// the server clock. The sync handler (<c>handoff_check_in</c>) and the order cron do the reads and
/// the writes; everything they decide is here, so every row of the spec's table is tested on its own.
/// <para>
/// Behind the flag <c>pickupClassification</c> (<see cref="EnabledEnv"/>, off everywhere), which only
/// works with <c>COMMITMENTS_ENABLED</c>. With it on, the transition to <c>accepted</c> stamps
/// <see cref="Field"/> <c>pending</c>: only such an order takes check-ins and is judged, so an order
/// accepted before the flag (or while it was off) keeps the P1 rules (spec K2).
/// </para>
/// <para>
/// Clocks: every decision reads the server's receipt time (<c>receivedAt</c>) of a check-in and the
/// server's own clock. The phone's <c>capturedAt</c> is kept as evidence only, and only when plausible
/// (<see cref="PlausibleCapturedAt"/>), so a phone whose clock is wrong can neither open the window early
/// nor make a late check-in count.
/// </para>
/// </summary>
public static class PickupJudgement
{
    /// <summary>The Lambda env flag <c>pickupClassification</c> (sync and order-cron).</summary>
    public const string EnabledEnv = "PICKUP_CLASSIFICATION_ENABLED";

    /// <summary>Whether <c>pickupClassification</c> is on in this Lambda. Off by default.</summary>
    public static bool IsEnabled
        => string.Equals(Environment.GetEnvironmentVariable(EnabledEnv), "true", StringComparison.OrdinalIgnoreCase);

    public const string EntityType = "handoff_check_in";

    /// <summary>Server-only order field: <c>pending</c> until judged, then the outcome (<see cref="Outcome"/>).</summary>
    public const string Field = "pickupJudgement";

    /// <summary>Server-only order field: when the order cron judged the pickup (ISO 8601 UTC).</summary>
    public const string JudgedAtField = "pickupJudgedAt";

    /// <summary>Server-only order map: one entry per check-in kind (<see cref="EntryReceivedAt"/> and the rest).</summary>
    public const string CheckInsField = "checkIns";

    /// <summary>
    /// Server-only order fields: the listing's point as it was when the order became judgeable. The
    /// check-ins are measured against it, so a farmer who moves the listing afterwards cannot push the
    /// trucker's check-in out of range (FUP-204). Never returned to a client.
    /// </summary>
    public const string PickupLatitudeField = "pickupLatitude";

    public const string PickupLongitudeField = "pickupLongitude";

    /// <summary>Values of <see cref="Field"/>.</summary>
    public static class Outcome
    {
        /// <summary>Accepted with the flag on: the pickup will be judged at <c>pickupDeadlineAt</c>.</summary>
        public const string Pending = "pending";

        /// <summary>The goods were picked up (the order lagged its delivery): the normal flow.</summary>
        public const string Scanned = "scanned";

        public const string TruckerNoShow = "trucker_no_show";
        public const string FarmerNoShow = "farmer_no_show";
        public const string BuyerNoShow = "buyer_no_show";

        /// <summary>Both checked in and nothing was scanned: the admin queue, money held.</summary>
        public const string BothPresentNoScan = "both_present_no_scan";

        /// <summary>A farmer no-show that rests on a check-in that could not be measured: the admin queue.</summary>
        public const string UncheckedCheckIn = "unchecked_check_in";
    }

    /// <summary>The check-in kinds (<c>arrived_dropoff</c> is ESC-36's, see <see cref="DropoffJudgement"/>).</summary>
    public static class Kind
    {
        public const string ArrivedPickup = "arrived_pickup";
        public const string CropsReady = "crops_ready";
        public const string ArrivedDropoff = "arrived_dropoff";
    }

    /// <summary>The order's <c>transportFailure</c> of a trucker no-show (reputation rule R10).</summary>
    public const string TruckerNoShowFailure = "trucker_no_show";

    /// <summary>The order's <c>cancelReason</c> of a farmer no-show (reputation rule R11).</summary>
    public const string FarmerNoShowReason = "farmer_no_show";

    /// <summary>The order's <c>cancelReason</c> of a self-pickup buyer who did not come (no points in P3).</summary>
    public const string BuyerNoShowReason = "buyer_no_show";

    /// <summary>The <c>farmerResponse</c> / <c>truckerResponse</c> of the party judged a no-show (spec 4.4).</summary>
    public const string NoShowResponse = "no_show";

    /// <summary>Admin queue reasons (<c>adminQueueReason</c>).</summary>
    public const string ReasonBothPresent = Outcome.BothPresentNoScan;
    public const string ReasonUnchecked = Outcome.UncheckedCheckIn;
    public const string ReasonLateEvidence = "late_pickup_evidence";

    // Refusal codes of handoff_check_in; each is permanent (the client drops the check-in and says why).
    public const string ValidationCode = "CHECKIN_VALIDATION";
    public const string NotPartyCode = "CHECKIN_NOT_PARTY";
    public const string TooEarlyCode = "CHECKIN_TOO_EARLY";
    public const string TooFarCode = "CHECKIN_TOO_FAR";
    public const string NotOpenCode = "CHECKIN_NOT_OPEN";
    public const string TooLateCode = "CHECKIN_TOO_LATE";

    // The fields of one check-in entry in the checkIns map.
    public const string EntryReceivedAt = "receivedAt";
    public const string EntryCapturedAt = "capturedAt";
    public const string EntryLatitude = "latitude";
    public const string EntryLongitude = "longitude";
    public const string EntryAccuracyM = "accuracyM";
    public const string EntryBy = "by";
    public const string EntryDistanceChecked = "distanceChecked";
    public const string EntryDistanceKm = "distanceKm";
    public const string EntryLate = "late";

    /// <summary>A farmer's fix more than 5 km from the farm: kept as evidence, never measured (GPS is optional there).</summary>
    public const string EntryOutOfRange = "outOfRange";

    /// <summary>The check-in radius around the pickup point, as the built pickup GPS rule.</summary>
    public const double RadiusKm = 5.0;

    /// <summary>The window opens this long before <c>pickupDate</c> (spec 4.8).</summary>
    public static readonly TimeSpan OpensBeforePickup = TimeSpan.FromHours(24);

    /// <summary>
    /// The judgement waits this long after <c>pickupDeadlineAt</c>: longer than a sync request may run
    /// (30 seconds), so every check-in and scan the server received before the deadline is written when
    /// the order cron reads the row. No check-in can count once the deadline passed, so the wait changes
    /// no outcome; it only closes the race between a check-in's write and the judgement's read.
    /// </summary>
    public static readonly TimeSpan SettleMargin = TimeSpan.FromMinutes(1);

    /// <summary>A phone's <c>capturedAt</c> later than its receipt by more than this is not kept.</summary>
    public static readonly TimeSpan CapturedAtMaxAhead = TimeSpan.FromMinutes(5);

    /// <summary>A phone's <c>capturedAt</c> older than this at receipt is not kept.</summary>
    public static readonly TimeSpan CapturedAtMaxAge = TimeSpan.FromDays(30);

    public const double MinLatitude = LocationPingRules.MinLatitude;
    public const double MaxLatitude = LocationPingRules.MaxLatitude;
    public const double MinLongitude = LocationPingRules.MinLongitude;
    public const double MaxLongitude = LocationPingRules.MaxLongitude;
    public const double MaxAccuracyM = LocationPingRules.MaxAccuracyM;

    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    {
        "orderId", "kind", "capturedAt", "latitude", "longitude", "accuracyM", "version"
    };

    // ── the order ───────────────────────────────────────────────────────────

    /// <summary>Whether the order was stamped judgeable (any value of <see cref="Field"/>).</summary>
    public static bool IsStamped(IReadOnlyDictionary<string, object> order) => Str(order, Field).Length > 0;

    /// <summary>Whether the order waits for its judgement.</summary>
    public static bool IsPending(IReadOnlyDictionary<string, object> order) => Str(order, Field) == Outcome.Pending;

    /// <summary>Whether the order cron has judged the pickup.</summary>
    public static bool IsJudged(IReadOnlyDictionary<string, object> order) => Str(order, JudgedAtField).Length > 0;

    /// <summary>A self-pickup order: the buyer takes the trucker's column.</summary>
    public static bool IsSelfPickup(IReadOnlyDictionary<string, object> order)
        => IsSelfPickup(Str(order, OrderCommitment.FulfillmentField), Str(order, "tripId"), Str(order, "truckerId"));

    /// <summary>
    /// The one self-pickup rule (the check-in's party, the judgement's column and the reputation stream's
    /// reporter, FUP-204): <c>fulfillment: self_pickup</c>, or a row without <c>fulfillment</c> (funded before
    /// the commitments) that names no trip and no trucker.
    /// </summary>
    public static bool IsSelfPickup(string fulfillment, string tripId, string truckerId)
        => fulfillment == OrderCommitment.Fulfillment.SelfPickup
           || (fulfillment.Length == 0 && tripId.Length == 0 && truckerId.Length == 0);

    /// <summary>The user who checks in for <paramref name="kind"/> on this order, or empty.</summary>
    public static string PartyOf(IReadOnlyDictionary<string, object> order, string kind) => kind switch
    {
        Kind.ArrivedPickup => IsSelfPickup(order) ? Str(order, "buyerId") : Str(order, "truckerId"),
        Kind.CropsReady => Str(order, "farmerId"),
        _ => string.Empty
    };

    /// <summary>The order's pickup deadline (T7 with the grace), or null.</summary>
    public static DateTime? DeadlineOf(IReadOnlyDictionary<string, object> order)
        => OrderCommitment.TryParseUtc(Str(order, PickupDeadline.Field), out var at) ? at : null;

    /// <summary>When the order cron judges it: the deadline plus <see cref="SettleMargin"/>.</summary>
    public static DateTime? JudgeAt(IReadOnlyDictionary<string, object> order)
        => DeadlineOf(order) is { } deadline ? deadline + SettleMargin : null;

    /// <summary>
    /// When the check-in window opens: <c>pickupDate</c> minus 24 hours. A self-pickup order has no
    /// <c>pickupDate</c> (the buyer comes within the window of acceptance), so it is open from the start.
    /// </summary>
    public static DateTime? OpensAt(IReadOnlyDictionary<string, object> order)
        => OrderCommitment.TryParseUtc(Str(order, OrderCommitment.PickupDateField), out var pickupDate)
            ? pickupDate - OpensBeforePickup
            : null;

    // ── the check-in operation ──────────────────────────────────────────────

    /// <summary>
    /// Parse and validate a <c>handoff_check_in</c> payload. Returns the problems (empty when valid);
    /// <paramref name="checkIn"/> is set only when there are none. ESC-36: the kinds taken follow the flags,
    /// <paramref name="pickupKinds"/> (<c>pickupClassification</c>) and <paramref name="dropoffKind"/>
    /// (<c>dropoffClassification</c>); a kind whose flag is off is refused as before it existed.
    /// </summary>
    public static List<string> Parse(
        string entityId, Dictionary<string, object> data, out ParsedCheckIn? checkIn,
        bool pickupKinds = true, bool dropoffKind = false)
    {
        checkIn = null;
        var errors = new List<string>();

        var unknown = data.Keys.Where(k => !AllowedFields.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            errors.Add($"Unknown fields: {string.Join(", ", unknown)}.");
        }

        var separator = entityId.LastIndexOf('#');
        var idOrder = separator > 0 ? entityId[..separator] : string.Empty;
        var idKind = separator > 0 ? entityId[(separator + 1)..] : string.Empty;

        var orderId = ReadString(data, "orderId");
        var kind = ReadString(data, "kind");
        if (string.IsNullOrWhiteSpace(orderId) || orderId.Length > 128)
        {
            errors.Add("orderId is required.");
        }

        var taken = (pickupKinds ? new[] { Kind.ArrivedPickup, Kind.CropsReady } : Array.Empty<string>())
            .Concat(dropoffKind ? new[] { Kind.ArrivedDropoff } : Array.Empty<string>())
            .ToList();
        if (kind == Kind.ArrivedDropoff && !dropoffKind)
        {
            errors.Add("arrived_dropoff is not taken yet (it comes with the drop-off check-in).");
        }
        else if (kind == null || !taken.Contains(kind))
        {
            errors.Add($"kind must be {string.Join(" or ", taken)}.");
        }

        if (!string.IsNullOrWhiteSpace(orderId) && !string.IsNullOrWhiteSpace(kind)
            && (idOrder != orderId || idKind != kind))
        {
            errors.Add("entityId must be '{orderId}#{kind}'.");
        }

        DateTime? capturedAt = null;
        if (data.TryGetValue("capturedAt", out var capturedRaw) && !IsNull(capturedRaw))
        {
            if (ReadString(data, "capturedAt") is { } text
                && DateTime.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            {
                capturedAt = parsed;
            }
            else
            {
                errors.Add("capturedAt must be an ISO 8601 date and time.");
            }
        }

        var hasLatitude = data.TryGetValue("latitude", out var latRaw) && !IsNull(latRaw);
        var hasLongitude = data.TryGetValue("longitude", out var lonRaw) && !IsNull(lonRaw);
        double? latitude = null, longitude = null, accuracy = null;
        if (hasLatitude != hasLongitude)
        {
            errors.Add("latitude and longitude come together.");
        }
        else if (hasLatitude)
        {
            latitude = Bounded(latRaw!, "latitude", MinLatitude, MaxLatitude, errors);
            longitude = Bounded(lonRaw!, "longitude", MinLongitude, MaxLongitude, errors);
            if (data.TryGetValue("accuracyM", out var accRaw) && !IsNull(accRaw))
            {
                accuracy = Bounded(accRaw!, "accuracyM", 0, MaxAccuracyM, errors);
            }
        }
        else if (kind is Kind.ArrivedPickup or Kind.ArrivedDropoff)
        {
            errors.Add($"latitude and longitude are required for {kind}.");
        }

        if (errors.Count == 0)
        {
            checkIn = new ParsedCheckIn(orderId!, kind!, capturedAt, latitude, longitude, accuracy);
        }

        return errors;
    }

    /// <summary>The phone's capture time when it is plausible at <paramref name="receivedAt"/>, else null.</summary>
    public static DateTime? PlausibleCapturedAt(DateTime? capturedAt, DateTime receivedAt)
        => capturedAt is { } at && at <= receivedAt + CapturedAtMaxAhead && at >= receivedAt - CapturedAtMaxAge
            ? at
            : null;

    /// <summary>
    /// The pickup point a check-in is measured against: the point stamped when the order became
    /// judgeable, else the listing's current one (an order stamped while the listing could not be read),
    /// else none.
    /// </summary>
    public static TrackPoint? PointFor(IReadOnlyDictionary<string, object> order, IReadOnlyDictionary<string, object>? listing)
    {
        if (TryNumber(order, PickupLatitudeField, out var lat) && TryNumber(order, PickupLongitudeField, out var lon))
        {
            return new TrackPoint(lat, lon);
        }

        return listing != null && TryNumber(listing, "latitude", out var listLat) && TryNumber(listing, "longitude", out var listLon)
            ? new TrackPoint(listLat, listLon)
            : null;
    }

    /// <summary>The distance in km from the check-in's fix to <paramref name="point"/>, or null when either is missing.</summary>
    public static double? DistanceKm(ParsedCheckIn checkIn, TrackPoint? point)
        => checkIn.Latitude is { } lat && checkIn.Longitude is { } lon && point != null
            ? GpsValidator.HaversineDistance(lat, lon, point.Latitude, point.Longitude)
            : null;

    /// <summary>
    /// The stored entry of one check-in (<see cref="CheckInsField"/>). A fix more than
    /// <see cref="RadiusKm"/> away (only a farmer's reaches here: an arrival that far is refused) is kept
    /// with <see cref="EntryOutOfRange"/> and counts as unmeasured.
    /// </summary>
    public static Dictionary<string, object> Entry(
        ParsedCheckIn checkIn, string callerId, DateTime receivedAt, double? distanceKm, bool late)
    {
        var outOfRange = distanceKm is { } measured && measured > RadiusKm;
        var entry = new Dictionary<string, object>
        {
            { EntryReceivedAt, OrderCommitment.Iso(receivedAt) },
            { EntryBy, callerId },
            { EntryDistanceChecked, distanceKm != null && !outOfRange },
            { EntryLate, late }
        };
        if (outOfRange)
        {
            entry[EntryOutOfRange] = true;
        }

        if (PlausibleCapturedAt(checkIn.CapturedAt, receivedAt) is { } captured)
        {
            entry[EntryCapturedAt] = OrderCommitment.Iso(captured);
        }

        if (checkIn.Latitude is { } lat && checkIn.Longitude is { } lon)
        {
            entry[EntryLatitude] = LocationPingRules.RoundCoordinate(lat);
            entry[EntryLongitude] = LocationPingRules.RoundCoordinate(lon);
        }

        if (checkIn.AccuracyM is { } accuracy)
        {
            entry[EntryAccuracyM] = Math.Round(accuracy, 1);
        }

        if (distanceKm is { } km)
        {
            entry[EntryDistanceKm] = Math.Round(km, 2);
        }

        return entry;
    }

    // ── the judgement (spec 4.8, T7) ────────────────────────────────────────

    /// <summary>
    /// The outcome of a pending order's judgement. <paramref name="pickedUp"/> says the order's delivery
    /// left <c>pending</c> (a scan reached the server; the order can lag its delivery). Only check-ins
    /// the server received by <paramref name="deadline"/> count:
    /// <list type="table">
    /// <item>scan: <see cref="Outcome.Scanned"/>, the normal flow;</item>
    /// <item>no arrival: <see cref="Outcome.TruckerNoShow"/> (a self-pickup: <see cref="Outcome.BuyerNoShow"/>),
    /// whatever the farmer did;</item>
    /// <item>arrival, no "crops are ready": <see cref="Outcome.FarmerNoShow"/>, or
    /// <see cref="Outcome.UncheckedCheckIn"/> when the arrival could not be measured;</item>
    /// <item>both: <see cref="Outcome.BothPresentNoScan"/>.</item>
    /// </list>
    /// </summary>
    public static string Decide(IReadOnlyDictionary<string, object> order, bool pickedUp, DateTime deadline)
    {
        if (pickedUp)
        {
            return Outcome.Scanned;
        }

        var arrived = CountedEntry(order, Kind.ArrivedPickup, deadline);
        if (arrived == null)
        {
            return IsSelfPickup(order) ? Outcome.BuyerNoShow : Outcome.TruckerNoShow;
        }

        if (CountedEntry(order, Kind.CropsReady, deadline) != null)
        {
            return Outcome.BothPresentNoScan;
        }

        return Bool(arrived, EntryDistanceChecked) ? Outcome.FarmerNoShow : Outcome.UncheckedCheckIn;
    }

    /// <summary>
    /// Whether the party an outcome judged absent has a check-in that arrived too late to count: the
    /// evidence an admin should see next to the judgement (<see cref="ReasonLateEvidence"/>).
    /// </summary>
    public static bool HasLateEvidenceAgainst(IReadOnlyDictionary<string, object> order, string outcome) => outcome switch
    {
        Outcome.TruckerNoShow or Outcome.BuyerNoShow => EntryOf(order, Kind.ArrivedPickup) != null,
        Outcome.FarmerNoShow => EntryOf(order, Kind.CropsReady) != null,
        _ => false
    };

    /// <summary>Whether a late check-in of <paramref name="kind"/> contradicts the judged outcome.</summary>
    public static bool Contradicts(string outcome, string kind) => kind switch
    {
        Kind.ArrivedPickup => outcome is Outcome.TruckerNoShow or Outcome.BuyerNoShow,
        Kind.CropsReady => outcome == Outcome.FarmerNoShow,
        _ => false
    };

    /// <summary>The entry of <paramref name="kind"/> when it counts: received by the deadline and not late.</summary>
    public static IReadOnlyDictionary<string, object>? CountedEntry(
        IReadOnlyDictionary<string, object> order, string kind, DateTime deadline)
    {
        var entry = EntryOf(order, kind);
        return entry != null
               && !Bool(entry, EntryLate)
               && OrderCommitment.TryParseUtc(Str(entry, EntryReceivedAt), out var received)
               && received <= deadline
            ? entry
            : null;
    }

    /// <summary>Whether a stored entry came after the deadline (kept as evidence, never counted).</summary>
    public static bool IsLateEntry(IReadOnlyDictionary<string, object> entry) => Bool(entry, EntryLate);

    /// <summary>The stored entry of <paramref name="kind"/>, or null.</summary>
    public static IReadOnlyDictionary<string, object>? EntryOf(IReadOnlyDictionary<string, object> order, string kind)
        => order.TryGetValue(CheckInsField, out var raw)
           && raw is IReadOnlyDictionary<string, object> map
           && map.TryGetValue(kind, out var entry)
           && entry is IReadOnlyDictionary<string, object> found
            ? found
            : null;

    // ── what the parties read ───────────────────────────────────────────────

    /// <summary>
    /// The check-ins as the parties and the admin read them: per kind the server's receipt time, the
    /// plausible capture time, whether the distance was checked and whether it came late. Never the
    /// coordinates, the accuracy or who sent it. Null when there is none.
    /// </summary>
    public static Dictionary<string, Models.OrderCheckInDto>? ViewOf(IReadOnlyDictionary<string, object> order)
    {
        Dictionary<string, Models.OrderCheckInDto>? view = null;
        foreach (var kind in new[] { Kind.ArrivedPickup, Kind.CropsReady })
        {
            if (EntryOf(order, kind) is not { } entry || Str(entry, EntryReceivedAt).Length == 0)
            {
                continue;
            }

            view ??= new Dictionary<string, Models.OrderCheckInDto>(StringComparer.Ordinal);
            view[kind] = new Models.OrderCheckInDto
            {
                ReceivedAt = Str(entry, EntryReceivedAt),
                CapturedAt = Str(entry, EntryCapturedAt) is { Length: > 0 } captured ? captured : null,
                DistanceChecked = Bool(entry, EntryDistanceChecked),
                Late = Bool(entry, EntryLate)
            };
        }

        // ESC-36: the trucker's drop-off check-in is stored on its own (dropoffCheckIn) and read as one more kind.
        if (DropoffJudgement.ViewOf(order) is { } dropoff)
        {
            view ??= new Dictionary<string, Models.OrderCheckInDto>(StringComparer.Ordinal);
            view[Kind.ArrivedDropoff] = dropoff;
        }

        return view;
    }

    /// <summary>
    /// A sync result whose order row goes back to a client: the check-ins replaced by
    /// <see cref="ViewOf"/>, and the stamped pickup point and an admin's re-open stamp (FUP-287,
    /// <see cref="PickupReopen.Field"/>) left out (ESC-36: also the raw drop-off check-in and the drop-off
    /// points stamped at pickup). Any other result is returned unchanged.
    /// </summary>
    public static Models.SyncOperationResult ForClient(Models.SyncOperationResult result)
    {
        if (result.ServerData is not { } row
            || !(row.ContainsKey(CheckInsField) || row.ContainsKey(PickupLatitudeField) || row.ContainsKey(PickupLongitudeField)
                 || row.ContainsKey(PickupReopen.Field) || DropoffJudgement.HiddenFields.Any(row.ContainsKey)))
        {
            return result;
        }

        var scrubbed = new Dictionary<string, object>(row);
        scrubbed.Remove(PickupLatitudeField);
        scrubbed.Remove(PickupLongitudeField);
        scrubbed.Remove(PickupReopen.Field);
        scrubbed.Remove(CheckInsField);
        foreach (var hidden in DropoffJudgement.HiddenFields)
        {
            scrubbed.Remove(hidden);
        }
        if (ViewOf(row) is { } view)
        {
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var (kind, entry) in view)
            {
                var item = new Dictionary<string, object>
                {
                    { EntryReceivedAt, entry.ReceivedAt },
                    { EntryDistanceChecked, entry.DistanceChecked },
                    { EntryLate, entry.Late }
                };
                if (entry.CapturedAt != null)
                {
                    item[EntryCapturedAt] = entry.CapturedAt;
                }

                map[kind] = item;
            }

            scrubbed[CheckInsField] = map;
        }

        return result with { ServerData = scrubbed };
    }

    // ── reading (also FUP-287's PickupReopen) ───────────────────────────────

    internal static string Str(IReadOnlyDictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;

    internal static bool Bool(IReadOnlyDictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) && value switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var parsed) && parsed,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            _ => false
        };

    internal static bool TryNumber(IReadOnlyDictionary<string, object> item, string key, out double number)
    {
        number = double.NaN;
        if (!item.TryGetValue(key, out var value) || value == null)
        {
            return false;
        }

        number = value switch
        {
            double d => d,
            decimal m => (double)m,
            int i => i,
            long l => l,
            JsonElement { ValueKind: JsonValueKind.Number } je => je.GetDouble(),
            _ => double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : double.NaN
        };
        return !double.IsNaN(number) && !double.IsInfinity(number);
    }

    private static bool IsNull(object? value)
        => value is null || value is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };

    private static string? ReadString(Dictionary<string, object> data, string key)
        => data.TryGetValue(key, out var value) ? value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            _ => null
        } : null;

    private static double? Bounded(object raw, string key, double min, double max, List<string> errors)
    {
        var number = raw switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } je => je.GetDouble(),
            double d => d,
            float f => f,
            decimal m => (double)m,
            int i => i,
            long l => l,
            _ => double.NaN
        };
        if (double.IsNaN(number) || double.IsInfinity(number))
        {
            errors.Add($"{key} must be a number.");
            return null;
        }

        if (number < min || number > max)
        {
            errors.Add($"{key} must be between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}.");
            return null;
        }

        return number;
    }
}

/// <summary>ESC-34: a parsed <c>handoff_check_in</c> (times UTC).</summary>
public sealed record ParsedCheckIn(
    string OrderId,
    string Kind,
    DateTime? CapturedAt,
    double? Latitude,
    double? Longitude,
    double? AccuracyM);
