using System.Globalization;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// Persona simulation 2026-11-19, BUG-6 (escrow-protocol.md 2.28.0): what the server already holds about
/// the handoffs of a disputed delivery, stamped on the escrow when the dispute is created, so an admin
/// reads it with the dispute instead of working it out from the timeline. Evidence only: it refuses
/// nothing and scores nothing. Server-only: the map is returned on admin routes alone
/// (<see cref="DisputeDto.HandoffEvidence"/>); the party reads never map it.
/// </summary>
public static class DisputeHandoffEvidence
{
    /// <summary>The escrow attribute (a map) the evidence is stored under.</summary>
    public const string Field = "disputeHandoffEvidence";

    private const string SelfPickupType = "self_pickup";

    /// <summary>
    /// The evidence of <paramref name="delivery"/> as stored when <paramref name="disputedBy"/> opens the
    /// dispute. How each leg was confirmed comes from the method stamped on the delivery (GCR-01,
    /// <see cref="HandoffEvidence"/>), else, for a delivery confirmed before that stamp, from the latest
    /// transaction-audit row of the transition (<c>picked_up</c>, <c>delivered</c>); who confirmed comes from
    /// that row. Each is left out when neither source has it.
    /// <c>confirmedByDisputer</c> is written only when the audit row names who confirmed. Null without a
    /// delivery.
    /// </summary>
    public static Dictionary<string, object>? Build(
        Dictionary<string, object>? delivery,
        IReadOnlyList<Dictionary<string, object>> deliveryAuditRows,
        string disputedBy)
    {
        var deliveryId = delivery == null ? null : Text(delivery, "deliveryId");
        if (delivery == null || string.IsNullOrEmpty(deliveryId))
        {
            return null;
        }

        var evidence = new Dictionary<string, object> { ["deliveryId"] = deliveryId };
        Put(evidence, "deliveryStatus", Text(delivery, "status"));
        var selfPickup = Text(delivery, "deliveryType") == SelfPickupType;
        evidence["selfPickup"] = selfPickup;

        var pickedUpAt = Text(delivery, "pickedUpAt");
        if (!selfPickup && !string.IsNullOrEmpty(pickedUpAt))
        {
            var pickupRow = Latest(deliveryAuditRows, deliveryId, "picked_up");
            evidence["pickedUpAt"] = pickedUpAt;
            evidence["pickupFix"] = HasFix(delivery, "gpsPickup");
            Put(evidence, "pickupMethod", HandoffEvidence.MethodOf(delivery, HandoffEvidence.PickupLeg)
                ?? MethodOf(pickupRow == null ? null : Text(pickupRow, "metadata")));
            Put(evidence, "pickupBy", pickupRow == null ? null : Text(pickupRow, "actor"));
        }

        var confirmedAt = Text(delivery, "confirmedAt");
        if (!string.IsNullOrEmpty(confirmedAt))
        {
            var deliveredRow = Latest(deliveryAuditRows, deliveryId, "delivered");
            var confirmedBy = deliveredRow == null ? null : Text(deliveredRow, "actor");
            evidence["confirmedAt"] = confirmedAt;
            evidence["dropoffFix"] = HasFix(delivery, "gpsDropoff");
            Put(evidence, "confirmMethod", HandoffEvidence.MethodOf(delivery, HandoffEvidence.DropoffLeg)
                ?? MethodOf(deliveredRow == null ? null : Text(deliveredRow, "metadata")));
            Put(evidence, "confirmedBy", confirmedBy);
            if (!string.IsNullOrEmpty(confirmedBy))
            {
                evidence["confirmedByDisputer"] = string.Equals(confirmedBy, disputedBy, StringComparison.Ordinal);
            }
        }

        return evidence;
    }

    /// <summary>The stored evidence of an escrow row for an admin response; null when none was stamped.</summary>
    public static DisputeHandoffEvidenceDto? Read(Dictionary<string, object> escrow)
    {
        if (!escrow.TryGetValue(Field, out var raw) || raw is not Dictionary<string, object> map)
        {
            return null;
        }

        return new DisputeHandoffEvidenceDto
        {
            DeliveryId = Text(map, "deliveryId") ?? "",
            DeliveryStatus = Text(map, "deliveryStatus"),
            SelfPickup = Flag(map, "selfPickup") ?? false,
            PickedUpAt = Text(map, "pickedUpAt"),
            PickupMethod = Text(map, "pickupMethod"),
            PickupBy = Text(map, "pickupBy"),
            PickupFix = Flag(map, "pickupFix"),
            ConfirmedAt = Text(map, "confirmedAt"),
            ConfirmMethod = Text(map, "confirmMethod"),
            ConfirmedBy = Text(map, "confirmedBy"),
            DropoffFix = Flag(map, "dropoffFix"),
            ConfirmedByDisputer = Flag(map, "confirmedByDisputer")
        };
    }

    /// <summary><c>qr</c> or <c>code</c> from a transition's audit metadata (<c>handoff=qr</c>); null otherwise.</summary>
    public static string? MethodOf(string? metadata)
    {
        if (string.IsNullOrEmpty(metadata))
        {
            return null;
        }

        foreach (var part in metadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("handoff=", StringComparison.Ordinal))
            {
                var method = part["handoff=".Length..];
                return method is "qr" or "code" ? method : null;
            }
        }

        return null;
    }

    /// <summary>Whether the delivery stores a point under <paramref name="key"/> that is a fix, not (0, 0).</summary>
    public static bool HasFix(Dictionary<string, object> delivery, string key)
    {
        if (!delivery.TryGetValue(key, out var raw) || raw is not Dictionary<string, object> point
            || !TryNumber(point, "latitude", out var latitude) || !TryNumber(point, "longitude", out var longitude))
        {
            return false;
        }

        return latitude != 0 || longitude != 0;
    }

    /// <summary>The latest audit row of one delivery transition.</summary>
    private static Dictionary<string, object>? Latest(
        IReadOnlyList<Dictionary<string, object>> rows, string deliveryId, string action)
        => rows
            .Where(r => Text(r, "entityType") == "delivery" && Text(r, "entityId") == deliveryId && Text(r, "action") == action)
            .OrderBy(r => Text(r, "timestamp") ?? "", StringComparer.Ordinal)
            .LastOrDefault();

    private static void Put(Dictionary<string, object> evidence, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            evidence[key] = value;
        }
    }

    private static string? Text(Dictionary<string, object> row, string key)
        => row.TryGetValue(key, out var value) && value is not null and not Dictionary<string, object> ? value.ToString() : null;

    private static bool? Flag(Dictionary<string, object> row, string key)
        => row.TryGetValue(key, out var value)
            ? value switch
            {
                bool b => b,
                string s when bool.TryParse(s, out var parsed) => parsed,
                _ => null
            }
            : null;

    private static bool TryNumber(Dictionary<string, object> row, string key, out double number)
    {
        number = 0;
        return row.TryGetValue(key, out var value) && value != null
            && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }
}
